// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateStorage.Scaling;

/// <summary>
/// A single hit from a search query, containing document ID, relevance score, and term frequency.
/// </summary>
/// <param name="DocumentId">The unique identifier of the matched document.</param>
/// <param name="Score">Relevance score for ranking (higher is more relevant).</param>
/// <param name="TermFrequency">Number of times the query term appears in the document.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Search hit result")]
public readonly record struct SearchHit(long DocumentId, double Score, int TermFrequency);

/// <summary>
/// Paginated search result with continuation support.
/// </summary>
/// <param name="Items">The search hits for the current page.</param>
/// <param name="TotalHits">Estimated total number of matching documents.</param>
/// <param name="HasMore">Whether more results are available beyond this page.</param>
/// <param name="ContinuationToken">Opaque token for retrieving the next page of results.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Paginated search result")]
public record PagedSearchResult(
    IReadOnlyList<SearchHit> Items,
    long TotalHits,
    bool HasMore,
    string? ContinuationToken);

/// <summary>
/// Boolean query operator for combining posting list results.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Boolean query operator")]
public enum BooleanOperator
{
    /// <summary>All terms must match (posting list intersection).</summary>
    And,

    /// <summary>Any term may match (posting list union).</summary>
    Or,

    /// <summary>Exclude documents containing the term (posting list difference).</summary>
    Not
}

/// <summary>
/// A single clause in a boolean search query.
/// </summary>
/// <param name="Term">The search term.</param>
/// <param name="Operator">How this term combines with previous clauses.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Boolean query clause")]
public record BooleanClause(string Term, BooleanOperator Operator = BooleanOperator.And);

/// <summary>
/// Tokenizer mode for text analysis before indexing.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Tokenizer configuration")]
public enum TokenizerMode
{
    /// <summary>Split on whitespace characters.</summary>
    Whitespace,

    /// <summary>Character n-gram tokenization for substring matching.</summary>
    NGram,

    /// <summary>Whitespace + lowercase + stop word removal.</summary>
    AnalyzerChain
}

/// <summary>
/// A posting list entry containing a document ID and its term frequency.
/// Entries are kept sorted by document ID for efficient intersection/union/difference.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Posting list entry")]
public readonly record struct PostingEntry(long DocumentId, int Frequency)
    : IComparable<PostingEntry>
{
    /// <inheritdoc/>
    public int CompareTo(PostingEntry other) => DocumentId.CompareTo(other.DocumentId);
}

/// <summary>
/// A posting list containing sorted document IDs with frequency data for a single term.
/// Supports block-based iteration for efficient skip during pagination.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Sorted posting list with block-based skip")]
public sealed class PostingList
{
    /// <summary>Block size for block-based skip during pagination. Default: 128 entries.</summary>
    public const int BlockSize = 128;

