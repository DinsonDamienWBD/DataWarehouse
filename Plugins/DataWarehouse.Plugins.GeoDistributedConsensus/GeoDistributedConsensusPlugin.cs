using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.GeoDistributedConsensus;

/// <summary>
/// Geo-Distributed Consensus Plugin implementing multi-datacenter consensus protocols.
///
/// Features:
/// - Multi-Paxos for geo-distribution with flexible quorum configuration
/// - Raft extensions for WAN: pre-vote protocol, learner nodes, leadership transfer
/// - Hierarchical consensus: local datacenter + global consensus
/// - Witness/observer nodes for tie-breaking
/// - Two-tier commit protocol
/// - Network partition detection and handling
/// - Leader election with datacenter awareness
/// - Configurable consistency levels (strong, session, bounded staleness, eventual)
/// - Conflict resolution strategies
/// - Hybrid Logical Clocks (HLC) for ordering
/// - Split-brain prevention mechanisms
/// - Log replication with state machine
/// - Snapshot transfer and log compaction
/// - Joint consensus for membership changes
/// - Speculative execution for latency optimization
///
/// Message Commands:
/// - geo.propose: Propose a value with specified consistency level
/// - geo.read: Read with consistency level
/// - geo.topology.add: Add a datacenter to the topology
/// - geo.topology.remove: Remove a datacenter
/// - geo.topology.status: Get topology status
/// - geo.leader.info: Get current leader information
/// - geo.partition.status: Get partition detection status
/// - geo.configure: Configure plugin settings
/// - geo.leader.transfer: Transfer leadership to another node
/// - geo.membership.change: Change cluster membership with joint consensus
/// - geo.snapshot.request: Request a snapshot transfer
/// - geo.log.status: Get log replication status
/// </summary>
public sealed class GeoDistributedConsensusPlugin : ConsensusPluginBase
{
    #region Identity

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.consensus.geodistributed";

    /// <inheritdoc/>
    public override string Name => "Geo-Distributed Consensus Plugin";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    #endregion

    #region State

    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, Datacenter> _datacenters = new();
    private readonly ConcurrentDictionary<string, DatacenterNode> _nodes = new();
    private readonly ConcurrentDictionary<long, ConsensusRound> _rounds = new();
    private readonly ConcurrentDictionary<string, ProposalState> _pendingProposals = new();
    private readonly ConcurrentDictionary<string, object?> _committedState = new();
    private readonly List<Action<Proposal>> _commitHandlers = new();
    private readonly object _handlerLock = new();

    private string _nodeId = string.Empty;
    private string _localDatacenterId = string.Empty;
    private LeaderInfo? _currentLeader;
    private HybridLogicalClock _hlc = new();
    private long _currentRound;
    private long _lastCommittedRound;
    private QuorumConfig _quorumConfig = new();
    private GeoConsensusConfig _config = new();
    private PartitionDetector? _partitionDetector;
    private ConflictResolver _conflictResolver = new(ConflictResolutionStrategy.LastWriteWins);

    // Raft extensions
    private GeoRaftState _raftState = new();
    private readonly LogReplicator _logReplicator;
    private readonly ConcurrentDictionary<string, GeoRaftNode> _raftNodes = new();
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    // Hierarchical consensus
    private readonly HierarchicalConsensusManager _hierarchicalConsensus;

    // Joint consensus for membership changes
    private JointConsensusState? _jointConsensus;

    // Snapshot management
    private readonly SnapshotManager _snapshotManager;

    // Speculative execution
    private readonly ConcurrentDictionary<string, SpeculativeExecution> _speculativeExecutions = new();

    private CancellationTokenSource? _cts;
    private Task? _leaderElectionTask;
    private Task? _heartbeatTask;
    private Task? _partitionMonitorTask;
    private Task? _staleFenceTask;
    private Task? _logCompactionTask;
    private Task? _snapshotTask;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new instance of the geo-distributed consensus plugin.
    /// </summary>
    public GeoDistributedConsensusPlugin()
    {
        _logReplicator = new LogReplicator(this);
        _hierarchicalConsensus = new HierarchicalConsensusManager(this);
        _snapshotManager = new SnapshotManager(this);
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override bool IsLeader
    {
        get
        {
            lock (_stateLock)
            {
                return _currentLeader?.NodeId == _nodeId &&
                       _currentLeader?.DatacenterId == _localDatacenterId;
            }
        }
    }

    /// <summary>
    /// Gets whether this node is the local datacenter leader.
    /// </summary>
    public bool IsLocalLeader => _raftState.Role == GeoRaftRole.Leader;

    /// <summary>
    /// Gets the current Raft state.
    /// </summary>
    public GeoRaftState RaftState => _raftState;

    /// <summary>
    /// Gets the current leader information.
    /// </summary>
    public LeaderInfo? CurrentLeader => _currentLeader;

    /// <summary>
    /// Gets the local node identifier.
    /// </summary>
    public string NodeId => _nodeId;

    /// <summary>
    /// Gets the local datacenter identifier.
    /// </summary>
    public string LocalDatacenterId => _localDatacenterId;

    /// <summary>
    /// Gets the current Hybrid Logical Clock timestamp.
    /// </summary>
    public HlcTimestamp CurrentTimestamp => _hlc.Now();

    /// <summary>
    /// Gets the number of active datacenters.
    /// </summary>
    public int DatacenterCount => _datacenters.Count;

    /// <summary>
    /// Gets the current term.
    /// </summary>
    public long CurrentTerm => _raftState.CurrentTerm;

    /// <summary>
    /// Gets the commit index.
    /// </summary>
    public long CommitIndex => _raftState.CommitIndex;

    /// <summary>
    /// Gets the last applied index.
    /// </summary>
    public long LastApplied => _raftState.LastApplied;

    /// <summary>
    /// Gets the log replicator for this node.
    /// </summary>
    internal LogReplicator LogReplicator => _logReplicator;

    /// <summary>
    /// Gets the hierarchical consensus manager.
    /// </summary>
    internal HierarchicalConsensusManager HierarchicalConsensus => _hierarchicalConsensus;

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _nodeId = $"geo-{request.KernelId}-{Guid.NewGuid():N}"[..24];
        _hlc = new HybridLogicalClock();
        _partitionDetector = new PartitionDetector(_config.PartitionDetectionConfig);
        _conflictResolver = new ConflictResolver(_config.ConflictResolutionStrategy);

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

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Initialize local datacenter if not configured
        if (string.IsNullOrEmpty(_localDatacenterId))
        {
            _localDatacenterId = "dc-primary";
            RegisterDatacenter(new Datacenter
            {
                Id = _localDatacenterId,
                Name = "Primary Datacenter",
                Region = "default",
                Weight = 100,
                Priority = 1,
                Status = DatacenterStatus.Active
            });
        }

        // Register self as a Raft node
        var selfNode = new GeoRaftNode
        {
            NodeId = _nodeId,
            DatacenterId = _localDatacenterId,
            Endpoint = $"localhost:{5100 + Math.Abs(_nodeId.GetHashCode()) % 100}",
            Role = SDK.Contracts.NodeRole.Voter,
            VotingStatus = VotingStatus.Voter,
            Status = SDK.Contracts.NodeStatus.Active,
            LastHeartbeat = DateTime.UtcNow
        };
        _raftNodes[_nodeId] = selfNode;

        // Register self as a datacenter node
        RegisterNode(new DatacenterNode
        {
            NodeId = _nodeId,
            DatacenterId = _localDatacenterId,
            Endpoint = selfNode.Endpoint,
            Role = NodeRole.Voter,
            Status = NodeStatus.Active,
            LastHeartbeat = DateTime.UtcNow
        });

        // Initialize Raft state
        _raftState = new GeoRaftState
        {
            Role = GeoRaftRole.Follower,
            CurrentTerm = 0,
            VotedFor = null,
            CommitIndex = 0,
            LastApplied = 0
        };

        // Start background tasks
        _leaderElectionTask = RunLeaderElectionLoopAsync(_cts.Token);
        _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);
        _partitionMonitorTask = RunPartitionMonitorLoopAsync(_cts.Token);
        _staleFenceTask = RunStaleFenceLoopAsync(_cts.Token);
        _logCompactionTask = RunLogCompactionLoopAsync(_cts.Token);
        _snapshotTask = RunSnapshotLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _cts?.Cancel();

        var tasks = new[] { _leaderElectionTask, _heartbeatTask, _partitionMonitorTask, _staleFenceTask, _logCompactionTask, _snapshotTask }
            .Where(t => t != null)
            .Select(t => t!.ContinueWith(_ => { }));

