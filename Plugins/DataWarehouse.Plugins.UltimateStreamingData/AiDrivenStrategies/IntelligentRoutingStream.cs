using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.AiDrivenStrategies;

#region Intelligent Routing Types

/// <summary>
/// Routing strategy for event placement across partitions.
/// </summary>
public enum RoutingStrategy
{
    /// <summary>AI-determined optimal partition based on workload analysis.</summary>
    AiOptimal,

    /// <summary>Consistent hash-based routing on event key.</summary>
    HashBased,

    /// <summary>Route to the partition with the lowest load.</summary>
    LeastLoaded,

    /// <summary>Round-robin across all available partitions.</summary>
    RoundRobin,

    /// <summary>Affinity-based: events with related keys go to the same partition.</summary>
    KeyAffinity,

    /// <summary>Latency-aware: route to the partition with the lowest response time.</summary>
    LatencyAware
}

/// <summary>
/// Result of a routing decision for a stream event.
/// </summary>
public sealed record RoutingDecision
{
    /// <summary>Gets the target partition identifier.</summary>
    public required int TargetPartition { get; init; }

    /// <summary>Gets the routing strategy that was used.</summary>
    public required RoutingStrategy StrategyUsed { get; init; }

    /// <summary>Gets the analysis method ("ML" or "RuleBased").</summary>
    public string AnalysisMethod { get; init; } = "RuleBased";

    /// <summary>Gets the confidence level of the routing decision (0.0-1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the reason for the routing decision.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the estimated partition load at the time of routing.</summary>
    public double EstimatedPartitionLoad { get; init; }

    /// <summary>Gets when the routing decision was made.</summary>
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Partition health and load information.
/// </summary>
public sealed record PartitionLoadInfo
{
    /// <summary>Gets the partition identifier.</summary>
    public required int PartitionId { get; init; }

    /// <summary>Gets the current event count in the partition.</summary>
    public long EventCount { get; init; }

    /// <summary>Gets the current throughput (events/sec).</summary>
    public double Throughput { get; init; }

    /// <summary>Gets the average processing latency (ms).</summary>
    public double LatencyMs { get; init; }

    /// <summary>Gets the consumer lag for this partition.</summary>
    public long ConsumerLag { get; init; }

    /// <summary>Gets the partition utilization (0-1).</summary>
    public double Utilization { get; init; }

    /// <summary>Gets whether this partition is healthy.</summary>
    public bool IsHealthy { get; init; } = true;

    /// <summary>Gets when this info was last updated.</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Configuration for intelligent stream routing.
/// </summary>
public sealed record IntelligentRoutingConfig
{
    /// <summary>Gets the total number of partitions to route across.</summary>
    public int PartitionCount { get; init; } = 16;

    /// <summary>Gets the maximum imbalance ratio before rebalancing triggers (e.g., 1.5 = 50% imbalance).</summary>
    public double MaxImbalanceRatio { get; init; } = 1.5;

    /// <summary>Gets the Intelligence plugin request timeout.</summary>
    public TimeSpan IntelligenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the partition load refresh interval.</summary>
    public TimeSpan LoadRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the latency threshold (ms) above which a partition is considered unhealthy.</summary>
    public double UnhealthyLatencyThresholdMs { get; init; } = 1000.0;

    /// <summary>Gets the key affinity hash seed for consistent hashing.</summary>
    public int AffinityHashSeed { get; init; } = 0x5F3759DF;
}

#endregion

/// <summary>
/// AI-driven intelligent routing for stream event partition selection.
/// Routes to Intelligence plugin (topic: "intelligence.route") for optimal partition placement.
/// Falls back to hash-based consistent routing when Intelligence is unavailable.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for ML-based optimal routing.
/// <b>MESSAGE TOPIC:</b> intelligence.route
/// <b>FALLBACK:</b> Hash-based consistent routing using SHA-256 of event key.
/// Supports least-loaded, round-robin, key-affinity, and latency-aware fallback strategies.
/// Thread-safe for concurrent routing decisions.
/// </remarks>
internal sealed class IntelligentRoutingStream : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<int, PartitionLoadInfo> _partitionLoads = new BoundedDictionary<int, PartitionLoadInfo>(1000);
    private readonly IMessageBus? _messageBus;
    private readonly IntelligentRoutingConfig _config;
    private long _roundRobinCounter;
    private long _totalRouted;
    private long _mlRoutings;
    private long _hashFallbacks;

