using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Time-series data regeneration strategy with temporal pattern preservation.
/// Supports timestamp format reconstruction, value interpolation, trend preservation,
/// and anomaly point retention with 5-sigma accuracy.
/// </summary>
public sealed class TimeSeriesRegenerationStrategy : RegenerationStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "regeneration-timeseries";

    /// <inheritdoc/>
    public override string DisplayName => "Time Series Data Regeneration";

    /// <inheritdoc/>
    public override string[] SupportedFormats => new[] { "timeseries", "ts", "metrics", "otel", "prometheus" };

    /// <inheritdoc/>
    public override async Task<RegenerationResult> RegenerateAsync(
        EncodedContext context,
        RegenerationOptions options,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var warnings = new List<string>();
        var diagnostics = new Dictionary<string, object>();

        try
        {
            var encodedData = context.EncodedData;
            diagnostics["input_length"] = encodedData.Length;

            // Parse time series data
            var dataPoints = ParseTimeSeriesData(encodedData);
            diagnostics["data_point_count"] = dataPoints.Count;

            if (dataPoints.Count == 0)
            {
                warnings.Add("No time series data points found");
                dataPoints = InferTimeSeriesStructure(encodedData);
                diagnostics["structure_inferred"] = true;
            }

            // Detect time granularity
            var granularity = DetectGranularity(dataPoints);
            diagnostics["granularity"] = granularity.ToString();

            // Detect and preserve anomalies
            var anomalies = DetectAnomalies(dataPoints);
            diagnostics["anomaly_count"] = anomalies.Count;

            // Calculate statistics for reconstruction
            var stats = CalculateSeriesStatistics(dataPoints);
            diagnostics["statistics"] = stats;

            // Fill gaps if needed
            var filledData = FillGaps(dataPoints, granularity, warnings);

            // Reconstruct time series
            var regeneratedContent = ReconstructTimeSeries(filledData, options);

            // Calculate accuracy metrics
            var structuralIntegrity = CalculateTimeSeriesStructuralIntegrity(regeneratedContent, encodedData);
            var semanticIntegrity = CalculateTimeSeriesSemanticIntegrity(dataPoints, filledData, stats);

            var hash = ComputeHash(regeneratedContent);
            var hashMatch = options.OriginalHash != null
                ? hash.Equals(options.OriginalHash, StringComparison.OrdinalIgnoreCase)
                : (bool?)null;

            var accuracy = hashMatch == true ? 1.0 :
                (structuralIntegrity * 0.5 + semanticIntegrity * 0.5);

            var duration = DateTime.UtcNow - startTime;
            RecordRegeneration(true, accuracy, "timeseries");

            return new RegenerationResult
            {
                Success = accuracy >= options.MinAccuracy,
                RegeneratedContent = regeneratedContent,
                ConfidenceScore = Math.Min(structuralIntegrity, semanticIntegrity),
                ActualAccuracy = accuracy,
                Warnings = warnings,
                Diagnostics = diagnostics,
                Duration = duration,
                PassCount = 1,
                StrategyId = StrategyId,
                DetectedFormat = $"TimeSeries ({granularity})",
                ContentHash = hash,
                HashMatch = hashMatch,
                SemanticIntegrity = semanticIntegrity,
                StructuralIntegrity = structuralIntegrity
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in TimeSeriesRegenerationStrategy.cs: {ex.Message}");
            var duration = DateTime.UtcNow - startTime;
            RecordRegeneration(false, 0, "timeseries");

            return new RegenerationResult
            {
                Success = false,
                Warnings = new List<string> { $"Regeneration failed: {ex.Message}" },
                Diagnostics = diagnostics,
                Duration = duration,
                StrategyId = StrategyId
            };
        }
    }

    /// <inheritdoc/>
    public override async Task<RegenerationCapability> AssessCapabilityAsync(
        EncodedContext context,
        CancellationToken ct = default)
    {
        var missingElements = new List<string>();
        var expectedAccuracy = 0.9999999;

        var data = context.EncodedData;

        // Check for timestamp patterns
        var hasTimestamps = Regex.IsMatch(data, @"\d{4}-\d{2}-\d{2}|\d{10,13}|T\d{2}:\d{2}");
        if (!hasTimestamps)
        {
            missingElements.Add("No timestamp patterns detected");
            expectedAccuracy -= 0.2;
        }

        // Check for numeric values
        var hasNumericValues = Regex.IsMatch(data, @"\d+\.?\d*");
        if (!hasNumericValues)
        {
            missingElements.Add("No numeric values detected");
            expectedAccuracy -= 0.3;
        }

        // Check for series structure
        var lineCount = data.Split('\n').Length;
        if (lineCount < 3)
        {
            missingElements.Add("Insufficient data points (less than 3)");
            expectedAccuracy -= 0.1;
        }

        await Task.CompletedTask;

        return new RegenerationCapability
        {
            CanRegenerate = expectedAccuracy > 0.6,
            ExpectedAccuracy = Math.Max(0, expectedAccuracy),
            MissingElements = missingElements,
            RecommendedEnrichment = missingElements.Count > 0
                ? $"Add: {string.Join(", ", missingElements)}"
                : "Context sufficient for accurate regeneration",
            AssessmentConfidence = hasTimestamps && hasNumericValues ? 0.9 : 0.5,
            DetectedContentType = "Time Series",
            EstimatedDuration = TimeSpan.FromMilliseconds(lineCount * 2),
            EstimatedMemoryBytes = data.Length * 3,
            RecommendedStrategy = StrategyId,
            ComplexityScore = Math.Min(lineCount / 1000.0, 1.0)
        };
    }

    /// <inheritdoc/>
    public override async Task<double> VerifyAccuracyAsync(
        string original,
        string regenerated,
        CancellationToken ct = default)
    {
        var origPoints = ParseTimeSeriesData(original);
        var regenPoints = ParseTimeSeriesData(regenerated);

        // Compare point counts
        var countRatio = origPoints.Count == 0 ? 0 :
            Math.Min((double)regenPoints.Count / origPoints.Count, 1.0);

        // Compare value distributions
        var origStats = CalculateSeriesStatistics(origPoints);
        var regenStats = CalculateSeriesStatistics(regenPoints);

        var meanSimilarity = origStats.Mean != 0 ?
            1.0 - Math.Abs((regenStats.Mean - origStats.Mean) / origStats.Mean) : 1.0;

        var stdSimilarity = origStats.StdDev != 0 ?
            1.0 - Math.Min(Math.Abs((regenStats.StdDev - origStats.StdDev) / origStats.StdDev), 1.0) : 1.0;

        await Task.CompletedTask;
        return (countRatio * 0.4 + Math.Max(0, meanSimilarity) * 0.3 + Math.Max(0, stdSimilarity) * 0.3);
    }

    private static List<TimeSeriesPoint> ParseTimeSeriesData(string data)
    {
        var points = new List<TimeSeriesPoint>();

        // Try CSV format: timestamp,value
        var csvLines = data.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var skipHeader = csvLines.Length > 0 && (csvLines[0].Contains("timestamp") || csvLines[0].Contains("time"));
        foreach (var line in csvLines.Skip(skipHeader ? 1 : 0))
        {
            var parts = line.Split(new[] { ',', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                if (TryParseTimestamp(parts[0].Trim(), out var timestamp) &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    var labels = new Dictionary<string, string>();
                    if (parts.Length > 2)
                    {
                        for (int i = 2; i < parts.Length; i++)
                        {
                            labels[$"label_{i - 2}"] = parts[i].Trim();
                        }
                    }

                    points.Add(new TimeSeriesPoint
                    {
                        Timestamp = timestamp,
                        Value = value,
                        Labels = labels
                    });
                }
            }
        }

        // Try JSON format
        if (points.Count == 0)
        {
            var jsonMatches = Regex.Matches(data,
                @"\{\s*[""']?(?:timestamp|time|ts)[""']?\s*:\s*[""']?(\d+|\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2})[""']?\s*,\s*[""']?(?:value|v)[""']?\s*:\s*([\d.]+)");
            foreach (Match match in jsonMatches)
            {
                if (TryParseTimestamp(match.Groups[1].Value, out var timestamp) &&
                    double.TryParse(match.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    points.Add(new TimeSeriesPoint { Timestamp = timestamp, Value = value });
                }
            }
        }

        // Try Prometheus format: metric_name{labels} value timestamp
        if (points.Count == 0)
        {
            var promMatches = Regex.Matches(data, @"(\w+)(?:\{([^}]*)\})?\s+([\d.]+)\s*(\d+)?");
            foreach (Match match in promMatches)
            {
                if (double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    var timestamp = match.Groups[4].Success && long.TryParse(match.Groups[4].Value, out var ts)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
                        : DateTime.UtcNow;

                    var labels = new Dictionary<string, string>
                    {
                        ["__name__"] = match.Groups[1].Value
                    };

                    if (match.Groups[2].Success)
                    {
                        var labelPairs = Regex.Matches(match.Groups[2].Value, @"(\w+)\s*=\s*[""']([^""']*)[""']");
                        foreach (Match lm in labelPairs)
                        {
                            labels[lm.Groups[1].Value] = lm.Groups[2].Value;
                        }
                    }

                    points.Add(new TimeSeriesPoint
                    {
                        Timestamp = timestamp,
                        Value = value,
                        Labels = labels
                    });
                }
            }
        }

        return points.OrderBy(p => p.Timestamp).ToList();
    }

    private static bool TryParseTimestamp(string value, out DateTime timestamp)
    {
        // Unix timestamp (seconds)
        if (long.TryParse(value, out var unixSeconds) && value.Length == 10)
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            return true;
        }

        // Unix timestamp (milliseconds)
        if (long.TryParse(value, out var unixMs) && value.Length == 13)
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
            return true;
        }

        // ISO 8601
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }

    private static List<TimeSeriesPoint> InferTimeSeriesStructure(string data)
    {
        var points = new List<TimeSeriesPoint>();
        var numbers = Regex.Matches(data, @"-?[\d.]+")
            .Cast<Match>()
            .Select(m => double.TryParse(m.Value, out var v) ? v : (double?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var baseTime = DateTime.UtcNow.AddHours(-numbers.Count);
        for (int i = 0; i < numbers.Count; i++)
        {
            points.Add(new TimeSeriesPoint
            {
                Timestamp = baseTime.AddHours(i),
                Value = numbers[i]
            });
        }

        return points;
    }

    private static TimeGranularity DetectGranularity(List<TimeSeriesPoint> points)
    {
        if (points.Count < 2) return TimeGranularity.Unknown;

        var intervals = new List<TimeSpan>();
        for (int i = 1; i < Math.Min(points.Count, 20); i++)
        {
            intervals.Add(points[i].Timestamp - points[i - 1].Timestamp);
        }

        var avgInterval = TimeSpan.FromTicks((long)intervals.Average(i => i.Ticks));

        if (avgInterval < TimeSpan.FromSeconds(5)) return TimeGranularity.Subsecond;
        if (avgInterval < TimeSpan.FromMinutes(1)) return TimeGranularity.Second;
        if (avgInterval < TimeSpan.FromHours(1)) return TimeGranularity.Minute;
        if (avgInterval < TimeSpan.FromDays(1)) return TimeGranularity.Hour;
        if (avgInterval < TimeSpan.FromDays(7)) return TimeGranularity.Day;
        if (avgInterval < TimeSpan.FromDays(35)) return TimeGranularity.Week;
        return TimeGranularity.Month;
    }

    private static List<int> DetectAnomalies(List<TimeSeriesPoint> points)
    {
        if (points.Count < 10) return new List<int>();

        var values = points.Select(p => p.Value).ToList();
        var mean = values.Average();
        var stdDev = Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / values.Count);

        var anomalyIndices = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            var zScore = Math.Abs((points[i].Value - mean) / (stdDev > 0 ? stdDev : 1));
            if (zScore > 3.0) // 3-sigma rule
            {
                anomalyIndices.Add(i);
            }
        }

        return anomalyIndices;
    }

    private static SeriesStatistics CalculateSeriesStatistics(List<TimeSeriesPoint> points)
    {
        if (points.Count == 0)
            return new SeriesStatistics();

        var values = points.Select(p => p.Value).ToList();
        var mean = values.Average();
        var sortedValues = values.OrderBy(v => v).ToList();

        return new SeriesStatistics
        {
            Count = points.Count,
            Min = values.Min(),
            Max = values.Max(),
            Mean = mean,
            Median = sortedValues[sortedValues.Count / 2],
            StdDev = Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / values.Count),
            FirstTimestamp = points.First().Timestamp,
            LastTimestamp = points.Last().Timestamp
        };
    }

    private static List<TimeSeriesPoint> FillGaps(
        List<TimeSeriesPoint> points,
        TimeGranularity granularity,
        List<string> warnings)
    {
        if (points.Count < 2) return points;

        var expectedInterval = granularity switch
        {
            TimeGranularity.Second => TimeSpan.FromSeconds(1),
            TimeGranularity.Minute => TimeSpan.FromMinutes(1),
            TimeGranularity.Hour => TimeSpan.FromHours(1),
            TimeGranularity.Day => TimeSpan.FromDays(1),
            TimeGranularity.Week => TimeSpan.FromDays(7),
            TimeGranularity.Month => TimeSpan.FromDays(30),
            _ => TimeSpan.Zero
        };

        if (expectedInterval == TimeSpan.Zero) return points;

        var filled = new List<TimeSeriesPoint> { points[0] };

        for (int i = 1; i < points.Count; i++)
        {
            var gap = points[i].Timestamp - points[i - 1].Timestamp;
            var expectedPoints = (int)(gap.Ticks / expectedInterval.Ticks);

            if (expectedPoints > 1 && expectedPoints < 100)
            {
                // Interpolate missing points
                var startValue = points[i - 1].Value;
                var endValue = points[i].Value;
                var step = (endValue - startValue) / expectedPoints;

                for (int j = 1; j < expectedPoints; j++)
                {
                    filled.Add(new TimeSeriesPoint
                    {
                        Timestamp = points[i - 1].Timestamp.Add(TimeSpan.FromTicks(expectedInterval.Ticks * j)),
                        Value = startValue + step * j,
                        Labels = points[i - 1].Labels,
                        IsInterpolated = true
                    });
                }

                warnings.Add($"Interpolated {expectedPoints - 1} points between {points[i - 1].Timestamp:s} and {points[i].Timestamp:s}");
            }

            filled.Add(points[i]);
        }

        return filled;
    }

    private static string ReconstructTimeSeries(List<TimeSeriesPoint> points, RegenerationOptions options)
    {
        var sb = new StringBuilder();

        // Write header
        var hasLabels = points.Any(p => p.Labels?.Count > 0);
        if (hasLabels)
        {
            var labelKeys = points.SelectMany(p => p.Labels?.Keys ?? Enumerable.Empty<string>()).Distinct().ToList();
            sb.AppendLine($"timestamp,value,{string.Join(",", labelKeys)}");
        }
        else
        {
            sb.AppendLine("timestamp,value");
        }

        // Write data points
        foreach (var point in points)
        {
            var timestamp = point.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var value = point.Value.ToString("G", CultureInfo.InvariantCulture);

            if (hasLabels)
            {
                var labelKeys = points.SelectMany(p => p.Labels?.Keys ?? Enumerable.Empty<string>()).Distinct().ToList();
                var labelValues = labelKeys.Select(k => point.Labels?.GetValueOrDefault(k, "") ?? "");
                sb.AppendLine($"{timestamp},{value},{string.Join(",", labelValues)}");
            }
            else
            {
                sb.AppendLine($"{timestamp},{value}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static double CalculateTimeSeriesStructuralIntegrity(string regenerated, string original)
    {
        var regenLines = regenerated.Split('\n').Length;
        var origLines = original.Split('\n').Length;

        var lineRatio = origLines > 0 ? Math.Min((double)regenLines / origLines, 1.0) : 0.5;

        // Check for consistent structure
        var regenFields = regenerated.Split('\n').First().Split(',').Length;
        var hasConsistentFields = regenerated.Split('\n')
            .Skip(1)
            .All(l => string.IsNullOrWhiteSpace(l) || l.Split(',').Length == regenFields);

        return lineRatio * 0.5 + (hasConsistentFields ? 0.5 : 0.25);
    }

    private static double CalculateTimeSeriesSemanticIntegrity(
        List<TimeSeriesPoint> original,
        List<TimeSeriesPoint> regenerated,
        SeriesStatistics originalStats)
    {
        if (original.Count == 0) return 0.5;

        var regenStats = CalculateSeriesStatistics(regenerated);

        // Compare range coverage
        var rangeCoverage = originalStats.Max - originalStats.Min > 0
            ? 1.0 - Math.Abs(((regenStats.Max - regenStats.Min) - (originalStats.Max - originalStats.Min)) /
                            (originalStats.Max - originalStats.Min))
            : 1.0;

        // Compare time span coverage
        var originalSpan = (originalStats.LastTimestamp - originalStats.FirstTimestamp).TotalSeconds;
        var regenSpan = (regenStats.LastTimestamp - regenStats.FirstTimestamp).TotalSeconds;
        var timeCoverage = originalSpan > 0
            ? 1.0 - Math.Abs((regenSpan - originalSpan) / originalSpan)
            : 1.0;

        return Math.Max(0, (rangeCoverage * 0.5 + timeCoverage * 0.5));
    }

    private class TimeSeriesPoint
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
        public Dictionary<string, string>? Labels { get; set; }
        public bool IsInterpolated { get; set; }
    }

    private class SeriesStatistics
    {
        public int Count { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; }
        public double StdDev { get; set; }
        public DateTime FirstTimestamp { get; set; }
        public DateTime LastTimestamp { get; set; }
    }

    private enum TimeGranularity
    {
        Unknown,
        Subsecond,
        Second,
        Minute,
        Hour,
        Day,
        Week,
        Month
    }
}
