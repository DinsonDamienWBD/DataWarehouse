using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Scoring;

#region Scoring Types

/// <summary>
/// Quality score for a single record.
/// </summary>
public sealed class RecordQualityScore
{
    /// <summary>
    /// Record ID.
    /// </summary>
    public required string RecordId { get; init; }

    /// <summary>
    /// Overall quality score (0-100).
    /// </summary>
    public double OverallScore { get; init; }

    /// <summary>
    /// Quality grade based on score.
    /// </summary>
    public QualityGrade Grade => OverallScore switch
    {
        >= 95 => QualityGrade.Excellent,
        >= 85 => QualityGrade.Good,
        >= 70 => QualityGrade.Acceptable,
        >= 50 => QualityGrade.Poor,
        _ => QualityGrade.Critical
    };

    /// <summary>
    /// Dimension scores.
    /// </summary>
    public Dictionary<QualityDimension, double> DimensionScores { get; init; } = new();

    /// <summary>
    /// Field-level scores.
    /// </summary>
    public Dictionary<string, FieldQualityScore> FieldScores { get; init; } = new();

    /// <summary>
    /// Issues affecting the score.
    /// </summary>
    public List<string> Issues { get; init; } = new();

    /// <summary>
    /// Improvement recommendations.
    /// </summary>
    public List<string> Recommendations { get; init; } = new();
}

/// <summary>
/// Quality score for a single field.
/// </summary>
public sealed class FieldQualityScore
{
    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Field score (0-100).
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Dimension scores for this field.
    /// </summary>
    public Dictionary<QualityDimension, double> Dimensions { get; init; } = new();

    /// <summary>
    /// Issues found.
    /// </summary>
    public List<string> Issues { get; init; } = new();
}

/// <summary>
/// Quality grade levels.
/// </summary>
public enum QualityGrade
{
    /// <summary>Score >= 95.</summary>
    Excellent,
    /// <summary>Score >= 85.</summary>
    Good,
    /// <summary>Score >= 70.</summary>
    Acceptable,
    /// <summary>Score >= 50.</summary>
    Poor,
    /// <summary>Score < 50.</summary>
    Critical
}

/// <summary>
/// Quality dimensions for scoring.
/// </summary>
public enum QualityDimension
{
    /// <summary>Data completeness - presence of required values.</summary>
    Completeness,
    /// <summary>Data accuracy - correctness of values.</summary>
    Accuracy,
    /// <summary>Data consistency - uniformity across the dataset.</summary>
    Consistency,
    /// <summary>Data validity - conformance to rules and formats.</summary>
    Validity,
    /// <summary>Data uniqueness - absence of duplicates.</summary>
    Uniqueness,
    /// <summary>Data timeliness - currency and freshness.</summary>
    Timeliness,
    /// <summary>Data integrity - referential integrity.</summary>
    Integrity
}

/// <summary>
/// Dataset quality score.
/// </summary>
public sealed class DatasetQualityScore
{
    /// <summary>
    /// Dataset identifier.
    /// </summary>
    public required string DatasetId { get; init; }

    /// <summary>
    /// Overall dataset score (0-100).
    /// </summary>
    public double OverallScore { get; init; }

    /// <summary>
    /// Grade.
    /// </summary>
    public QualityGrade Grade => OverallScore switch
    {
        >= 95 => QualityGrade.Excellent,
        >= 85 => QualityGrade.Good,
        >= 70 => QualityGrade.Acceptable,
        >= 50 => QualityGrade.Poor,
        _ => QualityGrade.Critical
    };

    /// <summary>
    /// Dimension scores.
    /// </summary>
    public Dictionary<QualityDimension, double> DimensionScores { get; init; } = new();

    /// <summary>
    /// Total records analyzed.
    /// </summary>
    public int TotalRecords { get; init; }

    /// <summary>
    /// Records by grade.
    /// </summary>
    public Dictionary<QualityGrade, int> RecordsByGrade { get; init; } = new();

    /// <summary>
    /// Field-level summary scores.
    /// </summary>
    public Dictionary<string, double> FieldScores { get; init; } = new();

    /// <summary>
    /// Top issues.
    /// </summary>
    public List<QualityIssue> TopIssues { get; init; } = new();

