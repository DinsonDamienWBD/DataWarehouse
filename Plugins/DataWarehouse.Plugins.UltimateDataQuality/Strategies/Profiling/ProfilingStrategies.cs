using System.Globalization;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Profiling;

#region Data Profile Types

/// <summary>
/// Complete data profile for a dataset.
/// </summary>
public sealed class DataProfile
{
    /// <summary>
    /// Profile identifier.
    /// </summary>
    public string ProfileId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Source identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// When the profile was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Total number of records analyzed.
    /// </summary>
    public long TotalRecords { get; set; }

    /// <summary>
    /// Processing time in milliseconds.
    /// </summary>
    public double ProcessingTimeMs { get; set; }

    /// <summary>
    /// Field-level profiles.
    /// </summary>
    public Dictionary<string, FieldProfile> FieldProfiles { get; init; } = new();

    /// <summary>
    /// Overall quality score (0-100).
    /// </summary>
    public double OverallQualityScore { get; set; }

    /// <summary>
    /// Cross-field correlations.
    /// </summary>
    public List<FieldCorrelation> Correlations { get; init; } = new();

    /// <summary>
    /// Detected data patterns.
    /// </summary>
    public List<PatternInfo> DetectedPatterns { get; init; } = new();

    /// <summary>
    /// Quality issues summary.
    /// </summary>
    public Dictionary<IssueCategory, int> IssueSummary { get; init; } = new();
}

/// <summary>
/// Profile for a single field/column.
/// </summary>
public sealed class FieldProfile
{
    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Inferred data type.
    /// </summary>
    public InferredDataType InferredType { get; set; }

    /// <summary>
    /// Total count of values.
    /// </summary>
    public long TotalCount { get; set; }

    /// <summary>
    /// Count of null/missing values.
    /// </summary>
    public long NullCount { get; set; }

    /// <summary>
    /// Count of distinct values.
    /// </summary>
    public long DistinctCount { get; set; }

    /// <summary>
    /// Percentage of null values.
    /// </summary>
    public double NullPercentage => TotalCount > 0 ? (double)NullCount / TotalCount * 100 : 0;

    /// <summary>
    /// Cardinality ratio (distinct/total).
    /// </summary>
    public double CardinalityRatio => TotalCount > 0 ? (double)DistinctCount / TotalCount : 0;

    /// <summary>
    /// Most frequent values with counts.
    /// </summary>
    public List<ValueFrequency> TopValues { get; init; } = new();

    /// <summary>
    /// Least frequent values with counts.
    /// </summary>
    public List<ValueFrequency> BottomValues { get; init; } = new();

    /// <summary>
    /// Numeric statistics (if applicable).
    /// </summary>
    public NumericProfile? NumericStats { get; set; }

    /// <summary>
    /// String statistics (if applicable).
    /// </summary>
    public StringProfile? StringStats { get; set; }

    /// <summary>
    /// Date/time statistics (if applicable).
    /// </summary>
    public DateTimeProfile? DateTimeStats { get; set; }

    /// <summary>
    /// Detected format pattern.
    /// </summary>
    public string? DetectedPattern { get; set; }

    /// <summary>
    /// Field-level quality score (0-100).
    /// </summary>
    public double QualityScore { get; set; }

    /// <summary>
    /// Issues found for this field.
    /// </summary>
    public List<string> Issues { get; init; } = new();
}

/// <summary>
/// Inferred data type for a field.
/// </summary>
public enum InferredDataType
{
    Unknown,
    String,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Time,
    Email,
    Phone,
    Url,
    Uuid,
    IpAddress,
    Json,
    Xml,
    Currency,
    Percentage,
    Binary,
    Mixed
}

/// <summary>
/// Value frequency information.
/// </summary>
public sealed class ValueFrequency
{
    public required object? Value { get; init; }
    public long Count { get; init; }
    public double Percentage { get; init; }
}

/// <summary>
/// Numeric field profile.
/// </summary>
public sealed class NumericProfile
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double Mode { get; init; }
    public double StdDev { get; init; }
    public double Variance { get; init; }
    public double Skewness { get; init; }
    public double Kurtosis { get; init; }
    public double Q1 { get; init; }
    public double Q3 { get; init; }
    public double IQR { get; init; }
    public double Sum { get; init; }
    public int ZeroCount { get; init; }
    public int NegativeCount { get; init; }
    public int PositiveCount { get; init; }
    public double[] Percentiles { get; init; } = Array.Empty<double>();
    public Dictionary<string, int> Histogram { get; init; } = new();
}

