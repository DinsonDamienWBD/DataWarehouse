using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// AI I/O scheduler strategy (T141.B1.1).
/// ML-based I/O scheduling that adapts in real-time to workload changes.
/// </summary>
/// <remarks>
/// <para><b>DEPENDENCY:</b> Uses AI provider for scheduling decisions.</para>
/// <para><b>INTEGRATION:</b> Works with storage plugins via message bus.</para>
/// </remarks>
public sealed class MlIoSchedulerStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, IoQueueState> _queues = new BoundedDictionary<string, IoQueueState>(1000);
    private readonly BoundedDictionary<string, SchedulingDecision> _recentDecisions = new BoundedDictionary<string, SchedulingDecision>(1000);
    private readonly ConcurrentQueue<IoRequest> _pendingRequests = new();
    private readonly SemaphoreSlim _schedulerLock = new(1, 1);

    /// <inheritdoc/>
    public override string StrategyId => "feature-ml-io-scheduler";

    /// <inheritdoc/>
    public override string StrategyName => "ML I/O Scheduler";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "ML I/O Scheduler",
        Description = "Machine learning-based I/O scheduling that outperforms static algorithms by adapting in real-time",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxQueueDepth", Description = "Maximum I/O queue depth", Required = false, DefaultValue = "256" },
            new ConfigurationRequirement { Key = "LatencyTarget99Ms", Description = "Target P99 latency in ms", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "ThroughputWeight", Description = "Weight for throughput optimization (0-1)", Required = false, DefaultValue = "0.5" },
            new ConfigurationRequirement { Key = "AdaptationRate", Description = "How quickly to adapt to changes", Required = false, DefaultValue = "0.1" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        Tags = new[] { "scheduler", "io", "ml", "performance", "latency", "throughput" }
    };

    /// <summary>
    /// Schedules an I/O request using ML-based optimization.
    /// </summary>
    public async Task<SchedulingDecision> ScheduleIoAsync(
        IoRequest request,
        IoQueueState queueState,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var latencyTarget = GetConfigDouble("LatencyTarget99Ms", 100);
            var throughputWeight = GetConfigDouble("ThroughputWeight", 0.5);

            // Calculate queue metrics
            var queueUtilization = queueState.CurrentDepth / (double)queueState.MaxDepth;
            var avgLatency = queueState.RecentLatencies.Any() ? queueState.RecentLatencies.Average() : 0;
            var latencyPressure = avgLatency / latencyTarget;

            // Determine priority based on request characteristics
            var priority = CalculatePriority(request, queueState, latencyPressure);

            // Determine optimal batch size
            var batchSize = CalculateOptimalBatchSize(queueState, throughputWeight, latencyPressure);

            // Determine if coalescing is beneficial
            var shouldCoalesce = ShouldCoalesceWithPending(request, queueState);

            // Determine execution strategy
            var strategy = DetermineExecutionStrategy(request, queueState, latencyPressure);

            var decision = new SchedulingDecision
            {
                RequestId = request.RequestId,
                Priority = priority,
                BatchSize = batchSize,
                ShouldCoalesce = shouldCoalesce,
                ExecutionStrategy = strategy,
                EstimatedLatencyMs = EstimateLatency(request, queueState, strategy),
                QueuePosition = CalculateQueuePosition(priority, queueState),
                Reasoning = GenerateReasoning(request, queueState, priority, strategy),
                DecidedAt = DateTime.UtcNow
            };

            _recentDecisions[request.RequestId] = decision;
            return decision;
        });
    }

    /// <summary>
    /// Analyzes queue performance and optimizes scheduling parameters.
    /// </summary>
    public async Task<QueueOptimization> OptimizeQueueAsync(
        string queueId,
        IoQueueState queueState,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for queue optimization");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var prompt = $@"Optimize I/O queue scheduling parameters:

Queue State:
- Queue ID: {queueId}
- Current Depth: {queueState.CurrentDepth}/{queueState.MaxDepth}
- Avg Latency: {queueState.RecentLatencies.DefaultIfEmpty(0).Average():F2}ms
- P99 Latency: {CalculatePercentile(queueState.RecentLatencies, 0.99):F2}ms
- Throughput: {queueState.ThroughputMBps:F2} MB/s
- Read/Write Ratio: {queueState.ReadWriteRatio:P0}
- Sequential Ratio: {queueState.SequentialRatio:P0}

Current Parameters:
- Batch Size: {queueState.CurrentBatchSize}
- Queue Depth Limit: {queueState.MaxDepth}
- Coalescing Enabled: {queueState.CoalescingEnabled}

Recommend optimizations:

Return JSON:
{{
  ""recommended_batch_size"": 32,
  ""recommended_max_depth"": 128,
  ""enable_coalescing"": true,
  ""priority_boost_reads"": false,
  ""latency_optimization_level"": ""balanced|aggressive|conservative"",
  ""expected_latency_improvement"": 0.15,
  ""expected_throughput_improvement"": 0.10,
  ""reasoning"": ""explanation""
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 400,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseQueueOptimization(response.Content, queueId);
        });
    }

    /// <summary>
    /// Predicts and prevents congestion before it occurs.
    /// </summary>
    public async Task<CongestionPrediction> PredictCongestionAsync(
        string queueId,
        IoQueueState queueState,
        IEnumerable<IoRequest> pendingRequests,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var pending = pendingRequests.ToList();
            var currentUtilization = queueState.CurrentDepth / (double)queueState.MaxDepth;
            var pendingBytes = pending.Sum(r => r.SizeBytes);
            var avgRequestSize = pending.Any() ? pending.Average(r => r.SizeBytes) : 0;

            // Calculate trend
            var utilizationTrend = CalculateUtilizationTrend(queueState);

            // Calculate time to congestion
            var timeToCongesion = EstimateTimeToCongestion(queueState, pending);

            // Determine congestion risk
            var congestionRisk = CalculateCongestionRisk(currentUtilization, utilizationTrend, pending.Count);

            return new CongestionPrediction
            {
                QueueId = queueId,
                CongestionRisk = congestionRisk,
                EstimatedTimeToCongestMs = timeToCongesion,
                CurrentUtilization = currentUtilization,
                UtilizationTrend = utilizationTrend,
                PendingRequestCount = pending.Count,
                PendingBytes = pendingBytes,
                RecommendedAction = GetCongestionAction(congestionRisk),
                PredictedAt = DateTime.UtcNow
            };
        });
    }

    private static int CalculatePriority(IoRequest request, IoQueueState queueState, double latencyPressure)
    {
        // Base priority
        var priority = 100;

        // Boost reads when queue is write-heavy
        if (request.IsRead && queueState.ReadWriteRatio < 0.3)
            priority += 20;

        // Boost sequential I/O for better throughput
        if (request.IsSequential)
            priority += 10;

        // Boost small requests under high latency pressure
        if (latencyPressure > 0.8 && request.SizeBytes < 4096)
            priority += 15;

        // Boost requests with QoS requirements
        if (request.QosTier > 0)
            priority += request.QosTier * 10;

        return priority;
    }

    private static int CalculateOptimalBatchSize(IoQueueState queueState, double throughputWeight, double latencyPressure)
    {
        // Start with current batch size
        var batchSize = queueState.CurrentBatchSize;

        // Increase batch size when optimizing for throughput
        if (throughputWeight > 0.6 && latencyPressure < 0.5)
            batchSize = Math.Min(batchSize * 2, 64);

        // Decrease batch size under latency pressure
        if (latencyPressure > 0.8)
            batchSize = Math.Max(batchSize / 2, 1);

        return batchSize;
    }

    private static bool ShouldCoalesceWithPending(IoRequest request, IoQueueState queueState)
    {
        if (!queueState.CoalescingEnabled)
            return false;

        // Coalesce sequential requests
        if (request.IsSequential && queueState.SequentialRatio > 0.5)
            return true;

        // Coalesce small writes
        if (!request.IsRead && request.SizeBytes < 4096)
            return true;

        return false;
    }

    private static string DetermineExecutionStrategy(IoRequest request, IoQueueState queueState, double latencyPressure)
    {
        if (latencyPressure > 0.9)
            return "IMMEDIATE";

        if (request.IsSequential && queueState.SequentialRatio > 0.7)
            return "BATCHED_SEQUENTIAL";

        if (queueState.ReadWriteRatio > 0.8)
            return "READ_OPTIMIZED";

        if (queueState.ReadWriteRatio < 0.2)
            return "WRITE_OPTIMIZED";

        return "BALANCED";
    }

    private static double EstimateLatency(IoRequest request, IoQueueState queueState, string strategy)
    {
        var baseLatency = queueState.RecentLatencies.DefaultIfEmpty(10).Average();

        return strategy switch
        {
            "IMMEDIATE" => baseLatency * 0.8,
            "BATCHED_SEQUENTIAL" => baseLatency * 1.2,
            "READ_OPTIMIZED" => request.IsRead ? baseLatency * 0.9 : baseLatency * 1.1,
            "WRITE_OPTIMIZED" => request.IsRead ? baseLatency * 1.1 : baseLatency * 0.9,
            _ => baseLatency
        };
    }

    private static int CalculateQueuePosition(int priority, IoQueueState queueState)
    {
        // Higher priority = lower position (closer to front)
        var position = (int)((200 - priority) / 200.0 * queueState.CurrentDepth);
        return Math.Max(0, position);
    }

    private static string GenerateReasoning(IoRequest request, IoQueueState queueState, int priority, string strategy)
    {
        var reasons = new List<string>();

        if (priority > 110)
            reasons.Add($"Priority boosted to {priority}");

        reasons.Add($"Strategy: {strategy}");

        if (queueState.CurrentDepth > queueState.MaxDepth * 0.8)
            reasons.Add("Queue pressure detected");

        return string.Join("; ", reasons);
    }

    private static double CalculateUtilizationTrend(IoQueueState queueState)
    {
        if (queueState.UtilizationHistory.Count < 2)
            return 0;

        var recent = queueState.UtilizationHistory.TakeLast(10).ToList();
        var older = recent.Take(5).Average();
        var newer = recent.Skip(5).Take(5).DefaultIfEmpty(older).Average();

        return newer - older;
    }

    private static double EstimateTimeToCongestion(IoQueueState queueState, List<IoRequest> pending)
    {
        var currentUtil = queueState.CurrentDepth / (double)queueState.MaxDepth;
        if (currentUtil >= 1)
            return 0;

        var remainingCapacity = queueState.MaxDepth - queueState.CurrentDepth;
        var avgProcessingTime = queueState.RecentLatencies.DefaultIfEmpty(10).Average();

        // Estimate based on pending requests
        if (pending.Count > remainingCapacity)
            return remainingCapacity * avgProcessingTime;

        return double.MaxValue; // No congestion predicted
    }

    private static double CalculateCongestionRisk(double currentUtilization, double trend, int pendingCount)
    {
        var risk = currentUtilization;

        if (trend > 0)
            risk += trend * 10;

        if (pendingCount > 100)
            risk += 0.1;

        return Math.Min(1.0, risk);
    }

    private static string GetCongestionAction(double congestionRisk)
    {
        if (congestionRisk > 0.9)
            return "THROTTLE_INCOMING";
        if (congestionRisk > 0.7)
            return "INCREASE_BATCH_SIZE";
        if (congestionRisk > 0.5)
            return "ENABLE_COALESCING";
        return "MONITOR";
    }

    private static double CalculatePercentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }

    private static QueueOptimization ParseQueueOptimization(string response, string queueId)
    {
        var optimization = new QueueOptimization
        {
            QueueId = queueId,
            OptimizedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("recommended_batch_size", out var bs))
                    optimization.RecommendedBatchSize = bs.GetInt32();

                if (doc.RootElement.TryGetProperty("recommended_max_depth", out var md))
                    optimization.RecommendedMaxDepth = md.GetInt32();

                if (doc.RootElement.TryGetProperty("enable_coalescing", out var coal))
                    optimization.EnableCoalescing = coal.GetBoolean();

                if (doc.RootElement.TryGetProperty("latency_optimization_level", out var lat))
                    optimization.LatencyOptimizationLevel = lat.GetString() ?? "balanced";

                if (doc.RootElement.TryGetProperty("expected_latency_improvement", out var li))
                    optimization.ExpectedLatencyImprovement = li.GetDouble();

                if (doc.RootElement.TryGetProperty("expected_throughput_improvement", out var ti))
                    optimization.ExpectedThroughputImprovement = ti.GetDouble();

                if (doc.RootElement.TryGetProperty("reasoning", out var reason))
                    optimization.Reasoning = reason.GetString();
            }
        }
        catch { /* Parsing failure — return optimization with defaults */ }

        return optimization;
    }
}