    /// <inheritdoc/>
    public override string StrategyId => "ai-intelligent-routing-stream";

    /// <inheritdoc/>
    public override string DisplayName => "AI Intelligent Routing Stream";

    /// <inheritdoc/>
    public override StreamingCategory Category => StreamingCategory.StreamScalability;

    /// <inheritdoc/>
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = false,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = false,
        SupportsPartitioning = true,
        SupportsAutoScaling = false,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 100_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "AI-driven intelligent routing for stream event partition selection. Routes to Intelligence plugin " +
        "for optimal partition placement with hash-based consistent routing fallback. Supports load-aware, " +
        "latency-aware, and affinity-based routing strategies.";

    /// <inheritdoc/>
    public override string[] Tags => ["intelligent-routing", "ai", "partitioning", "load-balancing", "consistent-hash"];

    /// <summary>
    /// Initializes a new instance of the <see cref="IntelligentRoutingStream"/> class.
    /// </summary>
    /// <param name="messageBus">Optional message bus for Intelligence plugin communication.</param>
    /// <param name="config">Routing configuration.</param>
    public IntelligentRoutingStream(IMessageBus? messageBus = null, IntelligentRoutingConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new IntelligentRoutingConfig();
        InitializePartitionLoads();
    }

    /// <summary>
    /// Initializes a new instance for auto-discovery (parameterless constructor).
    /// </summary>
    public IntelligentRoutingStream() : this(null, null) { }

    /// <summary>Gets the count of ML-based routing decisions.</summary>
    public long MlRoutings => Interlocked.Read(ref _mlRoutings);

    /// <summary>Gets the count of hash-based fallback routing decisions.</summary>
    public long HashFallbacks => Interlocked.Read(ref _hashFallbacks);

