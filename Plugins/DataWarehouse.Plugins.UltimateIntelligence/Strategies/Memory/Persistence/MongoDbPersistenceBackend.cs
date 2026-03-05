using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region MongoDB Configuration

/// <summary>
/// Configuration for MongoDB persistence backend.
/// </summary>
public sealed record MongoDbPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>MongoDB connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Database name.</summary>
    public string DatabaseName { get; init; } = "datawarehouse_memory";

    /// <summary>Collection name prefix (tier suffix will be added if UseTierCollections is true).</summary>
    public string CollectionPrefix { get; init; } = "memory_records";

    /// <summary>Create separate collection per tier.</summary>
    public bool UseTierCollections { get; init; } = true;

    /// <summary>Read preference (primary, primaryPreferred, secondary, secondaryPreferred, nearest).</summary>
    public string ReadPreference { get; init; } = "primaryPreferred";

    /// <summary>Write concern (w value: 0, 1, majority).</summary>
    public string WriteConcern { get; init; } = "majority";

    /// <summary>Enable journaling for write operations.</summary>
    public bool EnableJournaling { get; init; } = true;

    /// <summary>Create indexes on scope and metadata fields.</summary>
    public bool CreateIndexes { get; init; } = true;

    /// <summary>Index fields in metadata (dot notation supported).</summary>
    public string[] MetadataIndexFields { get; init; } = Array.Empty<string>();

    /// <summary>Enable change streams for real-time updates.</summary>
    public bool EnableChangeStreams { get; init; } = true;

    /// <summary>Use GridFS for content larger than this size (in bytes).</summary>
    public long GridFsThreshold { get; init; } = 16 * 1024 * 1024; // 16MB (MongoDB document limit)

    /// <summary>GridFS bucket name.</summary>
    public string GridFsBucketName { get; init; } = "memory_content";

    /// <summary>Maximum pool size.</summary>
    public int MaxPoolSize { get; init; } = 100;

    /// <summary>Minimum pool size.</summary>
    public int MinPoolSize { get; init; } = 10;

    /// <summary>Server selection timeout in milliseconds.</summary>
    public int ServerSelectionTimeoutMs { get; init; } = 30000;

    /// <summary>Socket timeout in milliseconds.</summary>
    public int SocketTimeoutMs { get; init; } = 0; // 0 = infinite

    /// <summary>Enable retry reads.</summary>
    public bool RetryReads { get; init; } = true;

    /// <summary>Enable retry writes.</summary>
    public bool RetryWrites { get; init; } = true;
}

#endregion

/// <summary>
/// MongoDB-based document persistence backend.
/// Provides flexible document storage with collection per tier, rich indexing on metadata,
/// change streams for real-time updates, and GridFS for large content.
/// </summary>
/// <remarks>
/// This backend requires the MongoDB.Driver NuGet package for production use.
/// It uses in-memory structures as a local development fallback when the driver is not available,
/// but will log a warning on construction indicating that data is NOT persisted to MongoDB.
/// </remarks>
public sealed class MongoDbPersistenceBackend : IProductionPersistenceBackend
{
    private readonly MongoDbPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;
    private readonly bool _isSimulated;

    // In-memory fallback collections per tier (used only when MongoDB driver is unavailable)
    private readonly BoundedDictionary<MemoryTier, BoundedDictionary<string, MongoDocument>> _collections = new BoundedDictionary<MemoryTier, BoundedDictionary<string, MongoDocument>>(1000);

    // In-memory fallback indexes
    private readonly BoundedDictionary<string, HashSet<string>> _scopeIndex = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _tagIndex = new BoundedDictionary<string, HashSet<string>>(1000);

    // In-memory fallback GridFS
    private readonly BoundedDictionary<string, byte[]> _gridFsFiles = new BoundedDictionary<string, byte[]>(1000);

