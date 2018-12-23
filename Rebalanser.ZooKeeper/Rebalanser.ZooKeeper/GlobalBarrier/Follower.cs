using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using org.apache.zookeeper;
using Rebalanser.Core;
using Rebalanser.Core.Logging;
using Rebalanser.ZooKeeper.Store;
using Rebalanser.ZooKeeper.Zk;

namespace Rebalanser.ZooKeeper.GlobalBarrier
{
    public class Follower : Watcher, IFollower
    {
        private IZooKeeperService zooKeeperService;
        private ILogger logger;
        private ResourceGroupStore store;
        private string clientId;
        private int clientNumber;
        private OnChangeActions onChangeActions;
        private CancellationToken followerToken;
        private FollowerStatus eventExitReason;
        private bool statusChange;
        private string watchSiblingPath;
        private string siblingId;
        private int statusVersion;

        private enum SiblingCheckResult
        {
            WatchingNewSibling,
            IsNewLeader,
            Error
        }
        
        public Follower(IZooKeeperService zooKeeperService,
            ILogger logger,
            ResourceGroupStore store,
            OnChangeActions onChangeActions,
            string clientId,
            int clientNumber,
            string watchSiblingPath,
            CancellationToken followerToken)
        {
            this.zooKeeperService = zooKeeperService;
            this.logger = logger;
            this.store = store;
            this.onChangeActions = onChangeActions;
            this.clientId = clientId;
            this.clientNumber = clientNumber;
            this.watchSiblingPath = watchSiblingPath;
            this.siblingId = watchSiblingPath.Substring(watchSiblingPath.LastIndexOf("/", StringComparison.Ordinal));
            this.followerToken = followerToken;
        }

        public async Task<bool> BecomeFollowerAsync()
        {
            var watchSiblingRes = await this.zooKeeperService.WatchSiblingNodeAsync(this.watchSiblingPath, this);
            if (watchSiblingRes != ZkResult.Ok)
            {
                if(watchSiblingRes == ZkResult.NoZnode)
                    this.logger.Info(this.clientId, $"Follower - Could not set a watch on sibling node {this.watchSiblingPath} as it no longer exists");
                return false;
            }

            var watchStatusRes = await this.zooKeeperService.WatchStatusAsync(this);
            if (watchStatusRes.Result != ZkResult.Ok)
                return false;
            
            return true;
        }
        
        public override async Task process(WatchedEvent @event)
        {
            if (@event.getState() == Event.KeeperState.Expired)
            {
                this.eventExitReason = FollowerStatus.SessionExpired;
            }
            // if the sibling client has been removed then this client must either be the new leader
            // or the node needs to monitor the next smallest client
            else if (@event.getPath().EndsWith(this.siblingId))
            {
                var siblingResult = await CheckForSiblings();
                switch (siblingResult)
                {
                    case SiblingCheckResult.WatchingNewSibling:
                        break;
                    case SiblingCheckResult.IsNewLeader:
                        eventExitReason = FollowerStatus.IsNewLeader;
                        break;
                    case SiblingCheckResult.Error:
                        eventExitReason = FollowerStatus.UnexpectedFailure;
                        break;
                    default:
                        this.logger.Error(this.clientId, $"Follower - Non-supported SiblingCheckResult {siblingResult}");
                        break;
                }
            }
            // status change
            else if (@event.getPath().EndsWith("status"))
            {
                statusChange = true;
            }
            else
            {
                // log it 
            }

            await Task.Yield();
        }
        
        public async Task<FollowerStatus> StartEventLoopAsync()
        {
            int lastStopVersion = 0;
            int lastStartVersion = 0;
            
            while (!this.followerToken.IsCancellationRequested)
            {
                if (this.eventExitReason != FollowerStatus.Ok)
                {
                    InvokeOnStopActions();
                    return this.eventExitReason;
                }
                
                if (this.statusChange)
                {
                    var result = await ProcessStatusChangeAsync(lastStopVersion, lastStartVersion);
                    if (result.ExitReason == FollowerStatus.Ok)
                    {
                        lastStartVersion = result.LastStartVersion;
                        lastStopVersion = result.LastStopVersion;
                    }
                    else
                    {
                        return result.ExitReason;
                    }
                }

                await WaitFor(1000);
            }

            if (this.followerToken.IsCancellationRequested)
            {
                await this.zooKeeperService.CloseSessionAsync();
                return FollowerStatus.Cancelled;
            }

            return FollowerStatus.UnexpectedFailure;
        }