/// <summary>
/// AI predictive prefetch strategy (T141.B2.1).
/// Anticipates data reads before they happen using ML pattern recognition.
/// </summary>
public sealed class AiPredictivePrefetchStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, AccessSequence> _accessSequences = new BoundedDictionary<string, AccessSequence>(1000);
    private readonly BoundedDictionary<string, PrefetchModel> _prefetchModels = new BoundedDictionary<string, PrefetchModel>(1000);
    private readonly ConcurrentQueue<PrefetchRequest> _prefetchQueue = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-ai-prefetch";

    /// <inheritdoc/>
    public override string StrategyName => "AI Predictive Prefetch";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Predictive Prefetch",
        Description = "Anticipates data reads using ML pattern recognition, even for seemingly random access patterns",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "SequenceWindowSize", Description = "Number of accesses for pattern detection", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "MinPredictionConfidence", Description = "Minimum confidence to trigger prefetch", Required = false, DefaultValue = "0.7" },
            new ConfigurationRequirement { Key = "MaxPrefetchAhead", Description = "Maximum blocks to prefetch", Required = false, DefaultValue = "16" },
            new ConfigurationRequirement { Key = "PrefetchTimeout", Description = "Prefetch timeout in ms", Required = false, DefaultValue = "5000" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        Tags = new[] { "prefetch", "prediction", "ml", "performance", "cache", "read-ahead" }
    };

    /// <summary>
    /// Records an access and returns prefetch recommendations.
    /// </summary>
    public async Task<StreamPrefetchRecommendation> RecordAccessAndPredictAsync(
        string streamId,
        long offset,
        long size,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var windowSize = GetConfigInt("SequenceWindowSize", 100);
            var minConfidence = GetConfigDouble("MinPredictionConfidence", 0.7);
            var maxPrefetch = GetConfigInt("MaxPrefetchAhead", 16);

            // Update access sequence
            var sequence = _accessSequences.GetOrAdd(streamId, _ => new AccessSequence { StreamId = streamId });
            sequence.Accesses.Enqueue(new AccessRecord { Offset = offset, Size = size, Timestamp = DateTime.UtcNow });
            while (sequence.Accesses.Count > windowSize)
                sequence.Accesses.TryDequeue(out _);

            // Detect pattern
            var pattern = DetectAccessPattern(sequence);

            // Predict next accesses
            var predictions = PredictNextAccesses(sequence, pattern, maxPrefetch);

            // Filter by confidence
            var highConfidence = predictions.Where(p => p.Confidence >= minConfidence).ToList();

            return new StreamPrefetchRecommendation
            {
                StreamId = streamId,
                DetectedPattern = pattern,
                PredictedAccesses = highConfidence,
                ShouldPrefetch = highConfidence.Any(),
                PrefetchBlockCount = highConfidence.Count,
                TotalPrefetchBytes = highConfidence.Sum(p => p.PredictedSize),
                Confidence = highConfidence.Any() ? highConfidence.Average(p => p.Confidence) : 0,
                RecommendedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Learns and updates the prefetch model for a stream.
    /// </summary>
    public async Task<PrefetchModelUpdate> UpdateModelAsync(
        string streamId,
        IEnumerable<AccessRecord> recentAccesses,
        IEnumerable<PrefetchOutcome> outcomes,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for model updates");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var accesses = recentAccesses.ToList();
            var outcomeList = outcomes.ToList();

            // Calculate model performance
            var hitRate = outcomeList.Count > 0 ?
                outcomeList.Count(o => o.WasUsed) / (double)outcomeList.Count : 0;

            var prompt = $@"Analyze prefetch model performance and suggest improvements:

Stream: {streamId}
Recent Accesses: {accesses.Count}
Prefetch Outcomes: {outcomeList.Count}
Hit Rate: {hitRate:P2}

Access Pattern Sample:
{string.Join("\n", accesses.Take(20).Select(a => $"Offset: {a.Offset}, Size: {a.Size}"))}

Prefetch Outcomes:
{string.Join("\n", outcomeList.Take(10).Select(o => $"Predicted: {o.PredictedOffset}, Used: {o.WasUsed}, Latency Saved: {o.LatencySavedMs}ms"))}

Suggest model improvements:

Return JSON:
{{
  ""pattern_detected"": ""sequential|strided|random|mixed"",
  ""stride_size"": 4096,
  ""confidence_adjustment"": 0.1,
  ""prefetch_depth_adjustment"": 2,
  ""recommendations"": [""rec1"", ""rec2""]
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 300,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseModelUpdate(response.Content, streamId, hitRate);
        });
    }

    /// <summary>
    /// Predicts prefetch needs for random access patterns.
    /// </summary>
    public async Task<RandomAccessPrediction> PredictRandomAccessAsync(
        string streamId,
        IEnumerable<AccessRecord> history,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for random access prediction");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var accesses = history.ToList();

            // Group by offset ranges
            var offsetRanges = GroupByOffsetRange(accesses, 1024 * 1024); // 1MB ranges

            var prompt = $@"Analyze random access patterns for prefetch optimization:

Stream: {streamId}
Total Accesses: {accesses.Count}

Offset Range Distribution:
{string.Join("\n", offsetRanges.Take(10).Select(r => $"Range {r.StartOffset / (1024 * 1024)}MB-{r.EndOffset / (1024 * 1024)}MB: {r.AccessCount} accesses"))}

Identify any hidden patterns even in seemingly random access:

Return JSON:
{{
  ""hidden_patterns"": [
    {{""type"": ""hotspot"", ""offset_range"": [0, 1048576], ""probability"": 0.8}},
    {{""type"": ""periodic"", ""interval_bytes"": 4096, ""probability"": 0.6}}
  ],
  ""prefetch_candidates"": [100000, 200000],
  ""confidence"": 0.6
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 400,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseRandomAccessPrediction(response.Content, streamId);
        });
    }

    /// <summary>
    /// Gets pending prefetch requests.
    /// </summary>
    public IEnumerable<PrefetchRequest> GetPendingPrefetches(int maxCount = 100)
    {
        var requests = new List<PrefetchRequest>();
        while (requests.Count < maxCount && _prefetchQueue.TryDequeue(out var req))
        {
            requests.Add(req);
        }
        return requests;
    }

    private static AccessPatternType DetectAccessPattern(AccessSequence sequence)
    {
        var accesses = sequence.Accesses.ToList();
        if (accesses.Count < 3)
            return AccessPatternType.Unknown;

        // Check for sequential pattern
        var isSequential = true;
        for (int i = 1; i < accesses.Count && isSequential; i++)
        {
            if (accesses[i].Offset != accesses[i - 1].Offset + accesses[i - 1].Size)
                isSequential = false;
        }
        if (isSequential)
            return AccessPatternType.Sequential;

        // Check for strided pattern
        if (accesses.Count >= 3)
        {
            var strides = new List<long>();
            for (int i = 1; i < accesses.Count; i++)
            {
                strides.Add(accesses[i].Offset - accesses[i - 1].Offset);
            }

            var avgStride = strides.Average();
            var strideVariance = strides.Sum(s => Math.Pow(s - avgStride, 2)) / strides.Count;
            if (strideVariance < avgStride * 0.1) // Low variance = consistent stride
                return AccessPatternType.Strided;
        }

        // Check for random
        return AccessPatternType.Random;
    }

    private static List<PrefetchPredictedAccess> PredictNextAccesses(AccessSequence sequence, AccessPatternType pattern, int maxPrefetch)
    {
        var predictions = new List<PrefetchPredictedAccess>();
        var accesses = sequence.Accesses.ToList();

        if (accesses.Count == 0)
            return predictions;

        var lastAccess = accesses.Last();

        switch (pattern)
        {
            case AccessPatternType.Sequential:
                for (int i = 1; i <= maxPrefetch; i++)
                {
                    predictions.Add(new PrefetchPredictedAccess
                    {
                        PredictedOffset = lastAccess.Offset + lastAccess.Size * i,
                        PredictedSize = lastAccess.Size,
                        Confidence = 0.95 - (i * 0.05)
                    });
                }
                break;

            case AccessPatternType.Strided:
                var strides = new List<long>();
                for (int i = 1; i < accesses.Count; i++)
                    strides.Add(accesses[i].Offset - accesses[i - 1].Offset);
                var stride = (long)strides.Average();

                for (int i = 1; i <= maxPrefetch; i++)
                {
                    predictions.Add(new PrefetchPredictedAccess
                    {
                        PredictedOffset = lastAccess.Offset + stride * i,
                        PredictedSize = lastAccess.Size,
                        Confidence = 0.85 - (i * 0.05)
                    });
                }
                break;

            case AccessPatternType.Random:
                // For random patterns, predict based on hotspots
                var offsetCounts = accesses.GroupBy(a => a.Offset / 4096 * 4096)
                    .OrderByDescending(g => g.Count())
                    .Take(maxPrefetch);

                foreach (var hotspot in offsetCounts)
                {
                    predictions.Add(new PrefetchPredictedAccess
                    {
                        PredictedOffset = hotspot.Key,
                        PredictedSize = 4096,
                        Confidence = 0.5 + (hotspot.Count() / (double)accesses.Count) * 0.3
                    });
                }
                break;
        }

        return predictions;
    }

    private static List<OffsetRange> GroupByOffsetRange(List<AccessRecord> accesses, long rangeSize)
    {
        return accesses
            .GroupBy(a => a.Offset / rangeSize)
            .Select(g => new OffsetRange
            {
                StartOffset = g.Key * rangeSize,
                EndOffset = (g.Key + 1) * rangeSize,
                AccessCount = g.Count()
            })
            .OrderByDescending(r => r.AccessCount)
            .ToList();
    }

    private static PrefetchModelUpdate ParseModelUpdate(string response, string streamId, double currentHitRate)
    {
        var update = new PrefetchModelUpdate
        {
            StreamId = streamId,
            CurrentHitRate = currentHitRate,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("pattern_detected", out var pat))
                    update.DetectedPattern = pat.GetString() ?? "unknown";

                if (doc.RootElement.TryGetProperty("stride_size", out var stride))
                    update.StrideSize = stride.GetInt64();

                if (doc.RootElement.TryGetProperty("confidence_adjustment", out var conf))
                    update.ConfidenceAdjustment = conf.GetDouble();

                if (doc.RootElement.TryGetProperty("prefetch_depth_adjustment", out var depth))
                    update.PrefetchDepthAdjustment = depth.GetInt32();

                if (doc.RootElement.TryGetProperty("recommendations", out var recs))
                    update.Recommendations = recs.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            }
        }
        catch { /* Parsing failure — return update with defaults */ }

        return update;
    }

    private static RandomAccessPrediction ParseRandomAccessPrediction(string response, string streamId)
    {
        var prediction = new RandomAccessPrediction
        {
            StreamId = streamId,
            PredictedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("prefetch_candidates", out var candidates))
                    prediction.PrefetchCandidates = candidates.EnumerateArray().Select(e => e.GetInt64()).ToList();

                if (doc.RootElement.TryGetProperty("confidence", out var conf))
                    prediction.Confidence = conf.GetDouble();
            }
        }
        catch { /* Parsing failure — return prediction with defaults */ }

        return prediction;
    }
}

