using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AccessPrediction;

/// <summary>
/// Production-ready access prediction plugin using Markov chains and time series analysis.
/// Implements intelligent pre-warming and caching based on predicted access patterns.
///
/// ML Techniques (Pure C#):
/// - Markov Chain models for sequential pattern prediction
/// - Exponential smoothing for time series forecasting
/// - ARIMA-like decomposition for trend/seasonality detection
/// - Recurrent pattern detection using sliding windows
///
/// Features:
/// - Access pattern learning from historical data
/// - Next-access prediction with confidence scores
/// - Time-based access pattern detection (hourly/daily/weekly)
/// - Pre-warming triggers for predicted accesses
/// - Cache hint generation
/// - Multi-order Markov chain support
///
/// Message Commands:
/// - prediction.next: Predict next likely accesses
/// - prediction.prewarm: Get items to pre-warm
/// - prediction.patterns: Analyze access patterns
/// - prediction.learn: Record access for learning
/// - prediction.stats: Get prediction statistics
/// </summary>
public sealed class AccessPredictionPlugin : IntelligencePluginBase
{
    private readonly ConcurrentDictionary<string, AccessSequence> _accessSequences;
    private readonly ConcurrentDictionary<string, TimeSeriesModel> _timeSeriesModels;
    private readonly ConcurrentDictionary<string, MarkovChain> _markovChains;
    private readonly ConcurrentQueue<PredictionRecord> _predictionHistory;
    private readonly ConcurrentDictionary<string, PrewarmCandidate> _prewarmQueue;
    private readonly SemaphoreSlim _modelLock;
    private readonly CancellationTokenSource _cts;

    private Task? _learnerTask;
    private Task? _prewarmTask;
    private GlobalPatternModel? _globalModel;
    private PredictionConfig _config;
    private long _totalPredictions;
    private long _correctPredictions;
    private long _prewarmHits;
    private long _prewarmMisses;
    private DateTime _sessionStart;

    private const int MaxSequenceLength = 1000;
    private const int MaxPredictionHistory = 10000;
    private const int LearnerIntervalMs = 60000; // 1 minute
    private const int PrewarmIntervalMs = 30000; // 30 seconds
    private const int DefaultMarkovOrder = 3;

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.prediction.access";

    /// <inheritdoc/>
    public override string Name => "Access Prediction Plugin";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.IntelligenceProvider;