    /// <summary>
    /// Processing timestamp.
    /// </summary>
    public DateTime ScoredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Summary of a quality issue.
/// </summary>
public sealed class QualityIssue
{
    /// <summary>
    /// Issue description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Affected field.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Affected dimension.
    /// </summary>
    public QualityDimension Dimension { get; init; }

    /// <summary>
    /// Count of affected records.
    /// </summary>
    public int AffectedCount { get; init; }

    /// <summary>
    /// Impact on score (0-100).
    /// </summary>
    public double ScoreImpact { get; init; }
}

/// <summary>
/// Configuration for quality scoring.
/// </summary>
public sealed class QualityScoringConfig
{
    /// <summary>
    /// Dimension weights (must sum to 1.0).
    /// </summary>
    public Dictionary<QualityDimension, double> DimensionWeights { get; init; } = new()
    {
        [QualityDimension.Completeness] = 0.20,
        [QualityDimension.Accuracy] = 0.20,
        [QualityDimension.Consistency] = 0.15,
        [QualityDimension.Validity] = 0.15,
        [QualityDimension.Uniqueness] = 0.10,
        [QualityDimension.Timeliness] = 0.10,
        [QualityDimension.Integrity] = 0.10
    };

    /// <summary>
    /// Required fields for completeness scoring.
    /// </summary>
    public string[] RequiredFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Fields that should be unique.
    /// </summary>
    public string[] UniqueFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Timeliness threshold in days.
    /// </summary>
    public int TimelinessThresholdDays { get; init; } = 30;
}

#endregion

#region Dimension-Based Scoring Strategy

/// <summary>
/// Multi-dimensional quality scoring strategy.
/// </summary>
public sealed class DimensionScoringStrategy : DataQualityStrategyBase
{
    private QualityScoringConfig _config = new();

    /// <inheritdoc/>
    public override string StrategyId => "dimension-scoring";

    /// <inheritdoc/>
    public override string DisplayName => "Dimension-Based Quality Scoring";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Scoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = false,
        MaxThroughput = 20000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Calculates quality scores across multiple dimensions including completeness, " +
        "accuracy, consistency, validity, uniqueness, timeliness, and integrity.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "scoring", "quality", "dimensions", "metrics", "assessment"
    };