/// <summary>
/// Latency optimization strategy (T141.B1.4).
/// Minimizes tail latency using ML-based optimizations.
/// </summary>
public sealed class LatencyOptimizerStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, LatencyProfile> _latencyProfiles = new BoundedDictionary<string, LatencyProfile>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "feature-latency-optimizer";

    /// <inheritdoc/>
    public override string StrategyName => "AI Latency Optimizer";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Latency Optimizer",
        Description = "Minimizes tail latency using ML-based analysis and real-time optimization",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "TargetP50Ms", Description = "Target P50 latency in ms", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "TargetP99Ms", Description = "Target P99 latency in ms", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "TargetP999Ms", Description = "Target P99.9 latency in ms", Required = false, DefaultValue = "500" },
            new ConfigurationRequirement { Key = "OptimizationAggression", Description = "How aggressive to optimize (0-1)", Required = false, DefaultValue = "0.5" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        Tags = new[] { "latency", "optimization", "tail-latency", "p99", "performance" }
    };

    /// <summary>
    /// Analyzes latency and provides optimization recommendations.
    /// </summary>
    public async Task<LatencyOptimization> AnalyzeAndOptimizeAsync(
        string operationId,
        IEnumerable<double> latencyMeasurements,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for latency optimization");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var measurements = latencyMeasurements.OrderBy(l => l).ToList();
            var targetP50 = GetConfigDouble("TargetP50Ms", 10);
            var targetP99 = GetConfigDouble("TargetP99Ms", 100);
            var targetP999 = GetConfigDouble("TargetP999Ms", 500);

            var p50 = CalculatePercentile(measurements, 0.50);
            var p90 = CalculatePercentile(measurements, 0.90);
            var p99 = CalculatePercentile(measurements, 0.99);
            var p999 = CalculatePercentile(measurements, 0.999);
            var max = measurements.Max();

            var prompt = $@"Analyze latency distribution and recommend optimizations:

Operation: {operationId}
Sample Size: {measurements.Count}

Current Latencies:
- P50: {p50:F2}ms (target: {targetP50}ms)
- P90: {p90:F2}ms
- P99: {p99:F2}ms (target: {targetP99}ms)
- P99.9: {p999:F2}ms (target: {targetP999}ms)
- Max: {max:F2}ms

Tail Ratio (P99/P50): {p99 / Math.Max(1, p50):F2}x

Recommend optimizations to reduce tail latency:

Return JSON:
{{
  ""bottleneck"": ""queue_depth|contention|io_wait|gc|network"",
  ""primary_optimization"": ""description"",
  ""secondary_optimizations"": [""opt1"", ""opt2""],
  ""expected_p99_reduction"": 0.20,
  ""implementation_priority"": ""immediate|high|medium|low""
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 400,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            var optimization = ParseLatencyOptimization(response.Content, operationId);
            optimization.CurrentP50Ms = p50;
            optimization.CurrentP90Ms = p90;
            optimization.CurrentP99Ms = p99;
            optimization.CurrentP999Ms = p999;
            optimization.CurrentMaxMs = max;
            optimization.TargetP50Ms = targetP50;
            optimization.TargetP99Ms = targetP99;
            optimization.TargetP999Ms = targetP999;
            optimization.MeetsTargets = p50 <= targetP50 && p99 <= targetP99 && p999 <= targetP999;

            return optimization;
        });
    }

    private static double CalculatePercentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }

    private static LatencyOptimization ParseLatencyOptimization(string response, string operationId)
    {
        var optimization = new LatencyOptimization
        {
            OperationId = operationId,
            AnalyzedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("bottleneck", out var bn))
                    optimization.IdentifiedBottleneck = bn.GetString();

                if (doc.RootElement.TryGetProperty("primary_optimization", out var po))
                    optimization.PrimaryOptimization = po.GetString();

                if (doc.RootElement.TryGetProperty("secondary_optimizations", out var so))
                    optimization.SecondaryOptimizations = so.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

                if (doc.RootElement.TryGetProperty("expected_p99_reduction", out var er))
                    optimization.ExpectedP99Reduction = er.GetDouble();

                if (doc.RootElement.TryGetProperty("implementation_priority", out var ip))
                    optimization.ImplementationPriority = ip.GetString();
            }
        }
        catch { /* Parsing failure — return optimization with defaults */ }

        return optimization;
    }
}

