using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Intelligence Integration Feature (C10).
    /// Integrates UltimateReplication with UniversalIntelligence via
    /// "intelligence.predict.conflict" message bus topic for AI-enhanced
    /// conflict prediction, with rule-based fallback when Intelligence is unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AI capabilities (via message bus):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Conflict prediction</b>: Predict conflicts before they occur using historical patterns</item>
    ///   <item><b>Strategy recommendation</b>: Recommend optimal replication strategy based on workload</item>
    ///   <item><b>Lag prediction</b>: Predict future lag trends from current measurements</item>
    ///   <item><b>Anomaly detection</b>: Detect anomalous replication patterns</item>
    /// </list>
    /// <para>
    /// Rule-based fallbacks for each AI capability:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Conflict prediction</b>: Multi-master + high write rate = high conflict probability</item>
    ///   <item><b>Strategy recommendation</b>: Match consistency requirements to known strategy characteristics</item>
    ///   <item><b>Lag prediction</b>: Linear extrapolation from recent lag samples</item>
    ///   <item><b>Anomaly detection</b>: Standard deviation threshold from mean</item>
    /// </list>
    /// </remarks>
    public sealed class IntelligenceIntegrationFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, PredictionRecord> _predictionHistory = new BoundedDictionary<string, PredictionRecord>(1000);
        private readonly ConcurrentQueue<LagDataPoint> _recentLagData = new();
        private readonly TimeSpan _intelligenceTimeout;
        private readonly int _maxLagDataPoints;
        private bool _disposed;
        private readonly IDisposable? _anomalySubscription;

        // Topics
        private const string IntelligencePredictConflictTopic = "intelligence.predict.conflict";
        private const string IntelligencePredictConflictResponseTopic = "intelligence.predict.conflict.response";
        private const string IntelligenceRecommendTopic = "intelligence.recommend.strategy";
        private const string IntelligenceRecommendResponseTopic = "intelligence.recommend.strategy.response";
        private const string IntelligenceAnomalyTopic = "intelligence.detect.anomaly";
        private const string IntelligenceAnomalyResponseTopic = "intelligence.detect.anomaly.response";

        // Statistics
        private long _totalPredictions;
        private long _aiPredictions;
        private long _fallbackPredictions;
        private long _totalRecommendations;
        private long _totalAnomaliesDetected;

        /// <summary>
        /// Initializes a new instance of the IntelligenceIntegrationFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for Intelligence communication.</param>
        /// <param name="intelligenceTimeout">Timeout for Intelligence responses. Default: 5 seconds.</param>
        /// <param name="maxLagDataPoints">Max lag data points for trend analysis. Default: 500.</param>
        public IntelligenceIntegrationFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            TimeSpan? intelligenceTimeout = null,
            int maxLagDataPoints = 500)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _intelligenceTimeout = intelligenceTimeout ?? TimeSpan.FromSeconds(5);
            _maxLagDataPoints = maxLagDataPoints;

            // Subscribe to anomaly detection requests from other plugins
            _anomalySubscription = _messageBus.Subscribe(IntelligenceAnomalyTopic, HandleAnomalyRequestAsync);
        }

        /// <summary>Gets total predictions made.</summary>
        public long TotalPredictions => Interlocked.Read(ref _totalPredictions);

        /// <summary>Gets predictions made via Intelligence.</summary>
        public long AiPredictions => Interlocked.Read(ref _aiPredictions);

        /// <summary>Gets predictions made via rule-based fallback.</summary>
        public long FallbackPredictions => Interlocked.Read(ref _fallbackPredictions);

        /// <summary>
        /// Predicts the probability of replication conflicts for a given workload.
        /// Attempts Intelligence-based prediction first, falls back to rules.
        /// </summary>
        /// <param name="sourceNode">Source node ID.</param>
        /// <param name="targetNodes">Target node IDs.</param>
        /// <param name="activeStrategyName">Currently active strategy name.</param>
        /// <param name="writeRatePerSecond">Current write operations per second.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Conflict prediction with probability and recommended actions.</returns>
        public async Task<ConflictPrediction> PredictConflictsAsync(
            string sourceNode,
            IReadOnlyList<string> targetNodes,
            string activeStrategyName,
            double writeRatePerSecond,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalPredictions);

            // Try Intelligence-based prediction
            var aiPrediction = await TryAiConflictPredictionAsync(
                sourceNode, targetNodes, activeStrategyName, writeRatePerSecond, ct);

            if (aiPrediction != null)
            {
                Interlocked.Increment(ref _aiPredictions);
                RecordPrediction(sourceNode, aiPrediction);
                return aiPrediction;
            }

            // Fallback to rule-based prediction
            Interlocked.Increment(ref _fallbackPredictions);
            var fallback = RuleBasedConflictPrediction(sourceNode, targetNodes, activeStrategyName, writeRatePerSecond);
            RecordPrediction(sourceNode, fallback);
            return fallback;
        }

        /// <summary>
        /// Recommends the optimal replication strategy for given requirements.
        /// </summary>
        /// <param name="dataType">Type of data being replicated.</param>
        /// <param name="accessPattern">Read/write access pattern description.</param>
        /// <param name="maxLatencyMs">Maximum acceptable replication latency.</param>
        /// <param name="requireMultiMaster">Whether multi-master is required.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Strategy recommendation.</returns>
        public async Task<StrategyRecommendation> RecommendStrategyAsync(
            string dataType,
            string accessPattern,
            long maxLatencyMs,
            bool requireMultiMaster,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalRecommendations);

            // Try Intelligence-based recommendation
            var aiRec = await TryAiStrategyRecommendationAsync(
                dataType, accessPattern, maxLatencyMs, requireMultiMaster, ct);

            if (aiRec != null) return aiRec;

            // Fallback: rule-based selection from registry
            return RuleBasedStrategyRecommendation(maxLatencyMs, requireMultiMaster);
        }

        /// <summary>
        /// Predicts future lag based on recent measurements.
        /// Uses linear regression on recent data points.
        /// </summary>
        /// <param name="nodeKey">Node pair key (source->target).</param>
        /// <param name="predictMinutesAhead">How far ahead to predict (minutes).</param>
        /// <returns>Predicted lag in milliseconds.</returns>
        public LagPrediction PredictLag(string nodeKey, int predictMinutesAhead = 5)
        {
            var points = _recentLagData.Where(p => p.NodeKey == nodeKey).ToList();

            if (points.Count < 3)
            {
                return new LagPrediction
                {
                    NodeKey = nodeKey,
                    PredictedLagMs = points.LastOrDefault()?.LagMs ?? 0,
                    Confidence = 0.3,
                    Method = "insufficient-data",
                    PredictionTime = DateTimeOffset.UtcNow.AddMinutes(predictMinutesAhead)
                };
            }

            // Linear regression on last N points
            var recent = points.TakeLast(20).ToList();
            var baseTime = recent.First().Timestamp.ToUnixTimeMilliseconds();
            var xValues = recent.Select(p => (double)(p.Timestamp.ToUnixTimeMilliseconds() - baseTime)).ToList();
            var yValues = recent.Select(p => (double)p.LagMs).ToList();

            var (slope, intercept) = LinearRegression(xValues, yValues);
            var futureX = xValues.Last() + (predictMinutesAhead * 60 * 1000);
            var predictedLag = Math.Max(0, slope * futureX + intercept);

            // R-squared for confidence
            var meanY = yValues.Average();
            var ssRes = yValues.Zip(xValues, (y, x) => Math.Pow(y - (slope * x + intercept), 2)).Sum();
            var ssTot = yValues.Sum(y => Math.Pow(y - meanY, 2));
            var rSquared = ssTot > 0 ? 1.0 - (ssRes / ssTot) : 0.0;

            return new LagPrediction
            {
                NodeKey = nodeKey,
                PredictedLagMs = (long)predictedLag,
                Confidence = Math.Clamp(rSquared, 0.0, 1.0),
                Method = "linear-regression",
                PredictionTime = DateTimeOffset.UtcNow.AddMinutes(predictMinutesAhead),
                Trend = slope > 0.01 ? "increasing" : (slope < -0.01 ? "decreasing" : "stable")
            };
        }

        /// <summary>
        /// Records a lag data point for trend analysis and prediction.
        /// </summary>
        /// <param name="nodeKey">Node pair key.</param>
        /// <param name="lagMs">Current lag in ms.</param>
        public void RecordLagDataPoint(string nodeKey, long lagMs)
        {
            _recentLagData.Enqueue(new LagDataPoint
            {
                NodeKey = nodeKey,
                LagMs = lagMs,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Trim old data points
            while (_recentLagData.Count > _maxLagDataPoints)
                _recentLagData.TryDequeue(out _);
        }

        /// <summary>
        /// Detects anomalies in replication lag using standard deviation threshold.
        /// </summary>
        /// <param name="nodeKey">Node pair key.</param>
        /// <param name="currentLagMs">Current lag measurement.</param>
        /// <param name="stdDevThreshold">Number of standard deviations for anomaly. Default: 2.0.</param>
        /// <returns>Whether the measurement is anomalous.</returns>
        public AnomalyDetectionResult DetectLagAnomaly(string nodeKey, long currentLagMs, double stdDevThreshold = 2.0)
        {
            var points = _recentLagData.Where(p => p.NodeKey == nodeKey).Select(p => (double)p.LagMs).ToList();

            if (points.Count < 10)
            {
                return new AnomalyDetectionResult
                {
                    NodeKey = nodeKey,
                    CurrentLagMs = currentLagMs,
                    IsAnomaly = false,
                    Confidence = 0.0,
                    Reason = "Insufficient data for anomaly detection"
                };
            }

            var mean = points.Average();
            var stdDev = Math.Sqrt(points.Sum(x => Math.Pow(x - mean, 2)) / points.Count);
            var zScore = stdDev > 0 ? Math.Abs(currentLagMs - mean) / stdDev : 0;
            var isAnomaly = zScore > stdDevThreshold;

            if (isAnomaly)
                Interlocked.Increment(ref _totalAnomaliesDetected);

            return new AnomalyDetectionResult
            {
                NodeKey = nodeKey,
                CurrentLagMs = currentLagMs,
                IsAnomaly = isAnomaly,
                Confidence = Math.Min(zScore / (stdDevThreshold * 2), 1.0),
                ZScore = zScore,
                Mean = mean,
                StdDev = stdDev,
                Reason = isAnomaly
                    ? $"Lag {currentLagMs}ms is {zScore:F1} std devs from mean {mean:F0}ms"
                    : $"Lag {currentLagMs}ms within normal range (mean: {mean:F0}ms, stddev: {stdDev:F0}ms)"
            };
        }

        /// <summary>
        /// Gets prediction history for auditing.
        /// </summary>
        public IReadOnlyDictionary<string, PredictionRecord> GetPredictionHistory()
        {
            return _predictionHistory;
        }

        #region Private Methods

        private async Task<ConflictPrediction?> TryAiConflictPredictionAsync(
            string sourceNode,
            IReadOnlyList<string> targetNodes,
            string strategyName,
            double writeRate,
            CancellationToken ct)
        {
            try
            {
                var correlationId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<ConflictPrediction?>();

                var subscription = _messageBus.Subscribe(IntelligencePredictConflictResponseTopic, msg =>
                {
                    if (msg.CorrelationId == correlationId)
                    {
                        var success = msg.Payload.GetValueOrDefault("success") is true;
                        if (success)
                        {
                            var probability = msg.Payload.GetValueOrDefault("probability") is double p ? p : 0.0;
                            var actions = msg.Payload.GetValueOrDefault("actions") as IEnumerable<object>;

                            tcs.TrySetResult(new ConflictPrediction
                            {
                                SourceNode = sourceNode,
                                ConflictProbability = probability,
                                Method = "ai-intelligence",
                                Confidence = msg.Payload.GetValueOrDefault("confidence") is double c ? c : 0.8,
                                RecommendedActions = actions?.Select(a => a.ToString()!).ToArray()
                                    ?? new[] { "Monitor conflict rate" },
                                PredictedAt = DateTimeOffset.UtcNow
                            });
                        }
                        else
                        {
                            tcs.TrySetResult(null);
                        }
                    }
                    return Task.CompletedTask;
                });

                try
                {
                    await _messageBus.PublishAsync(IntelligencePredictConflictTopic, new PluginMessage
                    {
                        Type = IntelligencePredictConflictTopic,
                        CorrelationId = correlationId,
                        Source = "replication.ultimate.intelligence",
                        Payload = new Dictionary<string, object>
                        {
                            ["sourceNode"] = sourceNode,
                            ["targetNodes"] = targetNodes.ToArray(),
                            ["strategyName"] = strategyName,
                            ["writeRatePerSecond"] = writeRate,
                            ["registeredStrategies"] = _registry.Count
                        }
                    }, ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_intelligenceTimeout);

                    return await tcs.Task.WaitAsync(cts.Token);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }
            catch
            {
                return null;
            }
        }

        private ConflictPrediction RuleBasedConflictPrediction(
            string sourceNode,
            IReadOnlyList<string> targetNodes,
            string strategyName,
            double writeRatePerSecond)
        {
            var strategy = _registry.Get(strategyName);
            var isMultiMaster = strategy?.Characteristics.Capabilities.SupportsMultiMaster ?? false;
            var supportsAutoConflict = strategy?.Characteristics.SupportsAutoConflictResolution ?? false;

            // Rule-based probability estimation
            double probability = 0.05; // Base probability

            // Multi-master increases conflict probability
            if (isMultiMaster)
                probability += 0.15;

            // More targets = more potential conflicts
            probability += Math.Min(targetNodes.Count * 0.02, 0.20);

            // High write rate increases conflicts
            if (writeRatePerSecond > 1000) probability += 0.20;
            else if (writeRatePerSecond > 100) probability += 0.10;
            else if (writeRatePerSecond > 10) probability += 0.05;

            // Auto conflict resolution reduces effective probability
            if (supportsAutoConflict)
                probability *= 0.5;

            probability = Math.Clamp(probability, 0.0, 0.95);

            var actions = new List<string>();
            if (probability > 0.5)
                actions.Add("Consider switching to a CRDT-based strategy");
            if (probability > 0.3 && isMultiMaster)
                actions.Add("Review multi-master write patterns for hotspots");
            if (writeRatePerSecond > 100)
                actions.Add("Consider write batching to reduce conflict window");
            if (actions.Count == 0)
                actions.Add("Current configuration looks good for conflict avoidance");

            return new ConflictPrediction
            {
                SourceNode = sourceNode,
                ConflictProbability = probability,
                Method = "rule-based-fallback",
                Confidence = 0.6,
                RecommendedActions = actions.ToArray(),
                PredictedAt = DateTimeOffset.UtcNow
            };
        }

        private async Task<StrategyRecommendation?> TryAiStrategyRecommendationAsync(
            string dataType, string accessPattern, long maxLatencyMs, bool requireMultiMaster,
            CancellationToken ct)
        {
            try
            {
                var correlationId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<StrategyRecommendation?>();

                var subscription = _messageBus.Subscribe(IntelligenceRecommendResponseTopic, msg =>
                {
                    if (msg.CorrelationId == correlationId)
                    {
                        var success = msg.Payload.GetValueOrDefault("success") is true;
                        var strategyName = msg.Payload.GetValueOrDefault("strategy")?.ToString();

                        if (success && !string.IsNullOrEmpty(strategyName))
                        {
                            tcs.TrySetResult(new StrategyRecommendation
                            {
                                RecommendedStrategy = strategyName,
                                Confidence = msg.Payload.GetValueOrDefault("confidence") is double c ? c : 0.8,
                                Method = "ai-intelligence",
                                Reason = msg.Payload.GetValueOrDefault("reason")?.ToString() ?? "AI recommendation"
                            });
                        }
                        else
                        {
                            tcs.TrySetResult(null);
                        }
                    }
                    return Task.CompletedTask;
                });

                try
                {
                    await _messageBus.PublishAsync(IntelligenceRecommendTopic, new PluginMessage
                    {
                        Type = IntelligenceRecommendTopic,
                        CorrelationId = correlationId,
                        Source = "replication.ultimate.intelligence",
                        Payload = new Dictionary<string, object>
                        {
                            ["dataType"] = dataType,
                            ["accessPattern"] = accessPattern,
                            ["maxLatencyMs"] = maxLatencyMs,
                            ["requireMultiMaster"] = requireMultiMaster,
                            ["availableStrategies"] = _registry.RegisteredStrategies.ToArray()
                        }
                    }, ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_intelligenceTimeout);

                    return await tcs.Task.WaitAsync(cts.Token);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }
            catch
            {
                return null;
            }
        }

        private StrategyRecommendation RuleBasedStrategyRecommendation(long maxLatencyMs, bool requireMultiMaster)
        {
            var candidates = _registry.GetAll()
                .Where(kv => kv.Value.Characteristics.TypicalLagMs <= maxLatencyMs)
                .Where(kv => !requireMultiMaster || kv.Value.Characteristics.Capabilities.SupportsMultiMaster)
                .OrderByDescending(kv => kv.Value.Characteristics.SupportsAutoConflictResolution ? 1 : 0)
                .ThenBy(kv => kv.Value.Characteristics.TypicalLagMs)
                .ToList();

            var best = candidates.FirstOrDefault();

            return new StrategyRecommendation
            {
                RecommendedStrategy = best.Key ?? "CRDT",
                Confidence = candidates.Count > 0 ? 0.7 : 0.3,
                Method = "rule-based-fallback",
                Reason = candidates.Count > 0
                    ? $"Selected from {candidates.Count} candidates matching latency<={maxLatencyMs}ms" +
                      (requireMultiMaster ? ", multi-master required" : "")
                    : "No strategies match requirements; defaulting to CRDT"
            };
        }

        private void RecordPrediction(string sourceNode, ConflictPrediction prediction)
        {
            var key = $"{sourceNode}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            _predictionHistory[key] = new PredictionRecord
            {
                Key = key,
                SourceNode = sourceNode,
                Probability = prediction.ConflictProbability,
                Method = prediction.Method,
                Confidence = prediction.Confidence,
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        private static (double slope, double intercept) LinearRegression(List<double> xValues, List<double> yValues)
        {
            var n = xValues.Count;
            if (n < 2) return (0, yValues.FirstOrDefault());

            var sumX = xValues.Sum();
            var sumY = yValues.Sum();
            var sumXY = xValues.Zip(yValues, (x, y) => x * y).Sum();
            var sumX2 = xValues.Sum(x => x * x);

            var denominator = n * sumX2 - sumX * sumX;
            if (Math.Abs(denominator) < 1e-10)
                return (0, sumY / n);

            var slope = (n * sumXY - sumX * sumY) / denominator;
            var intercept = (sumY - slope * sumX) / n;

            return (slope, intercept);
        }

        #endregion

        /// <inheritdoc/>
        private async Task HandleAnomalyRequestAsync(PluginMessage message)
        {
            var nodeKey = message.Payload.GetValueOrDefault("nodeKey")?.ToString();
            var lagMsStr = message.Payload.GetValueOrDefault("lagMs")?.ToString();

            if (!string.IsNullOrEmpty(nodeKey) && long.TryParse(lagMsStr, out var lagMs))
            {
                var result = DetectLagAnomaly(nodeKey, lagMs);

                await _messageBus.PublishAsync(IntelligenceAnomalyResponseTopic, new PluginMessage
                {
                    Type = IntelligenceAnomalyResponseTopic,
                    CorrelationId = message.CorrelationId,
                    Source = "replication.ultimate.intelligence",
                    Payload = new Dictionary<string, object>
                    {
                        ["nodeKey"] = nodeKey,
                        ["isAnomaly"] = result.IsAnomaly,
                        ["confidence"] = result.Confidence,
                        ["zScore"] = result.ZScore,
                        ["reason"] = result.Reason
                    }
                });
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _anomalySubscription?.Dispose();
        }
    }

    #region Intelligence Integration Types

    /// <summary>
    /// Conflict prediction result.
    /// </summary>
    public sealed class ConflictPrediction
    {
        /// <summary>Source node.</summary>
        public required string SourceNode { get; init; }
        /// <summary>Predicted conflict probability (0.0-1.0).</summary>
        public required double ConflictProbability { get; init; }
        /// <summary>Prediction method used (ai-intelligence or rule-based-fallback).</summary>
        public required string Method { get; init; }
        /// <summary>Prediction confidence (0.0-1.0).</summary>
        public required double Confidence { get; init; }
        /// <summary>Recommended actions.</summary>
        public required string[] RecommendedActions { get; init; }
        /// <summary>When predicted.</summary>
        public required DateTimeOffset PredictedAt { get; init; }
    }

    /// <summary>
    /// Strategy recommendation.
    /// </summary>
    public sealed class StrategyRecommendation
    {
        /// <summary>Recommended strategy name.</summary>
        public required string RecommendedStrategy { get; init; }
        /// <summary>Recommendation confidence.</summary>
        public required double Confidence { get; init; }
        /// <summary>Recommendation method.</summary>
        public required string Method { get; init; }
        /// <summary>Reason for recommendation.</summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Lag prediction result.
    /// </summary>
    public sealed class LagPrediction
    {
        /// <summary>Node pair key.</summary>
        public required string NodeKey { get; init; }
        /// <summary>Predicted lag in ms.</summary>
        public required long PredictedLagMs { get; init; }
        /// <summary>Prediction confidence (R-squared).</summary>
        public required double Confidence { get; init; }
        /// <summary>Prediction method.</summary>
        public required string Method { get; init; }
        /// <summary>Time the prediction is for.</summary>
        public required DateTimeOffset PredictionTime { get; init; }
        /// <summary>Trend direction.</summary>
        public string Trend { get; init; } = "stable";
    }

    /// <summary>
    /// Anomaly detection result.
    /// </summary>
    public sealed class AnomalyDetectionResult
    {
        /// <summary>Node pair key.</summary>
        public required string NodeKey { get; init; }
        /// <summary>Current lag measurement.</summary>
        public required long CurrentLagMs { get; init; }
        /// <summary>Whether the measurement is anomalous.</summary>
        public required bool IsAnomaly { get; init; }
        /// <summary>Detection confidence.</summary>
        public required double Confidence { get; init; }
        /// <summary>Z-score of measurement.</summary>
        public double ZScore { get; init; }
        /// <summary>Mean lag.</summary>
        public double Mean { get; init; }
        /// <summary>Standard deviation.</summary>
        public double StdDev { get; init; }
        /// <summary>Detection reason.</summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Lag data point for trend analysis.
    /// </summary>
    public sealed class LagDataPoint
    {
        /// <summary>Node pair key.</summary>
        public required string NodeKey { get; init; }
        /// <summary>Lag in ms.</summary>
        public required long LagMs { get; init; }
        /// <summary>When measured.</summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Record of a prediction for auditing.
    /// </summary>
    public sealed class PredictionRecord
    {
        /// <summary>Record key.</summary>
        public required string Key { get; init; }
        /// <summary>Source node.</summary>
        public required string SourceNode { get; init; }
        /// <summary>Predicted probability.</summary>
        public required double Probability { get; init; }
        /// <summary>Method used.</summary>
        public required string Method { get; init; }
        /// <summary>Confidence.</summary>
        public required double Confidence { get; init; }
        /// <summary>When predicted.</summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    #endregion
}
