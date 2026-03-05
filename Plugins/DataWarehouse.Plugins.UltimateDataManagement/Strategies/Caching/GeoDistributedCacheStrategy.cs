using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Geographic region for cache distribution.
/// </summary>
public sealed class CacheRegion
{
    /// <summary>
    /// Region identifier.
    /// </summary>
    public required string RegionId { get; init; }

    /// <summary>
    /// Region display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Region endpoint URL.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Latitude for geo-proximity calculation.
    /// </summary>
    public double Latitude { get; init; }

    /// <summary>
    /// Longitude for geo-proximity calculation.
    /// </summary>
    public double Longitude { get; init; }

    /// <summary>
    /// Whether this region is currently healthy.
    /// </summary>
    public bool IsHealthy { get; set; } = true;

    /// <summary>
    /// Last health check time.
    /// </summary>
    public DateTime LastHealthCheck { get; set; }

    /// <summary>
    /// Average latency to this region in milliseconds.
    /// </summary>
    public double AverageLatencyMs { get; set; }

    /// <summary>
    /// Whether this is the local region.
    /// </summary>
    public bool IsLocal { get; init; }
}

/// <summary>
/// Cache invalidation event for cross-region propagation.
/// </summary>
public sealed class InvalidationEvent
{
    /// <summary>
    /// Event identifier.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Keys to invalidate.
    /// </summary>
    public required string[] Keys { get; init; }

    /// <summary>
    /// Tags to invalidate.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Origin region.
    /// </summary>
    public required string OriginRegion { get; init; }

    /// <summary>
    /// When the event was created.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for geo-distributed cache.
/// </summary>
public sealed class GeoDistributedCacheConfig
{
    /// <summary>
    /// Local region identifier.
    /// </summary>
    public required string LocalRegionId { get; init; }

    /// <summary>
    /// Maximum local cache size in bytes.
    /// </summary>
    public long MaxLocalCacheSize { get; init; } = 256 * 1024 * 1024;