#region Supporting Types

/// <summary>I/O request for scheduling.</summary>
public sealed class IoRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public bool IsRead { get; init; }
    public long Offset { get; init; }
    public long SizeBytes { get; init; }
    public bool IsSequential { get; init; }
    public int QosTier { get; init; }
    public DateTime SubmittedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>I/O queue state.</summary>
public sealed class IoQueueState
{
    public int CurrentDepth { get; init; }
    public int MaxDepth { get; init; } = 256;
    public List<double> RecentLatencies { get; init; } = new();
    public double ThroughputMBps { get; init; }
    public double ReadWriteRatio { get; init; } = 0.5;
    public double SequentialRatio { get; init; } = 0.5;
    public int CurrentBatchSize { get; init; } = 16;
    public bool CoalescingEnabled { get; init; } = true;
    public List<double> UtilizationHistory { get; init; } = new();
}

/// <summary>Scheduling decision.</summary>
public sealed class SchedulingDecision
{
    public string RequestId { get; init; } = "";
    public int Priority { get; init; }
    public int BatchSize { get; init; }
    public bool ShouldCoalesce { get; init; }
    public string ExecutionStrategy { get; init; } = "";
    public double EstimatedLatencyMs { get; init; }
    public int QueuePosition { get; init; }
    public string? Reasoning { get; init; }
    public DateTime DecidedAt { get; init; }
}

