using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Access pattern learning feature strategy (90.R3.2).
/// Learns and models user/application access patterns for prediction.
/// </summary>
public sealed class AccessPatternLearningStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, AccessPatternModel> _userModels = new BoundedDictionary<string, AccessPatternModel>(1000);
    private readonly BoundedDictionary<string, AccessPatternModel> _applicationModels = new BoundedDictionary<string, AccessPatternModel>(1000);
    private readonly ConcurrentQueue<AccessEvent> _eventBuffer = new();
    private readonly object _trainingLock = new();
    private DateTime _lastTraining = DateTime.MinValue;

    /// <inheritdoc/>
    public override string StrategyId => "feature-access-pattern-learning";

    /// <inheritdoc/>
    public override string StrategyName => "Access Pattern Learning";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Access Pattern Learning",
        Description = "Machine learning-based user and application access pattern discovery and modeling",
        Capabilities = IntelligenceCapabilities.Prediction,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "LearningWindow", Description = "Hours of history for learning", Required = false, DefaultValue = "168" },
            new ConfigurationRequirement { Key = "MinEventsForModel", Description = "Minimum events before modeling", Required = false, DefaultValue = "50" },
            new ConfigurationRequirement { Key = "PatternDecayDays", Description = "Days before patterns decay", Required = false, DefaultValue = "30" },
            new ConfigurationRequirement { Key = "TrainingIntervalMinutes", Description = "Minutes between model retraining", Required = false, DefaultValue = "60" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "access-patterns", "learning", "behavior", "modeling" }
    };

    /// <summary>
    /// Records an access event for learning.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="applicationId">Application identifier.</param>
    /// <param name="resourceId">Accessed resource identifier.</param>
    /// <param name="accessType">Type of access (read, write, delete).</param>
    /// <param name="metadata">Additional metadata.</param>
    public void RecordAccess(
        string userId,
        string applicationId,
        string resourceId,
        AccessType accessType,
        Dictionary<string, object>? metadata = null)
    {
        var accessEvent = new AccessEvent
        {
            UserId = userId,
            ApplicationId = applicationId,
            ResourceId = resourceId,
            AccessType = accessType,
            Timestamp = DateTime.UtcNow,
            DayOfWeek = DateTime.UtcNow.DayOfWeek,
            HourOfDay = DateTime.UtcNow.Hour,
            Metadata = metadata ?? new()
        };

        _eventBuffer.Enqueue(accessEvent);

        // Trigger training if needed
        var trainingInterval = TimeSpan.FromMinutes(int.Parse(GetConfig("TrainingIntervalMinutes") ?? "60"));
        if (DateTime.UtcNow - _lastTraining > trainingInterval)
        {
            Task.Run(() => TrainModelsAsync());
        }
    }

    /// <summary>
    /// Gets the access pattern model for a user.
    /// </summary>
    public AccessPatternModel? GetUserPattern(string userId)
    {
        return _userModels.TryGetValue(userId, out var model) ? model : null;
    }

    /// <summary>
    /// Gets the access pattern model for an application.
    /// </summary>
    public AccessPatternModel? GetApplicationPattern(string applicationId)
    {
        return _applicationModels.TryGetValue(applicationId, out var model) ? model : null;
    }

    /// <summary>
    /// Trains models from recorded events.
    /// </summary>
    public Task TrainModelsAsync(CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            lock (_trainingLock)
            {
                if (DateTime.UtcNow - _lastTraining < TimeSpan.FromMinutes(1))
                    return Task.CompletedTask;

                var learningWindow = TimeSpan.FromHours(int.Parse(GetConfig("LearningWindow") ?? "168"));
                var minEvents = int.Parse(GetConfig("MinEventsForModel") ?? "50");
                var cutoff = DateTime.UtcNow - learningWindow;

                // Drain buffer
                var events = new List<AccessEvent>();
                while (_eventBuffer.TryDequeue(out var evt))
                {
                    if (evt.Timestamp >= cutoff)
                        events.Add(evt);
                }

                // Group by user
                var userGroups = events.GroupBy(e => e.UserId);
                foreach (var group in userGroups)
                {
                    var userEvents = group.ToList();
                    if (userEvents.Count >= minEvents)
                    {
                        var model = BuildPatternModel(group.Key, userEvents);
                        _userModels[group.Key] = model;
                    }
                }

                // Group by application
                var appGroups = events.GroupBy(e => e.ApplicationId);
                foreach (var group in appGroups)
                {
                    var appEvents = group.ToList();
                    if (appEvents.Count >= minEvents)
                    {
                        var model = BuildPatternModel(group.Key, appEvents);
                        _applicationModels[group.Key] = model;
                    }
                }

                _lastTraining = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Gets learned patterns summary.
    /// </summary>
    public PatternLearningSummary GetSummary()
    {
        return new PatternLearningSummary
        {
            UserModelsCount = _userModels.Count,
            ApplicationModelsCount = _applicationModels.Count,
            BufferedEventsCount = _eventBuffer.Count,
            LastTrainingTime = _lastTraining,
            TopUserPatterns = _userModels.Values
                .OrderByDescending(m => m.TotalAccessCount)
                .Take(10)
                .ToList(),
            TopApplicationPatterns = _applicationModels.Values
                .OrderByDescending(m => m.TotalAccessCount)
                .Take(10)
                .ToList()
        };
    }

    private static AccessPatternModel BuildPatternModel(string entityId, List<AccessEvent> events)
    {
        var model = new AccessPatternModel
        {
            EntityId = entityId,
            TotalAccessCount = events.Count,
            FirstSeen = events.Min(e => e.Timestamp),
            LastSeen = events.Max(e => e.Timestamp)
        };

        // Hourly distribution
        var hourlyDistribution = new float[24];
        foreach (var evt in events)
            hourlyDistribution[evt.HourOfDay]++;
        var totalEvents = (float)events.Count;
        for (int i = 0; i < 24; i++)
            hourlyDistribution[i] /= totalEvents;
        model.HourlyDistribution = hourlyDistribution;

        // Find peak hours
        model.PeakHours = hourlyDistribution
            .Select((prob, hour) => (Hour: hour, Probability: prob))
            .OrderByDescending(x => x.Probability)
            .Take(3)
            .Select(x => x.Hour)
            .ToList();

        // Day of week distribution
        var dailyDistribution = new float[7];
        foreach (var evt in events)
            dailyDistribution[(int)evt.DayOfWeek]++;
        for (int i = 0; i < 7; i++)
            dailyDistribution[i] /= totalEvents;
        model.DailyDistribution = dailyDistribution;

        // Active days
        model.MostActiveDays = dailyDistribution
            .Select((prob, day) => (Day: (DayOfWeek)day, Probability: prob))
            .OrderByDescending(x => x.Probability)
            .Take(3)
            .Select(x => x.Day)
            .ToList();

        // Access type distribution
        model.AccessTypeDistribution = events
            .GroupBy(e => e.AccessType)
            .ToDictionary(g => g.Key, g => (float)g.Count() / totalEvents);

        // Resource frequency
        model.TopResources = events
            .GroupBy(e => e.ResourceId)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new ResourceAccessFrequency
            {
                ResourceId = g.Key,
                AccessCount = g.Count(),
                Probability = (float)g.Count() / totalEvents,
                LastAccessed = g.Max(e => e.Timestamp)
            })
            .ToList();

        // Calculate temporal patterns
        model.AverageSessionDurationMinutes = CalculateAverageSessionDuration(events);
        model.AverageAccessesPerSession = CalculateAverageAccessesPerSession(events);

        // Sequential patterns (what resources are accessed together)
        model.SequentialPatterns = FindSequentialPatterns(events);

        return model;
    }

    private static double CalculateAverageSessionDuration(List<AccessEvent> events)
    {
        var orderedEvents = events.OrderBy(e => e.Timestamp).ToList();
        var sessions = new List<TimeSpan>();
        var sessionStart = orderedEvents[0].Timestamp;
        var lastEvent = sessionStart;

        foreach (var evt in orderedEvents.Skip(1))
        {
            var gap = evt.Timestamp - lastEvent;
            if (gap.TotalMinutes > 30) // New session if gap > 30 min
            {
                sessions.Add(lastEvent - sessionStart);
                sessionStart = evt.Timestamp;
            }
            lastEvent = evt.Timestamp;
        }
        sessions.Add(lastEvent - sessionStart);

        return sessions.Count > 0 ? sessions.Average(s => s.TotalMinutes) : 0;
    }

    private static double CalculateAverageAccessesPerSession(List<AccessEvent> events)
    {
        var orderedEvents = events.OrderBy(e => e.Timestamp).ToList();
        var sessionCounts = new List<int>();
        var currentSessionCount = 1;
        var lastEvent = orderedEvents[0].Timestamp;

        foreach (var evt in orderedEvents.Skip(1))
        {
            var gap = evt.Timestamp - lastEvent;
            if (gap.TotalMinutes > 30)
            {
                sessionCounts.Add(currentSessionCount);
                currentSessionCount = 0;
            }
            currentSessionCount++;
            lastEvent = evt.Timestamp;
        }
        sessionCounts.Add(currentSessionCount);

        return sessionCounts.Count > 0 ? sessionCounts.Average() : 0;
    }

    private static List<SequentialPattern> FindSequentialPatterns(List<AccessEvent> events)
    {
        var patterns = new Dictionary<string, int>();
        var orderedEvents = events.OrderBy(e => e.Timestamp).ToList();

        for (int i = 0; i < orderedEvents.Count - 1; i++)
        {
            var gap = orderedEvents[i + 1].Timestamp - orderedEvents[i].Timestamp;
            if (gap.TotalMinutes <= 5) // Within same session context
            {
                var pattern = $"{orderedEvents[i].ResourceId}->{orderedEvents[i + 1].ResourceId}";
                patterns[pattern] = patterns.GetValueOrDefault(pattern) + 1;
            }
        }

        return patterns
            .Where(p => p.Value >= 3) // Minimum 3 occurrences
            .OrderByDescending(p => p.Value)
            .Take(10)
            .Select(p =>
            {
                var parts = p.Key.Split("->");
                return new SequentialPattern
                {
                    FromResource = parts[0],
                    ToResource = parts[1],
                    Frequency = p.Value,
                    Confidence = (float)p.Value / events.Count
                };
            })
            .ToList();
    }
}