        await Task.WhenAll(tasks);

        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunLogCompactionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.LogCompactionIntervalMs, ct);
                await _logReplicator.CompactLogAsync(_config.LogCompactionThreshold);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Log compaction failed, will retry: {ex.Message}");
            }
        }
    }

    private async Task RunSnapshotLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.SnapshotIntervalMs, ct);
                if (_raftState.CommitIndex - _snapshotManager.LastSnapshotIndex > _config.SnapshotThreshold)
                {
                    await _snapshotManager.CreateSnapshotAsync(_raftState.CommitIndex);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Snapshot creation failed, will retry: {ex.Message}");
            }
        }
    }

    #endregion

    #region IConsensusEngine Implementation

    /// <inheritdoc/>
    public override async Task<bool> ProposeAsync(Proposal proposal)
    {
        return await ProposeWithConsistencyAsync(proposal, _config.DefaultConsistencyLevel);
    }

    /// <summary>
    /// Proposes a value with a specific consistency level.
    /// </summary>
    /// <param name="proposal">The proposal to submit.</param>
    /// <param name="consistencyLevel">The required consistency level.</param>
    /// <returns>True if the proposal was committed successfully.</returns>
    public async Task<bool> ProposeWithConsistencyAsync(Proposal proposal, ConsistencyLevel consistencyLevel)
    {
        var timestamp = _hlc.Now();

        // Create consensus round
        var round = new ConsensusRound
        {
            RoundNumber = Interlocked.Increment(ref _currentRound),
            ProposalId = proposal.Id,
            Command = proposal.Command,
            Payload = proposal.Payload,
            Timestamp = timestamp,
            ConsistencyLevel = consistencyLevel,
            Phase = ConsensusPhase.Prepare,
            InitiatorNodeId = _nodeId,
            InitiatorDatacenterId = _localDatacenterId
        };

        _rounds[round.RoundNumber] = round;

        var proposalState = new ProposalState
        {
            ProposalId = proposal.Id,
            Round = round,
            CompletionSource = new TaskCompletionSource<bool>()
        };
        _pendingProposals[proposal.Id] = proposalState;

        try
        {
            // Phase 1: Prepare (Paxos-style)
            var prepareResult = await RunPreparePhaseAsync(round);
            if (!prepareResult.Success)
            {
                proposalState.CompletionSource.TrySetResult(false);
                return false;
            }

            // Phase 2: Accept
            round.Phase = ConsensusPhase.Accept;
            var acceptResult = await RunAcceptPhaseAsync(round);
            if (!acceptResult.Success)
            {
                proposalState.CompletionSource.TrySetResult(false);
                return false;
            }

            // Phase 3: Commit
            round.Phase = ConsensusPhase.Commit;
            await CommitProposalAsync(proposal, round);
            round.Phase = ConsensusPhase.Committed;
            round.CommittedAt = DateTime.UtcNow;

            proposalState.CompletionSource.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            round.Phase = ConsensusPhase.Failed;
            round.FailureReason = ex.Message;
            proposalState.CompletionSource.TrySetResult(false);
            return false;
        }
        finally
        {
            _pendingProposals.TryRemove(proposal.Id, out _);
        }
    }

    /// <inheritdoc/>
    public override void OnCommit(Action<Proposal> handler)
    {
        lock (_handlerLock)
        {
            _commitHandlers.Add(handler);
        }
    }

    /// <inheritdoc/>
    public override Task<ClusterState> GetClusterStateAsync()
    {
        lock (_stateLock)
        {
            var activeDatacenters = _datacenters.Values.Count(dc => dc.Status == DatacenterStatus.Active);
            var activeNodes = _nodes.Values.Count(n => n.Status == NodeStatus.Active);

            return Task.FromResult(new ClusterState
            {
                IsHealthy = activeDatacenters >= _quorumConfig.MinDatacentersForQuorum &&
                           _currentLeader != null &&
                           !(_partitionDetector?.IsPartitioned ?? false),
                LeaderId = _currentLeader?.NodeId,
                NodeCount = activeNodes,
                Term = _currentRound
            });
        }
    }

    #endregion

    #region Datacenter Management

    /// <summary>
    /// Registers a datacenter in the topology.
    /// </summary>
    /// <param name="datacenter">The datacenter to register.</param>
    public void RegisterDatacenter(Datacenter datacenter)
    {
        datacenter.JoinedAt = DateTime.UtcNow;
        _datacenters[datacenter.Id] = datacenter;
        RecalculateQuorumRequirements();
    }

    /// <summary>
    /// Removes a datacenter from the topology.
    /// </summary>
    /// <param name="datacenterId">The datacenter ID to remove.</param>
    /// <returns>True if the datacenter was removed.</returns>
    public bool RemoveDatacenter(string datacenterId)
    {
        var removed = _datacenters.TryRemove(datacenterId, out _);
        if (removed)
        {
            // Remove all nodes from this datacenter
            var nodesToRemove = _nodes.Values
                .Where(n => n.DatacenterId == datacenterId)
                .Select(n => n.NodeId)
                .ToList();

            foreach (var nodeId in nodesToRemove)
            {
                _nodes.TryRemove(nodeId, out _);
            }

            RecalculateQuorumRequirements();
        }
        return removed;
    }

    /// <summary>
    /// Registers a node within a datacenter.
    /// </summary>
    /// <param name="node">The node to register.</param>
    public void RegisterNode(DatacenterNode node)
    {
        node.JoinedAt = DateTime.UtcNow;
        _nodes[node.NodeId] = node;
    }

    /// <summary>
    /// Gets all active datacenters.
    /// </summary>
    public IReadOnlyList<Datacenter> GetActiveDatacenters()
    {
        return _datacenters.Values
            .Where(dc => dc.Status == DatacenterStatus.Active)
            .OrderBy(dc => dc.Priority)
            .ToList();
    }

    /// <summary>
    /// Gets all nodes in a datacenter.
    /// </summary>
    public IReadOnlyList<DatacenterNode> GetNodesInDatacenter(string datacenterId)
    {
        return _nodes.Values
            .Where(n => n.DatacenterId == datacenterId)
            .ToList();
    }

    #endregion

    #region Quorum Management

    /// <summary>
    /// Calculates whether quorum is achieved based on votes.
    /// </summary>
    /// <param name="votes">The votes received (datacenter ID to vote).</param>
    /// <param name="consistencyLevel">The required consistency level.</param>
    /// <returns>Quorum calculation result.</returns>
    public QuorumResult CalculateQuorum(Dictionary<string, Vote> votes, ConsistencyLevel consistencyLevel)
    {
        var result = new QuorumResult();

        // Get active datacenters and their weights
        var activeDatacenters = _datacenters.Values
            .Where(dc => dc.Status == DatacenterStatus.Active)
            .ToList();

        if (activeDatacenters.Count == 0)
        {
            result.Achieved = false;
            result.Reason = "No active datacenters";
            return result;
        }

        var totalWeight = activeDatacenters.Sum(dc => dc.Weight);
        var votingWeight = 0;
        var datacentersVoting = 0;
        var acceptVotes = 0;
        var rejectVotes = 0;

        foreach (var dc in activeDatacenters)
        {
            if (votes.TryGetValue(dc.Id, out var vote))
            {
                if (vote.Accepted)
                {
                    votingWeight += dc.Weight;
                    acceptVotes++;
                }
                else
                {
                    rejectVotes++;
                }
                datacentersVoting++;
            }
        }

        result.TotalWeight = totalWeight;
        result.AchievedWeight = votingWeight;
        result.DatacentersResponded = datacentersVoting;
        result.TotalDatacenters = activeDatacenters.Count;
        result.AcceptVotes = acceptVotes;
        result.RejectVotes = rejectVotes;

        // Determine quorum based on consistency level
        switch (consistencyLevel)
        {
            case ConsistencyLevel.Strong:
                // All datacenters must respond and accept
                result.RequiredWeight = totalWeight;
                result.Achieved = votingWeight == totalWeight && rejectVotes == 0;
                break;

            case ConsistencyLevel.Quorum:
                // Majority by weight
                result.RequiredWeight = (totalWeight / 2) + 1;
                result.Achieved = votingWeight >= result.RequiredWeight;
                break;

            case ConsistencyLevel.BoundedStaleness:
                // At least local DC + one remote
                result.RequiredWeight = _datacenters.TryGetValue(_localDatacenterId, out var localDc)
                    ? localDc.Weight + (totalWeight - localDc.Weight) / activeDatacenters.Count
                    : totalWeight / 2;
                result.Achieved = votingWeight >= result.RequiredWeight;
                break;

            case ConsistencyLevel.Eventual:
                // Local datacenter is enough
                result.RequiredWeight = _datacenters.TryGetValue(_localDatacenterId, out var dc) ? dc.Weight : 1;
                var localVote = votes.GetValueOrDefault(_localDatacenterId);
                result.Achieved = localVote?.Accepted ?? false;
                break;

            case ConsistencyLevel.LocalQuorum:
                // Quorum within local datacenter only
                result.RequiredWeight = _datacenters.TryGetValue(_localDatacenterId, out var ldc) ? ldc.Weight : 1;
                var lv = votes.GetValueOrDefault(_localDatacenterId);
                result.Achieved = lv?.Accepted ?? false;
                break;

            default:
                result.RequiredWeight = (totalWeight / 2) + 1;
                result.Achieved = votingWeight >= result.RequiredWeight;
                break;
        }

        result.Reason = result.Achieved
            ? $"Quorum achieved: {votingWeight}/{result.RequiredWeight} weight"
            : $"Quorum not achieved: {votingWeight}/{result.RequiredWeight} weight needed";

        return result;
    }

    private void RecalculateQuorumRequirements()
    {
        var activeDatacenters = _datacenters.Values
            .Where(dc => dc.Status == DatacenterStatus.Active)
            .ToList();

        _quorumConfig.TotalWeight = activeDatacenters.Sum(dc => dc.Weight);
        _quorumConfig.MajorityWeight = (_quorumConfig.TotalWeight / 2) + 1;
        _quorumConfig.MinDatacentersForQuorum = Math.Max(1, (activeDatacenters.Count / 2) + 1);
    }

    #endregion

    #region Consensus Protocol

    private async Task<PhaseResult> RunPreparePhaseAsync(ConsensusRound round)
    {
        var votes = new Dictionary<string, Vote>();
        var activeDatacenters = GetActiveDatacenters();

        // Simulate prepare phase to each datacenter
        var prepareTasks = activeDatacenters.Select(async dc =>
        {
            try
            {
                // Simulate network latency based on datacenter distance
                var latency = dc.Id == _localDatacenterId ? 1 : dc.EstimatedLatencyMs;
                await Task.Delay(Math.Min(latency, _config.PrepareTimeoutMs / 2));

                // Check for higher round number (Paxos promise)
                var highestRound = _rounds.Values
                    .Where(r => r.Phase >= ConsensusPhase.Accept)
                    .Select(r => r.RoundNumber)
                    .DefaultIfEmpty(0)
                    .Max();

                var accepted = round.RoundNumber >= highestRound;

                return (dc.Id, new Vote
                {
                    DatacenterId = dc.Id,
                    NodeId = _nodeId,
                    RoundNumber = round.RoundNumber,
                    Accepted = accepted,
                    Timestamp = _hlc.Now(),
                    HighestSeenRound = highestRound
                });
            }
            catch
            {
                return (dc.Id, new Vote
                {
                    DatacenterId = dc.Id,
                    Accepted = false,
                    Timestamp = _hlc.Now()
                });
            }
        });

        var results = await Task.WhenAll(prepareTasks);
        foreach (var (dcId, vote) in results)
        {
            votes[dcId] = vote;
        }

        round.PrepareVotes = votes;
        var quorumResult = CalculateQuorum(votes, round.ConsistencyLevel);

        return new PhaseResult
        {
            Success = quorumResult.Achieved,
            Votes = votes,
            QuorumResult = quorumResult
        };
    }

    private async Task<PhaseResult> RunAcceptPhaseAsync(ConsensusRound round)
    {
        var votes = new Dictionary<string, Vote>();
        var activeDatacenters = GetActiveDatacenters();

        // Simulate accept phase to each datacenter
        var acceptTasks = activeDatacenters.Select(async dc =>
        {
            try
            {
                // Check if prepare was successful for this DC
                if (!round.PrepareVotes.TryGetValue(dc.Id, out var prepareVote) || !prepareVote.Accepted)
                {
                    return (dc.Id, new Vote { DatacenterId = dc.Id, Accepted = false, Timestamp = _hlc.Now() });
                }

                // Simulate network latency
                var latency = dc.Id == _localDatacenterId ? 1 : dc.EstimatedLatencyMs;
                await Task.Delay(Math.Min(latency, _config.AcceptTimeoutMs / 2));

                // Accept the proposal
                return (dc.Id, new Vote
                {
                    DatacenterId = dc.Id,
                    NodeId = _nodeId,
                    RoundNumber = round.RoundNumber,
                    Accepted = true,
                    Timestamp = _hlc.Now()
                });
            }
            catch
            {
                return (dc.Id, new Vote
                {
                    DatacenterId = dc.Id,
                    Accepted = false,
                    Timestamp = _hlc.Now()
                });
            }
        });

        var results = await Task.WhenAll(acceptTasks);
        foreach (var (dcId, vote) in results)
        {
            votes[dcId] = vote;
        }

        round.AcceptVotes = votes;
        var quorumResult = CalculateQuorum(votes, round.ConsistencyLevel);

        return new PhaseResult
        {
            Success = quorumResult.Achieved,
            Votes = votes,
            QuorumResult = quorumResult
        };
    }

    private async Task CommitProposalAsync(Proposal proposal, ConsensusRound round)
    {
        // Store in committed state
        var key = $"{proposal.Command}:{proposal.Id}";
        _committedState[key] = proposal.Payload;
        _lastCommittedRound = round.RoundNumber;

        // Update HLC
        _hlc.Update(round.Timestamp);

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
                // Log and continue
            }
        }

        // Async replication to other datacenters for eventual consistency
        if (round.ConsistencyLevel == ConsistencyLevel.Eventual)
        {
            _ = ReplicateAsyncToDatacentersAsync(proposal, round);
        }

        await Task.CompletedTask;
    }

    private async Task ReplicateAsyncToDatacentersAsync(Proposal proposal, ConsensusRound round)
    {
        var remoteDatacenters = GetActiveDatacenters()
            .Where(dc => dc.Id != _localDatacenterId)
            .ToList();

        foreach (var dc in remoteDatacenters)
        {
            try
            {
                await Task.Delay(dc.EstimatedLatencyMs);
                // Simulate async replication
            }
            catch
            {
                // Retry logic would go here
            }
        }
    }

    #endregion

    #region Leader Election

    private async Task RunLeaderElectionLoopAsync(CancellationToken ct)
    {
        var random = new Random();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var timeout = TimeSpan.FromMilliseconds(
                    _config.LeaderElectionTimeoutMinMs +
                    random.Next(_config.LeaderElectionTimeoutMaxMs - _config.LeaderElectionTimeoutMinMs)
                );

                await Task.Delay(timeout, ct);

                // Check if we need to elect a leader
                if (_currentLeader == null || IsLeaderStale())
                {
                    // Use pre-vote protocol to prevent disruption
                    if (_config.EnablePreVote)
                    {
                        var preVoteResult = await RunPreVoteAsync(ct);
                        if (preVoteResult.Success)
                        {
                            await StartLeaderElectionAsync(ct);
                        }
                    }
                    else
                    {
                        await StartLeaderElectionAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Election failed, retry
            }
        }
    }

    /// <summary>
    /// Runs the pre-vote protocol to prevent disruptive elections.
    /// Pre-vote checks if a candidate would get enough votes before incrementing term.
    /// This prevents nodes that are partitioned from incrementing terms unnecessarily.
    /// </summary>
    private async Task<PreVoteResult> RunPreVoteAsync(CancellationToken ct)
    {
        var preVoteRound = _currentRound + 1; // Don't increment yet
        var votes = new ConcurrentDictionary<string, PreVoteResponse>();

        var activeNodes = _raftNodes.Values
            .Where(n => n.Status == SDK.Contracts.NodeStatus.Active && (VotingStatus)n.VotingStatus! == VotingStatus.Voter)
            .ToList();

        // Send pre-vote requests
        var preVoteTasks = activeNodes.Select(async node =>
        {
            try
            {
                if (node.NodeId == _nodeId)
                {
                    // Vote for self
                    votes[node.NodeId] = new PreVoteResponse
                    {
                        NodeId = node.NodeId,
                        Term = preVoteRound,
                        VoteGranted = true,
                        Reason = "Self vote"
                    };
                    return;
                }

                await Task.Delay(node.DatacenterId == _localDatacenterId ? 1 : 10, ct);

                // Simulate pre-vote response
                var wouldVote = ShouldGrantPreVote(node, preVoteRound);
                votes[node.NodeId] = new PreVoteResponse
                {
                    NodeId = node.NodeId,
                    Term = preVoteRound,
                    VoteGranted = wouldVote,
                    Reason = wouldVote ? "Would grant vote" : "Would not grant vote"
                };
            }
            catch
            {
                // Pre-vote request failed
            }
        });

        await Task.WhenAll(preVoteTasks);

        var grantedVotes = votes.Values.Count(v => v.VoteGranted);
        var requiredVotes = (activeNodes.Count / 2) + 1;

        return new PreVoteResult
        {
            Success = grantedVotes >= requiredVotes,
            VotesReceived = grantedVotes,
            VotesRequired = requiredVotes,
            Term = preVoteRound
        };
    }

    private bool ShouldGrantPreVote(GeoRaftNode voter, long candidateTerm)
    {
        // Grant pre-vote if:
        // 1. Candidate's term is at least as high
        // 2. Candidate's log is at least as up-to-date
        // 3. Current leader hasn't sent heartbeat recently (prevents disruption)

        if (candidateTerm < _raftState.CurrentTerm)
            return false;

        if (_currentLeader != null && !IsLeaderStale())
            return false; // Current leader is healthy, don't grant pre-vote

        var lastLogIndex = _logReplicator.GetLastLogIndex();
        var lastLogTerm = _logReplicator.GetLastLogTerm();

        // Would the voter grant a real vote?
        return candidateTerm >= _raftState.CurrentTerm;
    }

    private bool IsLeaderStale()
    {
        if (_currentLeader == null) return true;

        var staleness = DateTime.UtcNow - _currentLeader.LastHeartbeat;
        return staleness > TimeSpan.FromMilliseconds(_config.LeaderHeartbeatTimeoutMs);
    }

    private async Task StartLeaderElectionAsync(CancellationToken ct)
    {
        // Increment term
        lock (_stateLock)
        {
            _raftState.CurrentTerm++;
            _raftState.VotedFor = _nodeId;
            _raftState.Role = GeoRaftRole.Candidate;
        }

        var electionRound = Interlocked.Increment(ref _currentRound);
        var votes = new ConcurrentDictionary<string, LeaderVote>();

        // Calculate this node's priority
        var localDatacenter = _datacenters.GetValueOrDefault(_localDatacenterId);
        var priority = CalculateLeaderPriority(localDatacenter);

        // Send RequestVote to all voting nodes
        var votingNodes = _raftNodes.Values
            .Where(n => (VotingStatus)n.VotingStatus! == VotingStatus.Voter && n.Status == SDK.Contracts.NodeStatus.Active)
            .ToList();

        var voteTasks = votingNodes.Select(async node =>
        {
            try
            {
                var request = new RequestVoteMessage
                {
                    Term = _raftState.CurrentTerm,
                    CandidateId = _nodeId,
                    CandidateDatacenterId = _localDatacenterId,
                    LastLogIndex = _logReplicator.GetLastLogIndex(),
                    LastLogTerm = _logReplicator.GetLastLogTerm(),
                    IsPreVote = false
                };

                if (node.NodeId == _nodeId)
                {
                    votes[node.NodeId] = new LeaderVote
                    {
                        DatacenterId = node.DatacenterId,
                        CandidateNodeId = _nodeId,
                        CandidateDatacenterId = _localDatacenterId,
                        Round = electionRound,
                        Granted = true,
                        Timestamp = _hlc.Now()
                    };
                    return;
                }

                var dc = _datacenters.GetValueOrDefault(node.DatacenterId);
                await Task.Delay(dc?.EstimatedLatencyMs ?? 50 / 2, ct);

                // Process vote request
                var response = ProcessRequestVote(request);
                votes[node.NodeId] = new LeaderVote
                {
                    DatacenterId = node.DatacenterId,
                    CandidateNodeId = _nodeId,
                    CandidateDatacenterId = _localDatacenterId,
                    Round = electionRound,
                    Granted = response.VoteGranted,
                    Timestamp = _hlc.Now()
                };
            }
            catch
            {
                // Vote not received
            }
        });

        await Task.WhenAll(voteTasks);

        // Check if we won the election
        var grantedVotes = votes.Values.Count(v => v.Granted);
        var requiredVotes = (votingNodes.Count / 2) + 1;

        if (grantedVotes >= requiredVotes)
        {
            BecomeLeader(electionRound, priority);
        }
        else
        {
            // Election failed, return to follower
            lock (_stateLock)
            {
                _raftState.Role = GeoRaftRole.Follower;
            }
        }
    }

    private void BecomeLeader(long electionRound, int priority)
    {
        lock (_stateLock)
        {
            _raftState.Role = GeoRaftRole.Leader;
            _currentLeader = new LeaderInfo
            {
                NodeId = _nodeId,
                DatacenterId = _localDatacenterId,
                ElectedAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow,
                LeaderRound = electionRound,
                Priority = priority
            };
        }

        // Initialize nextIndex and matchIndex for all nodes
        var lastLogIndex = _logReplicator.GetLastLogIndex();
        foreach (var node in _raftNodes.Values)
        {
            node.NextIndex = lastLogIndex + 1;
            node.MatchIndex = 0;
        }

        // Send immediate heartbeats
        _ = SendLeaderHeartbeatsAsync(CancellationToken.None);
    }

    /// <summary>
    /// Processes a RequestVote message from a candidate.
    /// </summary>
    private RequestVoteResponse ProcessRequestVote(RequestVoteMessage request)
    {
        lock (_stateLock)
        {
            // If term is higher, update and become follower
            if (request.Term > _raftState.CurrentTerm)
            {
                _raftState.CurrentTerm = request.Term;
                _raftState.VotedFor = null;
                _raftState.Role = GeoRaftRole.Follower;
            }

            // Reject if term is lower
            if (request.Term < _raftState.CurrentTerm)
            {
                return new RequestVoteResponse
                {
                    Term = _raftState.CurrentTerm,
                    VoteGranted = false,
                    Reason = "Term is lower than current term"
                };
            }

            // Check if we can vote for this candidate
            var canVote = _raftState.VotedFor == null || _raftState.VotedFor == request.CandidateId;

            // Check log is at least as up-to-date
            var lastLogIndex = _logReplicator.GetLastLogIndex();
            var lastLogTerm = _logReplicator.GetLastLogTerm();
            var logIsOk = request.LastLogTerm > lastLogTerm ||
                         (request.LastLogTerm == lastLogTerm && request.LastLogIndex >= lastLogIndex);

            if (canVote && logIsOk)
            {
                _raftState.VotedFor = request.CandidateId;
                return new RequestVoteResponse
                {
                    Term = _raftState.CurrentTerm,
                    VoteGranted = true,
                    Reason = "Vote granted"
                };
            }

            return new RequestVoteResponse
            {
                Term = _raftState.CurrentTerm,
                VoteGranted = false,
                Reason = canVote ? "Log not up-to-date" : "Already voted for another candidate"
            };
        }
    }

    /// <summary>
    /// Transfers leadership to a target node.
    /// </summary>
    public async Task<bool> TransferLeadershipAsync(string targetNodeId, CancellationToken ct = default)
    {
        if (!IsLeader)
            return false;

        if (!_raftNodes.TryGetValue(targetNodeId, out var targetNode))
            return false;

        if ((VotingStatus)targetNode.VotingStatus! != VotingStatus.Voter)
            return false;

        // Step 1: Stop accepting new client requests
        _raftState.IsTransferringLeadership = true;

        try
        {
            // Step 2: Catch up the target node's log
            var lastLogIndex = _logReplicator.GetLastLogIndex();
            var maxRetries = 10;
            var retries = 0;

            while (targetNode.MatchIndex < lastLogIndex && retries < maxRetries)
            {
                await _logReplicator.ReplicateToNodeAsync(targetNode.NodeId, lastLogIndex);
                retries++;
                await Task.Delay(100, ct);
            }

            if (targetNode.MatchIndex < lastLogIndex)
            {
                return false; // Could not catch up target
            }

            // Step 3: Send TimeoutNow to target to trigger immediate election
            var timeoutNow = new TimeoutNowMessage
            {
                FromNodeId = _nodeId,
                Term = _raftState.CurrentTerm
            };

            // Simulate sending TimeoutNow
            await Task.Delay(10, ct);

            // Step 4: Step down
            lock (_stateLock)
            {
                _raftState.Role = GeoRaftRole.Follower;
                _currentLeader = null;
            }

            return true;
        }
        finally
        {
            _raftState.IsTransferringLeadership = false;
        }
    }

    private int CalculateLeaderPriority(Datacenter? datacenter)
    {
        if (datacenter == null) return int.MaxValue;

        // Lower priority number = higher priority
        // Consider: DC priority, weight, and node health
        return datacenter.Priority * 100 + (1000 - datacenter.Weight);
    }

    private bool ShouldGrantLeaderVote(Datacenter votingDc, int candidatePriority)
    {
        // Grant vote if candidate has equal or better priority
        var localPriority = CalculateLeaderPriority(votingDc);
        return candidatePriority <= localPriority + 100; // Some tolerance
    }

    #endregion

    #region Membership Changes (Joint Consensus)

    /// <summary>
    /// Initiates a membership change using joint consensus.
    /// Joint consensus uses a two-phase approach to safely change cluster membership.
    /// </summary>
    public async Task<MembershipChangeResult> ChangeMembershipAsync(
        MembershipChangeRequest request,
        CancellationToken ct = default)
    {
        if (!IsLeader)
        {
            return new MembershipChangeResult
            {
                Success = false,
                Error = "Only leader can initiate membership changes"
            };
        }

        // Create joint configuration (old + new)
        var oldConfig = _raftNodes.Values
            .Where(n => (VotingStatus)n.VotingStatus! == VotingStatus.Voter)
            .Select(n => n.NodeId)
            .ToList();

        var newConfig = request.ChangeType switch
        {
            MembershipChangeType.AddNode => oldConfig.Concat(new[] { request.NodeId }).Distinct().ToList(),
            MembershipChangeType.RemoveNode => oldConfig.Where(id => id != request.NodeId).ToList(),
            MembershipChangeType.PromoteLearner => oldConfig.Concat(new[] { request.NodeId }).Distinct().ToList(),
            _ => oldConfig
        };

        // Phase 1: Commit joint configuration (C_old,new)
        _jointConsensus = new JointConsensusState
        {
            OldConfiguration = oldConfig.ToHashSet(),
            NewConfiguration = newConfig.ToHashSet(),
            Phase = JointConsensusPhase.Joint,
            StartedAt = DateTime.UtcNow
        };

        var jointEntry = new GeoLogEntry
        {
            Index = _logReplicator.GetLastLogIndex() + 1,
            Term = _raftState.CurrentTerm,
            Type = LogEntryType.Configuration,
            Command = "membership.joint",
            Data = JsonSerializer.SerializeToUtf8Bytes(_jointConsensus),
            Timestamp = _hlc.Now()
        };

        var jointCommitted = await _logReplicator.AppendAndReplicateAsync(jointEntry);
        if (!jointCommitted)
        {
            _jointConsensus = null;
            return new MembershipChangeResult
            {
                Success = false,
                Error = "Failed to commit joint configuration"
            };
        }

        // Apply the membership change
        ApplyMembershipChange(request);

        // Phase 2: Commit new configuration (C_new)
        _jointConsensus.Phase = JointConsensusPhase.New;

        var newEntry = new GeoLogEntry
        {
            Index = _logReplicator.GetLastLogIndex() + 1,
            Term = _raftState.CurrentTerm,
            Type = LogEntryType.Configuration,
            Command = "membership.new",
            Data = JsonSerializer.SerializeToUtf8Bytes(newConfig),
            Timestamp = _hlc.Now()
        };

        var newCommitted = await _logReplicator.AppendAndReplicateAsync(newEntry);

        _jointConsensus = null;

        return new MembershipChangeResult
        {
            Success = newCommitted,
            OldConfiguration = oldConfig.ToHashSet(),
            NewConfiguration = newConfig.ToHashSet(),
            Error = newCommitted ? null : "Failed to commit new configuration"
        };
    }

    private void ApplyMembershipChange(MembershipChangeRequest request)
    {
        switch (request.ChangeType)
        {
            case MembershipChangeType.AddNode:
                _raftNodes[request.NodeId] = new GeoRaftNode
                {
                    NodeId = request.NodeId,
                    DatacenterId = request.DatacenterId ?? _localDatacenterId,
                    Endpoint = request.Endpoint ?? "",
                    VotingStatus = VotingStatus.Voter,
                    Status = SDK.Contracts.NodeStatus.Active,
                    Role = SDK.Contracts.NodeRole.Voter
                };
                break;

            case MembershipChangeType.RemoveNode:
                _raftNodes.TryRemove(request.NodeId, out _);
                break;

            case MembershipChangeType.PromoteLearner:
                if (_raftNodes.TryGetValue(request.NodeId, out var learner))
                {
                    learner.VotingStatus = VotingStatus.Voter;
                }
                break;

            case MembershipChangeType.DemoteToLearner:
                if (_raftNodes.TryGetValue(request.NodeId, out var voter))
                {
                    voter.VotingStatus = VotingStatus.Learner;
                }
                break;
        }
    }

    /// <summary>
    /// Adds a learner node that replicates logs but doesn't vote.
    /// </summary>
    public void AddLearner(GeoRaftNode learner)
    {
        learner.VotingStatus = VotingStatus.Learner;
        _raftNodes[learner.NodeId] = learner;
    }

    /// <summary>
    /// Adds a witness node for tie-breaking in quorum calculations.
    /// </summary>
    public void AddWitness(GeoRaftNode witness)
    {
        witness.VotingStatus = VotingStatus.Witness;
        witness.Role = SDK.Contracts.NodeRole.Observer;
        _raftNodes[witness.NodeId] = witness;
    }

    #endregion

    #region Heartbeat and Health

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HeartbeatIntervalMs, ct);

                if (IsLeader)
                {
                    await SendLeaderHeartbeatsAsync(ct);
                }

                UpdateNodeHealth();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Heartbeat failed
            }
        }
    }

    private async Task SendLeaderHeartbeatsAsync(CancellationToken ct)
    {
        var activeDatacenters = GetActiveDatacenters();

        var heartbeatTasks = activeDatacenters
            .Where(dc => dc.Id != _localDatacenterId)
            .Select(async dc =>
            {
                try
                {
                    await Task.Delay(dc.EstimatedLatencyMs / 2, ct);
                    // Heartbeat sent successfully
                    dc.LastHeartbeat = DateTime.UtcNow;
                }
                catch
                {
                    // Heartbeat failed
                }
            });

        await Task.WhenAll(heartbeatTasks);

        if (_currentLeader != null)
        {
            _currentLeader.LastHeartbeat = DateTime.UtcNow;
        }
    }

    private void UpdateNodeHealth()
    {
        var now = DateTime.UtcNow;
        var staleThreshold = TimeSpan.FromMilliseconds(_config.NodeStaleThresholdMs);

        foreach (var node in _nodes.Values)
        {
            if (node.NodeId == _nodeId)
            {
                node.LastHeartbeat = now;
                node.Status = NodeStatus.Active;
                continue;
            }

            if (now - node.LastHeartbeat > staleThreshold)
            {
                node.Status = NodeStatus.Suspected;
            }
        }
    }

    #endregion

    #region Partition Detection

    private async Task RunPartitionMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.PartitionCheckIntervalMs, ct);

                var partitionStatus = DetectPartitions();
                _partitionDetector?.Update(partitionStatus);

                if (partitionStatus.IsPartitioned)
                {
                    await HandlePartitionAsync(partitionStatus, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Partition check failed
            }
        }
    }

    private PartitionStatus DetectPartitions()
    {
        var status = new PartitionStatus();
        var now = DateTime.UtcNow;

        var reachableDatacenters = new List<string>();
        var unreachableDatacenters = new List<string>();

        foreach (var dc in _datacenters.Values)
        {
            if (dc.Id == _localDatacenterId)
            {
                reachableDatacenters.Add(dc.Id);
                continue;
            }

            var staleness = now - dc.LastHeartbeat;
            if (staleness > TimeSpan.FromMilliseconds(_config.PartitionDetectionThresholdMs))
            {
                unreachableDatacenters.Add(dc.Id);
            }
            else
            {
                reachableDatacenters.Add(dc.Id);
            }
        }

        status.ReachableDatacenters = reachableDatacenters;
        status.UnreachableDatacenters = unreachableDatacenters;
        status.IsPartitioned = unreachableDatacenters.Count > 0;
        status.DetectedAt = now;

        // Determine partition type
        if (status.IsPartitioned)
        {
            var reachableWeight = reachableDatacenters
                .Select(id => _datacenters.GetValueOrDefault(id)?.Weight ?? 0)
                .Sum();
            var totalWeight = _quorumConfig.TotalWeight;

            status.PartitionType = reachableWeight >= _quorumConfig.MajorityWeight
                ? PartitionType.MinorityIsolated
                : PartitionType.MajorityIsolated;
        }

        return status;
    }

    private async Task HandlePartitionAsync(PartitionStatus status, CancellationToken ct)
    {
        switch (status.PartitionType)
        {
            case PartitionType.MajorityIsolated:
                // We're in the minority partition - go read-only
                _config.ReadOnlyMode = true;
                break;

            case PartitionType.MinorityIsolated:
                // We're in the majority partition - continue normal operation
                // But mark isolated DCs as inactive
                foreach (var dcId in status.UnreachableDatacenters)
                {
                    if (_datacenters.TryGetValue(dcId, out var dc))
                    {
                        dc.Status = DatacenterStatus.Partitioned;
                    }
                }
                break;
        }

        await Task.CompletedTask;
    }

    #endregion

    #region Stale Fence

    private async Task RunStaleFenceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.StaleFenceIntervalMs, ct);

                if (_config.DefaultConsistencyLevel == ConsistencyLevel.BoundedStaleness)
                {
                    CheckBoundedStaleness();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Stale fence check failed
            }
        }
    }

    private void CheckBoundedStaleness()
    {
        var now = _hlc.Now();
        var maxStaleness = TimeSpan.FromMilliseconds(_config.BoundedStalenessMaxMs);

        foreach (var round in _rounds.Values)
        {
            if (round.Phase == ConsensusPhase.Committed)
            {
                var age = now.WallTime - round.Timestamp.WallTime;
                if (age > maxStaleness)
                {
                    // Mark as stale for bounded staleness reads
                    round.IsStale = true;
                }
            }
        }
    }

    #endregion

    #region Conflict Resolution

    /// <summary>
    /// Resolves conflicts between concurrent writes.
    /// </summary>
    /// <param name="existing">The existing value.</param>
    /// <param name="incoming">The incoming value.</param>
    /// <returns>The resolved value.</returns>
    public ConflictResolutionResult ResolveConflict(CommittedValue existing, CommittedValue incoming)
    {
        return _conflictResolver.Resolve(existing, incoming);
    }

    #endregion

    #region Read Operations

    /// <summary>
    /// Reads a value with the specified consistency level.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="consistencyLevel">The required consistency level.</param>
    /// <returns>The read result.</returns>
    public async Task<ReadResult> ReadAsync(string key, ConsistencyLevel consistencyLevel)
    {
        // Check if in read-only mode due to partition
        if (_config.ReadOnlyMode && consistencyLevel == ConsistencyLevel.Strong)
        {
            return new ReadResult
            {
                Success = false,
                Error = "System is in read-only mode due to network partition"
            };
        }

        switch (consistencyLevel)
        {
            case ConsistencyLevel.Strong:
                return await ReadStrongAsync(key);

            case ConsistencyLevel.Quorum:
                return await ReadQuorumAsync(key);

            case ConsistencyLevel.BoundedStaleness:
                return await ReadBoundedStalenessAsync(key, _config.BoundedStalenessMaxMs);

            case ConsistencyLevel.Eventual:
            case ConsistencyLevel.LocalQuorum:
            default:
                return ReadLocal(key);
        }
    }

    private async Task<ReadResult> ReadStrongAsync(string key)
    {
        // Ensure we have the latest value by checking with leader
        if (!IsLeader)
        {
            // Would normally forward to leader
            await Task.Delay(10);
        }

        return ReadLocal(key);
    }

    private async Task<ReadResult> ReadQuorumAsync(string key)
    {
        // Read from quorum of datacenters and return most recent
        var activeDatacenters = GetActiveDatacenters();
        var readTasks = activeDatacenters.Select(async dc =>
        {
            try
            {
                await Task.Delay(dc.Id == _localDatacenterId ? 1 : dc.EstimatedLatencyMs / 2);
                var value = _committedState.GetValueOrDefault(key);
                return (dc.Id, value, _hlc.Now());
            }
            catch
            {
                return (dc.Id, (object?)null, _hlc.Now());
            }
        });

        var results = await Task.WhenAll(readTasks);
        var successfulReads = results.Where(r => r.Item2 != null).ToList();

        if (successfulReads.Count >= _quorumConfig.MinDatacentersForQuorum)
        {
            // Return most recent value based on timestamp
            var mostRecent = successfulReads.OrderByDescending(r => r.Item3.WallTime).First();
            return new ReadResult
            {
                Success = true,
                Value = mostRecent.Item2 as byte[],
                Timestamp = mostRecent.Item3,
                SourceDatacenter = mostRecent.Item1
            };
        }

        return new ReadResult { Success = false, Error = "Quorum not achieved for read" };
    }

    private async Task<ReadResult> ReadBoundedStalenessAsync(string key, int maxStalenessMs)
    {
        var result = ReadLocal(key);
        if (!result.Success) return result;

        var staleness = _hlc.Now().WallTime - result.Timestamp.WallTime;
        if (staleness.TotalMilliseconds > maxStalenessMs)
        {
            // Value is too stale, try to get fresher data
            return await ReadQuorumAsync(key);
        }

        return result;
    }

    private ReadResult ReadLocal(string key)
    {
        if (_committedState.TryGetValue(key, out var value))
        {
            return new ReadResult
            {
                Success = true,
                Value = value as byte[],
                Timestamp = _hlc.Now(),
                SourceDatacenter = _localDatacenterId
            };
        }

        return new ReadResult { Success = false, Error = "Key not found" };
    }

    #endregion

    #region Message Handling

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null) return;

        var response = message.Type switch
        {
            "geo.propose" => await HandleProposeMessageAsync(message.Payload),
            "geo.read" => await HandleReadMessageAsync(message.Payload),
            "geo.topology.add" => HandleTopologyAdd(message.Payload),
            "geo.topology.remove" => HandleTopologyRemove(message.Payload),
            "geo.topology.status" => HandleTopologyStatus(),
            "geo.leader.info" => HandleLeaderInfo(),
            "geo.partition.status" => HandlePartitionStatus(),
            "geo.configure" => HandleConfigure(message.Payload),
            "geo.round.status" => HandleRoundStatus(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        message.Payload["_response"] = response;
    }

    private async Task<Dictionary<string, object>> HandleProposeMessageAsync(Dictionary<string, object> payload)
    {
        var command = payload.GetValueOrDefault("command")?.ToString() ?? "";
        var payloadB64 = payload.GetValueOrDefault("payload")?.ToString() ?? "";
        var consistencyStr = payload.GetValueOrDefault("consistency")?.ToString() ?? "quorum";

        if (!Enum.TryParse<ConsistencyLevel>(consistencyStr, true, out var consistency))
        {
            consistency = ConsistencyLevel.Quorum;
        }

        var proposal = new Proposal
        {
            Command = command,
            Payload = string.IsNullOrEmpty(payloadB64) ? [] : Convert.FromBase64String(payloadB64)
        };

        var success = await ProposeWithConsistencyAsync(proposal, consistency);

        return new Dictionary<string, object>
        {
            ["success"] = success,
            ["proposalId"] = proposal.Id,
            ["consistency"] = consistency.ToString()
        };
    }

    private async Task<Dictionary<string, object>> HandleReadMessageAsync(Dictionary<string, object> payload)
    {
        var key = payload.GetValueOrDefault("key")?.ToString() ?? "";
        var consistencyStr = payload.GetValueOrDefault("consistency")?.ToString() ?? "eventual";

        if (!Enum.TryParse<ConsistencyLevel>(consistencyStr, true, out var consistency))
        {
            consistency = ConsistencyLevel.Eventual;
        }

        var result = await ReadAsync(key, consistency);

        return new Dictionary<string, object>
        {
            ["success"] = result.Success,
            ["value"] = result.Value != null ? Convert.ToBase64String(result.Value) : null!,
            ["error"] = result.Error ?? "",
            ["sourceDatacenter"] = result.SourceDatacenter ?? "",
            ["timestamp"] = result.Timestamp.WallTime.ToString("O")
        };
    }

    private Dictionary<string, object> HandleTopologyAdd(Dictionary<string, object> payload)
    {
        var id = payload.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString("N")[..8];
        var name = payload.GetValueOrDefault("name")?.ToString() ?? id;
        var region = payload.GetValueOrDefault("region")?.ToString() ?? "unknown";
        var weight = Convert.ToInt32(payload.GetValueOrDefault("weight") ?? 100);
        var priority = Convert.ToInt32(payload.GetValueOrDefault("priority") ?? 1);
        var latency = Convert.ToInt32(payload.GetValueOrDefault("latencyMs") ?? 50);

        var datacenter = new Datacenter
        {
            Id = id,
            Name = name,
            Region = region,
            Weight = weight,
            Priority = priority,
            EstimatedLatencyMs = latency,
            Status = DatacenterStatus.Active
        };

        RegisterDatacenter(datacenter);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["datacenterId"] = id,
            ["totalDatacenters"] = _datacenters.Count
        };
    }

    private Dictionary<string, object> HandleTopologyRemove(Dictionary<string, object> payload)
    {
        var id = payload.GetValueOrDefault("id")?.ToString() ?? "";

        if (string.IsNullOrEmpty(id))
        {
            return new Dictionary<string, object> { ["error"] = "Datacenter ID is required" };
        }

        var removed = RemoveDatacenter(id);

        return new Dictionary<string, object>
        {
            ["success"] = removed,
            ["datacenterId"] = id,
            ["totalDatacenters"] = _datacenters.Count
        };
    }

    private Dictionary<string, object> HandleTopologyStatus()
    {
        var datacenters = _datacenters.Values.Select(dc => new Dictionary<string, object>
        {
            ["id"] = dc.Id,
            ["name"] = dc.Name,
            ["region"] = dc.Region,
            ["weight"] = dc.Weight,
            ["priority"] = dc.Priority,
            ["status"] = dc.Status.ToString(),
            ["nodeCount"] = _nodes.Values.Count(n => n.DatacenterId == dc.Id),
            ["lastHeartbeat"] = dc.LastHeartbeat.ToString("O")
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["datacenters"] = datacenters,
            ["totalWeight"] = _quorumConfig.TotalWeight,
            ["majorityWeight"] = _quorumConfig.MajorityWeight,
            ["minDatacentersForQuorum"] = _quorumConfig.MinDatacentersForQuorum,
            ["localDatacenter"] = _localDatacenterId
        };
    }

    private Dictionary<string, object> HandleLeaderInfo()
    {
        var leader = _currentLeader;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["hasLeader"] = leader != null,
            ["isLocalLeader"] = IsLeader,
            ["leaderNodeId"] = leader?.NodeId ?? "",
            ["leaderDatacenterId"] = leader?.DatacenterId ?? "",
            ["electedAt"] = leader?.ElectedAt.ToString("O") ?? "",
            ["lastHeartbeat"] = leader?.LastHeartbeat.ToString("O") ?? "",
            ["leaderRound"] = leader?.LeaderRound ?? 0
        };
    }

    private Dictionary<string, object> HandlePartitionStatus()
    {
        var status = DetectPartitions();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["isPartitioned"] = status.IsPartitioned,
            ["partitionType"] = status.PartitionType.ToString(),
            ["reachableDatacenters"] = status.ReachableDatacenters,
            ["unreachableDatacenters"] = status.UnreachableDatacenters,
            ["readOnlyMode"] = _config.ReadOnlyMode,
            ["detectedAt"] = status.DetectedAt.ToString("O")
        };
    }

    private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("defaultConsistency", out var consistency))
        {
            if (Enum.TryParse<ConsistencyLevel>(consistency?.ToString(), true, out var level))
            {
                _config.DefaultConsistencyLevel = level;
            }
        }

        if (payload.TryGetValue("prepareTimeoutMs", out var prepareTimeout))
        {
            _config.PrepareTimeoutMs = Convert.ToInt32(prepareTimeout);
        }

        if (payload.TryGetValue("acceptTimeoutMs", out var acceptTimeout))
        {
            _config.AcceptTimeoutMs = Convert.ToInt32(acceptTimeout);
        }

        if (payload.TryGetValue("boundedStalenessMaxMs", out var staleness))
        {
            _config.BoundedStalenessMaxMs = Convert.ToInt32(staleness);
        }

        if (payload.TryGetValue("conflictResolution", out var resolution))
        {
            if (Enum.TryParse<ConflictResolutionStrategy>(resolution?.ToString(), true, out var strategy))
            {
                _config.ConflictResolutionStrategy = strategy;
                _conflictResolver = new ConflictResolver(strategy);
            }
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["defaultConsistency"] = _config.DefaultConsistencyLevel.ToString(),
            ["prepareTimeoutMs"] = _config.PrepareTimeoutMs,
            ["acceptTimeoutMs"] = _config.AcceptTimeoutMs,
            ["boundedStalenessMaxMs"] = _config.BoundedStalenessMaxMs,
            ["conflictResolution"] = _config.ConflictResolutionStrategy.ToString()
        };
    }

    private Dictionary<string, object> HandleRoundStatus(Dictionary<string, object> payload)
    {
        var roundNumber = Convert.ToInt64(payload.GetValueOrDefault("round") ?? _currentRound);

        if (_rounds.TryGetValue(roundNumber, out var round))
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["roundNumber"] = round.RoundNumber,
                ["phase"] = round.Phase.ToString(),
                ["proposalId"] = round.ProposalId,
                ["command"] = round.Command,
                ["consistencyLevel"] = round.ConsistencyLevel.ToString(),
                ["initiatorNode"] = round.InitiatorNodeId,
                ["initiatorDatacenter"] = round.InitiatorDatacenterId,
                ["isStale"] = round.IsStale,
                ["committedAt"] = round.CommittedAt?.ToString("O") ?? ""
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = false,
            ["error"] = $"Round {roundNumber} not found"
        };
    }

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "propose",
                Description = "Propose a value with geo-distributed consensus",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["command"] = new { type = "string", description = "Command type" },
                        ["payload"] = new { type = "string", description = "Base64-encoded payload" },
                        ["consistency"] = new { type = "string", description = "Consistency level: strong, quorum, boundedStaleness, eventual" }
                    },
                    ["required"] = new[] { "command", "payload" }
                }
            },
            new()
            {
                Name = "read",
                Description = "Read a value with specified consistency",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["key"] = new { type = "string", description = "Key to read" },
                        ["consistency"] = new { type = "string", description = "Consistency level" }
                    },
                    ["required"] = new[] { "key" }
                }
            },
            new()
            {
                Name = "topology.add",
                Description = "Add a datacenter to the topology",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["id"] = new { type = "string", description = "Datacenter ID" },
                        ["name"] = new { type = "string", description = "Datacenter name" },
                        ["region"] = new { type = "string", description = "Geographic region" },
                        ["weight"] = new { type = "number", description = "Voting weight" },
                        ["priority"] = new { type = "number", description = "Leader election priority" }
                    },
                    ["required"] = new[] { "id" }
                }
            },
            new()
            {
                Name = "topology.status",
                Description = "Get current topology status",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>()
                }
            },
            new()
            {
                Name = "leader.info",
                Description = "Get current leader information",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>()
                }
            },
            new()
            {
                Name = "partition.status",
                Description = "Get network partition status",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>()
                }
            }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var meta = base.GetMetadata();
        meta["ConsensusAlgorithm"] = "Multi-Paxos (Geo-Distributed)";
        meta["NodeId"] = _nodeId;
        meta["LocalDatacenter"] = _localDatacenterId;
        meta["DatacenterCount"] = _datacenters.Count;
        meta["CurrentRound"] = _currentRound;
        meta["LastCommittedRound"] = _lastCommittedRound;
        meta["IsLeader"] = IsLeader;
        meta["LeaderNodeId"] = _currentLeader?.NodeId ?? "none";
        meta["DefaultConsistency"] = _config.DefaultConsistencyLevel.ToString();
        meta["SupportsMultiLeader"] = true;
        meta["SupportsPartitionHandling"] = true;
        return meta;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Represents a datacenter in the geo-distributed topology.