    /// <summary>
    /// Default TTL for cached entries.
    /// </summary>
    public TimeSpan DefaultTTL { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether to replicate writes to all regions.
    /// </summary>
    public bool ReplicateWrites { get; init; } = true;

    /// <summary>
    /// Whether to prefer local cache over remote.
    /// </summary>
    public bool PreferLocal { get; init; } = true;

    /// <summary>
    /// Health check interval.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Invalidation propagation delay.
    /// </summary>
    public TimeSpan InvalidationDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Geo-distributed cache strategy providing edge caching across regions.
/// </summary>
/// <remarks>
/// Features:
/// - Multi-region cache distribution
/// - Cross-region cache invalidation
/// - Geo-proximity routing
/// - Local cache with remote fallback
/// - Health monitoring per region
/// </remarks>
public sealed class GeoDistributedCacheStrategy : CachingStrategyBase
{
    private readonly BoundedDictionary<string, CacheEntry> _localCache = new BoundedDictionary<string, CacheEntry>(1000);
    private readonly BoundedDictionary<string, CacheRegion> _regions = new BoundedDictionary<string, CacheRegion>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _tagIndex = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly ConcurrentQueue<InvalidationEvent> _invalidationQueue = new();
    private readonly object _tagLock = new();

    private readonly GeoDistributedCacheConfig _config;
    private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private long _currentSize;
    private Timer? _healthCheckTimer;
    private Timer? _invalidationTimer;

    private sealed class CacheEntry
    {
        private long _lastAccessTicks;

        public byte[] Value { get; }
        public DateTime? ExpiresAt { get; set; }

        public DateTime LastAccess
        {
            get => new DateTime(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastAccessTicks, value.Ticks);
        }

        public CachePriority Priority { get; }
        public string[]? Tags { get; }
        public string OriginRegion { get; init; }
        public long Version { get; set; }

        public CacheEntry(byte[] value, CacheOptions options, string originRegion)
        {
            Value = value;
            Priority = options.Priority;
            Tags = options.Tags;
            _lastAccessTicks = DateTime.UtcNow.Ticks;
            OriginRegion = originRegion;
            Version = DateTime.UtcNow.Ticks;

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
    /// Initializes a new GeoDistributedCacheStrategy.
    /// </summary>
    public GeoDistributedCacheStrategy(GeoDistributedCacheConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        // Timers are started in InitializeCoreAsync to avoid accessing uninitialized state.
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _healthCheckTimer = new Timer(PerformHealthChecks, null, TimeSpan.FromSeconds(10), _config.HealthCheckInterval);
        _invalidationTimer = new Timer(ProcessInvalidations, null, _config.InvalidationDelay, _config.InvalidationDelay);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.geo-distributed";

    /// <inheritdoc/>
    public override string DisplayName => "Geo-Distributed Cache";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Geo-distributed cache strategy providing edge caching across multiple regions. " +
        "Features cross-region invalidation, geo-proximity routing, and automatic health monitoring.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "geo-distributed", "edge", "multi-region", "cdn"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => Interlocked.Read(ref _currentSize);

    /// <inheritdoc/>
    public override long GetEntryCount() => _localCache.Count;

    /// <summary>
    /// Adds a cache region.
    /// </summary>
    public void AddRegion(CacheRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(region.RegionId);

        _regions[region.RegionId] = region;
    }

    /// <summary>
    /// Gets all registered regions.
    /// </summary>
    public IReadOnlyList<CacheRegion> GetRegions() => _regions.Values.ToList();

    /// <summary>
    /// Gets healthy regions.
    /// </summary>
    public IReadOnlyList<CacheRegion> GetHealthyRegions() =>
        _regions.Values.Where(r => r.IsHealthy).ToList();

    /// <summary>
    /// Gets the nearest region based on coordinates.
    /// </summary>
    /// <param name="latitude">Client latitude.</param>
    /// <param name="longitude">Client longitude.</param>
    /// <returns>Nearest healthy region.</returns>
    public CacheRegion? GetNearestRegion(double latitude, double longitude)
    {
        return _regions.Values
            .Where(r => r.IsHealthy)
            .OrderBy(r => CalculateDistance(latitude, longitude, r.Latitude, r.Longitude))
            .FirstOrDefault();
    }

    /// <summary>
    /// Broadcasts an invalidation event to all regions.
    /// </summary>
    /// <param name="keys">Keys to invalidate.</param>
    /// <param name="tags">Tags to invalidate.</param>
    public void BroadcastInvalidation(string[] keys, string[]? tags = null)
    {
        var evt = new InvalidationEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Keys = keys,
            Tags = tags,
            OriginRegion = _config.LocalRegionId
        };

        _invalidationQueue.Enqueue(evt);
    }

    /// <inheritdoc/>
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        // Try local cache first
        if (_localCache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                RemoveEntry(key, entry);
                return CacheResult<byte[]>.Miss();
            }

            entry.LastAccess = DateTime.UtcNow;
            return CacheResult<byte[]>.Hit(entry.Value, entry.GetTimeToExpiration());
        }

        // Try remote regions ordered by proximity (healthy regions, lowest latency first)
        var remoteRegions = _regions.Values
            .Where(r => !r.IsLocal && r.IsHealthy && !string.IsNullOrEmpty(r.Endpoint))
            .OrderBy(r => r.AverageLatencyMs)
            .ToList();
        foreach (var region in remoteRegions)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{region.Endpoint.TrimEnd('/')}/cache/{Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        // Populate local cache from remote hit
                        var remoteEntry = new CacheEntry(bytes, new CacheOptions(), region.RegionId);
                        _localCache[key] = remoteEntry;
                        Interlocked.Add(ref _currentSize, bytes.Length);
                        return CacheResult<byte[]>.Hit(bytes, remoteEntry.GetTimeToExpiration());
                    }
                }
            }
            catch { /* Best-effort remote fetch; continue to next region */ }
        }
        return CacheResult<byte[]>.Miss();
    }

    /// <inheritdoc/>
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        EnsureCapacity(value.Length);

        var entry = new CacheEntry(value, options, _config.LocalRegionId);

        if (_localCache.TryGetValue(key, out var existing))
        {
            Interlocked.Add(ref _currentSize, -existing.Value.Length);
        }

        _localCache[key] = entry;
        Interlocked.Add(ref _currentSize, value.Length);

        // Update tag index
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