/// <summary>
/// Access event for learning.
/// </summary>
public sealed class AccessEvent
{
    public string UserId { get; init; } = "";
    public string ApplicationId { get; init; } = "";
    public string ResourceId { get; init; } = "";
    public AccessType AccessType { get; init; }
    public DateTime Timestamp { get; init; }
    public DayOfWeek DayOfWeek { get; init; }
    public int HourOfDay { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Type of access.
/// </summary>
public enum AccessType
{
    Read, Write, Delete, List, Execute
}

/// <summary>
/// Learned access pattern model.
/// </summary>
public sealed class AccessPatternModel
{
    public string EntityId { get; init; } = "";
    public int TotalAccessCount { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public float[] HourlyDistribution { get; set; } = new float[24];
    public float[] DailyDistribution { get; set; } = new float[7];
    public List<int> PeakHours { get; set; } = new();
    public List<DayOfWeek> MostActiveDays { get; set; } = new();
    public Dictionary<AccessType, float> AccessTypeDistribution { get; set; } = new();
    public List<ResourceAccessFrequency> TopResources { get; set; } = new();
    public double AverageSessionDurationMinutes { get; set; }
    public double AverageAccessesPerSession { get; set; }
    public List<SequentialPattern> SequentialPatterns { get; set; } = new();
}

/// <summary>
/// Resource access frequency.
/// </summary>
public sealed class ResourceAccessFrequency
{
    public string ResourceId { get; init; } = "";
    public int AccessCount { get; init; }
    public float Probability { get; init; }
    public DateTime LastAccessed { get; init; }
}

/// <summary>
/// Sequential access pattern.
/// </summary>
public sealed class SequentialPattern
{
    public string FromResource { get; init; } = "";
    public string ToResource { get; init; } = "";
    public int Frequency { get; init; }
    public float Confidence { get; init; }
}

/// <summary>
/// Pattern learning summary.
/// </summary>
public sealed class PatternLearningSummary
{
    public int UserModelsCount { get; init; }
    public int ApplicationModelsCount { get; init; }
    public int BufferedEventsCount { get; init; }
    public DateTime LastTrainingTime { get; init; }
    public List<AccessPatternModel> TopUserPatterns { get; init; } = new();
    public List<AccessPatternModel> TopApplicationPatterns { get; init; } = new();
}

/// <summary>
/// Prefetch prediction feature strategy (90.R3.3).
/// Predicts what data to prefetch based on learned access patterns.
/// </summary>
public sealed class PrefetchPredictionStrategy : FeatureStrategyBase
{
    private readonly AccessPatternLearningStrategy _patternLearning;
    private readonly BoundedDictionary<string, PrefetchState> _prefetchStates = new BoundedDictionary<string, PrefetchState>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "feature-prefetch-prediction";

    /// <inheritdoc/>
    public override string StrategyName => "Prefetch Prediction";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Prefetch Prediction",
        Description = "Predicts resources to prefetch based on learned access patterns and sequential behavior",
        Capabilities = IntelligenceCapabilities.Prediction,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MinConfidence", Description = "Minimum prediction confidence", Required = false, DefaultValue = "0.3" },
            new ConfigurationRequirement { Key = "MaxPrefetchItems", Description = "Maximum items to prefetch", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "PrefetchAheadMinutes", Description = "Minutes ahead to predict", Required = false, DefaultValue = "5" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "prefetch", "prediction", "caching", "performance" }
    };

    /// <summary>
    /// Creates a prefetch prediction strategy with access pattern learning.
    /// </summary>
    /// <param name="patternLearning">Pattern learning strategy for accessing learned patterns.</param>
    public PrefetchPredictionStrategy(AccessPatternLearningStrategy? patternLearning = null)
    {
        _patternLearning = patternLearning ?? new AccessPatternLearningStrategy();
    }

    /// <summary>
    /// Predicts resources to prefetch for a user.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="currentResource">Currently accessed resource (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of resources to prefetch with confidence scores.</returns>
    public Task<IEnumerable<PrefetchRecommendation>> PredictPrefetchAsync(
        string userId,
        string? currentResource = null,
        CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var minConfidence = float.Parse(GetConfig("MinConfidence") ?? "0.3");
            var maxItems = int.Parse(GetConfig("MaxPrefetchItems") ?? "10");

            var recommendations = new List<PrefetchRecommendation>();

            // Get user pattern model
            var userPattern = _patternLearning.GetUserPattern(userId);
            if (userPattern == null)
            {
                return Task.FromResult<IEnumerable<PrefetchRecommendation>>(recommendations);
            }

            var now = DateTime.UtcNow;
            var currentHour = now.Hour;
            var currentDay = now.DayOfWeek;

            // Time-based predictions from frequently accessed resources
            foreach (var resource in userPattern.TopResources)
            {
                // Weight by current time relevance
                var timeWeight = userPattern.HourlyDistribution[currentHour] *
                                userPattern.DailyDistribution[(int)currentDay];

                var score = resource.Probability * (0.5f + 0.5f * timeWeight);

                if (score >= minConfidence)
                {
                    recommendations.Add(new PrefetchRecommendation
                    {
                        ResourceId = resource.ResourceId,
                        Confidence = score,
                        Reason = PrefetchReason.FrequentlyAccessed,
                        LastAccessed = resource.LastAccessed,
                        EstimatedAccessTime = now.AddMinutes(5)
                    });
                }
            }

            // Sequential pattern predictions
            if (!string.IsNullOrEmpty(currentResource))
            {
                var sequentialMatches = userPattern.SequentialPatterns
                    .Where(p => p.FromResource.Equals(currentResource, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Confidence);

                foreach (var pattern in sequentialMatches)
                {
                    if (pattern.Confidence >= minConfidence)
                    {
                        recommendations.Add(new PrefetchRecommendation
                        {
                            ResourceId = pattern.ToResource,
                            Confidence = pattern.Confidence,
                            Reason = PrefetchReason.SequentialPattern,
                            PrecedingResource = currentResource,
                            EstimatedAccessTime = now.AddSeconds(30)
                        });
                    }
                }
            }

            // Peak hour predictions
            if (userPattern.PeakHours.Contains(currentHour))
            {
                var peakBoost = 0.1f;
                foreach (var rec in recommendations)
                {
                    rec.Confidence = Math.Min(1.0f, rec.Confidence + peakBoost);
                }
            }

            return Task.FromResult<IEnumerable<PrefetchRecommendation>>(
                recommendations
                    .GroupBy(r => r.ResourceId)
                    .Select(g => g.OrderByDescending(r => r.Confidence).First())
                    .OrderByDescending(r => r.Confidence)
                    .Take(maxItems)
            );
        });
    }

    /// <summary>
    /// Notifies that a resource was accessed to update prediction state.
    /// </summary>
    public void NotifyAccess(string userId, string resourceId)
    {
        _prefetchStates.AddOrUpdate(
            userId,
            new PrefetchState { LastResource = resourceId, LastAccessTime = DateTime.UtcNow },
            (_, old) => new PrefetchState { LastResource = resourceId, LastAccessTime = DateTime.UtcNow, PreviousResource = old.LastResource }
        );
    }

    /// <summary>
    /// Gets prefetch-specific statistics.
    /// </summary>
    public PrefetchStatistics GetPrefetchStatistics()
    {
        return new PrefetchStatistics
        {
            ActiveUserStates = _prefetchStates.Count,
            PatternLearningSummary = _patternLearning.GetSummary()
        };
    }

    private sealed class PrefetchState
    {
        public string LastResource { get; init; } = "";
        public string? PreviousResource { get; init; }
        public DateTime LastAccessTime { get; init; }
    }
}

/// <summary>
/// Reason for prefetch recommendation.
/// </summary>
public enum PrefetchReason
{
    FrequentlyAccessed,
    SequentialPattern,
    TimeBasedPattern,
    UserBehavior,
    ApplicationPattern
}

/// <summary>
/// Prefetch recommendation.
/// </summary>
public sealed class PrefetchRecommendation
{
    public string ResourceId { get; init; } = "";
    public float Confidence { get; set; }
    public PrefetchReason Reason { get; init; }
    public DateTime? LastAccessed { get; init; }
    public DateTime? EstimatedAccessTime { get; init; }
    public string? PrecedingResource { get; init; }
}

/// <summary>
/// Prefetch statistics.
/// </summary>
public sealed class PrefetchStatistics
{
    public int ActiveUserStates { get; init; }
    public PatternLearningSummary PatternLearningSummary { get; init; } = new();
}

/// <summary>
/// Cache optimization feature strategy (90.R3.4).
/// Optimizes caching based on access predictions and patterns.
/// </summary>
public sealed class CacheOptimizationStrategy : FeatureStrategyBase
{
    private readonly AccessPatternLearningStrategy _patternLearning;
    private readonly BoundedDictionary<string, CacheEntry> _cacheState = new BoundedDictionary<string, CacheEntry>(1000);
    private long _cacheHits;
    private long _cacheMisses;