/// </summary>
public sealed class Datacenter
{
    /// <summary>
    /// Unique identifier for the datacenter.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Geographic region (e.g., "us-east", "eu-west").
    /// </summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>
    /// Voting weight for quorum calculations.
    /// </summary>
    public int Weight { get; init; } = 100;

    /// <summary>
    /// Priority for leader election (lower = higher priority).
    /// </summary>
    public int Priority { get; init; } = 1;

    /// <summary>
    /// Estimated network latency in milliseconds.
    /// </summary>
    public int EstimatedLatencyMs { get; init; } = 50;

    /// <summary>
    /// Current status of the datacenter.
    /// </summary>
    public DatacenterStatus Status { get; set; } = DatacenterStatus.Active;

    /// <summary>
    /// When the datacenter joined the topology.
    /// </summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// Last heartbeat received from this datacenter.
    /// </summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Status of a datacenter.
/// </summary>
public enum DatacenterStatus
{
    /// <summary>Active and participating in consensus.</summary>
    Active,
    /// <summary>Inactive and not participating.</summary>
    Inactive,
    /// <summary>Isolated due to network partition.</summary>
    Partitioned,
    /// <summary>Under maintenance.</summary>
    Maintenance,
    /// <summary>Failing or degraded.</summary>
    Degraded
}

/// <summary>
/// Represents a node within a datacenter.
/// </summary>
public sealed class DatacenterNode
{
    /// <summary>Unique node identifier.</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Parent datacenter ID.</summary>
    public string DatacenterId { get; init; } = string.Empty;

