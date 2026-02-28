using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

#region Strategy 1: AccessFrequencyValueStrategy

/// <summary>
/// Scores data value based on how frequently the data is accessed, when it was last accessed,
/// and whether the access trend is increasing, stable, or decreasing.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>access_count</term><description>Total number of accesses (int).</description></item>
///   <item><term>last_accessed</term><description>UTC timestamp of the most recent access (DateTime).</description></item>
///   <item><term>access_trend</term><description>Direction of access frequency: "increasing", "stable", or "decreasing" (string).</description></item>
/// </list>
/// When no access metadata is available, returns a neutral score of 50 because absence
/// of data is not proof of no access.
/// </remarks>
public sealed class AccessFrequencyValueStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "value-access-frequency";

    /// <inheritdoc />
    public override string DisplayName => "Access Frequency Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores data value based on access frequency, recency of last access, and access trend direction. " +
        "Higher access frequency and more recent access indicate higher value to the organization.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "access-frequency", "recency", "trend", "usage-pattern"];

    /// <summary>
    /// Computes the access frequency value score for a data object.
    /// </summary>
    /// <param name="metadata">Metadata dictionary containing access_count, last_accessed, and access_trend keys.</param>
    /// <returns>A score from 0 to 100, or 50 (neutral) if no access metadata is present.</returns>
    public double Score(IReadOnlyDictionary<string, object> metadata)
    {
        IncrementCounter("score_invocations");

        bool hasAccessCount = metadata.TryGetValue("access_count", out var accessCountObj);
        bool hasLastAccessed = metadata.TryGetValue("last_accessed", out var lastAccessedObj);
        bool hasAccessTrend = metadata.TryGetValue("access_trend", out var accessTrendObj);

        if (!hasAccessCount && !hasLastAccessed && !hasAccessTrend)
        {
            IncrementCounter("neutral_scores");
            return 50.0;
        }

        int accessCount = hasAccessCount ? Convert.ToInt32(accessCountObj) : 0;
        DateTime lastAccessed = hasLastAccessed ? Convert.ToDateTime(lastAccessedObj) : DateTime.MinValue;
        string accessTrend = hasAccessTrend ? accessTrendObj?.ToString() ?? "stable" : "stable";

        // Base score from access count: up to 50 points
        double baseScore = Math.Min(accessCount / 100.0 * 50.0, 50.0);

        // Recency bonus: up to 50 points based on how recently the data was accessed
        double recencyBonus = 0.0;
        if (hasLastAccessed && lastAccessed > DateTime.MinValue)
        {
            double daysSinceAccess = (DateTime.UtcNow - lastAccessed).TotalDays;
            recencyBonus = daysSinceAccess switch
            {
                <= 7 => 50.0,
                <= 30 => 30.0,
                <= 90 => 10.0,
                _ => 0.0
            };
        }

        // Trend bonus: +10 for increasing, 0 for stable, -10 for decreasing
        double trendBonus = accessTrend?.ToLowerInvariant() switch
        {
            "increasing" => 10.0,
            "stable" => 0.0,
            "decreasing" => -10.0,
            _ => 0.0
        };

        double score = Math.Clamp(baseScore + recencyBonus + trendBonus, 0.0, 100.0);
        IncrementCounter("scored_objects");
        return score;
    }
}

#endregion

#region Strategy 2: LineageDepthValueStrategy

/// <summary>
/// Scores data value based on lineage relationships: how many downstream dependents rely on
/// this data and how deep in the lineage graph the data sits.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>downstream_count</term><description>Number of downstream dependent datasets (int).</description></item>
///   <item><term>upstream_count</term><description>Number of upstream source datasets (int).</description></item>
///   <item><term>lineage_depth</term><description>Depth of the lineage chain from root sources (int).</description></item>
/// </list>
/// More downstream dependents indicate higher value since more systems rely on this data.
/// When no lineage metadata is available, returns 25 (low but not zero, could be a leaf node).
/// </remarks>
public sealed class LineageDepthValueStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "value-lineage-depth";

    /// <inheritdoc />
    public override string DisplayName => "Lineage Depth Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores data value based on lineage depth and downstream dependent count. " +
        "Data with more downstream dependents is more valuable because more systems rely on it.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "lineage", "dependency", "downstream", "upstream"];

    /// <summary>
    /// Computes the lineage depth value score for a data object.
    /// </summary>
    /// <param name="metadata">Metadata dictionary containing downstream_count, upstream_count, and lineage_depth keys.</param>
    /// <returns>A score from 0 to 100, or 25 (low) if no lineage metadata is present.</returns>
    public double Score(IReadOnlyDictionary<string, object> metadata)
    {
        IncrementCounter("score_invocations");

        bool hasDownstream = metadata.TryGetValue("downstream_count", out var downstreamObj);
        bool hasUpstream = metadata.TryGetValue("upstream_count", out var upstreamObj);
        bool hasDepth = metadata.TryGetValue("lineage_depth", out var depthObj);

        if (!hasDownstream && !hasUpstream && !hasDepth)
        {
            IncrementCounter("default_scores");
            return 25.0;
        }

        int downstream = hasDownstream ? Convert.ToInt32(downstreamObj) : 0;
        int depth = hasDepth ? Convert.ToInt32(depthObj) : 0;

        double score = Math.Min((downstream * 10.0) + (depth * 5.0), 100.0);
        IncrementCounter("scored_objects");
        return score;
    }
}

