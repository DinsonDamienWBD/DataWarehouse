using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Configuration;

// ============================================================================
// LOAD BALANCING CONFIGURATION
// Intelligent load balancing with automatic/manual mode switching
// Supports: Manual, Automatic, Intelligent modes with user override
// ============================================================================

#region Enums

/// <summary>
/// Load balancing mode that determines how requests are distributed.
/// </summary>
public enum LoadBalancingMode
{
    /// <summary>
    /// Manual mode - user controls all sharding and distribution.
    /// Best for: Single user, predictable workloads, debugging.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Automatic mode - system handles all distribution automatically.
    /// Best for: Dynamic workloads, cloud deployments, high availability.
    /// </summary>
    Automatic = 1,

    /// <summary>
    /// Intelligent mode - system selects optimal mode based on context.
    /// Switches between manual and automatic based on workload characteristics.
    /// </summary>
    Intelligent = 2
}

/// <summary>
/// Load balancing algorithm for request distribution.
/// </summary>
public enum LoadBalancingAlgorithm
{
    /// <summary>
    /// Round-robin distribution across all nodes.
    /// </summary>
    RoundRobin = 0,

    /// <summary>
    /// Route to node with fewest active connections.
    /// </summary>
    LeastConnections = 1,

    /// <summary>
    /// Route based on node weights (capacity-aware).
    /// </summary>
    WeightedRoundRobin = 2,

    /// <summary>
    /// Route to node with lowest latency.
    /// </summary>
    LatencyBased = 3,

    /// <summary>
    /// Consistent hashing for cache-friendly distribution.
    /// </summary>
    ConsistentHashing = 4,

    /// <summary>
    /// Random distribution with optional weights.
    /// </summary>
    Random = 5,

    /// <summary>
    /// Resource-aware routing based on CPU/memory/disk.
    /// </summary>
    ResourceAware = 6,

    /// <summary>
    /// Adaptive algorithm that switches based on conditions.
    /// </summary>
    Adaptive = 7
}

/// <summary>
/// Rebalancing aggressiveness level.
/// </summary>
public enum RebalancingAggressiveness
{
    /// <summary>
    /// Conservative - rebalance only when necessary.
    /// </summary>
    Conservative = 0,

    /// <summary>
    /// Moderate - regular rebalancing with minimal disruption.
    /// </summary>
    Moderate = 1,

    /// <summary>
    /// Aggressive - frequent rebalancing for optimal distribution.
    /// </summary>
    Aggressive = 2,

    /// <summary>
    /// Predictive - proactive rebalancing before issues occur.
    /// </summary>
    Predictive = 3
}

/// <summary>
/// Sharding strategy for data distribution.
/// </summary>
public enum ShardingStrategy
{
    /// <summary>
    /// No sharding - all data on single node.
    /// </summary>
    None = 0,

    /// <summary>
    /// Hash-based sharding using consistent hash ring.
    /// </summary>
    HashBased = 1,

    /// <summary>
    /// Range-based sharding (e.g., by key prefix or date).
    /// </summary>
    RangeBased = 2,

    /// <summary>
    /// Directory-based sharding with lookup service.
    /// </summary>
    DirectoryBased = 3,

    /// <summary>
    /// Geographic sharding based on data locality.
    /// </summary>
    Geographic = 4,

    /// <summary>
    /// Composite sharding combining multiple strategies.
    /// </summary>
    Composite = 5
}

#endregion

#region Configuration Classes

/// <summary>
/// Unified load balancing configuration with intelligent mode selection.
/// </summary>
public sealed class LoadBalancingConfig
{
    /// <summary>
    /// Primary load balancing mode.
    /// </summary>
    public LoadBalancingMode Mode { get; init; } = LoadBalancingMode.Intelligent;

    /// <summary>
    /// Whether the user explicitly set this mode (overrides intelligent selection).
    /// </summary>
    public bool IsUserOverride { get; init; }

    /// <summary>
    /// Load balancing algorithm to use.
    /// </summary>
    public LoadBalancingAlgorithm Algorithm { get; init; } = LoadBalancingAlgorithm.Adaptive;

    /// <summary>
    /// Sharding strategy for data distribution.
    /// </summary>
    public ShardingStrategy Sharding { get; init; } = ShardingStrategy.HashBased;

    /// <summary>
    /// Rebalancing aggressiveness level.
    /// </summary>
    public RebalancingAggressiveness Aggressiveness { get; init; } = RebalancingAggressiveness.Moderate;

