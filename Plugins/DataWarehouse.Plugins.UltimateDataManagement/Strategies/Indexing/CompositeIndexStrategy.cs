using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Multi-field compound index strategy with prefix matching and covering indexes.
/// Provides efficient queries on multiple fields with configurable key composition.
/// </summary>
/// <remarks>
/// Features:
/// - Multiple field indexing with configurable field ordering
/// - Prefix matching for partial queries (leftmost prefix rule)
/// - Covering indexes for index-only queries
/// - Index intersection for complex queries
/// - Configurable index key composition
/// - Statistics tracking for query optimization
/// - Support for both equality and range predicates
/// </remarks>
public sealed class CompositeIndexStrategy : IndexingStrategyBase
{
    /// <summary>
    /// Document storage by object ID.
    /// </summary>
    private readonly BoundedDictionary<string, IndexedDocument> _documents = new BoundedDictionary<string, IndexedDocument>(1000);

    /// <summary>
    /// Composite indexes: indexName -> CompositeIndex.
    /// </summary>
    private readonly BoundedDictionary<string, CompositeIndex> _indexes = new BoundedDictionary<string, CompositeIndex>(1000);

    /// <summary>
    /// Default index used when no specific index is specified.
    /// </summary>
    private readonly List<string> _defaultIndexFields;

    /// <summary>
    /// Statistics for query optimization.
    /// </summary>
    private readonly BoundedDictionary<string, IndexStatistics> _indexStatistics = new BoundedDictionary<string, IndexStatistics>(1000);

    /// <summary>
    /// Represents an indexed document with its field values.
    /// </summary>
    private sealed class IndexedDocument
    {
        public required string ObjectId { get; init; }
        public required Dictionary<string, object?> FieldValues { get; init; }
        public required string? TextContent { get; init; }
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a composite index on multiple fields.
    /// </summary>
    public sealed class CompositeIndex
    {
        /// <summary>Gets the index name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the ordered list of fields in this index.</summary>
        public required List<IndexField> Fields { get; init; }

        /// <summary>Gets whether this is a unique index.</summary>
        public bool IsUnique { get; init; }

        /// <summary>Gets whether this is a covering index (stores all document fields).</summary>
        public bool IsCovering { get; init; }

        /// <summary>The B-tree index structure: composite key -> document IDs.</summary>
        internal SortedDictionary<CompositeKey, HashSet<string>> Tree { get; } = new();

        /// <summary>Lock for tree modifications.</summary>
        internal object TreeLock { get; } = new();
    }

    /// <summary>
    /// Represents a field in a composite index.
    /// </summary>
    public sealed class IndexField
    {
        /// <summary>Gets the field name.</summary>
        public required string Name { get; init; }

        /// <summary>Gets the sort order for this field.</summary>
        public SortOrder Order { get; init; } = SortOrder.Ascending;

        /// <summary>Gets whether null values should be indexed.</summary>
        public bool IndexNulls { get; init; } = true;
    }

    /// <summary>
    /// Defines the sort order for an index field.
    /// </summary>
    public enum SortOrder
    {
        /// <summary>Ascending order.</summary>
        Ascending,
        /// <summary>Descending order.</summary>
        Descending
    }

    /// <summary>
    /// Represents a composite key for the index.
    /// </summary>
    public sealed class CompositeKey : IComparable<CompositeKey>
    {
        /// <summary>Gets the key values in field order.</summary>
        public object?[] Values { get; }

        /// <summary>Gets the sort orders for each field.</summary>
        public SortOrder[] SortOrders { get; }

        /// <summary>
        /// Initializes a new CompositeKey.
        /// </summary>
        /// <param name="values">The key values.</param>
        /// <param name="sortOrders">The sort orders for each field.</param>
        public CompositeKey(object?[] values, SortOrder[]? sortOrders = null)
        {
            Values = values;
            SortOrders = sortOrders ?? values.Select(_ => SortOrder.Ascending).ToArray();
        }

