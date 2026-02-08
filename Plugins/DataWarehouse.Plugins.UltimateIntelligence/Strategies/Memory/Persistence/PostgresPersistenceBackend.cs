using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region PostgreSQL Configuration

/// <summary>
/// Configuration for PostgreSQL persistence backend.
/// </summary>
public sealed record PostgresPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>PostgreSQL connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Schema name.</summary>
    public string Schema { get; init; } = "memory";

    /// <summary>Table name.</summary>
    public string TableName { get; init; } = "records";

    /// <summary>Enable table partitioning by tier.</summary>
    public bool EnablePartitioning { get; init; } = true;

    /// <summary>Use JSONB for metadata column.</summary>
    public bool UseJsonb { get; init; } = true;

    /// <summary>Create GIN indexes on metadata.</summary>
    public bool CreateGinIndexes { get; init; } = true;

    /// <summary>Enable full-text search with tsvector.</summary>
    public bool EnableFullTextSearch { get; init; } = true;

    /// <summary>Full-text search configuration (e.g., 'english', 'simple').</summary>
    public string FullTextSearchConfig { get; init; } = "english";

    /// <summary>Enable pgvector extension for embeddings.</summary>
    public bool EnablePgVector { get; init; } = true;

    /// <summary>Vector dimension for pgvector.</summary>
    public int VectorDimension { get; init; } = 1536;

    /// <summary>Use HNSW index for vector search (faster but less accurate).</summary>
    public bool UseHnswIndex { get; init; } = true;

    /// <summary>HNSW index m parameter.</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW index ef_construction parameter.</summary>
    public int HnswEfConstruction { get; init; } = 64;

    /// <summary>Maximum pool size.</summary>
    public int MaxPoolSize { get; init; } = 100;

    /// <summary>Minimum pool size.</summary>
    public int MinPoolSize { get; init; } = 10;

    /// <summary>Command timeout in seconds.</summary>
    public int CommandTimeout { get; init; } = 30;

    /// <summary>Enable automatic vacuum.</summary>
    public bool EnableAutoVacuum { get; init; } = true;

    /// <summary>Enable prepared statements.</summary>
    public bool EnablePreparedStatements { get; init; } = true;
}

#endregion

/// <summary>
/// PostgreSQL-based relational persistence backend.
/// Provides ACID-compliant storage with JSONB columns for flexible metadata,
/// table partitioning by tier, GIN indexes, full-text search with tsvector,
/// and pgvector support for embedding similarity search.
/// </summary>
/// <remarks>
/// This implementation simulates PostgreSQL behavior using in-memory structures.
/// In production, use Npgsql package.
/// </remarks>
public sealed class PostgresPersistenceBackend : IProductionPersistenceBackend
{
    private readonly PostgresPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;

    // Simulated partitioned tables
    private readonly ConcurrentDictionary<MemoryTier, ConcurrentDictionary<string, PostgresRow>> _partitions = new();

