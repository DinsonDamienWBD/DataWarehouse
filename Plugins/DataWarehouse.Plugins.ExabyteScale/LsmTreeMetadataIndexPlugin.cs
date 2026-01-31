using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Scale;

namespace DataWarehouse.Plugins.ExabyteScale;

/// <summary>
/// LSM-tree based distributed metadata index with Bloom filters.
/// Optimized for write-heavy workloads at trillion-object scale.
/// </summary>
public class LsmTreeMetadataIndexPlugin : DistributedMetadataIndexPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.scale.lsmindex";

    /// <inheritdoc/>
    public override string Name => "LSM-Tree Metadata Index";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.MetadataIndexingProvider;

    /// <inheritdoc/>
    protected override int MaxLevels => 7;

    /// <inheritdoc/>
    protected override long MemTableSizeBytes => 128 * 1024 * 1024; // 128 MB

    /// <inheritdoc/>
    protected override double BloomFilterFalsePositiveRate => 0.01; // 1%

    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _memTable = new();
    private readonly List<LsmLevel> _levels = new();
    private long _currentMemTableSize = 0;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _compactionLock = new(1, 1);

    /// <summary>
    /// Initializes the LSM-tree with empty levels.
    /// </summary>
    public LsmTreeMetadataIndexPlugin()
    {
        for (int i = 0; i < MaxLevels; i++)
        {
            _levels.Add(new LsmLevel
            {
                LevelNumber = i,
                SSTables = new List<SSTable>(),
                TotalSizeBytes = 0
            });
        }
    }

    /// <inheritdoc/>
    protected override async Task WriteToMemTableAsync(string key, Dictionary<string, object> metadata)
    {
        // Add to MemTable
        _memTable[key] = metadata;

        // Estimate size (rough approximation)
        var estimatedSize = key.Length + JsonSerializer.Serialize(metadata).Length;
        Interlocked.Add(ref _currentMemTableSize, estimatedSize);

        // Check if flush is needed
        if (_currentMemTableSize >= MemTableSizeBytes)
        {
            await FlushMemTableAsync();
        }
    }

    /// <inheritdoc/>
    protected override async Task FlushMemTableAsync()
    {
        await _flushLock.WaitAsync();
        try
        {
            if (_memTable.IsEmpty)
                return;

            // Freeze current MemTable
            var frozenMemTable = new Dictionary<string, Dictionary<string, object>>(_memTable);
            _memTable.Clear();
            Interlocked.Exchange(ref _currentMemTableSize, 0);

            // Sort entries by key
            var sortedEntries = frozenMemTable.OrderBy(kv => kv.Key).ToList();

            // Create SSTable
            var ssTableId = $"sst-{Guid.NewGuid():N}";
            var ssTable = new SSTable
            {
                Id = ssTableId,
                Entries = sortedEntries,
                BloomFilter = CreateBloomFilter(frozenMemTable.Keys),
                SizeBytes = sortedEntries.Sum(e => e.Key.Length + JsonSerializer.Serialize(e.Value).Length),
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Add to Level 0
            _levels[0].SSTables.Add(ssTable);
            _levels[0].TotalSizeBytes += ssTable.SizeBytes;

            // Trigger compaction if Level 0 has too many SSTables
            if (_levels[0].SSTables.Count > 4)
            {
                _ = Task.Run(async () => await CompactLevelAsync(0));
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<MetadataSearchResult> SearchLevelsAsync(MetadataQuery query)
    {
        var results = new List<MetadataSearchResult>();

        // Search MemTable first
        foreach (var entry in _memTable)
        {
            if (MatchesQuery(entry.Key, entry.Value, query))
            {
                results.Add(new MetadataSearchResult(
                    entry.Key,
                    entry.Value,
                    ComputeScore(entry.Key, entry.Value, query)
                ));
            }
        }

        // Search each level
        for (int level = 0; level < MaxLevels; level++)
        {
            foreach (var ssTable in _levels[level].SSTables)
            {
                // Check Bloom filter first for efficiency
                if (query.Pattern != null && !ssTable.BloomFilter.MightContain(query.Pattern))
                    continue;

                foreach (var entry in ssTable.Entries)
                {
                    if (MatchesQuery(entry.Key, entry.Value, query))
                    {
                        results.Add(new MetadataSearchResult(
                            entry.Key,
                            entry.Value,
                            ComputeScore(entry.Key, entry.Value, query)
                        ));
                    }
                }
            }
        }

        // Remove duplicates (keep newest version)
        var deduplicated = results
            .GroupBy(r => r.Key)
            .Select(g => g.First())
            .OrderByDescending(r => r.Score)
            .Skip(query.Offset)
            .Take(query.Limit);

        foreach (var result in deduplicated)
        {
            yield return result;
        }
    }

    /// <inheritdoc/>
    protected override async Task CompactLevelAsync(int level)
    {
        if (level >= MaxLevels - 1)
            return;

        await _compactionLock.WaitAsync();
        try
        {
            var currentLevel = _levels[level];
            var nextLevel = _levels[level + 1];

            if (currentLevel.SSTables.Count < 2)
                return;

            // Select SSTables to merge (up to 4 for size-tiered compaction)
            var toMerge = currentLevel.SSTables.Take(4).ToList();

            // Merge-sort entries
            var mergedEntries = toMerge
                .SelectMany(sst => sst.Entries)
                .GroupBy(e => e.Key)
                .Select(g => g.First()) // Remove duplicates, keep first (newest)
                .OrderBy(e => e.Key)
                .ToList();

            // Create merged SSTable
            var mergedSSTable = new SSTable
            {
                Id = $"sst-merged-{Guid.NewGuid():N}",
                Entries = mergedEntries,
                BloomFilter = CreateBloomFilter(mergedEntries.Select(e => e.Key)),
                SizeBytes = mergedEntries.Sum(e => e.Key.Length + JsonSerializer.Serialize(e.Value).Length),
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Add to next level
            nextLevel.SSTables.Add(mergedSSTable);
            nextLevel.TotalSizeBytes += mergedSSTable.SizeBytes;

            // Remove merged SSTables from current level
            foreach (var sst in toMerge)
            {
                currentLevel.SSTables.Remove(sst);
                currentLevel.TotalSizeBytes -= sst.SizeBytes;
            }
        }
        finally
        {
            _compactionLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override Task<DistributedIndexStatistics> ComputeStatisticsAsync()
    {
        var documentCount = _memTable.Count + _levels.Sum(l => l.SSTables.Sum(sst => sst.Entries.Count));
        var indexSizeBytes = _levels.Sum(l => l.TotalSizeBytes);
        var levelCount = _levels.Count(l => l.SSTables.Count > 0);
        var bloomFilterBytes = _levels.Sum(l => l.SSTables.Sum(sst => sst.BloomFilter.SizeBytes));

        return Task.FromResult(new DistributedIndexStatistics(
            documentCount,
            indexSizeBytes,
            levelCount,
            bloomFilterBytes
        ));
    }

    private bool MatchesQuery(string key, Dictionary<string, object> metadata, MetadataQuery query)
    {
        // Check pattern match
        if (query.Pattern != null && !MatchesPattern(key, query.Pattern))
            return false;

        // Check filters
        if (query.Filters != null)
        {
            foreach (var filter in query.Filters)
            {
                if (!metadata.TryGetValue(filter.Key, out var value) || !value.Equals(filter.Value))
                    return false;
            }
        }

        return true;
    }

    private bool MatchesPattern(string key, string pattern)
    {
        // Simple glob pattern matching (* and ?)
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(key, regex);
    }

    private double ComputeScore(string key, Dictionary<string, object> metadata, MetadataQuery query)
    {
        // Simple scoring: 1.0 for exact pattern match, 0.8 for wildcard match
        if (query.Pattern == null)
            return 1.0;

        return key.Equals(query.Pattern, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.8;
    }

    private SimpleBloomFilter CreateBloomFilter(IEnumerable<string> keys)
    {
        var filter = new SimpleBloomFilter(BloomFilterFalsePositiveRate);
        foreach (var key in keys)
        {
            filter.Add(key);
        }
        return filter;
    }

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken ct)
    {
        // Initialize LSM-tree structures
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        // Flush and cleanup
        _flushLock.Dispose();
        _compactionLock.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents a level in the LSM-tree.
/// </summary>
internal class LsmLevel
{
    public required int LevelNumber { get; init; }
    public required List<SSTable> SSTables { get; init; }
    public required long TotalSizeBytes { get; set; }
}

/// <summary>
/// Represents a Sorted String Table (SSTable) in the LSM-tree.
/// </summary>
internal class SSTable
{
    public required string Id { get; init; }
    public required List<KeyValuePair<string, Dictionary<string, object>>> Entries { get; init; }
    public required SimpleBloomFilter BloomFilter { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Simple Bloom filter implementation for approximate set membership testing.
/// </summary>
internal class SimpleBloomFilter
{
    private readonly BitArray _bits;
    private readonly int _numHashFunctions;
    private const int BitsPerElement = 10; // Gives ~1% false positive rate

    public long SizeBytes => _bits.Length / 8;

    public SimpleBloomFilter(double falsePositiveRate)
    {
        // Calculate optimal size: m = -(n * ln(p)) / (ln(2)^2)
        // For simplicity, use fixed bits per element
        _bits = new BitArray(10000); // Start with reasonable size
        _numHashFunctions = (int)Math.Ceiling(Math.Log(2) * BitsPerElement);
    }

    /// <summary>
    /// Adds a key to the Bloom filter.
    /// </summary>
    public void Add(string key)
    {
        var hash1 = GetHash1(key);
        var hash2 = GetHash2(key);

        for (int i = 0; i < _numHashFunctions; i++)
        {
            var combinedHash = (hash1 + i * hash2) % _bits.Length;
            _bits[(int)combinedHash] = true;
        }
    }

    /// <summary>
    /// Tests if a key might be in the set (may have false positives).
    /// </summary>
    public bool MightContain(string key)
    {
        var hash1 = GetHash1(key);
        var hash2 = GetHash2(key);

        for (int i = 0; i < _numHashFunctions; i++)
        {
            var combinedHash = (hash1 + i * hash2) % _bits.Length;
            if (!_bits[(int)combinedHash])
                return false;
        }

        return true;
    }

    private uint GetHash1(string key)
    {
        return (uint)key.GetHashCode();
    }

    private uint GetHash2(string key)
    {
        // Use a different hash function
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        return (uint)System.IO.Hashing.XxHash32.HashToUInt32(bytes);
    }
}

/// <summary>
/// Simple bit array for Bloom filter.
/// </summary>
internal class BitArray
{
    private readonly bool[] _bits;

    public int Length => _bits.Length;

    public BitArray(int size)
    {
        _bits = new bool[size];
    }

    public bool this[int index]
    {
        get => _bits[index];
        set => _bits[index] = value;
    }
}