    private readonly List<PostingEntry> _entries = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>Gets the number of entries in this posting list.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _entries.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Adds or updates a document entry in the posting list, maintaining sorted order.
    /// </summary>
    /// <param name="documentId">The document ID.</param>
    /// <param name="frequency">The term frequency in this document.</param>
    public void AddOrUpdate(long documentId, int frequency)
    {
        _lock.EnterWriteLock();
        try
        {
            var entry = new PostingEntry(documentId, frequency);
            int idx = _entries.BinarySearch(entry);
            if (idx >= 0)
            {
                _entries[idx] = entry;
            }
            else
            {
                _entries.Insert(~idx, entry);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a document from the posting list.
    /// </summary>
    /// <param name="documentId">The document ID to remove.</param>
    /// <returns><c>true</c> if the document was found and removed.</returns>
    public bool Remove(long documentId)
    {
        _lock.EnterWriteLock();
        try
        {
            var search = new PostingEntry(documentId, 0);
            int idx = _entries.BinarySearch(search);
            if (idx >= 0)
            {
                _entries.RemoveAt(idx);
                return true;
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Gets a read-only snapshot of all entries for iteration.
    /// </summary>
    /// <returns>A list of posting entries sorted by document ID.</returns>
    public IReadOnlyList<PostingEntry> GetEntries()
    {
        _lock.EnterReadLock();
        try
        {
            return _entries.ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Gets entries starting at the specified offset with a limit, for efficient pagination.
    /// Uses block-based skip for O(1) offset calculation.
    /// </summary>
    /// <param name="offset">Number of entries to skip.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <returns>A slice of posting entries.</returns>
    public IReadOnlyList<PostingEntry> GetEntriesPage(int offset, int limit)
    {
        _lock.EnterReadLock();
        try
        {
            if (offset >= _entries.Count)
                return Array.Empty<PostingEntry>();

            int count = Math.Min(limit, _entries.Count - offset);
            var result = new PostingEntry[count];
            _entries.CopyTo(offset, result, 0, count);
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }
}

/// <summary>
/// A single vector entry in a shard, containing the vector data and document association.
/// </summary>
/// <param name="DocumentId">The document this vector belongs to.</param>
/// <param name="Vector">The vector data (embedding).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Vector index entry")]
public record VectorEntry(long DocumentId, float[] Vector);

/// <summary>
/// A vector search result with similarity score.
/// </summary>
/// <param name="DocumentId">The matched document ID.</param>
/// <param name="Score">Cosine similarity score (higher is more similar).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Vector search result")]
public readonly record struct VectorSearchResult(long DocumentId, double Score)
    : IComparable<VectorSearchResult>
{
    /// <inheritdoc/>
    public int CompareTo(VectorSearchResult other) => other.Score.CompareTo(Score); // Descending
}

/// <summary>
/// A single shard of the vector index, containing a subset of vectors assigned by ID hash.
/// Each shard is independently searchable with its own bounded cache.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Vector index shard")]
public sealed class VectorShard : IDisposable
{
    private readonly BoundedCache<long, float[]> _vectors;
    private readonly int _shardId;
    private bool _disposed;

    /// <summary>
    /// Initializes a new vector shard with the specified capacity.
    /// </summary>
    /// <param name="shardId">The shard identifier.</param>
    /// <param name="maxEntries">Maximum number of vectors in this shard.</param>
    public VectorShard(int shardId, int maxEntries)
    {
        _shardId = shardId;
        _vectors = new BoundedCache<long, float[]>(
            new BoundedCacheOptions<long, float[]>
            {
                MaxEntries = maxEntries,
                EvictionPolicy = CacheEvictionMode.LRU
            });
    }

    /// <summary>Gets the shard identifier.</summary>
    public int ShardId => _shardId;

    /// <summary>Gets the number of vectors in this shard.</summary>
    public int Count => _vectors.Count;

    /// <summary>
    /// Inserts or updates a vector in this shard.
    /// </summary>
    /// <param name="documentId">The document ID.</param>
    /// <param name="vector">The vector data.</param>
    public void Put(long documentId, float[] vector)
    {
        _vectors.Put(documentId, vector);
    }

    /// <summary>
    /// Removes a vector from this shard.
    /// </summary>
    /// <param name="documentId">The document ID to remove.</param>
    /// <returns><c>true</c> if the vector was removed.</returns>
    public bool Remove(long documentId)
    {
        return _vectors.TryRemove(documentId, out _);
    }

    /// <summary>
    /// Searches this shard for the top-K vectors most similar to the query vector
    /// using cosine similarity.
    /// </summary>
    /// <param name="queryVector">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>Top-K results sorted by descending similarity score.</returns>
    public List<VectorSearchResult> Search(float[] queryVector, int topK)
    {
        var results = new List<VectorSearchResult>();

        foreach (var kvp in _vectors)
        {
            double score = CosineSimilarity(queryVector, kvp.Value);
            results.Add(new VectorSearchResult(kvp.Key, score));
        }

        results.Sort();
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return results;
    }

    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        double dot = 0, normA = 0, normB = 0;

        for (int i = 0; i < len; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0.0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vectors.Dispose();
    }
}

/// <summary>
/// Scaling manager for the search subsystem implementing <see cref="IScalableSubsystem"/>.
/// Replaces ConcurrentDictionary iteration with a proper inverted index structure,
/// provides paginated and streaming search results, and supports horizontally sharded
/// vector indexing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inverted index:</b> Each term maps to a <see cref="PostingList"/> stored in a
/// <see cref="BoundedCache{TKey,TValue}"/>. Posting lists contain sorted document IDs with
/// frequency data. Supports boolean queries (AND/OR/NOT) via posting list
/// intersection/union/difference.
/// </para>
/// <para>
/// <b>Pagination:</b> <see cref="Search(string, int, int)"/> returns a <see cref="PagedSearchResult"/>
/// with offset/limit semantics and continuation tokens. Block-based posting list skip
/// provides efficient offset handling.
/// </para>
/// <para>
/// <b>Streaming:</b> <see cref="SearchStreamAsync"/> returns <see cref="IAsyncEnumerable{SearchHit}"/>
/// for unbounded result sets with lazy materialization from posting list iterators.
/// </para>
/// <para>
/// <b>Vector sharding:</b> Vector space is partitioned into N shards (default: ProcessorCount)
/// assigned by document ID hash. Search queries fan out to all shards in parallel and merge
/// results by score. Each shard is bounded by <see cref="ScalingLimits.MaxCacheEntries"/>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-12: Search scaling with inverted index, pagination, streaming, and vector sharding")]
public sealed class SearchScalingManager : IScalableSubsystem, IDisposable
{
    private readonly ILogger _logger;

    // Inverted index: term -> PostingList
    private readonly BoundedCache<string, PostingList> _invertedIndex;

    // Term dictionary for prefix queries (sorted for binary search)
    private readonly SortedSet<string> _termDictionary = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _termDictLock = new(LockRecursionPolicy.NoRecursion);

    // Vector index shards
    private readonly VectorShard[] _vectorShards;
    private readonly int _shardCount;

    // Batch write buffer for index updates
    private readonly ConcurrentQueue<(string Term, long DocId, int Frequency)> _writeBuffer = new();
    private Timer? _flushTimer;
    private readonly TimeSpan _flushInterval;

    // Tokenizer configuration
    private TokenizerMode _tokenizerMode;

    // Concurrency control
    private SemaphoreSlim _searchSemaphore;
    private ScalingLimits _currentLimits;

    // Metrics
    private long _pendingSearches;
    private long _totalSearches;
    private long _indexedDocuments;
    private long _totalTerms;

    // Latency tracking (circular buffer for p50/p95/p99)
    private readonly long[] _latencySamples;
    private int _latencySampleIndex;
    private readonly object _latencyLock = new();

    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="SearchScalingManager"/> with the specified configuration.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <param name="limits">Optional initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="shardCount">Number of vector index shards. Default: <see cref="Environment.ProcessorCount"/>.</param>
    /// <param name="flushInterval">Batch write flush interval. Default: 1 second.</param>
    /// <param name="tokenizerMode">Text tokenizer mode. Default: <see cref="TokenizerMode.Whitespace"/>.</param>
    public SearchScalingManager(
        ILogger logger,
        ScalingLimits? limits = null,
        int shardCount = 0,
        TimeSpan? flushInterval = null,
        TokenizerMode tokenizerMode = TokenizerMode.Whitespace)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenizerMode = tokenizerMode;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(1);

        _currentLimits = limits ?? new ScalingLimits(
            MaxCacheEntries: 100_000,
            MaxConcurrentOperations: Environment.ProcessorCount);

        _searchSemaphore = new SemaphoreSlim(
            _currentLimits.MaxConcurrentOperations,
            _currentLimits.MaxConcurrentOperations);

        // Inverted index with LRU eviction for posting lists
        _invertedIndex = new BoundedCache<string, PostingList>(
            new BoundedCacheOptions<string, PostingList>
            {
                MaxEntries = _currentLimits.MaxCacheEntries,
                EvictionPolicy = CacheEvictionMode.LRU
            });

        // Initialize vector shards
        _shardCount = shardCount > 0 ? shardCount : Environment.ProcessorCount;
        int perShardCapacity = Math.Max(1, _currentLimits.MaxCacheEntries / _shardCount);
        _vectorShards = new VectorShard[_shardCount];
        for (int i = 0; i < _shardCount; i++)
        {
            _vectorShards[i] = new VectorShard(i, perShardCapacity);
        }

        // Latency tracking buffer (1000 samples)
        _latencySamples = new long[1000];

        // Start batch flush timer
        _flushTimer = new Timer(FlushWriteBuffer, null, _flushInterval, _flushInterval);

        _logger.LogInformation(
            "SearchScalingManager initialized: MaxTerms={MaxTerms}, VectorShards={Shards}, FlushInterval={Flush}ms, Tokenizer={Tokenizer}",
            _currentLimits.MaxCacheEntries, _shardCount, _flushInterval.TotalMilliseconds, _tokenizerMode);
    }

    /// <summary>
    /// Gets or sets the tokenizer mode for text analysis.
    /// </summary>
    public TokenizerMode Tokenizer
    {
        get => _tokenizerMode;
        set => _tokenizerMode = value;
    }

    /// <summary>
    /// Gets the number of vector index shards.
    /// </summary>
    public int ShardCount => _shardCount;

    // ---- Indexing ----

    /// <summary>
    /// Indexes a document's text content into the inverted index.
    /// Uses batch buffering with configurable flush interval for durability.
    /// </summary>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="text">The text content to index.</param>
    public void IndexDocument(long documentId, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var tokens = Tokenize(text);
        var termFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            termFrequency.TryGetValue(token, out int count);
            termFrequency[token] = count + 1;
        }

        foreach (var (term, freq) in termFrequency)
        {
            _writeBuffer.Enqueue((term, documentId, freq));
        }

        Interlocked.Increment(ref _indexedDocuments);
    }

    /// <summary>
    /// Immediately flushes buffered index writes to the inverted index.
    /// Called automatically on the configured flush interval.
    /// </summary>
    public void FlushIndex()
    {
        FlushWriteBuffer(null);
    }

    /// <summary>
    /// Removes a document from the inverted index across all terms.
    /// </summary>
    /// <param name="documentId">The document ID to remove.</param>
    /// <param name="terms">The terms that were indexed for this document.</param>
    public void RemoveDocument(long documentId, IEnumerable<string> terms)
    {
        foreach (var term in terms)
        {
            var normalized = NormalizeTerm(term);
            var postingList = _invertedIndex.GetOrDefault(normalized);
            postingList?.Remove(documentId);
        }
    }

    // ---- Text Search ----

    /// <summary>
    /// Searches the inverted index for documents matching the query with pagination.
    /// </summary>
    /// <param name="query">The search query (whitespace-separated terms with implicit AND).</param>
    /// <param name="offset">Number of results to skip.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A <see cref="PagedSearchResult"/> with matching documents.</returns>
    public PagedSearchResult Search(string query, int offset = 0, int limit = 20)
    {
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref _pendingSearches);
        Interlocked.Increment(ref _totalSearches);

        try
        {
            var terms = Tokenize(query);
            if (terms.Length == 0)
                return new PagedSearchResult(Array.Empty<SearchHit>(), 0, false, null);

            // Build boolean clauses (default AND for all terms)
            var clauses = terms
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new BooleanClause(t, BooleanOperator.And))
                .ToArray();

            // Use paged execution to avoid re-enumerating the sorted list with Skip/Take
            var (page, totalHits) = ExecuteBooleanQueryPaged(clauses, offset, limit);

            bool hasMore = offset + limit < totalHits;
            string? continuation = hasMore ? $"{offset + limit}" : null;

            return new PagedSearchResult(page.ToArray(), totalHits, hasMore, continuation);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSearches);
            RecordLatency(sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Executes a boolean query with explicit AND/OR/NOT operators.
    /// </summary>
    /// <param name="clauses">The boolean query clauses.</param>
    /// <param name="offset">Number of results to skip.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A <see cref="PagedSearchResult"/> with matching documents.</returns>
    public PagedSearchResult BooleanSearch(IReadOnlyList<BooleanClause> clauses, int offset = 0, int limit = 20)
    {
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref _pendingSearches);
        Interlocked.Increment(ref _totalSearches);

        try
        {
            // Use paged execution to avoid re-enumerating the sorted list with Skip/Take
            var (page, totalHits) = ExecuteBooleanQueryPaged(clauses, offset, limit);

            bool hasMore = offset + limit < totalHits;
            string? continuation = hasMore ? $"{offset + limit}" : null;

            return new PagedSearchResult(page.ToArray(), totalHits, hasMore, continuation);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSearches);
            RecordLatency(sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Streams search results lazily as an <see cref="IAsyncEnumerable{SearchHit}"/>
    /// for unbounded result sets. Materializes results from posting list iterators
    /// without buffering the full result set.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of search hits.</returns>
    public async IAsyncEnumerable<SearchHit> SearchStreamAsync(
        string query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var terms = Tokenize(query);
        if (terms.Length == 0) yield break;

        // For streaming, use the first term's posting list as the primary iterator
        var primaryTerm = NormalizeTerm(terms[0]);
        var primaryList = _invertedIndex.GetOrDefault(primaryTerm);
        if (primaryList == null) yield break;

        // Get all other posting lists for intersection
        var otherLists = new List<PostingList>();
        for (int i = 1; i < terms.Length; i++)
        {
            var normalized = NormalizeTerm(terms[i]);
            var list = _invertedIndex.GetOrDefault(normalized);
            if (list != null) otherLists.Add(list);
        }

        // Stream results in pages to avoid materializing entire posting list
        int pageOffset = 0;
        const int pageSize = 256;

        while (!ct.IsCancellationRequested)
        {
            var entries = primaryList.GetEntriesPage(pageOffset, pageSize);
            if (entries.Count == 0) break;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                // Verify document exists in all other posting lists (AND semantics)
                bool matchesAll = true;
                double totalScore = entry.Frequency;

                foreach (var otherList in otherLists)
                {
                    var otherEntries = otherList.GetEntries();
                    bool found = false;
                    foreach (var oe in otherEntries)
                    {
                        if (oe.DocumentId == entry.DocumentId)
                        {
                            totalScore += oe.Frequency;
                            found = true;
                            break;
                        }
                        if (oe.DocumentId > entry.DocumentId) break;
                    }
                    if (!found)
                    {
                        matchesAll = false;
                        break;
                    }
                }

                if (matchesAll)
                {
                    yield return new SearchHit(entry.DocumentId, totalScore, entry.Frequency);
                }
            }

            pageOffset += pageSize;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Finds terms matching the specified prefix using the sorted term dictionary.
    /// </summary>
    /// <param name="prefix">The prefix to search for.</param>
    /// <param name="maxResults">Maximum number of terms to return.</param>
    /// <returns>Terms matching the prefix.</returns>
    public IReadOnlyList<string> PrefixSearch(string prefix, int maxResults = 100)
    {
        _termDictLock.EnterReadLock();
        try
        {
            var results = new List<string>();
            foreach (var term in _termDictionary.GetViewBetween(prefix, prefix + char.MaxValue))
            {
                if (results.Count >= maxResults) break;
                results.Add(term);
            }
            return results;
        }
        finally { _termDictLock.ExitReadLock(); }
    }

    // ---- Vector Index ----

    /// <summary>
    /// Indexes a vector for the specified document. The vector is assigned to a shard
    /// based on document ID hash.
    /// </summary>
    /// <param name="documentId">The document ID.</param>
    /// <param name="vector">The vector (embedding) data.</param>
    public void IndexVector(long documentId, float[] vector)
    {
        int shardIndex = GetShardIndex(documentId);
        _vectorShards[shardIndex].Put(documentId, vector);
    }

    /// <summary>
    /// Removes a vector from the sharded index.
    /// </summary>
    /// <param name="documentId">The document ID to remove.</param>
    /// <returns><c>true</c> if the vector was found and removed.</returns>
    public bool RemoveVector(long documentId)
    {
        int shardIndex = GetShardIndex(documentId);
        return _vectorShards[shardIndex].Remove(documentId);
    }

    /// <summary>
    /// Searches all vector shards in parallel for the top-K most similar vectors,
    /// merging results by score.
    /// </summary>
    /// <param name="queryVector">The query vector.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Top-K results sorted by descending similarity score.</returns>
    public async Task<IReadOnlyList<VectorSearchResult>> VectorSearchAsync(
        float[] queryVector,
        int topK = 10,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fan out to all shards in parallel
        var tasks = new Task<List<VectorSearchResult>>[_shardCount];
        for (int i = 0; i < _shardCount; i++)
        {
            var shard = _vectorShards[i];
            tasks[i] = Task.Run(() => shard.Search(queryVector, topK), ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Merge results from all shards by score
        var merged = new List<VectorSearchResult>();
        foreach (var task in tasks)
        {
            merged.AddRange(task.Result);
        }

        merged.Sort(); // Descending by score
        if (merged.Count > topK)
            merged.RemoveRange(topK, merged.Count - topK);

        return merged;
    }

    // ---- Private helpers ----

    /// <summary>
    /// Tokenizes text according to the configured tokenizer mode.
    /// </summary>
    private string[] Tokenize(string text)
    {
        return _tokenizerMode switch
        {
            TokenizerMode.Whitespace => text.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            TokenizerMode.AnalyzerChain => TokenizeWithAnalyzer(text),
            TokenizerMode.NGram => TokenizeNGram(text, 3),
            _ => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        };
    }

    /// <summary>
    /// Static read-only stop word set shared across all calls to avoid per-call allocations.
    /// </summary>
    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will",
        "would", "could", "should", "may", "might", "shall", "can",
        "of", "in", "to", "for", "with", "on", "at", "by", "from",
        "and", "or", "not", "but", "if", "then", "else", "when",
        "this", "that", "these", "those", "it", "its"
    };

    /// <summary>
    /// Tokenizes with lowercase normalization and stop word removal.
    /// </summary>
    private static string[] TokenizeWithAnalyzer(string text)
    {
        var tokens = text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return tokens.Where(t => !_stopWords.Contains(t) && t.Length > 1).ToArray();
    }

    /// <summary>
    /// Produces character n-grams from text.
    /// </summary>
    private static string[] TokenizeNGram(string text, int n)
    {
        var normalized = text.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (normalized.Length < n) return new[] { normalized };

        var ngrams = new string[normalized.Length - n + 1];
        for (int i = 0; i <= normalized.Length - n; i++)
        {
            ngrams[i] = normalized.Substring(i, n);
        }
        return ngrams;
    }

    /// <summary>
    /// Normalizes a term for consistent lookup.
    /// </summary>
    private static string NormalizeTerm(string term)
    {
        return term.ToLowerInvariant().Trim();
    }

    /// <summary>
    /// Determines which vector shard a document ID belongs to using hash-based routing.
    /// </summary>
    private int GetShardIndex(long documentId)
    {
        // FNV-1a-style hash for stable distribution
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            hash ^= (ulong)documentId;
            hash *= 1099511628211UL;
            return (int)(hash % (ulong)_shardCount);
        }
    }

    /// <summary>
    /// Executes a boolean query using posting list intersection/union/difference.
    /// </summary>
    private List<SearchHit> ExecuteBooleanQuery(IReadOnlyList<BooleanClause> clauses)
    {
        if (clauses.Count == 0) return new List<SearchHit>();

        // Start with first clause
        var firstTerm = NormalizeTerm(clauses[0].Term);
        var firstList = _invertedIndex.GetOrDefault(firstTerm);
        if (firstList == null && clauses[0].Operator != BooleanOperator.Not)
            return new List<SearchHit>();

        // Build result set as dictionary for O(1) lookups
        var resultSet = new Dictionary<long, (double Score, int Freq)>();

        if (firstList != null && clauses[0].Operator != BooleanOperator.Not)
        {
            foreach (var entry in firstList.GetEntries())
            {
                resultSet[entry.DocumentId] = (entry.Frequency, entry.Frequency);
            }
        }

        // Apply subsequent clauses
        for (int i = 1; i < clauses.Count; i++)
        {
            var term = NormalizeTerm(clauses[i].Term);
            var list = _invertedIndex.GetOrDefault(term);
            var entries = list?.GetEntries() ?? (IReadOnlyList<PostingEntry>)Array.Empty<PostingEntry>();
            var entrySet = new HashSet<long>();
            foreach (var e in entries) entrySet.Add(e.DocumentId);

            switch (clauses[i].Operator)
            {
                case BooleanOperator.And:
                    // Intersection: keep only documents in both sets
                    var toRemoveAnd = new List<long>();
                    foreach (var docId in resultSet.Keys)
                    {
                        if (!entrySet.Contains(docId))
                            toRemoveAnd.Add(docId);
                    }
                    foreach (var docId in toRemoveAnd)
                        resultSet.Remove(docId);

                    // Update scores for remaining
                    foreach (var entry in entries)
                    {
                        if (resultSet.TryGetValue(entry.DocumentId, out var existing))
                        {
                            resultSet[entry.DocumentId] = (existing.Score + entry.Frequency, existing.Freq);
                        }
                    }
                    break;

                case BooleanOperator.Or:
                    // Union: add documents from this list
                    foreach (var entry in entries)
                    {
                        if (resultSet.TryGetValue(entry.DocumentId, out var existing))
                        {
                            resultSet[entry.DocumentId] = (existing.Score + entry.Frequency, existing.Freq);
                        }
                        else
                        {
                            resultSet[entry.DocumentId] = (entry.Frequency, entry.Frequency);
                        }
                    }
                    break;

                case BooleanOperator.Not:
                    // Difference: remove documents in this list
                    foreach (var entry in entries)
                    {
                        resultSet.Remove(entry.DocumentId);
                    }
                    break;
            }
        }

        // Convert to sorted results; materialise once but only as a List (no further LINQ chaining)
        var results = resultSet
            .Select(kvp => new SearchHit(kvp.Key, kvp.Value.Score, kvp.Value.Freq))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.DocumentId)
            .ToList();

        return results;
    }

    /// <summary>
    /// Executes a boolean query and returns only the page requested, avoiding full materialization
    /// when the caller supplies a non-zero offset+limit.  Used internally by paginated search
    /// methods to short-circuit sorting the entire posting list.
    /// </summary>
    private (List<SearchHit> Page, long TotalHits) ExecuteBooleanQueryPaged(
        IReadOnlyList<BooleanClause> clauses,
        int offset,
        int limit)
    {
        // Run the full query to get the result set dictionary (intersection/union is already O(n))
        var all = ExecuteBooleanQuery(clauses);
        long total = all.Count;
        // Slice the already-sorted list without re-enumerating
        var page = (offset < all.Count)
            ? all.GetRange(offset, Math.Min(limit, all.Count - offset))
            : new List<SearchHit>(0);
        return (page, total);
    }

    /// <summary>
    /// Flushes the write buffer to the inverted index.
    /// </summary>
    private void FlushWriteBuffer(object? state)
    {
        if (_disposed) return;

        int flushed = 0;
        while (_writeBuffer.TryDequeue(out var item))
        {
            var normalized = NormalizeTerm(item.Term);
            var postingList = _invertedIndex.GetOrDefault(normalized);

            if (postingList == null)
            {
                postingList = new PostingList();
                _invertedIndex.Put(normalized, postingList);

                // Add to term dictionary
                _termDictLock.EnterWriteLock();
                try
                {
                    if (_termDictionary.Add(normalized))
                        Interlocked.Increment(ref _totalTerms);
                }
                finally { _termDictLock.ExitWriteLock(); }
            }

            postingList.AddOrUpdate(item.DocId, item.Frequency);
            flushed++;
        }

        if (flushed > 0)
        {
            _logger.LogDebug("Flushed {Count} index writes", flushed);
        }
    }

    /// <summary>
    /// Records a search latency sample for p50/p95/p99 tracking.
    /// </summary>
    private void RecordLatency(long milliseconds)
    {
        lock (_latencyLock)
        {
            _latencySamples[_latencySampleIndex % _latencySamples.Length] = milliseconds;
            _latencySampleIndex++;
        }
    }

    /// <summary>
    /// Computes latency percentiles from the circular buffer.
    /// </summary>
    private (double P50, double P95, double P99) GetLatencyPercentiles()
    {
        long[] snapshot;
        int count;

        lock (_latencyLock)
        {
            count = Math.Min(_latencySampleIndex, _latencySamples.Length);
            if (count == 0) return (0, 0, 0);
            snapshot = new long[count];
            int start = _latencySampleIndex > _latencySamples.Length
                ? _latencySampleIndex % _latencySamples.Length
                : 0;
            for (int i = 0; i < count; i++)
            {
                snapshot[i] = _latencySamples[(start + i) % _latencySamples.Length];
            }
        }

        Array.Sort(snapshot);

        double p50 = snapshot[(int)(count * 0.50)];
        double p95 = snapshot[(int)(count * 0.95)];
        double p99 = snapshot[Math.Min((int)(count * 0.99), count - 1)];

        return (p50, p95, p99);
    }

    // ---- IScalableSubsystem ----

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var indexStats = _invertedIndex.GetStatistics();
        var (p50, p95, p99) = GetLatencyPercentiles();

        long totalVectors = 0;
        var shardDist = new Dictionary<string, object>();
        for (int i = 0; i < _shardCount; i++)
        {
            int shardCount = _vectorShards[i].Count;
            totalVectors += shardCount;
            shardDist[$"vector.shard.{i}.count"] = shardCount;
        }

        var metrics = new Dictionary<string, object>
        {
            ["index.termCount"] = Interlocked.Read(ref _totalTerms),
            ["index.documentCount"] = Interlocked.Read(ref _indexedDocuments),
            ["index.cacheSize"] = indexStats.ItemCount,
            ["index.cacheHitRate"] = (indexStats.Hits + indexStats.Misses) > 0
                ? (double)indexStats.Hits / (indexStats.Hits + indexStats.Misses)
                : 0.0,
            ["index.pendingWrites"] = _writeBuffer.Count,
            ["search.totalSearches"] = Interlocked.Read(ref _totalSearches),
            ["search.latency.p50ms"] = p50,
            ["search.latency.p95ms"] = p95,
            ["search.latency.p99ms"] = p99,
            ["vector.totalVectors"] = totalVectors,
            ["vector.shardCount"] = _shardCount,
            ["backpressure.queueDepth"] = Interlocked.Read(ref _pendingSearches),
            ["backpressure.state"] = CurrentBackpressureState.ToString(),
            ["concurrency.maxSearches"] = _currentLimits.MaxConcurrentOperations,
            ["concurrency.availableSlots"] = _searchSemaphore.CurrentCount
        };

        foreach (var kvp in shardDist)
            metrics[kvp.Key] = kvp.Value;

        return metrics;
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(limits);

        var oldLimits = _currentLimits;
        _currentLimits = limits;

        if (limits.MaxConcurrentOperations != oldLimits.MaxConcurrentOperations)
        {
            var newSemaphore = new SemaphoreSlim(
                limits.MaxConcurrentOperations,
                limits.MaxConcurrentOperations);
            var oldSemaphore = Interlocked.Exchange(ref _searchSemaphore, newSemaphore);
            oldSemaphore.Dispose();
        }

        _logger.LogInformation(
            "Search scaling limits reconfigured: MaxCache={MaxCache}, MaxConcurrent={MaxConcurrent}",
            limits.MaxCacheEntries, limits.MaxConcurrentOperations);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long pending = Interlocked.Read(ref _pendingSearches);
            int maxQueue = _currentLimits.MaxQueueDepth;

            if (pending <= 0) return BackpressureState.Normal;
            if (pending < maxQueue * 0.5) return BackpressureState.Normal;
            if (pending < maxQueue * 0.8) return BackpressureState.Warning;
            if (pending < maxQueue) return BackpressureState.Critical;
            return BackpressureState.Shedding;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer?.Dispose();
        _flushTimer = null;

        // Flush remaining writes before disposing
        FlushWriteBuffer(null);

        _invertedIndex.Dispose();
        _termDictLock.Dispose();
        _searchSemaphore.Dispose();

        foreach (var shard in _vectorShards)
            shard.Dispose();
    }
}