#endregion

#region Strategy 3: UniquenessValueStrategy

/// <summary>
/// Scores data value based on uniqueness: whether duplicates exist, how many, and
/// whether this is the primary copy.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>duplicate_count</term><description>Number of known duplicates (int).</description></item>
///   <item><term>similarity_score</term><description>Similarity to nearest neighbor, 0.0 (unique) to 1.0 (identical) (double).</description></item>
///   <item><term>is_primary_copy</term><description>Whether this is the authoritative copy (bool).</description></item>
/// </list>
/// A primary copy with zero duplicates is maximally valuable (100). Each duplicate reduces
/// the score. Non-primary copies start lower and degrade faster. Similarity above 0.9
/// counts as a near-duplicate even if duplicate_count is zero.
/// </remarks>
public sealed class UniquenessValueStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "value-uniqueness";

    /// <inheritdoc />
    public override string DisplayName => "Uniqueness Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores data value based on uniqueness and duplication status. " +
        "Unique, primary copies are most valuable; heavily duplicated secondary copies score lowest.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "uniqueness", "deduplication", "primary-copy", "similarity"];

    /// <summary>
    /// Computes the uniqueness value score for a data object.
    /// </summary>
    /// <param name="metadata">Metadata dictionary containing duplicate_count, similarity_score, and is_primary_copy keys.</param>
    /// <returns>A score from 0 to 100.</returns>
    public double Score(IReadOnlyDictionary<string, object> metadata)
    {
        IncrementCounter("score_invocations");

        int duplicateCount = metadata.TryGetValue("duplicate_count", out var dupObj)
            ? Convert.ToInt32(dupObj) : 0;
        double similarityScore = metadata.TryGetValue("similarity_score", out var simObj)
            ? Convert.ToDouble(simObj) : 0.0;
        bool isPrimary = metadata.TryGetValue("is_primary_copy", out var primaryObj)
            && Convert.ToBoolean(primaryObj);

        // Similarity above 0.9 counts as a near-duplicate
        if (similarityScore > 0.9 && duplicateCount == 0)
        {
            duplicateCount = 1;
        }

        double score;
        if (isPrimary)
        {
            // Primary copy: starts at 100, each duplicate reduces by 15, floor at 30
            score = Math.Max(100.0 - duplicateCount * 15.0, 30.0);
        }
        else
        {
            // Non-primary copy: starts at 50, each duplicate reduces by 20, floor at 0
            score = Math.Max(50.0 - duplicateCount * 20.0, 0.0);
        }

        IncrementCounter("scored_objects");
        return score;
    }
}

#endregion

#region Strategy 4: FreshnessValueStrategy

/// <summary>
/// Scores data value based on how fresh (recently created or modified) the data is,
/// using exponential decay with a configurable half-life.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>created_at</term><description>UTC creation timestamp (DateTime).</description></item>
///   <item><term>modified_at</term><description>UTC last-modification timestamp (DateTime).</description></item>
///   <item><term>freshness_half_life_days</term><description>Days until value halves; default 90 (int).</description></item>
/// </list>
/// Uses exponential decay: score = 100 * 0.5^(ageDays / halfLifeDays).
/// The most recent timestamp (created_at or modified_at) is used as the reference point.
/// </remarks>
public sealed class FreshnessValueStrategy : ConsciousnessStrategyBase
{
    private const int DefaultHalfLifeDays = 90;

    /// <inheritdoc />
    public override string StrategyId => "value-freshness";

