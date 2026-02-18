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
    [Obsolete("Replaced by UltimateConsensus with Multi-Raft. Remove in v4.0.")]
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

        // Log storage - persistent
        private IRaftLogStore? _logStore;
        private long _commitIndex;
        private long _lastApplied;

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
        private RaftConfiguration _config = new();

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

        /// <summary>
        /// Initializes a new instance of the RaftConsensusPlugin with default configuration.
        /// </summary>
        public RaftConsensusPlugin() : this(new RaftConfiguration())
        {
        }

        /// <summary>
        /// Initializes a new instance of the RaftConsensusPlugin with the specified configuration.
        /// </summary>
        /// <param name="config">The Raft configuration settings.</param>
        public RaftConsensusPlugin(RaftConfiguration config)
        {
            _config = config ?? new RaftConfiguration();
        }

        /// <summary>
        /// Initializes a new instance of the RaftConsensusPlugin with custom log store.
        /// </summary>
        /// <param name="config">The Raft configuration settings.</param>
        /// <param name="logStore">Custom log store implementation.</param>
        public RaftConsensusPlugin(RaftConfiguration config, IRaftLogStore logStore)
        {
            _config = config ?? new RaftConfiguration();
            _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
        }

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
                    Parameters = new Dictionary<string, object>
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
                    Parameters = new Dictionary<string, object>
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
                    Parameters = new Dictionary<string, object>
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
                    Parameters = new Dictionary<string, object>
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
            // Cannot be async: Override of base synchronous GetMetadata(). Using Task.Run to prevent sync context deadlock.
            meta["LogLength"] = _logStore != null ? Task.Run(() => _logStore.GetLastIndexAsync()).GetAwaiter().GetResult() : 0;
            meta["CommitIndex"] = _commitIndex;
            return meta;
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Initialize log store if not provided
            if (_logStore == null)
            {
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DataWarehouse", "Raft", _nodeId);
                _logStore = new FileRaftLogStore(dataDir);
            }

            // Initialize and load persistent state
            if (_logStore is FileRaftLogStore fileStore)
            {
                await fileStore.InitializeAsync();
            }

            // Restore persistent state
            var (term, votedFor) = await _logStore.GetPersistentStateAsync();
            lock (_stateLock)
            {
                _currentTerm = term;
                _votedFor = votedFor;
            }

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

            // Dispose log store if owned
            if (_logStore is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _cts?.Dispose();
            _cts = null;
        }

        public override async Task<bool> ProposeAsync(Proposal proposal)
        {
            // Only leader can propose
            RaftPeer? leaderToForward = null;
            bool shouldForward = false;

            lock (_stateLock)
            {
                if (_state != RaftState.Leader)
                {
                    // Forward to leader if known
                    if (_leaderId != null && _peers.TryGetValue(_leaderId, out var leader))
                    {
                        leaderToForward = leader;
                        shouldForward = true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if (shouldForward && leaderToForward != null)
            {
                return await ForwardProposalAsync(leaderToForward, proposal);
            }

            // Create log entry
            var lastIndex = await GetLastLogIndexAsync();
            var entry = new RaftLogEntry
            {
                Index = lastIndex + 1,
                Term = _currentTerm,
                Command = proposal.Command,
                Payload = proposal.Payload,
                ProposalId = proposal.Id,
                Timestamp = DateTime.UtcNow
            };

            // Append to persistent log
            if (_logStore == null)
                throw new InvalidOperationException("Log store not initialized");

            await _logStore.AppendAsync(entry);

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
                "raft.rpc.request-vote" => await HandleRequestVoteAsync(message.Payload),
                "raft.rpc.append-entries" => await HandleAppendEntriesAsync(message.Payload),
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
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Random election timeout
                    var timeout = TimeSpan.FromMilliseconds(
                        Random.Shared.Next(
                            (int)_electionTimeoutMin.TotalMilliseconds,
                            (int)_electionTimeoutMax.TotalMilliseconds
                        )
                    );

                    await Task.Delay(timeout, ct);

                    bool shouldStartElection = false;
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
                        shouldStartElection = true;
                    }

                    if (shouldStartElection && _logStore != null)
                    {
                        // Persist state before requesting votes
                        await _logStore.SavePersistentStateAsync(_currentTerm, _votedFor);

                        // Request votes from peers
                        await RequestVotesAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Election failed, will retry
                    Console.WriteLine($"[Raft] Election attempt failed - Node: {_nodeId}, State: {_state}, Term: {_currentTerm}, Error: {ex.Message}");
                }
            }
        }

        private async Task RequestVotesAsync()
        {
            var term = _currentTerm;
            var lastLogIndex = await GetLastLogIndexAsync();
            var lastLogTerm = await GetLastLogTermAsync();

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
                await BecomeLeaderAsync();
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
                    await StepDownAsync(response.Term);
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
                        _ = BecomeLeaderAsync();
                    }
                }
            }
        }

        private async Task BecomeLeaderAsync()
        {
            lock (_stateLock)
            {
                _state = RaftState.Leader;
                _leaderId = _nodeId;
            }

            // Initialize leader state
            var lastLogIndex = await GetLastLogIndexAsync();
            foreach (var peer in _peers.Keys)
            {
                _nextIndex[peer] = lastLogIndex + 1;
                _matchIndex[peer] = 0;
            }
        }

        private async Task StepDownAsync(long newTerm)
        {
            lock (_stateLock)
            {
                _currentTerm = newTerm;
                _state = RaftState.Follower;
                _votedFor = null;
            }

            if (_logStore != null)
            {
                await _logStore.SavePersistentStateAsync(newTerm, null);
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
                catch (Exception ex)
                {
                    // Heartbeat failed, will retry - expected during network partitions
                    Console.WriteLine($"[Raft] Heartbeat failed - Node: {_nodeId}, State: {_state}, Term: {_currentTerm}, PeerCount: {_peers.Count}, Error: {ex.Message}");
                }
            }
        }

        private async Task SendHeartbeatsAsync()
        {
            var tasks = _peers.Values.Select(peer => SendAppendEntriesAsync(peer, Array.Empty<RaftLogEntry>()));
            await Task.WhenAll(tasks);
        }

        private async Task ReplicateLogAsync(RaftLogEntry entry)
        {
            var tasks = _peers.Values.Select(async peer =>
            {
                var nextIdx = _nextIndex.GetOrAdd(peer.NodeId, entry.Index);
                var entriesToSend = await GetEntriesFromIndexAsync(nextIdx);
                return await SendAppendEntriesAsync(peer, entriesToSend);
            });

            await Task.WhenAll(tasks);
            await UpdateCommitIndexAsync();
        }

        private async Task UpdateCommitIndexAsync()
        {
            // Find the highest index that has been replicated to a quorum
            var matchIndices = _matchIndex.Values.ToList();
            matchIndices.Add(await GetLastLogIndexAsync()); // Include self

            matchIndices.Sort();
            var quorum = matchIndices.Count / 2;
            var newCommitIndex = matchIndices[quorum];

            if (newCommitIndex > _commitIndex)
            {
                // Verify the entry is from current term
                var entry = await GetLogEntryAsync(newCommitIndex);
                if (entry != null && entry.Term == _currentTerm)
                {
                    _commitIndex = newCommitIndex;

                    // Complete pending proposals up to commit index
                    await CompletePendingProposalsAsync(newCommitIndex);
                }
            }
        }

        private async Task CompletePendingProposalsAsync(long commitIndex)
        {
            if (_logStore == null) return;

            var lastIndex = await _logStore.GetLastIndexAsync();
            for (var i = _lastApplied + 1; i <= commitIndex && i <= lastIndex; i++)
            {
                var entry = await _logStore.GetEntryAsync(i);
                if (entry != null && _pendingProposals.TryRemove(entry.ProposalId, out var tcs))
                {
                    tcs.TrySetResult(true);
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
                        var entry = await GetLogEntryAsync(_lastApplied);
                        if (entry != null)
                        {
                            ApplyEntry(entry);
                        }
                    }

                    // Check for snapshot
                    var logLength = _logStore != null ? await _logStore.GetLastIndexAsync() : 0;
                    if (logLength > _snapshotThreshold)
                    {
                        await CreateSnapshotAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Commit loop error, will retry
                    Console.WriteLine($"[Raft] Commit loop error - Node: {_nodeId}, LastApplied: {_lastApplied}, CommitIndex: {_commitIndex}, Error: {ex.Message}");
                }
            }
        }

        private void ApplyEntry(RaftLogEntry entry)
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
                catch (Exception ex)
                {
                    // Handler error, continue - individual handler failures should not block commit processing
                    Console.WriteLine($"[Raft] Commit handler failed - Node: {_nodeId}, ProposalId: {proposal.Id}, Command: {proposal.Command}, Error: {ex.Message}");
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
            catch (Exception ex)
            {
                // Failed to parse lock acquire payload
                Console.WriteLine($"[Raft] Lock acquire parse failed - Node: {_nodeId}, PayloadLength: {payload.Length}, Error: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                // Failed to parse lock release payload
                Console.WriteLine($"[Raft] Lock release parse failed - Node: {_nodeId}, PayloadLength: {payload.Length}, Error: {ex.Message}");
            }
        }

        #endregion

        #region Cluster Management

        private async Task<Dictionary<string, object>> HandleClusterStatusAsync()
        {
            var state = await GetClusterStateAsync();
            var logLength = await GetLastLogIndexAsync();

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
                ["logLength"] = logLength,
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
            catch (Exception ex)
            {
                // Failed to parse cluster join payload
                Console.WriteLine($"[Raft] Cluster join parse failed - Node: {_nodeId}, PayloadLength: {payload.Length}, Error: {ex.Message}");
            }
        }

        private void ApplyClusterLeave(byte[] payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var nodeId = doc.RootElement.GetProperty("nodeId").GetString()!;

                _peers.TryRemove(nodeId, out _);
            }
            catch (Exception ex)
            {
                // Failed to parse cluster leave payload
                Console.WriteLine($"[Raft] Cluster leave parse failed - Node: {_nodeId}, PayloadLength: {payload.Length}, Error: {ex.Message}");
            }
        }

        #endregion

        #region RPC Handlers

        private async Task<Dictionary<string, object>> HandleRequestVoteAsync(Dictionary<string, object> payload)
        {
            var term = Convert.ToInt64(payload.GetValueOrDefault("term"));
            var candidateId = payload.GetValueOrDefault("candidateId")?.ToString() ?? "";
            var lastLogIndex = Convert.ToInt64(payload.GetValueOrDefault("lastLogIndex"));
            var lastLogTerm = Convert.ToInt64(payload.GetValueOrDefault("lastLogTerm"));

            bool shouldStepDown = false;
            bool shouldGrantVote = false;

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
                    shouldStepDown = true;
                }
            }

            if (shouldStepDown)
            {
                await StepDownAsync(term);
            }

            // Check if candidate's log is at least as up-to-date (must be done outside lock due to async)
            var myLastLogIndex = await GetLastLogIndexAsync();
            var myLastLogTerm = await GetLastLogTermAsync();

            lock (_stateLock)
            {
                // Vote if haven't voted or already voted for this candidate
                var canVote = _votedFor == null || _votedFor == candidateId;

                var logOk = lastLogTerm > myLastLogTerm ||
                           (lastLogTerm == myLastLogTerm && lastLogIndex >= myLastLogIndex);

                if (canVote && logOk)
                {
                    _votedFor = candidateId;
                    _lastHeartbeat = DateTime.UtcNow;
                    shouldGrantVote = true;
                }
            }

            if (shouldGrantVote && _logStore != null)
            {
                await _logStore.SavePersistentStateAsync(_currentTerm, _votedFor);
            }

            lock (_stateLock)
            {
                return new Dictionary<string, object>
                {
                    ["term"] = _currentTerm,
                    ["voteGranted"] = shouldGrantVote
                };
            }
        }

        private async Task<Dictionary<string, object>> HandleAppendEntriesAsync(Dictionary<string, object> payload)
        {
            var term = Convert.ToInt64(payload.GetValueOrDefault("term"));
            var leaderId = payload.GetValueOrDefault("leaderId")?.ToString() ?? "";
            var prevLogIndex = Convert.ToInt64(payload.GetValueOrDefault("prevLogIndex"));
            var prevLogTerm = Convert.ToInt64(payload.GetValueOrDefault("prevLogTerm"));
            var leaderCommit = Convert.ToInt64(payload.GetValueOrDefault("leaderCommit"));
            var entriesJson = payload.GetValueOrDefault("entries")?.ToString();

            bool shouldStepDown = false;
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
                    shouldStepDown = true;
                }
            }

            if (shouldStepDown)
            {
                await StepDownAsync(term);
            }

            lock (_stateLock)
            {
                _state = RaftState.Follower;
                _leaderId = leaderId;
                _lastHeartbeat = DateTime.UtcNow;
            }

            // Check log consistency
            if (prevLogIndex > 0)
            {
                var prevEntry = await GetLogEntryAsync(prevLogIndex);
                if (prevEntry == null || prevEntry.Term != prevLogTerm)
                {
                    var lastIndex = await GetLastLogIndexAsync();
                    return new Dictionary<string, object>
                    {
                        ["term"] = _currentTerm,
                        ["success"] = false,
                        ["conflictIndex"] = prevEntry == null ? lastIndex + 1 : prevLogIndex
                    };
                }
            }

            // Append entries
            if (!string.IsNullOrEmpty(entriesJson) && _logStore != null)
            {
                var entries = JsonSerializer.Deserialize<List<RaftLogEntry>>(entriesJson);
                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        var lastIndex = await _logStore.GetLastIndexAsync();
                        if (entry.Index <= lastIndex)
                        {
                            // Check for conflict
                            var existingEntry = await _logStore.GetEntryAsync(entry.Index);
                            if (existingEntry != null && existingEntry.Term != entry.Term)
                            {
                                // Overwrite conflicting entry
                                await _logStore.TruncateFromAsync(entry.Index);
                                await _logStore.AppendAsync(entry);
                            }
                        }
                        else
                        {
                            await _logStore.AppendAsync(entry);
                        }
                    }
                }
            }

            // Update commit index
            if (leaderCommit > _commitIndex)
            {
                var lastIndex = await GetLastLogIndexAsync();
                _commitIndex = Math.Min(leaderCommit, lastIndex);
            }

            var matchIndex = await GetLastLogIndexAsync();
            return new Dictionary<string, object>
            {
                ["term"] = _currentTerm,
                ["success"] = true,
                ["matchIndex"] = matchIndex
            };
        }

        #endregion

        #region Network

        private async Task StartListenerAsync()
        {
            try
            {
                // Use configured base port (0 = OS-assigned)
                _listener = new TcpListener(IPAddress.Any, _config.BasePort);
                _listener.Start();

                // Get the actual port assigned by OS if BasePort was 0
                var actualPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _nodeEndpoint = $"127.0.0.1:{actualPort}";

                Console.WriteLine($"[Raft] WARNING: Using 127.0.0.1 endpoint. Configure external address for production. Node: {_nodeId}");

                _ = AcceptConnectionsAsync(_cts!.Token);
            }
            catch (Exception ex)
            {
                // Listener failed, try next port in range if configured
                Console.WriteLine($"[Raft] TCP listener failed on port {_config.BasePort} - Node: {_nodeId}, Error: {ex.Message}");

                if (_config.BasePort > 0 && _config.PortRange > 0)
                {
                    // Try incrementing within the port range
                    var nextPort = _config.BasePort + 1;
                    if (nextPort < _config.BasePort + _config.PortRange)
                    {
                        _config = _config with { BasePort = nextPort };
                        await StartListenerAsync();
                    }
                    else
                    {
                        Console.WriteLine($"[Raft] Exhausted port range - Node: {_nodeId}, Range: [{_config.BasePort - _config.PortRange + 1}, {_config.BasePort}]");
                        throw;
                    }
                }
                else
                {
                    throw; // Re-throw if OS assignment failed or no range configured
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
                catch (Exception ex)
                {
                    // Accept failed, continue - expected during shutdown or temporary network issues
                    Console.WriteLine($"[Raft] TCP accept failed - Node: {_nodeId}, State: {_state}, Error: {ex.Message}");
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
            catch (Exception ex)
            {
                // Client handling failed - expected during network issues or malformed requests
                Console.WriteLine($"[Raft] Client request handling failed - Node: {_nodeId}, State: {_state}, Error: {ex.Message}");
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
            catch (Exception ex)
            {
                // Failed to send vote request - expected during network partitions
                Console.WriteLine($"[Raft] RequestVote RPC failed - Node: {_nodeId}, Peer: {peer.NodeId}, Endpoint: {peer.Endpoint}, Term: {request.Term}, Error: {ex.Message}");
                return null;
            }
        }

        private async Task<AppendEntriesResponse?> SendAppendEntriesAsync(RaftPeer peer, RaftLogEntry[] entries)
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
                var prevEntry = prevLogIndex > 0 ? await GetLogEntryAsync(prevLogIndex) : null;
                var prevLogTerm = prevEntry?.Term ?? 0;

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
            catch (Exception ex)
            {
                // Failed to send AppendEntries - expected during network partitions
                Console.WriteLine($"[Raft] AppendEntries RPC failed - Node: {_nodeId}, Peer: {peer.NodeId}, Endpoint: {peer.Endpoint}, EntryCount: {entries.Length}, Term: {_currentTerm}, Error: {ex.Message}");
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
            catch (Exception ex)
            {
                // Failed to forward proposal to leader
                Console.WriteLine($"[Raft] Proposal forwarding failed - Node: {_nodeId}, Leader: {leader.NodeId}, Endpoint: {leader.Endpoint}, ProposalId: {proposal.Id}, Error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helpers

        private async Task<long> GetLastLogIndexAsync()
        {
            if (_logStore == null) return 0;
            return await _logStore.GetLastIndexAsync();
        }

        private async Task<long> GetLastLogTermAsync()
        {
            if (_logStore == null) return 0;
            return await _logStore.GetLastTermAsync();
        }

        private async Task<RaftLogEntry?> GetLogEntryAsync(long index)
        {
            if (_logStore == null) return null;
            return await _logStore.GetEntryAsync(index);
        }

        private async Task<RaftLogEntry[]> GetEntriesFromIndexAsync(long startIndex)
        {
            if (_logStore == null) return Array.Empty<RaftLogEntry>();
            var entries = await _logStore.GetEntriesFromAsync(startIndex);
            return entries.ToArray();
        }

        private async Task CreateSnapshotAsync()
        {
            // Snapshot creation for log compaction
            // Captures the current state machine state and allows truncating the log

            if (_logStore == null) return;

            var logLength = await _logStore.GetLastIndexAsync();
            if (logLength <= _snapshotThreshold / 2)
            {
                // Not enough entries to compact
                return;
            }

            // Capture current state
            var lastIncludedIndex = _commitIndex;
            var committedEntry = await GetLogEntryAsync(lastIncludedIndex);
            var lastIncludedTerm = committedEntry?.Term ?? _currentTerm;

            // Serialize the committed state
            var stateData = SerializeCommittedState();

            var snapshot = new Snapshot
            {
                LastIncludedIndex = lastIncludedIndex,
                LastIncludedTerm = lastIncludedTerm,
                Data = stateData,
                CreatedAt = DateTime.UtcNow,
                NodeId = _nodeId
            };

            // Compact log - remove entries up to snapshot
            var entriesCompacted = (int)lastIncludedIndex;
            if (entriesCompacted > 0)
            {
                await _logStore.CompactAsync(entriesCompacted);
            }

            // Persist snapshot to storage
            await PersistSnapshotAsync(snapshot);

            Console.WriteLine($"[Raft] Snapshot created: index={snapshot.LastIncludedIndex}, term={snapshot.LastIncludedTerm}, compacted={entriesCompacted} entries");
        }

        private byte[] SerializeCommittedState()
        {
            // Serialize the current committed state for snapshot
            var state = new Dictionary<string, object>
            {
                ["commitIndex"] = _commitIndex,
                ["lastApplied"] = _lastApplied,
                ["currentTerm"] = _currentTerm,
                ["locks"] = _locks.ToDictionary(kv => kv.Key, kv => new
                {
                    kv.Value.LockId,
                    OwnerId = kv.Value.Owner,
                    kv.Value.AcquiredAt,
                    LeaseExpires = kv.Value.ExpiresAt
                }),
                ["nodeId"] = _nodeId,
                ["votedFor"] = _votedFor ?? ""
            };

            return JsonSerializer.SerializeToUtf8Bytes(state);
        }

        private async Task PersistSnapshotAsync(Snapshot snapshot)
        {
            // Persist snapshot to file system or storage provider
            var snapshotPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "Raft", $"snapshot-{_nodeId}");

            Directory.CreateDirectory(snapshotPath);

            var snapshotFile = Path.Combine(snapshotPath, $"snapshot-{snapshot.LastIncludedIndex}.json");
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(snapshotFile, json);

            // Keep only the latest 3 snapshots
            var snapshots = Directory.GetFiles(snapshotPath, "snapshot-*.json")
                .OrderByDescending(f => f)
                .Skip(3)
                .ToList();

            foreach (var oldSnapshot in snapshots)
            {
                try
                {
                    File.Delete(oldSnapshot);
                }
                catch (Exception ex)
                {
                    // Ignore cleanup errors - non-critical, will retry on next snapshot
                    Console.WriteLine($"[Raft] Snapshot cleanup failed - Node: {_nodeId}, File: {Path.GetFileName(oldSnapshot)}, Error: {ex.Message}");
                }
            }
        }

        private async Task<Snapshot?> LoadLatestSnapshotAsync()
        {
            var snapshotPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "Raft", $"snapshot-{_nodeId}");

            if (!Directory.Exists(snapshotPath))
                return null;

            var latestFile = Directory.GetFiles(snapshotPath, "snapshot-*.json")
                .OrderByDescending(f => f)
                .FirstOrDefault();

            if (latestFile == null)
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(latestFile);
                return JsonSerializer.Deserialize<Snapshot>(json);
            }
            catch (Exception ex)
            {
                // Failed to load snapshot - will start from empty state
                Console.WriteLine($"[Raft] Snapshot load failed - Node: {_nodeId}, File: {Path.GetFileName(latestFile)}, Error: {ex.Message}");
                return null;
            }
        }

        private sealed class Snapshot
        {
            public long LastIncludedIndex { get; init; }
            public long LastIncludedTerm { get; init; }
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public DateTime CreatedAt { get; init; }
            public string NodeId { get; init; } = "";
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

            if (payload.TryGetValue("basePort", out var basePort))
            {
                var port = Convert.ToInt32(basePort);
                _config = _config with { BasePort = port };
            }

            if (payload.TryGetValue("portRange", out var portRange))
            {
                var range = Convert.ToInt32(portRange);
                _config = _config with { PortRange = range };
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["electionTimeoutMinMs"] = _electionTimeoutMin.TotalMilliseconds,
                ["electionTimeoutMaxMs"] = _electionTimeoutMax.TotalMilliseconds,
                ["heartbeatIntervalMs"] = _heartbeatInterval.TotalMilliseconds,
                ["lockLeaseSeconds"] = _lockLeaseTime.TotalSeconds,
                ["basePort"] = _config.BasePort,
                ["portRange"] = _config.PortRange
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

    /// <summary>
    /// Configuration for Raft consensus plugin.
    /// </summary>
    public record RaftConfiguration
    {
        /// <summary>
        /// Base port for Raft communication. Default 0 = OS-assigned port (recommended for safety).
        /// Set to a specific port number for production deployments where fixed ports are required.
        /// </summary>
        public int BasePort { get; init; } = 0;

        /// <summary>
        /// Port range size for multi-node clusters.
        /// If BasePort is specified and port binding fails, the plugin will try ports in the range [BasePort, BasePort+PortRange).
        /// Default 100. Ignored when BasePort is 0 (OS-assigned).
        /// </summary>
        public int PortRange { get; init; } = 100;

        /// <summary>
        /// Node endpoints for cluster membership. Format: "host:port".
        /// Example: ["node1.example.com:5000", "node2.example.com:5000"]
        /// </summary>
        public List<string> ClusterEndpoints { get; init; } = new();
    }

    #endregion
}