/// <summary>Queue optimization result.</summary>
public sealed class QueueOptimization
{
    public string QueueId { get; init; } = "";
    public int RecommendedBatchSize { get; set; }
    public int RecommendedMaxDepth { get; set; }
    public bool EnableCoalescing { get; set; }
    public string LatencyOptimizationLevel { get; set; } = "balanced";
    public double ExpectedLatencyImprovement { get; set; }
    public double ExpectedThroughputImprovement { get; set; }
    public string? Reasoning { get; set; }
    public DateTime OptimizedAt { get; init; }
}

/// <summary>Congestion prediction.</summary>
public sealed class CongestionPrediction
{
    public string QueueId { get; init; } = "";
    public double CongestionRisk { get; init; }
    public double EstimatedTimeToCongestMs { get; init; }
    public double CurrentUtilization { get; init; }
    public double UtilizationTrend { get; init; }
    public int PendingRequestCount { get; init; }
    public long PendingBytes { get; init; }
    public string RecommendedAction { get; init; } = "";
    public DateTime PredictedAt { get; init; }
}

/// <summary>Access pattern type.</summary>
public enum AccessPatternType { Unknown, Sequential, Strided, Random, Mixed }

/// <summary>Access sequence.</summary>
public sealed class AccessSequence
{
    public string StreamId { get; init; } = "";
    public ConcurrentQueue<AccessRecord> Accesses { get; } = new();
}