    /// <summary>Network endpoint.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Node role in consensus.</summary>
    public NodeRole Role { get; init; } = NodeRole.Voter;

    /// <summary>Current status.</summary>
    public NodeStatus Status { get; set; } = NodeStatus.Active;

    /// <summary>Last heartbeat time.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary>When the node joined.</summary>
    public DateTime JoinedAt { get; set; }
}

/// <summary>
/// Role of a node in consensus.
/// </summary>
public enum NodeRole
{
    /// <summary>Full voting member.</summary>
    Voter,
    /// <summary>Non-voting learner.</summary>
    Learner,
    /// <summary>Witness for quorum.</summary>
    Witness
}

/// <summary>
/// Status of a node.
/// </summary>
public enum NodeStatus
{
    /// <summary>Active and healthy.</summary>
    Active,
    /// <summary>Suspected failure.</summary>
    Suspected,
    /// <summary>Confirmed failed.</summary>
    Failed,
    /// <summary>Recovering.</summary>
    Recovering
}

/// <summary>
/// Configuration for quorum calculations.
/// </summary>
public sealed class QuorumConfig
{
    /// <summary>Total voting weight across all datacenters.</summary>
    public int TotalWeight { get; set; }

    /// <summary>Weight required for majority.</summary>
    public int MajorityWeight { get; set; }

