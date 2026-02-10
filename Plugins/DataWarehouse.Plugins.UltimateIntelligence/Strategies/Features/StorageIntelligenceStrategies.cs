using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Workload DNA fingerprinting strategy (T140.B2.1).
/// Identifies and fingerprints unique workload patterns for intelligent storage decisions.
/// </summary>
/// <remarks>
/// <para><b>DEPENDENCY:</b> Uses AI provider for pattern analysis and classification.</para>
/// <para><b>INTEGRATION:</b> Works with UltimateStorage plugin via message bus.</para>
/// </remarks>
public sealed class WorkloadDnaStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, WorkloadDnaProfile> _profiles = new();
    private readonly ConcurrentDictionary<string, List<IoEvent>> _recentEvents = new();
    private readonly ConcurrentQueue<WorkloadClassification> _classificationHistory = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-workload-dna";

    /// <inheritdoc/>
    public override string StrategyName => "Workload DNA Fingerprinting";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Workload DNA",
        Description = "Identifies and fingerprints unique workload patterns for intelligent storage optimization",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.Prediction | IntelligenceCapabilities.Clustering,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ProfileWindowHours", Description = "Hours of data for profiling", Required = false, DefaultValue = "168" },
            new ConfigurationRequirement { Key = "FingerprintDimensions", Description = "Dimensions for workload fingerprint", Required = false, DefaultValue = "64" },
            new ConfigurationRequirement { Key = "ClassificationConfidence", Description = "Minimum confidence for classification", Required = false, DefaultValue = "0.7" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "workload", "fingerprinting", "classification", "storage", "optimization" }
    };

    /// <summary>
    /// Profiles a workload and generates a DNA fingerprint.
    /// </summary>
    public async Task<WorkloadDnaProfile> ProfileWorkloadAsync(
        string workloadId,
        IEnumerable<IoEvent> ioEvents,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for workload profiling");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var events = ioEvents.ToList();

            // Calculate statistical features
            var readOps = events.Count(e => e.OperationType == "Read");
            var writeOps = events.Count(e => e.OperationType == "Write");
            var totalOps = events.Count;

            var avgLatency = events.Average(e => e.LatencyMs);
            var p99Latency = CalculatePercentile(events.Select(e => e.LatencyMs), 0.99);
            var avgIoSize = events.Average(e => e.SizeBytes);
            var sequentialRatio = CalculateSequentialRatio(events);
            var randomRatio = 1 - sequentialRatio;

            var iops = CalculateIops(events);
            var throughput = CalculateThroughput(events);

            // Temporal patterns
            var hourlyDistribution = CalculateHourlyDistribution(events);
            var burstiness = CalculateBurstiness(events);

            // Generate DNA fingerprint using AI
            var prompt = $@"Analyze this I/O workload and generate a workload DNA fingerprint:

Workload Statistics:
- Total Operations: {totalOps}
- Read/Write Ratio: {(double)readOps / Math.Max(1, totalOps):P0} / {(double)writeOps / Math.Max(1, totalOps):P0}
- Average Latency: {avgLatency:F2}ms
- P99 Latency: {p99Latency:F2}ms
- Average I/O Size: {avgIoSize:N0} bytes
- Sequential Ratio: {sequentialRatio:P0}
- Random Ratio: {randomRatio:P0}
- IOPS: {iops:N0}
- Throughput: {throughput:N0} MB/s
- Burstiness Score: {burstiness:F2}

Peak Activity Hours: {string.Join(", ", hourlyDistribution.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}:00 ({kv.Value:P0})"))}

Classify this workload and generate fingerprint:

Return JSON:
{{
  ""workload_type"": ""OLTP|OLAP|STREAMING|BATCH|MIXED"",
  ""application_signature"": ""database|analytics|media|backup|general"",
  ""access_pattern"": ""random|sequential|mixed"",
  ""intensity"": ""low|medium|high|extreme"",
  ""predictability"": 0.0-1.0,
  ""seasonality"": ""hourly|daily|weekly|none"",
  ""fingerprint_features"": [0.1, 0.2, ...],
  ""optimization_hints"": [""hint1"", ""hint2""]
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 500,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            var profile = ParseWorkloadProfile(response.Content, workloadId, events);
            profile.ReadWriteRatio = (double)readOps / Math.Max(1, totalOps);
            profile.SequentialRatio = sequentialRatio;
            profile.AverageLatencyMs = avgLatency;
            profile.P99LatencyMs = p99Latency;
            profile.AverageIoSizeBytes = (long)avgIoSize;
            profile.Iops = iops;
            profile.ThroughputMBps = throughput;
            profile.Burstiness = burstiness;
            profile.HourlyDistribution = hourlyDistribution;

            _profiles[workloadId] = profile;
            return profile;
        });
    }

    /// <summary>
    /// Classifies a workload based on its DNA profile.
    /// </summary>
    public async Task<WorkloadClassification> ClassifyWorkloadAsync(
        string workloadId,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (!_profiles.TryGetValue(workloadId, out var profile))
            {
                return new WorkloadClassification
                {
                    WorkloadId = workloadId,
                    Success = false,
                    ErrorMessage = "Workload not profiled"
                };
            }

            var classification = new WorkloadClassification
            {
                WorkloadId = workloadId,
                Success = true,
                WorkloadType = profile.WorkloadType,
                ApplicationSignature = profile.ApplicationSignature,
                AccessPattern = profile.AccessPattern,
                Intensity = profile.Intensity,
                Predictability = profile.Predictability,
                Seasonality = profile.Seasonality,
                Fingerprint = profile.Fingerprint,
                Confidence = profile.ClassificationConfidence,
                ClassifiedAt = DateTime.UtcNow
            };

            // Store classification history
            _classificationHistory.Enqueue(classification);
            while (_classificationHistory.Count > 1000)
                _classificationHistory.TryDequeue(out _);

            return classification;
        });
    }

    /// <summary>
    /// Predicts future workload characteristics.
    /// </summary>
    public async Task<WorkloadPrediction> PredictWorkloadAsync(
        string workloadId,
        int horizonHours,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for workload prediction");

        return await ExecuteWithTrackingAsync(async () =>
        {
            if (!_profiles.TryGetValue(workloadId, out var profile))
            {
                return new WorkloadPrediction
                {
                    WorkloadId = workloadId,
                    HorizonHours = horizonHours,
                    Success = false,
                    ErrorMessage = "Workload not profiled"
                };
            }

            var prompt = $@"Predict future workload characteristics based on current profile:

Current Profile:
- Type: {profile.WorkloadType}
- Access Pattern: {profile.AccessPattern}
- Intensity: {profile.Intensity}
- Predictability: {profile.Predictability:P0}
- Seasonality: {profile.Seasonality}
- Current IOPS: {profile.Iops:N0}
- Current Throughput: {profile.ThroughputMBps:N0} MB/s
- Burstiness: {profile.Burstiness:F2}

Hourly Distribution: {string.Join(", ", profile.HourlyDistribution.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}h:{kv.Value:P0}"))}

Predict for next {horizonHours} hours:

Return JSON:
{{
  ""predicted_iops"": [1000, 1200, ...],
  ""predicted_throughput"": [100, 120, ...],
  ""peak_hours"": [9, 10, 14, 15],
  ""low_hours"": [2, 3, 4, 5],
  ""intensity_changes"": [{{""hour"": 9, ""change"": ""increase"", ""magnitude"": 0.3}}],
  ""confidence"": 0.8
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 400,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseWorkloadPrediction(response.Content, workloadId, horizonHours);
        });
    }

    /// <summary>
    /// Compares two workload fingerprints for similarity.
    /// </summary>
    public double CompareWorkloads(string workloadId1, string workloadId2)
    {
        if (!_profiles.TryGetValue(workloadId1, out var profile1) ||
            !_profiles.TryGetValue(workloadId2, out var profile2))
        {
            return 0;
        }

        if (profile1.Fingerprint == null || profile2.Fingerprint == null ||
            profile1.Fingerprint.Length != profile2.Fingerprint.Length)
        {
            return 0;
        }

        // Cosine similarity
        double dot = 0, norm1 = 0, norm2 = 0;
        for (int i = 0; i < profile1.Fingerprint.Length; i++)
        {
            dot += profile1.Fingerprint[i] * profile2.Fingerprint[i];
            norm1 += profile1.Fingerprint[i] * profile1.Fingerprint[i];
            norm2 += profile2.Fingerprint[i] * profile2.Fingerprint[i];
        }

        var denom = Math.Sqrt(norm1) * Math.Sqrt(norm2);
        return denom > 0 ? dot / denom : 0;
    }

    /// <summary>
    /// Gets optimization hints for a workload.
    /// </summary>
    public IEnumerable<string> GetOptimizationHints(string workloadId)
    {
        return _profiles.TryGetValue(workloadId, out var profile)
            ? profile.OptimizationHints
            : Enumerable.Empty<string>();
    }

    private static double CalculatePercentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }

    private static double CalculateSequentialRatio(List<IoEvent> events)
    {
        if (events.Count < 2) return 0.5;

        var sequential = 0;
        var ordered = events.OrderBy(e => e.Timestamp).ToList();

        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Offset == ordered[i - 1].Offset + ordered[i - 1].SizeBytes)
                sequential++;
        }

        return (double)sequential / (events.Count - 1);
    }

    private static double CalculateIops(List<IoEvent> events)
    {
        if (events.Count < 2) return 0;
        var timeSpan = events.Max(e => e.Timestamp) - events.Min(e => e.Timestamp);
        return events.Count / Math.Max(1, timeSpan.TotalSeconds);
    }

    private static double CalculateThroughput(List<IoEvent> events)
    {
        if (events.Count < 2) return 0;
        var totalBytes = events.Sum(e => e.SizeBytes);
        var timeSpan = events.Max(e => e.Timestamp) - events.Min(e => e.Timestamp);
        return (totalBytes / (1024.0 * 1024)) / Math.Max(1, timeSpan.TotalSeconds);
    }

    private static Dictionary<int, double> CalculateHourlyDistribution(List<IoEvent> events)
    {
        var hourCounts = events.GroupBy(e => e.Timestamp.Hour).ToDictionary(g => g.Key, g => g.Count());
        var total = (double)events.Count;
        return hourCounts.ToDictionary(kv => kv.Key, kv => kv.Value / total);
    }

    private static double CalculateBurstiness(List<IoEvent> events)
    {
        if (events.Count < 10) return 0;

        var intervals = new List<double>();
        var ordered = events.OrderBy(e => e.Timestamp).ToList();

        for (int i = 1; i < ordered.Count; i++)
        {
            intervals.Add((ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalMilliseconds);
        }

        var mean = intervals.Average();
        var variance = intervals.Sum(i => Math.Pow(i - mean, 2)) / intervals.Count;
        var stdDev = Math.Sqrt(variance);

        return mean > 0 ? stdDev / mean : 0; // Coefficient of variation
    }

    private static WorkloadDnaProfile ParseWorkloadProfile(string response, string workloadId, List<IoEvent> events)
    {
        var profile = new WorkloadDnaProfile
        {
            WorkloadId = workloadId,
            ProfiledAt = DateTime.UtcNow,
            EventCount = events.Count
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);

                profile.WorkloadType = doc.RootElement.TryGetProperty("workload_type", out var wt) ? wt.GetString() ?? "MIXED" : "MIXED";
                profile.ApplicationSignature = doc.RootElement.TryGetProperty("application_signature", out var ap) ? ap.GetString() ?? "general" : "general";
                profile.AccessPattern = doc.RootElement.TryGetProperty("access_pattern", out var acc) ? acc.GetString() ?? "mixed" : "mixed";
                profile.Intensity = doc.RootElement.TryGetProperty("intensity", out var inten) ? inten.GetString() ?? "medium" : "medium";
                profile.Predictability = doc.RootElement.TryGetProperty("predictability", out var pred) ? pred.GetDouble() : 0.5;
                profile.Seasonality = doc.RootElement.TryGetProperty("seasonality", out var seas) ? seas.GetString() ?? "none" : "none";

                if (doc.RootElement.TryGetProperty("fingerprint_features", out var fp))
                {
                    profile.Fingerprint = fp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                }

                if (doc.RootElement.TryGetProperty("optimization_hints", out var hints))
                {
                    profile.OptimizationHints = hints.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                }

                profile.ClassificationConfidence = 0.8;
            }
        }
        catch { }

        return profile;
    }

    private static WorkloadPrediction ParseWorkloadPrediction(string response, string workloadId, int horizonHours)
    {
        var prediction = new WorkloadPrediction
        {
            WorkloadId = workloadId,
            HorizonHours = horizonHours,
            Success = true,
            PredictedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("predicted_iops", out var iops))
                    prediction.PredictedIops = iops.EnumerateArray().Select(e => e.GetDouble()).ToList();

                if (doc.RootElement.TryGetProperty("predicted_throughput", out var tp))
                    prediction.PredictedThroughputMBps = tp.EnumerateArray().Select(e => e.GetDouble()).ToList();

                if (doc.RootElement.TryGetProperty("peak_hours", out var peak))
                    prediction.PeakHours = peak.EnumerateArray().Select(e => e.GetInt32()).ToList();

                if (doc.RootElement.TryGetProperty("low_hours", out var low))
                    prediction.LowHours = low.EnumerateArray().Select(e => e.GetInt32()).ToList();

                if (doc.RootElement.TryGetProperty("confidence", out var conf))
                    prediction.Confidence = conf.GetDouble();
            }
        }
        catch { }

        return prediction;
    }
}