/// <summary>
/// String field profile.
/// </summary>
public sealed class StringProfile
{
    public int MinLength { get; init; }
    public int MaxLength { get; init; }
    public double AvgLength { get; init; }
    public int EmptyCount { get; init; }
    public int WhitespaceOnlyCount { get; init; }
    public Dictionary<string, int> LengthDistribution { get; init; } = new();
    public Dictionary<char, int> CharacterFrequency { get; init; } = new();
    public bool ContainsUppercase { get; init; }
    public bool ContainsLowercase { get; init; }
    public bool ContainsDigits { get; init; }
    public bool ContainsSpecialChars { get; init; }
    public List<string> CommonPrefixes { get; init; } = new();
    public List<string> CommonSuffixes { get; init; } = new();
}

/// <summary>
/// Date/time field profile.
/// </summary>
public sealed class DateTimeProfile
{
    public DateTime Min { get; init; }
    public DateTime Max { get; init; }
    public TimeSpan Range { get; init; }
    public DateTime? Mode { get; init; }
    public Dictionary<DayOfWeek, int> DayOfWeekDistribution { get; init; } = new();
    public Dictionary<int, int> MonthDistribution { get; init; } = new();
    public Dictionary<int, int> YearDistribution { get; init; } = new();
    public Dictionary<int, int> HourDistribution { get; init; } = new();
    public int FutureDateCount { get; init; }
    public int WeekendCount { get; init; }
    public string? DetectedFormat { get; init; }
}

/// <summary>
/// Field correlation information.
/// </summary>
public sealed class FieldCorrelation
{
    public required string Field1 { get; init; }
    public required string Field2 { get; init; }
    public double CorrelationCoefficient { get; init; }
    public CorrelationType Type { get; init; }
}

/// <summary>
/// Type of correlation.
/// </summary>
public enum CorrelationType
{
    Pearson,
    Spearman,
    Kendall,
    Cramers
}

/// <summary>
/// Pattern information detected in data.
/// </summary>
public sealed class PatternInfo
{
    public required string FieldName { get; init; }
    public required string Pattern { get; init; }
    public int MatchCount { get; init; }
    public double MatchPercentage { get; init; }
    public string? Description { get; init; }
}

#endregion

#region Column Profiling Strategy

/// <summary>
/// Column-level profiling strategy for analyzing individual fields.
/// Provides detailed statistics and pattern detection for each column.
/// </summary>
public sealed class ColumnProfilingStrategy : DataQualityStrategyBase
{
    private const int TopN = 10;
    private const int MaxDistinctValues = 10000;

    /// <inheritdoc/>
    public override string StrategyId => "column-profiling";

