using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region Cassandra Configuration

/// <summary>
/// Configuration for Cassandra persistence backend.
/// </summary>
public sealed record CassandraPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>Cassandra contact points (comma-separated hosts).</summary>
    public required string ContactPoints { get; init; }

    /// <summary>Cassandra port (default 9042).</summary>
    public int Port { get; init; } = 9042;

    /// <summary>Keyspace name.</summary>
    public string Keyspace { get; init; } = "datawarehouse_memory";

    /// <summary>Table name.</summary>
    public string TableName { get; init; } = "memory_records";

    /// <summary>Username for authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Password for authentication.</summary>
    public string? Password { get; init; }

    /// <summary>Datacenter name for local DC-aware load balancing.</summary>
    public string? LocalDatacenter { get; init; }

    /// <summary>Replication factor for the keyspace.</summary>
    public int ReplicationFactor { get; init; } = 3;

    /// <summary>Replication strategy class.</summary>
    public string ReplicationStrategy { get; init; } = "SimpleStrategy";

    /// <summary>Read consistency level.</summary>
    public CassandraConsistency ReadConsistency { get; init; } = CassandraConsistency.LocalQuorum;

    /// <summary>Write consistency level.</summary>
    public CassandraConsistency WriteConsistency { get; init; } = CassandraConsistency.LocalQuorum;

    /// <summary>Default TTL for records in seconds (0 = no TTL).</summary>
    public int DefaultTTLSeconds { get; init; }

    /// <summary>TTL per tier in seconds.</summary>
    public Dictionary<MemoryTier, int> TierTTLSeconds { get; init; } = new()
    {
        [MemoryTier.Immediate] = 1800, // 30 minutes
        [MemoryTier.Working] = 86400, // 24 hours
        [MemoryTier.ShortTerm] = 2592000, // 30 days
        // LongTerm has no TTL
    };

    /// <summary>Enable speculative execution for reads.</summary>
    public bool EnableSpeculativeExecution { get; init; } = true;

    /// <summary>Maximum concurrent requests per connection.</summary>
    public int MaxConcurrentRequests { get; init; } = 1024;

    /// <summary>Core connection pool size.</summary>
    public int CoreConnectionsPerHost { get; init; } = 2;

    /// <summary>Maximum connection pool size.</summary>
    public int MaxConnectionsPerHost { get; init; } = 8;

    /// <summary>Compaction strategy for the table.</summary>
    public CassandraCompactionStrategy CompactionStrategy { get; init; } = CassandraCompactionStrategy.LeveledCompaction;

    /// <summary>Enable prepared statement caching.</summary>
    public bool EnablePreparedStatements { get; init; } = true;
}

/// <summary>
/// Cassandra consistency levels.
/// </summary>
public enum CassandraConsistency
{
    Any,
    One,
    Two,
    Three,
    Quorum,
    All,
    LocalQuorum,
    EachQuorum,
    LocalOne
}

/// <summary>
/// Cassandra compaction strategies.
/// </summary>
public enum CassandraCompactionStrategy
{
    SizeTieredCompaction,
    LeveledCompaction,
    TimeWindowCompaction
}

#endregion

/// <summary>
/// Cassandra-based wide-column distributed persistence backend.
/// Provides highly scalable storage with partition by scope, clustering by tier/time,
/// tunable consistency levels, TTL, and configurable compaction strategies.
/// </summary>
/// <remarks>
/// This implementation simulates Cassandra behavior using in-memory structures.
/// In production, use the DataStax C# driver.
/// </remarks>
public sealed class CassandraPersistenceBackend : IProductionPersistenceBackend
{
    private readonly CassandraPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;

    // Simulated Cassandra data model:
    // PRIMARY KEY ((scope), tier, created_at, id)
    // Partition key: scope
    // Clustering columns: tier, created_at (DESC), id
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<CassandraClusteringKey, CassandraRow>> _partitions = new();