    // Simulated indexes
    private readonly ConcurrentDictionary<string, HashSet<string>> _scopeIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _tagIndex = new();
    private readonly ConcurrentDictionary<string, string> _fullTextIndex = new(); // id -> tsvector text

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "PostgreSQL Relational Store";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.Transactions |
        PersistenceCapabilities.SecondaryIndexes |
        PersistenceCapabilities.FullTextSearch |
        PersistenceCapabilities.VectorSearch |
        PersistenceCapabilities.Compression |
        PersistenceCapabilities.OptimisticConcurrency;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    /// <summary>
    /// Creates a new PostgreSQL persistence backend.
    /// </summary>
    /// <param name="config">Backend configuration.</param>
    public PostgresPersistenceBackend(PostgresPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();

        // Initialize partitions
        foreach (var tier in Enum.GetValues<MemoryTier>())
        {
            _partitions[tier] = new ConcurrentDictionary<string, PostgresRow>();
        }

        // Simulate connection and schema creation
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        try
        {
            // In production, would execute:
            // CREATE SCHEMA IF NOT EXISTS ...
            // CREATE TABLE IF NOT EXISTS ... PARTITION BY LIST (tier)
            // CREATE INDEX ... USING GIN (metadata)
            // CREATE INDEX ... USING GIN (to_tsvector(...))
            // CREATE INDEX ... USING hnsw (embedding ...)
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

            var row = new PostgresRow
            {
                Id = id,
                Content = record.Content,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                Metadata = record.Metadata != null ? JsonSerializer.Serialize(record.Metadata) : null,
                CreatedAt = record.CreatedAt,
                LastAccessedAt = record.LastAccessedAt,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = 1,
                TsVector = _config.EnableFullTextSearch ? GenerateTsVector(record) : null
            };

            var partition = _partitions[record.Tier];
            partition[id] = row;

            // Update indexes
            UpdateIndexes(row, isDelete: false);

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
            PostgresRow? row = null;

            // Search all partitions
            foreach (var partition in _partitions.Values)
            {
                if (partition.TryGetValue(id, out row))
                {
                    break;
                }
            }

            if (row == null)
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            // Check expiration
            if (row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                await DeleteAsync(id, ct);
                _metrics.RecordCacheMiss();
                return null;
            }

            _metrics.RecordCacheHit();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return ToMemoryRecord(row);
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
            PostgresRow? existingRow = null;
            MemoryTier? existingTier = null;

            // Find existing row
            foreach (var (tier, partition) in _partitions)
            {
                if (partition.TryGetValue(id, out existingRow))
                {
                    existingTier = tier;
                    break;
                }
            }

            if (existingRow == null || !existingTier.HasValue)
            {
                throw new KeyNotFoundException($"Record with ID {id} not found");
            }

            // Optimistic concurrency check
            if (record.Version != existingRow.Version)
            {
                throw new InvalidOperationException($"Version conflict: expected {existingRow.Version}, got {record.Version}");
            }

            // Handle partition change (tier change)
            if (existingTier.Value != record.Tier)
            {
                _partitions[existingTier.Value].TryRemove(id, out _);
            }

            var updatedRow = new PostgresRow
            {
                Id = id,
                Content = record.Content,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                Metadata = record.Metadata != null ? JsonSerializer.Serialize(record.Metadata) : null,
                CreatedAt = existingRow.CreatedAt,
                LastAccessedAt = record.LastAccessedAt ?? DateTimeOffset.UtcNow,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = existingRow.Version + 1,
                TsVector = _config.EnableFullTextSearch ? GenerateTsVector(record) : null
            };

            _partitions[record.Tier][id] = updatedRow;

            // Update indexes
            UpdateIndexes(existingRow, isDelete: true);
            UpdateIndexes(updatedRow, isDelete: false);

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
            PostgresRow? row = null;

            foreach (var partition in _partitions.Values)
            {
                if (partition.TryRemove(id, out row))
                {
                    break;
                }
            }

            if (row != null)
            {
                UpdateIndexes(row, isDelete: true);
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

        foreach (var partition in _partitions.Values)
        {
            if (partition.TryGetValue(id, out var row))
            {
                if (row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTimeOffset.UtcNow)
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
            // PostgreSQL supports COPY for fast bulk inserts
            // or multi-row INSERT statements
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

        // PostgreSQL: WHERE id = ANY($1)
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

        // PostgreSQL: DELETE FROM ... WHERE id = ANY($1)
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
        IEnumerable<PostgresRow> rows;

        // Partition pruning based on tier
        if (query.Tier.HasValue)
        {
            rows = _partitions[query.Tier.Value].Values;
        }
        else
        {
            rows = _partitions.Values.SelectMany(p => p.Values);
        }

        // Use indexes
        if (!string.IsNullOrEmpty(query.Scope))
        {
            if (_scopeIndex.TryGetValue(query.Scope, out var scopeIds))
            {
                rows = rows.Where(r => scopeIds.Contains(r.Id));
            }
            else
            {
                yield break;
            }
        }

        // Full-text search using tsvector
        if (_config.EnableFullTextSearch && !string.IsNullOrEmpty(query.TextQuery))
        {
            rows = ApplyFullTextSearch(rows, query.TextQuery);
        }

        // Vector similarity search using pgvector
        if (_config.EnablePgVector && query.SimilarityVector != null && query.SimilarityVector.Length > 0)
        {
            rows = ApplyVectorSearch(rows, query.SimilarityVector, query.MinSimilarity ?? 0.7f);
        }

        // Apply remaining filters
        rows = ApplyFilters(rows, query, now);

        // Apply sorting
        if (!string.IsNullOrEmpty(query.SortBy))
        {
            rows = ApplySorting(rows, query.SortBy, query.SortDescending);
        }
        else
        {
            rows = rows.OrderByDescending(r => r.CreatedAt);
        }

        // Apply pagination (OFFSET and LIMIT)
        rows = rows.Skip(query.Skip).Take(query.Limit);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToMemoryRecord(row);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        IEnumerable<PostgresRow> rows = _partitions.Values.SelectMany(p => p.Values);

        if (query != null)
        {
            if (query.Tier.HasValue)
            {
                rows = _partitions[query.Tier.Value].Values;
            }

            rows = ApplyFilters(rows, query, now);

            if (_config.EnableFullTextSearch && !string.IsNullOrEmpty(query.TextQuery))
            {
                rows = ApplyFullTextSearch(rows, query.TextQuery);
            }
        }
        else
        {
            rows = rows.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now);
        }

        return Task.FromResult((long)rows.Count());
    }

    private IEnumerable<PostgresRow> ApplyFullTextSearch(IEnumerable<PostgresRow> rows, string textQuery)
    {
        // Simulates: WHERE to_tsvector('english', content) @@ plainto_tsquery('english', $1)
        var queryWords = textQuery.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Regex.Replace(w, @"[^\w]", ""))
            .Where(w => w.Length > 2)
            .ToArray();

        return rows.Where(r =>
        {
            if (r.TsVector == null) return false;
            return queryWords.All(w => r.TsVector.Contains(w, StringComparison.OrdinalIgnoreCase));
        });
    }

    private IEnumerable<PostgresRow> ApplyVectorSearch(IEnumerable<PostgresRow> rows, float[] queryVector, float minSimilarity)
    {
        // Simulates: ORDER BY embedding <-> $1 with cosine similarity
        return rows
            .Where(r => r.Embedding != null && r.Embedding.Length == queryVector.Length)
            .Select(r => (Row: r, Score: CosineSimilarity(queryVector, r.Embedding!)))
            .Where(x => x.Score >= minSimilarity)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Row);
    }

    private IEnumerable<PostgresRow> ApplyFilters(IEnumerable<PostgresRow> rows, MemoryQuery query, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(query.Scope))
        {
            rows = rows.Where(r => r.Scope == query.Scope);
        }

        if (!string.IsNullOrEmpty(query.ContentType))
        {
            rows = rows.Where(r => r.ContentType == query.ContentType);
        }

        if (query.MinImportanceScore.HasValue)
        {
            rows = rows.Where(r => r.ImportanceScore >= query.MinImportanceScore.Value);
        }

        if (query.MaxImportanceScore.HasValue)
        {
            rows = rows.Where(r => r.ImportanceScore <= query.MaxImportanceScore.Value);
        }

        if (query.CreatedAfter.HasValue)
        {
            rows = rows.Where(r => r.CreatedAt >= query.CreatedAfter.Value);
        }

        if (query.CreatedBefore.HasValue)
        {
            rows = rows.Where(r => r.CreatedAt <= query.CreatedBefore.Value);
        }

        if (query.AccessedAfter.HasValue)
        {
            rows = rows.Where(r => r.LastAccessedAt >= query.AccessedAfter.Value);
        }

        // JSONB query: metadata @> $1
        if (query.MetadataFilters != null && query.MetadataFilters.Count > 0)
        {
            rows = rows.Where(r => MatchesJsonb(r.Metadata, query.MetadataFilters));
        }

        if (query.Tags != null && query.Tags.Length > 0)
        {
            // Array overlap: tags && $1
            rows = rows.Where(r => r.Tags != null && query.Tags.Any(t => r.Tags.Contains(t)));
        }

        if (!query.IncludeExpired)
        {
            rows = rows.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now);
        }

