using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UniversalFabric.Scaling;

/// <summary>
/// Manages DataFabric subsystem scaling with runtime-configurable <c>MaxNodes</c> per topology,
/// dynamic topology switching based on node count thresholds, and health-based node management
/// with automatic discovery and removal of failed nodes.
/// Implements <see cref="IScalableSubsystem"/> to participate in the unified scaling infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runtime-configurable MaxNodes:</b> Replaces static Star=1000/Mesh=500/Federated=10000 limits
/// with a <see cref="BoundedCache{TKey,TValue}"/> mapping topology type to max nodes. Changes via
/// <see cref="ReconfigureLimitsAsync"/> take effect immediately without restart.
/// </para>
/// <para>
/// <b>Dynamic topology switching:</b> Monitors node count against configurable thresholds. When
/// Star topology exceeds its threshold, auto-switches (or recommends switching) to Mesh. When Mesh
/// exceeds its threshold, switches to Federated. Switching mode is configurable (automatic or
/// recommendation-only).
/// </para>
/// <para>
/// <b>Health-based node management:</b> Monitors node health via heartbeat with configurable
/// interval (default 15s). Nodes that miss N consecutive heartbeats (default 3) are auto-removed.
/// Discovered nodes are auto-added. Health state is tracked in a <see cref="BoundedCache{TKey,TValue}"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Fabric scaling with dynamic topology and health-based node management")]
public sealed class FabricScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Constants ----
    private const string SubsystemName = "UniversalFabric";
    private const int DefaultStarMaxNodes = 1_000;
    private const int DefaultMeshMaxNodes = 500;
    private const int DefaultFederatedMaxNodes = 10_000;
    private const int DefaultHeartbeatIntervalMs = 15_000;
    private const int DefaultMissedHeartbeatsThreshold = 3;
    private const int DefaultStarToMeshThreshold = 800;
    private const int DefaultMeshToFederatedThreshold = 1600;

    // ---- Configuration ----
    private volatile ScalingLimits _currentLimits;
    private volatile FabricTopology _currentTopology;
    private volatile bool _autoSwitchEnabled;
    private volatile int _heartbeatIntervalMs;
    private volatile int _missedHeartbeatsThreshold;
    private volatile int _starToMeshThreshold;
    private volatile int _meshToFederatedThreshold;

    // ---- Topology MaxNodes cache ----
    private readonly BoundedCache<string, int> _topologyMaxNodes;

    // ---- Node health tracking ----
    private readonly BoundedCache<string, NodeHealth> _nodeHealth;
    private readonly Timer _heartbeatTimer;
    private readonly object _nodeLock = new();

    // ---- Topology switch recommendations ----
    private readonly BoundedCache<string, TopologySwitchRecommendation> _switchRecommendations;

    // ---- Metrics ----
    private long _nodesAdded;
    private long _nodesRemoved;
    private long _heartbeatsSent;
    private long _heartbeatsMissed;
    private long _topologySwitches;
    private long _activeNodeCount;

    // ---- Backpressure ----
    private volatile BackpressureState _backpressureState = BackpressureState.Normal;

    // ---- Events ----
    /// <summary>
    /// Raised when a topology switch is recommended or automatically performed.
    /// </summary>
    public event Action<TopologySwitchRecommendation>? OnTopologySwitchRecommended;

    /// <summary>
    /// Raised when a node is removed due to missed heartbeats.
    /// </summary>
    public event Action<string, NodeHealth>? OnNodeRemoved;

    /// <summary>
    /// Raised when a new node is discovered and added.
    /// </summary>
    public event Action<string>? OnNodeDiscovered;

    // ---- Disposal ----
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="FabricScalingManager"/> with configurable topology limits,
    /// heartbeat monitoring, and topology switching behavior.
    /// </summary>
    /// <param name="initialTopology">The initial topology type. Default: Star.</param>
    /// <param name="initialLimits">Optional initial scaling limits. Uses defaults if null.</param>
    /// <param name="autoSwitchEnabled">Whether topology switching is automatic. Default: true.</param>
    /// <param name="heartbeatIntervalMs">Heartbeat interval in milliseconds. Default: 15000.</param>
    /// <param name="missedHeartbeatsThreshold">Missed heartbeats before node removal. Default: 3.</param>
    public FabricScalingManager(
        FabricTopology initialTopology = FabricTopology.Star,
        ScalingLimits? initialLimits = null,
        bool autoSwitchEnabled = true,
        int heartbeatIntervalMs = DefaultHeartbeatIntervalMs,
        int missedHeartbeatsThreshold = DefaultMissedHeartbeatsThreshold)
    {
        _currentTopology = initialTopology;
        _autoSwitchEnabled = autoSwitchEnabled;
        _heartbeatIntervalMs = heartbeatIntervalMs;
        _missedHeartbeatsThreshold = missedHeartbeatsThreshold;
        _starToMeshThreshold = DefaultStarToMeshThreshold;
        _meshToFederatedThreshold = DefaultMeshToFederatedThreshold;

        _currentLimits = initialLimits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxMemoryBytes: 256 * 1024 * 1024,
            MaxConcurrentOperations: 64,
            MaxQueueDepth: 1_000);

        // Initialize topology max nodes cache with defaults
        var topologyCacheOptions = new BoundedCacheOptions<string, int>
        {
            MaxEntries = 100,
            EvictionPolicy = CacheEvictionMode.LRU
        };
        _topologyMaxNodes = new BoundedCache<string, int>(topologyCacheOptions);
        _topologyMaxNodes.Put(FabricTopology.Star.ToString(), DefaultStarMaxNodes);
        _topologyMaxNodes.Put(FabricTopology.Mesh.ToString(), DefaultMeshMaxNodes);
        _topologyMaxNodes.Put(FabricTopology.Federated.ToString(), DefaultFederatedMaxNodes);

        // Initialize node health cache
        var healthCacheOptions = new BoundedCacheOptions<string, NodeHealth>
        {
            MaxEntries = 50_000,
            EvictionPolicy = CacheEvictionMode.LRU
        };
        _nodeHealth = new BoundedCache<string, NodeHealth>(healthCacheOptions);

        // Initialize switch recommendation cache
        var recCacheOptions = new BoundedCacheOptions<string, TopologySwitchRecommendation>
        {
            MaxEntries = 100,
            EvictionPolicy = CacheEvictionMode.TTL,
            DefaultTtl = TimeSpan.FromHours(1)
        };
        _switchRecommendations = new BoundedCache<string, TopologySwitchRecommendation>(recCacheOptions);

        // Start heartbeat monitor timer
        _heartbeatTimer = new Timer(
            HeartbeatMonitorCallback,
            null,
            TimeSpan.FromMilliseconds(_heartbeatIntervalMs),
            TimeSpan.FromMilliseconds(_heartbeatIntervalMs));
    }

    // ---------------------------------------------------------------
    // IScalableSubsystem
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var maxNodesForTopology = GetMaxNodesForTopology(_currentTopology);
        var metrics = new Dictionary<string, object>
        {
            ["topology.current"] = _currentTopology.ToString(),
            ["topology.maxNodes"] = maxNodesForTopology,
            ["topology.activeNodes"] = Interlocked.Read(ref _activeNodeCount),
            ["topology.switches"] = Interlocked.Read(ref _topologySwitches),
            ["topology.autoSwitch"] = _autoSwitchEnabled,
            ["nodes.added"] = Interlocked.Read(ref _nodesAdded),
            ["nodes.removed"] = Interlocked.Read(ref _nodesRemoved),
            ["nodes.healthyCount"] = CountHealthyNodes(),
            ["nodes.unhealthyCount"] = CountUnhealthyNodes(),
            ["heartbeat.sent"] = Interlocked.Read(ref _heartbeatsSent),
            ["heartbeat.missed"] = Interlocked.Read(ref _heartbeatsMissed),
            ["heartbeat.intervalMs"] = _heartbeatIntervalMs,
            ["heartbeat.missedThreshold"] = _missedHeartbeatsThreshold,
            ["config.starMaxNodes"] = GetMaxNodesForTopology(FabricTopology.Star),
            ["config.meshMaxNodes"] = GetMaxNodesForTopology(FabricTopology.Mesh),
            ["config.federatedMaxNodes"] = GetMaxNodesForTopology(FabricTopology.Federated),
            ["backpressure.state"] = _backpressureState.ToString(),
            // P2-4578: Report actual active-node count under a correctly-named key
            ["backpressure.activeNodeCount"] = Interlocked.Read(ref _activeNodeCount)
        };
        return metrics;
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);

        _currentLimits = limits;

        // Reconfigure topology max nodes from concurrent operations limit
        if (limits.MaxConcurrentOperations > 0)
        {
            SetMaxNodesForTopology(_currentTopology, limits.MaxConcurrentOperations);
        }

        UpdateBackpressureState();
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState => _backpressureState;

    // ---------------------------------------------------------------
    // Runtime-Configurable MaxNodes
    // ---------------------------------------------------------------

    /// <summary>
    /// Gets the maximum number of nodes allowed for the specified topology.
    /// </summary>
    /// <param name="topology">The topology type.</param>
    /// <returns>The maximum node count for the topology.</returns>
    public int GetMaxNodesForTopology(FabricTopology topology)
    {
        var maxNodes = _topologyMaxNodes.GetOrDefault(topology.ToString());
        return maxNodes > 0 ? maxNodes : topology switch
        {
            FabricTopology.Star => DefaultStarMaxNodes,
            FabricTopology.Mesh => DefaultMeshMaxNodes,
            FabricTopology.Federated => DefaultFederatedMaxNodes,
            _ => DefaultStarMaxNodes
        };
    }

    /// <summary>
    /// Sets the maximum number of nodes for a specific topology type.
    /// Changes take effect immediately.
    /// </summary>
    /// <param name="topology">The topology type to configure.</param>
    /// <param name="maxNodes">The new maximum node count.</param>
    public void SetMaxNodesForTopology(FabricTopology topology, int maxNodes)
    {
        if (maxNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxNodes), "Max nodes must be at least 1.");

        _topologyMaxNodes.Put(topology.ToString(), maxNodes);
    }

    /// <summary>
    /// Gets the current active topology.
    /// </summary>
    public FabricTopology CurrentTopology => _currentTopology;

    // ---------------------------------------------------------------
    // Dynamic Topology Switching
    // ---------------------------------------------------------------

    /// <summary>
    /// Configures the thresholds for automatic topology switching.
    /// </summary>
    /// <param name="starToMeshThreshold">Node count threshold to trigger Star-to-Mesh switch.</param>
    /// <param name="meshToFederatedThreshold">Node count threshold to trigger Mesh-to-Federated switch.</param>
    /// <param name="autoSwitch">Whether switching is automatic or recommendation-only.</param>
    public void ConfigureTopologySwitching(
        int starToMeshThreshold = DefaultStarToMeshThreshold,
        int meshToFederatedThreshold = DefaultMeshToFederatedThreshold,
        bool autoSwitch = true)
    {
        _starToMeshThreshold = starToMeshThreshold;
        _meshToFederatedThreshold = meshToFederatedThreshold;
        _autoSwitchEnabled = autoSwitch;
    }

    /// <summary>
    /// Forces a topology switch to the specified topology type.
    /// </summary>
    /// <param name="targetTopology">The target topology to switch to.</param>
    public void SwitchTopology(FabricTopology targetTopology)
    {
        TopologySwitchRecommendation recommendation;
        lock (_nodeLock)
        {
            var previous = _currentTopology;
            _currentTopology = targetTopology;
            Interlocked.Increment(ref _topologySwitches);

            recommendation = new TopologySwitchRecommendation
            {
                FromTopology = previous,
                ToTopology = targetTopology,
                Reason = "Manual topology switch",
                NodeCount = (int)Interlocked.Read(ref _activeNodeCount),
                TimestampUtc = DateTime.UtcNow,
                WasAutomatic = false
            };
        }
        _switchRecommendations.Put($"switch-{DateTime.UtcNow.Ticks}", recommendation);
    }

    // ---------------------------------------------------------------
    // Health-Based Node Management
    // ---------------------------------------------------------------

    /// <summary>
    /// Registers a heartbeat from a node, updating its health status.
    /// Automatically adds the node if it's not already tracked.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <param name="metadata">Optional metadata about the node (e.g., region, role).</param>
    public void RecordHeartbeat(string nodeId, IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("Node ID must not be empty.", nameof(nodeId));
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _nodeHealth.GetOrDefault(nodeId);
        var isNew = existing == null;

        var health = new NodeHealth
        {
            NodeId = nodeId,
            IsHealthy = true,
            LastHeartbeatUtc = DateTime.UtcNow,
            MissedHeartbeats = 0,
            Metadata = metadata ?? existing?.Metadata ?? new Dictionary<string, string>(),
            JoinedUtc = existing?.JoinedUtc ?? DateTime.UtcNow
        };

        _nodeHealth.Put(nodeId, health);
        Interlocked.Increment(ref _heartbeatsSent);

        if (isNew)
        {
            Interlocked.Increment(ref _nodesAdded);
            Interlocked.Increment(ref _activeNodeCount);
            OnNodeDiscovered?.Invoke(nodeId);

            // Evaluate topology switch after node addition
            EvaluateTopologySwitch();
        }
    }

    /// <summary>
    /// Gets the current health status of a specific node.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <returns>The node health info if tracked; otherwise null.</returns>
    public NodeHealth? GetNodeHealth(string nodeId)
    {
        return _nodeHealth.GetOrDefault(nodeId);
    }

    /// <summary>
    /// Gets the current count of active (healthy) nodes.
    /// </summary>
    public int ActiveNodeCount => (int)Interlocked.Read(ref _activeNodeCount);

    /// <summary>
    /// Configures the heartbeat monitoring parameters.
    /// </summary>
    /// <param name="intervalMs">Heartbeat check interval in milliseconds.</param>
    /// <param name="missedThreshold">Number of missed heartbeats before node removal.</param>
    public void ConfigureHeartbeat(int intervalMs, int missedThreshold)
    {
        if (intervalMs < 100)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "Interval must be at least 100ms.");
        if (missedThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(missedThreshold), "Threshold must be at least 1.");

        _heartbeatIntervalMs = intervalMs;
        _missedHeartbeatsThreshold = missedThreshold;

        // Restart timer with new interval
        _heartbeatTimer.Change(
            TimeSpan.FromMilliseconds(_heartbeatIntervalMs),
            TimeSpan.FromMilliseconds(_heartbeatIntervalMs));
    }

    // ---------------------------------------------------------------
    // Private Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Heartbeat monitor callback that checks all tracked nodes for missed heartbeats
    /// and auto-removes nodes that exceed the threshold.
    /// </summary>
    private void HeartbeatMonitorCallback(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var heartbeatTimeout = TimeSpan.FromMilliseconds(_heartbeatIntervalMs * 1.5);
        var nodesToRemove = new List<(string NodeId, NodeHealth Health)>();

        foreach (var entry in _nodeHealth)
        {
            var health = entry.Value;
            if ((now - health.LastHeartbeatUtc) > heartbeatTimeout)
            {
                // Increment missed heartbeats
                var updatedHealth = health with
                {
                    MissedHeartbeats = health.MissedHeartbeats + 1,
                    IsHealthy = health.MissedHeartbeats + 1 < _missedHeartbeatsThreshold
                };

                if (updatedHealth.MissedHeartbeats >= _missedHeartbeatsThreshold)
                {
                    nodesToRemove.Add((entry.Key, updatedHealth));
                }
                else
                {
                    _nodeHealth.Put(entry.Key, updatedHealth);
                }

                Interlocked.Increment(ref _heartbeatsMissed);
            }
        }

        // Remove failed nodes
        foreach (var (nodeId, health) in nodesToRemove)
        {
            _nodeHealth.TryRemove(nodeId, out _);
            Interlocked.Increment(ref _nodesRemoved);
            Interlocked.Decrement(ref _activeNodeCount);
            OnNodeRemoved?.Invoke(nodeId, health);
        }

        if (nodesToRemove.Count > 0)
        {
            UpdateBackpressureState();
        }
    }

    /// <summary>
    /// Evaluates whether a topology switch is needed based on current node count
    /// and configured thresholds.
    /// </summary>
    private void EvaluateTopologySwitch()
    {
        var nodeCount = (int)Interlocked.Read(ref _activeNodeCount);
        FabricTopology? targetTopology = null;
        string? reason = null;

        if (_currentTopology == FabricTopology.Star && nodeCount >= _starToMeshThreshold)
        {
            targetTopology = FabricTopology.Mesh;
            reason = $"Star topology exceeded threshold ({nodeCount} >= {_starToMeshThreshold} nodes)";
        }
        else if (_currentTopology == FabricTopology.Mesh && nodeCount >= _meshToFederatedThreshold)
        {
            targetTopology = FabricTopology.Federated;
            reason = $"Mesh topology exceeded threshold ({nodeCount} >= {_meshToFederatedThreshold} nodes)";
        }

        if (targetTopology.HasValue)
        {
            var recommendation = new TopologySwitchRecommendation
            {
                FromTopology = _currentTopology,
                ToTopology = targetTopology.Value,
                Reason = reason!,
                NodeCount = nodeCount,
                TimestampUtc = DateTime.UtcNow,
                WasAutomatic = _autoSwitchEnabled
            };

            if (_autoSwitchEnabled)
            {
                _currentTopology = targetTopology.Value;
                Interlocked.Increment(ref _topologySwitches);
            }

            _switchRecommendations.Put($"switch-{DateTime.UtcNow.Ticks}", recommendation);
            OnTopologySwitchRecommended?.Invoke(recommendation);
        }
    }

    /// <summary>
    /// Counts nodes currently marked as healthy.
    /// </summary>
    private int CountHealthyNodes()
    {
        return _nodeHealth.Count(entry => entry.Value.IsHealthy);
    }

    /// <summary>
    /// Counts nodes currently marked as unhealthy.
    /// </summary>
    private int CountUnhealthyNodes()
    {
        return _nodeHealth.Count(entry => !entry.Value.IsHealthy);
    }

    /// <summary>
    /// Updates the backpressure state based on node count vs topology max nodes.
    /// </summary>
    private void UpdateBackpressureState()
    {
        var maxNodes = GetMaxNodesForTopology(_currentTopology);
        var nodeCount = (int)Interlocked.Read(ref _activeNodeCount);

        if (maxNodes <= 0)
        {
            _backpressureState = BackpressureState.Normal;
            return;
        }

        var ratio = (double)nodeCount / maxNodes;
        _backpressureState = ratio switch
        {
            >= 1.0 => BackpressureState.Shedding,
            >= 0.8 => BackpressureState.Critical,
            >= 0.6 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }

    // ---------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _heartbeatTimer.Dispose();
        _topologyMaxNodes.Dispose();
        _nodeHealth.Dispose();
        _switchRecommendations.Dispose();
    }
}

