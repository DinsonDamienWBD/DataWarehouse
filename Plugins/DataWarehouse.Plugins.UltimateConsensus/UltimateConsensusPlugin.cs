using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConsensus;

/// <summary>
/// Ultimate Consensus plugin implementing Multi-Raft architecture.
///
/// <para>Multiple independent Raft groups run in parallel, each managing a subset of keys
/// via consistent hashing. This provides:</para>
/// <list type="bullet">
///   <item>Higher throughput: proposals to different groups are processed concurrently.</item>
///   <item>Fault isolation: leader failure in one group does not affect others.</item>
///   <item>Scalable consensus: add groups to scale without increasing per-group latency.</item>
/// </list>
///
/// <para><strong>Architecture:</strong></para>
/// <list type="number">
///   <item>Incoming proposals are hashed to a Raft group via <see cref="ConsistentHash"/>.</item>
///   <item>Each <see cref="RaftGroup"/> independently elects a leader and replicates its log.</item>
///   <item>Committed entries are applied to the group's local state machine.</item>
///   <item>Snapshots compact the log per-group for bounded memory usage.</item>
/// </list>
///
/// <para><strong>Strategies:</strong> Supports pluggable consensus algorithms via <see cref="IRaftStrategy"/>.
/// Full Raft is the production implementation; Paxos, PBFT, and ZAB stubs are provided for
/// interface compliance (future implementation).</para>
///
/// <para><strong>Message Bus Topics:</strong></para>
/// <list type="bullet">
///   <item><c>consensus.propose</c>: Submit a proposal to the cluster.</item>
///   <item><c>consensus.status</c>: Query cluster and group status.</item>
///   <item><c>consensus.health</c>: Query cluster health.</item>
///   <item><c>consensus.leader.elected</c>: Published when a leader is elected in any group.</item>
/// </list>
/// </summary>
public sealed class UltimateConsensusPlugin : ConsensusPluginBase, IDisposable
{
    private readonly BoundedDictionary<int, RaftGroup> _raftGroups = new BoundedDictionary<int, RaftGroup>(1000);
    private readonly ConsistentHash _groupHash;
    private readonly BoundedDictionary<string, IRaftStrategy> _strategies = new BoundedDictionary<string, IRaftStrategy>(1000);
    private readonly List<Action<Proposal>> _commitHandlers = new();
    private readonly object _handlerLock = new();
    private readonly string _nodeId;
    private readonly int _groupCount;
    private readonly int _votersPerGroup;
    private bool _disposed;
    private IRaftStrategy _activeStrategy;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.consensus.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Consensus";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <inheritdoc/>
    public override bool IsLeader
    {
        get
        {
            // Leader in ANY group counts
            return _raftGroups.Values.Any(g => g.IsLeader);
        }
    }

    /// <summary>
    /// Gets the number of active Raft groups.
    /// </summary>
    public int GroupCount => _raftGroups.Count;

