using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Models;

/// <summary>
/// Anomaly detector using multiple detection algorithms.
/// </summary>
public sealed class AnomalyDetector
{
    private readonly ConcurrentDictionary<string, MetricSeries> _metricSeries;
    private readonly IsolationForest _isolationForest;
    private readonly int _windowSize;
    private readonly double _threshold;

    public AnomalyDetector(int windowSize = 100, double threshold = 2.5)
    {
        _metricSeries = new ConcurrentDictionary<string, MetricSeries>();
        _isolationForest = new IsolationForest(100, 256);
        _windowSize = windowSize;
        _threshold = threshold;
    }

    /// <summary>
    /// Records a metric value and returns any detected anomaly.
    /// </summary>
    public AnomalyResult? RecordAndDetect(string metricName, double value, Dictionary<string, object>? tags = null)
    {
        var series = _metricSeries.GetOrAdd(metricName, _ => new MetricSeries
        {
            MetricName = metricName,
            WindowSize = _windowSize
        });

        lock (series)
        {
            series.AddValue(value);

            // Multi-method detection
            var zScoreAnomaly = DetectZScoreAnomaly(series, value);
            var iqrAnomaly = DetectIQRAnomaly(series, value);
            var trendAnomaly = DetectTrendAnomaly(series, value);

            // Combine detection methods
            var anomalyScore = (zScoreAnomaly.Score + iqrAnomaly.Score + trendAnomaly.Score) / 3.0;
            var isAnomaly = zScoreAnomaly.IsAnomaly || iqrAnomaly.IsAnomaly || trendAnomaly.IsAnomaly;

            if (isAnomaly)
            {
                var result = new AnomalyResult
                {
                    MetricName = metricName,
                    Value = value,
                    Timestamp = DateTime.UtcNow,
                    Severity = CalculateSeverity(anomalyScore),
                    Score = anomalyScore,
                    Type = DetermineAnomalyType(zScoreAnomaly, iqrAnomaly, trendAnomaly),
                    Tags = tags ?? new Dictionary<string, object>(),
                    Details = new AnomalyDetails
                    {
                        ZScore = zScoreAnomaly.Score,
                        IQRScore = iqrAnomaly.Score,
                        TrendDeviation = trendAnomaly.Score,
                        ExpectedValue = series.Mean,
                        ExpectedRange = (series.Mean - 2 * series.StdDev, series.Mean + 2 * series.StdDev)
                    }
                };

                series.AnomalyHistory.Add(result);
                return result;
            }

            return null;
        }
    }

    /// <summary>
    /// Detects anomalies in a batch of metric values.
    /// </summary>
    public List<AnomalyResult> DetectBatch(string metricName, double[] values)
    {
        var anomalies = new List<AnomalyResult>();

        foreach (var value in values)
        {
            var anomaly = RecordAndDetect(metricName, value);
            if (anomaly != null)
            {
                anomalies.Add(anomaly);
            }
        }

        return anomalies;
    }

    /// <summary>
    /// Detects multivariate anomalies across multiple metrics.
    /// </summary>
    public MultivariateAnomalyResult? DetectMultivariate(Dictionary<string, double> metrics)
    {
        // Build feature vector
        var features = metrics.Values.ToArray();

        // Use isolation forest for multivariate detection
        var isolationScore = _isolationForest.Score(features);

        // Calculate per-metric z-scores
        var perMetricScores = new Dictionary<string, double>();
        foreach (var (name, value) in metrics)
        {
            if (_metricSeries.TryGetValue(name, out var series))
            {
                lock (series)
                {
                    perMetricScores[name] = series.StdDev > 0.001
                        ? Math.Abs((value - series.Mean) / series.StdDev)
                        : 0;
                    series.AddValue(value);
                }
            }
            else
            {
                perMetricScores[name] = 0;
                RecordAndDetect(name, value);
            }
        }

        var avgScore = perMetricScores.Values.Average();
        var maxScore = perMetricScores.Values.Max();
        var combinedScore = (isolationScore + avgScore + maxScore) / 3.0;

        if (combinedScore > _threshold)
        {
            return new MultivariateAnomalyResult
            {
                Timestamp = DateTime.UtcNow,
                Metrics = metrics,
                Score = combinedScore,
                IsolationScore = isolationScore,
                PerMetricScores = perMetricScores,
                Severity = CalculateSeverity(combinedScore),
                ContributingMetrics = perMetricScores
                    .Where(kv => kv.Value > _threshold)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList()
            };
        }

        return null;
    }

