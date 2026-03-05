using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Infrastructure.Distributed;
using DataWarehouse.SDK.Infrastructure.InMemory;
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
///   <item>Each group is managed by an SDK <see cref="RaftConsensusEngine"/> via <see cref="MultiRaftManager"/>.</item>
///   <item>Committed entries are applied to the group's state machine inside the engine.</item>
///   <item>Log compaction is handled per-group by the SDK engine.</item>
/// </list>
///
/// <para><strong>Strategies:</strong> Supports pluggable consensus algorithms via <see cref="IRaftStrategy"/>.
/// Full Raft is the production implementation backed by SDK RaftConsensusEngine;
/// Paxos, PBFT, and ZAB are independent in-process consensus implementations.</para>
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
    private MultiRaftManager? _multiRaft;
    private readonly InMemoryClusterMembership _membership;
    private readonly InMemoryP2PNetwork _network;
    private readonly ConsistentHash _groupHash;
    private readonly BoundedDictionary<string, IRaftStrategy> _strategies = new BoundedDictionary<string, IRaftStrategy>(1000);
    private readonly List<Action<Proposal>> _commitHandlers = new();
    private readonly object _handlerLock = new();
    private readonly string _nodeId;
    private readonly int _groupCount;
    private readonly int _votersPerGroup;

    // Per-group commit index tracker (SDK engine doesn't expose CommitIndex directly).
    // Each successful proposal increments the counter for that group.
    private long[] _perGroupCommitIndex = Array.Empty<long>();

    // LOW-2227: Election term counter — incremented each time we detect a new leader.
    // SDK MultiRaftGroupStatus does not expose the Raft election term directly, so this
    // approximates it via leader-election observations rather than using commitIndex as term.
    private long _electionTerm;
    private bool _lastLeaderState;

    private bool _disposed;
    private IRaftStrategy _activeStrategy;

    // Leader election constants for polling after StartAsync
    private const int LeaderPollIntervalMs = 20;
    private const int LeaderElectionTimeoutMs = 2000;

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
            if (_multiRaft == null) return false;
            // Leader in ANY group counts
            return _multiRaft.GetGroupStatuses().Values.Any(s => s.IsLeader);
        }
    }

    /// <summary>
    /// Gets the number of active Raft groups.
    /// </summary>
    public int GroupCount => _multiRaft?.GroupCount ?? 0;

    /// <summary>
    /// Creates a new Multi-Raft consensus plugin.
    /// </summary>
    /// <param name="groupCount">Number of Raft groups to create (default 3).</param>
    /// <param name="votersPerGroup">Number of voters per group (default 3, range 1-7).</param>
    public UltimateConsensusPlugin(int groupCount = 3, int votersPerGroup = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(votersPerGroup, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(votersPerGroup, 7);

        _groupCount = groupCount;
        _votersPerGroup = votersPerGroup;
        _nodeId = $"consensus-{Guid.NewGuid():N}"[..24];
        _groupHash = new ConsistentHash(groupCount);

        // DEV-ONLY / SINGLE-NODE: InMemoryClusterMembership and InMemoryP2PNetwork are
        // in-memory implementations suitable for development, testing, and single-node deployments.
        // InMemoryClusterMembership reports only the local node, so RaftConsensusEngine
        // immediately wins elections (no peers to contact). InMemoryP2PNetwork has no real
        // transport — sends to peers will throw InvalidOperationException.
        //
        // TODO (v6.0): For production multi-node consensus, inject real IClusterMembership
        // (e.g., etcd/Consul-backed) and IP2PNetwork (e.g., gRPC transport) implementations
        // via constructor parameters or a service locator pattern.
        _membership = new InMemoryClusterMembership(_nodeId);
        _network = new InMemoryP2PNetwork();

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

        // Initialize Raft groups via SDK MultiRaftManager
        await InitializeGroupsAsync().ConfigureAwait(false);

        response.Metadata["NodeId"] = _nodeId;
        response.Metadata["GroupCount"] = GroupCount.ToString();
        response.Metadata["ActiveStrategy"] = _activeStrategy.AlgorithmName;
        response.Metadata["VotersPerGroup"] = _votersPerGroup.ToString();

        return response;
    }

    /// <summary>
    /// Initializes all Raft groups using SDK MultiRaftManager with InMemoryClusterMembership
    /// and InMemoryP2PNetwork. Waits for leader election in at least one group.
    /// </summary>
    private async Task InitializeGroupsAsync()
    {
        // Fast election timeouts for local/single-node operation
        var config = new RaftConfiguration
        {
            ElectionTimeoutMinMs = 50,
            ElectionTimeoutMaxMs = 100,
            HeartbeatIntervalMs = 20,
            MaxLogEntries = 10000
        };

        _multiRaft = new MultiRaftManager(_membership, _network, config);
        _perGroupCommitIndex = new long[_groupCount];

        // Create a named group for each partition
        for (int i = 0; i < _groupCount; i++)
        {
            await _multiRaft.CreateGroupAsync($"group-{i}", config).ConfigureAwait(false);
        }

        // Poll until at least one group elects a leader (in single-node mode this is fast)
        await WaitForAnyLeaderAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Polls until at least one group has an elected leader or the timeout expires.
    /// </summary>
    private async Task WaitForAnyLeaderAsync()
    {
        if (_multiRaft == null) return;

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(LeaderElectionTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_multiRaft.GetGroupStatuses().Values.Any(s => s.IsLeader))
                return;
            await Task.Delay(LeaderPollIntervalMs).ConfigureAwait(false);
        }
        // Timeout: proceed anyway -- proposals will poll per-group on first call
    }

    #region ConsensusPluginBase Implementation

    /// <inheritdoc/>
    public override async Task<bool> ProposeAsync(Proposal proposal, CancellationToken cancellationToken = default)
    {
        var result = await ProposeAsync(proposal.Payload, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            NotifyCommitHandlers(proposal);
        }
        return result.Success;
    }

    /// <inheritdoc/>
    public override IDisposable OnCommit(Action<Proposal> handler)
    {
        lock (_handlerLock)
        {
            _commitHandlers.Add(handler);
        }
        return new CommitHandlerRegistration(_commitHandlers, _handlerLock, handler);
    }

    private sealed class CommitHandlerRegistration : IDisposable
    {
        private readonly List<Action<Proposal>> _handlers;
        private readonly object _lock;
        private readonly Action<Proposal> _handler;
        private int _disposed;

        public CommitHandlerRegistration(List<Action<Proposal>> handlers, object handlerLock, Action<Proposal> handler)
        {
            _handlers = handlers;
            _lock = handlerLock;
            _handler = handler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (_lock)
                {
                    _handlers.Remove(_handler);
                }
            }
        }
    }

    /// <inheritdoc/>
    public override Task<ClusterState> GetClusterStateAsync()
    {
        if (_multiRaft == null)
        {
            return Task.FromResult(new ClusterState
            {
                IsHealthy = false,
                LeaderId = null,
                NodeCount = 0,
                Term = 0
            });
        }

        var statuses = _multiRaft.GetGroupStatuses();
        var healthyGroups = statuses.Values.Count(s => s.IsHealthy);
        var leaderExists = statuses.Values.Any(s => s.IsLeader);

        // LOW-2227: Track election term via leader-state transitions rather than using
        // commitIndex as term (commitIndex ≠ Raft election term).
        if (leaderExists != _lastLeaderState)
        {
            _lastLeaderState = leaderExists;
            if (leaderExists)
                Interlocked.Increment(ref _electionTerm);
        }

        return Task.FromResult(new ClusterState
        {
            IsHealthy = healthyGroups == _groupCount,
            LeaderId = leaderExists ? _nodeId : null,
            NodeCount = _groupCount,
            Term = Interlocked.Read(ref _electionTerm)
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
    /// Delegates to the SDK RaftConsensusEngine via MultiRaftManager for real distributed consensus.
    /// </summary>
    /// <param name="data">Binary payload to propose.</param>
    /// <param name="groupId">Target Raft group ID (0-based integer index).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Consensus result with success flag, leader ID, and log index.</returns>
    public async Task<ConsensusResult> ProposeToGroupAsync(byte[] data, int groupId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_multiRaft == null)
        {
            return new ConsensusResult(false, null, 0, "MultiRaftManager not initialized. Call OnHandshakeAsync first.");
        }

        var groupKey = $"group-{groupId}";
        var engine = _multiRaft.GetGroup(groupKey);

        if (engine == null)
        {
            return new ConsensusResult(false, null, 0, $"Raft group {groupId} not found");
        }

        // If the engine hasn't yet elected a leader, wait briefly
        if (!engine.IsLeader)
        {
            await WaitForGroupLeaderAsync(engine, ct).ConfigureAwait(false);
        }

        if (!engine.IsLeader)
        {
            return new ConsensusResult(false, null, 0, $"Raft group {groupId} has no leader");
        }

        var proposal = new Proposal
        {
            Id = Guid.NewGuid().ToString(),
            Command = "propose",
            Payload = data
        };

        bool committed;
        try
        {
            committed = await engine.ProposeAsync(proposal).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new ConsensusResult(false, null, 0, ex.Message);
        }

        if (!committed)
        {
            return new ConsensusResult(false, _nodeId, 0, "Entry not committed by quorum");
        }

        // Track log index locally since SDK RaftConsensusEngine doesn't expose CommitIndex
        var logIndex = Interlocked.Increment(ref _perGroupCommitIndex[groupId]);

        return new ConsensusResult(true, _nodeId, logIndex, null);
    }

    /// <summary>
    /// Waits briefly for a specific engine to become leader.
    /// </summary>
    private static async Task WaitForGroupLeaderAsync(RaftConsensusEngine engine, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(LeaderElectionTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline && !engine.IsLeader && !ct.IsCancellationRequested)
        {
            await Task.Delay(LeaderPollIntervalMs, ct).ConfigureAwait(false);
        }
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

        long maxCommit = _perGroupCommitIndex.Length > 0 ? _perGroupCommitIndex.Max() : 0;
        string? leaderId = IsLeader ? _nodeId : null;

        return Task.FromResult(new ConsensusState(
            "multi-raft",
            leaderId,
            maxCommit,
            maxCommit));
    }

    /// <inheritdoc/>
    public override Task<ClusterHealthInfo> GetClusterHealthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_multiRaft == null)
        {
            return Task.FromResult(new ClusterHealthInfo(0, 0, new Dictionary<string, string>()));
        }

        var statuses = _multiRaft.GetGroupStatuses();
        var totalNodes = _groupCount;
        var healthyNodes = statuses.Values.Count(s => s.IsHealthy);
        var nodeStates = new Dictionary<string, string>();

        int idx = 0;
        foreach (var (gid, status) in statuses)
        {
            nodeStates[$"{gid}.leader"] = status.IsLeader ? _nodeId : "none";
            nodeStates[$"{gid}.healthy"] = status.IsHealthy.ToString();

            var commitIndex = (idx >= 0 && idx < _perGroupCommitIndex.Length)
                ? _perGroupCommitIndex[idx]
                : 0;
            nodeStates[$"{gid}.commitIndex"] = commitIndex.ToString();
            idx++;
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
        message.Payload["groupCount"] = GroupCount;
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
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        // Restore persisted commit indices
        var commitData = await LoadStateAsync("commitIndices", ct);
        if (commitData != null && _perGroupCommitIndex.Length > 0)
        {
            try
            {
                var indices = System.Text.Json.JsonSerializer.Deserialize<long[]>(commitData);
                if (indices != null && indices.Length == _perGroupCommitIndex.Length)
                    Array.Copy(indices, _perGroupCommitIndex, indices.Length);
            }
            catch { /* corrupted state — start fresh */ }
        }

        // Restore active strategy
        var stratData = await LoadStateAsync("activeStrategy", ct);
        if (stratData != null)
        {
            try
            {
                var stratName = System.Text.Encoding.UTF8.GetString(stratData);
                if (_strategies.TryGetValue(stratName, out var strategy))
                    _activeStrategy = strategy;
            }
            catch { /* corrupted state — use default */ }
        }
    }

    /// <inheritdoc/>
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct)
    {
        if (_perGroupCommitIndex.Length > 0)
        {
            var commitBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_perGroupCommitIndex);
            await SaveStateAsync("commitIndices", commitBytes, ct);
        }

        await SaveStateAsync("activeStrategy",
            System.Text.Encoding.UTF8.GetBytes(_activeStrategy.AlgorithmName), ct);
    }

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
        metadata["GroupCount"] = GroupCount;
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
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
    }

    /// <summary>
    /// Disposes resources including the MultiRaftManager and all Raft group engines.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _multiRaft?.Dispose();
            _strategies.Clear();
        }
        base.Dispose(disposing);
    }
}