    /// <summary>
    /// Deployment tier - influences intelligent mode selection.
    /// </summary>
    public DeploymentTier Tier { get; init; } = DeploymentTier.SMB;

    /// <summary>
    /// Enable automatic sharding and rebalancing.
    /// </summary>
    public bool EnableAutoSharding { get; init; } = true;

    /// <summary>
    /// Enable hot shard detection and auto-splitting.
    /// </summary>
    public bool EnableHotShardDetection { get; init; } = true;

    /// <summary>
    /// Threshold for hot shard detection (requests per second).
    /// </summary>
    public int HotShardThreshold { get; init; } = 1000;

    /// <summary>
    /// Enable predictive load balancing using historical data.
    /// </summary>
    public bool EnablePredictiveBalancing { get; init; }

    /// <summary>
    /// Time window for load analysis.
    /// </summary>
    public TimeSpan LoadAnalysisWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum interval between rebalancing operations.
    /// </summary>
    public TimeSpan MinRebalanceInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum data movement per rebalance operation (percentage).
    /// </summary>
    public int MaxDataMovementPercent { get; init; } = 10;

    /// <summary>
    /// Enable health-based routing (avoid unhealthy nodes).
    /// </summary>
    public bool EnableHealthBasedRouting { get; init; } = true;

    /// <summary>
    /// Enable circuit breaker for failing nodes.
    /// </summary>
    public bool EnableCircuitBreaker { get; init; } = true;

    /// <summary>
    /// Number of failures before circuit opens.
    /// </summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>
    /// Circuit breaker reset timeout.
    /// </summary>
    public TimeSpan CircuitBreakerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates default configuration with intelligent mode.
    /// </summary>
    public static LoadBalancingConfig Default => new();

    /// <summary>
    /// Creates configuration for individual/laptop deployment (manual mode).
    /// </summary>
    public static LoadBalancingConfig ForIndividual() => new()
    {
        Mode = LoadBalancingMode.Manual,
        Tier = DeploymentTier.Individual,
        Algorithm = LoadBalancingAlgorithm.RoundRobin,
        Sharding = ShardingStrategy.None,
        EnableAutoSharding = false,
        EnableHotShardDetection = false,
        EnablePredictiveBalancing = false,
        EnableCircuitBreaker = false,
        Aggressiveness = RebalancingAggressiveness.Conservative
    };

    /// <summary>
    /// Creates configuration for SMB deployment.
    /// </summary>
    public static LoadBalancingConfig ForSMB() => new()
    {
        Mode = LoadBalancingMode.Automatic,
        Tier = DeploymentTier.SMB,
        Algorithm = LoadBalancingAlgorithm.LeastConnections,
        Sharding = ShardingStrategy.HashBased,
        EnableAutoSharding = true,
        EnableHotShardDetection = false,
        EnablePredictiveBalancing = false,
        Aggressiveness = RebalancingAggressiveness.Conservative,
        MinRebalanceInterval = TimeSpan.FromHours(1)
    };

    /// <summary>
    /// Creates configuration for enterprise deployment.
    /// </summary>
    public static LoadBalancingConfig ForEnterprise() => new()
    {
        Mode = LoadBalancingMode.Automatic,
        Tier = DeploymentTier.Enterprise,
        Algorithm = LoadBalancingAlgorithm.ResourceAware,
        Sharding = ShardingStrategy.HashBased,
        EnableAutoSharding = true,
        EnableHotShardDetection = true,
        EnablePredictiveBalancing = false,
        Aggressiveness = RebalancingAggressiveness.Moderate,
        MinRebalanceInterval = TimeSpan.FromMinutes(30)
    };

    /// <summary>
    /// Creates configuration for high-stakes deployment (automatic by default).
    /// </summary>
    public static LoadBalancingConfig ForHighStakes() => new()
    {
        Mode = LoadBalancingMode.Automatic,
        Tier = DeploymentTier.HighStakes,
        Algorithm = LoadBalancingAlgorithm.Adaptive,
        Sharding = ShardingStrategy.HashBased,
        EnableAutoSharding = true,
        EnableHotShardDetection = true,
        EnablePredictiveBalancing = true,
        Aggressiveness = RebalancingAggressiveness.Aggressive,
        MinRebalanceInterval = TimeSpan.FromMinutes(5),
        HotShardThreshold = 500 // More sensitive detection
    };