    /// <summary>
    /// Sets the scoring configuration.
    /// </summary>
    public void Configure(QualityScoringConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Scores a single record.
    /// </summary>
    public Task<RecordQualityScore> ScoreRecordAsync(
        DataRecord record,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var dimensionScores = new Dictionary<QualityDimension, double>();
        var fieldScores = new Dictionary<string, FieldQualityScore>();
        var issues = new List<string>();
        var recommendations = new List<string>();

        // Completeness
        var completeness = CalculateCompleteness(record);
        dimensionScores[QualityDimension.Completeness] = completeness;
        if (completeness < 100)
        {
            issues.Add($"Completeness: {100 - completeness:F1}% of fields are missing or empty");
            recommendations.Add("Fill in missing required fields");
        }

        // Validity (format checking)
        var validity = CalculateValidity(record, fieldScores);
        dimensionScores[QualityDimension.Validity] = validity;
        if (validity < 100)
        {
            issues.Add($"Validity: {100 - validity:F1}% of fields have format issues");
        }

        // Timeliness
        var timeliness = CalculateTimeliness(record);
        dimensionScores[QualityDimension.Timeliness] = timeliness;
        if (timeliness < 100)
        {
            issues.Add("Data may be stale");
            recommendations.Add("Update record with current information");
        }

        // Calculate weighted overall score
        double overallScore = 0;
        foreach (var (dimension, score) in dimensionScores)
        {
            var weight = _config.DimensionWeights.GetValueOrDefault(dimension, 0.1);
            overallScore += score * weight;
        }

        // Normalize if weights don't sum to 1
        var totalWeight = _config.DimensionWeights.Values.Sum();
        if (totalWeight > 0 && Math.Abs(totalWeight - 1.0) > 0.001)
        {
            overallScore /= totalWeight;
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);
        UpdateQualityScore(overallScore);

        return Task.FromResult(new RecordQualityScore
        {
            RecordId = record.RecordId,
            OverallScore = overallScore,
            DimensionScores = dimensionScores,
            FieldScores = fieldScores,
            Issues = issues,
            Recommendations = recommendations
        });
    }

    /// <summary>
    /// Scores an entire dataset.
    /// </summary>
    public async Task<DatasetQualityScore> ScoreDatasetAsync(
        IEnumerable<DataRecord> records,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var recordList = records.ToList();
        var recordScores = new List<RecordQualityScore>();
        var fieldScoreSums = new BoundedDictionary<string, double>(1000);
        var fieldCounts = new BoundedDictionary<string, int>(1000);
        var issueCounts = new BoundedDictionary<string, int>(1000);

        foreach (var record in recordList)
        {
            ct.ThrowIfCancellationRequested();
            var score = await ScoreRecordAsync(record, ct);
            recordScores.Add(score);

            // Aggregate field scores
            foreach (var (field, fieldScore) in score.FieldScores)
            {
                fieldScoreSums.AddOrUpdate(field, fieldScore.Score, (_, s) => s + fieldScore.Score);
                fieldCounts.AddOrUpdate(field, 1, (_, c) => c + 1);
            }

            // Count issues
            foreach (var issue in score.Issues)
            {
                issueCounts.AddOrUpdate(issue, 1, (_, c) => c + 1);
            }
        }

        // Calculate dataset dimension scores
        var dimensionScores = new Dictionary<QualityDimension, double>();
        foreach (QualityDimension dim in Enum.GetValues(typeof(QualityDimension)))
        {
            var dimScores = recordScores
                .Where(r => r.DimensionScores.ContainsKey(dim))
                .Select(r => r.DimensionScores[dim])
                .ToList();
            dimensionScores[dim] = dimScores.Count > 0 ? dimScores.Average() : 100;
        }

        // Calculate overall
        double overallScore = 0;
        foreach (var (dimension, score) in dimensionScores)
        {
            var weight = _config.DimensionWeights.GetValueOrDefault(dimension, 0.1);
            overallScore += score * weight;
        }

        var totalWeight = _config.DimensionWeights.Values.Sum();
        if (totalWeight > 0 && Math.Abs(totalWeight - 1.0) > 0.001)
        {
            overallScore /= totalWeight;
        }

        // Records by grade
        var recordsByGrade = recordScores
            .GroupBy(r => r.Grade)
            .ToDictionary(g => g.Key, g => g.Count());

        // Field scores
        var fieldScores = fieldScoreSums
            .ToDictionary(
                kvp => kvp.Key,
                kvp => fieldCounts.TryGetValue(kvp.Key, out var count) ? kvp.Value / count : 0);

        // Top issues
        var topIssues = issueCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(10)
            .Select(kvp => new QualityIssue
            {
                Description = kvp.Key,
                AffectedCount = kvp.Value,
                Dimension = QualityDimension.Validity,
                ScoreImpact = (double)kvp.Value / recordList.Count * 10
            })
            .ToList();

        // Add uniqueness check
        if (_config.UniqueFields.Length > 0)
        {
            var uniquenessScore = CalculateDatasetUniqueness(recordList);
            dimensionScores[QualityDimension.Uniqueness] = uniquenessScore;
        }

        return new DatasetQualityScore
        {
            DatasetId = recordList.FirstOrDefault()?.SourceId ?? "unknown",
            OverallScore = overallScore,
            DimensionScores = dimensionScores,
            TotalRecords = recordList.Count,
            RecordsByGrade = recordsByGrade,
            FieldScores = fieldScores,
            TopIssues = topIssues
        };
    }

    private double CalculateCompleteness(DataRecord record)
    {
        if (_config.RequiredFields.Length == 0)
        {
            // All fields
            var total = record.Fields.Count;
            var nonNull = record.Fields.Count(f => f.Value != null &&
                !(f.Value is string s && string.IsNullOrWhiteSpace(s)));
            return total > 0 ? (double)nonNull / total * 100 : 100;
        }

        var required = _config.RequiredFields.Length;
        var present = _config.RequiredFields.Count(f =>
            record.Fields.TryGetValue(f, out var v) && v != null &&
            !(v is string s && string.IsNullOrWhiteSpace(s)));
        return required > 0 ? (double)present / required * 100 : 100;
    }

    private double CalculateValidity(DataRecord record, Dictionary<string, FieldQualityScore> fieldScores)
    {
        double totalScore = 0;
        int fieldCount = 0;

        foreach (var (fieldName, value) in record.Fields)
        {
            if (value == null) continue;

            var issues = new List<string>();
            double fieldScore = 100;

            var strValue = value.ToString() ?? "";

            // Check for common validity issues
            if (strValue.Contains("N/A") || strValue.Contains("null") ||
                strValue.Contains("#VALUE!") || strValue.Contains("#REF!"))
            {
                fieldScore -= 30;
                issues.Add("Contains placeholder or error value");
            }

            // Check for suspicious patterns
            if (strValue.All(c => c == strValue[0]) && strValue.Length > 3)
            {
                fieldScore -= 20;
                issues.Add("Suspicious repeated character pattern");
            }

            fieldScores[fieldName] = new FieldQualityScore
            {
                FieldName = fieldName,
                Score = Math.Max(0, fieldScore),
                Issues = issues
            };

            totalScore += fieldScore;
            fieldCount++;
        }

        return fieldCount > 0 ? totalScore / fieldCount : 100;
    }

    private double CalculateTimeliness(DataRecord record)
    {
        var modifiedAt = record.ModifiedAt ?? record.CreatedAt;
        if (!modifiedAt.HasValue) return 100;

        var age = DateTime.UtcNow - modifiedAt.Value;
        var thresholdDays = _config.TimelinessThresholdDays;

        if (age.TotalDays <= thresholdDays)
            return 100;

        // Gradual decay
        var overDays = age.TotalDays - thresholdDays;
        return Math.Max(0, 100 - overDays * 2);
    }

    private double CalculateDatasetUniqueness(List<DataRecord> records)
    {
        if (_config.UniqueFields.Length == 0 || records.Count == 0)
            return 100;

        var seen = new HashSet<string>();
        var duplicates = 0;

        foreach (var record in records)
        {
            var key = string.Join("|", _config.UniqueFields
                .Select(f => record.GetFieldAsString(f) ?? ""));

            if (!seen.Add(key))
                duplicates++;
        }

        return (double)(records.Count - duplicates) / records.Count * 100;
    }
}

#endregion

#region Weighted Scoring Strategy

/// <summary>
/// Weighted field-based scoring strategy.
/// </summary>
public sealed class WeightedScoringStrategy : DataQualityStrategyBase
{
    private readonly Dictionary<string, double> _fieldWeights = new();
    private readonly Dictionary<string, Func<object?, double>> _fieldScorers = new();