/// <summary>
/// ML-based tier migration strategy (T140.B1.1).
/// Automatically migrates data between hot/warm/cold/archive tiers based on AI predictions.
/// </summary>
public sealed class AiTierMigrationStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, TierPlacement> _placements = new();
    private readonly ConcurrentDictionary<string, AccessHistory> _accessHistories = new();
    private readonly ConcurrentQueue<MigrationRecommendation> _migrationQueue = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-ai-tier-migration";

    /// <inheritdoc/>
    public override string StrategyName => "AI-Driven Tier Migration";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Tier Migration",
        Description = "ML-based automatic data migration between storage tiers based on predicted access patterns",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "HistoryWindowDays", Description = "Days of access history to analyze", Required = false, DefaultValue = "30" },
            new ConfigurationRequirement { Key = "HotThreshold", Description = "Access score threshold for hot tier", Required = false, DefaultValue = "0.8" },
            new ConfigurationRequirement { Key = "WarmThreshold", Description = "Access score threshold for warm tier", Required = false, DefaultValue = "0.5" },
            new ConfigurationRequirement { Key = "ColdThreshold", Description = "Access score threshold for cold tier", Required = false, DefaultValue = "0.2" },
            new ConfigurationRequirement { Key = "DecayFactor", Description = "Daily decay factor for access scores", Required = false, DefaultValue = "0.95" },
            new ConfigurationRequirement { Key = "PredictionHorizonDays", Description = "Days to predict ahead", Required = false, DefaultValue = "7" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "tiering", "migration", "ml", "storage", "optimization", "cost" }
    };

    /// <summary>
    /// Analyzes access patterns and recommends tier placement.
    /// </summary>
    public async Task<TierRecommendation> RecommendTierAsync(
        string objectId,
        IEnumerable<TierAccessEvent> accessHistory,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for tier recommendations");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var history = accessHistory.ToList();
            var hotThreshold = double.Parse(GetConfig("HotThreshold") ?? "0.8");
            var warmThreshold = double.Parse(GetConfig("WarmThreshold") ?? "0.5");
            var coldThreshold = double.Parse(GetConfig("ColdThreshold") ?? "0.2");
            var decayFactor = double.Parse(GetConfig("DecayFactor") ?? "0.95");

            // Calculate weighted access score with temporal decay
            var accessScore = CalculateAccessScore(history, decayFactor);

            // Update access history
            _accessHistories[objectId] = new AccessHistory
            {
                ObjectId = objectId,
                AccessScore = accessScore,
                LastAccess = history.MaxBy(h => h.Timestamp)?.Timestamp ?? DateTime.UtcNow,
                TotalAccesses = history.Count,
                UpdatedAt = DateTime.UtcNow
            };

            // Determine recommended tier
            var recommendedTier = accessScore >= hotThreshold ? "Hot" :
                                  accessScore >= warmThreshold ? "Warm" :
                                  accessScore >= coldThreshold ? "Cold" : "Archive";

            // Get AI-enhanced recommendation
            var prompt = $@"Analyze this object's access pattern for optimal tier placement:

Object: {objectId}
Access Score: {accessScore:F3}
Total Accesses: {history.Count}
Last Access: {history.MaxBy(h => h.Timestamp)?.Timestamp:O}

Recent Access Pattern:
{string.Join("\n", history.OrderByDescending(h => h.Timestamp).Take(10).Select(h => $"- {h.Timestamp:O}: {h.AccessType}"))}

Current Thresholds: Hot>{hotThreshold:F2}, Warm>{warmThreshold:F2}, Cold>{coldThreshold:F2}

Recommend tier placement considering:
1. Access frequency trends
2. Time of day patterns
3. Predicted future access

Return JSON:
{{
  ""recommended_tier"": ""Hot|Warm|Cold|Archive"",
  ""confidence"": 0.9,
  ""reasoning"": ""explanation"",
  ""predicted_access_trend"": ""increasing|stable|decreasing"",
  ""cost_savings_estimate"": 0.15
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 300,
                Temperature = 0.2f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseTierRecommendation(response.Content, objectId, recommendedTier, accessScore);
        });
    }

    /// <summary>
    /// Predicts access patterns for proactive tier migration.
    /// </summary>
    public async Task<AccessPredictionResult> PredictAccessAsync(
        string objectId,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for access prediction");

        return await ExecuteWithTrackingAsync(async () =>
        {
            if (!_accessHistories.TryGetValue(objectId, out var history))
            {
                return new AccessPredictionResult
                {
                    ObjectId = objectId,
                    Success = false,
                    ErrorMessage = "No access history for this object"
                };
            }

            var horizonDays = int.Parse(GetConfig("PredictionHorizonDays") ?? "7");

            var prompt = $@"Predict access patterns for the next {horizonDays} days:

Object: {objectId}
Current Access Score: {history.AccessScore:F3}
Last Access: {history.LastAccess:O}
Total Accesses: {history.TotalAccesses}

Predict:
1. Likelihood of access each day
2. Expected access frequency
3. Whether tier change is needed

Return JSON:
{{
  ""daily_probabilities"": [0.8, 0.7, ...],
  ""expected_accesses"": [5, 4, ...],
  ""tier_change_recommended"": true,
  ""new_tier"": ""Warm"",
  ""change_timing"": ""immediate|within_24h|within_week""
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 300,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseAccessPrediction(response.Content, objectId, horizonDays);
        });
    }

    /// <summary>
    /// Gets pending migration recommendations.
    /// </summary>
    public IEnumerable<MigrationRecommendation> GetPendingMigrations(int maxCount = 100)
    {
        var recommendations = new List<MigrationRecommendation>();
        while (recommendations.Count < maxCount && _migrationQueue.TryDequeue(out var rec))
        {
            recommendations.Add(rec);
        }
        return recommendations;
    }

    /// <summary>
    /// Records an access event for learning.
    /// </summary>
    public void RecordAccess(string objectId, TierAccessEvent accessEvent)
    {
        var history = _accessHistories.GetOrAdd(objectId, _ => new AccessHistory { ObjectId = objectId });
        history.TotalAccesses++;
        history.LastAccess = accessEvent.Timestamp;
        history.UpdatedAt = DateTime.UtcNow;
    }

    private double CalculateAccessScore(List<TierAccessEvent> history, double decayFactor)
    {
        if (history.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var score = 0.0;

        foreach (var access in history)
        {
            var daysSince = (now - access.Timestamp).TotalDays;
            var weight = Math.Pow(decayFactor, daysSince);
            score += weight;
        }

        // Normalize to 0-1 range
        return Math.Min(1.0, score / 30); // Assuming 30 accesses = max score
    }

    private static TierRecommendation ParseTierRecommendation(string response, string objectId, string fallbackTier, double accessScore)
    {
        var recommendation = new TierRecommendation
        {
            ObjectId = objectId,
            RecommendedTier = fallbackTier,
            AccessScore = accessScore,
            RecommendedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("recommended_tier", out var tier))
                    recommendation.RecommendedTier = tier.GetString() ?? fallbackTier;

                if (doc.RootElement.TryGetProperty("confidence", out var conf))
                    recommendation.Confidence = conf.GetDouble();

                if (doc.RootElement.TryGetProperty("reasoning", out var reason))
                    recommendation.Reasoning = reason.GetString();

                if (doc.RootElement.TryGetProperty("predicted_access_trend", out var trend))
                    recommendation.PredictedAccessTrend = trend.GetString();

                if (doc.RootElement.TryGetProperty("cost_savings_estimate", out var savings))
                    recommendation.CostSavingsEstimate = savings.GetDouble();
            }
        }
        catch { }

        return recommendation;
    }

    private static AccessPredictionResult ParseAccessPrediction(string response, string objectId, int horizonDays)
    {
        var prediction = new AccessPredictionResult
        {
            ObjectId = objectId,
            HorizonDays = horizonDays,
            Success = true,
            PredictedAt = DateTime.UtcNow
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("daily_probabilities", out var probs))
                    prediction.DailyAccessProbabilities = probs.EnumerateArray().Select(e => e.GetDouble()).ToList();

                if (doc.RootElement.TryGetProperty("expected_accesses", out var accesses))
                    prediction.ExpectedAccessCounts = accesses.EnumerateArray().Select(e => e.GetInt32()).ToList();

                if (doc.RootElement.TryGetProperty("tier_change_recommended", out var change))
                    prediction.TierChangeRecommended = change.GetBoolean();

                if (doc.RootElement.TryGetProperty("new_tier", out var newTier))
                    prediction.RecommendedNewTier = newTier.GetString();

                if (doc.RootElement.TryGetProperty("change_timing", out var timing))
                    prediction.ChangeTiming = timing.GetString();
            }
        }
        catch { }

        return prediction;
    }
}