    /// <inheritdoc />
    public override string DisplayName => "Freshness Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores data value based on freshness using exponential decay. " +
        "More recently created or modified data is more valuable; value halves every half-life period.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "freshness", "age", "decay", "half-life", "temporal"];

    /// <summary>
    /// Computes the freshness value score for a data object.
    /// </summary>
    /// <param name="metadata">Metadata dictionary containing created_at, modified_at, and freshness_half_life_days keys.</param>
    /// <returns>A score from 0 to 100 based on exponential decay.</returns>
    public double Score(IReadOnlyDictionary<string, object> metadata)
    {
        IncrementCounter("score_invocations");

        bool hasCreated = metadata.TryGetValue("created_at", out var createdObj);
        bool hasModified = metadata.TryGetValue("modified_at", out var modifiedObj);

        if (!hasCreated && !hasModified)
        {
            IncrementCounter("no_timestamp_scores");
            return 0.0; // No timestamp means we cannot assess freshness
        }

        DateTime createdAt = hasCreated ? Convert.ToDateTime(createdObj) : DateTime.MinValue;
        DateTime modifiedAt = hasModified ? Convert.ToDateTime(modifiedObj) : DateTime.MinValue;

        // Use the most recent timestamp
        DateTime referenceDate = modifiedAt > createdAt ? modifiedAt : createdAt;

        int halfLifeDays = metadata.TryGetValue("freshness_half_life_days", out var halfLifeObj)
            ? Convert.ToInt32(halfLifeObj) : DefaultHalfLifeDays;

        if (halfLifeDays <= 0) halfLifeDays = DefaultHalfLifeDays;

        double ageDays = (DateTime.UtcNow - referenceDate).TotalDays;
        if (ageDays < 0) ageDays = 0; // Future dates treated as just-created

        double score = Math.Clamp(100.0 * Math.Pow(0.5, ageDays / halfLifeDays), 0.0, 100.0);
        IncrementCounter("scored_objects");
        return score;
    }
}

#endregion

#region Strategy 5: BusinessCriticalityValueStrategy

/// <summary>
/// Scores data value based on business criticality: the tier assignment, owning business unit,
/// and direct/indirect revenue impact.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>business_unit</term><description>Name of the owning business unit (string).</description></item>
///   <item><term>criticality_tier</term><description>Criticality tier from 1 (mission-critical) to 5 (non-critical) (int).</description></item>
///   <item><term>revenue_impact</term><description>Revenue impact category: "direct", "indirect", or "none" (string).</description></item>
/// </list>
/// Tier 1 = 100, Tier 2 = 80, Tier 3 = 60, Tier 4 = 40, Tier 5 = 20.
/// Revenue impact bonus: direct = +20, indirect = +10, none = 0. Final score capped at 100.
/// </remarks>
public sealed class BusinessCriticalityValueStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "value-business-criticality";

    /// <inheritdoc />
    public override string DisplayName => "Business Criticality Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores data value based on business criticality tier and revenue impact. " +
        "Mission-critical data with direct revenue impact scores highest.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "business-criticality", "tier", "revenue-impact", "business-unit"];

    /// <summary>
    /// Computes the business criticality value score for a data object.
    /// </summary>
    /// <param name="metadata">Metadata dictionary containing criticality_tier, business_unit, and revenue_impact keys.</param>
    /// <returns>A score from 0 to 100.</returns>
    public double Score(IReadOnlyDictionary<string, object> metadata)
    {
        IncrementCounter("score_invocations");

        int tier = metadata.TryGetValue("criticality_tier", out var tierObj)
            ? Convert.ToInt32(tierObj) : 3; // Default to mid-tier

        // Tier score: 1=100, 2=80, 3=60, 4=40, 5=20
        double tierScore = tier switch
        {
            1 => 100.0,
            2 => 80.0,
            3 => 60.0,
            4 => 40.0,
            5 => 20.0,
            _ => tier < 1 ? 100.0 : 20.0 // Out-of-range: clamp
        };

        // Revenue impact bonus
        string revenueImpact = metadata.TryGetValue("revenue_impact", out var revenueObj)
            ? revenueObj?.ToString() ?? "none" : "none";

        double revenueBonus = revenueImpact.ToLowerInvariant() switch
        {
            "direct" => 20.0,
            "indirect" => 10.0,
            _ => 0.0
        };

        double score = Math.Min(tierScore + revenueBonus, 100.0);
        IncrementCounter("scored_objects");
        return score;
    }
}

#endregion

#region Strategy 6: ComplianceValueStrategy