    /// <summary>
    /// Gets recent anomalies for a metric.
    /// </summary>
    public IEnumerable<AnomalyResult> GetRecentAnomalies(string metricName, TimeSpan window)
    {
        if (!_metricSeries.TryGetValue(metricName, out var series))
            return Enumerable.Empty<AnomalyResult>();

        var cutoff = DateTime.UtcNow - window;
        lock (series)
        {
            return series.AnomalyHistory
                .Where(a => a.Timestamp >= cutoff)
                .OrderByDescending(a => a.Timestamp)
                .ToList();
        }
    }

    /// <summary>
    /// Gets anomaly statistics for a metric.
    /// </summary>
    public AnomalyStatistics GetStatistics(string metricName)
    {
        if (!_metricSeries.TryGetValue(metricName, out var series))
        {
            return new AnomalyStatistics { MetricName = metricName };
        }

        lock (series)
        {
            var now = DateTime.UtcNow;
            var hour = now - TimeSpan.FromHours(1);
            var day = now - TimeSpan.FromDays(1);
            var week = now - TimeSpan.FromDays(7);

            return new AnomalyStatistics
            {
                MetricName = metricName,
                TotalDataPoints = series.Values.Count,
                TotalAnomalies = series.AnomalyHistory.Count,
                AnomaliesLastHour = series.AnomalyHistory.Count(a => a.Timestamp >= hour),
                AnomaliesLastDay = series.AnomalyHistory.Count(a => a.Timestamp >= day),
                AnomaliesLastWeek = series.AnomalyHistory.Count(a => a.Timestamp >= week),
                AnomalyRate = series.Values.Count > 0
                    ? (double)series.AnomalyHistory.Count / series.Values.Count
                    : 0,
                MostCommonType = series.AnomalyHistory
                    .GroupBy(a => a.Type)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key ?? AnomalyType.Unknown,
                Mean = series.Mean,
                StdDev = series.StdDev
            };
        }
    }

    /// <summary>
    /// Trains the isolation forest on historical data.
    /// </summary>
    public void TrainIsolationForest(double[][] trainingData)
    {
        _isolationForest.Train(trainingData);
    }

    private (bool IsAnomaly, double Score) DetectZScoreAnomaly(MetricSeries series, double value)
    {
        if (series.Values.Count < 30 || series.StdDev < 0.001)
            return (false, 0);

        var zScore = Math.Abs((value - series.Mean) / series.StdDev);
        return (zScore > _threshold, zScore);
    }

    private (bool IsAnomaly, double Score) DetectIQRAnomaly(MetricSeries series, double value)
    {
        if (series.Values.Count < 30)
            return (false, 0);

        var sorted = series.Values.OrderBy(v => v).ToList();
        var q1 = sorted[(int)(sorted.Count * 0.25)];
        var q3 = sorted[(int)(sorted.Count * 0.75)];
        var iqr = q3 - q1;

        var lowerBound = q1 - 1.5 * iqr;
        var upperBound = q3 + 1.5 * iqr;

        if (value < lowerBound || value > upperBound)
        {
            var deviation = value < lowerBound
                ? (lowerBound - value) / iqr
                : (value - upperBound) / iqr;
            return (true, deviation);
        }

        return (false, 0);
    }