    /// <summary>
    /// Creates a new Multi-Raft consensus plugin.
    /// </summary>
    /// <param name="groupCount">Number of Raft groups to create (default 3).</param>
    /// <param name="votersPerGroup">Number of voters per group (default 3, range 3-5).</param>
    public UltimateConsensusPlugin(int groupCount = 3, int votersPerGroup = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(votersPerGroup, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(votersPerGroup, 7);

        _groupCount = groupCount;
        _votersPerGroup = votersPerGroup;
        _nodeId = $"consensus-{Guid.NewGuid():N}"[..24];
        _groupHash = new ConsistentHash(groupCount);

        // Register strategies
        _activeStrategy = new RaftStrategy(this);
        _strategies["Raft"] = _activeStrategy;
        _strategies["Paxos"] = new PaxosStrategy();
        _strategies["PBFT"] = new PbftStrategy();
        _strategies["ZAB"] = new ZabStrategy();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        // Initialize Raft groups
        await InitializeGroupsAsync().ConfigureAwait(false);

        response.Metadata["NodeId"] = _nodeId;
        response.Metadata["GroupCount"] = _raftGroups.Count.ToString();
        response.Metadata["ActiveStrategy"] = _activeStrategy.AlgorithmName;
        response.Metadata["VotersPerGroup"] = _votersPerGroup.ToString();

        return response;
    }

    /// <summary>
    /// Initializes all Raft groups with leader election.
    /// </summary>
    private async Task InitializeGroupsAsync()
    {
        for (int i = 0; i < _groupCount; i++)
        {
            var voterIds = GenerateVoterIds(i);
            var group = new RaftGroup(i, _nodeId, voterIds);
            _raftGroups[i] = group;

            // Start leader election
            await group.ElectLeaderAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generates voter IDs for a group. In single-node mode, the local node is the only voter.
    /// In distributed mode, voter IDs come from cluster membership.
    /// </summary>
    private List<string> GenerateVoterIds(int groupId)
    {
        var voters = new List<string> { _nodeId };
        for (int i = 1; i < _votersPerGroup; i++)
        {
            voters.Add($"{_nodeId}-voter-{groupId}-{i}");
        }
        return voters;
    }

    #region ConsensusPluginBase Implementation

    /// <inheritdoc/>
    public override async Task<bool> ProposeAsync(Proposal proposal)
    {
        var result = await ProposeAsync(proposal.Payload, CancellationToken.None).ConfigureAwait(false);
        if (result.Success)
        {
            NotifyCommitHandlers(proposal);
        }
        return result.Success;
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
        var healthyGroups = _raftGroups.Values.Count(g => g.CurrentLeader != null);
        var leaderGroup = _raftGroups.Values.FirstOrDefault(g => g.IsLeader);

        return Task.FromResult(new ClusterState
        {
            IsHealthy = healthyGroups == _raftGroups.Count,
            LeaderId = leaderGroup?.CurrentLeader,
            NodeCount = _raftGroups.Values.Sum(g => g.VoterIds.Count),
            Term = _raftGroups.Values.Max(g => g.CurrentTerm)
        });
    }

    #endregion

    #region Multi-Raft Extensions

    /// <inheritdoc/>
    public override async Task<ConsensusResult> ProposeAsync(byte[] data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Route to appropriate group via consistent hash
        int groupId = _groupHash.Route(data);
        return await ProposeToGroupAsync(data, groupId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Proposes data to a specific Raft group. Used by <see cref="IRaftStrategy"/> implementations.
    /// </summary>
    /// <param name="data">Binary payload to propose.</param>
    /// <param name="groupId">Target Raft group ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Consensus result.</returns>
    public async Task<ConsensusResult> ProposeToGroupAsync(byte[] data, int groupId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_raftGroups.TryGetValue(groupId, out var group))
        {
            return new ConsensusResult(false, null, 0, $"Raft group {groupId} not found");
        }

        var entry = group.CreateEntry(data);
        var committed = await group.AppendEntryAsync(entry).ConfigureAwait(false);

        if (committed)
        {
            await group.ApplyCommittedEntriesAsync().ConfigureAwait(false);
        }

        return new ConsensusResult(
            committed,
            group.CurrentLeader,
            entry.Index,
            committed ? null : "Entry not committed by quorum");
    }

    /// <inheritdoc/>
    public override Task<bool> IsLeaderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IsLeader);
    }

    /// <inheritdoc/>
    public override Task<ConsensusState> GetStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var leaderGroup = _raftGroups.Values.FirstOrDefault(g => g.IsLeader);
        var maxCommit = _raftGroups.Values.Any() ? _raftGroups.Values.Max(g => g.CommitIndex) : 0;
        var maxApplied = _raftGroups.Values.Any() ? _raftGroups.Values.Max(g => g.LastApplied) : 0;

        return Task.FromResult(new ConsensusState(
            "multi-raft",
            leaderGroup?.CurrentLeader,
            maxCommit,
            maxApplied));
    }

    /// <inheritdoc/>
    public override Task<ClusterHealthInfo> GetClusterHealthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var totalNodes = _raftGroups.Values.Sum(g => g.VoterIds.Count);
        var healthyNodes = _raftGroups.Values.Count(g => g.CurrentLeader != null) * _votersPerGroup;
        var nodeStates = new Dictionary<string, string>();

        foreach (var group in _raftGroups.Values)
        {
            foreach (var kvp in group.GetHealthSummary())
            {
                nodeStates[kvp.Key] = kvp.Value;
            }
        }

        return Task.FromResult(new ClusterHealthInfo(totalNodes, healthyNodes, nodeStates));
    }

