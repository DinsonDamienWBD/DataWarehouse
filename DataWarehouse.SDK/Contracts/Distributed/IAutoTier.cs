using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for automatic data placement by access patterns (DIST-06).
    /// Evaluates data access frequency, size, and age to recommend optimal tier placement.
    /// This is a decision contract -- it does NOT replace <see cref="ITieredStorage"/>
    /// or the composable <c>ITierManager</c> service.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IAutoTier
    {
        /// <summary>
        /// Raised when a tier event occurs (evaluation, migration started/completed/failed).
        /// </summary>
        event Action<TierEvent>? OnTierEvent;

        /// <summary>
        /// Evaluates the optimal tier placement for a data item.
        /// </summary>
        /// <param name="context">The evaluation context with data access patterns.</param>
        /// <param name="ct">Cancellation token for the evaluation operation.</param>
        /// <returns>A tier placement recommendation.</returns>
        Task<TierPlacement> EvaluatePlacementAsync(TierEvaluationContext context, CancellationToken ct = default);

        /// <summary>
        /// Migrates a data item between tiers.
        /// </summary>
        /// <param name="request">The migration request specifying source and target tiers.</param>
        /// <param name="ct">Cancellation token for the migration operation.</param>
        /// <returns>The result of the migration.</returns>
        Task<TierMigrationResult> MigrateAsync(TierMigrationRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets information about all available tiers.
        /// </summary>
        /// <param name="ct">Cancellation token for the retrieval operation.</param>
        /// <returns>A read-only list of tier information.</returns>
        Task<IReadOnlyList<TierInfo>> GetTiersAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// A tier placement recommendation from the auto-tiering engine.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record TierPlacement
    {
        /// <summary>
        /// The key of the data item being evaluated.
        /// </summary>
        public required string DataKey { get; init; }

        /// <summary>
        /// The recommended tier for the data item.
        /// </summary>
        public required string RecommendedTier { get; init; }

        /// <summary>
        /// The current tier of the data item.
        /// </summary>
        public required string CurrentTier { get; init; }

        /// <summary>
        /// Reason for the recommendation.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Confidence score for the recommendation (0.0 to 1.0).
        /// </summary>
        public required double ConfidenceScore { get; init; }
    }

    /// <summary>
    /// Context for evaluating tier placement.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record TierEvaluationContext
    {
        /// <summary>
        /// The key of the data item to evaluate.
        /// </summary>
        public required string DataKey { get; init; }

        /// <summary>
        /// Size of the data item in bytes.
        /// </summary>
        public required long DataSizeBytes { get; init; }

        /// <summary>
        /// Number of times the data item has been accessed.
        /// </summary>
        public required int AccessCount { get; init; }

        /// <summary>
        /// When the data item was last accessed.
        /// </summary>
        public required DateTimeOffset LastAccessedAt { get; init; }

        /// <summary>
        /// When the data item was created.
        /// </summary>
        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>
        /// Additional metadata about the data item.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Request to migrate a data item between tiers.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record TierMigrationRequest
    {
        /// <summary>
        /// The key of the data item to migrate.
        /// </summary>
        public required string DataKey { get; init; }

        /// <summary>
        /// The tier the data item is currently in.
        /// </summary>
        public required string SourceTier { get; init; }

        /// <summary>
        /// The tier to migrate the data item to.
        /// </summary>
        public required string TargetTier { get; init; }

        /// <summary>
        /// Whether to delete the data from the source tier after migration.
        /// </summary>
        public bool DeleteFromSource { get; init; } = true;
    }

    /// <summary>
    /// Result of a tier migration operation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record TierMigrationResult
    {
        /// <summary>
        /// Whether the migration completed successfully.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// The key of the migrated data item.
        /// </summary>
        public required string DataKey { get; init; }

        /// <summary>
        /// The source tier.
        /// </summary>
        public required string SourceTier { get; init; }

        /// <summary>
        /// The target tier.
        /// </summary>
        public required string TargetTier { get; init; }

        /// <summary>
        /// How long the migration took.
        /// </summary>
        public required TimeSpan Duration { get; init; }

        /// <summary>
        /// Error message if the migration failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Creates a successful migration result.
        /// </summary>
        public static TierMigrationResult Ok(string dataKey, string sourceTier, string targetTier, TimeSpan duration) =>
            new() { Success = true, DataKey = dataKey, SourceTier = sourceTier, TargetTier = targetTier, Duration = duration };

        /// <summary>
        /// Creates a failed migration result.
        /// </summary>
        public static TierMigrationResult Error(string dataKey, string sourceTier, string targetTier, string errorMessage) =>
            new() { Success = false, DataKey = dataKey, SourceTier = sourceTier, TargetTier = targetTier, Duration = TimeSpan.Zero, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Information about a storage tier.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record TierInfo
    {
        /// <summary>
        /// The name of the tier.
        /// </summary>
        public required string TierName { get; init; }

        /// <summary>
        /// Human-readable description of the tier.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Total capacity of the tier in bytes.
        /// </summary>
        public required long CapacityBytes { get; init; }

        /// <summary>
        /// Currently used capacity in bytes.
        /// </summary>
        public required long UsedBytes { get; init; }

        /// <summary>
        /// Cost per GB per month for this tier.
        /// </summary>
        public required double CostPerGBMonth { get; init; }

        /// <summary>
        /// Performance class of this tier.
        /// </summary>
        public required TierPerformanceClass PerformanceClass { get; init; }
    }

    /// <summary>
    /// Performance classes for storage tiers.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum TierPerformanceClass
    {
        /// <summary>Highest performance, most expensive. For frequently accessed data.</summary>
        Hot,
        /// <summary>Good performance, moderate cost. For regularly accessed data.</summary>
        Warm,
        /// <summary>Lower performance, lower cost. For infrequently accessed data.</summary>
        Cool,
        /// <summary>Minimal performance, low cost. For rarely accessed data.</summary>
        Cold,
        /// <summary>Lowest performance, cheapest. For long-term retention.</summary>
        Archive
    }

    /// <summary>
    /// A tier event describing tier-related operations.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record TierEvent
    {
        /// <summary>
        /// The type of tier event.
        /// </summary>
        public required TierEventType EventType { get; init; }

        /// <summary>
        /// The data key involved in the event.
        /// </summary>
        public required string DataKey { get; init; }

        /// <summary>
        /// The source tier, if applicable.
        /// </summary>
        public string? FromTier { get; init; }

        /// <summary>
        /// The target tier, if applicable.
        /// </summary>
        public string? ToTier { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Types of tier events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum TierEventType
    {
        /// <summary>Placement was evaluated for a data item.</summary>
        PlacementEvaluated,
        /// <summary>Migration was started for a data item.</summary>
        MigrationStarted,
        /// <summary>Migration completed successfully.</summary>
        MigrationCompleted,
        /// <summary>Migration failed.</summary>
        MigrationFailed
    }
}
