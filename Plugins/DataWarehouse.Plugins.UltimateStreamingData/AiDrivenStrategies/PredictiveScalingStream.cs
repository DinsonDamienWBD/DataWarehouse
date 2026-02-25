using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.AiDrivenStrategies;

#region Predictive Scaling Types

/// <summary>
/// Scaling direction recommendation.
/// </summary>
public enum ScalingDirection
{
    /// <summary>No scaling action needed.</summary>
    None,

    /// <summary>Scale up (add resources/partitions/consumers).</summary>
    ScaleUp,

    /// <summary>Scale down (reduce resources/partitions/consumers).</summary>
    ScaleDown
}

/// <summary>
/// Resource type that can be scaled.
/// </summary>
public enum ScalableResource
{
    /// <summary>Stream partitions.</summary>
    Partitions,

    /// <summary>Consumer instances.</summary>
    Consumers,

    /// <summary>Buffer capacity.</summary>
    BufferSize,

    /// <summary>Processing threads/parallelism.</summary>
    Parallelism,

    /// <summary>Memory allocation.</summary>
    Memory
}

/// <summary>
/// Result of a predictive scaling analysis.
/// </summary>
public sealed record ScalingRecommendation
{
    /// <summary>Gets the recommended scaling direction.</summary>
    public required ScalingDirection Direction { get; init; }

    /// <summary>Gets the resource type to scale.</summary>
    public required ScalableResource Resource { get; init; }

    /// <summary>Gets the recommended target value for the resource.</summary>
    public int RecommendedTarget { get; init; }

    /// <summary>Gets the current value of the resource.</summary>
    public int CurrentValue { get; init; }

    /// <summary>Gets the confidence level of the recommendation (0.0-1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the analysis method ("ML" or "RuleBased").</summary>
    public string AnalysisMethod { get; init; } = "RuleBased";

    /// <summary>Gets the predicted workload for the forecast horizon.</summary>
    public double PredictedWorkload { get; init; }

    /// <summary>Gets the current workload metric value.</summary>
    public double CurrentWorkload { get; init; }

    /// <summary>Gets the reason for the recommendation.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets when this recommendation was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Configuration for predictive scaling.
/// </summary>
public sealed record PredictiveScalingConfig
{
    /// <summary>Gets the scale-up threshold (utilization ratio). Default: 0.8 (80%).</summary>
    public double ScaleUpThreshold { get; init; } = 0.8;

    /// <summary>Gets the scale-down threshold (utilization ratio). Default: 0.2 (20%).</summary>
    public double ScaleDownThreshold { get; init; } = 0.2;

    /// <summary>Gets the minimum resources to maintain.</summary>
    public int MinResources { get; init; } = 1;

    /// <summary>Gets the maximum resources allowed.</summary>
    public int MaxResources { get; init; } = 100;

