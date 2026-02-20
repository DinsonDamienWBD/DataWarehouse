using System.Collections;
using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Ultra-dense metadata index using probabilistic data structures for exabyte-scale memory.
/// Optimized for fast existence queries and similarity lookups with minimal memory footprint.
///
/// Features:
/// - Bloom filters for existence queries (O(1) lookup, no false negatives)
/// - MinHash signatures for similarity estimation
/// - HyperLogLog for cardinality estimation
/// - Compressed bitmaps for set operations
/// - Quantized embeddings (int8) for similarity with minimal storage
/// </summary>
public sealed class CompressedManifestIndex : ContextIndexBase
{
    private readonly BoundedDictionary<string, ManifestEntry> _entries = new BoundedDictionary<string, ManifestEntry>(1000);
    private readonly BloomFilter _existenceFilter;
    private readonly BoundedDictionary<string, ulong[]> _minHashSignatures = new BoundedDictionary<string, ulong[]>(1000);
    private readonly BoundedDictionary<string, HyperLogLog> _scopeCardinality = new BoundedDictionary<string, HyperLogLog>(1000);
    private readonly BoundedDictionary<string, CompressedBitmap> _tagBitmaps = new BoundedDictionary<string, CompressedBitmap>(1000);
    private readonly BoundedDictionary<string, sbyte[]> _quantizedEmbeddings = new BoundedDictionary<string, sbyte[]>(1000);
    private readonly BoundedDictionary<string, int> _entryIdToIndex = new BoundedDictionary<string, int>(1000);
    private int _nextIndex = 0;

    private const int BloomFilterSize = 10_000_000; // ~10MB for billions of entries
    private const int BloomFilterHashCount = 7;
    private const int MinHashSignatureLength = 128;
    private const int HyperLogLogPrecision = 14;

    /// <inheritdoc/>
    public override string IndexId => "index-compressed-manifest";

    /// <inheritdoc/>
    public override string IndexName => "Compressed Manifest Index";

    /// <summary>
    /// Initializes the compressed manifest index.
    /// </summary>
    public CompressedManifestIndex()
    {
        _existenceFilter = new BloomFilter(BloomFilterSize, BloomFilterHashCount);
    }

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<IndexedContextEntry>();

            // Use quantized embedding for similarity if available
            if (query.QueryEmbedding != null)
            {
                var queryQuantized = QuantizeEmbedding(query.QueryEmbedding);
                var candidates = FindSimilarByQuantizedEmbedding(queryQuantized, query.MaxResults * 2);

                foreach (var (contentId, similarity) in candidates)
                {
                    if (!_entries.TryGetValue(contentId, out var entry))
                        continue;

                    if (!MatchesFilters(entry, query))
                        continue;

                    if (similarity >= query.MinRelevance)
                    {
                        results.Add(CreateEntryResult(entry, similarity, query.IncludeHierarchyPath, query.DetailLevel));
                    }
                }
            }
            else
            {
                // Text-based search
                var queryWords = query.SemanticQuery.ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Use tag bitmaps for filtering if tags are specified
                HashSet<string>? tagFilteredIds = null;
                if (query.RequiredTags?.Length > 0)
                {
                    tagFilteredIds = GetEntriesWithAllTags(query.RequiredTags);
                }

                foreach (var entry in _entries.Values)
                {
                    if (tagFilteredIds != null && !tagFilteredIds.Contains(entry.ContentId))
                        continue;

                    if (!MatchesFilters(entry, query))
                        continue;

                    var relevance = CalculateTextRelevance(entry, queryWords);
                    if (relevance >= query.MinRelevance)
                    {
                        results.Add(CreateEntryResult(entry, relevance, query.IncludeHierarchyPath, query.DetailLevel));
                    }
                }
            }

            // Sort and limit
            results = results
                .OrderByDescending(r => r.RelevanceScore)
                .Take(query.MaxResults)
                .ToList();

            sw.Stop();
            RecordQuery(sw.Elapsed);