        // Replicate to other regions if enabled — fire-and-forget with best-effort delivery
        if (_config.ReplicateWrites)
        {
            var payload = JsonSerializer.Serialize(new { key, value = Convert.ToBase64String(value), ttlSeconds = (long)(options.TTL?.TotalSeconds ?? 0) });
            foreach (var region in _regions.Values.Where(r => !r.IsLocal && r.IsHealthy && !string.IsNullOrEmpty(r.Endpoint)))
            {
                var regionEndpoint = region.Endpoint;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        await _httpClient.PutAsync($"{regionEndpoint.TrimEnd('/')}/cache/{Uri.EscapeDataString(key)}", content).ConfigureAwait(false);
                    }
                    catch { /* Best-effort replication; region health check will mark it unhealthy */ }
                });
            }
        }

    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        var removed = false;

        if (_localCache.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentSize, -entry.Value.Length);
            RemoveFromTagIndex(key, entry.Tags);
            removed = true;
        }

        // Broadcast invalidation to other regions
        BroadcastInvalidation(new[] { key });

        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        if (_localCache.TryGetValue(key, out var entry))
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
        var keysToRemove = new HashSet<string>();

        lock (_tagLock)
        {
            foreach (var tag in tags)
            {
                if (_tagIndex.TryRemove(tag, out var keys))
                {
                    foreach (var key in keys)
                    {
                        keysToRemove.Add(key);
                    }
                }
            }
        }

        foreach (var key in keysToRemove)
        {
            if (_localCache.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentSize, -entry.Value.Length);
            }
        }

        // Broadcast invalidation
        BroadcastInvalidation(keysToRemove.ToArray(), tags);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task ClearCoreAsync(CancellationToken ct)
    {
        var allKeys = _localCache.Keys.ToArray();

        _localCache.Clear();
        _tagIndex.Clear();
        Interlocked.Exchange(ref _currentSize, 0);

        // Broadcast invalidation
        BroadcastInvalidation(allKeys);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _healthCheckTimer?.Dispose();
        _invalidationTimer?.Dispose();
        _httpClient.Dispose();
        _localCache.Clear();
        _tagIndex.Clear();
        _regions.Clear();
        return Task.CompletedTask;
    }

    private void RemoveEntry(string key, CacheEntry entry)
    {
        if (_localCache.TryRemove(key, out _))
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
        if (currentSize + requiredSize <= _config.MaxLocalCacheSize)
            return;

        var toEvict = _localCache
            .Where(kvp => kvp.Value.Priority != CachePriority.NeverRemove)
            .OrderBy(kvp => kvp.Value.Priority)
            .ThenBy(kvp => kvp.Value.LastAccess)
            .Take(Math.Max(1, _localCache.Count / 10))
            .ToList();

        foreach (var kvp in toEvict)
        {
            RemoveEntry(kvp.Key, kvp.Value);

            if (Interlocked.Read(ref _currentSize) + requiredSize <= _config.MaxLocalCacheSize)
                break;
        }
    }

    private void PerformHealthChecks(object? state)
    {
        foreach (var region in _regions.Values)
        {
            if (string.IsNullOrEmpty(region.Endpoint))
            {
                region.LastHealthCheck = DateTime.UtcNow;
                continue;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var task = _httpClient.GetAsync($"{region.Endpoint.TrimEnd('/')}/health", cts.Token);
                task.Wait(cts.Token);
                sw.Stop();
                region.LastHealthCheck = DateTime.UtcNow;
                region.IsHealthy = task.IsCompletedSuccessfully && task.Result.IsSuccessStatusCode;
                region.AverageLatencyMs = (region.AverageLatencyMs * 0.8) + (sw.Elapsed.TotalMilliseconds * 0.2);
            }
            catch
            {
                region.IsHealthy = false;
                region.LastHealthCheck = DateTime.UtcNow;
            }
        }
    }

    private void ProcessInvalidations(object? state)
    {
        while (_invalidationQueue.TryDequeue(out var evt))
        {
            if (evt.OriginRegion == _config.LocalRegionId)
            {
                // Propagate to all healthy remote regions
                var payload = JsonSerializer.Serialize(new { eventId = evt.EventId, keys = evt.Keys, tags = evt.Tags, originRegion = evt.OriginRegion });
                foreach (var region in _regions.Values.Where(r => !r.IsLocal && r.IsHealthy && !string.IsNullOrEmpty(r.Endpoint)))
                {
                    var regionEndpoint = region.Endpoint;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                            await _httpClient.PostAsync($"{regionEndpoint.TrimEnd('/')}/cache/invalidate", content).ConfigureAwait(false);
                        }
                        catch { /* Best-effort; unhealthy region will be detected by health check */ }
                    });
                }
            }
            else
            {
                // Apply locally for events from other regions
                foreach (var key in evt.Keys)
                {
                    if (_localCache.TryRemove(key, out var entry))
                    {
                        Interlocked.Add(ref _currentSize, -entry.Value.Length);
                        RemoveFromTagIndex(key, entry.Tags);
                    }
                }

                if (evt.Tags != null)
                {
                    lock (_tagLock)
                    {
                        foreach (var tag in evt.Tags)
                        {
                            if (_tagIndex.TryRemove(tag, out var keys))
                            {
                                foreach (var key in keys)
                                {
                                    if (_localCache.TryRemove(key, out var entry))
                                    {
                                        Interlocked.Add(ref _currentSize, -entry.Value.Length);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        // Haversine formula for great-circle distance
        const double R = 6371; // Earth's radius in km

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