    /// <inheritdoc/>
    public override string StrategyId => "feature-cache-optimization";

    /// <inheritdoc/>
    public override string StrategyName => "Cache Optimization";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Cache Optimization",
        Description = "AI-driven cache policy optimization based on access patterns and predictions",
        Capabilities = IntelligenceCapabilities.Prediction,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxCacheSize", Description = "Maximum cache entries", Required = false, DefaultValue = "10000" },
            new ConfigurationRequirement { Key = "DefaultTtlMinutes", Description = "Default TTL in minutes", Required = false, DefaultValue = "60" },
            new ConfigurationRequirement { Key = "AdaptiveTtl", Description = "Enable adaptive TTL", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "EvictionStrategy", Description = "Eviction strategy (lru|lfu|arc|predictive)", Required = false, DefaultValue = "predictive" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "cache", "optimization", "ttl", "eviction" }
    };

    /// <summary>
    /// Creates a cache optimization strategy.
    /// </summary>
    /// <param name="patternLearning">Pattern learning strategy for access patterns.</param>
    public CacheOptimizationStrategy(AccessPatternLearningStrategy? patternLearning = null)
    {
        _patternLearning = patternLearning ?? new AccessPatternLearningStrategy();
    }

    /// <summary>
    /// Gets optimized TTL for a resource based on access patterns.
    /// </summary>
    /// <param name="resourceId">Resource identifier.</param>
    /// <param name="userId">Optional user identifier for personalized TTL.</param>
    /// <returns>Optimized TTL in minutes.</returns>
    public Task<CacheTtlRecommendation> GetOptimizedTtlAsync(
        string resourceId,
        string? userId = null,
        CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var defaultTtl = int.Parse(GetConfig("DefaultTtlMinutes") ?? "60");
            var adaptiveTtl = bool.Parse(GetConfig("AdaptiveTtl") ?? "true");

            if (!adaptiveTtl)
            {
                return Task.FromResult(new CacheTtlRecommendation
                {
                    ResourceId = resourceId,
                    RecommendedTtlMinutes = defaultTtl,
                    Reason = "Adaptive TTL disabled"
                });
            }

            // Get access patterns
            AccessPatternModel? pattern = null;
            if (!string.IsNullOrEmpty(userId))
            {
                pattern = _patternLearning.GetUserPattern(userId);
            }

            var resourceInfo = pattern?.TopResources.FirstOrDefault(r =>
                r.ResourceId.Equals(resourceId, StringComparison.OrdinalIgnoreCase));

            if (resourceInfo == null)
            {
                return Task.FromResult(new CacheTtlRecommendation
                {
                    ResourceId = resourceId,
                    RecommendedTtlMinutes = defaultTtl,
                    Reason = "No access history"
                });
            }

            // Calculate adaptive TTL based on access frequency
            var accessFrequency = resourceInfo.Probability;
            int optimizedTtl;
            string reason;

            if (accessFrequency > 0.1f) // Frequently accessed
            {
                optimizedTtl = Math.Max(defaultTtl * 2, 120);
                reason = $"High frequency access ({accessFrequency:P1})";
            }
            else if (accessFrequency > 0.01f) // Moderately accessed
            {
                optimizedTtl = defaultTtl;
                reason = $"Moderate frequency access ({accessFrequency:P1})";
            }
            else // Rarely accessed
            {
                optimizedTtl = Math.Max(defaultTtl / 2, 10);
                reason = $"Low frequency access ({accessFrequency:P1})";
            }

            // Adjust for recency
            var daysSinceLastAccess = (DateTime.UtcNow - resourceInfo.LastAccessed).TotalDays;
            if (daysSinceLastAccess > 7)
            {
                optimizedTtl = Math.Max(optimizedTtl / 2, 5);
                reason += ", reduced due to stale access";
            }

            return Task.FromResult(new CacheTtlRecommendation
            {
                ResourceId = resourceId,
                RecommendedTtlMinutes = optimizedTtl,
                Reason = reason,
                AccessFrequency = accessFrequency,
                LastAccessed = resourceInfo.LastAccessed
            });
        });
    }

    /// <summary>
    /// Gets eviction recommendations for cache optimization.
    /// </summary>
    /// <param name="targetEvictionCount">Number of entries to recommend for eviction.</param>
    /// <returns>Resources recommended for eviction.</returns>
    public Task<IEnumerable<CacheEvictionRecommendation>> GetEvictionRecommendationsAsync(
        int targetEvictionCount,
        CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var strategy = GetConfig("EvictionStrategy") ?? "predictive";

            var recommendations = strategy switch
            {
                "lru" => GetLruEvictions(targetEvictionCount),
                "lfu" => GetLfuEvictions(targetEvictionCount),
                "arc" => GetArcEvictions(targetEvictionCount),
                "predictive" => GetPredictiveEvictions(targetEvictionCount),
                _ => GetPredictiveEvictions(targetEvictionCount)
            };

            return Task.FromResult(recommendations);
        });
    }

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    public void RecordHit(string resourceId)
    {
        Interlocked.Increment(ref _cacheHits);
        _cacheState.AddOrUpdate(
            resourceId,
            new CacheEntry { HitCount = 1, LastHit = DateTime.UtcNow, CreatedAt = DateTime.UtcNow },
            (_, old) => old with { HitCount = old.HitCount + 1, LastHit = DateTime.UtcNow }
        );
    }

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    public void RecordMiss(string resourceId)
    {
        Interlocked.Increment(ref _cacheMisses);
    }

    /// <summary>
    /// Gets cache optimization-specific statistics.
    /// </summary>
    public CacheOptimizationStatistics GetCacheStatistics()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = hits + misses;

        return new CacheOptimizationStatistics
        {
            TotalHits = hits,
            TotalMisses = misses,
            HitRate = total > 0 ? (float)hits / total : 0,
            CachedEntries = _cacheState.Count,
            AverageHitsPerEntry = _cacheState.Count > 0 ? _cacheState.Values.Average(e => e.HitCount) : 0
        };
    }

    private IEnumerable<CacheEvictionRecommendation> GetLruEvictions(int count)
    {
        return _cacheState
            .OrderBy(e => e.Value.LastHit)
            .Take(count)
            .Select(e => new CacheEvictionRecommendation
            {
                ResourceId = e.Key,
                Priority = 1.0f - (float)(DateTime.UtcNow - e.Value.LastHit).TotalHours / 24,
                Reason = $"LRU: Last hit {e.Value.LastHit:g}"
            });
    }

    private IEnumerable<CacheEvictionRecommendation> GetLfuEvictions(int count)
    {
        return _cacheState
            .OrderBy(e => e.Value.HitCount)
            .Take(count)
            .Select(e => new CacheEvictionRecommendation
            {
                ResourceId = e.Key,
                Priority = 1.0f / (e.Value.HitCount + 1),
                Reason = $"LFU: {e.Value.HitCount} hits"
            });
    }

    private IEnumerable<CacheEvictionRecommendation> GetArcEvictions(int count)
    {
        // Simplified ARC: combine recency and frequency
        return _cacheState
            .Select(e =>
            {
                var recencyScore = 1.0 / ((DateTime.UtcNow - e.Value.LastHit).TotalMinutes + 1);
                var frequencyScore = e.Value.HitCount;
                var combinedScore = recencyScore * 0.5 + frequencyScore * 0.5;
                return (Entry: e, Score: combinedScore);
            })
            .OrderBy(x => x.Score)
            .Take(count)
            .Select(x => new CacheEvictionRecommendation
            {
                ResourceId = x.Entry.Key,
                Priority = (float)(1.0 / (x.Score + 1)),
                Reason = $"ARC score: {x.Score:F2}"
            });
    }

    private IEnumerable<CacheEvictionRecommendation> GetPredictiveEvictions(int count)
    {
        // Predictive: use access patterns to predict future access
        return _cacheState
            .Select(e =>
            {
                var age = (DateTime.UtcNow - e.Value.CreatedAt).TotalHours;
                var hitRate = e.Value.HitCount / Math.Max(age, 1);
                var recencyBonus = e.Value.LastHit > DateTime.UtcNow.AddHours(-1) ? 2.0 : 1.0;
                var predictedValue = hitRate * recencyBonus;

                return (Entry: e, Score: predictedValue);
            })
            .OrderBy(x => x.Score)
            .Take(count)
            .Select(x => new CacheEvictionRecommendation
            {
                ResourceId = x.Entry.Key,
                Priority = (float)(1.0 / (x.Score + 1)),
                Reason = $"Predicted low value: {x.Score:F2}"
            });
    }

    private sealed record CacheEntry
    {
        public long HitCount { get; init; }
        public DateTime LastHit { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}

/// <summary>
/// Cache TTL recommendation.
/// </summary>
public sealed class CacheTtlRecommendation
{
    public string ResourceId { get; init; } = "";
    public int RecommendedTtlMinutes { get; init; }
    public string Reason { get; init; } = "";
    public float AccessFrequency { get; init; }
    public DateTime? LastAccessed { get; init; }
}

/// <summary>
/// Cache eviction recommendation.
/// </summary>
public sealed class CacheEvictionRecommendation
{
    public string ResourceId { get; init; } = "";
    public float Priority { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Cache optimization statistics.
/// </summary>
public sealed class CacheOptimizationStatistics
{
    public long TotalHits { get; init; }
    public long TotalMisses { get; init; }
    public float HitRate { get; init; }
    public int CachedEntries { get; init; }
    public double AverageHitsPerEntry { get; init; }
}