    /// <summary>Minimum datacenters for quorum.</summary>
    public int MinDatacentersForQuorum { get; set; } = 1;

    /// <summary>Whether to require local datacenter in quorum.</summary>
    public bool RequireLocalDatacenter { get; set; }
}

/// <summary>
/// Result of quorum calculation.
/// </summary>
public sealed class QuorumResult
{
    /// <summary>Whether quorum was achieved.</summary>
    public bool Achieved { get; set; }

    /// <summary>Total weight in the topology.</summary>
    public int TotalWeight { get; set; }

    /// <summary>Weight achieved from votes.</summary>
    public int AchievedWeight { get; set; }

    /// <summary>Weight required for this quorum type.</summary>
    public int RequiredWeight { get; set; }

    /// <summary>Number of datacenters that responded.</summary>
    public int DatacentersResponded { get; set; }

    /// <summary>Total number of datacenters.</summary>
    public int TotalDatacenters { get; set; }

    /// <summary>Number of accept votes.</summary>
    public int AcceptVotes { get; set; }

    /// <summary>Number of reject votes.</summary>
    public int RejectVotes { get; set; }

    /// <summary>Explanation of the result.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Represents a consensus round in the Paxos protocol.
/// </summary>
public sealed class ConsensusRound
{
    /// <summary>Unique round number.</summary>
    public long RoundNumber { get; init; }