    /// <summary>
    /// Creates configuration for hyperscale deployment.
    /// </summary>
    public static LoadBalancingConfig ForHyperscale() => new()
    {
        Mode = LoadBalancingMode.Automatic,
        Tier = DeploymentTier.Hyperscale,
        Algorithm = LoadBalancingAlgorithm.ConsistentHashing,
        Sharding = ShardingStrategy.Composite,
        EnableAutoSharding = true,
        EnableHotShardDetection = true,
        EnablePredictiveBalancing = true,
        Aggressiveness = RebalancingAggressiveness.Predictive,
        MinRebalanceInterval = TimeSpan.FromMinutes(1),
        LoadAnalysisWindow = TimeSpan.FromMinutes(1),
        MaxDataMovementPercent = 5 // Smaller moves at scale
    };
}

#endregion

#region Load Balancing Manager

/// <summary>
/// Manages load balancing strategy selection and intelligent mode switching.
/// </summary>
public sealed class LoadBalancingManager
{
    private readonly BoundedDictionary<string, NodeMetrics> _nodeMetrics = new BoundedDictionary<string, NodeMetrics>(1000);
    private readonly BoundedDictionary<string, LoadBalancingConfig> _containerConfigs = new BoundedDictionary<string, LoadBalancingConfig>(1000);
    private LoadBalancingConfig _defaultConfig;
    private readonly object _lock = new();

    // Metrics for intelligent mode selection
    private long _totalRequests;
    private long _requestsLastMinute;
    private DateTime _lastMetricsReset = DateTime.UtcNow;
    private int _activeNodes;

    public LoadBalancingManager(LoadBalancingConfig? defaultConfig = null)
    {
        _defaultConfig = defaultConfig ?? LoadBalancingConfig.Default;
    }

    /// <summary>
    /// Gets the default load balancing configuration.
    /// </summary>
    public LoadBalancingConfig DefaultConfig => _defaultConfig;

    /// <summary>
    /// Sets the default load balancing configuration.
    /// </summary>
    public void SetDefaultConfig(LoadBalancingConfig config)
    {
        lock (_lock)
        {
            _defaultConfig = config ?? throw new ArgumentNullException(nameof(config));
        }
    }

    /// <summary>
    /// Gets the load balancing configuration for a container.
    /// </summary>
    public LoadBalancingConfig GetConfig(string? containerId = null)
    {
        if (containerId != null && _containerConfigs.TryGetValue(containerId, out var containerConfig))
        {
            return containerConfig;
        }
        return _defaultConfig;
    }