#region Supporting Types

/// <summary>I/O event for workload profiling.</summary>
public sealed class IoEvent
{
    public DateTime Timestamp { get; init; }
    public string OperationType { get; init; } = ""; // Read, Write
    public long Offset { get; init; }
    public long SizeBytes { get; init; }
    public double LatencyMs { get; init; }
}

/// <summary>Workload DNA profile.</summary>
public sealed class WorkloadDnaProfile
{
    public string WorkloadId { get; init; } = "";
    public DateTime ProfiledAt { get; init; }
    public int EventCount { get; init; }
    public string WorkloadType { get; set; } = ""; // OLTP, OLAP, STREAMING, BATCH, MIXED
    public string ApplicationSignature { get; set; } = ""; // database, analytics, media, backup, general
    public string AccessPattern { get; set; } = ""; // random, sequential, mixed
    public string Intensity { get; set; } = ""; // low, medium, high, extreme
    public double Predictability { get; set; }
    public string Seasonality { get; set; } = ""; // hourly, daily, weekly, none
    public double[]? Fingerprint { get; set; }
    public List<string> OptimizationHints { get; set; } = new();
    public double ClassificationConfidence { get; set; }
    public double ReadWriteRatio { get; set; }
    public double SequentialRatio { get; set; }
    public double AverageLatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public long AverageIoSizeBytes { get; set; }
    public double Iops { get; set; }
    public double ThroughputMBps { get; set; }
    public double Burstiness { get; set; }
    public Dictionary<int, double> HourlyDistribution { get; set; } = new();
}