    /// <summary>
    /// Routes a stream event to an optimal partition using AI or hash-based fallback.
    /// </summary>
    /// <param name="evt">The stream event to route.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The routing decision with target partition.</returns>
    public async Task<RoutingDecision> RouteEventAsync(StreamEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        Interlocked.Increment(ref _totalRouted);

        // Try ML-based routing via Intelligence plugin
        if (_messageBus != null)
        {
            try
            {
                var mlResult = await TryMlRoutingAsync(evt, ct);
                if (mlResult != null)
                {
                    Interlocked.Increment(ref _mlRoutings);
                    UpdatePartitionLoad(mlResult.TargetPartition);
                    RecordOperation("ml-routing");
                    return mlResult;
                }
            }
            catch (Exception ex)
            {

                // Fall through to hash-based routing
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Hash-based routing fallback
        Interlocked.Increment(ref _hashFallbacks);
        var result = HashBasedRouting(evt);
        UpdatePartitionLoad(result.TargetPartition);
        RecordOperation("hash-routing");
        return result;
    }

    /// <summary>
    /// Routes using a specified fallback strategy (non-AI).
    /// </summary>
    /// <param name="evt">The stream event to route.</param>
    /// <param name="strategy">The routing strategy to use.</param>
    /// <returns>The routing decision.</returns>
    public RoutingDecision RouteWithStrategy(StreamEvent evt, RoutingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(evt);

        return strategy switch
        {
            RoutingStrategy.HashBased => HashBasedRouting(evt),
            RoutingStrategy.LeastLoaded => LeastLoadedRouting(evt),
            RoutingStrategy.RoundRobin => RoundRobinRouting(evt),
            RoutingStrategy.KeyAffinity => KeyAffinityRouting(evt),
            RoutingStrategy.LatencyAware => LatencyAwareRouting(evt),
            _ => HashBasedRouting(evt)
        };
    }

    /// <summary>
    /// Updates the load information for a partition.
    /// </summary>
    /// <param name="info">The updated partition load info.</param>
    public void UpdatePartitionLoadInfo(PartitionLoadInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        _partitionLoads[info.PartitionId] = info;
    }

    /// <summary>
    /// Gets the current load distribution across partitions.
    /// </summary>
    /// <returns>A read-only collection of partition load information.</returns>
    public IReadOnlyCollection<PartitionLoadInfo> GetPartitionLoads() =>
        _partitionLoads.Values.ToArray();

    private async Task<RoutingDecision?> TryMlRoutingAsync(StreamEvent evt, CancellationToken ct)
    {
        if (_messageBus == null) return null;

        var request = new PluginMessage
        {
            Type = "routing.request",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "IntelligentRoutingStream",
            Payload = new Dictionary<string, object>
            {
                ["DataType"] = "stream-event-routing",
                ["RoutingType"] = "partition-selection",
                ["EventKey"] = evt.Key ?? string.Empty,
                ["EventSize"] = evt.Data.Length,
                ["PartitionCount"] = _config.PartitionCount,
                ["PartitionLoads"] = _partitionLoads.Values.Select(p => new Dictionary<string, object>
                {
                    ["PartitionId"] = p.PartitionId,
                    ["Utilization"] = p.Utilization,
                    ["LatencyMs"] = p.LatencyMs,
                    ["ConsumerLag"] = p.ConsumerLag,
                    ["IsHealthy"] = p.IsHealthy
                }).ToArray()
            }
        };

        var response = await _messageBus.SendAsync(
            "intelligence.route",
            request,
            _config.IntelligenceTimeout,
            ct);

        if (!response.Success) return null;

        var payload = response.Payload as Dictionary<string, object>;
        if (payload == null) return null;

        var partition = payload.TryGetValue("TargetPartition", out var partObj) && partObj is int p
            ? p : -1;
        var confidence = payload.TryGetValue("Confidence", out var confObj) && confObj is double c
            ? c : 0.0;
        var reason = payload.TryGetValue("Reason", out var reasonObj)
            ? reasonObj?.ToString() ?? "" : "";

        if (partition < 0 || partition >= _config.PartitionCount) return null;

        var load = _partitionLoads.TryGetValue(partition, out var info) ? info.Utilization : 0;

        return new RoutingDecision
        {
            TargetPartition = partition,
            StrategyUsed = RoutingStrategy.AiOptimal,
            AnalysisMethod = "ML",
            Confidence = confidence,
            Reason = reason,
            EstimatedPartitionLoad = load
        };
    }

    private RoutingDecision HashBasedRouting(StreamEvent evt)
    {
        int partition;
        if (!string.IsNullOrEmpty(evt.Key))
        {
            // Consistent hash using SHA-256 for uniform distribution
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(evt.Key);
            var hash = SHA256.HashData(keyBytes);
            var hashInt = BitConverter.ToInt32(hash, 0);
            partition = Math.Abs(hashInt) % _config.PartitionCount;
        }
        else
        {
            partition = Random.Shared.Next(_config.PartitionCount);
        }

        var load = _partitionLoads.TryGetValue(partition, out var info) ? info.Utilization : 0;

        return new RoutingDecision
        {
            TargetPartition = partition,
            StrategyUsed = RoutingStrategy.HashBased,
            AnalysisMethod = "RuleBased",
            Confidence = 1.0,
            Reason = evt.Key != null
                ? $"Consistent hash of key '{evt.Key}' maps to partition {partition}"
                : $"Random partition {partition} (no key)",
            EstimatedPartitionLoad = load
        };
    }

    private RoutingDecision LeastLoadedRouting(StreamEvent evt)
    {
        var healthyPartitions = _partitionLoads.Values
            .Where(p => p.IsHealthy)
            .ToArray();

        if (healthyPartitions.Length == 0)
        {
            // All unhealthy, fall back to hash
            return HashBasedRouting(evt);
        }

        var leastLoaded = healthyPartitions.MinBy(p => p.Utilization)!;

        return new RoutingDecision
        {
            TargetPartition = leastLoaded.PartitionId,
            StrategyUsed = RoutingStrategy.LeastLoaded,
            AnalysisMethod = "RuleBased",
            Confidence = 0.9,
            Reason = $"Partition {leastLoaded.PartitionId} has lowest utilization ({leastLoaded.Utilization:P0})",
            EstimatedPartitionLoad = leastLoaded.Utilization
        };
    }

    private RoutingDecision RoundRobinRouting(StreamEvent evt)
    {
        var partition = (int)(Interlocked.Increment(ref _roundRobinCounter) % _config.PartitionCount);
        var load = _partitionLoads.TryGetValue(partition, out var info) ? info.Utilization : 0;

        return new RoutingDecision
        {
            TargetPartition = partition,
            StrategyUsed = RoutingStrategy.RoundRobin,
            AnalysisMethod = "RuleBased",
            Confidence = 1.0,
            Reason = $"Round-robin to partition {partition}",
            EstimatedPartitionLoad = load
        };
    }

    private RoutingDecision KeyAffinityRouting(StreamEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Key))
            return RoundRobinRouting(evt);

        // Consistent hashing with seed for key affinity
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(evt.Key);
        var seedBytes = BitConverter.GetBytes(_config.AffinityHashSeed);
        var combined = new byte[keyBytes.Length + seedBytes.Length];
        Array.Copy(keyBytes, combined, keyBytes.Length);
        Array.Copy(seedBytes, 0, combined, keyBytes.Length, seedBytes.Length);

        var hash = SHA256.HashData(combined);
        var partition = Math.Abs(BitConverter.ToInt32(hash, 0)) % _config.PartitionCount;
        var load = _partitionLoads.TryGetValue(partition, out var info) ? info.Utilization : 0;

        return new RoutingDecision
        {
            TargetPartition = partition,
            StrategyUsed = RoutingStrategy.KeyAffinity,
            AnalysisMethod = "RuleBased",
            Confidence = 1.0,
            Reason = $"Key affinity: '{evt.Key}' always maps to partition {partition}",
            EstimatedPartitionLoad = load
        };
    }