    /// <inheritdoc/>
    public override string DisplayName => "Column Profiling";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Profiling;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 50000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Performs column-level data profiling to understand data characteristics. " +
        "Analyzes data types, distributions, patterns, and quality metrics for each field.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "profiling", "column", "statistics", "distribution", "type-inference"
    };

    /// <summary>
    /// Profiles a dataset and returns a complete data profile.
    /// </summary>
    public async Task<DataProfile> ProfileAsync(
        IEnumerable<DataRecord> records,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var fieldData = new BoundedDictionary<string, List<object?>>(1000);
        var recordList = records.ToList();

        // Collect all values by field
        foreach (var record in recordList)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (fieldName, value) in record.Fields)
            {
                fieldData.AddOrUpdate(fieldName,
                    _ => new List<object?> { value },
                    (_, list) => { list.Add(value); return list; });
            }
        }

        var profile = new DataProfile
        {
            SourceId = recordList.FirstOrDefault()?.SourceId ?? "unknown",
            TotalRecords = recordList.Count
        };

        // Profile each field
        foreach (var (fieldName, values) in fieldData)
        {
            ct.ThrowIfCancellationRequested();
            var fieldProfile = await ProfileFieldAsync(fieldName, values, ct);
            profile.FieldProfiles[fieldName] = fieldProfile;
        }

        // Calculate overall quality score
        if (profile.FieldProfiles.Count > 0)
        {
            profile.OverallQualityScore = profile.FieldProfiles.Values
                .Average(f => f.QualityScore);
        }

        // Detect correlations for numeric fields
        var numericFields = profile.FieldProfiles
            .Where(f => f.Value.InferredType is InferredDataType.Integer or InferredDataType.Decimal)
            .Select(f => f.Key)
            .ToList();

        if (numericFields.Count >= 2)
        {
            profile.Correlations.AddRange(
                await CalculateCorrelationsAsync(numericFields, fieldData, ct));
        }

        profile.ProcessingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
        return profile;
    }

    private Task<FieldProfile> ProfileFieldAsync(
        string fieldName,
        List<object?> values,
        CancellationToken ct)
    {
        var profile = new FieldProfile
        {
            FieldName = fieldName,
            TotalCount = values.Count,
            NullCount = values.Count(v => v == null || (v is string s && string.IsNullOrWhiteSpace(s)))
        };

        var nonNullValues = values.Where(v => v != null && !(v is string s && string.IsNullOrWhiteSpace(s))).ToList();

        // Calculate distinct count
        var distinctValues = new HashSet<string>();
        foreach (var v in nonNullValues.Take(MaxDistinctValues))
        {
            distinctValues.Add(v?.ToString() ?? "");
        }
        profile.DistinctCount = distinctValues.Count;

        // Infer data type
        profile.InferredType = InferDataType(nonNullValues);

        // Calculate value frequencies
        var frequencies = nonNullValues
            .GroupBy(v => v?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.Count())
            .OrderByDescending(kvp => kvp.Value)
            .ToList();

        profile.TopValues.AddRange(frequencies.Take(TopN).Select(f => new ValueFrequency
        {
            Value = f.Key,
            Count = f.Value,
            Percentage = (double)f.Value / values.Count * 100
        }));

        profile.BottomValues.AddRange(frequencies.TakeLast(TopN).Select(f => new ValueFrequency
        {
            Value = f.Key,
            Count = f.Value,
            Percentage = (double)f.Value / values.Count * 100
        }));

        // Type-specific profiling
        switch (profile.InferredType)
        {
            case InferredDataType.Integer:
            case InferredDataType.Decimal:
            case InferredDataType.Currency:
            case InferredDataType.Percentage:
                profile.NumericStats = ProfileNumeric(nonNullValues);
                break;

            case InferredDataType.String:
            case InferredDataType.Email:
            case InferredDataType.Phone:
            case InferredDataType.Url:
            case InferredDataType.Uuid:
            case InferredDataType.IpAddress:
                profile.StringStats = ProfileString(nonNullValues);
                break;

            case InferredDataType.Date:
            case InferredDataType.DateTime:
                profile.DateTimeStats = ProfileDateTime(nonNullValues);
                break;
        }

        // Calculate quality score
        profile.QualityScore = CalculateFieldQualityScore(profile);

        // Detect issues
        DetectFieldIssues(profile);

        return Task.FromResult(profile);
    }

    private InferredDataType InferDataType(List<object?> values)
    {
        if (values.Count == 0) return InferredDataType.Unknown;

        var typeCounts = new Dictionary<InferredDataType, int>();
        var sampleSize = Math.Min(100, values.Count);

        foreach (var value in values.Take(sampleSize))
        {
            var type = InferValueType(value?.ToString());
            typeCounts.TryGetValue(type, out var count);
            typeCounts[type] = count + 1;
        }

        // If 80%+ of values are the same type, use that
        var dominant = typeCounts.OrderByDescending(t => t.Value).First();
        if ((double)dominant.Value / sampleSize >= 0.8)
        {
            return dominant.Key;
        }

        return InferredDataType.Mixed;
    }

    private static readonly Regex EmailPattern = new(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.Compiled);
    private static readonly Regex PhonePattern = new(@"^[\+]?[(]?[0-9]{1,3}[)]?[-\s\.]?[(]?[0-9]{1,4}[)]?[-\s\.]?[0-9]{1,4}[-\s\.]?[0-9]{1,9}$", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(@"^https?://", RegexOptions.Compiled);
    private static readonly Regex UuidPattern = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex IpPattern = new(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$", RegexOptions.Compiled);

    private InferredDataType InferValueType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return InferredDataType.Unknown;

        // Boolean
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return InferredDataType.Boolean;

        // Integer
        if (long.TryParse(value, out _))
            return InferredDataType.Integer;

        // Decimal
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return InferredDataType.Decimal;

        // Currency
        if (value.StartsWith("$") || value.StartsWith("EUR") || value.EndsWith("USD"))
            return InferredDataType.Currency;

        // Percentage
        if (value.EndsWith("%"))
            return InferredDataType.Percentage;

        // Date/DateTime
        if (DateTime.TryParse(value, out var dt))
        {
            return dt.TimeOfDay == TimeSpan.Zero ? InferredDataType.Date : InferredDataType.DateTime;
        }

        // Email
        if (EmailPattern.IsMatch(value))
            return InferredDataType.Email;

        // Phone
        if (PhonePattern.IsMatch(value))
            return InferredDataType.Phone;

        // URL
        if (UrlPattern.IsMatch(value))
            return InferredDataType.Url;

        // UUID
        if (UuidPattern.IsMatch(value))
            return InferredDataType.Uuid;

        // IP Address
        if (IpPattern.IsMatch(value))
            return InferredDataType.IpAddress;

        // JSON
        if ((value.StartsWith("{") && value.EndsWith("}")) ||
            (value.StartsWith("[") && value.EndsWith("]")))
            return InferredDataType.Json;

        // XML
        if (value.StartsWith("<") && value.EndsWith(">"))
            return InferredDataType.Xml;

        return InferredDataType.String;
    }

    private NumericProfile ProfileNumeric(List<object?> values)
    {
        var numericValues = values
            .Select(v => double.TryParse(v?.ToString(), out var d) ? (double?)d : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        if (numericValues.Count == 0)
            return new NumericProfile();

        var sorted = numericValues.OrderBy(v => v).ToList();
        var mean = numericValues.Average();
        var variance = numericValues.Sum(v => Math.Pow(v - mean, 2)) / numericValues.Count;
        var stdDev = Math.Sqrt(variance);

        var n = sorted.Count;
        var q1Index = n / 4;
        var q3Index = 3 * n / 4;

        return new NumericProfile
        {
            Min = sorted.First(),
            Max = sorted.Last(),
            Mean = mean,
            Median = sorted[n / 2],
            Mode = numericValues.GroupBy(v => Math.Round(v, 2)).OrderByDescending(g => g.Count()).First().Key,
            StdDev = stdDev,
            Variance = variance,
            Q1 = sorted[q1Index],
            Q3 = sorted[q3Index],
            IQR = sorted[q3Index] - sorted[q1Index],
            Sum = numericValues.Sum(),
            ZeroCount = numericValues.Count(v => v == 0),
            NegativeCount = numericValues.Count(v => v < 0),
            PositiveCount = numericValues.Count(v => v > 0),
            Percentiles = new[]
            {
                sorted[(int)(n * 0.1)],
                sorted[(int)(n * 0.25)],
                sorted[(int)(n * 0.5)],
                sorted[(int)(n * 0.75)],
                sorted[(int)(n * 0.9)]
            }
        };
    }

    private StringProfile ProfileString(List<object?> values)
    {
        var stringValues = values.Select(v => v?.ToString() ?? "").ToList();
        var lengths = stringValues.Select(s => s.Length).ToList();

        var charFreq = new Dictionary<char, int>();
        foreach (var s in stringValues.Take(1000))
        {
            foreach (var c in s)
            {
                charFreq.TryGetValue(c, out var count);
                charFreq[c] = count + 1;
            }
        }

        return new StringProfile
        {
            MinLength = lengths.Count > 0 ? lengths.Min() : 0,
            MaxLength = lengths.Count > 0 ? lengths.Max() : 0,
            AvgLength = lengths.Count > 0 ? lengths.Average() : 0,
            EmptyCount = stringValues.Count(s => string.IsNullOrEmpty(s)),
            WhitespaceOnlyCount = stringValues.Count(s => !string.IsNullOrEmpty(s) && string.IsNullOrWhiteSpace(s)),
            LengthDistribution = lengths.GroupBy(l => l).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            CharacterFrequency = charFreq,
            ContainsUppercase = stringValues.Any(s => s.Any(char.IsUpper)),
            ContainsLowercase = stringValues.Any(s => s.Any(char.IsLower)),
            ContainsDigits = stringValues.Any(s => s.Any(char.IsDigit)),
            ContainsSpecialChars = stringValues.Any(s => s.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)))
        };
    }

    private DateTimeProfile ProfileDateTime(List<object?> values)
    {
        var dateValues = values
            .Select(v => DateTime.TryParse(v?.ToString(), out var dt) ? (DateTime?)dt : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        if (dateValues.Count == 0)
            return new DateTimeProfile();

        var sorted = dateValues.OrderBy(d => d).ToList();

        return new DateTimeProfile
        {
            Min = sorted.First(),
            Max = sorted.Last(),
            Range = sorted.Last() - sorted.First(),
            DayOfWeekDistribution = dateValues.GroupBy(d => d.DayOfWeek).ToDictionary(g => g.Key, g => g.Count()),
            MonthDistribution = dateValues.GroupBy(d => d.Month).ToDictionary(g => g.Key, g => g.Count()),
            YearDistribution = dateValues.GroupBy(d => d.Year).ToDictionary(g => g.Key, g => g.Count()),
            HourDistribution = dateValues.GroupBy(d => d.Hour).ToDictionary(g => g.Key, g => g.Count()),
            FutureDateCount = dateValues.Count(d => d > DateTime.UtcNow),
            WeekendCount = dateValues.Count(d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        };
    }

    private double CalculateFieldQualityScore(FieldProfile profile)
    {
        var score = 100.0;

        // Penalize for nulls (up to -30 points)
        score -= Math.Min(30, profile.NullPercentage * 0.5);

        // Penalize for low cardinality in non-categorical fields
        if (profile.InferredType is InferredDataType.String &&
            profile.TotalCount > 100 && profile.DistinctCount < 5)
        {
            score -= 10;
        }

        // Penalize for very high cardinality (might indicate data issues)
        if (profile.CardinalityRatio > 0.99 && profile.TotalCount > 100)
        {
            score -= 5;
        }

        return Math.Max(0, score);
    }

    private void DetectFieldIssues(FieldProfile profile)
    {
        if (profile.NullPercentage > 50)
        {
            profile.Issues.Add($"High null rate: {profile.NullPercentage:F1}%");
        }

        if (profile.InferredType == InferredDataType.Mixed)
        {
            profile.Issues.Add("Inconsistent data types detected");
        }

        if (profile.NumericStats?.StdDev > profile.NumericStats?.Mean * 10)
        {
            profile.Issues.Add("High variance may indicate outliers");
        }

        if (profile.DateTimeStats?.FutureDateCount > 0)
        {
            profile.Issues.Add($"Contains {profile.DateTimeStats.FutureDateCount} future dates");
        }
    }

    private Task<List<FieldCorrelation>> CalculateCorrelationsAsync(
        List<string> fields,
        BoundedDictionary<string, List<object?>> fieldData,
        CancellationToken ct)
    {
        var correlations = new List<FieldCorrelation>();

        for (int i = 0; i < fields.Count - 1; i++)
        {
            for (int j = i + 1; j < fields.Count; j++)
            {
                ct.ThrowIfCancellationRequested();

                var field1 = fields[i];
                var field2 = fields[j];

                if (!fieldData.TryGetValue(field1, out var values1) ||
                    !fieldData.TryGetValue(field2, out var values2))
                    continue;

                var pairs = values1.Zip(values2)
                    .Where(p => double.TryParse(p.First?.ToString(), out _) &&
                               double.TryParse(p.Second?.ToString(), out _))
                    .Select(p => (
                        First: double.Parse(p.First!.ToString()!),
                        Second: double.Parse(p.Second!.ToString()!)))
                    .ToList();

                if (pairs.Count < 10) continue;

                var correlation = CalculatePearsonCorrelation(pairs);
                if (Math.Abs(correlation) > 0.3) // Only include meaningful correlations
                {
                    correlations.Add(new FieldCorrelation
                    {
                        Field1 = field1,
                        Field2 = field2,
                        CorrelationCoefficient = correlation,
                        Type = CorrelationType.Pearson
                    });
                }
            }
        }

        return Task.FromResult(correlations);
    }

    private double CalculatePearsonCorrelation(List<(double First, double Second)> pairs)
    {
        var n = pairs.Count;
        var sumX = pairs.Sum(p => p.First);
        var sumY = pairs.Sum(p => p.Second);
        var sumXY = pairs.Sum(p => p.First * p.Second);
        var sumX2 = pairs.Sum(p => p.First * p.First);
        var sumY2 = pairs.Sum(p => p.Second * p.Second);

        var numerator = n * sumXY - sumX * sumY;
        var denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));

        return denominator == 0 ? 0 : numerator / denominator;
    }
}

