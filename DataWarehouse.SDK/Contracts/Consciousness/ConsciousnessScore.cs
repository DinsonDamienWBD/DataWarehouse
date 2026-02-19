using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Contracts.Consciousness
{
    /// <summary>
    /// Grade assigned to a data object based on its consciousness score.
    /// Mirrors QualityGrade pattern from UltimateDataQuality.
    /// </summary>
    public enum ConsciousnessGrade
    {
        /// <summary>Score >= 90: Data is fully understood, governed, and optimally managed.</summary>
        Enlightened,
        /// <summary>Score >= 75: Data is well-characterized with known value and liability.</summary>
        Aware,
        /// <summary>Score >= 50: Data has partial characterization; gaps exist.</summary>
        Dormant,
        /// <summary>Score >= 25: Data is poorly understood with significant unknowns.</summary>
        Dark,
        /// <summary>Score &lt; 25: Data consciousness is effectively absent.</summary>
        Unknown
    }

    /// <summary>
    /// Recommended lifecycle action based on the value/liability quadrant analysis.
    /// </summary>
    public enum ConsciousnessAction
    {
        /// <summary>High value, low liability: Keep and invest in this data.</summary>
        Retain,
        /// <summary>Low value, low liability: Move to cold storage to reduce cost.</summary>
        Archive,
        /// <summary>High value, high liability: Needs human review for risk mitigation.</summary>
        Review,
        /// <summary>Low value, high liability: Eliminate to reduce risk exposure.</summary>
        Purge,
        /// <summary>Anomalous or unclassifiable: Isolate until further analysis.</summary>
        Quarantine
    }

    /// <summary>
    /// Dimensions that contribute to a data object's value score.
    /// </summary>
    public enum ValueDimension
    {
        /// <summary>How frequently the data is accessed or queried.</summary>
        AccessFrequency,
        /// <summary>How many downstream dependents rely on this data.</summary>
        Lineage,
        /// <summary>Whether the data is duplicated elsewhere in the warehouse.</summary>
        Uniqueness,
        /// <summary>Age vs staleness — how current the data is.</summary>
        Freshness,
        /// <summary>How critical the data is to business operations and decisions.</summary>
        BusinessCriticality,
        /// <summary>Value derived from regulatory or compliance requirements.</summary>
        ComplianceValue
    }

    /// <summary>
    /// Dimensions that contribute to a data object's liability score.
    /// </summary>
    public enum LiabilityDimension
    {
        /// <summary>Presence of personally identifiable information.</summary>
        PIIPresence,
        /// <summary>Presence of protected health information.</summary>
        PHIPresence,
        /// <summary>Presence of payment card industry data.</summary>
        PCIPresence,
        /// <summary>Data classification level (public, internal, confidential, restricted).</summary>
        ClassificationLevel,
        /// <summary>Legal or regulatory retention obligations.</summary>
        RetentionObligation,
        /// <summary>Exposure to regulatory action or fines.</summary>
        RegulatoryExposure,
        /// <summary>Risk and impact of a data breach involving this data.</summary>
        BreachRisk
    }

    /// <summary>
    /// Immutable score representing the assessed value of a data object across multiple dimensions.
    /// </summary>
    /// <param name="OverallScore">Aggregate value score from 0 (no value) to 100 (maximum value).</param>
    /// <param name="DimensionScores">Per-dimension breakdown of the value score.</param>
    /// <param name="ValueDrivers">Human-readable descriptions of what drives this object's value.</param>
    /// <param name="ScoredAt">UTC timestamp when this value score was computed.</param>
    public sealed record ValueScore(
        double OverallScore,
        IReadOnlyDictionary<ValueDimension, double> DimensionScores,
        IReadOnlyList<string> ValueDrivers,
        DateTime ScoredAt);

    /// <summary>
    /// Immutable score representing the assessed liability of a data object across multiple dimensions.
    /// </summary>
    /// <param name="OverallScore">Aggregate liability score from 0 (no liability) to 100 (maximum liability).</param>
    /// <param name="DimensionScores">Per-dimension breakdown of the liability score.</param>
    /// <param name="LiabilityFactors">Human-readable descriptions of what drives this object's liability.</param>
    /// <param name="DetectedPIITypes">Types of PII detected (e.g., SSN, email, phone).</param>
    /// <param name="ApplicableRegulations">Regulations that apply to this data (e.g., GDPR, HIPAA, CCPA).</param>
    /// <param name="ScoredAt">UTC timestamp when this liability score was computed.</param>
    public sealed record LiabilityScore(
        double OverallScore,
        IReadOnlyDictionary<LiabilityDimension, double> DimensionScores,
        IReadOnlyList<string> LiabilityFactors,
        IReadOnlyList<string> DetectedPIITypes,
        IReadOnlyList<string> ApplicableRegulations,
        DateTime ScoredAt);

    /// <summary>
    /// Immutable composite consciousness score for a data object, combining value and liability
    /// assessments into a single 0-100 score with computed grade and recommended action.
    /// </summary>
    /// <param name="ObjectId">Unique identifier of the scored data object.</param>
    /// <param name="CompositeScore">Overall consciousness score from 0 (unknown) to 100 (enlightened).</param>
    /// <param name="Value">The value assessment component.</param>
    /// <param name="Liability">The liability assessment component.</param>
    /// <param name="ScoringStrategy">Identifier of the strategy that produced this score.</param>
    /// <param name="ScoredAt">UTC timestamp when this composite score was computed.</param>
    /// <param name="Metadata">Extensible metadata bag for strategy-specific additional context.</param>
    public sealed record ConsciousnessScore(
        string ObjectId,
        double CompositeScore,
        ValueScore Value,
        LiabilityScore Liability,
        string ScoringStrategy,
        DateTime ScoredAt,
        IReadOnlyDictionary<string, object>? Metadata = null)
    {
        /// <summary>
        /// Gets the consciousness grade computed from the composite score.
        /// </summary>
        public ConsciousnessGrade Grade => CompositeScore switch
        {
            >= 90 => ConsciousnessGrade.Enlightened,
            >= 75 => ConsciousnessGrade.Aware,
            >= 50 => ConsciousnessGrade.Dormant,
            >= 25 => ConsciousnessGrade.Dark,
            _ => ConsciousnessGrade.Unknown
        };

        /// <summary>
        /// Gets the recommended lifecycle action based on value/liability quadrant analysis.
        /// High/low boundary is 50. High value + low liability = Retain, high value + high liability = Review,
        /// low value + low liability = Archive, low value + high liability = Purge.
        /// </summary>
        public ConsciousnessAction RecommendedAction
        {
            get
            {
                bool highValue = Value.OverallScore >= 50.0;
                bool highLiability = Liability.OverallScore >= 50.0;

                return (highValue, highLiability) switch
                {
                    (true, false) => ConsciousnessAction.Retain,
                    (true, true) => ConsciousnessAction.Review,
                    (false, false) => ConsciousnessAction.Archive,
                    (false, true) => ConsciousnessAction.Purge
                };
            }
        }
    }

    /// <summary>
    /// Configuration for consciousness scoring, controlling dimension weights, composite ratio,
    /// action thresholds, and batch processing parameters.
    /// </summary>
    /// <param name="ValueWeights">
    /// Weight for each value dimension. Must sum to 1.0.
    /// Default: even distribution across all <see cref="ValueDimension"/> values.
    /// </param>
    /// <param name="LiabilityWeights">
    /// Weight for each liability dimension. Must sum to 1.0.
    /// Default: even distribution across all <see cref="LiabilityDimension"/> values.
    /// </param>
    /// <param name="ValueLiabilityRatio">
    /// How much value contributes to the composite score (0.0-1.0). Liability contribution is 1 - this value.
    /// Default: 0.6 (value contributes 60%, liability contributes 40%).
    /// </param>
    /// <param name="ArchiveThreshold">Composite score below which Archive is suggested. Default: 30.</param>
    /// <param name="PurgeThreshold">Composite score below which Purge is suggested. Default: 20.</param>
    /// <param name="ReviewThreshold">Composite score above which Review is suggested. Default: 70.</param>
    /// <param name="ScoringBatchSize">Maximum number of objects to score in a single batch. Default: 1000.</param>
    public sealed record ConsciousnessScoringConfig(
        IReadOnlyDictionary<ValueDimension, double>? ValueWeights = null,
        IReadOnlyDictionary<LiabilityDimension, double>? LiabilityWeights = null,
        double ValueLiabilityRatio = 0.6,
        double ArchiveThreshold = 30.0,
        double PurgeThreshold = 20.0,
        double ReviewThreshold = 70.0,
        int ScoringBatchSize = 1000)
    {
        /// <summary>
        /// Gets the effective value weights, using even distribution if none were provided.
        /// </summary>
        public IReadOnlyDictionary<ValueDimension, double> EffectiveValueWeights =>
            ValueWeights ?? CreateEvenWeights<ValueDimension>();

        /// <summary>
        /// Gets the effective liability weights, using even distribution if none were provided.
        /// </summary>
        public IReadOnlyDictionary<LiabilityDimension, double> EffectiveLiabilityWeights =>
            LiabilityWeights ?? CreateEvenWeights<LiabilityDimension>();

        /// <summary>
        /// Creates an even weight distribution for all values of the specified enum type.
        /// </summary>
        private static IReadOnlyDictionary<TEnum, double> CreateEvenWeights<TEnum>() where TEnum : struct, Enum
        {
            var values = Enum.GetValues<TEnum>();
            double weight = 1.0 / values.Length;
            return values.ToDictionary(v => v, _ => weight);
        }
    }
}
