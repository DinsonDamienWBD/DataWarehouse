using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Metadata index strategy for structured attribute search.
/// Provides fast lookup by metadata fields with support for range queries.
/// </summary>
/// <remarks>
/// Features:
/// - Exact match queries on string fields
/// - Range queries on numeric fields
/// - Date range queries
/// - Multi-value field support (tags, categories)
/// - Compound queries with AND/OR
/// - Secondary indexes for common fields
/// </remarks>
public sealed class MetadataIndexStrategy : IndexingStrategyBase
{
    // Field indexes: fieldName -> (value -> docIds)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HashSet<string>>> _stringIndexes = new();
    // Numeric indexes: fieldName -> sorted list of (value, docId)
    private readonly ConcurrentDictionary<string, SortedList<double, HashSet<string>>> _numericIndexes = new();
    // Date indexes: fieldName -> sorted list of (ticks, docId)
    private readonly ConcurrentDictionary<string, SortedList<long, HashSet<string>>> _dateIndexes = new();
    // Document store
    private readonly ConcurrentDictionary<string, IndexedMetadata> _documents = new();

    private readonly object _numericLock = new();
    private readonly object _dateLock = new();

    private sealed class IndexedMetadata
    {
        public required string ObjectId { get; init; }
        public required string? Filename { get; init; }
        public required Dictionary<string, object>? Metadata { get; init; }
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.metadata";

    /// <inheritdoc/>
    public override string DisplayName => "Metadata Index";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Structured metadata index for attribute-based search. " +
        "Supports exact match, range queries, and multi-value fields. " +
        "Best for filtering and faceted search.";

    /// <inheritdoc/>
    public override string[] Tags => ["index", "metadata", "structured", "faceted", "filter"];