    // Secondary index on ID for direct lookups
    private readonly ConcurrentDictionary<string, (string Scope, CassandraClusteringKey ClusteringKey)> _idIndex = new();

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "Cassandra Wide-Column Store";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.TTL |
        PersistenceCapabilities.Replication |
        PersistenceCapabilities.SecondaryIndexes |
        PersistenceCapabilities.Compression;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    /// <summary>
    /// Creates a new Cassandra persistence backend.
    /// </summary>
    /// <param name="config">Backend configuration.</param>
    public CassandraPersistenceBackend(CassandraPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();

        // Simulate connection and schema creation
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        try
        {
            // In production, would execute:
            // CREATE KEYSPACE IF NOT EXISTS ...
            // CREATE TABLE IF NOT EXISTS ...
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

            var clusteringKey = new CassandraClusteringKey
            {
                Tier = record.Tier,
                CreatedAt = record.CreatedAt,
                Id = id
            };

            var row = new CassandraRow
            {
                Id = id,
                Content = record.Content,
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
                ExpiresAt = GetExpiration(record.Tier),
                Version = 1
            };

            // Get or create partition
            if (!_partitions.TryGetValue(record.Scope, out var partition))
            {
                partition = new ConcurrentDictionary<CassandraClusteringKey, CassandraRow>();
                _partitions[record.Scope] = partition;
            }

            // Insert row
            partition[clusteringKey] = row;

            // Update ID index
            _idIndex[id] = (record.Scope, clusteringKey);

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
            // Use ID index for direct lookup
            if (!_idIndex.TryGetValue(id, out var location))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!_partitions.TryGetValue(location.Scope, out var partition))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!partition.TryGetValue(location.ClusteringKey, out var row))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            // Check TTL
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
            if (!_idIndex.TryGetValue(id, out var location))
            {
                throw new KeyNotFoundException($"Record with ID {id} not found");
            }

            if (!_partitions.TryGetValue(location.Scope, out var partition))
            {
                throw new KeyNotFoundException($"Record with ID {id} not found");
            }

            if (!partition.TryGetValue(location.ClusteringKey, out var existingRow))
            {
                throw new KeyNotFoundException($"Record with ID {id} not found");
            }

            // Check for partition key (scope) change - requires delete and re-insert
            if (existingRow.Scope != record.Scope)
            {
                // Delete from old partition
                partition.TryRemove(location.ClusteringKey, out _);
                _idIndex.TryRemove(id, out _);

                // Insert into new partition (will create new clustering key)
                await StoreAsync(record with { Id = id, Version = existingRow.Version + 1 }, ct);
                return;
            }