// ---------------------------------------------------------------
// Supporting Types
// ---------------------------------------------------------------

/// <summary>
/// Supported DataFabric topology types that determine node interconnection patterns
/// and scaling characteristics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Fabric topology enumeration")]
public enum FabricTopology
{
    /// <summary>
    /// Star topology with a central coordinator node. Best for small clusters
    /// (&lt;1000 nodes). Simple but coordinator is a single point of failure.
    /// </summary>
    Star,

    /// <summary>
    /// Mesh topology with peer-to-peer connections. Best for medium clusters
    /// (&lt;500 nodes). No single point of failure but O(N^2) connections.
    /// </summary>
    Mesh,

    /// <summary>
    /// Federated topology with hierarchical zones. Best for large clusters
    /// (&lt;10000 nodes). Balances scalability with manageable connection count.
    /// </summary>
    Federated
}

/// <summary>
/// Tracks the health status of a node in the DataFabric topology, including
/// heartbeat timing, missed heartbeat count, and optional metadata.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Node health tracking for fabric topology")]
public sealed record NodeHealth
{
    /// <summary>Gets the unique identifier of the node.</summary>
    public required string NodeId { get; init; }

    /// <summary>Gets whether the node is currently considered healthy.</summary>
    public required bool IsHealthy { get; init; }

    /// <summary>Gets the UTC timestamp of the last received heartbeat.</summary>
    public required DateTime LastHeartbeatUtc { get; init; }

    /// <summary>Gets the number of consecutive missed heartbeats.</summary>
    public required int MissedHeartbeats { get; init; }

    /// <summary>Gets the UTC timestamp when this node first joined the fabric.</summary>
    public required DateTime JoinedUtc { get; init; }

    /// <summary>Gets optional metadata about the node (region, role, version, etc.).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Describes a topology switch recommendation or automatic switch event,
/// including the reason, node count, and whether it was automatically applied.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Topology switch recommendation")]
public sealed record TopologySwitchRecommendation
{
    /// <summary>Gets the topology before the switch.</summary>
    public required FabricTopology FromTopology { get; init; }

    /// <summary>Gets the recommended or applied target topology.</summary>
    public required FabricTopology ToTopology { get; init; }

    /// <summary>Gets the reason for the switch recommendation.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets the node count at the time of the recommendation.</summary>
    public required int NodeCount { get; init; }

    /// <summary>Gets the UTC timestamp of the recommendation.</summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>Gets whether the switch was automatically applied.</summary>
    public required bool WasAutomatic { get; init; }
}