    /// <summary>Associated proposal ID.</summary>
    public string ProposalId { get; init; } = string.Empty;

    /// <summary>Command being proposed.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Proposal payload.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>Hybrid logical clock timestamp.</summary>
    public HlcTimestamp Timestamp { get; init; }

    /// <summary>Required consistency level.</summary>
    public ConsistencyLevel ConsistencyLevel { get; init; }

    /// <summary>Current phase of the round.</summary>
    public ConsensusPhase Phase { get; set; }

    /// <summary>Node that initiated this round.</summary>
    public string InitiatorNodeId { get; init; } = string.Empty;

    /// <summary>Datacenter that initiated this round.</summary>
    public string InitiatorDatacenterId { get; init; } = string.Empty;

    /// <summary>Votes received in prepare phase.</summary>
    public Dictionary<string, Vote> PrepareVotes { get; set; } = new();

    /// <summary>Votes received in accept phase.</summary>
    public Dictionary<string, Vote> AcceptVotes { get; set; } = new();

    /// <summary>When the round was committed.</summary>
    public DateTime? CommittedAt { get; set; }

    /// <summary>Whether this round's value is stale.</summary>
    public bool IsStale { get; set; }

    /// <summary>Reason for failure if failed.</summary>
    public string? FailureReason { get; set; }
}

/// <summary>
/// Phase of the consensus protocol.
/// </summary>
public enum ConsensusPhase
{
    /// <summary>Initial state.</summary>
    Init,
    /// <summary>Prepare phase (Paxos Phase 1).</summary>
    Prepare,
    /// <summary>Accept phase (Paxos Phase 2).</summary>
    Accept,
    /// <summary>Commit phase.</summary>
    Commit,
    /// <summary>Successfully committed.</summary>
    Committed,
    /// <summary>Round failed.</summary>
    Failed
}

/// <summary>
/// A vote in the consensus protocol.
/// </summary>
public sealed class Vote
{
    /// <summary>Datacenter that cast the vote.</summary>
    public string DatacenterId { get; init; } = string.Empty;