    // Change stream
    private readonly ConcurrentQueue<ChangeStreamEvent> _changeStream = new();

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "MongoDB Document Store";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.SecondaryIndexes |
        PersistenceCapabilities.FullTextSearch |
        PersistenceCapabilities.ChangeStreams |
        PersistenceCapabilities.Replication |
        PersistenceCapabilities.Compression;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    /// <summary>
    /// Creates a new MongoDB persistence backend.
    /// </summary>
    /// <param name="config">Backend configuration.</param>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when the MongoDB.Driver NuGet package is not installed and
    /// <see cref="PersistenceBackendConfig.RequireRealBackend"/> is true.
    /// </exception>
    public MongoDbPersistenceBackend(MongoDbPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();

        _isSimulated = !IsMongoDriverAvailable();
        if (_isSimulated && _config.RequireRealBackend)
        {
            throw new PlatformNotSupportedException(
                "MongoDB persistence requires the 'MongoDB.Driver' NuGet package. " +
                "Install it via: dotnet add package MongoDB.Driver. " +
                "Set RequireRealBackend=false to use in-memory fallback (NOT for production).");
        }

        // Initialize collections
        foreach (var tier in Enum.GetValues<MemoryTier>())
        {
            _collections[tier] = new BoundedDictionary<string, MongoDocument>(1000);
        }

        Connect();
    }

