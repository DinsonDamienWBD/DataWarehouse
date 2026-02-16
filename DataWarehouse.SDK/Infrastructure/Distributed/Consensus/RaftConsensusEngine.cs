using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Raft consensus engine implementing leader election and log replication.
    /// Provides exactly-one-leader guarantee in multi-node clusters with automatic
    /// re-election on leader failure. Integrates with SWIM membership for peer discovery
    /// and reports elected leader back to the cluster membership.
    ///
    /// Implements IConsensusEngine for backward compatibility with existing plugin consumers.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Raft consensus leader election")]
    public sealed class RaftConsensusEngine : IConsensusEngine, IDisposable
    {
        private readonly IClusterMembership _membership;
        private readonly IP2PNetwork _network;
        private readonly RaftConfiguration _config;
        private readonly RaftPersistentState _persistent = new();
        private readonly RaftVolatileState _volatile = new();
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private readonly List<Action<Proposal>> _commitHandlers = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingProposals = new();

        private RaftRole _role = RaftRole.Follower;
        private string? _leaderId;
        private DateTimeOffset _lastHeartbeatReceived = DateTimeOffset.UtcNow;
        private Task? _electionTask;
        private Task? _heartbeatTask;
        private int _currentElectionTimeoutMs;

        /// <summary>
        /// Creates a new Raft consensus engine.
        /// </summary>
        /// <param name="membership">Cluster membership for peer discovery and leader reporting.</param>
        /// <param name="network">P2P network for RPC communication.</param>
        /// <param name="config">Optional Raft configuration.</param>
        public RaftConsensusEngine(
            IClusterMembership membership,
            IP2PNetwork network,
            RaftConfiguration? config = null)
        {
            _membership = membership ?? throw new ArgumentNullException(nameof(membership));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _config = config ?? new RaftConfiguration();

            RandomizeElectionTimeout();
        }

        #region IPlugin Implementation (minimal -- Raft is not a kernel-loaded plugin)

        /// <inheritdoc />
        public string Id => "raft-consensus-engine";

        /// <inheritdoc />
        public PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <inheritdoc />
        public string Name => "RaftConsensusEngine";

        /// <inheritdoc />
        public string Version => "2.0.0";

        /// <inheritdoc />
        public Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            return Task.FromResult(new HandshakeResponse
            {
                Success = true,
                ReadyState = PluginReadyState.Ready,
                PluginId = Id,
                Name = Name,
                Version = new Version(2, 0, 0),
                Category = PluginCategory.OrchestrationProvider
            });
        }

        /// <inheritdoc />
        public Task OnMessageAsync(PluginMessage message) => Task.CompletedTask;

        #endregion

        #region IConsensusEngine Implementation

        /// <inheritdoc />
        public bool IsLeader => _role == RaftRole.Leader;

        /// <inheritdoc />
        public async Task<bool> ProposeAsync(Proposal proposal)
        {
            if (_role != RaftRole.Leader)
            {
                throw new InvalidOperationException("Not the leader. Only the leader can propose state changes.");
            }

            await _stateLock.WaitAsync().ConfigureAwait(false);
            RaftLogEntry entry;
            try
            {
                entry = new RaftLogEntry
                {
                    Index = _persistent.Log.Count + 1,
                    Term = _persistent.CurrentTerm,
                    Command = proposal.Command,
                    Payload = proposal.Payload,
                    Timestamp = DateTimeOffset.UtcNow
                };

                _persistent.Log.Add(entry);
                CompactLogIfNeeded();
            }
            finally
            {
                _stateLock.Release();
            }

            // Track proposal for completion notification
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingProposals[proposal.Id] = tcs;

            // Replicate to peers
            await ReplicateToAllPeersAsync().ConfigureAwait(false);

            // Wait for commit (with timeout)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(-1, timeoutCts.Token)).ConfigureAwait(false);
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout
            }
            finally
            {
                _pendingProposals.TryRemove(proposal.Id, out _);
            }

            return false;
        }

        /// <inheritdoc />
        public void OnCommit(Action<Proposal> handler)
        {
            _commitHandlers.Add(handler);
        }

        #endregion

        /// <summary>
        /// Starts the Raft engine: subscribes to network events and begins the election timeout loop.
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            _network.OnPeerEvent += HandleNetworkEvent;
            _membership.OnMembershipChanged += HandleMembershipChanged;

            _lastHeartbeatReceived = DateTimeOffset.UtcNow;
            _electionTask = Task.Run(() => RunElectionTimeoutLoopAsync(_cts.Token), _cts.Token);

            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Stops the Raft engine, cleaning up subscriptions and background tasks.
        /// </summary>
        public void Stop()
        {
            _cts.Cancel();
            _network.OnPeerEvent -= HandleNetworkEvent;
            _membership.OnMembershipChanged -= HandleMembershipChanged;
        }

        private async Task RunElectionTimeoutLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(50, ct).ConfigureAwait(false); // Check interval

                    if (_role == RaftRole.Leader) continue;

                    var elapsed = (DateTimeOffset.UtcNow - _lastHeartbeatReceived).TotalMilliseconds;
                    if (elapsed >= _currentElectionTimeoutMs)
                    {
                        await StartElectionAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Election loop failure should not crash the engine
                }
            }
        }

        private async Task StartElectionAsync(CancellationToken ct)
        {
            string selfId = _membership.GetSelf().NodeId;
            long newTerm;
            long lastLogIndex;
            long lastLogTerm;

            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Transition to Candidate
                _role = RaftRole.Candidate;
                _persistent.CurrentTerm++;
                newTerm = _persistent.CurrentTerm;
                _persistent.VotedFor = selfId;
                _lastHeartbeatReceived = DateTimeOffset.UtcNow; // Reset timer
                RandomizeElectionTimeout();

                lastLogIndex = _persistent.Log.Count;
                lastLogTerm = _persistent.Log.Count > 0 ? _persistent.Log[^1].Term : 0;
            }
            finally
            {
                _stateLock.Release();
            }

            // Request votes from all peers
            var peers = _membership.GetMembers()
                .Where(m => !string.Equals(m.NodeId, selfId, StringComparison.Ordinal))
                .ToList();

            if (peers.Count == 0)
            {
                // Single node cluster -- become leader immediately
                await BecomeLeaderAsync(ct).ConfigureAwait(false);
                return;
            }

            var voteRequest = new RaftMessage
            {
                Type = RaftMessageType.RequestVote,
                Term = newTerm,
                SenderId = selfId,
                CandidateId = selfId,
                LastLogIndex = lastLogIndex,
                LastLogTerm = lastLogTerm
            };

            int votesReceived = 1; // Vote for self
            int totalMembers = peers.Count + 1;
            int majority = (totalMembers / 2) + 1;

            var voteTasks = peers.Select(peer => RequestVoteFromPeerAsync(peer, voteRequest, ct)).ToList();

            foreach (var task in voteTasks)
            {
                try
                {
                    var response = await task.ConfigureAwait(false);
                    if (response == null) continue;

                    // If response contains higher term, step down
                    if (response.Term > newTerm)
                    {
                        await StepDownToFollowerAsync(response.Term, ct).ConfigureAwait(false);
                        return;
                    }

                    if (response.VoteGranted)
                    {
                        votesReceived++;
                        if (votesReceived >= majority && _role == RaftRole.Candidate)
                        {
                            await BecomeLeaderAsync(ct).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                catch
                {
                    // Vote request failed -- continue
                }
            }

            // Did not get majority -- stay candidate, will retry on next timeout
        }

        private async Task<RaftMessage?> RequestVoteFromPeerAsync(ClusterNode peer, RaftMessage request, CancellationToken ct)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.ElectionTimeoutMinMs);

                var responseData = await _network.RequestFromPeerAsync(
                    peer.NodeId,
                    request.Serialize(),
                    timeoutCts.Token).ConfigureAwait(false);

                return RaftMessage.Deserialize(responseData);
            }
            catch
            {
                return null;
            }
        }

        private async Task BecomeLeaderAsync(CancellationToken ct)
        {
            string selfId = _membership.GetSelf().NodeId;

            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _role = RaftRole.Leader;
                _leaderId = selfId;

                // Initialize NextIndex and MatchIndex for all peers
                long lastLogIndex = _persistent.Log.Count;
                var peers = _membership.GetMembers()
                    .Where(m => !string.Equals(m.NodeId, selfId, StringComparison.Ordinal));

                foreach (var peer in peers)
                {
                    _volatile.NextIndex[peer.NodeId] = lastLogIndex + 1;
                    _volatile.MatchIndex[peer.NodeId] = 0;
                }
            }
            finally
            {
                _stateLock.Release();
            }

            // Report leader to SWIM membership
            if (_membership is SwimClusterMembership swimMembership)
            {
                swimMembership.SetLeader(selfId);
            }

            // Start heartbeat loop
            _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token), _cts.Token);
        }

        private async Task RunHeartbeatLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.HeartbeatIntervalMs));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_role != RaftRole.Leader) break;

                try
                {
                    await SendHeartbeatsAsync(ct).ConfigureAwait(false);
                    await AdvanceCommitIndexAsync(ct).ConfigureAwait(false);
                    await ApplyCommittedEntriesAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Heartbeat failure should not crash the loop
                }
            }
        }

        private async Task SendHeartbeatsAsync(CancellationToken ct)
        {
            string selfId = _membership.GetSelf().NodeId;
            var peers = _membership.GetMembers()
                .Where(m => !string.Equals(m.NodeId, selfId, StringComparison.Ordinal))
                .ToList();

            var tasks = new List<Task>();

            foreach (var peer in peers)
            {
                tasks.Add(SendAppendEntriesToPeerAsync(peer, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task SendAppendEntriesToPeerAsync(ClusterNode peer, CancellationToken ct)
        {
            try
            {
                long nextIndex = _volatile.NextIndex.GetValueOrDefault(peer.NodeId, 1);
                long prevLogIndex = nextIndex - 1;
                long prevLogTerm = 0;

                await _stateLock.WaitAsync(ct).ConfigureAwait(false);
                List<RaftLogEntry>? entries;
                long currentTerm;
                long commitIndex;
                try
                {
                    currentTerm = _persistent.CurrentTerm;
                    commitIndex = _volatile.CommitIndex;

                    if (prevLogIndex > 0 && prevLogIndex <= _persistent.Log.Count)
                    {
                        prevLogTerm = _persistent.Log[(int)(prevLogIndex - 1)].Term;
                    }

                    // Get entries to send
                    if (nextIndex <= _persistent.Log.Count)
                    {
                        int startIdx = (int)(nextIndex - 1);
                        int count = Math.Min(_persistent.Log.Count - startIdx, 50); // Batch size
                        entries = _persistent.Log.GetRange(startIdx, count);
                    }
                    else
                    {
                        entries = null; // Heartbeat only
                    }
                }
                finally
                {
                    _stateLock.Release();
                }

                var appendEntries = new RaftMessage
                {
                    Type = RaftMessageType.AppendEntries,
                    Term = currentTerm,
                    SenderId = _membership.GetSelf().NodeId,
                    PrevLogIndex = prevLogIndex,
                    PrevLogTerm = prevLogTerm,
                    Entries = entries,
                    LeaderCommit = commitIndex
                };

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.HeartbeatIntervalMs * 2);

                var responseData = await _network.RequestFromPeerAsync(
                    peer.NodeId,
                    appendEntries.Serialize(),
                    timeoutCts.Token).ConfigureAwait(false);

                var response = RaftMessage.Deserialize(responseData);
                if (response == null) return;

                if (response.Term > currentTerm)
                {
                    await StepDownToFollowerAsync(response.Term, ct).ConfigureAwait(false);
                    return;
                }

                if (response.Success)
                {
                    _volatile.MatchIndex[peer.NodeId] = response.MatchIndex;
                    _volatile.NextIndex[peer.NodeId] = response.MatchIndex + 1;
                }
                else
                {
                    // Decrement NextIndex and retry on next heartbeat
                    var currentNext = _volatile.NextIndex.GetValueOrDefault(peer.NodeId, 1);
                    _volatile.NextIndex[peer.NodeId] = Math.Max(1, currentNext - 1);
                }
            }
            catch
            {
                // Peer unreachable -- will retry on next heartbeat
            }
        }

        private async Task ReplicateToAllPeersAsync()
        {
            if (_role != RaftRole.Leader) return;

            string selfId = _membership.GetSelf().NodeId;
            var peers = _membership.GetMembers()
                .Where(m => !string.Equals(m.NodeId, selfId, StringComparison.Ordinal))
                .ToList();

            var tasks = peers.Select(peer => SendAppendEntriesToPeerAsync(peer, CancellationToken.None)).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task AdvanceCommitIndexAsync(CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long logCount = _persistent.Log.Count;
                int totalMembers = _membership.GetMembers().Count;
                int majority = (totalMembers / 2) + 1;

                // Find the highest N such that a majority of MatchIndex[i] >= N
                // and Log[N].Term == currentTerm
                for (long n = logCount; n > _volatile.CommitIndex; n--)
                {
                    if (n <= 0 || n > _persistent.Log.Count) continue;

                    var entry = _persistent.Log[(int)(n - 1)];
                    if (entry.Term != _persistent.CurrentTerm) continue;

                    int replicated = 1; // Leader has it
                    foreach (var matchIndex in _volatile.MatchIndex.Values)
                    {
                        if (matchIndex >= n) replicated++;
                    }

                    if (replicated >= majority)
                    {
                        _volatile.CommitIndex = n;
                        break;
                    }
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private async Task ApplyCommittedEntriesAsync(CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (_volatile.LastApplied < _volatile.CommitIndex)
                {
                    _volatile.LastApplied++;
                    int idx = (int)(_volatile.LastApplied - 1);

                    if (idx >= 0 && idx < _persistent.Log.Count)
                    {
                        var entry = _persistent.Log[idx];
                        var proposal = new Proposal
                        {
                            Command = entry.Command,
                            Payload = entry.Payload
                        };

                        // Invoke commit handlers
                        foreach (var handler in _commitHandlers)
                        {
                            try
                            {
                                handler(proposal);
                            }
                            catch
                            {
                                // Handler errors should not block state machine progress
                            }
                        }

                        // Complete any pending proposal
                        foreach (var pending in _pendingProposals)
                        {
                            pending.Value.TrySetResult(true);
                        }
                    }
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private void HandleNetworkEvent(PeerEvent peerEvent)
        {
            // Network events are handled via RequestFromPeerAsync response path.
            // Incoming Raft messages arrive when peers call RequestFromPeerAsync targeting us.
        }

        /// <summary>
        /// Handles an incoming Raft RPC message from the network.
        /// Called by the transport layer when a Raft message is received.
        /// </summary>
        internal async Task<byte[]?> HandleIncomingMessageAsync(byte[] data, CancellationToken ct = default)
        {
            var message = RaftMessage.Deserialize(data);
            if (message == null) return null;

            return message.Type switch
            {
                RaftMessageType.RequestVote => (await HandleRequestVoteAsync(message, ct).ConfigureAwait(false))?.Serialize(),
                RaftMessageType.AppendEntries => (await HandleAppendEntriesAsync(message, ct).ConfigureAwait(false))?.Serialize(),
                _ => null
            };
        }

        private async Task<RaftMessage> HandleRequestVoteAsync(RaftMessage request, CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // If request term > currentTerm, update and step down
                if (request.Term > _persistent.CurrentTerm)
                {
                    _persistent.CurrentTerm = request.Term;
                    _persistent.VotedFor = null;
                    _role = RaftRole.Follower;
                }

                bool grantVote = false;
                string reason;

                if (request.Term < _persistent.CurrentTerm)
                {
                    reason = "Stale term";
                }
                else if (_persistent.VotedFor != null
                    && !string.Equals(_persistent.VotedFor, request.CandidateId, StringComparison.Ordinal))
                {
                    reason = "Already voted for another candidate";
                }
                else if (!IsLogUpToDate(request.LastLogIndex, request.LastLogTerm))
                {
                    reason = "Candidate log not up-to-date";
                }
                else
                {
                    grantVote = true;
                    _persistent.VotedFor = request.CandidateId;
                    _lastHeartbeatReceived = DateTimeOffset.UtcNow; // Reset election timer
                    reason = "Vote granted";
                }

                return new RaftMessage
                {
                    Type = RaftMessageType.RequestVoteResponse,
                    Term = _persistent.CurrentTerm,
                    SenderId = _membership.GetSelf().NodeId,
                    VoteGranted = grantVote,
                    Reason = reason
                };
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private async Task<RaftMessage> HandleAppendEntriesAsync(RaftMessage request, CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // If term > currentTerm, update and step down
                if (request.Term > _persistent.CurrentTerm)
                {
                    _persistent.CurrentTerm = request.Term;
                    _persistent.VotedFor = null;
                    _role = RaftRole.Follower;
                }

                // If term < currentTerm, reject
                if (request.Term < _persistent.CurrentTerm)
                {
                    return new RaftMessage
                    {
                        Type = RaftMessageType.AppendEntriesResponse,
                        Term = _persistent.CurrentTerm,
                        SenderId = _membership.GetSelf().NodeId,
                        Success = false,
                        MatchIndex = 0
                    };
                }

                // Reset election timer (leader is alive)
                _lastHeartbeatReceived = DateTimeOffset.UtcNow;
                _leaderId = request.SenderId;
                _role = RaftRole.Follower;

                // Report leader to membership
                if (_membership is SwimClusterMembership swimMembership)
                {
                    swimMembership.SetLeader(request.SenderId);
                }

                // Log consistency check
                if (request.PrevLogIndex > 0)
                {
                    if (request.PrevLogIndex > _persistent.Log.Count)
                    {
                        return new RaftMessage
                        {
                            Type = RaftMessageType.AppendEntriesResponse,
                            Term = _persistent.CurrentTerm,
                            SenderId = _membership.GetSelf().NodeId,
                            Success = false,
                            MatchIndex = _persistent.Log.Count
                        };
                    }

                    var existingEntry = _persistent.Log[(int)(request.PrevLogIndex - 1)];
                    if (existingEntry.Term != request.PrevLogTerm)
                    {
                        // Delete conflicting entry and all following
                        _persistent.Log.RemoveRange((int)(request.PrevLogIndex - 1),
                            _persistent.Log.Count - (int)(request.PrevLogIndex - 1));

                        return new RaftMessage
                        {
                            Type = RaftMessageType.AppendEntriesResponse,
                            Term = _persistent.CurrentTerm,
                            SenderId = _membership.GetSelf().NodeId,
                            Success = false,
                            MatchIndex = _persistent.Log.Count
                        };
                    }
                }

                // Append new entries
                if (request.Entries != null && request.Entries.Count > 0)
                {
                    foreach (var entry in request.Entries)
                    {
                        if (entry.Index > _persistent.Log.Count)
                        {
                            _persistent.Log.Add(entry);
                        }
                        else if (entry.Index > 0 && entry.Index <= _persistent.Log.Count)
                        {
                            var existing = _persistent.Log[(int)(entry.Index - 1)];
                            if (existing.Term != entry.Term)
                            {
                                _persistent.Log.RemoveRange((int)(entry.Index - 1),
                                    _persistent.Log.Count - (int)(entry.Index - 1));
                                _persistent.Log.Add(entry);
                            }
                        }
                    }

                    CompactLogIfNeeded();
                }

                // Advance commit index
                if (request.LeaderCommit > _volatile.CommitIndex)
                {
                    _volatile.CommitIndex = Math.Min(request.LeaderCommit, _persistent.Log.Count);
                }

                // Apply committed entries
                while (_volatile.LastApplied < _volatile.CommitIndex)
                {
                    _volatile.LastApplied++;
                    int idx = (int)(_volatile.LastApplied - 1);

                    if (idx >= 0 && idx < _persistent.Log.Count)
                    {
                        var appliedEntry = _persistent.Log[idx];
                        var proposal = new Proposal
                        {
                            Command = appliedEntry.Command,
                            Payload = appliedEntry.Payload
                        };

                        foreach (var handler in _commitHandlers)
                        {
                            try
                            {
                                handler(proposal);
                            }
                            catch
                            {
                                // Handler errors should not block
                            }
                        }
                    }
                }

                return new RaftMessage
                {
                    Type = RaftMessageType.AppendEntriesResponse,
                    Term = _persistent.CurrentTerm,
                    SenderId = _membership.GetSelf().NodeId,
                    Success = true,
                    MatchIndex = _persistent.Log.Count
                };
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private bool IsLogUpToDate(long candidateLastIndex, long candidateLastTerm)
        {
            long myLastIndex = _persistent.Log.Count;
            long myLastTerm = _persistent.Log.Count > 0 ? _persistent.Log[^1].Term : 0;

            if (candidateLastTerm != myLastTerm)
            {
                return candidateLastTerm > myLastTerm;
            }

            return candidateLastIndex >= myLastIndex;
        }

        private async Task StepDownToFollowerAsync(long newTerm, CancellationToken ct)
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _persistent.CurrentTerm = newTerm;
                _persistent.VotedFor = null;
                _role = RaftRole.Follower;
                _lastHeartbeatReceived = DateTimeOffset.UtcNow;
                RandomizeElectionTimeout();
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private void HandleMembershipChanged(ClusterMembershipEvent evt)
        {
            // When nodes join/leave, Raft will naturally adjust through elections
            // No explicit action needed since peer lists come from membership on each cycle
        }

        private void RandomizeElectionTimeout()
        {
            _currentElectionTimeoutMs = RandomNumberGenerator.GetInt32(
                _config.ElectionTimeoutMinMs,
                _config.ElectionTimeoutMaxMs + 1);
        }

        private void CompactLogIfNeeded()
        {
            if (_persistent.Log.Count > _config.MaxLogEntries)
            {
                int toRemove = _persistent.Log.Count - (_config.MaxLogEntries - 1000);
                if (toRemove > 0 && toRemove <= _volatile.CommitIndex)
                {
                    _persistent.Log.RemoveRange(0, toRemove);

                    // Adjust indexes
                    for (int i = 0; i < _persistent.Log.Count; i++)
                    {
                        _persistent.Log[i].Index = i + 1;
                    }
                }
            }
        }

        /// <summary>
        /// Disposes the Raft engine, stopping all background tasks and releasing resources.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _stateLock.Dispose();
            _network.OnPeerEvent -= HandleNetworkEvent;
            _membership.OnMembershipChanged -= HandleMembershipChanged;
        }
    }
}