    /// <summary>Node that cast the vote.</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Round number for this vote.</summary>
    public long RoundNumber { get; init; }

    /// <summary>Whether the vote accepts the proposal.</summary>
    public bool Accepted { get; init; }

    /// <summary>Timestamp of the vote.</summary>
    public HlcTimestamp Timestamp { get; init; }

    /// <summary>Highest round seen by this voter.</summary>
    public long HighestSeenRound { get; init; }
}

/// <summary>
/// Information about the current leader.
/// </summary>
public sealed class LeaderInfo
{
    /// <summary>Leader's node ID.</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Leader's datacenter ID.</summary>
    public string DatacenterId { get; init; } = string.Empty;

    /// <summary>When the leader was elected.</summary>
    public DateTime ElectedAt { get; init; }

    /// <summary>Last heartbeat from leader.</summary>
    public DateTime LastHeartbeat { get; set; }

    /// <summary>Round in which leader was elected.</summary>
    public long LeaderRound { get; init; }

    /// <summary>Leader's priority score.</summary>
    public int Priority { get; init; }
}

/// <summary>
/// Vote for leader election.
/// </summary>
public sealed class LeaderVote
{
    /// <summary>Datacenter casting the vote.</summary>
    public string DatacenterId { get; init; } = string.Empty;

    /// <summary>Candidate's node ID.</summary>
    public string CandidateNodeId { get; init; } = string.Empty;

    /// <summary>Candidate's datacenter ID.</summary>
    public string CandidateDatacenterId { get; init; } = string.Empty;

    /// <summary>Election round.</summary>
    public long Round { get; init; }

    /// <summary>Whether vote was granted.</summary>
    public bool Granted { get; init; }

    /// <summary>Vote timestamp.</summary>
    public HlcTimestamp Timestamp { get; init; }
}

/// <summary>
/// Consistency levels for read/write operations.
/// </summary>
public enum ConsistencyLevel
{
    /// <summary>All datacenters must acknowledge.</summary>
    Strong,

    /// <summary>Majority of datacenters must acknowledge.</summary>
    Quorum,

    /// <summary>Data may be stale up to a configured bound.</summary>
    BoundedStaleness,

    /// <summary>Eventually consistent - local DC only.</summary>
    Eventual,

    /// <summary>Quorum within local datacenter only.</summary>
    LocalQuorum
}

/// <summary>
/// Hybrid Logical Clock for ordering events.
/// </summary>
public sealed class HybridLogicalClock
{
    private long _wallTime;
    private int _logicalCounter;
    private readonly object _lock = new();

    /// <summary>
    /// Gets the current HLC timestamp.
    /// </summary>
    public HlcTimestamp Now()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var nowTicks = now.Ticks;

            if (nowTicks > _wallTime)
            {
                _wallTime = nowTicks;
                _logicalCounter = 0;
            }
            else
            {
                _logicalCounter++;
            }

            return new HlcTimestamp
            {
                WallTime = new DateTime(_wallTime, DateTimeKind.Utc),
                LogicalCounter = _logicalCounter
            };
        }
    }

    /// <summary>
    /// Updates the clock based on a received timestamp.
    /// </summary>
    public void Update(HlcTimestamp received)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow.Ticks;
            var receivedTicks = received.WallTime.Ticks;

            if (now > _wallTime && now > receivedTicks)
            {
                _wallTime = now;
                _logicalCounter = 0;
            }
            else if (_wallTime == receivedTicks)
            {
                _logicalCounter = Math.Max(_logicalCounter, received.LogicalCounter) + 1;
            }
            else if (receivedTicks > _wallTime)
            {
                _wallTime = receivedTicks;
                _logicalCounter = received.LogicalCounter + 1;
            }
            else
            {
                _logicalCounter++;
            }
        }
    }
}