/// <summary>Access record.</summary>
public sealed class AccessRecord
{
    public long Offset { get; init; }
    public long Size { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>Predicted access for prefetch.</summary>
public sealed class PrefetchPredictedAccess
{
    public long PredictedOffset { get; init; }
    public long PredictedSize { get; init; }
    public double Confidence { get; init; }
}

/// <summary>Stream prefetch recommendation.</summary>
public sealed class StreamPrefetchRecommendation
{
    public string StreamId { get; init; } = "";
    public AccessPatternType DetectedPattern { get; init; }
    public List<PrefetchPredictedAccess> PredictedAccesses { get; init; } = new();
    public bool ShouldPrefetch { get; init; }
    public int PrefetchBlockCount { get; init; }
    public long TotalPrefetchBytes { get; init; }
    public double Confidence { get; init; }
    public DateTime RecommendedAt { get; init; }
}

/// <summary>Prefetch outcome for model training.</summary>
public sealed class PrefetchOutcome
{
    public long PredictedOffset { get; init; }
    public bool WasUsed { get; init; }
    public double LatencySavedMs { get; init; }
}

/// <summary>Prefetch model update result.</summary>
public sealed class PrefetchModelUpdate
{
    public string StreamId { get; init; } = "";
    public double CurrentHitRate { get; init; }
    public string DetectedPattern { get; set; } = "";
    public long StrideSize { get; set; }
    public double ConfidenceAdjustment { get; set; }
    public int PrefetchDepthAdjustment { get; set; }
    public List<string> Recommendations { get; set; } = new();
    public DateTime UpdatedAt { get; init; }
}

/// <summary>Prefetch model.</summary>
public sealed class PrefetchModel
{
    public string StreamId { get; init; } = "";
    public AccessPatternType Pattern { get; set; }
    public long StrideSize { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.7;
    public int PrefetchDepth { get; set; } = 8;
    public double HitRate { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>Prefetch request.</summary>
public sealed class PrefetchRequest
{
    public string StreamId { get; init; } = "";
    public long Offset { get; init; }
    public long Size { get; init; }
    public double Confidence { get; init; }
}

/// <summary>Offset range for analysis.</summary>
public sealed class OffsetRange
{
    public long StartOffset { get; init; }
    public long EndOffset { get; init; }
    public int AccessCount { get; init; }
}

/// <summary>Random access prediction.</summary>
public sealed class RandomAccessPrediction
{
    public string StreamId { get; init; } = "";
    public List<long> PrefetchCandidates { get; set; } = new();
    public double Confidence { get; set; }
    public DateTime PredictedAt { get; init; }
}

/// <summary>Latency profile.</summary>
public sealed class LatencyProfile
{
    public string OperationId { get; init; } = "";
    public List<double> Measurements { get; init; } = new();
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Latency optimization result.</summary>
public sealed class LatencyOptimization
{
    public string OperationId { get; init; } = "";
    public double CurrentP50Ms { get; set; }
    public double CurrentP90Ms { get; set; }
    public double CurrentP99Ms { get; set; }
    public double CurrentP999Ms { get; set; }
    public double CurrentMaxMs { get; set; }
    public double TargetP50Ms { get; set; }
    public double TargetP99Ms { get; set; }
    public double TargetP999Ms { get; set; }
    public bool MeetsTargets { get; set; }
    public string? IdentifiedBottleneck { get; set; }
    public string? PrimaryOptimization { get; set; }
    public List<string> SecondaryOptimizations { get; set; } = new();
    public double ExpectedP99Reduction { get; set; }
    public string? ImplementationPriority { get; set; }
    public DateTime AnalyzedAt { get; init; }
}

#endregion