            // Check for clustering key change (tier or created_at)
            if (existingRow.Tier != record.Tier)
            {
                // Remove old row
                partition.TryRemove(location.ClusteringKey, out _);

                // Create new clustering key
                var newClusteringKey = new CassandraClusteringKey
                {
                    Tier = record.Tier,
                    CreatedAt = record.CreatedAt,
                    Id = id
                };

                var newRow = new CassandraRow
                {
                    Id = id,
                    Content = record.Content,
                    Tier = record.Tier,
                    Scope = record.Scope,
                    Embedding = record.Embedding,
                    Metadata = record.Metadata,
                    CreatedAt = record.CreatedAt,
                    LastAccessedAt = record.LastAccessedAt ?? DateTimeOffset.UtcNow,
                    AccessCount = record.AccessCount,
                    ImportanceScore = record.ImportanceScore,
                    ContentType = record.ContentType,
                    Tags = record.Tags,
                    ExpiresAt = GetExpiration(record.Tier),
                    Version = existingRow.Version + 1
                };

                partition[newClusteringKey] = newRow;
                _idIndex[id] = (record.Scope, newClusteringKey);
            }
            else
            {
                // Simple update - same clustering key
                var updatedRow = existingRow with
                {
                    Content = record.Content,
                    Embedding = record.Embedding,
                    Metadata = record.Metadata,
                    LastAccessedAt = record.LastAccessedAt ?? DateTimeOffset.UtcNow,
                    AccessCount = record.AccessCount,
                    ImportanceScore = record.ImportanceScore,
                    ContentType = record.ContentType,
                    Tags = record.Tags,
                    Version = existingRow.Version + 1
                };

                partition[location.ClusteringKey] = updatedRow;
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
            if (_idIndex.TryRemove(id, out var location))
            {
                if (_partitions.TryGetValue(location.Scope, out var partition))
                {
                    partition.TryRemove(location.ClusteringKey, out _);
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

        if (!_idIndex.TryGetValue(id, out var location))
        {
            return Task.FromResult(false);
        }

        if (!_partitions.TryGetValue(location.Scope, out var partition))
        {
            return Task.FromResult(false);
        }

        if (!partition.TryGetValue(location.ClusteringKey, out var row))
        {
            return Task.FromResult(false);
        }

        // Check TTL
        if (row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
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
            // In Cassandra, BATCH is used for atomicity within a partition
            // For cross-partition batches, we use UNLOGGED BATCH (no atomicity guarantee)

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
        IEnumerable<CassandraRow> rows;

        // Partition key filtering (scope) is required for efficient queries
        if (!string.IsNullOrEmpty(query.Scope))
        {
            // Efficient: query single partition
            if (!_partitions.TryGetValue(query.Scope, out var partition))
            {
                yield break;
            }

            rows = partition.Values;

            // Apply clustering column filters (tier, created_at)
            if (query.Tier.HasValue)
            {
                rows = rows.Where(r => r.Tier == query.Tier.Value);
            }
        }
        else
        {
            // Full table scan - less efficient
            rows = _partitions.Values.SelectMany(p => p.Values);
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
            // Default sort by clustering columns (tier, created_at DESC)
            rows = rows.OrderBy(r => r.Tier).ThenByDescending(r => r.CreatedAt);
        }

        // Apply pagination
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
        IEnumerable<CassandraRow> rows = _partitions.Values.SelectMany(p => p.Values);

        if (query != null)
        {
            if (!string.IsNullOrEmpty(query.Scope))
            {
                if (!_partitions.TryGetValue(query.Scope, out var partition))
                {
                    return Task.FromResult(0L);
                }
                rows = partition.Values;
            }

            rows = ApplyFilters(rows, query, now);
        }
        else
        {
            rows = rows.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now);
        }

        return Task.FromResult((long)rows.Count());
    }

    private IEnumerable<CassandraRow> ApplyFilters(IEnumerable<CassandraRow> rows, MemoryQuery query, DateTimeOffset now)
    {
        if (query.Tier.HasValue)
        {
            rows = rows.Where(r => r.Tier == query.Tier.Value);
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

        if (query.Tags != null && query.Tags.Length > 0)
        {
            rows = rows.Where(r => r.Tags != null && query.Tags.Any(t => r.Tags.Contains(t)));
        }

        if (!query.IncludeExpired)
        {
            rows = rows.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now);
        }

        return rows;
    }

    private IEnumerable<CassandraRow> ApplySorting(IEnumerable<CassandraRow> rows, string sortBy, bool descending)
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
            "tier" => descending
                ? rows.OrderByDescending(r => r.Tier)
                : rows.OrderBy(r => r.Tier),
            _ => rows
        };
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;

        var records = _partitions.Values
            .SelectMany(p => p.Values)
            .Where(r => r.Tier == tier && (!r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now))
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

        if (!_partitions.TryGetValue(scope, out var partition))
        {
            return Task.FromResult<IEnumerable<MemoryRecord>>(Array.Empty<MemoryRecord>());
        }

        var now = DateTimeOffset.UtcNow;

        var records = partition.Values
            .Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now)
            .OrderBy(r => r.Tier)
            .ThenByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    #endregion

    #region Maintenance Operations

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;

