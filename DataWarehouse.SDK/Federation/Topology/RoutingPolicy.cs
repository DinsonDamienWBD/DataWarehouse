using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Federation.Topology;

/// <summary>
/// Defines routing policies for location-aware node selection.
/// </summary>
/// <remarks>
/// <para>
/// RoutingPolicy controls how the <see cref="LocationAwareRouter"/> scores and selects target
/// nodes for storage operations. Each policy optimizes for a different dimension:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>LatencyOptimized:</strong> Minimize network latency by preferring nodes in the
///       same rack/datacenter/region. Best for interactive workloads and user-facing APIs.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>ThroughputOptimized:</strong> Maximize throughput by distributing load across
///       nodes. Prefers least-loaded nodes regardless of location. Best for batch workloads and
///       bulk data transfers.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>CostOptimized:</strong> Minimize operational costs by preferring on-premises
///       or cheaper cloud regions. Best for archival and cold storage workloads.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>BalancedAuto:</strong> Balance latency and load distribution. Uses proximity
///       scoring with load-based adjustments. Best for general-purpose workloads.
///     </description>
///   </item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Routing policy enumeration for location-aware routing (FOS-04)")]
public enum RoutingPolicy
{
    /// <summary>
    /// Optimize for minimum latency by preferring topologically close nodes.
    /// </summary>
    /// <remarks>
    /// Scoring: same-rack > same-datacenter > same-region > cross-region.
    /// Node load is not considered.
    /// </remarks>
    LatencyOptimized,

    /// <summary>
    /// Optimize for maximum throughput by distributing load across nodes.
    /// </summary>
    /// <remarks>
    /// Scoring: prefers nodes with most available capacity (highest FreeBytes/TotalBytes ratio).
    /// Topology proximity is not considered.
    /// </remarks>
    ThroughputOptimized,

    /// <summary>
    /// Optimize for minimum cost by preferring cheaper storage tiers/regions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoring: on-premises > cheap cloud regions > expensive cloud regions.
    /// </para>
    /// <para>
    /// Cost information is typically embedded in node metadata or region labels.
    /// Implementation-specific: routers may define custom cost scoring logic.
    /// </para>
    /// </remarks>
    CostOptimized,

    /// <summary>
    /// Balanced mode that considers both latency and load.
    /// </summary>
    /// <remarks>
    /// Scoring: combines proximity score with load factor. Nodes that are both close and
    /// less-loaded receive highest scores. This is the recommended default for general workloads.
    /// </remarks>
    BalancedAuto
}