    /// <summary>
    /// Records a request for metrics tracking.
    /// </summary>
    public void RecordRequest(string nodeId)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _requestsLastMinute);

        _nodeMetrics.AddOrUpdate(
            nodeId,
            _ => new NodeMetrics { NodeId = nodeId, RequestCount = 1, LastRequestTime = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.RequestCount++;
                existing.LastRequestTime = DateTime.UtcNow;
                return existing;
            });

        // Reset minute counter periodically
        if ((DateTime.UtcNow - _lastMetricsReset).TotalMinutes >= 1)
        {
            Interlocked.Exchange(ref _requestsLastMinute, 0);
            _lastMetricsReset = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Updates node health metrics.
    /// </summary>
    public void UpdateNodeHealth(string nodeId, NodeHealthStatus health)
    {
        _nodeMetrics.AddOrUpdate(
            nodeId,
            _ => new NodeMetrics { NodeId = nodeId, Health = health, LastHealthCheck = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.Health = health;
                existing.LastHealthCheck = DateTime.UtcNow;
                return existing;
            });
    }

    /// <summary>
    /// Intelligently selects the load balancing mode based on current conditions.
    /// </summary>
    public LoadBalancingMode SelectIntelligentMode(LoadBalancingConfig config)
    {
        if (config.IsUserOverride)
        {
            return config.Mode;
        }

        var activeNodes = _nodeMetrics.Count(n => n.Value.Health == NodeHealthStatus.Healthy);
        var requestRate = _requestsLastMinute;

        // Decision matrix for intelligent mode selection
        return (config.Tier, activeNodes, requestRate) switch
        {
            // Single node or very low load - use manual
            (_, <= 1, _) => LoadBalancingMode.Manual,
            (DeploymentTier.Individual, _, _) => LoadBalancingMode.Manual,

            // SMB with low load - manual is fine
            (DeploymentTier.SMB, _, < 10) => LoadBalancingMode.Manual,
            (DeploymentTier.SMB, _, _) => LoadBalancingMode.Automatic,

            // Enterprise - automatic for reliability
            (DeploymentTier.Enterprise, _, _) => LoadBalancingMode.Automatic,

            // High-stakes - always automatic
            (DeploymentTier.HighStakes, _, _) => LoadBalancingMode.Automatic,

            // Hyperscale - always automatic with predictive
            (DeploymentTier.Hyperscale, _, _) => LoadBalancingMode.Automatic,

            // Default to automatic if multiple nodes
            _ when activeNodes >= 2 => LoadBalancingMode.Automatic,

            // Fallback to manual
            _ => LoadBalancingMode.Manual
        };
    }

    /// <summary>
    /// Selects the optimal load balancing algorithm based on current conditions.
    /// </summary>
    public LoadBalancingAlgorithm SelectOptimalAlgorithm(LoadBalancingConfig config)
    {
        if (config.Algorithm != LoadBalancingAlgorithm.Adaptive)
        {
            return config.Algorithm;
        }

        var healthyNodes = _nodeMetrics.Values.Where(n => n.Health == NodeHealthStatus.Healthy).ToList();
        if (healthyNodes.Count == 0)
        {
            return LoadBalancingAlgorithm.RoundRobin;
        }

        // Check for load imbalance
        var avgLoad = healthyNodes.Average(n => n.RequestCount);
        var maxLoad = healthyNodes.Max(n => n.RequestCount);
        var imbalanceRatio = avgLoad > 0 ? maxLoad / avgLoad : 1;

        // Check latency variance
        var latencies = healthyNodes.Where(n => n.AverageLatencyMs > 0).Select(n => n.AverageLatencyMs).ToList();
        var latencyVariance = latencies.Count > 1
            ? latencies.Max() - latencies.Min()
            : 0;

        return (imbalanceRatio, latencyVariance, config.Tier) switch
        {
            // High imbalance - use least connections
            ( > 2, _, _) => LoadBalancingAlgorithm.LeastConnections,

            // High latency variance - use latency-based
            (_, > 100, _) => LoadBalancingAlgorithm.LatencyBased,

            // Hyperscale - use consistent hashing for cache efficiency
            (_, _, DeploymentTier.Hyperscale) => LoadBalancingAlgorithm.ConsistentHashing,

            // High-stakes - use resource-aware
            (_, _, DeploymentTier.HighStakes) => LoadBalancingAlgorithm.ResourceAware,

            // Default to weighted round-robin
            _ => LoadBalancingAlgorithm.WeightedRoundRobin
        };
    }

    /// <summary>
    /// Determines the rebalancing aggressiveness based on current conditions.
    /// </summary>
    public RebalancingAggressiveness DetermineAggressiveness(LoadBalancingConfig config)
    {
        if (config.Aggressiveness != RebalancingAggressiveness.Predictive)
        {
            return config.Aggressiveness;
        }

        var healthyNodes = _nodeMetrics.Values.Where(n => n.Health == NodeHealthStatus.Healthy).ToList();
        if (healthyNodes.Count == 0)
        {
            return RebalancingAggressiveness.Conservative;
        }

        // Analyze load distribution
        var avgLoad = healthyNodes.Average(n => n.RequestCount);
        var maxLoad = healthyNodes.Max(n => n.RequestCount);
        var imbalanceRatio = avgLoad > 0 ? maxLoad / avgLoad : 1;

        // Check for hot shards
        var hotShards = healthyNodes.Count(n => n.RequestCount > config.HotShardThreshold);

        return (imbalanceRatio, hotShards) switch
        {
            ( > 3, _) => RebalancingAggressiveness.Aggressive,   // Severe imbalance
            ( > 2, _) => RebalancingAggressiveness.Moderate,      // Moderate imbalance
            (_, > 0) => RebalancingAggressiveness.Aggressive,    // Hot shards detected
            _ => RebalancingAggressiveness.Conservative
        };
    }

    /// <summary>
    /// Selects the next node for a request based on the current algorithm.
    /// </summary>
    public string? SelectNode(LoadBalancingConfig config, string? affinityKey = null)
    {
        var healthyNodes = _nodeMetrics.Values
            .Where(n => n.Health == NodeHealthStatus.Healthy || !config.EnableHealthBasedRouting)
            .ToList();

        if (healthyNodes.Count == 0)
        {
            return null;
        }

        var algorithm = SelectOptimalAlgorithm(config);

        return algorithm switch
        {
            LoadBalancingAlgorithm.RoundRobin => SelectRoundRobin(healthyNodes),
            LoadBalancingAlgorithm.LeastConnections => SelectLeastConnections(healthyNodes),
            LoadBalancingAlgorithm.WeightedRoundRobin => SelectWeightedRoundRobin(healthyNodes),
            LoadBalancingAlgorithm.LatencyBased => SelectLatencyBased(healthyNodes),
            LoadBalancingAlgorithm.ConsistentHashing => SelectConsistentHashing(healthyNodes, affinityKey),
            LoadBalancingAlgorithm.Random => SelectRandom(healthyNodes),
            LoadBalancingAlgorithm.ResourceAware => SelectResourceAware(healthyNodes),
            _ => SelectRoundRobin(healthyNodes)
        };
    }

    private int _roundRobinIndex;
    private string SelectRoundRobin(List<NodeMetrics> nodes)
    {
        var index = Interlocked.Increment(ref _roundRobinIndex) % nodes.Count;
        return nodes[index].NodeId;
    }

    private string SelectLeastConnections(List<NodeMetrics> nodes)
    {
        return nodes.OrderBy(n => n.ActiveConnections).First().NodeId;
    }

    private string SelectWeightedRoundRobin(List<NodeMetrics> nodes)
    {
        var totalWeight = nodes.Sum(n => n.Weight);
        var random = Random.Shared.Next(totalWeight);
        var cumulative = 0;

        foreach (var node in nodes)
        {
            cumulative += node.Weight;
            if (random < cumulative)
            {
                return node.NodeId;
            }
        }

        return nodes[0].NodeId;
    }

    private string SelectLatencyBased(List<NodeMetrics> nodes)
    {
        return nodes.OrderBy(n => n.AverageLatencyMs).First().NodeId;
    }

    private string SelectConsistentHashing(List<NodeMetrics> nodes, string? affinityKey)
    {
        if (string.IsNullOrEmpty(affinityKey))
        {
            return SelectRoundRobin(nodes);
        }

        var hash = affinityKey.GetHashCode();
        var index = Math.Abs(hash) % nodes.Count;
        return nodes[index].NodeId;
    }

    private string SelectRandom(List<NodeMetrics> nodes)
    {
        return nodes[Random.Shared.Next(nodes.Count)].NodeId;
    }

    private string SelectResourceAware(List<NodeMetrics> nodes)
    {
        // Score based on available resources (lower is better)
        return nodes.OrderBy(n =>
            n.CpuUsagePercent * 0.4 +
            n.MemoryUsagePercent * 0.3 +
            n.DiskUsagePercent * 0.2 +
            n.ActiveConnections * 0.1
        ).First().NodeId;
    }

    /// <summary>
    /// Gets current load balancing statistics.
    /// </summary>
    public LoadBalancingStats GetStats()
    {
        var nodes = _nodeMetrics.Values.ToList();
        return new LoadBalancingStats
        {
            TotalRequests = _totalRequests,
            RequestsPerMinute = _requestsLastMinute,
            ActiveNodes = nodes.Count(n => n.Health == NodeHealthStatus.Healthy),
            TotalNodes = nodes.Count,
            AverageLatencyMs = nodes.Where(n => n.AverageLatencyMs > 0).Select(n => n.AverageLatencyMs).DefaultIfEmpty(0).Average(),
            NodeStats = nodes.ToDictionary(n => n.NodeId, n => n)
        };
    }
}

/// <summary>
/// Metrics for a single node.
/// </summary>
public sealed class NodeMetrics
{
    public required string NodeId { get; init; }
    public NodeHealthStatus Health { get; set; } = NodeHealthStatus.Unknown;
    public long RequestCount { get; set; }
    public int ActiveConnections { get; set; }
    public double AverageLatencyMs { get; set; }
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public double DiskUsagePercent { get; set; }
    public int Weight { get; set; } = 100;
    public DateTime LastRequestTime { get; set; }
    public DateTime LastHealthCheck { get; set; }
}

/// <summary>
/// Node health status.
/// </summary>
public enum NodeHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Offline = 4
}

/// <summary>
/// Load balancing statistics.
/// </summary>
public sealed class LoadBalancingStats
{
    public long TotalRequests { get; init; }
    public long RequestsPerMinute { get; init; }
    public int ActiveNodes { get; init; }
    public int TotalNodes { get; init; }
    public double AverageLatencyMs { get; init; }
    public Dictionary<string, NodeMetrics> NodeStats { get; init; } = new();
}

#endregion
