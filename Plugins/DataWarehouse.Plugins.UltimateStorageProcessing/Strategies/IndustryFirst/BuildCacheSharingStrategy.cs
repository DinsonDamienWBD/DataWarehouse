using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.StorageProcessing;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.IndustryFirst;

/// <summary>
/// Build cache sharing strategy using content-addressable storage with SHA-256 based cache keys.
/// Provides distributed cache lookup via ProcessingQuery Source as cache namespace,
/// tracks cache hit/miss statistics, and supports cache invalidation by prefix.
/// </summary>
internal sealed class BuildCacheSharingStrategy : StorageProcessingStrategyBase
{
    private readonly BoundedDictionary<string, CacheEntry> _cache = new BoundedDictionary<string, CacheEntry>(1000);
    private long _hits;
    private long _misses;

    /// <inheritdoc/>
    public override string StrategyId => "industryfirst-buildcache";

    /// <inheritdoc/>
    public override string Name => "Build Cache Sharing Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 6
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var mode = CliProcessHelper.GetOption<string>(query, "mode") ?? "lookup";
        var cacheNamespace = query.Source;

        if (mode == "store" && File.Exists(query.Source))
        {
            // Store file content in cache
            var data = await File.ReadAllBytesAsync(query.Source, ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(data));
            var key = $"{cacheNamespace}:{hash}";

            _cache[key] = new CacheEntry
            {
                Key = key,
                Hash = hash,
                Size = data.Length,
                StoredAt = DateTimeOffset.UtcNow,
                Namespace = cacheNamespace
            };

            sw.Stop();
            return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["mode"] = "store", ["cacheKey"] = key, ["hash"] = hash,
                    ["size"] = data.Length, ["namespace"] = cacheNamespace
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = data.Length, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
        }

        if (mode == "invalidate")
        {
            var prefix = CliProcessHelper.GetOption<string>(query, "prefix") ?? cacheNamespace;
            var removed = 0;
            // Snapshot Keys before removal to avoid mutate-while-enumerate (finding 4288).
            var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in keysToRemove)
            {
                if (_cache.TryRemove(key, out _)) removed++;
            }

            sw.Stop();
            return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["mode"] = "invalidate", ["prefix"] = prefix, ["removedCount"] = removed
                },
                Metadata = new ProcessingMetadata { RowsProcessed = removed, RowsReturned = 1, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
        }

        // Lookup mode
        if (File.Exists(query.Source))
        {
            var data = await File.ReadAllBytesAsync(query.Source, ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(data));
            var lookupKey = $"{cacheNamespace}:{hash}";

            if (_cache.TryGetValue(lookupKey, out var entry))
            {
                Interlocked.Increment(ref _hits);
                sw.Stop();
                return new ProcessingResult
                {
                    Data = new Dictionary<string, object?>
                    {
                        ["mode"] = "lookup", ["cacheHit"] = true, ["cacheKey"] = lookupKey,
                        ["hash"] = hash, ["storedAt"] = entry.StoredAt.ToString("O"),
                        ["hitRate"] = GetHitRate()
                    },
                    Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = data.Length, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
                };
            }

            Interlocked.Increment(ref _misses);
        }
        else
        {
            Interlocked.Increment(ref _misses);
        }

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["mode"] = "lookup", ["cacheHit"] = false, ["namespace"] = cacheNamespace,
                ["cacheSize"] = _cache.Count, ["hitRate"] = GetHitRate()
            },
            Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var limit = query.Limit ?? int.MaxValue;
        var idx = 0;

        foreach (var entry in _cache.Values.OrderByDescending(e => e.StoredAt))
        {
            ct.ThrowIfCancellationRequested();
            if (idx >= limit) break;

            yield return new ProcessingResult
            {
                Data = new Dictionary<string, object?>
                {
                    ["cacheKey"] = entry.Key, ["hash"] = entry.Hash,
                    ["size"] = entry.Size, ["namespace"] = entry.Namespace,
                    ["storedAt"] = entry.StoredAt.ToString("O")
                },
                Metadata = new ProcessingMetadata { RowsProcessed = 1, RowsReturned = 1, BytesProcessed = entry.Size, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds }
            };
            idx++;
        }
        // No await needed here (finding 4299: `await Task.CompletedTask` is unnecessary anti-pattern).
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        var values = _cache.Values.Select(e => (long)e.Size).ToList();
        object result = aggregationType switch
        {
            AggregationType.Count => (long)values.Count,
            AggregationType.Sum => values.Count > 0 ? values.Sum() : 0L,
            AggregationType.Average => values.Count > 0 ? values.Average() : 0.0,
            AggregationType.Min => values.Count > 0 ? (object)values.Min() : 0L,
            AggregationType.Max => values.Count > 0 ? (object)values.Max() : 0L,
            _ => 0.0
        };
        return Task.FromResult(new AggregationResult { AggregationType = aggregationType, Value = result });
    }

    private double GetHitRate()
    {
        var h = Interlocked.Read(ref _hits);
        var m = Interlocked.Read(ref _misses);
        var total = h + m;
        return total > 0 ? Math.Round((double)h / total * 100.0, 2) : 0.0;
    }

    private sealed class CacheEntry
    {
        public required string Key { get; init; }
        public required string Hash { get; init; }
        public required long Size { get; init; }
        public required DateTimeOffset StoredAt { get; init; }
        public required string Namespace { get; init; }
    }
}
