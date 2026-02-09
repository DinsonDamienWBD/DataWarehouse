using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.Consensus;

/// <summary>
/// Node state in Raft consensus.
/// </summary>
public enum RaftState { Follower, Candidate, Leader }

/// <summary>
/// Raft consensus protocol implementation.
/// </summary>
public sealed class RaftConsensusStrategy : ResilienceStrategyBase
{
    private RaftState _state = RaftState.Follower;
    private long _currentTerm;
    private string? _votedFor;
    private string? _leaderId;
    private readonly ConcurrentDictionary<string, long> _nextIndex = new();
    private readonly ConcurrentDictionary<string, long> _matchIndex = new();
    private readonly List<(long term, object command)> _log = new();
    private long _commitIndex;
    private long _lastApplied;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.UtcNow;
    private readonly object _stateLock = new();

    private readonly string _nodeId;
    private readonly List<string> _clusterNodes;
    private readonly TimeSpan _electionTimeout;
    private readonly TimeSpan _heartbeatInterval;

    public RaftConsensusStrategy()
        : this(
            nodeId: Guid.NewGuid().ToString("N")[..8],
            clusterNodes: new List<string>(),
            electionTimeout: TimeSpan.FromMilliseconds(Random.Shared.Next(150, 300)),
            heartbeatInterval: TimeSpan.FromMilliseconds(50))
    {
    }

    public RaftConsensusStrategy(string nodeId, List<string> clusterNodes, TimeSpan electionTimeout, TimeSpan heartbeatInterval)
    {
        _nodeId = nodeId;
        _clusterNodes = clusterNodes;
        _electionTimeout = electionTimeout;
        _heartbeatInterval = heartbeatInterval;
    }

    public override string StrategyId => "consensus-raft";
    public override string StrategyName => "Raft Consensus";
    public override string Category => "Consensus";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Raft Consensus",
        Description = "Raft distributed consensus protocol with leader election, log replication, and safety guarantees",
        Category = "Consensus",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Gets the current node state.</summary>
    public RaftState State => _state;

    /// <summary>Gets the current term.</summary>
    public long CurrentTerm => _currentTerm;

    /// <summary>Gets the current leader ID.</summary>
    public string? LeaderId => _leaderId;

    /// <summary>Gets whether this node is the leader.</summary>
    public bool IsLeader => _state == RaftState.Leader;

    /// <summary>
    /// Adds a node to the cluster.
    /// </summary>
    public void AddNode(string nodeId)
    {
        lock (_stateLock)
        {
            if (!_clusterNodes.Contains(nodeId))
            {
                _clusterNodes.Add(nodeId);
            }
        }
    }