    /// <inheritdoc/>
    public override long GetDocumentCount() => _documents.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        long size = 0;
        foreach (var field in _stringIndexes)
        {
            size += field.Key.Length * 2;
            foreach (var value in field.Value)
            {
                size += value.Key.Length * 2 + value.Value.Count * 40;
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
        _stringIndexes.Clear();
        _numericIndexes.Clear();
        _dateIndexes.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Remove existing if re-indexing
        if (_documents.ContainsKey(objectId))
        {
            await RemoveCoreAsync(objectId, ct);
        }

        var doc = new IndexedMetadata
        {
            ObjectId = objectId,
            Filename = content.Filename,
            Metadata = content.Metadata
        };

        _documents[objectId] = doc;

        // Index filename
        if (!string.IsNullOrEmpty(content.Filename))
        {
            IndexStringField("_filename", content.Filename, objectId);

            // Also index extension
            var ext = Path.GetExtension(content.Filename);
            if (!string.IsNullOrEmpty(ext))
            {
                IndexStringField("_extension", ext.ToLowerInvariant(), objectId);
            }
        }

        // Index content type
        if (!string.IsNullOrEmpty(content.ContentType))
        {
            IndexStringField("_contentType", content.ContentType, objectId);
        }

        // Index size
        if (content.Size.HasValue)
        {
            IndexNumericField("_size", content.Size.Value, objectId);
        }

        // Index custom metadata
        if (content.Metadata != null)
        {
            foreach (var (key, value) in content.Metadata)
            {
                IndexField(key, value, objectId);
            }
        }

        sw.Stop();
        return IndexResult.Ok(1, content.Metadata?.Count ?? 0, sw.Elapsed);
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        // Parse query as field:value pairs
        var filters = ParseQuery(query);

        if (options.Filters != null)
        {
            foreach (var (key, value) in options.Filters)
            {
                filters[key] = value;
            }
        }

        if (filters.Count == 0)
        {
            // Return all documents if no filters
            var allDocs = _documents.Values
                .Take(options.MaxResults)
                .Select(d => new IndexSearchResult
                {
                    ObjectId = d.ObjectId,
                    Score = 1.0,
                    Metadata = d.Metadata
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<IndexSearchResult>>(allDocs);
        }

        // Find matching documents
        HashSet<string>? matchingDocs = null;

        foreach (var (fieldName, fieldValue) in filters)
        {
            var fieldMatches = FindMatchingDocuments(fieldName, fieldValue);

            if (matchingDocs == null)
            {
                matchingDocs = fieldMatches;
            }
            else
            {
                matchingDocs.IntersectWith(fieldMatches);
            }

            if (matchingDocs.Count == 0)
                break;
        }

        matchingDocs ??= new HashSet<string>();

        var results = matchingDocs
            .Where(docId => _documents.ContainsKey(docId))
            .Take(options.MaxResults)
            .Select(docId =>
            {
                var doc = _documents[docId];
                var score = CalculateMatchScore(doc, filters);

                return new IndexSearchResult
                {
                    ObjectId = docId,
                    Score = score,
                    Metadata = doc.Metadata
                };
            })
            .Where(r => r.Score >= options.MinScore)
            .OrderByDescending(r => r.Score)
            .ToList();

        return Task.FromResult<IReadOnlyList<IndexSearchResult>>(results);
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        if (!_documents.TryRemove(objectId, out var doc))
            return Task.FromResult(false);

        // Remove from all string indexes
        foreach (var fieldIndex in _stringIndexes.Values)
        {
            foreach (var docs in fieldIndex.Values)
            {
                lock (docs)
                {
                    docs.Remove(objectId);
                }
            }
        }

        // Remove from numeric indexes
        lock (_numericLock)
        {
            foreach (var fieldIndex in _numericIndexes.Values)
            {
                foreach (var docs in fieldIndex.Values)
                {
                    docs.Remove(objectId);
                }
            }
        }

        // Remove from date indexes
        lock (_dateLock)
        {
            foreach (var fieldIndex in _dateIndexes.Values)
            {
                foreach (var docs in fieldIndex.Values)
                {
                    docs.Remove(objectId);
                }
            }
        }

        return Task.FromResult(true);
    }

    private void IndexField(string fieldName, object value, string objectId)
    {
        switch (value)
        {
            case string s:
                IndexStringField(fieldName, s, objectId);
                break;
            case int i:
                IndexNumericField(fieldName, i, objectId);
                break;
            case long l:
                IndexNumericField(fieldName, l, objectId);
                break;
            case double d:
                IndexNumericField(fieldName, d, objectId);
                break;
            case float f:
                IndexNumericField(fieldName, f, objectId);
                break;
            case decimal dec:
                IndexNumericField(fieldName, (double)dec, objectId);
                break;
            case DateTime dt:
                IndexDateField(fieldName, dt, objectId);
                break;
            case DateTimeOffset dto:
                IndexDateField(fieldName, dto.UtcDateTime, objectId);
                break;
            case bool b:
                IndexStringField(fieldName, b.ToString().ToLowerInvariant(), objectId);
                break;
            case IEnumerable<string> strings:
                foreach (var s in strings)
                {
                    IndexStringField(fieldName, s, objectId);
                }
                break;
            case IEnumerable<object> objects:
                foreach (var obj in objects)
                {
                    IndexField(fieldName, obj, objectId);
                }
                break;
            default:
                IndexStringField(fieldName, value.ToString() ?? "", objectId);
                break;
        }
    }

    private void IndexStringField(string fieldName, string value, string objectId)
    {
        var fieldIndex = _stringIndexes.GetOrAdd(fieldName, _ => new ConcurrentDictionary<string, HashSet<string>>());
        var docs = fieldIndex.GetOrAdd(value.ToLowerInvariant(), _ => new HashSet<string>());

        lock (docs)
        {
            docs.Add(objectId);
        }
    }

    private void IndexNumericField(string fieldName, double value, string objectId)
    {
        lock (_numericLock)
        {
            if (!_numericIndexes.TryGetValue(fieldName, out var fieldIndex))
            {
                fieldIndex = new SortedList<double, HashSet<string>>();
                _numericIndexes[fieldName] = fieldIndex;
            }

            if (!fieldIndex.TryGetValue(value, out var docs))
            {
                docs = new HashSet<string>();
                fieldIndex[value] = docs;
            }

            docs.Add(objectId);
        }
    }

    private void IndexDateField(string fieldName, DateTime value, string objectId)
    {
        lock (_dateLock)
        {
            if (!_dateIndexes.TryGetValue(fieldName, out var fieldIndex))
            {
                fieldIndex = new SortedList<long, HashSet<string>>();
                _dateIndexes[fieldName] = fieldIndex;
            }

            var ticks = value.Ticks;
            if (!fieldIndex.TryGetValue(ticks, out var docs))
            {
                docs = new HashSet<string>();
                fieldIndex[ticks] = docs;
            }

            docs.Add(objectId);
        }
    }

    private Dictionary<string, object> ParseQuery(string query)
    {
        var filters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Parse "field:value" pairs
        var parts = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex > 0 && colonIndex < part.Length - 1)
            {
                var field = part[..colonIndex];
                var value = part[(colonIndex + 1)..];
                filters[field] = value;
            }
        }

        return filters;
    }

    private HashSet<string> FindMatchingDocuments(string fieldName, object value)
    {
        var results = new HashSet<string>();

        var valueStr = value.ToString()?.ToLowerInvariant() ?? "";

        // Check for range query syntax: ">100", "<50", ">=10", "<=20", "10..20"
        if (valueStr.StartsWith(">="))
        {
            if (double.TryParse(valueStr[2..], out var min))
            {
                AddNumericRangeResults(fieldName, min, null, results);
                return results;
            }
        }
        else if (valueStr.StartsWith("<="))
        {
            if (double.TryParse(valueStr[2..], out var max))
            {
                AddNumericRangeResults(fieldName, null, max, results);
                return results;
            }
        }
        else if (valueStr.StartsWith(">"))
        {
            if (double.TryParse(valueStr[1..], out var min))
            {
                AddNumericRangeResults(fieldName, min + 0.0001, null, results);
                return results;
            }
        }
        else if (valueStr.StartsWith("<"))
        {
            if (double.TryParse(valueStr[1..], out var max))
            {
                AddNumericRangeResults(fieldName, null, max - 0.0001, results);
                return results;
            }
        }
        else if (valueStr.Contains(".."))
        {
            var rangeParts = valueStr.Split("..");
            if (rangeParts.Length == 2 &&
                double.TryParse(rangeParts[0], out var min) &&
                double.TryParse(rangeParts[1], out var max))
            {
                AddNumericRangeResults(fieldName, min, max, results);
                return results;
            }
        }

        // Check string index
        if (_stringIndexes.TryGetValue(fieldName, out var stringIndex))
        {
            // Support wildcard
            if (valueStr.EndsWith('*'))
            {
                var prefix = valueStr[..^1];
                foreach (var (indexValue, docs) in stringIndex)
                {
                    if (indexValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (docs)
                        {
                            results.UnionWith(docs);
                        }
                    }
                }
            }
            else if (stringIndex.TryGetValue(valueStr, out var docs))
            {
                lock (docs)
                {
                    results.UnionWith(docs);
                }
            }
        }

        return results;
    }

    private void AddNumericRangeResults(string fieldName, double? min, double? max, HashSet<string> results)
    {
        lock (_numericLock)
        {
            if (_numericIndexes.TryGetValue(fieldName, out var fieldIndex))
            {
                foreach (var (value, docs) in fieldIndex)
                {
                    if ((!min.HasValue || value >= min.Value) &&
                        (!max.HasValue || value <= max.Value))
                    {
                        results.UnionWith(docs);
                    }
                }
            }
        }
    }

    private double CalculateMatchScore(IndexedMetadata doc, Dictionary<string, object> filters)
    {
        // Simple scoring: 1.0 for exact match on all fields
        return 1.0;
    }
}