    /// <inheritdoc/>
    public override string StrategyId => "weighted-scoring";

    /// <inheritdoc/>
    public override string DisplayName => "Weighted Field Scoring";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Scoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 50000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Calculates quality scores using weighted field importance. " +
        "Allows custom scoring functions per field.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "scoring", "weighted", "custom", "field-level"
    };

    /// <summary>
    /// Sets the weight for a field.
    /// </summary>
    public void SetFieldWeight(string fieldName, double weight)
    {
        _fieldWeights[fieldName] = Math.Max(0, Math.Min(1, weight));
    }

    /// <summary>
    /// Registers a custom scorer for a field.
    /// </summary>
    public void RegisterFieldScorer(string fieldName, Func<object?, double> scorer)
    {
        _fieldScorers[fieldName] = scorer;
    }

    /// <summary>
    /// Scores a record using weighted fields.
    /// </summary>
    public Task<RecordQualityScore> ScoreAsync(DataRecord record, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var fieldScores = new Dictionary<string, FieldQualityScore>();
        double weightedSum = 0;
        double totalWeight = 0;

        foreach (var (fieldName, value) in record.Fields)
        {
            ct.ThrowIfCancellationRequested();

            var weight = _fieldWeights.GetValueOrDefault(fieldName, 1.0);
            double score;

            if (_fieldScorers.TryGetValue(fieldName, out var scorer))
            {
                score = scorer(value);
            }
            else
            {
                score = DefaultScore(value);
            }

            score = Math.Max(0, Math.Min(100, score));

            fieldScores[fieldName] = new FieldQualityScore
            {
                FieldName = fieldName,
                Score = score
            };

            weightedSum += score * weight;
            totalWeight += weight;
        }

        var overallScore = totalWeight > 0 ? weightedSum / totalWeight : 0;

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);
        UpdateQualityScore(overallScore);

        return Task.FromResult(new RecordQualityScore
        {
            RecordId = record.RecordId,
            OverallScore = overallScore,
            FieldScores = fieldScores
        });
    }

    private static double DefaultScore(object? value)
    {
        if (value == null) return 0;
        if (value is string s && string.IsNullOrWhiteSpace(s)) return 0;
        return 100;
    }
}

#endregion