    private RoutingDecision LatencyAwareRouting(StreamEvent evt)
    {
        var healthyPartitions = _partitionLoads.Values
            .Where(p => p.IsHealthy && p.LatencyMs < _config.UnhealthyLatencyThresholdMs)
            .ToArray();

        if (healthyPartitions.Length == 0)
        {
            return HashBasedRouting(evt);
        }

        var lowestLatency = healthyPartitions.MinBy(p => p.LatencyMs)!;

        return new RoutingDecision
        {
            TargetPartition = lowestLatency.PartitionId,
            StrategyUsed = RoutingStrategy.LatencyAware,
            AnalysisMethod = "RuleBased",
            Confidence = 0.85,
            Reason = $"Partition {lowestLatency.PartitionId} has lowest latency ({lowestLatency.LatencyMs:F1}ms)",
            EstimatedPartitionLoad = lowestLatency.Utilization
        };
    }

    private void InitializePartitionLoads()
    {
        for (int i = 0; i < _config.PartitionCount; i++)
        {
            _partitionLoads[i] = new PartitionLoadInfo
            {
                PartitionId = i,
                IsHealthy = true
            };
        }
    }

    private void UpdatePartitionLoad(int partition)
    {
        if (_partitionLoads.TryGetValue(partition, out var info))
        {
            _partitionLoads[partition] = info with
            {
                EventCount = info.EventCount + 1,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
    }
}