        // Remove expired rows (tombstone cleanup)
        foreach (var partition in _partitions.Values)
        {
            var expiredKeys = partition
                .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (partition.TryRemove(key, out var row))
                {
                    _idIndex.TryRemove(row.Id, out _);
                }
            }
        }

        // Remove empty partitions
        var emptyPartitions = _partitions
            .Where(kvp => kvp.Value.IsEmpty)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var partitionKey in emptyPartitions)
        {
            _partitions.TryRemove(partitionKey, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // In production, Cassandra handles durability via commitlog
        // FLUSH forces memtables to SSTables
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var allRows = _partitions.Values
            .SelectMany(p => p.Values)
            .Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now)
            .ToList();

        var recordsByTier = new Dictionary<MemoryTier, long>();
        var sizeByTier = new Dictionary<MemoryTier, long>();

        foreach (var tier in Enum.GetValues<MemoryTier>())
        {
            var tierRows = allRows.Where(r => r.Tier == tier).ToList();
            recordsByTier[tier] = tierRows.Count;
            sizeByTier[tier] = tierRows.Sum(r => r.Content?.Length ?? 0);
        }

        return Task.FromResult(new PersistenceStatistics
        {
            BackendId = BackendId,
            TotalRecords = allRows.Count,
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
            ConnectionPoolSize = _config.CoreConnectionsPerHost,
            IsHealthy = IsConnected,
            HealthCheckTime = DateTimeOffset.UtcNow,
            CustomMetrics = new Dictionary<string, object>
            {
                ["partitionCount"] = _partitions.Count,
                ["readConsistency"] = _config.ReadConsistency.ToString(),
                ["writeConsistency"] = _config.WriteConsistency.ToString(),
                ["compactionStrategy"] = _config.CompactionStrategy.ToString()
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(false);
        if (!_circuitBreaker.AllowOperation()) return Task.FromResult(false);

        // In production, would execute a health check query
        return Task.FromResult(_isConnected);
    }

    #endregion

    #region Private Helpers

    private DateTimeOffset? GetExpiration(MemoryTier tier)
    {
        if (_config.TierTTLSeconds.TryGetValue(tier, out var ttlSeconds) && ttlSeconds > 0)
        {
            return DateTimeOffset.UtcNow.AddSeconds(ttlSeconds);
        }

        if (_config.DefaultTTLSeconds > 0)
        {
            return DateTimeOffset.UtcNow.AddSeconds(_config.DefaultTTLSeconds);
        }

        return null;
    }

    private static MemoryRecord ToMemoryRecord(CassandraRow row)
    {
        return new MemoryRecord
        {
            Id = row.Id,
            Content = row.Content,
            Tier = row.Tier,
            Scope = row.Scope,
            Embedding = row.Embedding,
            Metadata = row.Metadata,
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
            throw new ObjectDisposedException(nameof(CassandraPersistenceBackend));
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

        _partitions.Clear();
        _idIndex.Clear();

        return ValueTask.CompletedTask;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Cassandra clustering key structure.
/// </summary>
internal readonly struct CassandraClusteringKey : IEquatable<CassandraClusteringKey>, IComparable<CassandraClusteringKey>
{
    public MemoryTier Tier { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Id { get; init; }

    public bool Equals(CassandraClusteringKey other)
    {
        return Tier == other.Tier && CreatedAt == other.CreatedAt && Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        return obj is CassandraClusteringKey key && Equals(key);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Tier, CreatedAt, Id);
    }

    public int CompareTo(CassandraClusteringKey other)
    {
        var tierCompare = Tier.CompareTo(other.Tier);
        if (tierCompare != 0) return tierCompare;

        var dateCompare = other.CreatedAt.CompareTo(CreatedAt); // DESC
        if (dateCompare != 0) return dateCompare;

        return string.Compare(Id, other.Id, StringComparison.Ordinal);
    }
}

/// <summary>
/// Cassandra row data.
/// </summary>
internal sealed record CassandraRow
{
    public required string Id { get; init; }
    public required byte[] Content { get; init; }
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

#endregion