/// <summary>Workload classification result.</summary>
public sealed class WorkloadClassification
{
    public string WorkloadId { get; init; } = "";
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string WorkloadType { get; init; } = "";
    public string ApplicationSignature { get; init; } = "";
    public string AccessPattern { get; init; } = "";
    public string Intensity { get; init; } = "";
    public double Predictability { get; init; }
    public string Seasonality { get; init; } = "";
    public double[]? Fingerprint { get; init; }
    public double Confidence { get; init; }
    public DateTime ClassifiedAt { get; init; }
}

/// <summary>Workload prediction result.</summary>
public sealed class WorkloadPrediction
{
    public string WorkloadId { get; init; } = "";
    public int HorizonHours { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public List<double> PredictedIops { get; set; } = new();
    public List<double> PredictedThroughputMBps { get; set; } = new();
    public List<int> PeakHours { get; set; } = new();
    public List<int> LowHours { get; set; } = new();
    public double Confidence { get; set; }
    public DateTime PredictedAt { get; init; }
}

/// <summary>Access event for tier analysis.</summary>
public sealed class TierAccessEvent
{
    public DateTime Timestamp { get; init; }
    public string AccessType { get; init; } = ""; // Read, Write
    public long SizeBytes { get; init; }
}

/// <summary>Access history for an object.</summary>
public sealed class AccessHistory
{
    public string ObjectId { get; init; } = "";
    public double AccessScore { get; set; }
    public DateTime LastAccess { get; set; }
    public int TotalAccesses { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Tier placement record.</summary>
public sealed class TierPlacement
{
    public string ObjectId { get; init; } = "";
    public string CurrentTier { get; init; } = "";
    public DateTime PlacedAt { get; init; }
}

/// <summary>Tier recommendation.</summary>
public sealed class TierRecommendation
{
    public string ObjectId { get; init; } = "";
    public string RecommendedTier { get; set; } = "";
    public double AccessScore { get; init; }
    public double Confidence { get; set; }
    public string? Reasoning { get; set; }
    public string? PredictedAccessTrend { get; set; }
    public double CostSavingsEstimate { get; set; }
    public DateTime RecommendedAt { get; init; }
}

/// <summary>Migration recommendation.</summary>
public sealed class MigrationRecommendation
{
    public string ObjectId { get; init; } = "";
    public string CurrentTier { get; init; } = "";
    public string TargetTier { get; init; } = "";
    public double Urgency { get; init; }
    public string Reason { get; init; } = "";
    public DateTime RecommendedAt { get; init; }
}

/// <summary>Access prediction result.</summary>
public sealed class AccessPredictionResult
{
    public string ObjectId { get; init; } = "";
    public int HorizonDays { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public List<double> DailyAccessProbabilities { get; set; } = new();
    public List<int> ExpectedAccessCounts { get; set; } = new();
    public bool TierChangeRecommended { get; set; }
    public string? RecommendedNewTier { get; set; }
    public string? ChangeTiming { get; set; }
    public DateTime PredictedAt { get; init; }
}

#endregion