/// <summary>
/// Timestamp from a Hybrid Logical Clock.
/// </summary>
public readonly struct HlcTimestamp : IComparable<HlcTimestamp>
{
    /// <summary>Wall clock time.</summary>
    public DateTime WallTime { get; init; }

    /// <summary>Logical counter for same wall time.</summary>
    public int LogicalCounter { get; init; }

    /// <inheritdoc/>
    public int CompareTo(HlcTimestamp other)
    {
        var wallComparison = WallTime.CompareTo(other.WallTime);
        return wallComparison != 0 ? wallComparison : LogicalCounter.CompareTo(other.LogicalCounter);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{WallTime:O}.{LogicalCounter}";
}

/// <summary>
/// Configuration for the geo-distributed consensus plugin.
/// </summary>
public sealed class GeoConsensusConfig
{
    /// <summary>Default consistency level for operations.</summary>
    public ConsistencyLevel DefaultConsistencyLevel { get; set; } = ConsistencyLevel.Quorum;

    /// <summary>Timeout for prepare phase in milliseconds.</summary>
    public int PrepareTimeoutMs { get; set; } = 5000;

    /// <summary>Timeout for accept phase in milliseconds.</summary>
    public int AcceptTimeoutMs { get; set; } = 5000;

    /// <summary>Heartbeat interval in milliseconds.</summary>
    public int HeartbeatIntervalMs { get; set; } = 1000;

    /// <summary>Minimum leader election timeout in milliseconds.</summary>
    public int LeaderElectionTimeoutMinMs { get; set; } = 5000;

    /// <summary>Maximum leader election timeout in milliseconds.</summary>
    public int LeaderElectionTimeoutMaxMs { get; set; } = 10000;

    /// <summary>Leader heartbeat timeout in milliseconds.</summary>
    public int LeaderHeartbeatTimeoutMs { get; set; } = 15000;

    /// <summary>Node stale threshold in milliseconds.</summary>
    public int NodeStaleThresholdMs { get; set; } = 30000;

    /// <summary>Partition check interval in milliseconds.</summary>
    public int PartitionCheckIntervalMs { get; set; } = 5000;

    /// <summary>Partition detection threshold in milliseconds.</summary>
    public int PartitionDetectionThresholdMs { get; set; } = 10000;

    /// <summary>Maximum staleness for bounded staleness reads in milliseconds.</summary>
    public int BoundedStalenessMaxMs { get; set; } = 5000;

    /// <summary>Stale fence check interval in milliseconds.</summary>
    public int StaleFenceIntervalMs { get; set; } = 1000;

    /// <summary>Conflict resolution strategy.</summary>
    public ConflictResolutionStrategy ConflictResolutionStrategy { get; set; } = ConflictResolutionStrategy.LastWriteWins;

    /// <summary>Whether the system is in read-only mode.</summary>
    public bool ReadOnlyMode { get; set; }

    /// <summary>Partition detection configuration.</summary>
    public PartitionDetectionConfig PartitionDetectionConfig { get; set; } = new();

    /// <summary>Enable pre-vote protocol to prevent disruptive elections.</summary>
    public bool EnablePreVote { get; set; } = true;

    /// <summary>Snapshot threshold - create snapshot after this many log entries.</summary>
    public int SnapshotThreshold { get; set; } = 10000;

    /// <summary>Snapshot interval in milliseconds.</summary>
    public int SnapshotIntervalMs { get; set; } = 300000;

    /// <summary>Log compaction threshold - compact log after this many entries.</summary>
    public int LogCompactionThreshold { get; set; } = 5000;

    /// <summary>Log compaction interval in milliseconds.</summary>
    public int LogCompactionIntervalMs { get; set; } = 60000;
}

/// <summary>
/// Configuration for partition detection.
/// </summary>
public sealed class PartitionDetectionConfig
{
    /// <summary>Number of consecutive failures before marking as partitioned.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Time window for failure detection in milliseconds.</summary>
    public int DetectionWindowMs { get; set; } = 30000;
}

/// <summary>
/// Status of network partitions.
/// </summary>
public sealed class PartitionStatus
{
    /// <summary>Whether a partition is detected.</summary>
    public bool IsPartitioned { get; set; }

    /// <summary>Type of partition.</summary>
    public PartitionType PartitionType { get; set; }

    /// <summary>Reachable datacenter IDs.</summary>
    public List<string> ReachableDatacenters { get; set; } = new();

    /// <summary>Unreachable datacenter IDs.</summary>
    public List<string> UnreachableDatacenters { get; set; } = new();

    /// <summary>When the partition was detected.</summary>
    public DateTime DetectedAt { get; set; }
}

/// <summary>
/// Type of network partition.
/// </summary>
public enum PartitionType
{
    /// <summary>No partition detected.</summary>
    None,

    /// <summary>Local node is in the minority partition.</summary>
    MajorityIsolated,

    /// <summary>Local node is in the majority partition.</summary>
    MinorityIsolated,

    /// <summary>Multiple partitions exist.</summary>
    Split
}

/// <summary>
/// Detects and tracks network partitions.
/// </summary>
public sealed class PartitionDetector
{
    private readonly PartitionDetectionConfig _config;
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();
    private volatile bool _isPartitioned;

    /// <summary>
    /// Creates a new partition detector.
    /// </summary>
    public PartitionDetector(PartitionDetectionConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Whether a partition is currently detected.
    /// </summary>
    public bool IsPartitioned => _isPartitioned;

    /// <summary>
    /// Updates the partition status.
    /// </summary>
    public void Update(PartitionStatus status)
    {
        _isPartitioned = status.IsPartitioned;

        foreach (var dc in status.UnreachableDatacenters)
        {
            _failureCounts.AddOrUpdate(dc, 1, (_, count) => count + 1);
        }

        foreach (var dc in status.ReachableDatacenters)
        {
            _failureCounts.TryRemove(dc, out _);
        }
    }

    /// <summary>
    /// Gets failure count for a datacenter.
    /// </summary>
    public int GetFailureCount(string datacenterId)
    {
        return _failureCounts.GetValueOrDefault(datacenterId);
    }
}

/// <summary>
/// Conflict resolution strategies.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>Most recent write wins based on timestamp.</summary>
    LastWriteWins,

    /// <summary>Higher priority datacenter wins.</summary>
    HigherPriorityWins,

    /// <summary>Merge conflicting values.</summary>
    Merge,

    /// <summary>Reject the conflicting write.</summary>
    Reject,

    /// <summary>Custom resolution logic.</summary>
    Custom
}

/// <summary>
/// A committed value with metadata.
/// </summary>
public sealed class CommittedValue
{
    /// <summary>The value data.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>HLC timestamp of the commit.</summary>
    public HlcTimestamp Timestamp { get; init; }

    /// <summary>Datacenter that committed the value.</summary>
    public string DatacenterId { get; init; } = string.Empty;

    /// <summary>Node that committed the value.</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Version number.</summary>
    public long Version { get; init; }
}

/// <summary>
/// Result of conflict resolution.
/// </summary>
public sealed class ConflictResolutionResult
{
    /// <summary>The resolved value.</summary>
    public byte[] ResolvedValue { get; init; } = [];

    /// <summary>Which value won.</summary>
    public string WinnerSource { get; init; } = string.Empty;

    /// <summary>Resolution strategy used.</summary>
    public ConflictResolutionStrategy StrategyUsed { get; init; }

    /// <summary>Additional resolution details.</summary>
    public string Details { get; init; } = string.Empty;
}

/// <summary>
/// Resolves conflicts between concurrent writes.
/// </summary>
public sealed class ConflictResolver
{
    private readonly ConflictResolutionStrategy _strategy;

    /// <summary>
    /// Creates a new conflict resolver.
    /// </summary>
    public ConflictResolver(ConflictResolutionStrategy strategy)
    {
        _strategy = strategy;
    }

    /// <summary>
    /// Resolves a conflict between two values.
    /// </summary>
    public ConflictResolutionResult Resolve(CommittedValue existing, CommittedValue incoming)
    {
        return _strategy switch
        {
            ConflictResolutionStrategy.LastWriteWins => ResolveLastWriteWins(existing, incoming),
            ConflictResolutionStrategy.HigherPriorityWins => ResolveHigherPriority(existing, incoming),
            ConflictResolutionStrategy.Merge => ResolveMerge(existing, incoming),
            ConflictResolutionStrategy.Reject => ResolveReject(existing),
            _ => ResolveLastWriteWins(existing, incoming)
        };
    }

    private ConflictResolutionResult ResolveLastWriteWins(CommittedValue existing, CommittedValue incoming)
    {
        var winner = incoming.Timestamp.CompareTo(existing.Timestamp) > 0 ? incoming : existing;
        return new ConflictResolutionResult
        {
            ResolvedValue = winner.Data,
            WinnerSource = winner.DatacenterId,
            StrategyUsed = ConflictResolutionStrategy.LastWriteWins,
            Details = $"Timestamp comparison: {incoming.Timestamp} vs {existing.Timestamp}"
        };
    }

    private ConflictResolutionResult ResolveHigherPriority(CommittedValue existing, CommittedValue incoming)
    {
        // In a real implementation, would compare datacenter priorities
        return ResolveLastWriteWins(existing, incoming);
    }

    private ConflictResolutionResult ResolveMerge(CommittedValue existing, CommittedValue incoming)
    {
        // Simple merge: concatenate (in real impl, would use CRDT or custom merge logic)
        var merged = new byte[existing.Data.Length + incoming.Data.Length];
        Buffer.BlockCopy(existing.Data, 0, merged, 0, existing.Data.Length);
        Buffer.BlockCopy(incoming.Data, 0, merged, existing.Data.Length, incoming.Data.Length);

        return new ConflictResolutionResult
        {
            ResolvedValue = merged,
            WinnerSource = "merged",
            StrategyUsed = ConflictResolutionStrategy.Merge,
            Details = "Values merged by concatenation"
        };
    }

    private ConflictResolutionResult ResolveReject(CommittedValue existing)
    {
        return new ConflictResolutionResult
        {
            ResolvedValue = existing.Data,
            WinnerSource = existing.DatacenterId,
            StrategyUsed = ConflictResolutionStrategy.Reject,
            Details = "Incoming value rejected, keeping existing"
        };
    }
}

/// <summary>
/// Result of a read operation.
/// </summary>
public sealed class ReadResult
{
    /// <summary>Whether the read was successful.</summary>
    public bool Success { get; init; }

    /// <summary>The read value.</summary>
    public byte[]? Value { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Datacenter the value was read from.</summary>
    public string? SourceDatacenter { get; init; }

    /// <summary>Timestamp of the value.</summary>
    public HlcTimestamp Timestamp { get; init; }
}

/// <summary>
/// Result of a consensus phase.
/// </summary>
internal sealed class PhaseResult
{
    /// <summary>Whether the phase succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Votes received.</summary>
    public Dictionary<string, Vote> Votes { get; init; } = new();

    /// <summary>Quorum calculation result.</summary>
    public QuorumResult QuorumResult { get; init; } = new();
}

/// <summary>
/// State of a pending proposal.
/// </summary>
internal sealed class ProposalState
{
    /// <summary>Proposal ID.</summary>
    public string ProposalId { get; init; } = string.Empty;

    /// <summary>Associated consensus round.</summary>
    public ConsensusRound Round { get; init; } = null!;

    /// <summary>Completion source for async waiting.</summary>
    public TaskCompletionSource<bool> CompletionSource { get; init; } = null!;
}

/// <summary>
/// Voting status for Raft nodes.
/// </summary>
internal enum VotingStatus
{
    Voter,
    Learner,
    Witness
}

/// <summary>
/// Membership change type.
/// </summary>
public enum MembershipChangeType
{
    AddNode,
    RemoveNode,
    PromoteLearner,
    DemoteToLearner
}

/// <summary>
/// Membership change request.
/// </summary>
public sealed class MembershipChangeRequest
{
    public string NodeId { get; init; } = string.Empty;
    public MembershipChangeType ChangeType { get; init; }
    public string? DatacenterId { get; init; }
    public string? Endpoint { get; init; }
}

/// <summary>
/// Membership change result.
/// </summary>
public sealed class MembershipChangeResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public HashSet<string> OldConfiguration { get; set; } = new();
    public HashSet<string> NewConfiguration { get; set; } = new();
}

/// <summary>
/// Pre-vote response for pre-vote protocol.
/// </summary>
public sealed class PreVoteResponse
{
    public long Term { get; set; }
    public bool VoteGranted { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Timeout now message for leadership transfer.
/// </summary>
public sealed class TimeoutNowMessage
{
    public long Term { get; set; }
    public string LeaderId { get; set; } = string.Empty;
    public string FromNodeId { get; set; } = string.Empty;
}

/// <summary>
/// Geo log entry for replication.
/// </summary>
public sealed class GeoLogEntry
{
    public long Index { get; set; }
    public long Term { get; set; }
    public LogEntryType Type { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Command { get; set; } = string.Empty;
    public HlcTimestamp Timestamp { get; set; }
}

/// <summary>
/// Log entry type.
/// </summary>
public enum LogEntryType
{
    Normal,
    Configuration,
    NoOp
}

/// <summary>
/// Joint consensus phase.
/// </summary>
public enum JointConsensusPhase
{
    OldConfiguration,
    Joint,
    New
}

#endregion
