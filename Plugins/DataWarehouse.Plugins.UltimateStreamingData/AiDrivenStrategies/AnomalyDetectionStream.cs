using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.AiDrivenStrategies;

#region Anomaly Detection Types

/// <summary>
/// Type of anomaly detected in stream data.
/// </summary>
public enum StreamAnomalyType
{
    /// <summary>Value exceeds expected range.</summary>
    ValueOutlier,

    /// <summary>Rate of events deviates significantly from baseline.</summary>
    RateAnomaly,

    /// <summary>An unusual pattern in event sequencing.</summary>
    PatternAnomaly,

    /// <summary>Sudden shift in the distribution of values.</summary>
    DistributionShift,

    /// <summary>Missing expected events within a time window.</summary>
    MissingEvents,

    /// <summary>Duplicate events beyond expected deduplication tolerance.</summary>
    DuplicateSpike
}

/// <summary>
/// Severity of a detected anomaly.
/// </summary>
public enum StreamAnomalySeverity
{
    /// <summary>Informational -- borderline anomalous.</summary>
    Low,

    /// <summary>Notable deviation from baseline.</summary>
    Medium,

    /// <summary>Significant anomaly requiring attention.</summary>
    High,

    /// <summary>Critical anomaly indicating potential system failure or attack.</summary>
    Critical
}

/// <summary>
/// Result of anomaly detection analysis on a stream event or window.
/// </summary>
public sealed record StreamAnomalyResult
{
    /// <summary>Gets whether an anomaly was detected.</summary>
    public bool IsAnomaly { get; init; }

    /// <summary>Gets the anomaly score (0.0 = normal, 1.0 = highly anomalous).</summary>
    public double AnomalyScore { get; init; }

    /// <summary>Gets the anomaly type if detected.</summary>
    public StreamAnomalyType? AnomalyType { get; init; }

    /// <summary>Gets the anomaly severity.</summary>
    public StreamAnomalySeverity Severity { get; init; }

    /// <summary>Gets the analysis method used ("ML" or "Statistical-ZScore").</summary>
    public string AnalysisMethod { get; init; } = "Statistical-ZScore";

    /// <summary>Gets the confidence level of the detection (0.0-1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the event that was analyzed.</summary>
    public required StreamEvent Event { get; init; }

    /// <summary>Gets the Z-score computed for statistical analysis.</summary>
    public double? ZScore { get; init; }

    /// <summary>Gets the baseline mean used in statistical analysis.</summary>
    public double? BaselineMean { get; init; }

    /// <summary>Gets the baseline standard deviation used in statistical analysis.</summary>
    public double? BaselineStdDev { get; init; }

    /// <summary>Gets when the analysis was performed.</summary>
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Configuration for stream anomaly detection.
/// </summary>
public sealed record AnomalyDetectionConfig
{
    /// <summary>Gets the Z-score threshold for the statistical fallback (default: 3.0 = 99.7% confidence).</summary>
    public double ZScoreThreshold { get; init; } = 3.0;

    /// <summary>Gets the minimum number of observations before anomaly detection activates.</summary>
    public int MinObservations { get; init; } = 30;

    /// <summary>Gets the sliding window size for computing running statistics.</summary>
    public int StatisticalWindowSize { get; init; } = 1000;

    /// <summary>Gets the Intelligence plugin request timeout.</summary>
    public TimeSpan IntelligenceTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the value extractor to produce a numeric metric from stream events.</summary>
    public Func<StreamEvent, double>? ValueExtractor { get; init; }
}

#endregion

/// <summary>
/// AI-driven anomaly detection for streaming data.
/// Routes to Intelligence plugin (topic: "intelligence.analyze") for ML-based anomaly detection.
/// Falls back to Z-score statistical method when Intelligence is unavailable.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for ML-based anomaly detection.
/// <b>MESSAGE TOPIC:</b> intelligence.analyze
/// <b>FALLBACK:</b> Z-score statistical anomaly detection (mean +/- N*sigma).
/// Thread-safe for concurrent event analysis.
/// </remarks>
internal sealed class AnomalyDetectionStream : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, RunningStatistics> _statistics = new BoundedDictionary<string, RunningStatistics>(1000);
    private readonly IMessageBus? _messageBus;
    private readonly AnomalyDetectionConfig _config;
    private long _totalAnalyzed;
    private long _totalAnomalies;
    private long _mlAnalyses;
    private long _statisticalFallbacks;

    /// <inheritdoc/>
    public override string StrategyId => "ai-anomaly-detection-stream";

    /// <inheritdoc/>
    public override string DisplayName => "AI Anomaly Detection Stream";