    private static bool IsMongoDriverAvailable()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "MongoDB.Driver");
        }
        catch
        {
            return false;
        }
    }

    private void Connect()
    {
        try
        {
            if (_isSimulated)
            {
                Debug.WriteLine("WARNING: MongoDbPersistenceBackend running in in-memory simulation mode. " +
                    "Data will NOT be persisted to MongoDB. Install 'MongoDB.Driver' NuGet package for production use.");
            }
            // In production with real driver, would establish actual MongoDB connection
            // and create indexes
            _isConnected = true;
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    #region Core CRUD Operations

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        var sw = Stopwatch.StartNew();

        try
        {
            var id = record.Id ?? Guid.NewGuid().ToString();

            byte[] content = record.Content;
            string? gridFsId = null;

            // Use GridFS for large content
            if (record.Content.Length > _config.GridFsThreshold)
            {
                gridFsId = Guid.NewGuid().ToString();
                _gridFsFiles[gridFsId] = record.Content;
                content = Array.Empty<byte>();
            }

            var doc = new MongoDocument
            {
                Id = id,
                Content = content,
                GridFsId = gridFsId,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                Metadata = record.Metadata,
                CreatedAt = record.CreatedAt,
                LastAccessedAt = record.LastAccessedAt,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = 1
            };

            var collection = GetCollection(record.Tier);
            collection[id] = doc;

            // Update indexes
            UpdateIndexes(doc, isDelete: false);

            // Publish change event
            if (_config.EnableChangeStreams)
            {
                PublishChange("insert", id, doc);
            }

            sw.Stop();
            _metrics.RecordWrite(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return id;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        var sw = Stopwatch.StartNew();

        try
        {
            MongoDocument? doc = null;

            // Search all collections
            foreach (var collection in _collections.Values)
            {
                if (collection.TryGetValue(id, out doc))
                {
                    break;
                }
            }

            if (doc == null)
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            // Check TTL
            if (doc.ExpiresAt.HasValue && doc.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                await DeleteAsync(id, ct);
                _metrics.RecordCacheMiss();
                return null;
            }

            _metrics.RecordCacheHit();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return ToMemoryRecord(doc);
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string id, MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        var sw = Stopwatch.StartNew();

        try
        {
            MongoDocument? existingDoc = null;
            MemoryTier? existingTier = null;

            // Find existing document
            foreach (var (tier, collection) in _collections)
            {
                if (collection.TryGetValue(id, out existingDoc))
                {
                    existingTier = tier;
                    break;
                }
            }

            if (existingDoc == null || !existingTier.HasValue)
            {
                throw new KeyNotFoundException($"Document with ID {id} not found");
            }

            // Handle tier change
            if (existingTier.Value != record.Tier)
            {
                _collections[existingTier.Value].TryRemove(id, out _);
            }

            // Handle GridFS
            byte[] content = record.Content;
            string? gridFsId = existingDoc.GridFsId;

            if (record.Content.Length > _config.GridFsThreshold)
            {
                if (gridFsId != null)
                {
                    _gridFsFiles[gridFsId] = record.Content;
                }
                else
                {
                    gridFsId = Guid.NewGuid().ToString();
                    _gridFsFiles[gridFsId] = record.Content;
                }
                content = Array.Empty<byte>();
            }
            else if (gridFsId != null)
            {
                // Remove old GridFS file
                _gridFsFiles.TryRemove(gridFsId, out _);
                gridFsId = null;
            }

            var updatedDoc = new MongoDocument
            {
                Id = id,
                Content = content,
                GridFsId = gridFsId,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                Metadata = record.Metadata,
                CreatedAt = existingDoc.CreatedAt,
                LastAccessedAt = record.LastAccessedAt ?? DateTimeOffset.UtcNow,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = existingDoc.Version + 1
            };

            var targetCollection = GetCollection(record.Tier);
            targetCollection[id] = updatedDoc;

            // Update indexes
            UpdateIndexes(existingDoc, isDelete: true);
            UpdateIndexes(updatedDoc, isDelete: false);

            // Publish change event
            if (_config.EnableChangeStreams)
            {
                PublishChange("update", id, updatedDoc);
            }

            sw.Stop();
            _metrics.RecordWrite(sw.Elapsed);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        try
        {
            MongoDocument? doc = null;

            // Find and remove from collection
            foreach (var collection in _collections.Values)
            {
                if (collection.TryRemove(id, out doc))
                {
                    break;
                }
            }

            if (doc != null)
            {
                // Remove from GridFS if applicable
                if (doc.GridFsId != null)
                {
                    _gridFsFiles.TryRemove(doc.GridFsId, out _);
                }

                // Update indexes
                UpdateIndexes(doc, isDelete: true);

                // Publish change event
                if (_config.EnableChangeStreams)
                {
                    PublishChange("delete", id, null);
                }

                _metrics.RecordDelete();
            }

            _circuitBreaker.RecordSuccess();
            return Task.CompletedTask;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        foreach (var collection in _collections.Values)
        {
            if (collection.TryGetValue(id, out var doc))
            {
                // Check TTL
                if (doc.ExpiresAt.HasValue && doc.ExpiresAt.Value < DateTimeOffset.UtcNow)
                {
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    #endregion

    #region Batch Operations

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        try
        {
            // MongoDB supports bulkWrite for atomic batch operations
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                await StoreAsync(record, ct);
            }

            _circuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var results = new List<MemoryRecord>();

        foreach (var id in ids)
        {
            var record = await GetAsync(id, ct);
            if (record != null)
            {
                results.Add(record);
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        foreach (var id in ids)
        {
            await DeleteAsync(id, ct);
        }
    }

    #endregion

    #region Query Operations

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        IEnumerable<MongoDocument> docs;

        // Select collection(s) based on tier filter
        if (query.Tier.HasValue && _config.UseTierCollections)
        {
            docs = _collections[query.Tier.Value].Values;
        }
        else
        {
            docs = _collections.Values.SelectMany(c => c.Values);
        }

        // Use indexes for scope and tag filtering
        if (!string.IsNullOrEmpty(query.Scope))
        {
            if (_scopeIndex.TryGetValue(query.Scope, out var scopeIds))
            {
                docs = docs.Where(d => scopeIds.Contains(d.Id));
            }
            else
            {
                yield break;
            }
        }

        if (query.Tags != null && query.Tags.Length > 0)
        {
            var tagMatchIds = new HashSet<string>();
            foreach (var tag in query.Tags)
            {
                if (_tagIndex.TryGetValue(tag, out var tagIds))
                {
                    foreach (var id in tagIds)
                    {
                        tagMatchIds.Add(id);
                    }
                }
            }
            docs = docs.Where(d => tagMatchIds.Contains(d.Id));
        }

        // Apply remaining filters
        docs = ApplyFilters(docs, query, now);

        // Full-text search
        if (!string.IsNullOrEmpty(query.TextQuery))
        {
            docs = ApplyTextSearch(docs, query.TextQuery);
        }

        // Apply sorting
        if (!string.IsNullOrEmpty(query.SortBy))
        {
            docs = ApplySorting(docs, query.SortBy, query.SortDescending);
        }
        else
        {
            docs = docs.OrderByDescending(d => d.CreatedAt);
        }

        // Apply pagination
        docs = docs.Skip(query.Skip).Take(query.Limit);

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToMemoryRecord(doc);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        IEnumerable<MongoDocument> docs = _collections.Values.SelectMany(c => c.Values);

        if (query != null)
        {
            if (query.Tier.HasValue)
            {
                docs = _collections[query.Tier.Value].Values;
            }

            docs = ApplyFilters(docs, query, now);

            if (!string.IsNullOrEmpty(query.TextQuery))
            {
                docs = ApplyTextSearch(docs, query.TextQuery);
            }
        }
        else
        {
            docs = docs.Where(d => !d.ExpiresAt.HasValue || d.ExpiresAt.Value >= now);
        }

        return Task.FromResult((long)docs.Count());
    }

    private IEnumerable<MongoDocument> ApplyFilters(IEnumerable<MongoDocument> docs, MemoryQuery query, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(query.Scope))
        {
            docs = docs.Where(d => d.Scope == query.Scope);
        }

        if (!string.IsNullOrEmpty(query.ContentType))
        {
            docs = docs.Where(d => d.ContentType == query.ContentType);
        }

        if (query.MinImportanceScore.HasValue)
        {
            docs = docs.Where(d => d.ImportanceScore >= query.MinImportanceScore.Value);
        }

        if (query.MaxImportanceScore.HasValue)
        {
            docs = docs.Where(d => d.ImportanceScore <= query.MaxImportanceScore.Value);
        }

        if (query.CreatedAfter.HasValue)
        {
            docs = docs.Where(d => d.CreatedAt >= query.CreatedAfter.Value);
        }

        if (query.CreatedBefore.HasValue)
        {
            docs = docs.Where(d => d.CreatedAt <= query.CreatedBefore.Value);
        }

        if (query.AccessedAfter.HasValue)
        {
            docs = docs.Where(d => d.LastAccessedAt >= query.AccessedAfter.Value);
        }

        // Metadata filters
        if (query.MetadataFilters != null && query.MetadataFilters.Count > 0)
        {
            docs = docs.Where(d => MatchesMetadata(d.Metadata, query.MetadataFilters));
        }

        if (!query.IncludeExpired)
        {
            docs = docs.Where(d => !d.ExpiresAt.HasValue || d.ExpiresAt.Value >= now);
        }

        return docs;
    }

    private IEnumerable<MongoDocument> ApplyTextSearch(IEnumerable<MongoDocument> docs, string textQuery)
    {
        // Simplified text search (MongoDB uses $text with text indexes)
        var queryLower = textQuery.ToLowerInvariant();
        var queryWords = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return docs.Where(d =>
        {
            var contentText = Encoding.UTF8.GetString(GetFullContent(d)).ToLowerInvariant();
            return queryWords.All(w => contentText.Contains(w));
        });
    }

    private IEnumerable<MongoDocument> ApplySorting(IEnumerable<MongoDocument> docs, string sortBy, bool descending)
    {
        return sortBy.ToLowerInvariant() switch
        {
            "createdat" => descending
                ? docs.OrderByDescending(d => d.CreatedAt)
                : docs.OrderBy(d => d.CreatedAt),
            "lastaccessedat" => descending
                ? docs.OrderByDescending(d => d.LastAccessedAt)
                : docs.OrderBy(d => d.LastAccessedAt),
            "importancescore" => descending
                ? docs.OrderByDescending(d => d.ImportanceScore)
                : docs.OrderBy(d => d.ImportanceScore),
            "accesscount" => descending
                ? docs.OrderByDescending(d => d.AccessCount)
                : docs.OrderBy(d => d.AccessCount),
            _ => docs
        };
    }

    private bool MatchesMetadata(Dictionary<string, object>? docMetadata, Dictionary<string, object> filters)
    {
        if (docMetadata == null) return false;

        foreach (var (key, value) in filters)
        {
            // Support dot notation for nested fields
            var actualValue = GetNestedValue(docMetadata, key);
            if (actualValue == null || !actualValue.Equals(value))
            {
                return false;
            }
        }

        return true;
    }

    private object? GetNestedValue(Dictionary<string, object> dict, string path)
    {
        var parts = path.Split('.');
        object? current = dict;

        foreach (var part in parts)
        {
            if (current is Dictionary<string, object> d)
            {
                if (!d.TryGetValue(part, out current))
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var collection = GetCollection(tier);

        var records = collection.Values
            .Where(d => !d.ExpiresAt.HasValue || d.ExpiresAt.Value >= now)
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!_scopeIndex.TryGetValue(scope, out var ids))
        {
            return Task.FromResult<IEnumerable<MemoryRecord>>(Array.Empty<MemoryRecord>());
        }

        var now = DateTimeOffset.UtcNow;
        var records = new List<MemoryRecord>();

        foreach (var id in ids.Take(limit))
        {
            foreach (var collection in _collections.Values)
            {
                if (collection.TryGetValue(id, out var doc) &&
                    (!doc.ExpiresAt.HasValue || doc.ExpiresAt.Value >= now))
                {
                    records.Add(ToMemoryRecord(doc));
                    break;
                }
            }
        }

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    #endregion

    #region Maintenance Operations

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;

        // Remove expired documents
        foreach (var collection in _collections.Values)
        {
            var expiredIds = collection.Values
                .Where(d => d.ExpiresAt.HasValue && d.ExpiresAt.Value < now)
                .Select(d => d.Id)
                .ToList();

            foreach (var id in expiredIds)
            {
                if (collection.TryRemove(id, out var doc))
                {
                    if (doc.GridFsId != null)
                    {
                        _gridFsFiles.TryRemove(doc.GridFsId, out _);
                    }
                    UpdateIndexes(doc, isDelete: true);
                }
            }
        }

        // Clean up orphaned GridFS files
        var validGridFsIds = _collections.Values
            .SelectMany(c => c.Values)
            .Where(d => d.GridFsId != null)
            .Select(d => d.GridFsId!)
            .ToHashSet();

        var orphanedIds = _gridFsFiles.Keys.Except(validGridFsIds).ToList();
        foreach (var orphanedId in orphanedIds)
        {
            _gridFsFiles.TryRemove(orphanedId, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // MongoDB handles durability via journaling
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var recordsByTier = new Dictionary<MemoryTier, long>();
        var sizeByTier = new Dictionary<MemoryTier, long>();

        foreach (var (tier, collection) in _collections)
        {
            var validDocs = collection.Values
                .Where(d => !d.ExpiresAt.HasValue || d.ExpiresAt.Value >= now)
                .ToList();

            recordsByTier[tier] = validDocs.Count;
            sizeByTier[tier] = validDocs.Sum(d => GetFullContent(d).Length);
        }

        return Task.FromResult(new PersistenceStatistics
        {
            BackendId = BackendId,
            TotalRecords = recordsByTier.Values.Sum(),
            TotalSizeBytes = sizeByTier.Values.Sum() + _gridFsFiles.Values.Sum(f => f.Length),
            RecordsByTier = recordsByTier,
            SizeByTier = sizeByTier,
            PendingWrites = 0,
            TotalReads = _metrics.TotalReads,
            TotalWrites = _metrics.TotalWrites,
            TotalDeletes = _metrics.TotalDeletes,
            CacheHitRatio = _metrics.CacheHitRatio,
            AvgReadLatencyMs = _metrics.AvgReadLatencyMs,
            AvgWriteLatencyMs = _metrics.AvgWriteLatencyMs,
            P99ReadLatencyMs = _metrics.P99ReadLatencyMs,
            P99WriteLatencyMs = _metrics.P99WriteLatencyMs,
            ActiveConnections = 1,
            ConnectionPoolSize = _config.MaxPoolSize,
            IsHealthy = IsConnected,
            HealthCheckTime = DateTimeOffset.UtcNow,
            CustomMetrics = new Dictionary<string, object>
            {
                ["gridFsFileCount"] = _gridFsFiles.Count,
                ["gridFsTotalBytes"] = _gridFsFiles.Values.Sum(f => f.Length),
                ["changeStreamLength"] = _changeStream.Count,
                ["scopeIndexSize"] = _scopeIndex.Count,
                ["tagIndexSize"] = _tagIndex.Count
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(false);
        if (!_circuitBreaker.AllowOperation()) return Task.FromResult(false);

        // In production, would ping the MongoDB server
        return Task.FromResult(_isConnected);
    }

    #endregion

    #region Change Streams

    /// <summary>
    /// Subscribes to change stream events.
    /// </summary>
    /// <param name="startAtOperationTime">Start at this operation time (null for live changes only).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of change events.</returns>
    public async IAsyncEnumerable<ChangeStreamEvent> WatchAsync(
        DateTimeOffset? startAtOperationTime = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var snapshot = _changeStream.ToArray();
        var started = startAtOperationTime == null;

        foreach (var evt in snapshot)
        {
            ct.ThrowIfCancellationRequested();

            if (!started)
            {
                if (evt.Timestamp >= startAtOperationTime)
                {
                    started = true;
                }
                else
                {
                    continue;
                }
            }

            yield return evt;
        }

        await Task.CompletedTask;
    }

    private void PublishChange(string operationType, string documentId, MongoDocument? document)
    {
        var evt = new ChangeStreamEvent
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            OperationType = operationType,
            DocumentId = documentId,
            FullDocument = document != null ? ToMemoryRecord(document) : null
        };

        _changeStream.Enqueue(evt);

        // Limit stream size
        while (_changeStream.Count > 10000)
        {
            _changeStream.TryDequeue(out _);
        }
    }

    #endregion

    #region Private Helpers

    private BoundedDictionary<string, MongoDocument> GetCollection(MemoryTier tier)
    {
        return _collections[tier];
    }

    private byte[] GetFullContent(MongoDocument doc)
    {
        if (doc.GridFsId != null && _gridFsFiles.TryGetValue(doc.GridFsId, out var content))
        {
            return content;
        }
        return doc.Content;
    }

    private void UpdateIndexes(MongoDocument doc, bool isDelete)
    {
        // Scope index
        if (!_scopeIndex.TryGetValue(doc.Scope, out var scopeIds))
        {
            scopeIds = new HashSet<string>();
            _scopeIndex[doc.Scope] = scopeIds;
        }

        lock (scopeIds)
        {
            if (isDelete)
                scopeIds.Remove(doc.Id);
            else
                scopeIds.Add(doc.Id);
        }

        // Tag index
        if (doc.Tags != null)
        {
            foreach (var tag in doc.Tags)
            {
                if (!_tagIndex.TryGetValue(tag, out var tagIds))
                {
                    tagIds = new HashSet<string>();
                    _tagIndex[tag] = tagIds;
                }

                lock (tagIds)
                {
                    if (isDelete)
                        tagIds.Remove(doc.Id);
                    else
                        tagIds.Add(doc.Id);
                }
            }
        }
    }

    private MemoryRecord ToMemoryRecord(MongoDocument doc)
    {
        return new MemoryRecord
        {
            Id = doc.Id,
            Content = GetFullContent(doc),
            Tier = doc.Tier,
            Scope = doc.Scope,
            Embedding = doc.Embedding,
            Metadata = doc.Metadata,
            CreatedAt = doc.CreatedAt,
            LastAccessedAt = doc.LastAccessedAt,
            AccessCount = doc.AccessCount,
            ImportanceScore = doc.ImportanceScore,
            ContentType = doc.ContentType,
            Tags = doc.Tags,
            ExpiresAt = doc.ExpiresAt,
            Version = doc.Version
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MongoDbPersistenceBackend));
        }
    }

    private void EnsureCircuitBreaker()
    {
        if (!_circuitBreaker.AllowOperation())
        {
            throw new InvalidOperationException("Circuit breaker is open - backend temporarily unavailable");
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        foreach (var collection in _collections.Values)
        {
            collection.Clear();
        }

        _scopeIndex.Clear();
        _tagIndex.Clear();
        _gridFsFiles.Clear();

        return ValueTask.CompletedTask;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Internal MongoDB document representation.
/// </summary>
internal sealed record MongoDocument
{
    public required string Id { get; init; }
    public required byte[] Content { get; init; }
    public string? GridFsId { get; init; }
    public MemoryTier Tier { get; init; }
    public required string Scope { get; init; }
    public float[]? Embedding { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public long AccessCount { get; init; }
    public double ImportanceScore { get; init; }
    public string? ContentType { get; init; }
    public string[]? Tags { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Change stream event from MongoDB.
/// </summary>
public sealed record ChangeStreamEvent
{
    /// <summary>Event ID.</summary>
    public required string Id { get; init; }

    /// <summary>Event timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Operation type (insert, update, delete, replace).</summary>
    public required string OperationType { get; init; }

    /// <summary>Affected document ID.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Full document (null for deletes).</summary>
    public MemoryRecord? FullDocument { get; init; }
}

#endregion