        /// <inheritdoc/>
        public int CompareTo(CompositeKey? other)
        {
            if (other == null) return 1;

            var minLen = Math.Min(Values.Length, other.Values.Length);

            for (int i = 0; i < minLen; i++)
            {
                var cmp = CompareValues(Values[i], other.Values[i]);
                if (cmp != 0)
                {
                    return SortOrders[i] == SortOrder.Descending ? -cmp : cmp;
                }
            }

            return Values.Length.CompareTo(other.Values.Length);
        }

        /// <summary>
        /// Checks if this key is a prefix of another key.
        /// </summary>
        public bool IsPrefixOf(CompositeKey other)
        {
            if (Values.Length > other.Values.Length)
                return false;

            for (int i = 0; i < Values.Length; i++)
            {
                if (!ValuesEqual(Values[i], other.Values[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if this key matches a prefix pattern with wildcards.
        /// </summary>
        public bool MatchesPrefix(CompositeKey prefix)
        {
            if (prefix.Values.Length > Values.Length)
                return false;

            for (int i = 0; i < prefix.Values.Length; i++)
            {
                // Null in prefix means "any value"
                if (prefix.Values[i] == null)
                    continue;

                if (!ValuesEqual(Values[i], prefix.Values[i]))
                    return false;
            }

            return true;
        }

        private static int CompareValues(object? a, object? b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            if (a is IComparable ca && b is IComparable)
            {
                try
                {
                    return ca.CompareTo(b);
                }
                catch (InvalidCastException)
                {
                    // P2-2437: Type mismatch between comparable types — fall through to string comparison.
                    // Only InvalidCastException is expected here; other exceptions propagate.
                }
            }

            return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValuesEqual(object? a, object? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a.Equals(b)) return true;

            // String comparison fallback
            return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            if (obj is not CompositeKey other) return false;
            if (Values.Length != other.Values.Length) return false;

            for (int i = 0; i < Values.Length; i++)
            {
                if (!ValuesEqual(Values[i], other.Values[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var v in Values)
            {
                hash.Add(v?.ToString()?.ToLowerInvariant());
            }
            return hash.ToHashCode();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"[{string.Join(", ", Values.Select(v => v?.ToString() ?? "null"))}]";
        }
    }

    /// <summary>
    /// Statistics for an index used in query optimization.
    /// </summary>
    public sealed class IndexStatistics
    {
        private long _queryCount;
        private long _scanCount;

        /// <summary>Gets the index name.</summary>
        public required string IndexName { get; init; }

        /// <summary>Gets the number of entries in the index.</summary>
        public long EntryCount { get; set; }

        /// <summary>Gets the number of unique keys.</summary>
        public long UniqueKeyCount { get; set; }

        /// <summary>Gets the average entries per key.</summary>
        public double AverageEntriesPerKey => UniqueKeyCount > 0 ? (double)EntryCount / UniqueKeyCount : 0;

        /// <summary>Gets the selectivity (0-1, lower is more selective).</summary>
        public double Selectivity => EntryCount > 0 ? (double)UniqueKeyCount / EntryCount : 1;

        /// <summary>Gets the total query count using this index.</summary>
        public long QueryCount { get => _queryCount; set => _queryCount = value; }

        /// <summary>Gets the total scan count (entries examined).</summary>
        public long ScanCount { get => _scanCount; set => _scanCount = value; }

        /// <summary>Gets the average scan efficiency.</summary>
        public double AverageScanEfficiency => QueryCount > 0 ? (double)ScanCount / QueryCount : 0;

        /// <summary>Gets the last updated timestamp.</summary>
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Increments the query count atomically.
        /// </summary>
        public void IncrementQueryCount() => Interlocked.Increment(ref _queryCount);

        /// <summary>
        /// Adds to the scan count atomically.
        /// </summary>
        /// <param name="value">Value to add.</param>
        public void AddScanCount(long value) => Interlocked.Add(ref _scanCount, value);
    }

    /// <summary>
    /// Initializes a new CompositeIndexStrategy with default configuration.
    /// </summary>
    public CompositeIndexStrategy() : this(new[] { "_id", "_type" }) { }

    /// <summary>
    /// Initializes a new CompositeIndexStrategy with specified default index fields.
    /// </summary>
    /// <param name="defaultIndexFields">Fields for the default composite index.</param>
    public CompositeIndexStrategy(IEnumerable<string> defaultIndexFields)
    {
        _defaultIndexFields = defaultIndexFields.ToList();

        // Create default index
        CreateIndex("_default", _defaultIndexFields.Select(f => new IndexField { Name = f }).ToList());
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.composite";

    /// <inheritdoc/>
    public override string DisplayName => "Composite Index";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.3
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Multi-field compound index with prefix matching and covering index support. " +
        "Enables efficient queries on multiple fields with configurable key composition. " +
        "Best for complex queries requiring multiple field filters.";

    /// <inheritdoc/>
    public override string[] Tags => ["index", "composite", "compound", "multifield", "covering", "prefix"];

    /// <inheritdoc/>
    public override long GetDocumentCount() => _documents.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        long size = 0;

        foreach (var index in _indexes.Values)
        {
            lock (index.TreeLock)
            {
                size += index.Tree.Count * 64; // Key overhead
                foreach (var set in index.Tree.Values)
                {
                    size += set.Count * 40; // Document ID references
                }
            }
        }

        return size;
    }

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default)
    {
        return Task.FromResult(_documents.ContainsKey(objectId));
    }

    /// <inheritdoc/>
    public override Task ClearAsync(CancellationToken ct = default)
    {
        _documents.Clear();

        foreach (var index in _indexes.Values)
        {
            lock (index.TreeLock)
            {
                index.Tree.Clear();
            }
        }

        foreach (var stats in _indexStatistics.Values)
        {
            stats.EntryCount = 0;
            stats.UniqueKeyCount = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a new composite index.
    /// </summary>
    /// <param name="name">Index name.</param>
    /// <param name="fields">Ordered list of fields to index.</param>
    /// <param name="isUnique">Whether duplicate keys are allowed.</param>
    /// <param name="isCovering">Whether to store all document fields in the index.</param>
    /// <returns>The created index.</returns>
    public CompositeIndex CreateIndex(string name, List<IndexField> fields, bool isUnique = false, bool isCovering = false)
    {
        var index = new CompositeIndex
        {
            Name = name,
            Fields = fields,
            IsUnique = isUnique,
            IsCovering = isCovering
        };

        _indexes[name] = index;
        _indexStatistics[name] = new IndexStatistics { IndexName = name };

        // Index existing documents
        foreach (var doc in _documents.Values)
        {
            AddToIndex(index, doc);
        }

        UpdateStatistics(name);
        return index;
    }

    /// <summary>
    /// Creates a composite index from field names with default settings.
    /// </summary>
    /// <param name="name">Index name.</param>
    /// <param name="fieldNames">Ordered field names.</param>
    /// <returns>The created index.</returns>
    public CompositeIndex CreateIndex(string name, params string[] fieldNames)
    {
        var fields = fieldNames.Select(f =>
        {
            var order = SortOrder.Ascending;
            var fieldName = f;

            if (f.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase))
            {
                order = SortOrder.Descending;
                fieldName = f[..^5].Trim();
            }
            else if (f.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase))
            {
                fieldName = f[..^4].Trim();
            }

            return new IndexField { Name = fieldName, Order = order };
        }).ToList();

        return CreateIndex(name, fields);
    }

    /// <summary>
    /// Drops an index.
    /// </summary>
    /// <param name="name">Index name to drop.</param>
    /// <returns>True if index was dropped.</returns>
    public bool DropIndex(string name)
    {
        if (name == "_default")
            return false; // Cannot drop default index

        var removed = _indexes.TryRemove(name, out _);
        _indexStatistics.TryRemove(name, out _);
        return removed;
    }

    /// <summary>
    /// Gets all index names.
    /// </summary>
    public IReadOnlyList<string> GetIndexNames() => _indexes.Keys.ToList();

    /// <summary>
    /// Gets statistics for an index.
    /// </summary>
    /// <param name="indexName">Index name.</param>
    /// <returns>Statistics or null if index not found.</returns>
    public IndexStatistics? GetIndexStatistics(string indexName)
    {
        return _indexStatistics.TryGetValue(indexName, out var stats) ? stats : null;
    }

    /// <summary>
    /// Gets all index statistics.
    /// </summary>
    public IReadOnlyDictionary<string, IndexStatistics> GetAllStatistics()
    {
        return _indexStatistics.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <inheritdoc/>
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Extract field values
        var fieldValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["_id"] = objectId
        };

        if (content.Metadata != null)
        {
            foreach (var (key, value) in content.Metadata)
            {
                fieldValues[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(content.Filename))
            fieldValues["_filename"] = content.Filename;
        if (!string.IsNullOrEmpty(content.ContentType))
            fieldValues["_contentType"] = content.ContentType;
        if (content.Size.HasValue)
            fieldValues["_size"] = content.Size.Value;

        // Remove existing document if re-indexing
        if (_documents.TryRemove(objectId, out var existingDoc))
        {
            RemoveFromAllIndexes(existingDoc);
        }

        var doc = new IndexedDocument
        {
            ObjectId = objectId,
            FieldValues = fieldValues,
            TextContent = content.TextContent
        };

        _documents[objectId] = doc;

        // Add to all indexes
        foreach (var index in _indexes.Values)
        {
            AddToIndex(index, doc);
        }

        sw.Stop();
        return Task.FromResult(IndexResult.Ok(1, fieldValues.Count, sw.Elapsed));
    }

    /// <summary>
    /// Adds a document to an index.
    /// </summary>
    private void AddToIndex(CompositeIndex index, IndexedDocument doc)
    {
        var keyValues = new object?[index.Fields.Count];
        var sortOrders = new SortOrder[index.Fields.Count];

        for (int i = 0; i < index.Fields.Count; i++)
        {
            var field = index.Fields[i];
            doc.FieldValues.TryGetValue(field.Name, out var value);

            // Skip if null and not indexing nulls
            if (value == null && !field.IndexNulls)
                return;

            keyValues[i] = value;
            sortOrders[i] = field.Order;
        }

        var key = new CompositeKey(keyValues, sortOrders);

        lock (index.TreeLock)
        {
            if (!index.Tree.TryGetValue(key, out var docIds))
            {
                docIds = new HashSet<string>();
                index.Tree[key] = docIds;
            }

            if (index.IsUnique && docIds.Count > 0 && !docIds.Contains(doc.ObjectId))
            {
                throw new InvalidOperationException($"Duplicate key violation in unique index '{index.Name}'");
            }

            docIds.Add(doc.ObjectId);
        }
    }

    /// <summary>
    /// Removes a document from all indexes.
    /// </summary>
    private void RemoveFromAllIndexes(IndexedDocument doc)
    {
        foreach (var index in _indexes.Values)
        {
            RemoveFromIndex(index, doc);
        }
    }

    /// <summary>
    /// Removes a document from an index.
    /// </summary>
    private void RemoveFromIndex(CompositeIndex index, IndexedDocument doc)
    {
        var keyValues = new object?[index.Fields.Count];
        var sortOrders = new SortOrder[index.Fields.Count];

        for (int i = 0; i < index.Fields.Count; i++)
        {
            var field = index.Fields[i];
            doc.FieldValues.TryGetValue(field.Name, out var value);
            keyValues[i] = value;
            sortOrders[i] = field.Order;
        }

        var key = new CompositeKey(keyValues, sortOrders);

        lock (index.TreeLock)
        {
            if (index.Tree.TryGetValue(key, out var docIds))
            {
                docIds.Remove(doc.ObjectId);
                if (docIds.Count == 0)
                {
                    index.Tree.Remove(key);
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        var queryParams = ParseSearchQuery(query);
        var results = new List<IndexSearchResult>();

        // Determine which index to use
        var indexName = "_default";
        if (queryParams.TryGetValue("_index", out var specifiedIndex))
        {
            indexName = specifiedIndex;
            queryParams.Remove("_index");
        }
        else
        {
            // Find best index for query
            indexName = SelectBestIndex(queryParams);
        }

        if (!_indexes.TryGetValue(indexName, out var index))
        {
            index = _indexes["_default"];
            indexName = "_default";
        }

        // Build query key prefix
        var searchResults = SearchWithIndex(index, queryParams, options);

        // Apply additional filters not covered by index
        var filteredResults = ApplyAdditionalFilters(searchResults, queryParams, options);

        // Track statistics
        if (_indexStatistics.TryGetValue(indexName, out var stats))
        {
            stats.IncrementQueryCount();
            stats.AddScanCount(filteredResults.Count);
        }

        return Task.FromResult<IReadOnlyList<IndexSearchResult>>(filteredResults);
    }

    /// <summary>
    /// Selects the best index for a query based on field coverage and selectivity.
    /// </summary>
    private string SelectBestIndex(Dictionary<string, string> queryParams)
    {
        var queryFields = queryParams.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string bestIndex = "_default";
        int bestScore = -1;
        double bestSelectivity = double.MaxValue;

        foreach (var (indexName, index) in _indexes)
        {
            // Calculate prefix match score
            int prefixMatch = 0;
            foreach (var field in index.Fields)
            {
                if (queryFields.Contains(field.Name))
                    prefixMatch++;
                else
                    break; // Prefix rule: stop at first non-matching field
            }

            if (prefixMatch > bestScore)
            {
                bestScore = prefixMatch;
                bestIndex = indexName;

                if (_indexStatistics.TryGetValue(indexName, out var stats))
                {
                    bestSelectivity = stats.Selectivity;
                }
            }
            else if (prefixMatch == bestScore && _indexStatistics.TryGetValue(indexName, out var stats))
            {
                // Prefer more selective index
                if (stats.Selectivity < bestSelectivity)
                {
                    bestIndex = indexName;
                    bestSelectivity = stats.Selectivity;
                }
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Searches using a specific index.
    /// </summary>
    private List<IndexedDocument> SearchWithIndex(CompositeIndex index, Dictionary<string, string> queryParams, IndexSearchOptions options)
    {
        var results = new List<IndexedDocument>();

        // Build prefix key from query params that match index fields
        var prefixValues = new List<object?>();
        var sortOrders = new List<SortOrder>();
        bool hasRange = false;

        foreach (var field in index.Fields)
        {
            if (queryParams.TryGetValue(field.Name, out var valueStr))
            {
                // Check for range query
                if (valueStr.Contains("..") || valueStr.StartsWith(">") || valueStr.StartsWith("<"))
                {
                    hasRange = true;
                    break;
                }

                prefixValues.Add(ParseValue(valueStr));
                sortOrders.Add(field.Order);
            }
            else
            {
                break; // Prefix rule: stop at first missing field
            }
        }

        lock (index.TreeLock)
        {
            if (prefixValues.Count == 0)
            {
                // No prefix match - scan all
                foreach (var docIds in index.Tree.Values)
                {
                    foreach (var docId in docIds)
                    {
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            results.Add(doc);
                        }
                    }
                }
            }
            else if (hasRange)
            {
                // Range query - find matching range in tree
                results = SearchRangeInIndex(index, queryParams);
            }
            else
            {
                // Prefix match
                var prefix = new CompositeKey(prefixValues.ToArray(), sortOrders.ToArray());

                foreach (var (key, docIds) in index.Tree)
                {
                    if (key.MatchesPrefix(prefix))
                    {
                        foreach (var docId in docIds)
                        {
                            if (_documents.TryGetValue(docId, out var doc))
                            {
                                results.Add(doc);
                            }
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Searches for a range within an index.
    /// </summary>
    private List<IndexedDocument> SearchRangeInIndex(CompositeIndex index, Dictionary<string, string> queryParams)
    {
        var results = new List<IndexedDocument>();

        // Parse range bounds
        object? minBound = null;
        object? maxBound = null;
        bool minInclusive = true;
        bool maxInclusive = true;
        string? rangeField = null;

        foreach (var field in index.Fields)
        {
            if (queryParams.TryGetValue(field.Name, out var valueStr))
            {
                if (valueStr.Contains(".."))
                {
                    var parts = valueStr.Split("..");
                    minBound = ParseValue(parts[0]);
                    maxBound = ParseValue(parts[1]);
                    rangeField = field.Name;
                    break;
                }
                else if (valueStr.StartsWith(">="))
                {
                    minBound = ParseValue(valueStr[2..]);
                    rangeField = field.Name;
                }
                else if (valueStr.StartsWith(">"))
                {
                    minBound = ParseValue(valueStr[1..]);
                    minInclusive = false;
                    rangeField = field.Name;
                }
                else if (valueStr.StartsWith("<="))
                {
                    maxBound = ParseValue(valueStr[2..]);
                    rangeField = field.Name;
                }
                else if (valueStr.StartsWith("<"))
                {
                    maxBound = ParseValue(valueStr[1..]);
                    maxInclusive = false;
                    rangeField = field.Name;
                }
            }
        }

        if (rangeField == null)
            return results;

        // Scan index for matching range
        var fieldIndex = index.Fields.FindIndex(f => f.Name.Equals(rangeField, StringComparison.OrdinalIgnoreCase));
        if (fieldIndex < 0)
            return results;

        foreach (var (key, docIds) in index.Tree)
        {
            if (key.Values.Length <= fieldIndex)
                continue;

            var keyValue = key.Values[fieldIndex];
            if (keyValue == null)
                continue;

            bool inRange = true;

            if (minBound != null)
            {
                var cmp = CompareForRange(keyValue, minBound);
                inRange = minInclusive ? cmp >= 0 : cmp > 0;
            }

            if (inRange && maxBound != null)
            {
                var cmp = CompareForRange(keyValue, maxBound);
                inRange = maxInclusive ? cmp <= 0 : cmp < 0;
            }

            if (inRange)
            {
                foreach (var docId in docIds)
                {
                    if (_documents.TryGetValue(docId, out var doc))
                    {
                        results.Add(doc);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Compares two values for range queries.
    /// </summary>
    private static int CompareForRange(object a, object b)
    {
        if (a is IComparable ca)
        {
            try
            {
                // Try to convert to same type
                if (a.GetType() != b.GetType())
                {
                    if (a is double || b is double)
                    {
                        return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
                    }
                    if (a is long || b is long || a is int || b is int)
                    {
                        return Convert.ToInt64(a).CompareTo(Convert.ToInt64(b));
                    }
                }

                return ca.CompareTo(b);
            }
            catch
            {

                // Fall back to string comparison
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies additional filters not covered by index.
    /// </summary>
    private List<IndexSearchResult> ApplyAdditionalFilters(List<IndexedDocument> candidates, Dictionary<string, string> queryParams, IndexSearchOptions options)
    {
        IEnumerable<IndexedDocument> filtered = candidates;

        // Get index fields used (to skip re-checking)
        var usedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Apply remaining filters
        foreach (var (fieldName, valueStr) in queryParams)
        {
            if (usedFields.Contains(fieldName))
                continue;

            // Range filters
            if (valueStr.Contains(".."))
            {
                var parts = valueStr.Split("..");
                var min = ParseValue(parts[0]);
                var max = ParseValue(parts[1]);

                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val) || val == null)
                        return false;

                    return CompareForRange(val, min!) >= 0 && CompareForRange(val, max!) <= 0;
                });
            }
            else if (valueStr.StartsWith(">="))
            {
                var bound = ParseValue(valueStr[2..]);
                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val) || val == null)
                        return false;
                    return CompareForRange(val, bound!) >= 0;
                });
            }
            else if (valueStr.StartsWith(">"))
            {
                var bound = ParseValue(valueStr[1..]);
                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val) || val == null)
                        return false;
                    return CompareForRange(val, bound!) > 0;
                });
            }
            else if (valueStr.StartsWith("<="))
            {
                var bound = ParseValue(valueStr[2..]);
                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val) || val == null)
                        return false;
                    return CompareForRange(val, bound!) <= 0;
                });
            }
            else if (valueStr.StartsWith("<"))
            {
                var bound = ParseValue(valueStr[1..]);
                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val) || val == null)
                        return false;
                    return CompareForRange(val, bound!) < 0;
                });
            }
            else if (valueStr.EndsWith("*"))
            {
                // Prefix match
                var prefix = valueStr[..^1];
                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val) || val == null)
                        return false;
                    return val.ToString()?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
                });
            }
            else
            {
                // Exact match
                var target = ParseValue(valueStr);
                filtered = filtered.Where(doc =>
                {
                    if (!doc.FieldValues.TryGetValue(fieldName, out var val))
                        return false;
                    if (val == null && target == null) return true;
                    if (val == null || target == null) return false;
                    return val.ToString()?.Equals(target.ToString(), StringComparison.OrdinalIgnoreCase) == true;
                });
            }
        }

        // Convert to results
        return filtered
            .Take(options.MaxResults)
            .Select(doc => CreateSearchResult(doc))
            .Where(r => r.Score >= options.MinScore)
            .ToList();
    }

    /// <summary>
    /// Performs index intersection for complex queries.
    /// </summary>
    /// <param name="indexNames">Names of indexes to intersect.</param>
    /// <param name="queryParams">Query parameters.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Intersected results.</returns>
    public List<IndexSearchResult> SearchWithIntersection(IEnumerable<string> indexNames, Dictionary<string, string> queryParams, IndexSearchOptions options)
    {
        HashSet<string>? resultIds = null;

        foreach (var indexName in indexNames)
        {
            if (!_indexes.TryGetValue(indexName, out var index))
                continue;

            var indexResults = SearchWithIndex(index, queryParams, options);
            var indexDocIds = indexResults.Select(d => d.ObjectId).ToHashSet();

            if (resultIds == null)
            {
                resultIds = indexDocIds;
            }
            else
            {
                resultIds.IntersectWith(indexDocIds);
            }

            if (resultIds.Count == 0)
                break;
        }

        if (resultIds == null || resultIds.Count == 0)
            return new List<IndexSearchResult>();

        return resultIds
            .Where(id => _documents.ContainsKey(id))
            .Select(id => _documents[id])
            .Take(options.MaxResults)
            .Select(doc => CreateSearchResult(doc))
            .Where(r => r.Score >= options.MinScore)
            .ToList();
    }

    /// <summary>
    /// Creates a search result from a document.
    /// </summary>
    private static IndexSearchResult CreateSearchResult(IndexedDocument doc)
    {
        var metadata = new Dictionary<string, object>();
        foreach (var (key, value) in doc.FieldValues)
        {
            if (value != null)
                metadata[key] = value;
        }
        metadata["_indexedAt"] = doc.IndexedAt;

        return new IndexSearchResult
        {
            ObjectId = doc.ObjectId,
            Score = 1.0,
            Snippet = doc.TextContent ?? $"Fields: {string.Join(", ", doc.FieldValues.Keys)}",
            Metadata = metadata
        };
    }

    /// <summary>
    /// Updates statistics for an index.
    /// </summary>
    private void UpdateStatistics(string indexName)
    {
        if (!_indexes.TryGetValue(indexName, out var index))
            return;
        if (!_indexStatistics.TryGetValue(indexName, out var stats))
            return;

        lock (index.TreeLock)
        {
            stats.UniqueKeyCount = index.Tree.Count;
            stats.EntryCount = index.Tree.Values.Sum(s => s.Count);
            stats.LastUpdated = DateTime.UtcNow;
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        if (!_documents.TryRemove(objectId, out var doc))
            return Task.FromResult(false);

        RemoveFromAllIndexes(doc);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public override Task OptimizeAsync(CancellationToken ct = default)
    {
        // Update all index statistics
        foreach (var indexName in _indexes.Keys)
        {
            UpdateStatistics(indexName);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses a value string into appropriate type.
    /// </summary>
    private static object? ParseValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;

        // Try numeric
        if (long.TryParse(value, out var longVal))
            return longVal;
        if (double.TryParse(value, out var doubleVal))
            return doubleVal;

        // Try boolean
        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        // Try datetime
        if (DateTime.TryParse(value, out var dateVal))
            return dateVal;

        return value;
    }

    /// <summary>
    /// Parses a search query string into parameters.
    /// </summary>
    private static Dictionary<string, string> ParseSearchQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parts = query.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex > 0 && colonIndex < part.Length - 1)
            {
                var key = part[..colonIndex];
                var value = part[(colonIndex + 1)..];
                result[key] = value;
            }
            else if (part.Contains('='))
            {
                var eqIndex = part.IndexOf('=');
                if (eqIndex > 0 && eqIndex < part.Length - 1)
                {
                    var key = part[..eqIndex];
                    var value = part[(eqIndex + 1)..];
                    result[key] = value;
                }
            }
        }

        return result;
    }
}
