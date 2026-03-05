using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Gravity-aware placement optimizer that scores objects on multiple dimensions
/// and generates rebalance plans that minimize disruption by moving low-gravity objects first.
/// </summary>
/// <remarks>
/// <para>Gravity dimensions:</para>
/// <list type="number">
///   <item><description>Access frequency: normalized reads+writes per hour (0=cold, 1=hottest)</description></item>
///   <item><description>Colocation: count of co-accessed dependencies on same node (0=isolated, 1=fully colocated)</description></item>
///   <item><description>Egress cost: normalized cost to move data out of current location (0=free, 1=most expensive)</description></item>
///   <item><description>Latency: inverse of current latency relative to consumers (0=worst latency, 1=optimal)</description></item>
///   <item><description>Compliance: 1.0 if compliance region matches, 0.0 if moving would violate regulations</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public sealed class GravityAwarePlacementOptimizer : IPlacementOptimizer
{
    private readonly IPlacementAlgorithm _crushAlgorithm;
    private readonly GravityScoringWeights _weights;

    private readonly Func<string, CancellationToken, Task<AccessMetrics>>? _accessMetricsProvider;
    private readonly Func<string, CancellationToken, Task<ColocationMetrics>>? _colocationProvider;
    private readonly Func<string, CancellationToken, Task<CostMetrics>>? _costProvider;
    private readonly Func<string, CancellationToken, Task<LatencyMetrics>>? _latencyProvider;
    private readonly Func<string, CancellationToken, Task<ComplianceMetrics>>? _complianceProvider;

    /// <summary>
    /// Creates a gravity-aware placement optimizer that delegates initial placement to CRUSH
    /// and then applies gravity-based optimization.
    /// </summary>
    /// <param name="crushAlgorithm">The underlying deterministic placement algorithm.</param>
    /// <param name="weights">Scoring weights (null uses <see cref="GravityScoringWeights.Default"/>).</param>
    /// <param name="accessMetricsProvider">Provider for access frequency metrics per object key.</param>
    /// <param name="colocationProvider">Provider for colocation metrics per object key.</param>
    /// <param name="costProvider">Provider for egress/storage cost metrics per object key.</param>
    /// <param name="latencyProvider">Provider for latency metrics per object key.</param>
    /// <param name="complianceProvider">Provider for compliance region metrics per object key.</param>
    public GravityAwarePlacementOptimizer(
        IPlacementAlgorithm crushAlgorithm,
        GravityScoringWeights? weights = null,
        Func<string, CancellationToken, Task<AccessMetrics>>? accessMetricsProvider = null,
        Func<string, CancellationToken, Task<ColocationMetrics>>? colocationProvider = null,
        Func<string, CancellationToken, Task<CostMetrics>>? costProvider = null,
        Func<string, CancellationToken, Task<LatencyMetrics>>? latencyProvider = null,
        Func<string, CancellationToken, Task<ComplianceMetrics>>? complianceProvider = null)
    {
        _crushAlgorithm = crushAlgorithm ?? throw new ArgumentNullException(nameof(crushAlgorithm));
        _weights = (weights ?? GravityScoringWeights.Default).Normalize();
        _accessMetricsProvider = accessMetricsProvider;
        _colocationProvider = colocationProvider;
        _costProvider = costProvider;
        _latencyProvider = latencyProvider;
        _complianceProvider = complianceProvider;
    }

    /// <inheritdoc />
    public async Task<DataGravityScore> ComputeGravityAsync(string objectKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        // Gather metrics from all dimensions (providers return defaults if not configured)
        var access = _accessMetricsProvider != null
            ? await _accessMetricsProvider(objectKey, ct).ConfigureAwait(false)
            : new AccessMetrics();
        var colocation = _colocationProvider != null
            ? await _colocationProvider(objectKey, ct).ConfigureAwait(false)
            : new ColocationMetrics();
        var cost = _costProvider != null
            ? await _costProvider(objectKey, ct).ConfigureAwait(false)
            : new CostMetrics();
        var latency = _latencyProvider != null
            ? await _latencyProvider(objectKey, ct).ConfigureAwait(false)
            : new LatencyMetrics();
        var compliance = _complianceProvider != null
            ? await _complianceProvider(objectKey, ct).ConfigureAwait(false)
            : new ComplianceMetrics();

        // Compute normalized dimension scores [0, 1]
        double accessScore = NormalizeAccessFrequency(access.ReadsPerHour + access.WritesPerHour);
        double colocationScore = colocation.ColocatedDependencies > 0
            ? Math.Min(1.0, colocation.ColocatedDependencies / 10.0)
            : 0.0;
        double egressScore = Math.Min(1.0, (double)cost.EgressCostPerGB / 0.12); // $0.12/GB as max reference
        double latencyScore = latency.CurrentLatencyMs > 0
            ? Math.Max(0.0, 1.0 - (latency.CurrentLatencyMs / 200.0)) // 200ms as worst-case reference
            : 0.5; // unknown latency = neutral
        double complianceScore = compliance.InComplianceRegion ? 1.0 : 0.0;

        // Weighted composite score
        double composite = _weights.AccessFrequency * accessScore
            + _weights.Colocation * colocationScore
            + _weights.EgressCost * egressScore
            + _weights.Latency * latencyScore
            + _weights.Compliance * complianceScore;

        return new DataGravityScore(
            ObjectKey: objectKey,
            CurrentNode: cost.CurrentNodeId ?? "",
            AccessFrequency: access.ReadsPerHour + access.WritesPerHour,
            LastAccessUtc: access.LastAccessUtc,
            ColocatedDependencies: colocation.ColocatedDependencies,
            EgressCostPerGB: cost.EgressCostPerGB,
            LatencyMs: latency.CurrentLatencyMs,
            ComplianceWeight: complianceScore,
            CompositeScore: Math.Clamp(composite, 0.0, 1.0));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataGravityScore>> ComputeGravityBatchAsync(
        IReadOnlyList<string> objectKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(objectKeys);

        // Process in parallel with bounded concurrency
        using var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
        var tasks = objectKeys.Select(async key =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try { return await ComputeGravityAsync(key, ct).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<PlacementDecision> OptimizePlacementAsync(
        PlacementTarget target,
        IReadOnlyList<NodeDescriptor> clusterMap,
        DataGravityScore gravity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(clusterMap);
        ArgumentNullException.ThrowIfNull(gravity);

        // Start with CRUSH placement
        var crushDecision = _crushAlgorithm.ComputePlacement(target, clusterMap);

        // If gravity is high (> 0.7), prefer keeping object on current node if it's in the CRUSH set
        if (gravity.CompositeScore > 0.7 && !string.IsNullOrEmpty(gravity.CurrentNode))
        {
            var currentInCluster = clusterMap.FirstOrDefault(n => n.NodeId == gravity.CurrentNode);
            if (currentInCluster != null && crushDecision.TargetNodes.Any(n => n.NodeId == gravity.CurrentNode))
            {
                // Already includes current node -- promote it to primary
                var reordered = new List<NodeDescriptor> { currentInCluster };
                reordered.AddRange(crushDecision.TargetNodes.Where(n => n.NodeId != gravity.CurrentNode));
                return new PlacementDecision(
                    TargetNodes: reordered,
                    PrimaryNode: currentInCluster,
                    ReplicaNodes: reordered.Skip(1).ToList(),
                    PlacementRuleId: crushDecision.PlacementRuleId,
                    Deterministic: crushDecision.Deterministic,
                    Timestamp: DateTimeOffset.UtcNow);
            }
        }

        return crushDecision;
    }

    /// <inheritdoc />
    public async Task<RebalancePlan> GenerateRebalancePlanAsync(
        IReadOnlyList<NodeDescriptor> clusterMap,
        RebalanceOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clusterMap);
        ArgumentNullException.ThrowIfNull(options);

        // Calculate ideal distribution based on node weights
        double totalWeight = clusterMap.Sum(n => n.Weight);
        if (totalWeight <= 0)
        {
            return new RebalancePlan(
                Moves: Array.Empty<RebalanceMove>(),
                EstimatedDurationSeconds: 0,
                EstimatedEgressBytes: 0,
                EstimatedCost: 0m);
        }

        var idealDistribution = clusterMap.ToDictionary(
            n => n.NodeId,
            n => n.Weight / totalWeight);

        // WARNING: Move enumeration requires storage layer integration (Plan 07 rebalancer service).
        // The optimizer produces the scoring and ideal distribution; the rebalancer enumerates objects,
        // scores them, and feeds them back through OptimizePlacementAsync to build the move list.
        // Until the rebalancer integration is complete, this returns an empty plan with ideal distribution metadata.
        System.Diagnostics.Trace.TraceWarning(
            "[GravityAwarePlacementOptimizer] GenerateRebalancePlanAsync returned empty plan: " +
            "storage layer integration (Plan 07 rebalancer service) not yet wired. " +
            $"Ideal distribution computed for {clusterMap.Count} nodes.");

        return new RebalancePlan(
            Moves: Array.Empty<RebalanceMove>(),
            EstimatedDurationSeconds: 0,
            EstimatedEgressBytes: 0,
            EstimatedCost: 0m);
    }

    /// <summary>
    /// Normalize access frequency to [0, 1] using logarithmic scaling.
    /// 0 access/hr = 0, 1 ~ 0.08, 10 ~ 0.26, 100 ~ 0.50, 1000 ~ 0.75, 10000+ ~ 1.0
    /// </summary>
    private static double NormalizeAccessFrequency(double accessesPerHour)
    {
        if (accessesPerHour <= 0) return 0.0;
        return Math.Min(1.0, Math.Log10(1 + accessesPerHour) / 4.0); // log10(10001) ~ 4.0
    }
}

/// <summary>
/// Access frequency metrics for gravity scoring. Captures read/write rates and recency.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public record AccessMetrics
{
    /// <summary>Average reads per hour over the scoring window.</summary>
    public double ReadsPerHour { get; init; }

    /// <summary>Average writes per hour over the scoring window.</summary>
    public double WritesPerHour { get; init; }

    /// <summary>Timestamp of the most recent access.</summary>
    public DateTimeOffset LastAccessUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Colocation metrics for gravity scoring. Measures how many co-accessed dependencies share the same node.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public record ColocationMetrics
{
    /// <summary>Number of co-accessed objects located on the same node.</summary>
    public int ColocatedDependencies { get; init; }

    /// <summary>Keys of the co-accessed dependency objects.</summary>
    public IReadOnlyList<string> DependencyKeys { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Cost metrics for gravity scoring. Captures egress and storage costs for the current placement.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public record CostMetrics
{
    /// <summary>ID of the node currently hosting this object.</summary>
    public string? CurrentNodeId { get; init; }

    /// <summary>Cost per GB to move data out of the current location.</summary>
    public decimal EgressCostPerGB { get; init; }

    /// <summary>Monthly storage cost per GB at the current location.</summary>
    public decimal StorageCostPerGBMonth { get; init; }
}

/// <summary>
/// Latency metrics for gravity scoring. Captures current and optimal latency to consumers.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public record LatencyMetrics
{
    /// <summary>Current average latency in milliseconds from consumers to this object.</summary>
    public double CurrentLatencyMs { get; init; }

    /// <summary>Theoretical optimal latency if placed in the best location.</summary>
    public double OptimalLatencyMs { get; init; }
}

/// <summary>
/// Compliance metrics for gravity scoring. Determines whether current placement satisfies regulatory constraints.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Gravity-aware placement")]
public record ComplianceMetrics
{
    /// <summary>True if the object is currently in a compliant region.</summary>
    public bool InComplianceRegion { get; init; } = true;

    /// <summary>List of region identifiers that satisfy compliance requirements.</summary>
    public IReadOnlyList<string> RequiredRegions { get; init; } = Array.Empty<string>();
}