        return rows;
    }

    private IEnumerable<PostgresRow> ApplySorting(IEnumerable<PostgresRow> rows, string sortBy, bool descending)
    {
        return sortBy.ToLowerInvariant() switch
        {
            "createdat" => descending
                ? rows.OrderByDescending(r => r.CreatedAt)
                : rows.OrderBy(r => r.CreatedAt),
            "lastaccessedat" => descending
                ? rows.OrderByDescending(r => r.LastAccessedAt)
                : rows.OrderBy(r => r.LastAccessedAt),
            "importancescore" => descending
                ? rows.OrderByDescending(r => r.ImportanceScore)
                : rows.OrderBy(r => r.ImportanceScore),
            "accesscount" => descending
                ? rows.OrderByDescending(r => r.AccessCount)
                : rows.OrderBy(r => r.AccessCount),
            _ => rows
        };
    }

    private bool MatchesJsonb(string? metadata, Dictionary<string, object> filters)
    {
        if (metadata == null) return false;

        try
        {
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadata);
            if (doc == null) return false;

            foreach (var (key, value) in filters)
            {
                if (!doc.TryGetValue(key, out var element))
                    return false;

                if (!JsonElementEquals(element, value))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool JsonElementEquals(JsonElement element, object value)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() == value?.ToString(),
            JsonValueKind.Number when value is int i => element.GetInt32() == i,
            JsonValueKind.Number when value is long l => element.GetInt64() == l,
            JsonValueKind.Number when value is double d => Math.Abs(element.GetDouble() - d) < 0.0001,
            JsonValueKind.True => value is bool b && b,
            JsonValueKind.False => value is bool b && !b,
            _ => false
        };
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;

        var records = _partitions[tier].Values
            .Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now)
            .OrderByDescending(r => r.CreatedAt)
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
            foreach (var partition in _partitions.Values)
            {
                if (partition.TryGetValue(id, out var row) &&
                    (!row.ExpiresAt.HasValue || row.ExpiresAt.Value >= now))
                {
                    records.Add(ToMemoryRecord(row));
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

        // Remove expired rows (VACUUM equivalent)
        foreach (var partition in _partitions.Values)
        {
            var expiredIds = partition.Values
                .Where(r => r.ExpiresAt.HasValue && r.ExpiresAt.Value < now)
                .Select(r => r.Id)
                .ToList();

            foreach (var id in expiredIds)
            {
                if (partition.TryRemove(id, out var row))
                {
                    UpdateIndexes(row, isDelete: true);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // PostgreSQL handles WAL flushing automatically
        // CHECKPOINT could be triggered here
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var recordsByTier = new Dictionary<MemoryTier, long>();
        var sizeByTier = new Dictionary<MemoryTier, long>();

        foreach (var (tier, partition) in _partitions)
        {
            var validRows = partition.Values
                .Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now)
                .ToList();

            recordsByTier[tier] = validRows.Count;
            sizeByTier[tier] = validRows.Sum(r => r.Content?.Length ?? 0);
        }

        return Task.FromResult(new PersistenceStatistics
        {
            BackendId = BackendId,
            TotalRecords = recordsByTier.Values.Sum(),
            TotalSizeBytes = sizeByTier.Values.Sum(),
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
                ["partitionCount"] = _partitions.Count,
                ["scopeIndexSize"] = _scopeIndex.Count,
                ["tagIndexSize"] = _tagIndex.Count,
                ["fullTextSearchEnabled"] = _config.EnableFullTextSearch,
                ["pgVectorEnabled"] = _config.EnablePgVector
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(false);
        if (!_circuitBreaker.AllowOperation()) return Task.FromResult(false);

        // In production, would execute: SELECT 1
        return Task.FromResult(_isConnected);
    }

    #endregion

    #region Private Helpers

    private void UpdateIndexes(PostgresRow row, bool isDelete)
    {
        // Scope index
        if (!_scopeIndex.TryGetValue(row.Scope, out var scopeIds))
        {
            scopeIds = new HashSet<string>();
            _scopeIndex[row.Scope] = scopeIds;
        }

        lock (scopeIds)
        {
            if (isDelete)
                scopeIds.Remove(row.Id);
            else
                scopeIds.Add(row.Id);
        }

        // Tag index
        if (row.Tags != null)
        {
            foreach (var tag in row.Tags)
            {
                if (!_tagIndex.TryGetValue(tag, out var tagIds))
                {
                    tagIds = new HashSet<string>();
                    _tagIndex[tag] = tagIds;
                }

                lock (tagIds)
                {
                    if (isDelete)
                        tagIds.Remove(row.Id);
                    else
                        tagIds.Add(row.Id);
                }
            }
        }

        // Full-text index
        if (isDelete)
        {
            _fullTextIndex.TryRemove(row.Id, out _);
        }
        else if (row.TsVector != null)
        {
            _fullTextIndex[row.Id] = row.TsVector;
        }
    }

    private string? GenerateTsVector(MemoryRecord record)
    {
        if (!_config.EnableFullTextSearch) return null;

        // Simulates PostgreSQL to_tsvector()
        var text = Encoding.UTF8.GetString(record.Content);
        var words = text.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Regex.Replace(w, @"[^\w]", ""))
            .Where(w => w.Length > 2)
            .Distinct();

        return string.Join(" ", words);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator > 0 ? (float)(dotProduct / denominator) : 0;
    }

    private MemoryRecord ToMemoryRecord(PostgresRow row)
    {
        Dictionary<string, object>? metadata = null;
        if (row.Metadata != null)
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(row.Metadata);
            }
            catch { /* Ignore deserialization errors */ }
        }

        return new MemoryRecord
        {
            Id = row.Id,
            Content = row.Content,
            Tier = row.Tier,
            Scope = row.Scope,
            Embedding = row.Embedding,
            Metadata = metadata,
            CreatedAt = row.CreatedAt,
            LastAccessedAt = row.LastAccessedAt,
            AccessCount = row.AccessCount,
            ImportanceScore = row.ImportanceScore,
            ContentType = row.ContentType,
            Tags = row.Tags,
            ExpiresAt = row.ExpiresAt,
            Version = row.Version
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PostgresPersistenceBackend));
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

        foreach (var partition in _partitions.Values)
        {
            partition.Clear();
        }

        _scopeIndex.Clear();
        _tagIndex.Clear();
        _fullTextIndex.Clear();

        return ValueTask.CompletedTask;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Internal PostgreSQL row representation.
/// </summary>
internal sealed record PostgresRow
{
    public required string Id { get; init; }
    public required byte[] Content { get; init; }
    public MemoryTier Tier { get; init; }
    public required string Scope { get; init; }
    public float[]? Embedding { get; init; }
    public string? Metadata { get; init; } // JSONB
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public long AccessCount { get; init; }
    public double ImportanceScore { get; init; }
    public string? ContentType { get; init; }
    public string[]? Tags { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Version { get; init; }
    public string? TsVector { get; init; } // Full-text search vector
}

#endregion