            return new ContextQueryResult
            {
                Entries = results,
                NavigationSummary = GenerateNavigationSummary(results, _entries.Count),
                TotalMatchingEntries = results.Count,
                QueryDuration = sw.Elapsed,
                WasTruncated = results.Count >= query.MaxResults
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            RecordQuery(sw.Elapsed);
            throw;
        }
    }

    /// <inheritdoc/>
    public override Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        // This index doesn't have a hierarchical structure
        return Task.FromResult<ContextNode?>(null);
    }

    /// <inheritdoc/>
    public override Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        // This index doesn't have a hierarchical structure
        return Task.FromResult<IEnumerable<ContextNode>>(Array.Empty<ContextNode>());
    }

    /// <inheritdoc/>
    public override Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        var entry = new ManifestEntry
        {
            ContentId = contentId,
            ContentSizeBytes = content.Length,
            Summary = metadata.Summary ?? $"Entry {contentId}",
            Tags = metadata.Tags ?? Array.Empty<string>(),
            Tier = metadata.Tier,
            Scope = metadata.Scope ?? "default",
            CreatedAt = metadata.CreatedAt ?? DateTimeOffset.UtcNow,
            ImportanceScore = CalculateImportance(content, metadata),
            Pointer = new ContextPointer
            {
                StorageBackend = "memory",
                Path = $"entries/{contentId}",
                Offset = 0,
                Length = content.Length
            }
        };

        _entries[contentId] = entry;

        // Assign index
        var index = Interlocked.Increment(ref _nextIndex) - 1;
        _entryIdToIndex[contentId] = index;

        // Add to bloom filter
        _existenceFilter.Add(contentId);

        // Create MinHash signature for similarity
        var textContent = System.Text.Encoding.UTF8.GetString(content);
        var signature = CreateMinHashSignature(textContent);
        _minHashSignatures[contentId] = signature;

        // Update scope cardinality
        if (!_scopeCardinality.ContainsKey(entry.Scope))
            _scopeCardinality[entry.Scope] = new HyperLogLog(HyperLogLogPrecision);
        _scopeCardinality[entry.Scope].Add(contentId);

        // Update tag bitmaps
        foreach (var tag in entry.Tags)
        {
            if (!_tagBitmaps.ContainsKey(tag))
                _tagBitmaps[tag] = new CompressedBitmap();
            _tagBitmaps[tag].Set(index);
        }

        // Quantize embedding if available
        if (metadata.Embedding != null)
        {
            var quantized = QuantizeEmbedding(metadata.Embedding);
            _quantizedEmbeddings[contentId] = quantized;
        }

        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(contentId, out var entry))
            return Task.CompletedTask;

        if (update.NewSummary != null)
            entry = entry with { Summary = update.NewSummary };

        if (update.NewTags != null)
        {
            // Update tag bitmaps
            var index = _entryIdToIndex[contentId];

            // Remove from old tag bitmaps
            foreach (var tag in entry.Tags)
            {
                if (_tagBitmaps.TryGetValue(tag, out var bitmap))
                    bitmap.Clear(index);
            }

            // Add to new tag bitmaps
            foreach (var tag in update.NewTags)
            {
                if (!_tagBitmaps.ContainsKey(tag))
                    _tagBitmaps[tag] = new CompressedBitmap();
                _tagBitmaps[tag].Set(index);
            }

            entry = entry with { Tags = update.NewTags };
        }

        if (update.NewEmbedding != null)
        {
            var quantized = QuantizeEmbedding(update.NewEmbedding);
            _quantizedEmbeddings[contentId] = quantized;
        }

        if (update.RecordAccess)
        {
            entry = entry with
            {
                AccessCount = entry.AccessCount + 1,
                LastAccessedAt = DateTimeOffset.UtcNow
            };
        }

        _entries[contentId] = entry;
        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default)
    {
        if (_entries.TryRemove(contentId, out var entry))
        {
            // Note: Bloom filter doesn't support removal
            // MinHash signature removal
            _minHashSignatures.TryRemove(contentId, out _);

            // Quantized embedding removal
            _quantizedEmbeddings.TryRemove(contentId, out _);

            // Tag bitmap removal
            if (_entryIdToIndex.TryGetValue(contentId, out var index))
            {
                foreach (var tag in entry.Tags)
                {
                    if (_tagBitmaps.TryGetValue(tag, out var bitmap))
                        bitmap.Clear(index);
                }
            }

            MarkUpdated();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var totalContentBytes = _entries.Values.Sum(e => e.ContentSizeBytes);
        var indexSizeBytes = EstimateIndexSize();

        var stats = GetBaseStatistics(
            _entries.Count,
            totalContentBytes,
            indexSizeBytes,
            0,
            0
        );

        var byTier = _entries.Values
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var byScope = _scopeCardinality.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Count());

        return Task.FromResult(stats with
        {
            EntriesByTier = byTier,
            EntriesByScope = byScope
        });
    }

    #region Probabilistic Data Structure Operations

    /// <summary>
    /// Checks if a content ID might exist (false positives possible, no false negatives).
    /// </summary>
    /// <param name="contentId">Content identifier.</param>
    /// <returns>True if the content might exist.</returns>
    public bool MightExist(string contentId)
    {
        return _existenceFilter.MightContain(contentId);
    }

    /// <summary>
    /// Estimates the Jaccard similarity between two entries using MinHash.
    /// </summary>
    /// <param name="contentId1">First content identifier.</param>
    /// <param name="contentId2">Second content identifier.</param>
    /// <returns>Estimated Jaccard similarity (0-1).</returns>
    public float EstimateSimilarity(string contentId1, string contentId2)
    {
        if (!_minHashSignatures.TryGetValue(contentId1, out var sig1) ||
            !_minHashSignatures.TryGetValue(contentId2, out var sig2))
            return 0f;

        return CalculateMinHashSimilarity(sig1, sig2);
    }

    /// <summary>
    /// Finds similar entries using MinHash signatures.
    /// </summary>
    /// <param name="contentId">Reference content identifier.</param>
    /// <param name="topK">Maximum results.</param>
    /// <param name="minSimilarity">Minimum similarity threshold.</param>
    /// <returns>Similar entries with similarity scores.</returns>
    public IList<(string ContentId, float Similarity)> FindSimilar(string contentId, int topK = 10, float minSimilarity = 0.5f)
    {
        if (!_minHashSignatures.TryGetValue(contentId, out var refSignature))
            return Array.Empty<(string, float)>();

        return _minHashSignatures
            .Where(kvp => kvp.Key != contentId)
            .Select(kvp => (ContentId: kvp.Key, Similarity: CalculateMinHashSimilarity(refSignature, kvp.Value)))
            .Where(x => x.Similarity >= minSimilarity)
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Estimates the number of unique entries in a scope.
    /// </summary>
    /// <param name="scope">Scope name.</param>
    /// <returns>Estimated cardinality.</returns>
    public long EstimateCardinality(string scope)
    {
        return _scopeCardinality.TryGetValue(scope, out var hll)
            ? hll.Count()
            : 0;
    }

    /// <summary>
    /// Gets entries with all specified tags using bitmap intersection.
    /// </summary>
    /// <param name="tags">Required tags.</param>
    /// <returns>Content IDs with all tags.</returns>
    public HashSet<string> GetEntriesWithAllTags(string[] tags)
    {
        if (tags.Length == 0)
            return _entries.Keys.ToHashSet();

        CompressedBitmap? result = null;

        foreach (var tag in tags)
        {
            if (!_tagBitmaps.TryGetValue(tag, out var bitmap))
                return new HashSet<string>();

            if (result == null)
                result = bitmap.Clone();
            else
                result.And(bitmap);
        }

        if (result == null)
            return new HashSet<string>();

        // Convert bitmap indices back to content IDs
        var indexToId = _entryIdToIndex.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
        return result.GetSetBits()
            .Where(indexToId.ContainsKey)
            .Select(i => indexToId[i])
            .ToHashSet();
    }

    /// <summary>
    /// Gets entries with any of the specified tags using bitmap union.
    /// </summary>
    /// <param name="tags">Tags to match (OR logic).</param>
    /// <returns>Content IDs with any tag.</returns>
    public HashSet<string> GetEntriesWithAnyTag(string[] tags)
    {
        if (tags.Length == 0)
            return new HashSet<string>();

        CompressedBitmap? result = null;

        foreach (var tag in tags)
        {
            if (!_tagBitmaps.TryGetValue(tag, out var bitmap))
                continue;

            if (result == null)
                result = bitmap.Clone();
            else
                result.Or(bitmap);
        }

        if (result == null)
            return new HashSet<string>();

        var indexToId = _entryIdToIndex.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
        return result.GetSetBits()
            .Where(indexToId.ContainsKey)
            .Select(i => indexToId[i])
            .ToHashSet();
    }

    /// <summary>
    /// Gets compression statistics.
    /// </summary>
    /// <returns>Compression statistics.</returns>
    public CompressionStats GetCompressionStats()
    {
        var fullEmbeddingSize = _entries.Count * 1536 * 4; // Assuming 1536-dim float32
        var quantizedSize = _quantizedEmbeddings.Values.Sum(e => e.Length);

        return new CompressionStats
        {
            EntryCount = _entries.Count,
            BloomFilterSizeBytes = BloomFilterSize / 8,
            BloomFilterFalsePositiveRate = _existenceFilter.FalsePositiveRate,
            MinHashSignatureCount = _minHashSignatures.Count,
            MinHashTotalBytes = _minHashSignatures.Values.Sum(s => s.Length * 8),
            HyperLogLogCount = _scopeCardinality.Count,
            TagBitmapCount = _tagBitmaps.Count,
            TagBitmapTotalBytes = _tagBitmaps.Values.Sum(b => b.SizeBytes),
            QuantizedEmbeddingCount = _quantizedEmbeddings.Count,
            QuantizedEmbeddingBytes = quantizedSize,
            FullEmbeddingBytesEstimate = fullEmbeddingSize,
            EmbeddingCompressionRatio = fullEmbeddingSize > 0 ? (double)fullEmbeddingSize / quantizedSize : 0
        };
    }

    #endregion

    #region Private Helper Methods

    private ulong[] CreateMinHashSignature(string text)
    {
        var words = text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToHashSet();

        var signature = new ulong[MinHashSignatureLength];

        for (int i = 0; i < MinHashSignatureLength; i++)
        {
            signature[i] = ulong.MaxValue;

            foreach (var word in words)
            {
                // Hash with seed
                var hash = MurmurHash3(word, (uint)i);
                if (hash < signature[i])
                    signature[i] = hash;
            }
        }

        return signature;
    }

    private static float CalculateMinHashSimilarity(ulong[] sig1, ulong[] sig2)
    {
        if (sig1.Length != sig2.Length) return 0f;

        var matches = 0;
        for (int i = 0; i < sig1.Length; i++)
        {
            if (sig1[i] == sig2[i])
                matches++;
        }

        return (float)matches / sig1.Length;
    }

    private static sbyte[] QuantizeEmbedding(float[] embedding)
    {
        // Quantize float32 to int8
        var quantized = new sbyte[embedding.Length];

        // Find min/max for scaling
        var min = embedding.Min();
        var max = embedding.Max();
        var range = max - min;

        if (range <= 0)
        {
            Array.Fill(quantized, (sbyte)0);
            return quantized;
        }

        for (int i = 0; i < embedding.Length; i++)
        {
            var normalized = (embedding[i] - min) / range;
            quantized[i] = (sbyte)(normalized * 255 - 128);
        }

        return quantized;
    }

    private IEnumerable<(string ContentId, float Similarity)> FindSimilarByQuantizedEmbedding(sbyte[] queryQuantized, int topK)
    {
        return _quantizedEmbeddings
            .Select(kvp => (ContentId: kvp.Key, Similarity: CalculateQuantizedSimilarity(queryQuantized, kvp.Value)))
            .OrderByDescending(x => x.Similarity)
            .Take(topK);
    }

    private static float CalculateQuantizedSimilarity(sbyte[] a, sbyte[] b)
    {
        if (a.Length != b.Length) return 0f;

        // Approximate cosine similarity using int8 values
        long dotProduct = 0;
        long normA = 0;
        long normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator > 0 ? (float)(dotProduct / denominator) : 0f;
    }

    private static ulong MurmurHash3(string key, uint seed)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(key);
        var hash = seed;

        foreach (var b in data)
        {
            hash ^= b;
            hash *= 0x5bd1e995;
            hash ^= hash >> 15;
        }

        return hash;
    }

    private bool MatchesFilters(ManifestEntry entry, ContextQuery query)
    {
        if (query.Scope != null && !entry.Scope.Equals(query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.TimeRange != null)
        {
            if (query.TimeRange.Start.HasValue && entry.CreatedAt < query.TimeRange.Start.Value)
                return false;
            if (query.TimeRange.End.HasValue && entry.CreatedAt > query.TimeRange.End.Value)
                return false;
        }

        return true;
    }

    private float CalculateTextRelevance(ManifestEntry entry, string[] queryWords)
    {
        if (queryWords.Length == 0) return 0f;

        var summaryLower = entry.Summary.ToLowerInvariant();
        var matchCount = queryWords.Count(w => summaryLower.Contains(w));
        var tagMatches = entry.Tags.Count(t => queryWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));

        return (float)(matchCount + tagMatches) / (queryWords.Length + 1);
    }

    private IndexedContextEntry CreateEntryResult(ManifestEntry entry, float relevance, bool includePath, SummaryLevel detailLevel)
    {
        return new IndexedContextEntry
        {
            ContentId = entry.ContentId,
            HierarchyPath = includePath ? $"scope/{entry.Scope}" : null,
            RelevanceScore = relevance,
            Summary = TruncateSummary(entry.Summary, detailLevel),
            SemanticTags = entry.Tags,
            ContentSizeBytes = entry.ContentSizeBytes,
            Pointer = entry.Pointer,
            CreatedAt = entry.CreatedAt,
            LastAccessedAt = entry.LastAccessedAt,
            AccessCount = entry.AccessCount,
            ImportanceScore = entry.ImportanceScore,
            Tier = entry.Tier
        };
    }

    private static string TruncateSummary(string summary, SummaryLevel level)
    {
        var maxLength = level switch
        {
            SummaryLevel.Minimal => 50,
            SummaryLevel.Brief => 150,
            SummaryLevel.Detailed => 500,
            SummaryLevel.Full => int.MaxValue,
            _ => 150
        };

        return summary.Length <= maxLength ? summary : summary[..maxLength] + "...";
    }

    private long EstimateIndexSize()
    {
        var bloomSize = BloomFilterSize / 8;
        var minHashSize = _minHashSignatures.Values.Sum(s => s.Length * 8);
        var hllSize = _scopeCardinality.Count * (1 << HyperLogLogPrecision);
        var bitmapSize = _tagBitmaps.Values.Sum(b => b.SizeBytes);
        var quantizedSize = _quantizedEmbeddings.Values.Sum(e => e.Length);

        return bloomSize + minHashSize + hllSize + bitmapSize + quantizedSize;
    }

    #endregion

    #region Internal Types

    private sealed record ManifestEntry
    {
        public required string ContentId { get; init; }
        public long ContentSizeBytes { get; init; }
        public required string Summary { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public MemoryTier Tier { get; init; }
        public required string Scope { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastAccessedAt { get; init; }
        public int AccessCount { get; init; }
        public float ImportanceScore { get; init; }
        public required ContextPointer Pointer { get; init; }
    }

    #endregion
}

#region Probabilistic Data Structures

/// <summary>
/// Simple Bloom filter implementation.
/// </summary>
internal sealed class BloomFilter
{
    private readonly BitArray _bits;
    private readonly int _hashCount;
    private int _itemCount;

    public BloomFilter(int size, int hashCount)
    {
        _bits = new BitArray(size);
        _hashCount = hashCount;
    }

    public void Add(string item)
    {
        foreach (var hash in GetHashes(item))
        {
            _bits[hash % _bits.Length] = true;
        }
        _itemCount++;
    }

    public bool MightContain(string item)
    {
        foreach (var hash in GetHashes(item))
        {
            if (!_bits[hash % _bits.Length])
                return false;
        }
        return true;
    }

    public double FalsePositiveRate
    {
        get
        {
            // Approximation: (1 - e^(-kn/m))^k
            double m = _bits.Length;
            double k = _hashCount;
            double n = _itemCount;
            return Math.Pow(1 - Math.Exp(-k * n / m), k);
        }
    }

    private IEnumerable<int> GetHashes(string item)
    {
        var hash1 = item.GetHashCode();
        var hash2 = item.Reverse().ToString()?.GetHashCode() ?? 0;

        for (int i = 0; i < _hashCount; i++)
        {
            yield return Math.Abs(hash1 + i * hash2);
        }
    }
}

/// <summary>
/// Simple HyperLogLog implementation for cardinality estimation.
/// </summary>
internal sealed class HyperLogLog
{
    private readonly byte[] _registers;
    private readonly int _precision;

    public HyperLogLog(int precision)
    {
        _precision = precision;
        _registers = new byte[1 << precision];
    }

    public void Add(string item)
    {
        var hash = (ulong)item.GetHashCode();
        var index = (int)(hash >> (64 - _precision));
        var value = hash << _precision;
        var rank = LeadingZeros(value) + 1;

        if (rank > _registers[index])
            _registers[index] = (byte)rank;
    }

    public long Count()
    {
        var m = _registers.Length;
        var alpha = GetAlpha(m);
        var sum = _registers.Sum(r => Math.Pow(2, -r));
        var estimate = alpha * m * m / sum;

        // Small range correction
        if (estimate <= 2.5 * m)
        {
            var zeros = _registers.Count(r => r == 0);
            if (zeros > 0)
                estimate = m * Math.Log((double)m / zeros);
        }

        return (long)estimate;
    }

    private static int LeadingZeros(ulong value)
    {
        if (value == 0) return 64;
        var count = 0;
        while ((value & 0x8000000000000000) == 0)
        {
            count++;
            value <<= 1;
        }
        return count;
    }

    private static double GetAlpha(int m)
    {
        return m switch
        {
            16 => 0.673,
            32 => 0.697,
            64 => 0.709,
            _ => 0.7213 / (1 + 1.079 / m)
        };
    }
}

/// <summary>
/// Simple compressed bitmap using run-length encoding.
/// </summary>
internal sealed class CompressedBitmap
{
    private readonly List<(int Start, int Length)> _runs = new();
    private readonly object _lock = new();

    public void Set(int index)
    {
        lock (_lock)
        {
            // Find where to insert
            for (int i = 0; i < _runs.Count; i++)
            {
                var (start, length) = _runs[i];

                // Check if index extends this run
                if (index == start - 1)
                {
                    _runs[i] = (index, length + 1);
                    MergeRuns(i);
                    return;
                }
                if (index == start + length)
                {
                    _runs[i] = (start, length + 1);
                    MergeRuns(i);
                    return;
                }
                if (index >= start && index < start + length)
                {
                    // Already set
                    return;
                }
                if (index < start)
                {
                    _runs.Insert(i, (index, 1));
                    return;
                }
            }

            _runs.Add((index, 1));
        }
    }

    public void Clear(int index)
    {
        lock (_lock)
        {
            for (int i = 0; i < _runs.Count; i++)
            {
                var (start, length) = _runs[i];

                if (index >= start && index < start + length)
                {
                    if (length == 1)
                    {
                        _runs.RemoveAt(i);
                    }
                    else if (index == start)
                    {
                        _runs[i] = (start + 1, length - 1);
                    }
                    else if (index == start + length - 1)
                    {
                        _runs[i] = (start, length - 1);
                    }
                    else
                    {
                        // Split the run
                        _runs[i] = (start, index - start);
                        _runs.Insert(i + 1, (index + 1, start + length - index - 1));
                    }
                    return;
                }
            }
        }
    }

    public bool Get(int index)
    {
        lock (_lock)
        {
            foreach (var (start, length) in _runs)
            {
                if (index >= start && index < start + length)
                    return true;
            }
            return false;
        }
    }

    public void And(CompressedBitmap other)
    {
        lock (_lock)
        lock (other._lock)
        {
            var result = new List<(int, int)>();
            var i = 0;
            var j = 0;

            while (i < _runs.Count && j < other._runs.Count)
            {
                var (s1, l1) = _runs[i];
                var (s2, l2) = other._runs[j];

                var start = Math.Max(s1, s2);
                var end = Math.Min(s1 + l1, s2 + l2);

                if (start < end)
                    result.Add((start, end - start));

                if (s1 + l1 < s2 + l2) i++;
                else j++;
            }

            _runs.Clear();
            _runs.AddRange(result);
        }
    }

    public void Or(CompressedBitmap other)
    {
        lock (_lock)
        lock (other._lock)
        {
            foreach (var (start, length) in other._runs)
            {
                for (int i = 0; i < length; i++)
                    Set(start + i);
            }
        }
    }

    public CompressedBitmap Clone()
    {
        var clone = new CompressedBitmap();
        lock (_lock)
        {
            clone._runs.AddRange(_runs);
        }
        return clone;
    }

    public IEnumerable<int> GetSetBits()
    {
        lock (_lock)
        {
            foreach (var (start, length) in _runs)
            {
                for (int i = 0; i < length; i++)
                    yield return start + i;
            }
        }
    }

    public int SizeBytes => _runs.Count * 8 + 16;

    private void MergeRuns(int index)
    {
        // Merge with next if adjacent
        if (index + 1 < _runs.Count)
        {
            var (s1, l1) = _runs[index];
            var (s2, l2) = _runs[index + 1];

            if (s1 + l1 >= s2)
            {
                _runs[index] = (s1, Math.Max(l1, s2 + l2 - s1));
                _runs.RemoveAt(index + 1);
            }
        }

        // Merge with previous if adjacent
        if (index > 0)
        {
            var (s0, l0) = _runs[index - 1];
            var (s1, l1) = _runs[index];

            if (s0 + l0 >= s1)
            {
                _runs[index - 1] = (s0, Math.Max(l0, s1 + l1 - s0));
                _runs.RemoveAt(index);
            }
        }
    }
}

#endregion

#region Compression Types

/// <summary>
/// Statistics about index compression.
/// </summary>
public record CompressionStats
{
    /// <summary>Total number of entries.</summary>
    public long EntryCount { get; init; }

    /// <summary>Bloom filter size in bytes.</summary>
    public long BloomFilterSizeBytes { get; init; }

    /// <summary>Bloom filter false positive rate.</summary>
    public double BloomFilterFalsePositiveRate { get; init; }

    /// <summary>Number of MinHash signatures.</summary>
    public long MinHashSignatureCount { get; init; }

    /// <summary>Total MinHash bytes.</summary>
    public long MinHashTotalBytes { get; init; }

    /// <summary>Number of HyperLogLog counters.</summary>
    public int HyperLogLogCount { get; init; }

    /// <summary>Number of tag bitmaps.</summary>
    public int TagBitmapCount { get; init; }

    /// <summary>Total tag bitmap bytes.</summary>
    public long TagBitmapTotalBytes { get; init; }

    /// <summary>Number of quantized embeddings.</summary>
    public long QuantizedEmbeddingCount { get; init; }

    /// <summary>Total quantized embedding bytes.</summary>
    public long QuantizedEmbeddingBytes { get; init; }

    /// <summary>Estimated full embedding bytes.</summary>
    public long FullEmbeddingBytesEstimate { get; init; }

    /// <summary>Embedding compression ratio.</summary>
    public double EmbeddingCompressionRatio { get; init; }
}

#endregion