#endregion

#region Pattern Detection Strategy

/// <summary>
/// Pattern detection strategy for identifying data patterns and formats.
/// </summary>
public sealed class PatternDetectionStrategy : DataQualityStrategyBase
{
    private static readonly (string Name, Regex Pattern)[] KnownPatterns = new[]
    {
        ("SSN", new Regex(@"^\d{3}-\d{2}-\d{4}$", RegexOptions.Compiled)),
        ("Phone-US", new Regex(@"^\(?(\d{3})\)?[-.\s]?(\d{3})[-.\s]?(\d{4})$", RegexOptions.Compiled)),
        ("Phone-Intl", new Regex(@"^\+\d{1,3}[-.\s]?\d{1,14}$", RegexOptions.Compiled)),
        ("Email", new Regex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.Compiled)),
        ("ZipCode-US", new Regex(@"^\d{5}(-\d{4})?$", RegexOptions.Compiled)),
        ("PostalCode-CA", new Regex(@"^[A-Z]\d[A-Z]\s?\d[A-Z]\d$", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("CreditCard", new Regex(@"^\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}$", RegexOptions.Compiled)),
        ("Date-ISO", new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled)),
        ("Date-US", new Regex(@"^\d{2}/\d{2}/\d{4}$", RegexOptions.Compiled)),
        ("Date-EU", new Regex(@"^\d{2}\.\d{2}\.\d{4}$", RegexOptions.Compiled)),
        ("Time-24H", new Regex(@"^\d{2}:\d{2}(:\d{2})?$", RegexOptions.Compiled)),
        ("IPv4", new Regex(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$", RegexOptions.Compiled)),
        ("MAC", new Regex(@"^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$", RegexOptions.Compiled)),
        ("UUID", new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled)),
        ("URL", new Regex(@"^https?://[^\s]+$", RegexOptions.Compiled)),
        ("Hex", new Regex(@"^(0x)?[0-9a-fA-F]+$", RegexOptions.Compiled)),
        ("Currency-USD", new Regex(@"^\$[\d,]+(\.\d{2})?$", RegexOptions.Compiled)),
        ("Percentage", new Regex(@"^\d+(\.\d+)?%$", RegexOptions.Compiled))
    };

    /// <inheritdoc/>
    public override string StrategyId => "pattern-detection";

    /// <inheritdoc/>
    public override string DisplayName => "Pattern Detection";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Profiling;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 30000,
        TypicalLatencyMs = 1.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects data patterns and formats including SSN, phone numbers, emails, dates, and more. " +
        "Helps understand data structure and identify format inconsistencies.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "profiling", "pattern", "format", "detection", "regex"
    };