    /// <summary>Gets the cooldown period between scaling actions.</summary>
    public TimeSpan ScalingCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the forecast horizon for predictive analysis.</summary>
    public TimeSpan ForecastHorizon { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the observation window for collecting metrics.</summary>
    public int ObservationWindowSize { get; init; } = 60;

    /// <summary>Gets the Intelligence plugin request timeout.</summary>
    public TimeSpan IntelligenceTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Workload metrics for a stream.
/// </summary>
public sealed record StreamWorkloadMetrics
{
    /// <summary>Gets the stream or partition identifier.</summary>
    public required string StreamId { get; init; }

    /// <summary>Gets the current throughput (events/sec).</summary>
    public double Throughput { get; init; }

    /// <summary>Gets the current processing latency (ms).</summary>
    public double LatencyMs { get; init; }

    /// <summary>Gets the current resource utilization (0-1).</summary>
    public double Utilization { get; init; }

    /// <summary>Gets the consumer lag (events behind).</summary>
    public long ConsumerLag { get; init; }

    /// <summary>Gets the current number of active resources.</summary>
    public int ActiveResources { get; init; }

    /// <summary>Gets the timestamp of this metric snapshot.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

#endregion

/// <summary>
/// AI-driven predictive scaling for stream processing resources.
/// Routes to Intelligence plugin (topic: "intelligence.predict") for workload forecasting.
/// Falls back to rule-based threshold scaling when Intelligence is unavailable.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for predictive workload forecasting.
/// <b>MESSAGE TOPIC:</b> intelligence.predict
/// <b>FALLBACK:</b> Rule-based threshold scaling (simple high/low watermarks with linear extrapolation).
/// Thread-safe for concurrent metric ingestion and recommendation generation.
/// </remarks>
internal sealed class PredictiveScalingStream : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, MetricsHistory> _metricsHistory = new BoundedDictionary<string, MetricsHistory>(1000);
    private readonly BoundedDictionary<string, DateTimeOffset> _lastScaleAction = new BoundedDictionary<string, DateTimeOffset>(1000);
    private readonly IMessageBus? _messageBus;
    private readonly PredictiveScalingConfig _config;
    private long _totalRecommendations;
    private long _scaleUpRecommendations;
    private long _scaleDownRecommendations;
    private long _mlPredictions;
    private long _ruleBasedFallbacks;

    /// <inheritdoc/>
    public override string StrategyId => "ai-predictive-scaling-stream";

    /// <inheritdoc/>
    public override string DisplayName => "AI Predictive Scaling Stream";

    /// <inheritdoc/>
    public override StreamingCategory Category => StreamingCategory.StreamScalability;

    /// <inheritdoc/>
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = false,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = false,
        SupportsPartitioning = false,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 10_000,
        TypicalLatencyMs = 50.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "AI-driven predictive scaling for stream processing. Routes to Intelligence plugin for workload " +
        "forecasting with rule-based threshold fallback. Maintains per-stream metrics history for " +
        "trend analysis and proactive resource scaling.";

    /// <inheritdoc/>
    public override string[] Tags => ["predictive-scaling", "ai", "auto-scaling", "workload-forecasting", "intelligence"];

    /// <summary>
    /// Initializes a new instance of the <see cref="PredictiveScalingStream"/> class.
    /// </summary>
    /// <param name="messageBus">Optional message bus for Intelligence plugin communication.</param>
    /// <param name="config">Predictive scaling configuration.</param>
    public PredictiveScalingStream(IMessageBus? messageBus = null, PredictiveScalingConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new PredictiveScalingConfig();
    }

    /// <summary>
    /// Initializes a new instance for auto-discovery (parameterless constructor).
    /// </summary>
    public PredictiveScalingStream() : this(null, null) { }

    /// <summary>Gets the count of ML-based predictions performed.</summary>
    public long MlPredictions => Interlocked.Read(ref _mlPredictions);

    /// <summary>Gets the count of rule-based fallback analyses.</summary>
    public long RuleBasedFallbacks => Interlocked.Read(ref _ruleBasedFallbacks);

    /// <summary>
    /// Records workload metrics for a stream and generates a scaling recommendation.
    /// </summary>
    /// <param name="metrics">Current workload metrics.</param>
    /// <param name="resource">The resource type to evaluate for scaling.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A scaling recommendation.</returns>
    public async Task<ScalingRecommendation> EvaluateAndRecommendAsync(
        StreamWorkloadMetrics metrics,
        ScalableResource resource,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        Interlocked.Increment(ref _totalRecommendations);

        // Record metrics in history
        var history = _metricsHistory.GetOrAdd(metrics.StreamId, _ =>
            new MetricsHistory(_config.ObservationWindowSize));
        history.Add(metrics);

        // Check cooldown
        if (IsInCooldown(metrics.StreamId))
        {
            return new ScalingRecommendation
            {
                Direction = ScalingDirection.None,
                Resource = resource,
                CurrentValue = metrics.ActiveResources,
                RecommendedTarget = metrics.ActiveResources,
                Confidence = 1.0,
                AnalysisMethod = "Cooldown",
                CurrentWorkload = metrics.Utilization,
                Reason = "Within scaling cooldown period"
            };
        }

        // Try ML-based prediction via Intelligence plugin
        if (_messageBus != null)
        {
            try
            {
                var mlResult = await TryMlPredictionAsync(metrics, resource, history, ct);
                if (mlResult != null)
                {
                    Interlocked.Increment(ref _mlPredictions);
                    TrackScalingDirection(mlResult.Direction);
                    RecordOperation("ml-scaling-prediction");
                    return mlResult;
                }
            }
            catch (Exception)
            {
                // Fall through to rule-based fallback
            }
        }

        // Rule-based threshold fallback
        Interlocked.Increment(ref _ruleBasedFallbacks);
        var result = RuleBasedScaling(metrics, resource, history);
        TrackScalingDirection(result.Direction);
        RecordOperation("rule-based-scaling");
        return result;
    }

    /// <summary>
    /// Records a scaling action to enforce the cooldown period.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    public void RecordScalingAction(string streamId)
    {
        _lastScaleAction[streamId] = DateTimeOffset.UtcNow;
    }

    private async Task<ScalingRecommendation?> TryMlPredictionAsync(
        StreamWorkloadMetrics metrics,
        ScalableResource resource,
        MetricsHistory history,
        CancellationToken ct)
    {
        if (_messageBus == null) return null;

        var request = new PluginMessage
        {
            Type = "prediction.request",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "PredictiveScalingStream",
            Payload = new Dictionary<string, object>
            {
                ["DataType"] = "stream-workload",
                ["PredictionType"] = "scaling",
                ["StreamId"] = metrics.StreamId,
                ["Resource"] = resource.ToString(),
                ["ForecastHorizon"] = _config.ForecastHorizon.TotalMinutes,
                ["CurrentMetrics"] = new Dictionary<string, object>
                {
                    ["Throughput"] = metrics.Throughput,
                    ["LatencyMs"] = metrics.LatencyMs,
                    ["Utilization"] = metrics.Utilization,
                    ["ConsumerLag"] = metrics.ConsumerLag,
                    ["ActiveResources"] = metrics.ActiveResources
                },
                ["History"] = new Dictionary<string, object>
                {
                    ["ThroughputTrend"] = history.GetThroughputTrend(),
                    ["UtilizationTrend"] = history.GetUtilizationTrend(),
                    ["AvgLatency"] = history.AverageLatency,
                    ["ObservationCount"] = history.Count
                }
            }
        };

        var response = await _messageBus.SendAsync(
            "intelligence.predict",
            request,
            _config.IntelligenceTimeout,
            ct);

        if (!response.Success) return null;

        var payload = response.Payload as Dictionary<string, object>;
        if (payload == null) return null;

        var dirStr = payload.TryGetValue("Direction", out var dirObj) ? dirObj?.ToString() : "None";
        var target = payload.TryGetValue("RecommendedTarget", out var targetObj) && targetObj is int t ? t : metrics.ActiveResources;
        var predicted = payload.TryGetValue("PredictedWorkload", out var predObj) && predObj is double p ? p : metrics.Utilization;
        var confidence = payload.TryGetValue("Confidence", out var confObj) && confObj is double c ? c : 0.0;
        var reason = payload.TryGetValue("Reason", out var reasonObj) ? reasonObj?.ToString() ?? "" : "";

        if (!Enum.TryParse<ScalingDirection>(dirStr, out var direction))
            direction = ScalingDirection.None;

        // Clamp target within bounds
        target = Math.Clamp(target, _config.MinResources, _config.MaxResources);

        return new ScalingRecommendation
        {
            Direction = direction,
            Resource = resource,
            RecommendedTarget = target,
            CurrentValue = metrics.ActiveResources,
            Confidence = confidence,
            AnalysisMethod = "ML",
            PredictedWorkload = predicted,
            CurrentWorkload = metrics.Utilization,
            Reason = reason
        };
    }

    private ScalingRecommendation RuleBasedScaling(
        StreamWorkloadMetrics metrics,
        ScalableResource resource,
        MetricsHistory history)
    {
        var utilization = metrics.Utilization;
        var trend = history.GetUtilizationTrend();

        // Linear extrapolation: predict utilization at forecast horizon
        var minutesAhead = _config.ForecastHorizon.TotalMinutes;
        var predictedUtilization = utilization + (trend * minutesAhead);
        predictedUtilization = Math.Clamp(predictedUtilization, 0.0, 1.0);

        ScalingDirection direction;
        int target;
        string reason;

        if (predictedUtilization >= _config.ScaleUpThreshold || utilization >= _config.ScaleUpThreshold)
        {
            direction = ScalingDirection.ScaleUp;
            // Scale proportionally to predicted load
            var scaleFactor = predictedUtilization / _config.ScaleUpThreshold;
            target = (int)Math.Ceiling(metrics.ActiveResources * scaleFactor);
            target = Math.Clamp(target, metrics.ActiveResources + 1, _config.MaxResources);
            reason = $"Utilization {utilization:P0} (predicted {predictedUtilization:P0}) exceeds threshold {_config.ScaleUpThreshold:P0}";
        }
        else if (predictedUtilization <= _config.ScaleDownThreshold && utilization <= _config.ScaleDownThreshold)
        {
            direction = ScalingDirection.ScaleDown;
            target = Math.Max(_config.MinResources, metrics.ActiveResources - 1);
            reason = $"Utilization {utilization:P0} (predicted {predictedUtilization:P0}) below threshold {_config.ScaleDownThreshold:P0}";
        }
        else
        {
            direction = ScalingDirection.None;
            target = metrics.ActiveResources;
            reason = $"Utilization {utilization:P0} within normal range";
        }

        var confidence = Math.Min(1.0, (double)history.Count / _config.ObservationWindowSize);

        return new ScalingRecommendation
        {
            Direction = direction,
            Resource = resource,
            RecommendedTarget = target,
            CurrentValue = metrics.ActiveResources,
            Confidence = confidence,
            AnalysisMethod = "RuleBased",
            PredictedWorkload = predictedUtilization,
            CurrentWorkload = utilization,
            Reason = reason
        };
    }

    private bool IsInCooldown(string streamId)
    {
        if (_lastScaleAction.TryGetValue(streamId, out var lastAction))
        {
            return DateTimeOffset.UtcNow - lastAction < _config.ScalingCooldown;
        }
        return false;
    }

    private void TrackScalingDirection(ScalingDirection direction)
    {
        if (direction == ScalingDirection.ScaleUp) Interlocked.Increment(ref _scaleUpRecommendations);
        else if (direction == ScalingDirection.ScaleDown) Interlocked.Increment(ref _scaleDownRecommendations);
    }

    /// <summary>
    /// Maintains a sliding window of workload metrics for trend analysis.
    /// </summary>
    private sealed class MetricsHistory
    {
        private readonly Queue<StreamWorkloadMetrics> _history;
        private readonly int _maxSize;
        private readonly object _lock = new();

        public MetricsHistory(int maxSize)
        {
            _maxSize = maxSize;
            _history = new Queue<StreamWorkloadMetrics>(maxSize);
        }

        public int Count { get { lock (_lock) { return _history.Count; } } }

        public double AverageLatency
        {
            get
            {
                lock (_lock)
                {
                    return _history.Count > 0 ? _history.Average(m => m.LatencyMs) : 0;
                }
            }
        }

        public void Add(StreamWorkloadMetrics metrics)
        {
            lock (_lock)
            {
                if (_history.Count >= _maxSize)
                    _history.Dequeue();
                _history.Enqueue(metrics);
            }
        }

        /// <summary>
        /// Computes throughput trend (events/sec per minute) using linear regression slope.
        /// </summary>
        public double GetThroughputTrend()
        {
            lock (_lock)
            {
                return ComputeSlope(_history.Select(m => m.Throughput).ToArray());
            }
        }

        /// <summary>
        /// Computes utilization trend (change per observation) using linear regression slope.
        /// </summary>
        public double GetUtilizationTrend()
        {
            lock (_lock)
            {
                return ComputeSlope(_history.Select(m => m.Utilization).ToArray());
            }
        }

        private static double ComputeSlope(double[] values)
        {
            if (values.Length < 2) return 0;

            // Simple linear regression: y = slope * x + intercept
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            int n = values.Length;

            for (int i = 0; i < n; i++)
            {
                sumX += i;
                sumY += values[i];
                sumXY += i * values[i];
                sumX2 += i * i;
            }

            var denominator = (n * sumX2) - (sumX * sumX);
            if (Math.Abs(denominator) < 1e-10) return 0;

            return ((n * sumXY) - (sumX * sumY)) / denominator;
        }
    }
}