/// <summary>
/// Scores data value based on compliance and regulatory obligations: applicable regulatory
/// frameworks, audit requirements, and legal hold status.
/// </summary>
/// <remarks>
/// Metadata keys consumed:
/// <list type="bullet">
///   <item><term>regulatory_frameworks</term><description>Array of applicable regulatory frameworks (string[]).</description></item>
///   <item><term>audit_required</term><description>Whether the data requires audit trail (bool).</description></item>
///   <item><term>legal_hold</term><description>Whether the data is under legal hold (bool).</description></item>
/// </list>
/// Legal hold = automatic 100 (data cannot be archived or purged under any circumstance).
/// Audit required = minimum 80. Each regulatory framework adds 10 points (GDPR, HIPAA, SOX, etc.),
/// capped at 100.
/// </remarks>
public sealed class ComplianceValueStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "value-compliance";

    /// <inheritdoc />
    public override string DisplayName => "Compliance Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scores data value based on compliance obligations, regulatory frameworks, audit requirements, " +
        "and legal hold status. Data under legal hold always scores 100.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "compliance", "regulatory", "legal-hold", "audit", "gdpr", "hipaa", "sox"];

    /// <summary>
    /// Computes the compliance value score for a data object.
    /// </summary>
    /// <param name="metadata">Metadata dictionary containing regulatory_frameworks, audit_required, and legal_hold keys.</param>
    /// <returns>A score from 0 to 100.</returns>
    public double Score(IReadOnlyDictionary<string, object> metadata)
    {
        IncrementCounter("score_invocations");

        // Legal hold = automatic 100
        if (metadata.TryGetValue("legal_hold", out var legalHoldObj) && Convert.ToBoolean(legalHoldObj))
        {
            IncrementCounter("legal_hold_scores");
            return 100.0;
        }

        double score = 0.0;

        // Audit required = minimum 80
        if (metadata.TryGetValue("audit_required", out var auditObj) && Convert.ToBoolean(auditObj))
        {
            score = 80.0;
        }

        // Each regulatory framework adds 10 points
        if (metadata.TryGetValue("regulatory_frameworks", out var frameworksObj))
        {
            int frameworkCount = 0;
            if (frameworksObj is string[] frameworks)
            {
                frameworkCount = frameworks.Length;
            }
            else if (frameworksObj is IEnumerable<object> frameworkList)
            {
                frameworkCount = frameworkList.Count();
            }
            else if (frameworksObj is IEnumerable<string> frameworkStrings)
            {
                frameworkCount = frameworkStrings.Count();
            }

            // Framework bonus: each framework adds 10 points, take max with existing (audit) base.
            var frameworkBonus = frameworkCount * 10.0;
            score = Math.Max(score, frameworkBonus);
        }

        // If audit is required but no frameworks, ensure minimum 80
        if (metadata.TryGetValue("audit_required", out var auditFinal) && Convert.ToBoolean(auditFinal))
        {
            score = Math.Max(score, 80.0);
        }

        score = Math.Min(score, 100.0);
        IncrementCounter("scored_objects");
        return score;
    }
}

#endregion

#region Strategy 7: CompositeValueScoringStrategy

/// <summary>
/// Aggregates all 6 value dimension strategies into a single composite value score with
/// configurable weights per dimension. Implements <see cref="IValueScorer"/> to serve
/// as the primary entry point for value scoring in the consciousness system.
/// </summary>
/// <remarks>
/// Default weights:
/// <list type="bullet">
///   <item><term>AccessFrequency</term><description>0.25</description></item>
///   <item><term>Lineage</term><description>0.15</description></item>
///   <item><term>Uniqueness</term><description>0.15</description></item>
///   <item><term>Freshness</term><description>0.20</description></item>
///   <item><term>BusinessCriticality</term><description>0.15</description></item>
///   <item><term>ComplianceValue</term><description>0.10</description></item>
/// </list>
/// Weights can be overridden via <see cref="ConsciousnessScoringConfig.ValueWeights"/>.
/// </remarks>
public sealed class CompositeValueScoringStrategy : ConsciousnessStrategyBase, IValueScorer
{
    private static readonly IReadOnlyDictionary<ValueDimension, double> DefaultWeights =
        new Dictionary<ValueDimension, double>
        {
            [ValueDimension.AccessFrequency] = 0.25,
            [ValueDimension.Lineage] = 0.15,
            [ValueDimension.Uniqueness] = 0.15,
            [ValueDimension.Freshness] = 0.20,
            [ValueDimension.BusinessCriticality] = 0.15,
            [ValueDimension.ComplianceValue] = 0.10
        };