    /// <inheritdoc/>
    public override StreamingCategory Category => StreamingCategory.StreamAnalytics;

    /// <inheritdoc/>
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = false,
        SupportsWindowing = true,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = false,
        SupportsPartitioning = true,
        SupportsAutoScaling = false,
        SupportsDistributed = false,
        MaxThroughputEventsPerSec = 50_000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "AI-driven anomaly detection for streaming data. Routes to Intelligence plugin for ML-based detection " +
        "with Z-score statistical fallback when AI is unavailable. Maintains per-key running statistics " +
        "for baseline computation.";

    /// <inheritdoc/>
    public override string[] Tags => ["anomaly-detection", "ai", "ml", "z-score", "statistical", "intelligence"];

    /// <summary>
    /// Initializes a new instance of the <see cref="AnomalyDetectionStream"/> class.
    /// </summary>
    /// <param name="messageBus">Optional message bus for Intelligence plugin communication.</param>
    /// <param name="config">Anomaly detection configuration.</param>
    public AnomalyDetectionStream(IMessageBus? messageBus = null, AnomalyDetectionConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new AnomalyDetectionConfig();
    }

    /// <summary>
    /// Initializes a new instance for auto-discovery (parameterless constructor).
    /// </summary>
    public AnomalyDetectionStream() : this(null, null) { }

    /// <summary>Gets the count of ML-based analyses performed.</summary>
    public long MlAnalyses => Interlocked.Read(ref _mlAnalyses);

    /// <summary>Gets the count of statistical fallback analyses performed.</summary>
    public long StatisticalFallbacks => Interlocked.Read(ref _statisticalFallbacks);