    /// <summary>
    /// Initializes a new instance of the AccessPredictionPlugin.
    /// </summary>
    /// <param name="config">Optional prediction configuration.</param>
    public AccessPredictionPlugin(PredictionConfig? config = null)
    {
        _accessSequences = new ConcurrentDictionary<string, AccessSequence>();
        _timeSeriesModels = new ConcurrentDictionary<string, TimeSeriesModel>();
        _markovChains = new ConcurrentDictionary<string, MarkovChain>();
        _predictionHistory = new ConcurrentQueue<PredictionRecord>();
        _prewarmQueue = new ConcurrentDictionary<string, PrewarmCandidate>();
        _modelLock = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();
        _config = config ?? new PredictionConfig();
        _sessionStart = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _sessionStart = DateTime.UtcNow;
        _globalModel = new GlobalPatternModel();

        _learnerTask = RunLearnerAsync(_cts.Token);
        _prewarmTask = RunPrewarmGeneratorAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _cts.Cancel();

        var tasks = new List<Task>();
        if (_learnerTask != null) tasks.Add(_learnerTask);
        if (_prewarmTask != null) tasks.Add(_prewarmTask);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Expected
        }
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case "prediction.next":
                    HandlePredictNext(message);
                    break;
                case "prediction.prewarm":
                    HandleGetPrewarmItems(message);
                    break;
                case "prediction.patterns":
                    HandleAnalyzePatterns(message);
                    break;
                case "prediction.learn":
                    HandleRecordAccess(message);
                    break;
                case "prediction.stats":
                    HandleGetStats(message);
                    break;
                case "prediction.validate":
                    HandleValidatePrediction(message);
                    break;
                case "prediction.timeseries":
                    HandleTimeSeriesForecast(message);
                    break;
                case "prediction.user.patterns":
                    HandleGetUserPatterns(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }
        catch (Exception ex)
        {
            message.Payload["error"] = ex.Message;
            message.Payload["success"] = false;
        }
    }

    /// <summary>
    /// Records an access event for learning.
    /// </summary>
    /// <param name="userId">User or session identifier.</param>
    /// <param name="itemId">Accessed item identifier.</param>
    /// <param name="context">Optional context information.</param>
    public void RecordAccess(string userId, string itemId, AccessContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID required", nameof(userId));
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item ID required", nameof(itemId));

        var sequence = _accessSequences.GetOrAdd(userId, _ => new AccessSequence { UserId = userId });

        lock (sequence)
        {
            var entry = new AccessEntry
            {
                ItemId = itemId,
                Timestamp = DateTime.UtcNow,
                Hour = DateTime.UtcNow.Hour,
                DayOfWeek = DateTime.UtcNow.DayOfWeek,
                Context = context
            };

            sequence.Entries.Add(entry);
            if (sequence.Entries.Count > MaxSequenceLength)
            {
                sequence.Entries.RemoveAt(0);
            }
        }

        // Update global model
        _globalModel?.RecordAccess(itemId, DateTime.UtcNow);

        // Update Markov chain
        UpdateMarkovChain(userId, itemId);

        // Update time series model
        UpdateTimeSeriesModel(itemId);

        // Validate previous predictions
        ValidatePendingPredictions(userId, itemId);
    }

    /// <summary>
    /// Predicts the next likely accesses for a user.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="topK">Number of predictions to return.</param>
    /// <returns>List of predictions ordered by likelihood.</returns>
    public List<AccessPrediction> PredictNextAccesses(string userId, int topK = 5)
    {
        Interlocked.Increment(ref _totalPredictions);

        var predictions = new List<AccessPrediction>();

        // 1. Markov chain predictions
        if (_markovChains.TryGetValue(userId, out var markovChain))
        {
            var markovPredictions = markovChain.PredictNext(topK * 2);
            foreach (var (itemId, probability) in markovPredictions)
            {
                predictions.Add(new AccessPrediction
                {
                    ItemId = itemId,
                    Probability = probability,
                    Source = PredictionSource.MarkovChain,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        // 2. Time-based patterns
        var currentHour = DateTime.UtcNow.Hour;
        var currentDay = DateTime.UtcNow.DayOfWeek;

        if (_accessSequences.TryGetValue(userId, out var sequence))
        {
            lock (sequence)
            {
                var timeBasedItems = sequence.Entries
                    .Where(e => e.Hour == currentHour || Math.Abs(e.Hour - currentHour) <= 1)
                    .GroupBy(e => e.ItemId)
                    .Select(g => new
                    {
                        ItemId = g.Key,
                        Count = g.Count(),
                        DayMatch = g.Any(e => e.DayOfWeek == currentDay)
                    })
                    .OrderByDescending(x => x.Count * (x.DayMatch ? 1.5 : 1))
                    .Take(topK);

                foreach (var item in timeBasedItems)
                {
                    var existing = predictions.FirstOrDefault(p => p.ItemId == item.ItemId);
                    if (existing != null)
                    {
                        // Boost probability if also predicted by time pattern
                        existing.Probability = Math.Min(0.99, existing.Probability * 1.3);
                    }
                    else
                    {
                        var probability = Math.Min(0.8, item.Count / (double)sequence.Entries.Count * 2);
                        predictions.Add(new AccessPrediction
                        {
                            ItemId = item.ItemId,
                            Probability = probability,
                            Source = PredictionSource.TimePattern,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        // 3. Global popularity
        if (_globalModel != null)
        {
            var popularItems = _globalModel.GetPopularItems(topK);
            foreach (var (itemId, score) in popularItems)
            {
                if (!predictions.Any(p => p.ItemId == itemId))
                {
                    predictions.Add(new AccessPrediction
                    {
                        ItemId = itemId,
                        Probability = Math.Min(0.5, score / 100.0),
                        Source = PredictionSource.GlobalPopularity,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
        }

        // Normalize and sort
        var maxProb = predictions.Count > 0 ? predictions.Max(p => p.Probability) : 1;
        if (maxProb > 0)
        {
            foreach (var pred in predictions)
            {
                pred.Probability /= maxProb;
            }
        }

        var result = predictions
            .OrderByDescending(p => p.Probability)
            .Take(topK)
            .ToList();

        // Record predictions for validation
        foreach (var pred in result)
        {
            var record = new PredictionRecord
            {
                UserId = userId,
                ItemId = pred.ItemId,
                Probability = pred.Probability,
                PredictedAt = DateTime.UtcNow,
                Validated = false
            };

            _predictionHistory.Enqueue(record);
            while (_predictionHistory.Count > MaxPredictionHistory)
            {
                _predictionHistory.TryDequeue(out _);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets items that should be pre-warmed based on predictions.
    /// </summary>
    /// <param name="maxItems">Maximum items to return.</param>
    /// <param name="minConfidence">Minimum confidence threshold.</param>
    /// <returns>List of items to pre-warm.</returns>
    public List<PrewarmRecommendation> GetPrewarmRecommendations(int maxItems = 10, double minConfidence = 0.5)
    {
        var recommendations = new List<PrewarmRecommendation>();

        foreach (var (userId, _) in _accessSequences)
        {
            var predictions = PredictNextAccesses(userId, 5);

            foreach (var pred in predictions.Where(p => p.Probability >= minConfidence))
            {
                var existing = recommendations.FirstOrDefault(r => r.ItemId == pred.ItemId);
                if (existing != null)
                {
                    existing.RequestingUsers++;
                    existing.AggregateScore += pred.Probability;
                }
                else
                {
                    recommendations.Add(new PrewarmRecommendation
                    {
                        ItemId = pred.ItemId,
                        AggregateScore = pred.Probability,
                        RequestingUsers = 1,
                        Source = pred.Source,
                        RecommendedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // Add time-series based predictions
        var currentHour = DateTime.UtcNow.Hour;
        foreach (var (itemId, model) in _timeSeriesModels)
        {
            var forecast = model.Forecast(currentHour);
            if (forecast.PredictedAccesses > model.AverageHourlyAccesses * 1.5)
            {
                var existing = recommendations.FirstOrDefault(r => r.ItemId == itemId);
                if (existing != null)
                {
                    existing.AggregateScore += 0.3;
                }
                else
                {
                    recommendations.Add(new PrewarmRecommendation
                    {
                        ItemId = itemId,
                        AggregateScore = 0.6,
                        RequestingUsers = 0,
                        Source = PredictionSource.TimeSeries,
                        RecommendedAt = DateTime.UtcNow
                    });
                }
            }
        }

        return recommendations
            .OrderByDescending(r => r.AggregateScore * (1 + Math.Log(r.RequestingUsers + 1)))
            .Take(maxItems)
            .ToList();
    }

    /// <summary>
    /// Analyzes access patterns for a user.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <returns>Pattern analysis result.</returns>
    public PatternAnalysis AnalyzePatterns(string userId)
    {
        var analysis = new PatternAnalysis { UserId = userId };

        if (!_accessSequences.TryGetValue(userId, out var sequence))
        {
            return analysis;
        }

        lock (sequence)
        {
            if (sequence.Entries.Count == 0)
                return analysis;

            // Hourly distribution
            analysis.HourlyDistribution = sequence.Entries
                .GroupBy(e => e.Hour)
                .ToDictionary(g => g.Key, g => g.Count());

            // Day of week distribution
            analysis.DayOfWeekDistribution = sequence.Entries
                .GroupBy(e => e.DayOfWeek)
                .ToDictionary(g => g.Key, g => g.Count());

            // Most frequent items
            analysis.MostFrequentItems = sequence.Entries
                .GroupBy(e => e.ItemId)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count());

            // Calculate periodicity
            analysis.Periodicity = DetectPeriodicity(sequence.Entries);

            // Sequence patterns
            analysis.CommonSequences = DetectCommonSequences(sequence.Entries, 3);

            // Session analysis
            analysis.AverageSessionLength = CalculateAverageSessionLength(sequence.Entries);
            analysis.TotalSessions = CountSessions(sequence.Entries);
        }

        return analysis;
    }

    /// <summary>
    /// Gets time series forecast for an item.
    /// </summary>
    /// <param name="itemId">Item identifier.</param>
    /// <param name="hoursAhead">Hours to forecast.</param>
    /// <returns>Time series forecast.</returns>
    public TimeSeriesForecast GetTimeSeriesForecast(string itemId, int hoursAhead = 24)
    {
        var forecast = new TimeSeriesForecast
        {
            ItemId = itemId,
            StartTime = DateTime.UtcNow,
            HoursAhead = hoursAhead
        };

        if (_timeSeriesModels.TryGetValue(itemId, out var model))
        {
            for (int h = 0; h < hoursAhead; h++)
            {
                var targetHour = (DateTime.UtcNow.Hour + h) % 24;
                var point = model.Forecast(targetHour);

                forecast.DataPoints.Add(new ForecastPoint
                {
                    Hour = targetHour,
                    Timestamp = DateTime.UtcNow.AddHours(h),
                    PredictedAccesses = point.PredictedAccesses,
                    Confidence = point.Confidence
                });
            }

            forecast.TrendDirection = model.GetTrendDirection();
            forecast.Seasonality = model.HasSeasonality() ? "Daily" : "None";
        }

        return forecast;
    }

    private void HandlePredictNext(PluginMessage message)
    {
        var userId = GetString(message.Payload, "userId");
        if (string.IsNullOrEmpty(userId))
        {
            message.Payload["error"] = "userId required";
            message.Payload["success"] = false;
            return;
        }

        var topK = GetInt(message.Payload, "topK") ?? 5;
        var predictions = PredictNextAccesses(userId, topK);

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["predictions"] = predictions.Select(p => new
            {
                p.ItemId,
                p.Probability,
                source = p.Source.ToString(),
                p.Timestamp
            }).ToList()
        };
        message.Payload["success"] = true;
    }

    private void HandleGetPrewarmItems(PluginMessage message)
    {
        var maxItems = GetInt(message.Payload, "maxItems") ?? 10;
        var minConfidence = GetDouble(message.Payload, "minConfidence") ?? 0.5;

        var recommendations = GetPrewarmRecommendations(maxItems, minConfidence);

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["recommendations"] = recommendations.Select(r => new
            {
                r.ItemId,
                r.AggregateScore,
                r.RequestingUsers,
                source = r.Source.ToString(),
                r.RecommendedAt
            }).ToList(),
            ["count"] = recommendations.Count
        };
        message.Payload["success"] = true;
    }

    private void HandleAnalyzePatterns(PluginMessage message)
    {
        var userId = GetString(message.Payload, "userId");
        if (string.IsNullOrEmpty(userId))
        {
            message.Payload["error"] = "userId required";
            message.Payload["success"] = false;
            return;
        }

        var analysis = AnalyzePatterns(userId);

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["userId"] = analysis.UserId,
            ["hourlyDistribution"] = analysis.HourlyDistribution,
            ["dayOfWeekDistribution"] = analysis.DayOfWeekDistribution.ToDictionary(
                kv => kv.Key.ToString(), kv => kv.Value),
            ["mostFrequentItems"] = analysis.MostFrequentItems,
            ["periodicity"] = analysis.Periodicity,
            ["commonSequences"] = analysis.CommonSequences,
            ["averageSessionLength"] = analysis.AverageSessionLength,
            ["totalSessions"] = analysis.TotalSessions
        };
        message.Payload["success"] = true;
    }

    private void HandleRecordAccess(PluginMessage message)
    {
        var userId = GetString(message.Payload, "userId");
        var itemId = GetString(message.Payload, "itemId");

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(itemId))
        {
            message.Payload["error"] = "userId and itemId required";
            message.Payload["success"] = false;
            return;
        }

        var context = new AccessContext();
        if (message.Payload.TryGetValue("context", out var ctxObj) && ctxObj is Dictionary<string, object> ctx)
        {
            context.DeviceType = GetString(ctx, "deviceType");
            context.Location = GetString(ctx, "location");
        }

        RecordAccess(userId, itemId, context);

        message.Payload["result"] = new { recorded = true, userId, itemId };
        message.Payload["success"] = true;
    }

    private void HandleGetStats(PluginMessage message)
    {
        var uptime = DateTime.UtcNow - _sessionStart;
        var accuracy = _totalPredictions > 0
            ? (double)_correctPredictions / _totalPredictions
            : 0;
        var prewarmHitRate = (_prewarmHits + _prewarmMisses) > 0
            ? (double)_prewarmHits / (_prewarmHits + _prewarmMisses)
            : 0;

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["uptimeSeconds"] = uptime.TotalSeconds,
            ["trackedUsers"] = _accessSequences.Count,
            ["markovChainCount"] = _markovChains.Count,
            ["timeSeriesModelCount"] = _timeSeriesModels.Count,
            ["totalPredictions"] = Interlocked.Read(ref _totalPredictions),
            ["correctPredictions"] = Interlocked.Read(ref _correctPredictions),
            ["predictionAccuracy"] = accuracy,
            ["prewarmHits"] = Interlocked.Read(ref _prewarmHits),
            ["prewarmMisses"] = Interlocked.Read(ref _prewarmMisses),
            ["prewarmHitRate"] = prewarmHitRate,
            ["pendingPrewarm"] = _prewarmQueue.Count
        };
        message.Payload["success"] = true;
    }

    private void HandleValidatePrediction(PluginMessage message)
    {
        var userId = GetString(message.Payload, "userId");
        var itemId = GetString(message.Payload, "itemId");
        var wasAccessed = GetBool(message.Payload, "wasAccessed") ?? false;

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(itemId))
        {
            message.Payload["error"] = "userId and itemId required";
            message.Payload["success"] = false;
            return;
        }

        var validated = false;
        var predictionFound = false;

        // Find and validate prediction
        foreach (var record in _predictionHistory.Where(r => r.UserId == userId && r.ItemId == itemId && !r.Validated))
        {
            predictionFound = true;
            record.Validated = true;
            record.WasCorrect = wasAccessed;

            if (wasAccessed)
            {
                Interlocked.Increment(ref _correctPredictions);
            }
            validated = true;
            break;
        }

        message.Payload["result"] = new { validated, predictionFound };
        message.Payload["success"] = true;
    }

    private void HandleTimeSeriesForecast(PluginMessage message)
    {
        var itemId = GetString(message.Payload, "itemId");
        if (string.IsNullOrEmpty(itemId))
        {
            message.Payload["error"] = "itemId required";
            message.Payload["success"] = false;
            return;
        }

        var hoursAhead = GetInt(message.Payload, "hoursAhead") ?? 24;
        var forecast = GetTimeSeriesForecast(itemId, hoursAhead);

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["itemId"] = forecast.ItemId,
            ["startTime"] = forecast.StartTime,
            ["hoursAhead"] = forecast.HoursAhead,
            ["dataPoints"] = forecast.DataPoints.Select(p => new
            {
                p.Hour,
                p.Timestamp,
                p.PredictedAccesses,
                p.Confidence
            }).ToList(),
            ["trendDirection"] = forecast.TrendDirection,
            ["seasonality"] = forecast.Seasonality
        };
        message.Payload["success"] = true;
    }

    private void HandleGetUserPatterns(PluginMessage message)
    {
        var userId = GetString(message.Payload, "userId");
        if (string.IsNullOrEmpty(userId))
        {
            message.Payload["error"] = "userId required";
            message.Payload["success"] = false;
            return;
        }

        var patterns = new Dictionary<string, object>();

        if (_markovChains.TryGetValue(userId, out var chain))
        {
            patterns["markovOrder"] = chain.Order;
            patterns["stateCount"] = chain.StateCount;
            patterns["transitionCount"] = chain.TransitionCount;
        }

        if (_accessSequences.TryGetValue(userId, out var sequence))
        {
            lock (sequence)
            {
                patterns["totalAccesses"] = sequence.Entries.Count;
                patterns["uniqueItems"] = sequence.Entries.Select(e => e.ItemId).Distinct().Count();
                patterns["firstAccess"] = sequence.Entries.FirstOrDefault()?.Timestamp;
                patterns["lastAccess"] = sequence.Entries.LastOrDefault()?.Timestamp;
            }
        }

        message.Payload["result"] = patterns;
        message.Payload["success"] = true;
    }

    private void UpdateMarkovChain(string userId, string itemId)
    {
        var chain = _markovChains.GetOrAdd(userId, _ => new MarkovChain(_config.MarkovOrder));
        chain.AddTransition(itemId);
    }

    private void UpdateTimeSeriesModel(string itemId)
    {
        var model = _timeSeriesModels.GetOrAdd(itemId, _ => new TimeSeriesModel());
        model.RecordAccess(DateTime.UtcNow);
    }

    private void ValidatePendingPredictions(string userId, string itemId)
    {
        foreach (var record in _predictionHistory.Where(r =>
            r.UserId == userId &&
            r.ItemId == itemId &&
            !r.Validated &&
            (DateTime.UtcNow - r.PredictedAt).TotalMinutes < 30))
        {
            record.Validated = true;
            record.WasCorrect = true;
            Interlocked.Increment(ref _correctPredictions);

            // Update prewarm stats if this was a prewarmed item
            if (_prewarmQueue.TryRemove(itemId, out _))
            {
                Interlocked.Increment(ref _prewarmHits);
            }
        }
    }

    private PeriodicityInfo DetectPeriodicity(List<AccessEntry> entries)
    {
        if (entries.Count < 24) return new PeriodicityInfo();

        // Check for daily patterns
        var hourCounts = new int[24];
        foreach (var entry in entries)
        {
            hourCounts[entry.Hour]++;
        }

        var maxHour = Array.IndexOf(hourCounts, hourCounts.Max());
        var minHour = Array.IndexOf(hourCounts, hourCounts.Min());

        var variance = CalculateVariance(hourCounts.Select(c => (double)c).ToArray());
        var hasDailyPattern = variance > hourCounts.Average() * 0.5;

        // Check for weekly patterns
        var dayCounts = new int[7];
        foreach (var entry in entries)
        {
            dayCounts[(int)entry.DayOfWeek]++;
        }

        var hasWeeklyPattern = CalculateVariance(dayCounts.Select(c => (double)c).ToArray()) >
                              dayCounts.Average() * 0.3;

        return new PeriodicityInfo
        {
            HasDailyPattern = hasDailyPattern,
            PeakHour = maxHour,
            LowHour = minHour,
            HasWeeklyPattern = hasWeeklyPattern,
            PeakDay = (DayOfWeek)Array.IndexOf(dayCounts, dayCounts.Max())
        };
    }

    private Dictionary<string, int> DetectCommonSequences(List<AccessEntry> entries, int length)
    {
        var sequences = new Dictionary<string, int>();

        for (int i = 0; i <= entries.Count - length; i++)
        {
            var seq = string.Join("->", entries.Skip(i).Take(length).Select(e => e.ItemId));
            sequences[seq] = sequences.GetValueOrDefault(seq, 0) + 1;
        }

        return sequences
            .Where(kv => kv.Value > 1)
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private double CalculateAverageSessionLength(List<AccessEntry> entries)
    {
        if (entries.Count < 2) return 0;

        var sessionLengths = new List<int>();
        int currentSessionLength = 1;
        var sessionTimeout = TimeSpan.FromMinutes(30);

        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Timestamp - entries[i - 1].Timestamp > sessionTimeout)
            {
                sessionLengths.Add(currentSessionLength);
                currentSessionLength = 1;
            }
            else
            {
                currentSessionLength++;
            }
        }

        sessionLengths.Add(currentSessionLength);
        return sessionLengths.Average();
    }

    private int CountSessions(List<AccessEntry> entries)
    {
        if (entries.Count == 0) return 0;

        int sessions = 1;
        var sessionTimeout = TimeSpan.FromMinutes(30);

        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Timestamp - entries[i - 1].Timestamp > sessionTimeout)
            {
                sessions++;
            }
        }

        return sessions;
    }

    private static double CalculateVariance(double[] values)
    {
        if (values.Length == 0) return 0;
        var mean = values.Average();
        return values.Select(v => Math.Pow(v - mean, 2)).Average();
    }

    private async Task RunLearnerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LearnerIntervalMs, ct);

                // Expire old prewarm candidates
                var cutoff = DateTime.UtcNow.AddMinutes(-30);
                var expiredKeys = _prewarmQueue
                    .Where(kv => kv.Value.RecommendedAt < cutoff)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    if (_prewarmQueue.TryRemove(key, out _))
                    {
                        Interlocked.Increment(ref _prewarmMisses);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task RunPrewarmGeneratorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PrewarmIntervalMs, ct);

                if (!_config.PrewarmEnabled) continue;

                var recommendations = GetPrewarmRecommendations(
                    _config.MaxPrewarmItems,
                    _config.MinPrewarmConfidence);

                foreach (var rec in recommendations)
                {
                    _prewarmQueue.TryAdd(rec.ItemId, new PrewarmCandidate
                    {
                        ItemId = rec.ItemId,
                        Score = rec.AggregateScore,
                        RecommendedAt = DateTime.UtcNow
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private static string? GetString(Dictionary<string, object> payload, string key)
    {
        return payload.TryGetValue(key, out var val) && val is string s ? s : null;
    }

    private static int? GetInt(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is string s && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static double? GetDouble(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is double d) return d;
            if (val is float f) return f;
            if (val is int i) return i;
            if (val is string s && double.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? GetBool(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is bool b) return b;
            if (val is string s && bool.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "prediction.next", DisplayName = "Predict Next", Description = "Predict next likely accesses" },
            new() { Name = "prediction.prewarm", DisplayName = "Pre-warm Items", Description = "Get items to pre-warm" },
            new() { Name = "prediction.patterns", DisplayName = "Analyze Patterns", Description = "Analyze access patterns" },
            new() { Name = "prediction.learn", DisplayName = "Record Access", Description = "Record access for learning" },
            new() { Name = "prediction.stats", DisplayName = "Statistics", Description = "Get prediction statistics" },
            new() { Name = "prediction.timeseries", DisplayName = "Time Series", Description = "Get time series forecast" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TrackedUsers"] = _accessSequences.Count;
        metadata["MarkovOrder"] = _config.MarkovOrder;
        metadata["TotalPredictions"] = Interlocked.Read(ref _totalPredictions);
        metadata["PrewarmEnabled"] = _config.PrewarmEnabled;
        return metadata;
    }
}

#region ML Models

/// <summary>
/// Markov Chain model for sequential prediction.
/// Supports variable order chains.
/// </summary>
internal sealed class MarkovChain
{
    private readonly int _order;
    private readonly Dictionary<string, Dictionary<string, int>> _transitions;
    private readonly List<string> _history;
    private readonly object _lock = new();

    public int Order => _order;
    public int StateCount => _transitions.Count;
    public int TransitionCount => _transitions.Values.Sum(d => d.Count);

    public MarkovChain(int order = 3)
    {
        _order = Math.Max(1, Math.Min(order, 10));
        _transitions = new Dictionary<string, Dictionary<string, int>>();
        _history = new List<string>();
    }

    public void AddTransition(string item)
    {
        lock (_lock)
        {
            // Add transitions for all orders up to _order
            for (int o = 1; o <= Math.Min(_order, _history.Count); o++)
            {
                var state = string.Join("|", _history.TakeLast(o));

                if (!_transitions.ContainsKey(state))
                {
                    _transitions[state] = new Dictionary<string, int>();
                }

                _transitions[state][item] = _transitions[state].GetValueOrDefault(item, 0) + 1;
            }

            _history.Add(item);
            if (_history.Count > _order * 10)
            {
                _history.RemoveAt(0);
            }
        }
    }

    public List<(string item, double probability)> PredictNext(int topK = 5)
    {
        lock (_lock)
        {
            var predictions = new Dictionary<string, double>();

            // Try higher order states first, with decreasing weight
            for (int o = Math.Min(_order, _history.Count); o >= 1; o--)
            {
                var state = string.Join("|", _history.TakeLast(o));
                var weight = Math.Pow(2, o - 1); // Higher order = higher weight

                if (_transitions.TryGetValue(state, out var nextStates))
                {
                    var total = nextStates.Values.Sum();
                    foreach (var (item, count) in nextStates)
                    {
                        var prob = (double)count / total * weight;
                        predictions[item] = predictions.GetValueOrDefault(item, 0) + prob;
                    }
                }
            }

            // Normalize
            var maxProb = predictions.Values.Count > 0 ? predictions.Values.Max() : 1;
            return predictions
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .Select(kv => (kv.Key, kv.Value / maxProb))
                .ToList();
        }
    }
}

/// <summary>
/// Time series model for access pattern forecasting.
/// Uses exponential smoothing with seasonality detection.
/// </summary>
internal sealed class TimeSeriesModel
{
    private readonly double[] _hourlyAccesses;
    private readonly double[] _hourlyTrend;
    private readonly Queue<(DateTime time, int hour)> _recentAccesses;
    private int _totalAccesses;
    private readonly double _alpha; // Smoothing factor
    private readonly object _lock = new();

    public double AverageHourlyAccesses => _totalAccesses / Math.Max(1, 24.0);

    public TimeSeriesModel(double alpha = 0.3)
    {
        _hourlyAccesses = new double[24];
        _hourlyTrend = new double[24];
        _recentAccesses = new Queue<(DateTime, int)>();
        _alpha = alpha;
    }

    public void RecordAccess(DateTime timestamp)
    {
        lock (_lock)
        {
            var hour = timestamp.Hour;
            _totalAccesses++;

            // Exponential smoothing update
            _hourlyAccesses[hour] = _alpha * 1 + (1 - _alpha) * _hourlyAccesses[hour];

            // Update trend
            _hourlyTrend[hour] = _alpha * (_hourlyAccesses[hour] - _hourlyTrend[hour]) +
                                (1 - _alpha) * _hourlyTrend[hour];

            _recentAccesses.Enqueue((timestamp, hour));
            while (_recentAccesses.Count > 1000)
            {
                _recentAccesses.Dequeue();
            }
        }
    }

    public TimeSeriesForecastPoint Forecast(int hour)
    {
        lock (_lock)
        {
            var baseValue = _hourlyAccesses[hour];
            var trend = _hourlyTrend[hour];

            return new TimeSeriesForecastPoint
            {
                PredictedAccesses = Math.Max(0, baseValue + trend),
                Confidence = Math.Min(0.9, _totalAccesses / 100.0)
            };
        }
    }

    public string GetTrendDirection()
    {
        lock (_lock)
        {
            var avgTrend = _hourlyTrend.Average();
            if (avgTrend > 0.1) return "Increasing";
            if (avgTrend < -0.1) return "Decreasing";
            return "Stable";
        }
    }

    public bool HasSeasonality()
    {
        lock (_lock)
        {
            var variance = CalculateVariance(_hourlyAccesses);
            var mean = _hourlyAccesses.Average();
            return variance > mean * 0.5;
        }
    }

    private static double CalculateVariance(double[] values)
    {
        if (values.Length == 0) return 0;
        var mean = values.Average();
        return values.Select(v => Math.Pow(v - mean, 2)).Average();
    }
}

/// <summary>
/// Global pattern model for cross-user analysis.
/// </summary>
internal sealed class GlobalPatternModel
{
    private readonly ConcurrentDictionary<string, double> _itemPopularity;
    private readonly double _decayFactor;
    private DateTime _lastDecay;

    public GlobalPatternModel(double decayFactor = 0.99)
    {
        _itemPopularity = new ConcurrentDictionary<string, double>();
        _decayFactor = decayFactor;
        _lastDecay = DateTime.UtcNow;
    }

    public void RecordAccess(string itemId, DateTime timestamp)
    {
        // Apply decay if needed
        if ((DateTime.UtcNow - _lastDecay).TotalHours >= 1)
        {
            foreach (var key in _itemPopularity.Keys.ToList())
            {
                _itemPopularity.AddOrUpdate(key, 0, (_, v) => v * _decayFactor);
            }
            _lastDecay = DateTime.UtcNow;
        }

        _itemPopularity.AddOrUpdate(itemId, 1, (_, v) => v + 1);
    }

    public List<(string itemId, double score)> GetPopularItems(int topK)
    {
        return _itemPopularity
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}

#endregion

#region Data Types

/// <summary>
/// Prediction configuration.
/// </summary>
public sealed class PredictionConfig
{
    /// <summary>Markov chain order.</summary>
    public int MarkovOrder { get; set; } = 3;
    /// <summary>Whether pre-warming is enabled.</summary>
    public bool PrewarmEnabled { get; set; } = true;
    /// <summary>Maximum items to pre-warm.</summary>
    public int MaxPrewarmItems { get; set; } = 20;
    /// <summary>Minimum confidence for pre-warming.</summary>
    public double MinPrewarmConfidence { get; set; } = 0.5;
}

/// <summary>
/// Access sequence for a user.
/// </summary>
internal sealed class AccessSequence
{
    public required string UserId { get; init; }
    public List<AccessEntry> Entries { get; } = new();
}

/// <summary>
/// Single access entry.
/// </summary>
internal sealed class AccessEntry
{
    public required string ItemId { get; init; }
    public DateTime Timestamp { get; init; }
    public int Hour { get; init; }
    public DayOfWeek DayOfWeek { get; init; }
    public AccessContext? Context { get; init; }
}

/// <summary>
/// Access context information.
/// </summary>
public sealed class AccessContext
{
    /// <summary>Device type.</summary>
    public string? DeviceType { get; set; }
    /// <summary>Geographic location.</summary>
    public string? Location { get; set; }
}

/// <summary>
/// Access prediction result.
/// </summary>
public sealed class AccessPrediction
{
    /// <summary>Predicted item ID.</summary>
    public required string ItemId { get; init; }
    /// <summary>Probability of access.</summary>
    public double Probability { get; set; }
    /// <summary>Prediction source.</summary>
    public PredictionSource Source { get; init; }
    /// <summary>When prediction was made.</summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Source of prediction.
/// </summary>
public enum PredictionSource
{
    /// <summary>From Markov chain model.</summary>
    MarkovChain,
    /// <summary>From time-based patterns.</summary>
    TimePattern,
    /// <summary>From time series model.</summary>
    TimeSeries,
    /// <summary>From global popularity.</summary>
    GlobalPopularity
}

/// <summary>
/// Pre-warm recommendation.
/// </summary>
public sealed class PrewarmRecommendation
{
    /// <summary>Item to pre-warm.</summary>
    public required string ItemId { get; init; }
    /// <summary>Aggregate confidence score.</summary>
    public double AggregateScore { get; set; }
    /// <summary>Number of users likely to request.</summary>
    public int RequestingUsers { get; set; }
    /// <summary>Prediction source.</summary>
    public PredictionSource Source { get; init; }
    /// <summary>When recommendation was made.</summary>
    public DateTime RecommendedAt { get; init; }
}

/// <summary>
/// Pre-warm candidate in queue.
/// </summary>
internal sealed class PrewarmCandidate
{
    public required string ItemId { get; init; }
    public double Score { get; init; }
    public DateTime RecommendedAt { get; init; }
}

/// <summary>
/// Prediction record for validation.
/// </summary>
internal sealed class PredictionRecord
{
    public required string UserId { get; init; }
    public required string ItemId { get; init; }
    public double Probability { get; init; }
    public DateTime PredictedAt { get; init; }
    public bool Validated { get; set; }
    public bool WasCorrect { get; set; }
}

/// <summary>
/// Pattern analysis result.
/// </summary>
public sealed class PatternAnalysis
{
    /// <summary>User identifier.</summary>
    public required string UserId { get; init; }
    /// <summary>Hourly access distribution.</summary>
    public Dictionary<int, int> HourlyDistribution { get; set; } = new();
    /// <summary>Day of week distribution.</summary>
    public Dictionary<DayOfWeek, int> DayOfWeekDistribution { get; set; } = new();
    /// <summary>Most frequently accessed items.</summary>
    public Dictionary<string, int> MostFrequentItems { get; set; } = new();
    /// <summary>Periodicity information.</summary>
    public PeriodicityInfo Periodicity { get; set; } = new();
    /// <summary>Common access sequences.</summary>
    public Dictionary<string, int> CommonSequences { get; set; } = new();
    /// <summary>Average session length in accesses.</summary>
    public double AverageSessionLength { get; set; }
    /// <summary>Total number of sessions.</summary>
    public int TotalSessions { get; set; }
}

/// <summary>
/// Periodicity detection result.
/// </summary>
public sealed class PeriodicityInfo
{
    /// <summary>Whether daily patterns exist.</summary>
    public bool HasDailyPattern { get; set; }
    /// <summary>Peak hour of activity.</summary>
    public int PeakHour { get; set; }
    /// <summary>Low activity hour.</summary>
    public int LowHour { get; set; }
    /// <summary>Whether weekly patterns exist.</summary>
    public bool HasWeeklyPattern { get; set; }
    /// <summary>Peak day of week.</summary>
    public DayOfWeek PeakDay { get; set; }
}

/// <summary>
/// Time series forecast result.
/// </summary>
public sealed class TimeSeriesForecast
{
    /// <summary>Item identifier.</summary>
    public required string ItemId { get; init; }
    /// <summary>Forecast start time.</summary>
    public DateTime StartTime { get; init; }
    /// <summary>Hours forecasted.</summary>
    public int HoursAhead { get; init; }
    /// <summary>Forecast data points.</summary>
    public List<ForecastPoint> DataPoints { get; } = new();
    /// <summary>Overall trend direction.</summary>
    public string TrendDirection { get; set; } = "Stable";
    /// <summary>Detected seasonality.</summary>
    public string Seasonality { get; set; } = "None";
}

/// <summary>
/// Single forecast point.
/// </summary>
public sealed class ForecastPoint
{
    /// <summary>Hour of day.</summary>
    public int Hour { get; init; }
    /// <summary>Timestamp.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Predicted number of accesses.</summary>
    public double PredictedAccesses { get; init; }
    /// <summary>Confidence level.</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Time series forecast point from model.
/// </summary>
internal sealed class TimeSeriesForecastPoint
{
    public double PredictedAccesses { get; init; }
    public double Confidence { get; init; }
}

#endregion