        private async Task<StateChangeResult> ProcessStatusChangeAsync(int lastStopVersion, int lastStartVersion)
        {
            this.statusChange = false;
            var watchStatusRes = await this.zooKeeperService.WatchStatusAsync(this);
            if (watchStatusRes.Result != ZkResult.Ok)
            {
                if (watchStatusRes.Result == ZkResult.SessionExpired)
                    return new StateChangeResult(FollowerStatus.SessionExpired);

                return new StateChangeResult(FollowerStatus.UnexpectedFailure);
            }

            var result = new StateChangeResult(FollowerStatus.Ok);
            var status = watchStatusRes.Data;

            // check for cancellation
            if (this.followerToken.IsCancellationRequested)
            {
                result.ExitReason = FollowerStatus.Cancelled;
                return result;
            }
            
            if (status.RebalancingStatus == RebalancingStatus.StopActivity)
            {
                result.LastStopVersion = status.Version;
                result.LastStartVersion = lastStartVersion;
                
                InvokeOnStopActions();
                
                // check for cancellation, stop actions can be of arbitrary time
                if (this.followerToken.IsCancellationRequested)
                {
                    result.ExitReason = FollowerStatus.Cancelled;
                    return result;
                }
                
                var stoppedRes = await this.zooKeeperService.SetFollowerAsStopped(this.clientId);
                if (stoppedRes != ZkResult.Ok)
                {
                    if (stoppedRes == ZkResult.NodeAlreadyExists && lastStopVersion > lastStartVersion)
                        this.logger.Info(this.clientId, $"Follower - Two consecutive stop commands received. Last Stop Status Version: {lastStopVersion}, Last ResourceGranted Status Version: {lastStartVersion}");
                    else if (stoppedRes == ZkResult.SessionExpired)
                        result.ExitReason = FollowerStatus.SessionExpired;
                    else
                        result.ExitReason = FollowerStatus.UnexpectedFailure;
                }
            }
            else if (status.RebalancingStatus == RebalancingStatus.ResourcesGranted)
            {
                if (lastStopVersion > 0)
                {
                    var resourcesRes = await this.zooKeeperService.GetResourcesAsync();
                    if (resourcesRes.Result != ZkResult.Ok)
                    {
                        if (resourcesRes.Result == ZkResult.SessionExpired)
                            result.ExitReason = FollowerStatus.SessionExpired;
                        else
                            result.ExitReason = FollowerStatus.UnexpectedFailure;
                    }
                    else
                    {
                        var resources = resourcesRes.Data;
                        var assignedResources = resources.ResourceAssignments.Assignments
                            .Where(x => x.ClientId.Equals(this.clientId))
                            .Select(x => x.Resource)
                            .ToList();

                        this.store.SetResources(new SetResourcesRequest()
                        {
                            AssignmentStatus = AssignmentStatus.ResourcesAssigned,
                            Resources = assignedResources
                        });

                        InvokeOnStartActions(assignedResources);
                        
                        // check for cancellation, start actions can be of arbitrary time
                        if (this.followerToken.IsCancellationRequested)
                        {
                            result.ExitReason = FollowerStatus.Cancelled;
                            return result;
                        }
                        
                        var startedRes = await this.zooKeeperService.SetFollowerAsStarted(this.clientId);
                        if (startedRes != ZkResult.Ok && startedRes != ZkResult.NoZnode)
                        {
                            if (startedRes == ZkResult.SessionExpired)
                                result.ExitReason = FollowerStatus.SessionExpired;
                            else
                                result.ExitReason = FollowerStatus.UnexpectedFailure;
                        }
                    }
                }
                else
                {
                    this.logger.Info(this.clientId, "Follower - Ignoring ResourcesGranted status as did not receive a StopActivity notification. Likely I am a new follower.");
                }

                result.LastStartVersion = status.Version;
                result.LastStopVersion = lastStopVersion;    
            }
            else if (status.RebalancingStatus == RebalancingStatus.StartConfirmed)
            {
                // do nothing
            }
            else
            {
                // log unexpected status
            }
            
            return result;
        }

        private void InvokeOnStopActions()
        {
            foreach(var onStopAction in this.onChangeActions.OnStopActions)
                onStopAction.Invoke();
        }
        
        private void InvokeOnStartActions(List<string> assignedResources)
        {
            foreach(var onStartAction in this.onChangeActions.OnStartActions)
                onStartAction.Invoke(assignedResources);
        }
        
        private async Task WaitFor(int milliseconds)
        {
            try
            {
                await Task.Delay(milliseconds, this.followerToken);
            }
            catch (TaskCanceledException)
            {}
        }

        private async Task<SiblingCheckResult> CheckForSiblings()
        {
            int maxClientNumber = -1;
            string watchChild = string.Empty;
            var clientsRes = await this.zooKeeperService.GetActiveClientsAsync();
            if (clientsRes.Result != ZkResult.Ok)
                return SiblingCheckResult.Error;
                
            var clients = clientsRes.Data;
            foreach (var childPath in clients.ClientPaths)
            {
                int siblingClientNumber = int.Parse(childPath.Substring(childPath.Length - 10, 10));
                if (siblingClientNumber > maxClientNumber && siblingClientNumber < this.clientNumber)
                {
                    watchChild = childPath;
                    maxClientNumber = siblingClientNumber;
                }
            }

            if (maxClientNumber == -1)
                return SiblingCheckResult.IsNewLeader;
            
            this.watchSiblingPath = watchChild;
            this.siblingId = watchSiblingPath.Substring(watchChild.LastIndexOf("/", StringComparison.Ordinal));
            var newWatchRes = await this.zooKeeperService.WatchSiblingNodeAsync(watchChild, this);
            if (newWatchRes != ZkResult.Ok)
                return SiblingCheckResult.Error;
            
            return SiblingCheckResult.WatchingNewSibling;
        }
    }
}