    private readonly AccessFrequencyValueStrategy _accessFrequency = new();
    private readonly LineageDepthValueStrategy _lineageDepth = new();
    private readonly UniquenessValueStrategy _uniqueness = new();
    private readonly FreshnessValueStrategy _freshness = new();
    private readonly BusinessCriticalityValueStrategy _businessCriticality = new();
    private readonly ComplianceValueStrategy _compliance = new();

    private ConsciousnessScoringConfig _config = new();

    /// <inheritdoc />
    public override string StrategyId => "value-composite";

    /// <inheritdoc />
    public override string DisplayName => "Composite Value Scorer";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.ValueScoring;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRealTime: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Composite value scorer that aggregates all 6 value dimension strategies " +
        "(access frequency, lineage depth, uniqueness, freshness, business criticality, compliance) " +
        "with configurable weights into a single 0-100 value score.";

    /// <inheritdoc />
    public override string[] Tags => ["value", "composite", "aggregator", "weighted", "consciousness"];

    /// <summary>
    /// Configures the composite scorer with custom weights and thresholds.
    /// </summary>
    /// <param name="config">The scoring configuration to apply.</param>
    public void Configure(ConsciousnessScoringConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    public Task<ValueScore> ScoreValueAsync(
        string objectId,
        byte[] data,
        IReadOnlyDictionary<string, object> metadata,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("score_invocations");

        // Score each dimension
        double accessScore = _accessFrequency.Score(metadata);
        double lineageScore = _lineageDepth.Score(metadata);
        double uniquenessScore = _uniqueness.Score(metadata);
        double freshnessScore = _freshness.Score(metadata);
        double criticalityScore = _businessCriticality.Score(metadata);
        double complianceScore = _compliance.Score(metadata);

        // Get configured or default weights
        var weights = _config.ValueWeights ?? DefaultWeights;

        // Compute weighted aggregate
        double overall =
            accessScore * GetWeight(weights, ValueDimension.AccessFrequency) +
            lineageScore * GetWeight(weights, ValueDimension.Lineage) +
            uniquenessScore * GetWeight(weights, ValueDimension.Uniqueness) +
            freshnessScore * GetWeight(weights, ValueDimension.Freshness) +
            criticalityScore * GetWeight(weights, ValueDimension.BusinessCriticality) +
            complianceScore * GetWeight(weights, ValueDimension.ComplianceValue);

        overall = Math.Clamp(overall, 0.0, 100.0);

        // Build dimension scores dictionary
        var dimensionScores = new Dictionary<ValueDimension, double>
        {
            [ValueDimension.AccessFrequency] = accessScore,
            [ValueDimension.Lineage] = lineageScore,
            [ValueDimension.Uniqueness] = uniquenessScore,
            [ValueDimension.Freshness] = freshnessScore,
            [ValueDimension.BusinessCriticality] = criticalityScore,
            [ValueDimension.ComplianceValue] = complianceScore
        };

        // Build value drivers list (dimensions scoring above 70)
        var drivers = new List<string>();
        if (accessScore >= 70) drivers.Add($"High access frequency (score: {accessScore:F1})");
        if (lineageScore >= 70) drivers.Add($"Deep lineage with many dependents (score: {lineageScore:F1})");
        if (uniquenessScore >= 70) drivers.Add($"Highly unique data (score: {uniquenessScore:F1})");
        if (freshnessScore >= 70) drivers.Add($"Very fresh data (score: {freshnessScore:F1})");
        if (criticalityScore >= 70) drivers.Add($"High business criticality (score: {criticalityScore:F1})");
        if (complianceScore >= 70) drivers.Add($"Significant compliance value (score: {complianceScore:F1})");

        if (drivers.Count == 0)
        {
            drivers.Add($"No dominant value dimension (overall: {overall:F1})");
        }

        var valueScore = new ValueScore(
            OverallScore: overall,
            DimensionScores: dimensionScores,
            ValueDrivers: drivers.AsReadOnly(),
            ScoredAt: DateTime.UtcNow);

        IncrementCounter("scored_objects");
        return Task.FromResult(valueScore);
    }

    /// <summary>
    /// Gets the weight for a given dimension, falling back to the default weights.
    /// </summary>
    private static double GetWeight(IReadOnlyDictionary<ValueDimension, double> weights, ValueDimension dimension)
    {
        return weights.TryGetValue(dimension, out var weight) ? weight : DefaultWeights[dimension];
    }
}

#endregion