    private (bool IsAnomaly, double Score) DetectTrendAnomaly(MetricSeries series, double value)
    {
        if (series.Values.Count < 50)
            return (false, 0);

        // Use recent trend to predict expected value
        var recentValues = series.Values.TakeLast(20).ToList();
        var trend = CalculateLinearTrend(recentValues);
        var expected = recentValues.Last() + trend;

        var deviation = Math.Abs(value - expected);
        var normalizedDeviation = series.StdDev > 0.001 ? deviation / series.StdDev : 0;

        return (normalizedDeviation > _threshold * 1.5, normalizedDeviation);
    }

    private double CalculateLinearTrend(List<double> values)
    {
        if (values.Count < 2) return 0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        return (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
    }

    private AnomalySeverity CalculateSeverity(double score)
    {
        return score switch
        {
            > 5.0 => AnomalySeverity.Critical,
            > 4.0 => AnomalySeverity.High,
            > 3.0 => AnomalySeverity.Medium,
            > 2.0 => AnomalySeverity.Low,
            _ => AnomalySeverity.Info
        };
    }

    private AnomalyType DetermineAnomalyType(
        (bool IsAnomaly, double Score) zScore,
        (bool IsAnomaly, double Score) iqr,
        (bool IsAnomaly, double Score) trend)
    {
        if (trend.IsAnomaly && trend.Score > zScore.Score && trend.Score > iqr.Score)
            return AnomalyType.TrendDeviation;
        if (zScore.Score > 4)
            return AnomalyType.Spike;
        if (iqr.IsAnomaly)
            return AnomalyType.Outlier;
        return AnomalyType.Statistical;
    }
}

/// <summary>
/// Isolation Forest algorithm for multivariate anomaly detection.
/// </summary>
internal sealed class IsolationForest
{
    private readonly int _numTrees;
    private readonly int _sampleSize;
    private readonly List<IsolationTree> _trees;
    private readonly Random _random;
    private bool _trained;

    public IsolationForest(int numTrees = 100, int sampleSize = 256)
    {
        _numTrees = numTrees;
        _sampleSize = sampleSize;
        _trees = new List<IsolationTree>();
        _random = new Random(42);
    }

    public void Train(double[][] data)
    {
        _trees.Clear();

        for (int i = 0; i < _numTrees; i++)
        {
            // Sample subset
            var sample = data.OrderBy(_ => _random.Next()).Take(_sampleSize).ToArray();
            var tree = new IsolationTree();
            tree.Build(sample, 0, (int)Math.Ceiling(Math.Log2(_sampleSize)));
            _trees.Add(tree);
        }

        _trained = true;
    }

    public double Score(double[] point)
    {
        if (!_trained || _trees.Count == 0)
            return 0;

        var avgPathLength = _trees.Average(t => t.PathLength(point));
        var c = AveragePathLength(_sampleSize);

        return Math.Pow(2, -avgPathLength / c);
    }

    private double AveragePathLength(int n)
    {
        if (n <= 1) return 1;
        return 2 * (Math.Log(n - 1) + 0.5772156649) - 2 * (n - 1.0) / n;
    }
}

internal sealed class IsolationTree
{
    private IsolationNode? _root;

    public void Build(double[][] data, int depth, int maxDepth)
    {
        _root = BuildNode(data, depth, maxDepth, new Random());
    }

    private IsolationNode BuildNode(double[][] data, int depth, int maxDepth, Random random)
    {
        if (depth >= maxDepth || data.Length <= 1)
        {
            return new IsolationNode { IsLeaf = true, Size = data.Length };
        }

        // Select random feature
        var numFeatures = data[0].Length;
        var featureIndex = random.Next(numFeatures);

        // Find min/max for feature
        var values = data.Select(d => d[featureIndex]).ToArray();
        var min = values.Min();
        var max = values.Max();

        if (Math.Abs(max - min) < 0.0001)
        {
            return new IsolationNode { IsLeaf = true, Size = data.Length };
        }

        // Select random split point
        var splitValue = min + random.NextDouble() * (max - min);

        var leftData = data.Where(d => d[featureIndex] < splitValue).ToArray();
        var rightData = data.Where(d => d[featureIndex] >= splitValue).ToArray();

        return new IsolationNode
        {
            IsLeaf = false,
            FeatureIndex = featureIndex,
            SplitValue = splitValue,
            Left = BuildNode(leftData, depth + 1, maxDepth, random),
            Right = BuildNode(rightData, depth + 1, maxDepth, random)
        };
    }