    /// <summary>
    /// Detects patterns in a field's values.
    /// </summary>
    public Task<List<PatternInfo>> DetectPatternsAsync(
        string fieldName,
        IEnumerable<object?> values,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var valueList = values.Where(v => v != null).Select(v => v!.ToString() ?? "").ToList();
        var patterns = new List<PatternInfo>();
        var totalCount = valueList.Count;

        if (totalCount == 0)
            return Task.FromResult(patterns);

        foreach (var (name, regex) in KnownPatterns)
        {
            ct.ThrowIfCancellationRequested();

            var matchCount = valueList.Count(v => regex.IsMatch(v));
            if (matchCount > 0)
            {
                var percentage = (double)matchCount / totalCount * 100;
                patterns.Add(new PatternInfo
                {
                    FieldName = fieldName,
                    Pattern = name,
                    MatchCount = matchCount,
                    MatchPercentage = percentage,
                    Description = $"Matches {name} pattern"
                });
            }
        }

        return Task.FromResult(patterns.OrderByDescending(p => p.MatchPercentage).ToList());
    }

    /// <summary>
    /// Generates a pattern template from sample values.
    /// </summary>
    public string GeneratePatternTemplate(IEnumerable<string> sampleValues)
    {
        var samples = sampleValues.Take(10).ToList();
        if (samples.Count == 0) return "";

        // Convert each character to a pattern character
        var patterns = samples.Select(s => new string(s.Select(c =>
        {
            if (char.IsDigit(c)) return '9';
            if (char.IsLetter(c)) return char.IsUpper(c) ? 'A' : 'a';
            return c;
        }).ToArray())).ToList();

        // Find the most common pattern
        return patterns.GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }
}

#endregion
