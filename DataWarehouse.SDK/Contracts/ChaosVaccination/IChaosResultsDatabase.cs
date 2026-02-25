using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.ChaosVaccination
{
    /// <summary>
    /// Contract for storing, querying, and summarizing chaos experiment results.
    /// Provides the persistence layer for the chaos vaccination system.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public interface IChaosResultsDatabase
    {
        /// <summary>
        /// Stores an experiment record.
        /// </summary>
        /// <param name="record">The experiment record to store.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task StoreAsync(ExperimentRecord record, CancellationToken ct = default);

        /// <summary>
        /// Queries experiment records matching the specified criteria.
        /// </summary>
        /// <param name="query">The query parameters.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>Matching experiment records.</returns>
        Task<IReadOnlyList<ExperimentRecord>> QueryAsync(ExperimentQuery query, CancellationToken ct = default);

        /// <summary>
        /// Gets a specific experiment record by experiment ID.
        /// </summary>
        /// <param name="experimentId">The experiment ID to look up.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The experiment record, or null if not found.</returns>
        Task<ExperimentRecord?> GetByIdAsync(string experimentId, CancellationToken ct = default);

        /// <summary>
        /// Computes an aggregate summary of experiment results matching the optional filter.
        /// </summary>
        /// <param name="filter">Optional query filter. Null returns a summary of all records.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>An aggregate summary of experiment results.</returns>
        Task<ExperimentSummary> GetSummaryAsync(ExperimentQuery? filter, CancellationToken ct = default);

        /// <summary>
        /// Purges all experiment records older than the specified date.
        /// </summary>
        /// <param name="olderThan">The cutoff date. Records created before this date are purged.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default);
    }

    /// <summary>
    /// A stored experiment record combining the result with metadata.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ExperimentRecord
    {
        /// <summary>
        /// The chaos experiment result.
        /// </summary>
        public required ChaosExperimentResult Result { get; init; }

        /// <summary>
        /// The schedule ID that triggered this experiment. Null if manually triggered.
        /// </summary>
        public string? Schedule { get; init; }

        /// <summary>
        /// Identifier of who or what triggered this experiment (e.g., "scheduler", "operator:admin", "api").
        /// </summary>
        public required string TriggeredBy { get; init; }

        /// <summary>
        /// When this record was created.
        /// </summary>
        public required DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>
    /// Query parameters for searching experiment records.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ExperimentQuery
    {
        /// <summary>
        /// Filter by fault types. Null means no fault type filter.
        /// </summary>
        public FaultType[]? FaultTypes { get; init; }

        /// <summary>
        /// Filter by experiment statuses. Null means no status filter.
        /// </summary>
        public ExperimentStatus[]? Statuses { get; init; }

        /// <summary>
        /// Only include records created at or after this time. Null means no lower bound.
        /// </summary>
        public DateTimeOffset? Since { get; init; }

        /// <summary>
        /// Only include records created before this time. Null means no upper bound.
        /// </summary>
        public DateTimeOffset? Until { get; init; }

        /// <summary>
        /// Filter by affected plugin IDs. Null means no plugin filter.
        /// </summary>
        public string[]? PluginIds { get; init; }

        /// <summary>
        /// Maximum number of records to return. Default: 100.
        /// </summary>
        public int MaxResults { get; init; } = 100;

        /// <summary>
        /// Whether to order results by descending creation date. Default: true.
        /// </summary>
        public bool OrderByDescending { get; init; } = true;
    }

    /// <summary>
    /// Aggregate summary of experiment results.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ExperimentSummary
    {
        /// <summary>
        /// Total number of experiments matching the filter.
        /// </summary>
        public required int TotalExperiments { get; init; }

        /// <summary>
        /// The rate of successful experiments (0.0 to 1.0).
        /// </summary>
        public required double SuccessRate { get; init; }

        /// <summary>
        /// Average recovery time in milliseconds across all experiments with measured recovery.
        /// </summary>
        public required double AverageRecoveryMs { get; init; }

        /// <summary>
        /// Count of experiments by fault type.
        /// </summary>
        public Dictionary<FaultType, int> MostCommonFaults { get; init; } = new();

        /// <summary>
        /// Distribution of actual blast radii across experiments.
        /// </summary>
        public Dictionary<BlastRadiusLevel, int> BlastRadiusDistribution { get; init; } = new();
    }
}