    #endregion

    #region Strategy Factory

    /// <summary>
    /// Creates or retrieves a consensus strategy by name.
    /// </summary>
    /// <param name="strategyName">Strategy name ("Raft", "Paxos", "PBFT", "ZAB").</param>
    /// <returns>The requested strategy, or null if not found.</returns>
    public IRaftStrategy? CreateStrategy(string strategyName)
    {
        return _strategies.TryGetValue(strategyName, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Sets the active consensus strategy for future proposals.
    /// </summary>
    /// <param name="strategyName">Name of the strategy to activate.</param>
    /// <returns>True if the strategy was found and activated.</returns>
    public bool SetActiveStrategy(string strategyName)
    {
        if (_strategies.TryGetValue(strategyName, out var strategy))
        {
            _activeStrategy = strategy;
            return true;
        }
        return false;
    }

    #endregion

    #region Message Handling

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "consensus.propose":
                await HandleProposeMessageAsync(message).ConfigureAwait(false);
                break;
            case "consensus.status":
                await HandleStatusMessageAsync(message).ConfigureAwait(false);
                break;
            case "consensus.health":
                await HandleHealthMessageAsync(message).ConfigureAwait(false);
                break;
            default:
                await base.OnMessageAsync(message).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleProposeMessageAsync(PluginMessage message)
    {
        var data = Array.Empty<byte>();
        if (message.Payload.TryGetValue("data", out var dataObj) && dataObj is string base64)
        {
            data = Convert.FromBase64String(base64);
        }

        var result = await ProposeAsync(data, CancellationToken.None).ConfigureAwait(false);

        message.Payload["success"] = result.Success;
        message.Payload["leader"] = result.LeaderId ?? "";
        message.Payload["logIndex"] = result.LogIndex;
        if (result.Error != null)
        {
            message.Payload["error"] = result.Error;
        }
    }

    private async Task HandleStatusMessageAsync(PluginMessage message)
    {
        var state = await GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        message.Payload["state"] = state.State;
        message.Payload["leader"] = state.LeaderId ?? "";
        message.Payload["commitIndex"] = state.CommitIndex;
        message.Payload["lastApplied"] = state.LastApplied;
        message.Payload["groupCount"] = _raftGroups.Count;
        message.Payload["activeStrategy"] = _activeStrategy.AlgorithmName;
    }

    private async Task HandleHealthMessageAsync(PluginMessage message)
    {
        var health = await GetClusterHealthAsync(CancellationToken.None).ConfigureAwait(false);

        message.Payload["totalNodes"] = health.TotalNodes;
        message.Payload["healthyNodes"] = health.HealthyNodes;
        message.Payload["nodeStates"] = health.NodeStates;
    }

    #endregion

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "consensus.propose", DisplayName = "Propose", Description = "Propose data to Multi-Raft consensus" },
            new() { Name = "consensus.status", DisplayName = "Status", Description = "Get Multi-Raft cluster status" },
            new() { Name = "consensus.health", DisplayName = "Health", Description = "Get Multi-Raft cluster health" },
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ConsensusAlgorithm"] = "Multi-Raft";
        metadata["NodeId"] = _nodeId;
        metadata["GroupCount"] = _raftGroups.Count;
        metadata["VotersPerGroup"] = _votersPerGroup;
        metadata["ActiveStrategy"] = _activeStrategy.AlgorithmName;
        metadata["AvailableStrategies"] = string.Join(", ", _strategies.Keys);
        return metadata;
    }

    private void NotifyCommitHandlers(Proposal proposal)
    {
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
                // Individual handler failure should not block other handlers
            }
        }
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _raftGroups.Clear();
            _strategies.Clear();
        }
        base.Dispose(disposing);
    }
}
