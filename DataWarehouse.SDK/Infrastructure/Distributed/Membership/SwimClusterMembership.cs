using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// SWIM gossip-based cluster membership with failure detection via
    /// random probing, indirect ping-req, suspicion timeout, and incarnation number refutation.
    /// Implements the SWIM protocol (Das et al., 2002) adapted for the SDK IClusterMembership contract.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: SWIM gossip membership")]
    public sealed class SwimClusterMembership : IClusterMembership, IDisposable
    {
        private readonly BoundedDictionary<string, SwimMemberState> _members = new BoundedDictionary<string, SwimMemberState>(1000);
        private readonly ClusterNode _self;
        private readonly IP2PNetwork _network;
        private readonly IGossipProtocol _gossip;
        private readonly SwimConfiguration _config;
        private readonly CancellationTokenSource _probeCts = new();
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private readonly ConcurrentQueue<SwimMembershipUpdate> _recentUpdates = new();
        // LOW-454: Track queue length via Interlocked counter to avoid O(n) ConcurrentQueue.Count in EnqueueUpdate.
        private int _recentUpdatesCount;
        private readonly BoundedDictionary<string, int> _deadReportCounts = new BoundedDictionary<string, int>(1000);
        private readonly BoundedDictionary<string, DateTimeOffset> _lastStateChangePerNode = new BoundedDictionary<string, DateTimeOffset>(1000);
        private string? _leaderId;
        private Task? _probeLoopTask;
        private Task? _suspicionCheckTask;

        /// <summary>
        /// Cached member list for zero-allocation GetMembers() calls.
        /// Invalidated atomically when membership changes (join/leave/suspect/dead events).
        /// At ~20 calls/sec, this eliminates 20 LINQ allocations per second.
        /// </summary>
        private volatile IReadOnlyList<ClusterNode>? _cachedMembers;

        /// <summary>
        /// Maximum number of recent membership updates to track for piggyback dissemination.
        /// </summary>
        private const int MaxRecentUpdates = 100;

        /// <summary>
        /// Creates a new SWIM cluster membership instance.
        /// </summary>
        /// <param name="nodeId">Unique identifier for this node.</param>
        /// <param name="address">Network address for this node.</param>
        /// <param name="port">Port this node listens on.</param>
        /// <param name="network">P2P network for inter-node communication.</param>
        /// <param name="gossip">Gossip protocol for membership change dissemination.</param>
        /// <param name="config">Optional SWIM configuration.</param>
        public SwimClusterMembership(
            string nodeId,
            string address,
            int port,
            IP2PNetwork network,
            IGossipProtocol gossip,
            SwimConfiguration? config = null)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _gossip = gossip ?? throw new ArgumentNullException(nameof(gossip));
            _config = config ?? new SwimConfiguration();

            _self = new ClusterNode
            {
                NodeId = nodeId,
                Address = address,
                Port = port,
                Role = ClusterNodeRole.Follower,
                Status = ClusterNodeStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow
            };

            // Add self to members
            _members[nodeId] = new SwimMemberState
            {
                Node = _self,
                Status = ClusterNodeStatus.Active,
                IncarnationNumber = 0,
                LastPingAt = DateTimeOffset.UtcNow
            };

            // Subscribe to network events for incoming SWIM messages
            _network.OnPeerEvent += HandlePeerEvent;
            _gossip.OnGossipReceived += HandleGossipReceived;
        }

        /// <inheritdoc />
        public event Action<ClusterMembershipEvent>? OnMembershipChanged;

        /// <inheritdoc />
        /// <remarks>
        /// Returns a cached member list (zero allocation). The cache is invalidated atomically
        /// when membership changes (join/leave/suspect/dead events). This is critical because
        /// GetMembers() is called ~20x/sec and the previous implementation allocated a new
        /// List on every call.
        /// </remarks>
        public IReadOnlyList<ClusterNode> GetMembers()
        {
            var cached = _cachedMembers;
            if (cached != null)
                return cached;

            // Rebuild cache on miss
            var members = _members.Values
                .Where(m => m.Status != ClusterNodeStatus.Dead)
                .Select(m => m.Node)
                .ToList()
                .AsReadOnly();

            _cachedMembers = members;
            return members;
        }

        /// <summary>
        /// Invalidates the cached member list, forcing a rebuild on next GetMembers() call.
        /// Called whenever membership state changes.
        /// </summary>
        private void InvalidateMemberCache()
        {
            _cachedMembers = null;
        }

        /// <inheritdoc />
        public ClusterNode? GetLeader()
        {
            if (_leaderId != null && _members.TryGetValue(_leaderId, out var leader)
                && leader.Status == ClusterNodeStatus.Active)
            {
                return leader.Node;
            }
            return null;
        }

        /// <inheritdoc />
        public ClusterNode GetSelf() => _self;

        /// <inheritdoc />
        public async Task JoinAsync(ClusterJoinRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Broadcast Join message to all known peers
            var joinMessage = new SwimMessage
            {
                Type = SwimMessageType.Join,
                SourceNodeId = _self.NodeId,
                TargetNodeId = string.Empty,
                IncarnationNumber = 0,
                MembershipUpdates = new List<SwimMembershipUpdate>
                {
                    new SwimMembershipUpdate
                    {
                        NodeId = _self.NodeId,
                        Address = _self.Address,
                        Port = _self.Port,
                        Status = ClusterNodeStatus.Active,
                        IncarnationNumber = 0
                    }
                }
            };

            await _network.BroadcastAsync(joinMessage.Serialize(_config.ClusterSecret), ct).ConfigureAwait(false);

            // Start the probe loop for failure detection
            StartProbeLoop();

            FireMembershipEvent(ClusterMembershipEventType.NodeJoined, _self, "Self joined cluster");
        }

        /// <inheritdoc />
        public async Task LeaveAsync(string reason, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_members.TryGetValue(_self.NodeId, out var selfState))
                {
                    selfState.Status = ClusterNodeStatus.Leaving;
                    selfState.Node = _self with { Status = ClusterNodeStatus.Leaving };
                }
            }
            finally
            {
                _stateLock.Release();
            }

            // Broadcast Leave message
            var leaveMessage = new SwimMessage
            {
                Type = SwimMessageType.Leave,
                SourceNodeId = _self.NodeId,
                TargetNodeId = string.Empty,
                IncarnationNumber = GetSelfIncarnation()
            };

            await _network.BroadcastAsync(leaveMessage.Serialize(_config.ClusterSecret), ct).ConfigureAwait(false);

            // Stop probe loop
            StopProbeLoop();

            FireMembershipEvent(ClusterMembershipEventType.NodeLeft, _self, reason);
        }

        /// <inheritdoc />
        public Task<bool> IsHealthyAsync(string nodeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_members.TryGetValue(nodeId, out var member))
            {
                return Task.FromResult(member.Status == ClusterNodeStatus.Active);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Sets the cluster leader. Called by Raft consensus engine (Plan 29-02) when leadership changes.
        /// </summary>
        /// <param name="nodeId">The node ID of the new leader.</param>
        internal void SetLeader(string nodeId)
        {
            _leaderId = nodeId;

            if (_members.TryGetValue(nodeId, out var leaderState))
            {
                var updatedNode = leaderState.Node with { Role = ClusterNodeRole.Leader };
                leaderState.Node = updatedNode;

                FireMembershipEvent(ClusterMembershipEventType.LeaderChanged, updatedNode, "Raft leader elected");
            }
        }

        private void StartProbeLoop()
        {
            var ct = _probeCts.Token;
            _probeLoopTask = Task.Run(() => RunProbeLoopAsync(ct), ct);
            _suspicionCheckTask = Task.Run(() => RunSuspicionCheckAsync(ct), ct);
        }

        private void StopProbeLoop()
        {
            _probeCts.Cancel();
        }

        private async Task RunProbeLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.ProtocolPeriodMs));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var target = SelectRandomMember(exclude: _self.NodeId);
                    if (target == null) continue;

                    bool acked = await TryDirectPingAsync(target, ct).ConfigureAwait(false);
                    if (!acked)
                    {
                        // Indirect probing via k random members
                        var probers = SelectRandomMembers(_config.IndirectPingCount, exclude: new[] { _self.NodeId, target.Node.NodeId });
                        acked = await TryIndirectPingAsync(target, probers, ct).ConfigureAwait(false);
                    }

                    if (!acked)
                    {
                        await MarkSuspectedAsync(target.Node.NodeId, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SwimClusterMembership.RunProbeLoopAsync] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task RunSuspicionCheckAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.ProtocolPeriodMs));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var suspectedMembers = _members.Values
                        .Where(m => m.Status == ClusterNodeStatus.Suspected)
                        .ToList();

                    foreach (var member in suspectedMembers)
                    {
                        if ((now - member.SuspectedAt).TotalMilliseconds >= _config.SuspicionTimeoutMs)
                        {
                            await MarkDeadAsync(member.Node.NodeId, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SwimClusterMembership.RunSuspicionCheckAsync] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task<bool> TryDirectPingAsync(SwimMemberState target, CancellationToken ct)
        {
            var pingMessage = new SwimMessage
            {
                Type = SwimMessageType.Ping,
                SourceNodeId = _self.NodeId,
                TargetNodeId = target.Node.NodeId,
                IncarnationNumber = GetSelfIncarnation(),
                MembershipUpdates = GetRecentUpdates()
            };

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.PingTimeoutMs);

                var response = await _network.RequestFromPeerAsync(
                    target.Node.NodeId,
                    pingMessage.Serialize(_config.ClusterSecret),
                    timeoutCts.Token).ConfigureAwait(false);

                var ackMessage = SwimMessage.Deserialize(response, _config.ClusterSecret);
                if (ackMessage?.Type == SwimMessageType.Ack)
                {
                    target.LastPingAt = DateTimeOffset.UtcNow;
                    ProcessMembershipUpdates(ackMessage.MembershipUpdates);
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Ping timeout -- not the overall cancellation
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwimClusterMembership.TryDirectPingAsync] {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }

        private async Task<bool> TryIndirectPingAsync(SwimMemberState target, List<SwimMemberState> probers, CancellationToken ct)
        {
            if (probers.Count == 0) return false;

            var pingReqMessage = new SwimMessage
            {
                Type = SwimMessageType.PingReq,
                SourceNodeId = _self.NodeId,
                TargetNodeId = target.Node.NodeId,
                IncarnationNumber = GetSelfIncarnation(),
                MembershipUpdates = GetRecentUpdates()
            };

            var tasks = probers.Select(prober => TrySendPingReqAsync(prober, pingReqMessage, ct)).ToList();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.PingTimeoutMs * 2);

                // Wait for any successful indirect probe
                while (tasks.Count > 0)
                {
                    var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(completed);

                    if (await completed.ConfigureAwait(false))
                    {
                        target.LastPingAt = DateTimeOffset.UtcNow;
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Indirect ping timeout
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwimClusterMembership.TryIndirectPingAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return false;
        }

        private async Task<bool> TrySendPingReqAsync(SwimMemberState prober, SwimMessage pingReqMessage, CancellationToken ct)
        {
            try
            {
                var response = await _network.RequestFromPeerAsync(
                    prober.Node.NodeId,
                    pingReqMessage.Serialize(_config.ClusterSecret),
                    ct).ConfigureAwait(false);

                var ackMessage = SwimMessage.Deserialize(response, _config.ClusterSecret);
                return ackMessage?.Type == SwimMessageType.Ack;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwimClusterMembership.TrySendPingReqAsync] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async Task MarkSuspectedAsync(string nodeId, CancellationToken ct)
        {
            // If self is suspected, refute by incrementing incarnation
            if (string.Equals(nodeId, _self.NodeId, StringComparison.Ordinal))
            {
                await RefuteSuspicionAsync(ct).ConfigureAwait(false);
                return;
            }

            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_members.TryGetValue(nodeId, out var member) && member.Status == ClusterNodeStatus.Active)
                {
                    member.Status = ClusterNodeStatus.Suspected;
                    member.SuspectedAt = DateTimeOffset.UtcNow;
                    member.Node = member.Node with { Status = ClusterNodeStatus.Suspected };

                    EnqueueUpdate(new SwimMembershipUpdate
                    {
                        NodeId = nodeId,
                        Address = member.Node.Address,
                        Port = member.Node.Port,
                        Status = ClusterNodeStatus.Suspected,
                        IncarnationNumber = member.IncarnationNumber
                    });

                    FireMembershipEvent(ClusterMembershipEventType.NodeSuspected, member.Node, "Probe failed");
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private async Task MarkDeadAsync(string nodeId, CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_members.TryGetValue(nodeId, out var member)
                    && (member.Status == ClusterNodeStatus.Suspected || member.Status == ClusterNodeStatus.Active))
                {
                    member.Status = ClusterNodeStatus.Dead;
                    member.Node = member.Node with { Status = ClusterNodeStatus.Dead };

                    EnqueueUpdate(new SwimMembershipUpdate
                    {
                        NodeId = nodeId,
                        Address = member.Node.Address,
                        Port = member.Node.Port,
                        Status = ClusterNodeStatus.Dead,
                        IncarnationNumber = member.IncarnationNumber
                    });

                    FireMembershipEvent(ClusterMembershipEventType.NodeDead, member.Node, "Suspicion timeout");
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private async Task RefuteSuspicionAsync(CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_members.TryGetValue(_self.NodeId, out var selfState))
                {
                    selfState.IncarnationNumber++;
                    selfState.Status = ClusterNodeStatus.Active;
                    selfState.Node = _self with { Status = ClusterNodeStatus.Active };
                }
            }
            finally
            {
                _stateLock.Release();
            }

            // Broadcast Alive message with incremented incarnation
            var aliveMessage = new SwimMessage
            {
                Type = SwimMessageType.Alive,
                SourceNodeId = _self.NodeId,
                TargetNodeId = _self.NodeId,
                IncarnationNumber = GetSelfIncarnation()
            };

            await _network.BroadcastAsync(aliveMessage.Serialize(_config.ClusterSecret), ct).ConfigureAwait(false);
        }

        private void HandleAlive(string nodeId, int incarnation)
        {
            if (_members.TryGetValue(nodeId, out var member) && incarnation > member.IncarnationNumber)
            {
                member.IncarnationNumber = incarnation;
                member.Status = ClusterNodeStatus.Active;
                member.Node = member.Node with { Status = ClusterNodeStatus.Active };
                member.LastPingAt = DateTimeOffset.UtcNow;

                EnqueueUpdate(new SwimMembershipUpdate
                {
                    NodeId = nodeId,
                    Address = member.Node.Address,
                    Port = member.Node.Port,
                    Status = ClusterNodeStatus.Active,
                    IncarnationNumber = incarnation
                });
            }
        }

        private void HandlePeerEvent(PeerEvent peerEvent)
        {
            if (peerEvent.EventType == PeerEventType.PeerUpdated)
            {
                // Incoming data from peer -- try to deserialize as SWIM message
                // The IP2PNetwork fires PeerUpdated for data arrival
                // Actual message handling is done via RequestFromPeerAsync response path
            }
        }

        private void HandleGossipReceived(GossipMessage gossipMessage)
        {
            try
            {
                // DIST-03: Verify HMAC before processing any SWIM message
                var swimMessage = SwimMessage.Deserialize(gossipMessage.Payload, _config.ClusterSecret);
                if (swimMessage != null)
                {
                    ProcessSwimMessage(swimMessage);
                }
                // null return means HMAC verification failed or invalid message -- silently reject
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwimClusterMembership.HandleGossipReceived] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ProcessSwimMessage(SwimMessage message)
        {
            switch (message.Type)
            {
                case SwimMessageType.Join:
                    HandleJoinMessage(message);
                    break;
                case SwimMessageType.Leave:
                    HandleLeaveMessage(message);
                    break;
                case SwimMessageType.Suspect:
                    HandleSuspectMessage(message);
                    break;
                case SwimMessageType.Alive:
                    HandleAlive(message.SourceNodeId, message.IncarnationNumber);
                    break;
                case SwimMessageType.Dead:
                    HandleDeadMessage(message);
                    break;
            }

            ProcessMembershipUpdates(message.MembershipUpdates);
        }

        private void HandleJoinMessage(SwimMessage message)
        {
            foreach (var update in message.MembershipUpdates)
            {
                if (!_members.ContainsKey(update.NodeId))
                {
                    var newNode = new ClusterNode
                    {
                        NodeId = update.NodeId,
                        Address = update.Address,
                        Port = update.Port,
                        Role = ClusterNodeRole.Follower,
                        Status = ClusterNodeStatus.Active,
                        JoinedAt = DateTimeOffset.UtcNow
                    };

                    var newState = new SwimMemberState
                    {
                        Node = newNode,
                        Status = ClusterNodeStatus.Active,
                        IncarnationNumber = update.IncarnationNumber,
                        LastPingAt = DateTimeOffset.UtcNow
                    };

                    if (_members.TryAdd(update.NodeId, newState))
                    {
                        InvalidateMemberCache();
                        FireMembershipEvent(ClusterMembershipEventType.NodeJoined, newNode, "SWIM join");
                    }
                }
            }
        }

        private void HandleLeaveMessage(SwimMessage message)
        {
            if (_members.TryGetValue(message.SourceNodeId, out var member))
            {
                member.Status = ClusterNodeStatus.Leaving;
                member.Node = member.Node with { Status = ClusterNodeStatus.Leaving };
                InvalidateMemberCache();
                FireMembershipEvent(ClusterMembershipEventType.NodeLeft, member.Node, "Graceful leave");
            }
        }

        private void HandleSuspectMessage(SwimMessage message)
        {
            string targetNodeId = message.TargetNodeId;

            // If we are being suspected, refute
            if (string.Equals(targetNodeId, _self.NodeId, StringComparison.Ordinal))
            {
                // LOW-451: use _probeCts.Token so refutation is cancelled on shutdown.
                _ = RefuteSuspicionAsync(_probeCts.Token)
                    .ContinueWith(t => System.Diagnostics.Debug.WriteLine(
                        $"[SWIM] Suspicion refutation failed: {t.Exception?.InnerException?.Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
                return;
            }

            if (_members.TryGetValue(targetNodeId, out var member)
                && message.IncarnationNumber >= member.IncarnationNumber
                && member.Status == ClusterNodeStatus.Active)
            {
                member.Status = ClusterNodeStatus.Suspected;
                member.SuspectedAt = DateTimeOffset.UtcNow;
                member.Node = member.Node with { Status = ClusterNodeStatus.Suspected };
                FireMembershipEvent(ClusterMembershipEventType.NodeSuspected, member.Node, "Remote suspicion");
            }
        }

        private void HandleDeadMessage(SwimMessage message)
        {
            string targetNodeId = message.TargetNodeId;

            // DIST-03: Rate-limit state changes per node
            if (!IsStateChangeAllowed(targetNodeId))
            {
                return; // Throttled -- ignore rapid state changes
            }

            if (_members.TryGetValue(targetNodeId, out var member)
                && member.Status != ClusterNodeStatus.Dead)
            {
                // DIST-03: Require quorum of independent Dead reports before marking as dead.
                // A single Dead message should not immediately evict a node -- prevents poisoning attacks.
                int reportCount = _deadReportCounts.AddOrUpdate(targetNodeId, 1, (_, c) => c + 1);
                if (reportCount < _config.DeadNodeQuorum)
                {
                    // Not enough reports yet -- mark as suspected first if not already
                    if (member.Status == ClusterNodeStatus.Active)
                    {
                        member.Status = ClusterNodeStatus.Suspected;
                        member.SuspectedAt = DateTimeOffset.UtcNow;
                        member.Node = member.Node with { Status = ClusterNodeStatus.Suspected };
                        RecordStateChange(targetNodeId);
                        FireMembershipEvent(ClusterMembershipEventType.NodeSuspected, member.Node,
                            $"Dead report {reportCount}/{_config.DeadNodeQuorum} -- awaiting quorum");
                    }
                    return;
                }

                member.Status = ClusterNodeStatus.Dead;
                member.Node = member.Node with { Status = ClusterNodeStatus.Dead };
                _deadReportCounts.TryRemove(targetNodeId, out _);
                RecordStateChange(targetNodeId);
                FireMembershipEvent(ClusterMembershipEventType.NodeDead, member.Node,
                    $"Dead quorum reached ({reportCount} reports)");
            }
        }

        /// <summary>
        /// Checks whether a state change is allowed for the given node (rate limiting, DIST-03).
        /// </summary>
        private bool IsStateChangeAllowed(string nodeId)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastStateChangePerNode.TryGetValue(nodeId, out var lastChange))
            {
                var elapsed = (now - lastChange).TotalSeconds;
                if (elapsed < 1.0 / _config.MaxStateChangesPerNodePerSecond)
                {
                    return false; // Rate limited
                }
            }
            return true;
        }

        /// <summary>
        /// Records that a state change occurred for the given node (for rate limiting).
        /// </summary>
        private void RecordStateChange(string nodeId)
        {
            _lastStateChangePerNode[nodeId] = DateTimeOffset.UtcNow;
        }

        private void ProcessMembershipUpdates(List<SwimMembershipUpdate> updates)
        {
            var changed = false;

            foreach (var update in updates)
            {
                if (string.Equals(update.NodeId, _self.NodeId, StringComparison.Ordinal))
                    continue;

                if (_members.TryGetValue(update.NodeId, out var existing))
                {
                    if (update.IncarnationNumber > existing.IncarnationNumber)
                    {
                        existing.IncarnationNumber = update.IncarnationNumber;
                        existing.Status = update.Status;
                        existing.Node = existing.Node with { Status = update.Status };
                        changed = true;
                    }
                }
                else if (update.Status == ClusterNodeStatus.Active)
                {
                    var newNode = new ClusterNode
                    {
                        NodeId = update.NodeId,
                        Address = update.Address,
                        Port = update.Port,
                        Role = ClusterNodeRole.Follower,
                        Status = ClusterNodeStatus.Active,
                        JoinedAt = DateTimeOffset.UtcNow
                    };

                    if (_members.TryAdd(update.NodeId, new SwimMemberState
                    {
                        Node = newNode,
                        Status = ClusterNodeStatus.Active,
                        IncarnationNumber = update.IncarnationNumber,
                        LastPingAt = DateTimeOffset.UtcNow
                    }))
                    {
                        changed = true;
                    }
                }
            }

            // Invalidate cache if any membership state changed
            if (changed)
            {
                InvalidateMemberCache();
            }
        }

        private SwimMemberState? SelectRandomMember(string exclude)
        {
            var candidates = _members.Values
                .Where(m => !string.Equals(m.Node.NodeId, exclude, StringComparison.Ordinal)
                         && m.Status == ClusterNodeStatus.Active)
                .ToList();

            if (candidates.Count == 0) return null;

            int index = RandomNumberGenerator.GetInt32(0, candidates.Count);
            return candidates[index];
        }

        private List<SwimMemberState> SelectRandomMembers(int count, string[] exclude)
        {
            var candidates = _members.Values
                .Where(m => !exclude.Contains(m.Node.NodeId, StringComparer.Ordinal)
                         && m.Status == ClusterNodeStatus.Active)
                .ToList();

            if (candidates.Count == 0) return new List<SwimMemberState>();

            int selectCount = Math.Min(count, candidates.Count);
            var selected = new List<SwimMemberState>(selectCount);

            // Fisher-Yates shuffle for unbiased selection
            for (int i = candidates.Count - 1; i > 0 && selected.Count < selectCount; i--)
            {
                int j = RandomNumberGenerator.GetInt32(0, i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            for (int i = 0; i < selectCount; i++)
            {
                selected.Add(candidates[i]);
            }

            return selected;
        }

        private int GetSelfIncarnation()
        {
            if (_members.TryGetValue(_self.NodeId, out var selfState))
            {
                return selfState.IncarnationNumber;
            }
            return 0;
        }

        private List<SwimMembershipUpdate> GetRecentUpdates()
        {
            var updates = new List<SwimMembershipUpdate>();
            int count = 0;

            foreach (var update in _recentUpdates)
            {
                if (count >= _config.MaxGossipPiggybackSize) break;
                updates.Add(update);
                count++;
            }

            return updates;
        }

        private void EnqueueUpdate(SwimMembershipUpdate update)
        {
            _recentUpdates.Enqueue(update);
            // LOW-454: Increment counter atomically before trimming to avoid O(n) ConcurrentQueue.Count in loop.
            int newCount = Interlocked.Increment(ref _recentUpdatesCount);

            // Trim excess entries; each dequeue decrements the counter.
            while (newCount > MaxRecentUpdates)
            {
                if (_recentUpdates.TryDequeue(out _))
                    newCount = Interlocked.Decrement(ref _recentUpdatesCount);
                else
                    break;
            }
        }

        private void FireMembershipEvent(ClusterMembershipEventType eventType, ClusterNode node, string? reason)
        {
            // Invalidate cached member list on any membership change
            InvalidateMemberCache();

            OnMembershipChanged?.Invoke(new ClusterMembershipEvent
            {
                EventType = eventType,
                Node = node,
                Timestamp = DateTimeOffset.UtcNow,
                Reason = reason
            });
        }

        /// <summary>
        /// Disposes the SWIM membership, stopping the probe loop and releasing resources.
        /// </summary>
        public void Dispose()
        {
            _probeCts.Cancel();
            _probeCts.Dispose();
            _stateLock.Dispose();
            _network.OnPeerEvent -= HandlePeerEvent;
            _gossip.OnGossipReceived -= HandleGossipReceived;
        }
    }
}
