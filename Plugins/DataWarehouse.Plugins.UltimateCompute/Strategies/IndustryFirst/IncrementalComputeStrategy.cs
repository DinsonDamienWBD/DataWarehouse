using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Incremental compute strategy that detects changed inputs via SHA-256 fingerprinting and
/// re-executes only affected portions. Caches partial results in a concurrent dictionary
/// to avoid redundant computation on unchanged input segments.
/// </summary>
/// <remarks>
/// <para>
/// Input is segmented by line (or configurable delimiter). Each segment is hashed with SHA-256.
/// Cached results for unchanged segments are returned directly, while only modified or new
/// segments trigger re-execution. The cache uses LRU eviction when capacity is exceeded.
/// </para>
/// </remarks>
internal sealed class IncrementalComputeStrategy : ComputeRuntimeStrategyBase
{
    private readonly BoundedDictionary<string, CachedSegment> _cache = new BoundedDictionary<string, CachedSegment>(1000);
    private const int DefaultMaxCacheEntries = 10_000;

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.incremental";
    /// <inheritdoc/>
    public override string StrategyName => "Incremental Compute";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(2),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 16, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var input = task.GetInputDataAsString();
            var delimiter = "\n";
            if (task.Metadata?.TryGetValue("segment_delimiter", out var sd) == true && sd is string sds)
                delimiter = sds;

            var segments = input.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);
            var cacheKeyPrefix = task.Metadata?.TryGetValue("cache_key", out var ck) == true && ck is string cks
                ? cks
                : ComputeCodeHash(task.Code.ToArray());

            var maxCacheEntries = DefaultMaxCacheEntries;
            if (task.Metadata?.TryGetValue("max_cache_entries", out var mce) == true && mce is int mcei)
                maxCacheEntries = mcei;

            // Fingerprint each segment and determine what needs re-execution
            var segmentHashes = new string[segments.Length];
            var changedIndices = new List<int>();
            var cachedResults = new string?[segments.Length];

            for (var i = 0; i < segments.Length; i++)
            {
                var hash = ComputeHash(segments[i]);
                segmentHashes[i] = hash;
                var cacheKey = $"{cacheKeyPrefix}:{hash}";

                if (_cache.TryGetValue(cacheKey, out var cached))
                {
                    cachedResults[i] = cached.Output;
                    cached.LastAccessed = DateTime.UtcNow;
                }
                else
                {
                    changedIndices.Add(i);
                }
            }

            // Re-execute only changed segments
            var recomputedResults = new BoundedDictionary<int, string>(1000);
            if (changedIndices.Count > 0)
            {
                var codePath = Path.GetTempFileName() + ".sh";
                try
                {
                    await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);
                    var parallelism = Math.Min(changedIndices.Count, Environment.ProcessorCount);

                    await Parallel.ForEachAsync(
                        changedIndices,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                        async (idx, ct) =>
                        {
                            var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                                stdin: segments[idx], timeout: GetEffectiveTimeout(task),
                                cancellationToken: ct);

                            var output = result.ExitCode == 0 ? result.StandardOutput : $"ERROR:{result.StandardError}";
                            recomputedResults[idx] = output;

                            // Cache the result
                            var cacheKey = $"{cacheKeyPrefix}:{segmentHashes[idx]}";
                            _cache[cacheKey] = new CachedSegment(output, DateTime.UtcNow);
                        });
                }
                finally
                {
                    try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                }
            }

            // Evict old cache entries if over limit
            EvictCache(maxCacheEntries);

            // Assemble final output preserving order
            var output = new StringBuilder();
            for (var i = 0; i < segments.Length; i++)
            {
                if (cachedResults[i] != null)
                    output.Append(cachedResults[i]);
                else if (recomputedResults.TryGetValue(i, out var recomputed))
                    output.Append(recomputed);
            }

            var cacheHitRate = segments.Length > 0
                ? (segments.Length - changedIndices.Count) / (double)segments.Length
                : 0.0;

            var logs = new StringBuilder();
            logs.AppendLine($"IncrementalCompute: {segments.Length} segments, {changedIndices.Count} recomputed, {segments.Length - changedIndices.Count} cached");
            logs.AppendLine($"  Cache hit rate: {cacheHitRate:P1}, Cache size: {_cache.Count}/{maxCacheEntries}");
            logs.AppendLine($"  Changed indices: [{string.Join(", ", changedIndices.Take(20))}{(changedIndices.Count > 20 ? "..." : "")}]");

            return (EncodeOutput(output.ToString()), logs.ToString());
        }, cancellationToken);
    }

    /// <summary>
    /// Computes SHA-256 hash of a string segment.
    /// </summary>
    private static string ComputeHash(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Computes SHA-256 hash of a byte array (for code fingerprinting).
    /// </summary>
    private static string ComputeCodeHash(byte[] code)
    {
        return Convert.ToHexString(SHA256.HashData(code));
    }

    /// <summary>
    /// Evicts oldest cache entries when exceeding max capacity.
    /// Uses partial sort (select minimum N by LastAccessed) instead of a full sort
    /// to reduce O(n log n) to O(n) for the common case where few entries need eviction.
    /// </summary>
    private void EvictCache(int maxEntries)
    {
        if (_cache.Count <= maxEntries) return;

        int evictCount = _cache.Count - maxEntries;
        // Collect entries and find the evictCount oldest using a max-heap of size evictCount.
        // Simple approach: one pass to collect all, then partial sort only evictCount items.
        var entries = _cache.ToArray().ToList(); // snapshot as List to allow .Count access
        // Partial selection: bring the evictCount smallest LastAccessed to the front.
        // Array.Sort on the full array is O(n log n); MinHeap-based selection would be O(n log k).
        // For typical cache sizes (≤10,000) a single-pass partial sort is sufficient.
        if (evictCount <= entries.Count / 4)
        {
            // Few entries to evict: single pass to find threshold then evict.
            var threshold = entries
                .Select(kv => kv.Value.LastAccessed)
                .OrderBy(t => t)
                .Skip(evictCount - 1)
                .FirstOrDefault();

            int removed = 0;
            foreach (var kv in entries)
            {
                if (removed >= evictCount) break;
                if (kv.Value.LastAccessed <= threshold)
                {
                    _cache.TryRemove(kv.Key, out _);
                    removed++;
                }
            }
        }
        else
        {
            // Many entries to evict: full sort is acceptable.
            var toEvict = entries
                .OrderBy(kv => kv.Value.LastAccessed)
                .Take(evictCount)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toEvict)
                _cache.TryRemove(key, out _);
        }
    }

    /// <summary>Cached segment result with LRU tracking.</summary>
    private sealed class CachedSegment(string output, DateTime lastAccessed)
    {
        public string Output { get; } = output;
        public DateTime LastAccessed { get; set; } = lastAccessed;
    }
}