    public int PathLength(double[] point)
    {
        return PathLength(point, _root, 0);
    }

    private int PathLength(double[] point, IsolationNode? node, int depth)
    {
        if (node == null || node.IsLeaf)
        {
            return depth + (node?.Size > 1 ? (int)Math.Ceiling(Math.Log2(node.Size)) : 0);
        }

        return point[node.FeatureIndex] < node.SplitValue
            ? PathLength(point, node.Left, depth + 1)
            : PathLength(point, node.Right, depth + 1);
    }
}

internal sealed class IsolationNode
{
    public bool IsLeaf { get; init; }
    public int Size { get; init; }
    public int FeatureIndex { get; init; }
    public double SplitValue { get; init; }
    public IsolationNode? Left { get; init; }
    public IsolationNode? Right { get; init; }
}

/// <summary>
/// Time series data for a single metric.
/// </summary>
internal sealed class MetricSeries
{
    public string MetricName { get; init; } = string.Empty;
    public int WindowSize { get; init; } = 100;
    public List<double> Values { get; } = new();
    public List<AnomalyResult> AnomalyHistory { get; } = new();
    public double Mean { get; private set; }
    public double StdDev { get; private set; }
    private double _sumSquares;
    private double _sum;

    public void AddValue(double value)
    {
        Values.Add(value);

        // Update running statistics
        _sum += value;

        // Trim if needed
        while (Values.Count > WindowSize)
        {
            _sum -= Values[0];
            Values.RemoveAt(0);
        }

        // Recalculate stats
        if (Values.Count > 0)
        {
            Mean = _sum / Values.Count;
            _sumSquares = Values.Sum(v => Math.Pow(v - Mean, 2));
            StdDev = Math.Sqrt(_sumSquares / Values.Count);
        }

        // Trim anomaly history
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        AnomalyHistory.RemoveAll(a => a.Timestamp < cutoff);
    }
}

#region Supporting Types

public enum AnomalySeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public enum AnomalyType
{
    Unknown,
    Spike,
    Outlier,
    TrendDeviation,
    Statistical,
    Seasonal
}

public sealed class AnomalyResult
{
    public string MetricName { get; init; } = string.Empty;
    public double Value { get; init; }
    public DateTime Timestamp { get; init; }
    public AnomalySeverity Severity { get; init; }
    public double Score { get; init; }
    public AnomalyType Type { get; init; }
    public Dictionary<string, object> Tags { get; init; } = new();
    public AnomalyDetails Details { get; init; } = new();
}

public sealed class AnomalyDetails
{
    public double ZScore { get; init; }
    public double IQRScore { get; init; }
    public double TrendDeviation { get; init; }
    public double ExpectedValue { get; init; }
    public (double Lower, double Upper) ExpectedRange { get; init; }
}

public sealed class MultivariateAnomalyResult
{
    public DateTime Timestamp { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
    public double Score { get; init; }
    public double IsolationScore { get; init; }
    public Dictionary<string, double> PerMetricScores { get; init; } = new();
    public AnomalySeverity Severity { get; init; }
    public List<string> ContributingMetrics { get; init; } = new();
}

public sealed class AnomalyStatistics
{
    public string MetricName { get; init; } = string.Empty;
    public int TotalDataPoints { get; init; }
    public int TotalAnomalies { get; init; }
    public int AnomaliesLastHour { get; init; }
    public int AnomaliesLastDay { get; init; }
    public int AnomaliesLastWeek { get; init; }
    public double AnomalyRate { get; init; }
    public AnomalyType MostCommonType { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
}

#endregion
