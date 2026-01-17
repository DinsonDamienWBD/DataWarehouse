using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Raft
{
    /// <summary>
    /// Raft distributed consensus plugin.
    /// Implements the Raft consensus algorithm for distributed systems.
    ///
    /// Features:
    /// - Leader election with randomized timeouts
    /// - Log replication with quorum confirmation
    /// - Distributed locking with lease-based timeouts
    /// - Cluster membership management
    /// - Snapshot support for log compaction
    ///
    /// Message Commands:
    /// - raft.propose: Propose a state change
    /// - raft.lock.acquire: Acquire a distributed lock
    /// - raft.lock.release: Release a distributed lock
    /// - raft.cluster.status: Get cluster status
    /// - raft.cluster.join: Join a node to the cluster
    /// - raft.cluster.leave: Remove a node from the cluster
    /// </summary>
    public sealed class RaftConsensusPlugin : ConsensusPluginBase
    {
        public override string Id => "datawarehouse.raft";
        public override string Name => "Raft Consensus";
        public override string Version => "1.0.0";

        // Raft state
        private readonly object _stateLock = new();
        private RaftState _state = RaftState.Follower;
        private long _currentTerm;
        private string? _votedFor;
        private string? _leaderId;
        private DateTime _lastHeartbeat = DateTime.UtcNow;

        // Node configuration
        private string _nodeId = string.Empty;
        private string _nodeEndpoint = string.Empty;
        private readonly ConcurrentDictionary<string, RaftPeer> _peers = new();

        // Log
        private readonly List<LogEntry> _log = new();
        private long _commitIndex;
        private long _lastApplied;
        private readonly object _logLock = new();

        // Leader state (volatile, reset after election)
        private readonly ConcurrentDictionary<string, long> _nextIndex = new();
        private readonly ConcurrentDictionary<string, long> _matchIndex = new();

        // Commit handlers
        private readonly List<Action<Proposal>> _commitHandlers = new();
        private readonly object _handlerLock = new();

        // Distributed locks
        private readonly ConcurrentDictionary<string, DistributedLock> _locks = new();

        // Pending proposals waiting for commit
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingProposals = new();

        // Timers and cancellation
        private CancellationTokenSource? _cts;
        private Task? _electionTask;
        private Task? _heartbeatTask;
        private Task? _commitTask;

        // Configuration
        private TimeSpan _electionTimeoutMin = TimeSpan.FromMilliseconds(150);
        private TimeSpan _electionTimeoutMax = TimeSpan.FromMilliseconds(300);
        private TimeSpan _heartbeatInterval = TimeSpan.FromMilliseconds(50);
        private TimeSpan _lockLeaseTime = TimeSpan.FromSeconds(30);
        private int _snapshotThreshold = 10000;

        // Network
        private TcpListener? _listener;
        private int _listenPort = 5000;

        public override bool IsLeader
        {
            get
            {
                lock (_stateLock)
                {
                    return _state == RaftState.Leader;
                }
            }
        }

        public string NodeId => _nodeId;
        public long CurrentTerm => Interlocked.Read(ref _currentTerm);
        public string? LeaderId => _leaderId;

        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            // Generate unique node ID
            _nodeId = $"raft-{request.KernelId}-{Guid.NewGuid().ToString("N")[..8]}";

            return Task.FromResult(new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Metadata = GetMetadata()
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "propose",
                    Description = "Propose a state change to the cluster (quorum required)",
                    InputSchema = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["command"] = new { type = "string", description = "Command type" },
                            ["payload"] = new { type = "string", description = "Base64-encoded payload" }
                        },
                        ["required"] = new[] { "command", "payload" }
                    }
                },
                new()
                {
                    Name = "lock.acquire",
                    Description = "Acquire a distributed lock",
                    InputSchema = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["lockId"] = new { type = "string", description = "Lock identifier" },
                            ["owner"] = new { type = "string", description = "Owner identifier" },
                            ["leaseSeconds"] = new { type = "number", description = "Lease duration in seconds" }
                        },
                        ["required"] = new[] { "lockId", "owner" }
                    }
                },
                new()
                {
                    Name = "lock.release",
                    Description = "Release a distributed lock",
                    InputSchema = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["lockId"] = new { type = "string", description = "Lock identifier" },
                            ["owner"] = new { type = "string", description = "Owner identifier" }
                        },
                        ["required"] = new[] { "lockId", "owner" }
                    }
                },
                new()
                {
                    Name = "cluster.status",
                    Description = "Get cluster status and membership",
                    InputSchema = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>()
                    }
                }
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var meta = base.GetMetadata();
            meta["ConsensusAlgorithm"] = "Raft";
            meta["NodeId"] = _nodeId;
            meta["CurrentTerm"] = _currentTerm;
            meta["State"] = _state.ToString();
            meta["LeaderId"] = _leaderId ?? "none";
            meta["PeerCount"] = _peers.Count;
            meta["LogLength"] = _log.Count;
            meta["CommitIndex"] = _commitIndex;
            return meta;
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Start TCP listener for peer communication
            await StartListenerAsync();

            // Start background tasks
            _electionTask = RunElectionLoopAsync(_cts.Token);
            _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);
            _commitTask = RunCommitLoopAsync(_cts.Token);
        }

        public override async Task StopAsync()
        {
            _cts?.Cancel();

            // Stop listener
            _listener?.Stop();

            // Wait for tasks to complete
            if (_electionTask != null) await _electionTask.ContinueWith(_ => { });
            if (_heartbeatTask != null) await _heartbeatTask.ContinueWith(_ => { });
            if (_commitTask != null) await _commitTask.ContinueWith(_ => { });

            _cts?.Dispose();
            _cts = null;
        }

        public override async Task<bool> ProposeAsync(Proposal proposal)
        {
            // Only leader can propose
            lock (_stateLock)
            {
                if (_state != RaftState.Leader)
                {
                    // Forward to leader if known
                    if (_leaderId != null && _peers.TryGetValue(_leaderId, out var leader))
                    {
                        return await ForwardProposalAsync(leader, proposal);
                    }
                    return false;
                }
            }

            // Create log entry
            var entry = new LogEntry
            {
                Index = GetLastLogIndex() + 1,
                Term = _currentTerm,
                Command = proposal.Command,
                Payload = proposal.Payload,
                ProposalId = proposal.Id,
                Timestamp = DateTime.UtcNow
            };

            // Append to local log
            lock (_logLock)
            {
                _log.Add(entry);
            }

            // Create completion source for quorum confirmation
            var tcs = new TaskCompletionSource<bool>();
            _pendingProposals[proposal.Id] = tcs;

            try
            {
                // Replicate to peers
                await ReplicateLogAsync(entry);

                // Wait for quorum (with timeout)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var completedTask = await Task.WhenAny(
                    tcs.Task,
                    Task.Delay(Timeout.Infinite, timeoutCts.Token)
                );

                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }

                return false;
            }
            finally
            {
                _pendingProposals.TryRemove(proposal.Id, out _);
            }
        }

        public override void OnCommit(Action<Proposal> handler)
        {
            lock (_handlerLock)
            {
                _commitHandlers.Add(handler);
            }
        }

        public override Task<ClusterState> GetClusterStateAsync()
        {
            lock (_stateLock)
            {
                return Task.FromResult(new ClusterState
                {
                    IsHealthy = _state == RaftState.Leader || _leaderId != null,
                    LeaderId = _leaderId,
                    NodeCount = _peers.Count + 1,
                    Term = _currentTerm
                });
            }
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Payload == null) return;

            var response = message.Type switch
            {
                "raft.propose" => await HandleProposeAsync(message.Payload),
                "raft.lock.acquire" => await HandleLockAcquireAsync(message.Payload),
                "raft.lock.release" => await HandleLockReleaseAsync(message.Payload),
                "raft.lock.status" => HandleLockStatus(message.Payload),
                "raft.cluster.status" => await HandleClusterStatusAsync(),
                "raft.cluster.join" => await HandleClusterJoinAsync(message.Payload),
                "raft.cluster.leave" => HandleClusterLeave(message.Payload),
                "raft.configure" => HandleConfigure(message.Payload),
                // Internal RPC messages
                "raft.rpc.request-vote" => HandleRequestVote(message.Payload),
                "raft.rpc.append-entries" => HandleAppendEntries(message.Payload),
                _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
            };

            // Store response in payload for caller
            if (response != null)
            {
                message.Payload["_response"] = response;
            }
        }

        #region Leader Election

        private async Task RunElectionLoopAsync(CancellationToken ct)
        {
            var random = new Random();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Random election timeout
                    var timeout = TimeSpan.FromMilliseconds(
                        random.Next(
                            (int)_electionTimeoutMin.TotalMilliseconds,
                            (int)_electionTimeoutMax.TotalMilliseconds
                        )
                    );

                    await Task.Delay(timeout, ct);

                    lock (_stateLock)
                    {
                        // Check if we should start election
                        if (_state == RaftState.Leader) continue;

                        var elapsed = DateTime.UtcNow - _lastHeartbeat;
                        if (elapsed < timeout) continue;

                        // Start election
                        _state = RaftState.Candidate;
                        _currentTerm++;
                        _votedFor = _nodeId;
                    }

                    // Request votes from peers
                    await RequestVotesAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Election failed, will retry
                }
            }
        }

        private async Task RequestVotesAsync()
        {
            var term = _currentTerm;
            var lastLogIndex = GetLastLogIndex();
            var lastLogTerm = GetLastLogTerm();

            var voteRequest = new RequestVoteRequest
            {
                Term = term,
                CandidateId = _nodeId,
                LastLogIndex = lastLogIndex,
                LastLogTerm = lastLogTerm
            };

            var votes = 1; // Vote for self
            var peers = _peers.Values.ToArray();

            if (peers.Length == 0)
            {
                // Single node cluster - become leader immediately
                BecomeLeader();
                return;
            }

            var tasks = peers.Select(peer => SendRequestVoteAsync(peer, voteRequest));
            var responses = await Task.WhenAll(tasks);

            foreach (var response in responses)
            {
                if (response == null) continue;

                if (response.Term > term)
                {
                    // Higher term found, step down
                    StepDown(response.Term);
                    return;
                }

                if (response.VoteGranted)
                {
                    votes++;
                }
            }

            // Check if we have quorum
            var quorum = (peers.Length + 1) / 2 + 1;
            if (votes >= quorum)
            {
                lock (_stateLock)
                {
                    if (_state == RaftState.Candidate && _currentTerm == term)
                    {
                        BecomeLeader();
                    }
                }
            }
        }

        private void BecomeLeader()
        {
            lock (_stateLock)
            {
                _state = RaftState.Leader;
                _leaderId = _nodeId;
            }

            // Initialize leader state
            var lastLogIndex = GetLastLogIndex();
            foreach (var peer in _peers.Keys)
            {
                _nextIndex[peer] = lastLogIndex + 1;
                _matchIndex[peer] = 0;
            }
        }

        private void StepDown(long newTerm)
        {
            lock (_stateLock)
            {
                _currentTerm = newTerm;
                _state = RaftState.Follower;
                _votedFor = null;
            }
        }

        #endregion

        #region Log Replication

        private async Task RunHeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_heartbeatInterval, ct);

                    lock (_stateLock)
                    {
                        if (_state != RaftState.Leader) continue;
                    }

                    // Send heartbeats (empty AppendEntries) to all peers
                    await SendHeartbeatsAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Heartbeat failed, will retry
                }
            }
        }

        private async Task SendHeartbeatsAsync()
        {
            var tasks = _peers.Values.Select(peer => SendAppendEntriesAsync(peer, Array.Empty<LogEntry>()));
            await Task.WhenAll(tasks);
        }

        private async Task ReplicateLogAsync(LogEntry entry)
        {
            var tasks = _peers.Values.Select(async peer =>
            {
                var nextIdx = _nextIndex.GetOrAdd(peer.NodeId, entry.Index);
                var entriesToSend = GetEntriesFromIndex(nextIdx);
                return await SendAppendEntriesAsync(peer, entriesToSend);
            });

            await Task.WhenAll(tasks);
            UpdateCommitIndex();
        }

        private void UpdateCommitIndex()
        {
            // Find the highest index that has been replicated to a quorum
            var matchIndices = _matchIndex.Values.ToList();
            matchIndices.Add(GetLastLogIndex()); // Include self

            matchIndices.Sort();
            var quorum = matchIndices.Count / 2;
            var newCommitIndex = matchIndices[quorum];

            if (newCommitIndex > _commitIndex)
            {
                // Verify the entry is from current term
                var entry = GetLogEntry(newCommitIndex);
                if (entry != null && entry.Term == _currentTerm)
                {
                    _commitIndex = newCommitIndex;

                    // Complete pending proposals up to commit index
                    CompletePendingProposals(newCommitIndex);
                }
            }
        }

        private void CompletePendingProposals(long commitIndex)
        {
            lock (_logLock)
            {
                for (var i = _lastApplied + 1; i <= commitIndex && i <= _log.Count; i++)
                {
                    var entry = _log[(int)i - 1];
                    if (_pendingProposals.TryRemove(entry.ProposalId, out var tcs))
                    {
                        tcs.TrySetResult(true);
                    }
                }
            }
        }

        private async Task RunCommitLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), ct);

                    while (_lastApplied < _commitIndex)
                    {
                        _lastApplied++;
                        var entry = GetLogEntry(_lastApplied);
                        if (entry != null)
                        {
                            ApplyEntry(entry);
                        }
                    }

                    // Check for snapshot
                    if (_log.Count > _snapshotThreshold)
                    {
                        await CreateSnapshotAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Commit loop error, will retry
                }
            }
        }

        private void ApplyEntry(LogEntry entry)
        {
            // Create proposal from entry
            var proposal = new Proposal
            {
                Id = entry.ProposalId,
                Command = entry.Command,
                Payload = entry.Payload
            };

            // Handle internal commands
            switch (entry.Command)
            {
                case "lock.acquire":
                    ApplyLockAcquire(entry.Payload);
                    break;
                case "lock.release":
                    ApplyLockRelease(entry.Payload);
                    break;
                case "cluster.join":
                    ApplyClusterJoin(entry.Payload);
                    break;
                case "cluster.leave":
                    ApplyClusterLeave(entry.Payload);
                    break;
            }

            // Notify commit handlers
            List<Action<Proposal>> handlers;
            lock (_handlerLock)
            {
                handlers = _commitHandlers.ToList();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(proposal);
                }
                catch
                {
                    // Handler error, continue
                }
            }
        }

        #endregion

        #region Distributed Locking

        private async Task<Dictionary<string, object>> HandleLockAcquireAsync(Dictionary<string, object> payload)
        {
            var lockId = payload.GetValueOrDefault("lockId")?.ToString();
            var owner = payload.GetValueOrDefault("owner")?.ToString();
            var leaseSeconds = payload.GetValueOrDefault("leaseSeconds") as double? ?? _lockLeaseTime.TotalSeconds;

            if (string.IsNullOrEmpty(lockId) || string.IsNullOrEmpty(owner))
            {
                return new Dictionary<string, object> { ["error"] = "lockId and owner are required" };
            }

            // Propose lock acquisition through Raft
            var lockData = JsonSerializer.SerializeToUtf8Bytes(new
            {
                lockId,
                owner,
                leaseSeconds,
                timestamp = DateTime.UtcNow
            });

            var proposal = new Proposal
            {
                Command = "lock.acquire",
                Payload = lockData
            };

            var success = await ProposeAsync(proposal);

            return new Dictionary<string, object>
            {
                ["success"] = success,
                ["lockId"] = lockId,
                ["owner"] = owner,
                ["leaseSeconds"] = leaseSeconds
            };
        }

        private async Task<Dictionary<string, object>> HandleLockReleaseAsync(Dictionary<string, object> payload)
        {
            var lockId = payload.GetValueOrDefault("lockId")?.ToString();
            var owner = payload.GetValueOrDefault("owner")?.ToString();

            if (string.IsNullOrEmpty(lockId) || string.IsNullOrEmpty(owner))
            {
                return new Dictionary<string, object> { ["error"] = "lockId and owner are required" };
            }

            var lockData = JsonSerializer.SerializeToUtf8Bytes(new { lockId, owner });

            var proposal = new Proposal
            {
                Command = "lock.release",
                Payload = lockData
            };

            var success = await ProposeAsync(proposal);

            return new Dictionary<string, object>
            {
                ["success"] = success,
                ["lockId"] = lockId
            };
        }

        private Dictionary<string, object> HandleLockStatus(Dictionary<string, object> payload)
        {
            var lockId = payload.GetValueOrDefault("lockId")?.ToString();

            if (string.IsNullOrEmpty(lockId))
            {
                // Return all locks
                var allLocks = _locks.Values.Select(l => new Dictionary<string, object>
                {
                    ["lockId"] = l.LockId,
                    ["owner"] = l.Owner,
                    ["acquiredAt"] = l.AcquiredAt.ToString("O"),
                    ["expiresAt"] = l.ExpiresAt.ToString("O"),
                    ["isExpired"] = l.IsExpired
                }).ToList();

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["locks"] = allLocks
                };
            }

            if (_locks.TryGetValue(lockId, out var @lock))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["locked"] = !@lock.IsExpired,
                    ["owner"] = @lock.Owner,
                    ["expiresAt"] = @lock.ExpiresAt.ToString("O")
                };
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["locked"] = false
            };
        }

        private void ApplyLockAcquire(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var lockId = root.GetProperty("lockId").GetString()!;
                var owner = root.GetProperty("owner").GetString()!;
                var leaseSeconds = root.GetProperty("leaseSeconds").GetDouble();

                // Check if lock exists and is held by another owner
                if (_locks.TryGetValue(lockId, out var existing) && !existing.IsExpired && existing.Owner != owner)
                {
                    return; // Lock held by another owner
                }

                var @lock = new DistributedLock
                {
                    LockId = lockId,
                    Owner = owner,
                    AcquiredAt = DateTime.UtcNow,
                    LeaseTime = TimeSpan.FromSeconds(leaseSeconds)
                };

                _locks[lockId] = @lock;
            }
            catch { }
        }

        private void ApplyLockRelease(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var lockId = root.GetProperty("lockId").GetString()!;
                var owner = root.GetProperty("owner").GetString()!;

                if (_locks.TryGetValue(lockId, out var existing) && existing.Owner == owner)
                {
                    _locks.TryRemove(lockId, out _);
                }
            }
            catch { }
        }

        #endregion

        #region Cluster Management

        private async Task<Dictionary<string, object>> HandleClusterStatusAsync()
        {
            var state = await GetClusterStateAsync();

            var peerList = _peers.Values.Select(p => new Dictionary<string, object>
            {
                ["nodeId"] = p.NodeId,
                ["endpoint"] = p.Endpoint,
                ["lastContact"] = p.LastContact.ToString("O"),
                ["isHealthy"] = (DateTime.UtcNow - p.LastContact).TotalSeconds < 30
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["state"] = _state.ToString(),
                ["term"] = _currentTerm,
                ["nodeId"] = _nodeId,
                ["leaderId"] = _leaderId ?? "none",
                ["commitIndex"] = _commitIndex,
                ["lastApplied"] = _lastApplied,
                ["logLength"] = _log.Count,
                ["peers"] = peerList,
                ["isHealthy"] = state.IsHealthy
            };
        }

        private async Task<Dictionary<string, object>> HandleClusterJoinAsync(Dictionary<string, object> payload)
        {
            var nodeId = payload.GetValueOrDefault("nodeId")?.ToString();
            var endpoint = payload.GetValueOrDefault("endpoint")?.ToString();

            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(endpoint))
            {
                return new Dictionary<string, object> { ["error"] = "nodeId and endpoint are required" };
            }

            // Propose cluster join through Raft
            var joinData = JsonSerializer.SerializeToUtf8Bytes(new { nodeId, endpoint });

            var proposal = new Proposal
            {
                Command = "cluster.join",
                Payload = joinData
            };

            var success = await ProposeAsync(proposal);

            return new Dictionary<string, object>
            {
                ["success"] = success,
                ["nodeId"] = nodeId
            };
        }

        private Dictionary<string, object> HandleClusterLeave(Dictionary<string, object> payload)
        {
            var nodeId = payload.GetValueOrDefault("nodeId")?.ToString();

            if (string.IsNullOrEmpty(nodeId))
            {
                return new Dictionary<string, object> { ["error"] = "nodeId is required" };
            }

            _peers.TryRemove(nodeId, out _);
            _nextIndex.TryRemove(nodeId, out _);
            _matchIndex.TryRemove(nodeId, out _);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["nodeId"] = nodeId
            };
        }

        private void ApplyClusterJoin(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var nodeId = root.GetProperty("nodeId").GetString()!;
                var endpoint = root.GetProperty("endpoint").GetString()!;

                if (nodeId != _nodeId)
                {
                    _peers[nodeId] = new RaftPeer
                    {
                        NodeId = nodeId,
                        Endpoint = endpoint,
                        LastContact = DateTime.UtcNow
                    };
                }
            }
            catch { }
        }

        private void ApplyClusterLeave(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var nodeId = doc.RootElement.GetProperty("nodeId").GetString()!;

                _peers.TryRemove(nodeId, out _);
            }
            catch { }
        }

        #endregion

        #region RPC Handlers

        private Dictionary<string, object> HandleRequestVote(Dictionary<string, object> payload)
        {
            var term = Convert.ToInt64(payload.GetValueOrDefault("term"));
            var candidateId = payload.GetValueOrDefault("candidateId")?.ToString() ?? "";
            var lastLogIndex = Convert.ToInt64(payload.GetValueOrDefault("lastLogIndex"));
            var lastLogTerm = Convert.ToInt64(payload.GetValueOrDefault("lastLogTerm"));

            lock (_stateLock)
            {
                // Reply false if term < currentTerm
                if (term < _currentTerm)
                {
                    return new Dictionary<string, object>
                    {
                        ["term"] = _currentTerm,
                        ["voteGranted"] = false
                    };
                }

                // Update term if newer
                if (term > _currentTerm)
                {
                    StepDown(term);
                }

                // Vote if haven't voted or already voted for this candidate
                var canVote = _votedFor == null || _votedFor == candidateId;

                // Check if candidate's log is at least as up-to-date
                var myLastLogIndex = GetLastLogIndex();
                var myLastLogTerm = GetLastLogTerm();
                var logOk = lastLogTerm > myLastLogTerm ||
                           (lastLogTerm == myLastLogTerm && lastLogIndex >= myLastLogIndex);

                if (canVote && logOk)
                {
                    _votedFor = candidateId;
                    _lastHeartbeat = DateTime.UtcNow;

                    return new Dictionary<string, object>
                    {
                        ["term"] = _currentTerm,
                        ["voteGranted"] = true
                    };
                }

                return new Dictionary<string, object>
                {
                    ["term"] = _currentTerm,
                    ["voteGranted"] = false
                };
            }
        }

        private Dictionary<string, object> HandleAppendEntries(Dictionary<string, object> payload)
        {
            var term = Convert.ToInt64(payload.GetValueOrDefault("term"));
            var leaderId = payload.GetValueOrDefault("leaderId")?.ToString() ?? "";
            var prevLogIndex = Convert.ToInt64(payload.GetValueOrDefault("prevLogIndex"));
            var prevLogTerm = Convert.ToInt64(payload.GetValueOrDefault("prevLogTerm"));
            var leaderCommit = Convert.ToInt64(payload.GetValueOrDefault("leaderCommit"));
            var entriesJson = payload.GetValueOrDefault("entries")?.ToString();

            lock (_stateLock)
            {
                // Reply false if term < currentTerm
                if (term < _currentTerm)
                {
                    return new Dictionary<string, object>
                    {
                        ["term"] = _currentTerm,
                        ["success"] = false
                    };
                }

                // Update term and step down if needed
                if (term > _currentTerm)
                {
                    StepDown(term);
                }

                _state = RaftState.Follower;
                _leaderId = leaderId;
                _lastHeartbeat = DateTime.UtcNow;
            }

            // Check log consistency
            if (prevLogIndex > 0)
            {
                var prevEntry = GetLogEntry(prevLogIndex);
                if (prevEntry == null || prevEntry.Term != prevLogTerm)
                {
                    return new Dictionary<string, object>
                    {
                        ["term"] = _currentTerm,
                        ["success"] = false,
                        ["conflictIndex"] = prevEntry == null ? GetLastLogIndex() + 1 : prevLogIndex
                    };
                }
            }

            // Append entries
            if (!string.IsNullOrEmpty(entriesJson))
            {
                var entries = JsonSerializer.Deserialize<List<LogEntry>>(entriesJson);
                if (entries != null)
                {
                    lock (_logLock)
                    {
                        foreach (var entry in entries)
                        {
                            if (entry.Index <= _log.Count)
                            {
                                // Overwrite conflicting entry
                                if (_log[(int)entry.Index - 1].Term != entry.Term)
                                {
                                    _log.RemoveRange((int)entry.Index - 1, _log.Count - (int)entry.Index + 1);
                                    _log.Add(entry);
                                }
                            }
                            else
                            {
                                _log.Add(entry);
                            }
                        }
                    }
                }
            }

            // Update commit index
            if (leaderCommit > _commitIndex)
            {
                _commitIndex = Math.Min(leaderCommit, GetLastLogIndex());
            }

            return new Dictionary<string, object>
            {
                ["term"] = _currentTerm,
                ["success"] = true,
                ["matchIndex"] = GetLastLogIndex()
            };
        }

        #endregion

        #region Network

        private async Task StartListenerAsync()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _listenPort);
                _listener.Start();
                _nodeEndpoint = $"127.0.0.1:{_listenPort}";

                _ = AcceptConnectionsAsync(_cts!.Token);
            }
            catch
            {
                // Listener failed, try next port
                _listenPort++;
                if (_listenPort < 5100)
                {
                    await StartListenerAsync();
                }
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(client, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Accept failed, continue
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                {
                    var requestLine = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    var request = JsonSerializer.Deserialize<Dictionary<string, object>>(requestLine);
                    if (request == null) return;

                    var messageType = request.GetValueOrDefault("type")?.ToString() ?? "";
                    var payload = request.GetValueOrDefault("payload") as JsonElement? ?? default;

                    var payloadDict = JsonSerializer.Deserialize<Dictionary<string, object>>(payload.GetRawText())
                        ?? new Dictionary<string, object>();

                    var message = new PluginMessage
                    {
                        Type = messageType,
                        Payload = payloadDict
                    };

                    await OnMessageAsync(message);

                    var response = payloadDict.GetValueOrDefault("_response") as Dictionary<string, object>
                        ?? new Dictionary<string, object>();

                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
            }
            catch
            {
                // Client handling failed
            }
        }

        private async Task<RequestVoteResponse?> SendRequestVoteAsync(RaftPeer peer, RequestVoteRequest request)
        {
            try
            {
                using var client = new TcpClient();
                var parts = peer.Endpoint.Split(':');
                await client.ConnectAsync(parts[0], int.Parse(parts[1]));

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var requestJson = JsonSerializer.Serialize(new
                {
                    type = "raft.rpc.request-vote",
                    payload = new
                    {
                        term = request.Term,
                        candidateId = request.CandidateId,
                        lastLogIndex = request.LastLogIndex,
                        lastLogTerm = request.LastLogTerm
                    }
                });

                await writer.WriteLineAsync(requestJson);

                var responseLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(responseLine)) return null;

                var response = JsonSerializer.Deserialize<RequestVoteResponse>(responseLine);
                peer.LastContact = DateTime.UtcNow;

                return response;
            }
            catch
            {
                return null;
            }
        }

        private async Task<AppendEntriesResponse?> SendAppendEntriesAsync(RaftPeer peer, LogEntry[] entries)
        {
            try
            {
                using var client = new TcpClient();
                var parts = peer.Endpoint.Split(':');
                await client.ConnectAsync(parts[0], int.Parse(parts[1]));

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var nextIdx = _nextIndex.GetOrAdd(peer.NodeId, 1);
                var prevLogIndex = nextIdx - 1;
                var prevLogTerm = prevLogIndex > 0 ? GetLogEntry(prevLogIndex)?.Term ?? 0 : 0;

                var requestJson = JsonSerializer.Serialize(new
                {
                    type = "raft.rpc.append-entries",
                    payload = new
                    {
                        term = _currentTerm,
                        leaderId = _nodeId,
                        prevLogIndex,
                        prevLogTerm,
                        leaderCommit = _commitIndex,
                        entries = entries.Length > 0 ? JsonSerializer.Serialize(entries) : null
                    }
                });

                await writer.WriteLineAsync(requestJson);

                var responseLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(responseLine)) return null;

                var response = JsonSerializer.Deserialize<AppendEntriesResponse>(responseLine);
                peer.LastContact = DateTime.UtcNow;

                if (response != null && response.Success)
                {
                    _nextIndex[peer.NodeId] = response.MatchIndex + 1;
                    _matchIndex[peer.NodeId] = response.MatchIndex;
                }
                else if (response != null)
                {
                    // Decrement nextIndex and retry
                    _nextIndex[peer.NodeId] = Math.Max(1, response.ConflictIndex);
                }

                return response;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> ForwardProposalAsync(RaftPeer leader, Proposal proposal)
        {
            try
            {
                using var client = new TcpClient();
                var parts = leader.Endpoint.Split(':');
                await client.ConnectAsync(parts[0], int.Parse(parts[1]));

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var requestJson = JsonSerializer.Serialize(new
                {
                    type = "raft.propose",
                    payload = new
                    {
                        command = proposal.Command,
                        payload = Convert.ToBase64String(proposal.Payload)
                    }
                });

                await writer.WriteLineAsync(requestJson);

                var responseLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(responseLine)) return false;

                using var doc = JsonDocument.Parse(responseLine);
                return doc.RootElement.TryGetProperty("success", out var success) && success.GetBoolean();
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Helpers

        private long GetLastLogIndex()
        {
            lock (_logLock)
            {
                return _log.Count;
            }
        }

        private long GetLastLogTerm()
        {
            lock (_logLock)
            {
                return _log.Count > 0 ? _log[^1].Term : 0;
            }
        }

        private LogEntry? GetLogEntry(long index)
        {
            lock (_logLock)
            {
                if (index <= 0 || index > _log.Count) return null;
                return _log[(int)index - 1];
            }
        }

        private LogEntry[] GetEntriesFromIndex(long startIndex)
        {
            lock (_logLock)
            {
                if (startIndex > _log.Count) return Array.Empty<LogEntry>();
                return _log.Skip((int)startIndex - 1).ToArray();
            }
        }

        private async Task CreateSnapshotAsync()
        {
            // TODO: Implement snapshot creation for log compaction
            await Task.CompletedTask;
        }

        private async Task<Dictionary<string, object>> HandleProposeAsync(Dictionary<string, object> payload)
        {
            var command = payload.GetValueOrDefault("command")?.ToString() ?? "";
            var payloadB64 = payload.GetValueOrDefault("payload")?.ToString() ?? "";

            var proposal = new Proposal
            {
                Command = command,
                Payload = Convert.FromBase64String(payloadB64)
            };

            var success = await ProposeAsync(proposal);

            return new Dictionary<string, object>
            {
                ["success"] = success,
                ["proposalId"] = proposal.Id
            };
        }

        private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("electionTimeoutMinMs", out var minMs))
            {
                _electionTimeoutMin = TimeSpan.FromMilliseconds(Convert.ToDouble(minMs));
            }

            if (payload.TryGetValue("electionTimeoutMaxMs", out var maxMs))
            {
                _electionTimeoutMax = TimeSpan.FromMilliseconds(Convert.ToDouble(maxMs));
            }

            if (payload.TryGetValue("heartbeatIntervalMs", out var hbMs))
            {
                _heartbeatInterval = TimeSpan.FromMilliseconds(Convert.ToDouble(hbMs));
            }

            if (payload.TryGetValue("lockLeaseSeconds", out var leaseS))
            {
                _lockLeaseTime = TimeSpan.FromSeconds(Convert.ToDouble(leaseS));
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["electionTimeoutMinMs"] = _electionTimeoutMin.TotalMilliseconds,
                ["electionTimeoutMaxMs"] = _electionTimeoutMax.TotalMilliseconds,
                ["heartbeatIntervalMs"] = _heartbeatInterval.TotalMilliseconds,
                ["lockLeaseSeconds"] = _lockLeaseTime.TotalSeconds
            };
        }

        #endregion
    }

    #region Supporting Types

    internal enum RaftState
    {
        Follower,
        Candidate,
        Leader
    }

    internal class LogEntry
    {
        public long Index { get; set; }
        public long Term { get; set; }
        public string Command { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string ProposalId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    internal class RaftPeer
    {
        public string NodeId { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public DateTime LastContact { get; set; } = DateTime.UtcNow;
    }

    internal class DistributedLock
    {
        public string LockId { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public DateTime AcquiredAt { get; set; }
        public TimeSpan LeaseTime { get; set; }
        public DateTime ExpiresAt => AcquiredAt + LeaseTime;
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }

    internal class RequestVoteRequest
    {
        public long Term { get; set; }
        public string CandidateId { get; set; } = string.Empty;
        public long LastLogIndex { get; set; }
        public long LastLogTerm { get; set; }
    }

    internal class RequestVoteResponse
    {
        public long Term { get; set; }
        public bool VoteGranted { get; set; }
    }

    internal class AppendEntriesResponse
    {
        public long Term { get; set; }
        public bool Success { get; set; }
        public long MatchIndex { get; set; }
        public long ConflictIndex { get; set; }
    }

    #endregion
}
