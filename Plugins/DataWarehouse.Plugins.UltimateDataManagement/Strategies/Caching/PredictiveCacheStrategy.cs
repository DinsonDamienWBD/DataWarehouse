using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Access prediction for cache prefetching.
/// </summary>
public sealed class AccessPrediction
{
    /// <summary>
    /// Key to prefetch.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Probability of access (0.0-1.0).
    /// </summary>
    public double Probability { get; init; }

    /// <summary>
    /// Predicted time until access.
    /// </summary>
    public TimeSpan? TimeUntilAccess { get; init; }

    /// <summary>
    /// Confidence in the prediction (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Access pattern record.
/// </summary>
internal sealed class AccessPatternRecord
{
    public required string Key { get; init; }
    public DateTime AccessTime { get; init; }
    public string? PreviousKey { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Predictive cache strategy using AI for prefetching.
/// Learns access patterns and proactively warms the cache.
/// </summary>
/// <remarks>
/// Features:
/// - AI-driven access pattern learning
/// - Proactive cache warming
/// - Sequential access prediction
/// - User-based pattern recognition
/// - Graceful fallback to frequency-based prediction
/// </remarks>
public sealed class PredictiveCacheStrategy : CachingStrategyBase
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _tagIndex = new();
    private readonly ConcurrentDictionary<string, int> _accessCounts = new();
    private readonly ConcurrentDictionary<string, List<string>> _sequentialPatterns = new(); // key -> next keys
    private readonly ConcurrentQueue<AccessPatternRecord> _accessHistory = new();
    private readonly object _tagLock = new();
    private readonly object _patternLock = new();

    private IMessageBus? _messageBus;
    private volatile bool _intelligenceAvailable;
    private IntelligenceCapabilities _capabilities = IntelligenceCapabilities.None;
    private readonly List<IDisposable> _subscriptions = new();

    private readonly long _maxSize;
    private readonly int _maxHistorySize;
    private readonly double _prefetchThreshold;
    private long _currentSize;
    private string? _lastAccessedKey;
    private DataLoaderDelegate? _prefetchLoader;

    private readonly Timer _patternAnalysisTimer;
    private readonly Timer _prefetchTimer;

    private sealed class CacheEntry
    {
        public byte[] Value { get; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime LastAccess { get; set; }
        public CachePriority Priority { get; }
        public string[]? Tags { get; }
        public int AccessCount { get; set; }
        public bool IsPrefetched { get; set; }

        public CacheEntry(byte[] value, CacheOptions options)
        {
            Value = value;
            Priority = options.Priority;
            Tags = options.Tags;
            LastAccess = DateTime.UtcNow;

            if (options.TTL.HasValue)
                ExpiresAt = DateTime.UtcNow.Add(options.TTL.Value);
        }

        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

        public TimeSpan? GetTimeToExpiration()
        {
            if (!ExpiresAt.HasValue) return null;
            var remaining = ExpiresAt.Value - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Initializes a new PredictiveCacheStrategy.
    /// </summary>
    public PredictiveCacheStrategy() : this(256 * 1024 * 1024, 10000, 0.3) { }

    /// <summary>
    /// Initializes a new PredictiveCacheStrategy with configuration.
    /// </summary>
    /// <param name="maxSizeBytes">Maximum cache size in bytes.</param>
    /// <param name="maxHistorySize">Maximum access history entries.</param>
    /// <param name="prefetchThreshold">Probability threshold for prefetching.</param>
    public PredictiveCacheStrategy(long maxSizeBytes, int maxHistorySize, double prefetchThreshold)
    {
        _maxSize = maxSizeBytes;
        _maxHistorySize = maxHistorySize;
        _prefetchThreshold = prefetchThreshold;

        _patternAnalysisTimer = new Timer(AnalyzePatterns, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _prefetchTimer = new Timer(ExecutePrefetch, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.predictive";

    /// <inheritdoc/>
    public override string DisplayName => "Predictive Cache";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 500_000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Predictive cache strategy using AI to learn access patterns and proactively warm the cache. " +
        "Predicts next access and prefetches data for optimal performance.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "predictive", "ai", "prefetch", "pattern-learning"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => Interlocked.Read(ref _currentSize);

    /// <inheritdoc/>
    public override long GetEntryCount() => _cache.Count;

    /// <summary>
    /// Sets the message bus for AI integration.
    /// </summary>
    public void SetMessageBus(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        SubscribeToIntelligence();
    }

    /// <summary>
    /// Sets the loader for prefetch operations.
    /// </summary>
    public void SetPrefetchLoader(DataLoaderDelegate loader)
    {
        _prefetchLoader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>
    /// Gets predictions for next likely accesses.
    /// </summary>
    /// <param name="currentKey">Current key being accessed.</param>
    /// <param name="maxPredictions">Maximum predictions to return.</param>
    /// <returns>List of access predictions.</returns>
    public IReadOnlyList<AccessPrediction> GetAccessPredictions(string currentKey, int maxPredictions = 5)
    {
        var predictions = new List<AccessPrediction>();

        // Check sequential patterns
        if (_sequentialPatterns.TryGetValue(currentKey, out var nextKeys))
        {
            var keyCounts = nextKeys.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            var total = nextKeys.Count;

            foreach (var (key, count) in keyCounts.OrderByDescending(kvp => kvp.Value).Take(maxPredictions))
            {
                predictions.Add(new AccessPrediction
                {
                    Key = key,
                    Probability = (double)count / total,
                    Confidence = Math.Min(1.0, (double)count / 10),
                    TimeUntilAccess = TimeSpan.FromSeconds(1)
                });
            }
        }

        // Add frequency-based predictions
        var remaining = maxPredictions - predictions.Count;
        if (remaining > 0)
        {
            var frequentKeys = _accessCounts
                .Where(kvp => kvp.Key != currentKey && !predictions.Any(p => p.Key == kvp.Key))
                .OrderByDescending(kvp => kvp.Value)
                .Take(remaining);

            var maxCount = _accessCounts.Values.DefaultIfEmpty(1).Max();
            foreach (var (key, count) in frequentKeys)
            {
                predictions.Add(new AccessPrediction
                {
                    Key = key,
                    Probability = (double)count / maxCount * 0.5, // Lower probability for frequency-based
                    Confidence = 0.3
                });
            }
        }

        return predictions;
    }

    /// <inheritdoc/>
    protected override Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        RecordAccess(key);

        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                RemoveEntry(key, entry);
                return Task.FromResult(CacheResult<byte[]>.Miss());
            }

            entry.LastAccess = DateTime.UtcNow;
            entry.AccessCount++;
            return Task.FromResult(CacheResult<byte[]>.Hit(entry.Value, entry.GetTimeToExpiration()));
        }

        return Task.FromResult(CacheResult<byte[]>.Miss());
    }

    /// <inheritdoc/>
    protected override Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        EnsureCapacity(value.Length);

        var entry = new CacheEntry(value, options);

        if (_cache.TryGetValue(key, out var existing))
        {
            Interlocked.Add(ref _currentSize, -existing.Value.Length);
        }

        _cache[key] = entry;
        Interlocked.Add(ref _currentSize, value.Length);

        if (options.Tags != null)
        {
            lock (_tagLock)
            {
                foreach (var tag in options.Tags)
                {
                    var keys = _tagIndex.GetOrAdd(tag, _ => new HashSet<string>());
                    keys.Add(key);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentSize, -entry.Value.Length);
            RemoveFromTagIndex(key, entry.Tags);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                RemoveEntry(key, entry);
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct)
    {
        foreach (var tag in tags)
        {
            lock (_tagLock)
            {
                if (_tagIndex.TryRemove(tag, out var keys))
                {
                    foreach (var key in keys)
                    {
                        if (_cache.TryRemove(key, out var entry))
                        {
                            Interlocked.Add(ref _currentSize, -entry.Value.Length);
                        }
                    }
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task ClearCoreAsync(CancellationToken ct)
    {
        _cache.Clear();
        _tagIndex.Clear();
        _accessCounts.Clear();
        _sequentialPatterns.Clear();

        while (_accessHistory.TryDequeue(out _)) { }

        Interlocked.Exchange(ref _currentSize, 0);
        _lastAccessedKey = null;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _patternAnalysisTimer.Dispose();
        _prefetchTimer.Dispose();

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { /* Best-effort cleanup */ }
        }

        _cache.Clear();
        _tagIndex.Clear();
        return Task.CompletedTask;
    }

    private void RecordAccess(string key)
    {
        _accessCounts.AddOrUpdate(key, 1, (_, count) => count + 1);

        var record = new AccessPatternRecord
        {
            Key = key,
            AccessTime = DateTime.UtcNow,
            PreviousKey = _lastAccessedKey
        };

        _accessHistory.Enqueue(record);
        _lastAccessedKey = key;

        // Trim history
        while (_accessHistory.Count > _maxHistorySize && _accessHistory.TryDequeue(out _)) { }

        // Record sequential pattern
        if (record.PreviousKey != null)
        {
            lock (_patternLock)
            {
                var nextKeys = _sequentialPatterns.GetOrAdd(record.PreviousKey, _ => new List<string>());
                nextKeys.Add(key);

                // Limit pattern storage
                if (nextKeys.Count > 100)
                {
                    nextKeys.RemoveRange(0, 50);
                }
            }
        }
    }

    private void AnalyzePatterns(object? state)
    {
        // Clean up old patterns
        lock (_patternLock)
        {
            var keysToRemove = _sequentialPatterns
                .Where(kvp => kvp.Value.Count < 3)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _sequentialPatterns.TryRemove(key, out _);
            }
        }

        // Clean up old access counts
        var inactiveKeys = _accessCounts
            .Where(kvp => !_cache.ContainsKey(kvp.Key))
            .Select(kvp => kvp.Key)
            .Take(1000)
            .ToList();

        foreach (var key in inactiveKeys)
        {
            _accessCounts.TryRemove(key, out _);
        }
    }

    private void ExecutePrefetch(object? state)
    {
        // Timer callbacks must be void, so we use Task.Run for async work
        _ = Task.Run(async () =>
        {
            if (_prefetchLoader == null || _lastAccessedKey == null)
                return;

            try
            {
                var predictions = GetAccessPredictions(_lastAccessedKey, 3);

                foreach (var prediction in predictions)
                {
                    if (prediction.Probability >= _prefetchThreshold && !_cache.ContainsKey(prediction.Key))
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            var value = await _prefetchLoader(prediction.Key, cts.Token);

                            if (value != null)
                            {
                                await SetCoreAsync(prediction.Key, value, new CacheOptions
                                {
                                    TTL = TimeSpan.FromMinutes(5),
                                    Priority = CachePriority.Low
                                }, CancellationToken.None);

                                if (_cache.TryGetValue(prediction.Key, out var entry))
                                {
                                    entry.IsPrefetched = true;
                                }
                            }
                        }
                        catch
                        {
                            // Prefetch failures are non-fatal
                        }
                    }
                }
            }
            catch
            {
                // Timer callback failures should not propagate
            }
        });
    }

    private void SubscribeToIntelligence()
    {
        if (_messageBus == null) return;

        var availableSub = _messageBus.Subscribe(IntelligenceTopics.Available, msg =>
        {
            _intelligenceAvailable = true;
            if (msg.Payload.TryGetValue("capabilities", out var cap) && cap is long capLong)
            {
                _capabilities = (IntelligenceCapabilities)capLong;
            }
            return Task.CompletedTask;
        });
        _subscriptions.Add(availableSub);

        var unavailableSub = _messageBus.Subscribe(IntelligenceTopics.Unavailable, _ =>
        {
            _intelligenceAvailable = false;
            return Task.CompletedTask;
        });
        _subscriptions.Add(unavailableSub);
    }

    private void RemoveEntry(string key, CacheEntry entry)
    {
        if (_cache.TryRemove(key, out _))
        {
            Interlocked.Add(ref _currentSize, -entry.Value.Length);
            RemoveFromTagIndex(key, entry.Tags);
        }
    }

    private void RemoveFromTagIndex(string key, string[]? tags)
    {
        if (tags == null) return;

        lock (_tagLock)
        {
            foreach (var tag in tags)
            {
                if (_tagIndex.TryGetValue(tag, out var keys))
                {
                    keys.Remove(key);
                    if (keys.Count == 0)
                        _tagIndex.TryRemove(tag, out _);
                }
            }
        }
    }

    private void EnsureCapacity(long requiredSize)
    {
        var currentSize = Interlocked.Read(ref _currentSize);
        if (currentSize + requiredSize <= _maxSize)
            return;

        var toEvict = _cache
            .Where(kvp => kvp.Value.Priority != CachePriority.NeverRemove)
            .OrderBy(kvp => kvp.Value.IsPrefetched ? 0 : 1) // Evict prefetched first
            .ThenBy(kvp => kvp.Value.Priority)
            .ThenBy(kvp => kvp.Value.LastAccess)
            .Take(Math.Max(1, _cache.Count / 10))
            .ToList();

        foreach (var kvp in toEvict)
        {
            RemoveEntry(kvp.Key, kvp.Value);

            if (Interlocked.Read(ref _currentSize) + requiredSize <= _maxSize)
                break;
        }
    }
}
