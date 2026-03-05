using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Consciousness
{
    /// <summary>
    /// Scores the value of a data object across multiple dimensions (access frequency,
    /// lineage, uniqueness, freshness, business criticality, compliance value).
    /// </summary>
    public interface IValueScorer
    {
        /// <summary>
        /// Computes a value score for a single data object.
        /// </summary>
        /// <param name="objectId">Unique identifier of the data object.</param>
        /// <param name="data">Raw data bytes of the object.</param>
        /// <param name="metadata">Additional metadata about the object (e.g., schema, tags, access logs).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="ValueScore"/> with overall and per-dimension scores.</returns>
        Task<ValueScore> ScoreValueAsync(
            string objectId,
            byte[] data,
            IReadOnlyDictionary<string, object> metadata,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Scores the liability of a data object across multiple dimensions (PII presence,
    /// PHI presence, PCI presence, classification, retention, regulatory exposure, breach risk).
    /// </summary>
    public interface ILiabilityScorer
    {
        /// <summary>
        /// Computes a liability score for a single data object.
        /// </summary>
        /// <param name="objectId">Unique identifier of the data object.</param>
        /// <param name="data">Raw data bytes of the object.</param>
        /// <param name="metadata">Additional metadata about the object (e.g., schema, tags, classification).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="LiabilityScore"/> with overall and per-dimension scores.</returns>
        Task<LiabilityScore> ScoreLiabilityAsync(
            string objectId,
            byte[] data,
            IReadOnlyDictionary<string, object> metadata,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Computes the composite consciousness score for data objects by combining
    /// value and liability assessments into a single 0-100 score with grade and action.
    /// Supports both single-object and batch scoring.
    /// </summary>
    public interface IConsciousnessScorer
    {
        /// <summary>
        /// Computes the composite consciousness score for a single data object.
        /// </summary>
        /// <param name="objectId">Unique identifier of the data object.</param>
        /// <param name="data">Raw data bytes of the object.</param>
        /// <param name="metadata">Additional metadata about the object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="ConsciousnessScore"/> with composite score, grade, and recommended action.</returns>
        Task<ConsciousnessScore> ScoreAsync(
            string objectId,
            byte[] data,
            IReadOnlyDictionary<string, object> metadata,
            CancellationToken ct = default);

        /// <summary>
        /// Computes consciousness scores for a batch of data objects.
        /// Implementations should respect <see cref="ConsciousnessScoringConfig.ScoringBatchSize"/>.
        /// </summary>
        /// <param name="batch">Collection of (objectId, data, metadata) tuples to score.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A read-only list of consciousness scores, one per input object.</returns>
        Task<IReadOnlyList<ConsciousnessScore>> ScoreBatchAsync(
            IReadOnlyList<(string objectId, byte[] data, Dictionary<string, object> metadata)> batch,
            CancellationToken ct = default);
    }
}