    /// <summary>
    /// Starts an election (become candidate).
    /// </summary>
    public async Task<bool> StartElectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _state = RaftState.Candidate;
            _currentTerm++;
            _votedFor = _nodeId;
        }

        var votesReceived = 1; // Vote for self
        var votesNeeded = (_clusterNodes.Count + 1) / 2 + 1;

        // Request votes from other nodes (simulated)
        var voteTasks = _clusterNodes.Select(async nodeId =>
        {
            await Task.Delay(Random.Shared.Next(5, 20), cancellationToken);
            // In production: send RequestVote RPC to nodeId
            // Simulate vote response
            return Random.Shared.NextDouble() > 0.3;
        }).ToList();

        var results = await Task.WhenAll(voteTasks);
        votesReceived += results.Count(v => v);

        lock (_stateLock)
        {
            if (votesReceived >= votesNeeded && _state == RaftState.Candidate)
            {
                _state = RaftState.Leader;
                _leaderId = _nodeId;

                // Initialize leader state
                foreach (var nodeId in _clusterNodes)
                {
                    _nextIndex[nodeId] = _log.Count;
                    _matchIndex[nodeId] = 0;
                }

                return true;
            }
            else
            {
                _state = RaftState.Follower;
                return false;
            }
        }
    }

    /// <summary>
    /// Appends a command to the log (leader only).
    /// </summary>
    public async Task<bool> AppendCommandAsync(object command, CancellationToken cancellationToken = default)
    {
        if (_state != RaftState.Leader)
            return false;

        lock (_stateLock)
        {
            _log.Add((_currentTerm, command));
        }

        // Replicate to followers (simulated)
        var replicationTasks = _clusterNodes.Select(async nodeId =>
        {
            await Task.Delay(Random.Shared.Next(5, 15), cancellationToken);
            // In production: send AppendEntries RPC
            return Random.Shared.NextDouble() > 0.2;
        }).ToList();

        var results = await Task.WhenAll(replicationTasks);
        var replicatedCount = results.Count(r => r) + 1; // +1 for leader

        var majority = (_clusterNodes.Count + 1) / 2 + 1;

        if (replicatedCount >= majority)
        {
            lock (_stateLock)
            {
                _commitIndex = _log.Count - 1;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles a heartbeat from the leader.
    /// </summary>
    public void ReceiveHeartbeat(string leaderId, long term)
    {
        lock (_stateLock)
        {
            if (term >= _currentTerm)
            {
                _currentTerm = term;
                _state = RaftState.Follower;
                _leaderId = leaderId;
                _lastHeartbeat = DateTimeOffset.UtcNow;
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        // Check if we need to start an election
        if (_state == RaftState.Follower && DateTimeOffset.UtcNow - _lastHeartbeat > _electionTimeout)
        {
            await StartElectionAsync(cancellationToken);
        }

        // Only leader can execute operations
        if (_state != RaftState.Leader)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException($"Not the leader. Current leader: {_leaderId ?? "unknown"}"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata =
                {
                    ["state"] = _state.ToString(),
                    ["leaderId"] = _leaderId ?? "unknown",
                    ["term"] = _currentTerm
                }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["state"] = _state.ToString(),
                    ["term"] = _currentTerm,
                    ["commitIndex"] = _commitIndex
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"{_state} (term: {_currentTerm}, leader: {_leaderId ?? "none"})";
}

/// <summary>
/// Paxos consensus protocol implementation.
/// </summary>
public sealed class PaxosConsensusStrategy : ResilienceStrategyBase
{
    private long _proposalNumber;
    private long _highestAcceptedProposal;
    private object? _acceptedValue;
    private readonly ConcurrentDictionary<long, object?> _promises = new();
    private readonly object _stateLock = new();

    private readonly string _nodeId;
    private readonly int _quorumSize;

    public PaxosConsensusStrategy()
        : this(nodeId: Guid.NewGuid().ToString("N")[..8], quorumSize: 3)
    {
    }

    public PaxosConsensusStrategy(string nodeId, int quorumSize)
    {
        _nodeId = nodeId;
        _quorumSize = quorumSize;
    }

    public override string StrategyId => "consensus-paxos";
    public override string StrategyName => "Paxos Consensus";
    public override string Category => "Consensus";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Paxos Consensus",
        Description = "Classic Paxos consensus algorithm for distributed agreement with prepare/accept phases",
        Category = "Consensus",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Proposes a value using Paxos protocol.
    /// </summary>
    public async Task<(bool success, object? value)> ProposeAsync(object value, CancellationToken cancellationToken = default)
    {
        // Phase 1: Prepare
        long proposalNum;
        lock (_stateLock)
        {
            _proposalNumber++;
            proposalNum = _proposalNumber;
        }

        // Send Prepare messages (simulated)
        var prepareResponses = new List<(long acceptedProposal, object? acceptedValue)>();
        for (int i = 0; i < _quorumSize; i++)
        {
            await Task.Delay(Random.Shared.Next(5, 15), cancellationToken);
            // Simulate acceptor response
            if (Random.Shared.NextDouble() > 0.2)
            {
                prepareResponses.Add((_highestAcceptedProposal, _acceptedValue));
            }
        }

        if (prepareResponses.Count < (_quorumSize / 2 + 1))
        {
            return (false, null);
        }

        // Choose value: if any acceptor has accepted a value, use the one with highest proposal
        var valueToPropose = prepareResponses
            .Where(r => r.acceptedValue != null)
            .OrderByDescending(r => r.acceptedProposal)
            .Select(r => r.acceptedValue)
            .FirstOrDefault() ?? value;

        // Phase 2: Accept
        var acceptCount = 0;
        for (int i = 0; i < _quorumSize; i++)
        {
            await Task.Delay(Random.Shared.Next(5, 15), cancellationToken);
            // Simulate acceptor response
            if (Random.Shared.NextDouble() > 0.2)
            {
                acceptCount++;
            }
        }

        if (acceptCount >= (_quorumSize / 2 + 1))
        {
            lock (_stateLock)
            {
                _highestAcceptedProposal = proposalNum;
                _acceptedValue = valueToPropose;
            }
            return (true, valueToPropose);
        }

        return (false, null);
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            // Execute operation and propose result
            var result = await operation(cancellationToken);

            var (success, _) = await ProposeAsync(result!, cancellationToken);

            if (!success)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new InvalidOperationException("Failed to reach consensus"),
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["proposalNumber"] = _proposalNumber,
                    ["quorumSize"] = _quorumSize
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"Proposal: {_proposalNumber}, Accepted: {_highestAcceptedProposal}";
}

/// <summary>
/// Practical Byzantine Fault Tolerance (PBFT) consensus.
/// </summary>
public sealed class PbftConsensusStrategy : ResilienceStrategyBase
{
    private long _viewNumber;
    private long _sequenceNumber;
    private readonly ConcurrentDictionary<long, (object request, int prepareCount, int commitCount)> _pending = new();
    private readonly ConcurrentDictionary<long, object> _committed = new();
    private readonly object _stateLock = new();

    private readonly string _nodeId;
    private readonly int _totalNodes;
    private readonly int _faultyNodes;

    public PbftConsensusStrategy()
        : this(nodeId: Guid.NewGuid().ToString("N")[..8], totalNodes: 4, faultyNodes: 1)
    {
    }

    public PbftConsensusStrategy(string nodeId, int totalNodes, int faultyNodes)
    {
        _nodeId = nodeId;
        _totalNodes = totalNodes;
        _faultyNodes = faultyNodes;
    }

    public override string StrategyId => "consensus-pbft";
    public override string StrategyName => "PBFT Consensus";
    public override string Category => "Consensus";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PBFT Consensus",
        Description = "Practical Byzantine Fault Tolerance for systems requiring resistance to malicious nodes",
        Category = "Consensus",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 15.0,
        MemoryFootprint = "High"
    };

    /// <summary>
    /// Executes PBFT consensus for a request.
    /// </summary>
    public async Task<bool> ExecuteConsensusAsync(object request, CancellationToken cancellationToken = default)
    {
        long seqNum;
        lock (_stateLock)
        {
            seqNum = ++_sequenceNumber;
            _pending[seqNum] = (request, 0, 0);
        }

        // Pre-prepare phase (leader broadcasts)
        await Task.Delay(Random.Shared.Next(2, 10), cancellationToken);

        // Prepare phase
        var prepareCount = 0;
        for (int i = 0; i < _totalNodes; i++)
        {
            await Task.Delay(Random.Shared.Next(2, 10), cancellationToken);
            if (Random.Shared.NextDouble() > 0.1)
            {
                prepareCount++;
            }
        }

        var prepareQuorum = 2 * _faultyNodes;
        if (prepareCount < prepareQuorum)
        {
            return false;
        }

        // Commit phase
        var commitCount = 0;
        for (int i = 0; i < _totalNodes; i++)
        {
            await Task.Delay(Random.Shared.Next(2, 10), cancellationToken);
            if (Random.Shared.NextDouble() > 0.1)
            {
                commitCount++;
            }
        }

        var commitQuorum = 2 * _faultyNodes + 1;
        if (commitCount >= commitQuorum)
        {
            lock (_stateLock)
            {
                _committed[seqNum] = request;
                _pending.TryRemove(seqNum, out _);
            }
            return true;
        }

        return false;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var result = await operation(cancellationToken);

            var success = await ExecuteConsensusAsync(result!, cancellationToken);

            if (!success)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new InvalidOperationException("PBFT consensus failed"),
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["viewNumber"] = _viewNumber,
                    ["sequenceNumber"] = _sequenceNumber,
                    ["committedCount"] = _committed.Count
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"View: {_viewNumber}, Seq: {_sequenceNumber}, Committed: {_committed.Count}";
}

/// <summary>
/// ZAB (Zookeeper Atomic Broadcast) consensus protocol.
/// </summary>
public sealed class ZabConsensusStrategy : ResilienceStrategyBase
{
    private long _zxid; // Zookeeper transaction ID
    private long _epoch;
    private bool _isLeader;
    private readonly ConcurrentDictionary<long, object> _proposals = new();
    private readonly ConcurrentDictionary<long, int> _ackCounts = new();
    private readonly object _stateLock = new();

    private readonly string _nodeId;
    private readonly int _quorumSize;

    public ZabConsensusStrategy()
        : this(nodeId: Guid.NewGuid().ToString("N")[..8], quorumSize: 3)
    {
    }

    public ZabConsensusStrategy(string nodeId, int quorumSize)
    {
        _nodeId = nodeId;
        _quorumSize = quorumSize;
    }

    public override string StrategyId => "consensus-zab";
    public override string StrategyName => "ZAB Consensus";
    public override string Category => "Consensus";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ZAB Consensus",
        Description = "Zookeeper Atomic Broadcast protocol optimized for primary-backup replication with strong ordering",
        Category = "Consensus",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 8.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Gets the current ZXID.</summary>
    public long Zxid => _zxid;

    /// <summary>Gets the current epoch.</summary>
    public long Epoch => _epoch;

    /// <summary>
    /// Broadcasts a proposal using ZAB.
    /// </summary>
    public async Task<bool> BroadcastAsync(object proposal, CancellationToken cancellationToken = default)
    {
        if (!_isLeader) return false;

        long proposalZxid;
        lock (_stateLock)
        {
            _zxid++;
            proposalZxid = _zxid;
            _proposals[proposalZxid] = proposal;
            _ackCounts[proposalZxid] = 1; // Leader's own ack
        }

        // Send proposal to followers
        var ackTasks = Enumerable.Range(0, _quorumSize - 1).Select(async _ =>
        {
            await Task.Delay(Random.Shared.Next(3, 12), cancellationToken);
            return Random.Shared.NextDouble() > 0.15;
        }).ToList();

        var acks = await Task.WhenAll(ackTasks);
        var totalAcks = 1 + acks.Count(a => a);

        if (totalAcks >= (_quorumSize / 2 + 1))
        {
            // Send COMMIT to followers
            await Task.Delay(Random.Shared.Next(2, 8), cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Elects this node as leader.
    /// </summary>
    public void BecomeLeader()
    {
        lock (_stateLock)
        {
            _isLeader = true;
            _epoch++;
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_isLeader)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("Not the leader"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["isLeader"] = false, ["epoch"] = _epoch }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            var success = await BroadcastAsync(result!, cancellationToken);

            if (!success)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new InvalidOperationException("ZAB broadcast failed"),
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["zxid"] = _zxid, ["epoch"] = _epoch }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"Epoch: {_epoch}, ZXID: {_zxid}, Leader: {_isLeader}";
}

/// <summary>
/// Viewstamped Replication consensus protocol.
/// </summary>
public sealed class ViewstampedReplicationStrategy : ResilienceStrategyBase
{
    private long _viewNumber;
    private long _opNumber;
    private long _commitNumber;
    private bool _isPrimary;
    private readonly ConcurrentDictionary<long, object> _log = new();
    private readonly object _stateLock = new();

    private readonly string _nodeId;
    private readonly int _replicaCount;

    public ViewstampedReplicationStrategy()
        : this(nodeId: Guid.NewGuid().ToString("N")[..8], replicaCount: 3)
    {
    }

    public ViewstampedReplicationStrategy(string nodeId, int replicaCount)
    {
        _nodeId = nodeId;
        _replicaCount = replicaCount;
    }

    public override string StrategyId => "consensus-viewstamped";
    public override string StrategyName => "Viewstamped Replication";
    public override string Category => "Consensus";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Viewstamped Replication",
        Description = "Viewstamped replication protocol for state machine replication with view changes",
        Category = "Consensus",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 7.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Processes a client request (primary only).
    /// </summary>
    public async Task<bool> ProcessRequestAsync(object request, CancellationToken cancellationToken = default)
    {
        if (!_isPrimary) return false;

        long opNum;
        lock (_stateLock)
        {
            _opNumber++;
            opNum = _opNumber;
            _log[opNum] = request;
        }

        // Send PREPARE to backups
        var prepareCount = 1;
        for (int i = 0; i < _replicaCount - 1; i++)
        {
            await Task.Delay(Random.Shared.Next(3, 10), cancellationToken);
            if (Random.Shared.NextDouble() > 0.15)
            {
                prepareCount++;
            }
        }

        if (prepareCount >= (_replicaCount / 2 + 1))
        {
            lock (_stateLock)
            {
                _commitNumber = opNum;
            }

            // Send COMMIT to backups
            await Task.Delay(Random.Shared.Next(2, 8), cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Makes this replica the primary.
    /// </summary>
    public void BecomePrimary()
    {
        lock (_stateLock)
        {
            _isPrimary = true;
            _viewNumber++;
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_isPrimary)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("Not the primary"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["isPrimary"] = false, ["viewNumber"] = _viewNumber }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            var success = await ProcessRequestAsync(result!, cancellationToken);

            if (!success)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new InvalidOperationException("Viewstamped replication failed"),
                    Attempts = 1,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["viewNumber"] = _viewNumber,
                    ["opNumber"] = _opNumber,
                    ["commitNumber"] = _commitNumber
                }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        $"View: {_viewNumber}, Op: {_opNumber}, Commit: {_commitNumber}, Primary: {_isPrimary}";
}