    /// <summary>
    /// Analyzes a stream event for anomalies using ML (Intelligence plugin) or Z-score fallback.
    /// </summary>
    /// <param name="evt">The stream event to analyze.</param>
    /// <param name="partitionKey">Optional partition key for per-key statistics isolation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The anomaly detection result.</returns>
    public async Task<StreamAnomalyResult> AnalyzeAsync(
        StreamEvent evt,
        string? partitionKey = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        Interlocked.Increment(ref _totalAnalyzed);

        var key = partitionKey ?? evt.Key ?? "default";
        var value = ExtractValue(evt);

        // Update running statistics
        var stats = _statistics.GetOrAdd(key, _ => new RunningStatistics(_config.StatisticalWindowSize));
        stats.Add(value);

        // Try ML-based analysis via Intelligence plugin
        if (_messageBus != null)
        {
            try
            {
                var mlResult = await TryMlAnalysisAsync(evt, key, value, stats, ct);
                if (mlResult != null)
                {
                    Interlocked.Increment(ref _mlAnalyses);
                    if (mlResult.IsAnomaly) Interlocked.Increment(ref _totalAnomalies);
                    RecordOperation("ml-anomaly-analysis");
                    return mlResult;
                }
            }
            catch (Exception ex)
            {

                // Fall through to statistical fallback
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Statistical Z-score fallback
        Interlocked.Increment(ref _statisticalFallbacks);
        var result = StatisticalAnalysis(evt, value, stats);
        if (result.IsAnomaly) Interlocked.Increment(ref _totalAnomalies);
        RecordOperation("statistical-anomaly-analysis");
        return result;
    }

    private async Task<StreamAnomalyResult?> TryMlAnalysisAsync(
        StreamEvent evt,
        string key,
        double value,
        RunningStatistics stats,
        CancellationToken ct)
    {
        if (_messageBus == null) return null;

        var request = new PluginMessage
        {
            Type = "analysis.request",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "AnomalyDetectionStream",
            Payload = new Dictionary<string, object>
            {
                ["DataType"] = "streaming-event",
                ["AnalysisType"] = "anomaly-detection",
                ["PartitionKey"] = key,
                ["Value"] = value,
                ["EventId"] = evt.EventId,
                ["Timestamp"] = evt.Timestamp.ToString("O"),
                ["BaselineMean"] = stats.Mean,
                ["BaselineStdDev"] = stats.StdDev,
                ["ObservationCount"] = stats.Count,
                ["Context"] = new Dictionary<string, object>
                {
                    ["WindowSize"] = _config.StatisticalWindowSize,
                    ["Min"] = stats.Min,
                    ["Max"] = stats.Max
                }
            }
        };

        var response = await _messageBus.SendAsync(
            "intelligence.analyze",
            request,
            _config.IntelligenceTimeout,
            ct);

        if (!response.Success) return null;

        var payload = response.Payload as Dictionary<string, object>;
        if (payload == null) return null;

        var isAnomaly = payload.TryGetValue("IsAnomaly", out var anomalyObj) && anomalyObj is bool a && a;
        var score = payload.TryGetValue("AnomalyScore", out var scoreObj) && scoreObj is double s ? s : 0.0;
        var confidence = payload.TryGetValue("Confidence", out var confObj) && confObj is double c ? c : 0.0;
        var typeStr = payload.TryGetValue("AnomalyType", out var typeObj) ? typeObj?.ToString() : null;

        StreamAnomalyType? anomalyType = null;
        if (typeStr != null && Enum.TryParse<StreamAnomalyType>(typeStr, out var parsed))
            anomalyType = parsed;

        return new StreamAnomalyResult
        {
            IsAnomaly = isAnomaly,
            AnomalyScore = score,
            AnomalyType = anomalyType,
            Severity = ScoreSeverity(score),
            AnalysisMethod = "ML",
            Confidence = confidence,
            Event = evt,
            BaselineMean = stats.Mean,
            BaselineStdDev = stats.StdDev
        };
    }

    private StreamAnomalyResult StatisticalAnalysis(StreamEvent evt, double value, RunningStatistics stats)
    {
        if (stats.Count < _config.MinObservations)
        {
            // Not enough data for reliable detection
            return new StreamAnomalyResult
            {
                IsAnomaly = false,
                AnomalyScore = 0.0,
                Severity = StreamAnomalySeverity.Low,
                AnalysisMethod = "Statistical-ZScore",
                Confidence = 0.0,
                Event = evt,
                BaselineMean = stats.Mean,
                BaselineStdDev = stats.StdDev
            };
        }

        var zScore = stats.StdDev > 0
            ? Math.Abs((value - stats.Mean) / stats.StdDev)
            : 0.0;

        var isAnomaly = zScore >= _config.ZScoreThreshold;
        var anomalyScore = Math.Min(1.0, zScore / (_config.ZScoreThreshold * 2));
        var confidence = Math.Min(1.0, (double)stats.Count / (_config.MinObservations * 10));

        return new StreamAnomalyResult
        {
            IsAnomaly = isAnomaly,
            AnomalyScore = anomalyScore,
            AnomalyType = isAnomaly ? StreamAnomalyType.ValueOutlier : null,
            Severity = ScoreSeverity(anomalyScore),
            AnalysisMethod = "Statistical-ZScore",
            Confidence = confidence,
            Event = evt,
            ZScore = zScore,
            BaselineMean = stats.Mean,
            BaselineStdDev = stats.StdDev
        };
    }

    private double ExtractValue(StreamEvent evt)
    {
        if (_config.ValueExtractor != null)
            return _config.ValueExtractor(evt);

        // Default: use data length as the metric
        return evt.Data.Length;
    }

    private static StreamAnomalySeverity ScoreSeverity(double score) => score switch
    {
        >= 0.9 => StreamAnomalySeverity.Critical,
        >= 0.7 => StreamAnomalySeverity.High,
        >= 0.4 => StreamAnomalySeverity.Medium,
        _ => StreamAnomalySeverity.Low
    };

    /// <summary>
    /// Online algorithm for computing running mean, variance, min, and max
    /// over a sliding window using Welford's method.
    /// </summary>
    private sealed class RunningStatistics
    {
        private readonly Queue<double> _window;
        private readonly int _maxSize;
        private double _sum;
        private double _sumSquares;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private readonly object _lock = new();

        public RunningStatistics(int maxSize)
        {
            _maxSize = maxSize;
            _window = new Queue<double>(maxSize);
        }

        public int Count { get { lock (_lock) { return _window.Count; } } }

        public double Mean
        {
            get
            {
                lock (_lock)
                {
                    return _window.Count > 0 ? _sum / _window.Count : 0;
                }
            }
        }

        public double StdDev
        {
            get
            {
                lock (_lock)
                {
                    if (_window.Count < 2) return 0;
                    var mean = _sum / _window.Count;
                    var variance = (_sumSquares / _window.Count) - (mean * mean);
                    return variance > 0 ? Math.Sqrt(variance) : 0;
                }
            }
        }

        public double Min { get { lock (_lock) { return _min == double.MaxValue ? 0 : _min; } } }
        public double Max { get { lock (_lock) { return _max == double.MinValue ? 0 : _max; } } }

        public void Add(double value)
        {
            lock (_lock)
            {
                if (_window.Count >= _maxSize)
                {
                    var removed = _window.Dequeue();
                    _sum -= removed;
                    _sumSquares -= removed * removed;
                }

                _window.Enqueue(value);
                _sum += value;
                _sumSquares += value * value;

                if (value < _min) _min = value;
                if (value > _max) _max = value;
            }
        }
    }
}
