using System;
using System.IO;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage.Billing;
using DataWarehouse.SDK.Storage.Placement;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.ZeroGravity;

/// <summary>
/// Configuration options for the zero-gravity storage strategy.
/// Controls subsystem behavior including CRUSH placement, gravity-aware optimization,
/// autonomous rebalancing, background migration, billing integration, and cost optimization.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity storage strategy")]
public sealed record ZeroGravityStorageOptions
{
    /// <summary>
    /// Enable autonomous rebalancing. When enabled, the rebalancer continuously monitors
    /// cluster balance and triggers gravity-aware data migrations.
    /// </summary>
    public bool EnableRebalancing { get; init; } = true;

    /// <summary>
    /// Enable cost optimization recommendations. When enabled, billing data is analyzed
    /// to generate spot/reserved/tier/arbitrage recommendations.
    /// </summary>
    public bool EnableCostOptimization { get; init; } = true;

    /// <summary>
    /// Enable billing API integration (requires cloud credentials).
    /// When false, cost optimization operates without real billing data.
    /// </summary>
    public bool EnableBillingIntegration { get; init; } = false;

    /// <summary>
    /// Rebalancer behavior configuration including check interval, imbalance threshold,
    /// concurrent migration limits, quiet hours, and budget controls.
    /// </summary>
    public RebalancerOptions RebalancerOptions { get; init; } = RebalancerOptions.Default;

    /// <summary>
    /// Gravity scoring weights for the five gravity dimensions:
    /// access frequency, colocation, egress cost, latency, and compliance.
    /// </summary>
    public GravityScoringWeights GravityWeights { get; init; } = GravityScoringWeights.Default;

    /// <summary>
    /// CRUSH stripe count for write parallelism. Higher values increase write throughput
    /// but may reduce read locality for small objects.
    /// </summary>
    public int CrushStripeCount { get; init; } = 64;

    /// <summary>
    /// Default replica count for stored objects. CRUSH places objects across this many
    /// distinct failure domains (zones/racks/hosts).
    /// </summary>
    public int DefaultReplicaCount { get; init; } = 3;

    /// <summary>
    /// Migration checkpoint directory for crash recovery. The background migration engine
    /// saves progress after each batch to this location.
    /// </summary>
    public string CheckpointDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "dw-migration-checkpoints");

    /// <summary>
    /// Cost optimizer thresholds and configuration including minimum savings,
    /// spot risk tolerance, reserved capacity minimums, and cold tier access thresholds.
    /// </summary>
    public StorageCostOptimizerOptions CostOptimizerOptions { get; init; } = new();

    /// <summary>
    /// Message bus topic for rebalance events. The rebalancer publishes job status,
    /// migration progress, and completion events to this topic.
    /// </summary>
    public string RebalanceEventTopic { get; init; } = "storage.zerogravity.rebalance";

    /// <summary>
    /// Message bus topic for cost optimization events. The cost optimizer publishes
    /// recommendation plans and savings reports to this topic.
    /// </summary>
    public string CostEventTopic { get; init; } = "storage.zerogravity.cost";

    /// <summary>
    /// Batch size for migration engine checkpoint intervals.
    /// The engine saves a checkpoint after processing this many objects.
    /// </summary>
    public int MigrationBatchSize { get; init; } = 100;

    /// <summary>
    /// Read forwarding TTL. During migration, reads for migrated objects are forwarded
    /// to the new location for this duration before the forwarding entry expires.
    /// </summary>
    public TimeSpan ReadForwardingTtl { get; init; } = TimeSpan.FromHours(24);
}
