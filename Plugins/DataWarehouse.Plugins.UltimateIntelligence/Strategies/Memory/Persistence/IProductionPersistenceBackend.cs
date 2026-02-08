using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region Core Interfaces and Types

/// <summary>
/// Production-grade persistence backend interface for the tiered memory system.
/// Provides comprehensive CRUD, batch, query, and maintenance operations with
/// full support for distributed deployments, encryption, and replication.
/// </summary>
public interface IProductionPersistenceBackend : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this backend instance.
    /// Format: "{backend-type}-{instance-id}" (e.g., "rocksdb-primary", "redis-cluster-01").
    /// </summary>
    string BackendId { get; }

    /// <summary>
    /// Gets the human-readable display name for this backend.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the capabilities supported by this backend.
    /// </summary>
    PersistenceCapabilities Capabilities { get; }

    /// <summary>
    /// Gets whether the backend is currently connected and operational.
    /// </summary>
    bool IsConnected { get; }

    #region Core CRUD Operations

    /// <summary>
    /// Stores a memory record and returns its assigned ID.
    /// </summary>
    /// <param name="record">The memory record to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assigned record ID.</returns>
    Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a memory record by ID.
    /// </summary>
    /// <param name="id">The record ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The memory record, or null if not found.</returns>
    Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing memory record.
    /// </summary>
    /// <param name="id">The record ID to update.</param>
    /// <param name="record">The updated record.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(string id, MemoryRecord record, CancellationToken ct = default);

    /// <summary>
    /// Deletes a memory record by ID.
    /// </summary>
    /// <param name="id">The record ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Checks if a record exists by ID.
    /// </summary>
    /// <param name="id">The record ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the record exists.</returns>
    Task<bool> ExistsAsync(string id, CancellationToken ct = default);

    #endregion

    #region Batch Operations

    /// <summary>
    /// Stores multiple records in a batch operation.
    /// </summary>
    /// <param name="records">The records to store.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Retrieves multiple records by their IDs.
    /// </summary>
    /// <param name="ids">The record IDs to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The found records (missing IDs are omitted).</returns>
    Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple records by their IDs.
    /// </summary>
    /// <param name="ids">The record IDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default);

    #endregion

    #region Query Operations

    /// <summary>
    /// Queries records based on filter criteria.
    /// </summary>
    /// <param name="query">The query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of matching records.</returns>
    IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, CancellationToken ct = default);

    /// <summary>
    /// Counts records matching the optional query.
    /// </summary>
    /// <param name="query">Optional query filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of matching records.</returns>
    Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default);

    #endregion

    #region Tier Operations

    /// <summary>
    /// Retrieves records by memory tier.
    /// </summary>
    /// <param name="tier">The target tier.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Records in the specified tier.</returns>
    Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Retrieves records by scope.
    /// </summary>
    /// <param name="scope">The target scope.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Records in the specified scope.</returns>
    Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default);

    #endregion

    #region Maintenance Operations

    /// <summary>
    /// Compacts storage by removing deleted entries and optimizing layout.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task CompactAsync(CancellationToken ct = default);

    /// <summary>
    /// Flushes all pending writes to durable storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets current storage statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Persistence statistics.</returns>
    Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if the backend is healthy and operational.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if healthy.</returns>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    #endregion
}

/// <summary>
/// Capabilities supported by persistence backends.
/// </summary>
[Flags]
public enum PersistenceCapabilities
{
    /// <summary>No special capabilities.</summary>
    None = 0,

    /// <summary>Supports ACID transactions.</summary>
    Transactions = 1 << 0,

    /// <summary>Supports data compression.</summary>
    Compression = 1 << 1,

    /// <summary>Supports encryption at rest.</summary>
    Encryption = 1 << 2,

    /// <summary>Supports data replication.</summary>
    Replication = 1 << 3,

    /// <summary>Supports point-in-time snapshots.</summary>
    Snapshots = 1 << 4,

    /// <summary>Supports TTL-based expiration.</summary>
    TTL = 1 << 5,

    /// <summary>Supports secondary indexes.</summary>
    SecondaryIndexes = 1 << 6,

    /// <summary>Supports full-text search.</summary>
    FullTextSearch = 1 << 7,

    /// <summary>Supports change streams/notifications.</summary>
    ChangeStreams = 1 << 8,

    /// <summary>Supports vector similarity search.</summary>
    VectorSearch = 1 << 9,

    /// <summary>Supports geo-spatial queries.</summary>
    GeoSpatial = 1 << 10,

    /// <summary>Supports atomic batch operations.</summary>
    AtomicBatch = 1 << 11,

    /// <summary>Supports optimistic concurrency control.</summary>
    OptimisticConcurrency = 1 << 12,

    /// <summary>Supports distributed locking.</summary>
    DistributedLocking = 1 << 13,

    /// <summary>All local storage capabilities.</summary>
    AllLocal = Transactions | Compression | Encryption | Snapshots | SecondaryIndexes | AtomicBatch,

    /// <summary>All distributed capabilities.</summary>
    AllDistributed = Replication | TTL | ChangeStreams | DistributedLocking,

    /// <summary>All search capabilities.</summary>
    AllSearch = SecondaryIndexes | FullTextSearch | VectorSearch | GeoSpatial
}

/// <summary>
/// Memory record for persistence operations.
/// Contains all data needed for storage, retrieval, and querying.
/// </summary>
public sealed record MemoryRecord
{
    /// <summary>Unique identifier for this record.</summary>
    public required string Id { get; init; }

    /// <summary>Raw content bytes (may be compressed or encrypted).</summary>
    public required byte[] Content { get; init; }

    /// <summary>Memory tier for hierarchical storage.</summary>
    public MemoryTier Tier { get; init; } = MemoryTier.Working;

    /// <summary>Scope/namespace for isolation.</summary>
    public required string Scope { get; init; }

    /// <summary>Vector embedding for semantic search (optional).</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Additional metadata as key-value pairs.</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last accessed.</summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>Number of times this record has been accessed.</summary>
    public long AccessCount { get; init; }

    /// <summary>Importance score for retention decisions (0.0-1.0).</summary>
    public double ImportanceScore { get; init; } = 0.5;

    /// <summary>MIME type of the content.</summary>
    public string? ContentType { get; init; }

    /// <summary>Tags for categorization and filtering.</summary>
    public string[]? Tags { get; init; }

    /// <summary>TTL expiration time (null = never expires).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Version number for optimistic concurrency.</summary>
    public long Version { get; init; } = 1;

    /// <summary>Hash of content for integrity verification.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Size of content in bytes.</summary>
    public long SizeBytes => Content?.Length ?? 0;

    /// <summary>Whether this record has expired.</summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTimeOffset.UtcNow;
}

/// <summary>
/// Query parameters for filtering memory records.
/// </summary>
public sealed record MemoryQuery
{
    /// <summary>Filter by memory tier.</summary>
    public MemoryTier? Tier { get; init; }

    /// <summary>Filter by scope.</summary>
    public string? Scope { get; init; }

    /// <summary>Filter by tags (any match).</summary>
    public string[]? Tags { get; init; }

    /// <summary>Filter by minimum importance score.</summary>
    public double? MinImportanceScore { get; init; }

    /// <summary>Filter by maximum importance score.</summary>
    public double? MaxImportanceScore { get; init; }

    /// <summary>Filter by creation date (from).</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Filter by creation date (to).</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>Filter by last access date (from).</summary>
    public DateTimeOffset? AccessedAfter { get; init; }

    /// <summary>Filter by content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Metadata key-value filters.</summary>
    public Dictionary<string, object>? MetadataFilters { get; init; }

    /// <summary>Full-text search query.</summary>
    public string? TextQuery { get; init; }

    /// <summary>Vector for similarity search.</summary>
    public float[]? SimilarityVector { get; init; }

    /// <summary>Minimum similarity score for vector search.</summary>
    public float? MinSimilarity { get; init; }

    /// <summary>Sort field name.</summary>
    public string? SortBy { get; init; }

    /// <summary>Sort in descending order.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Number of records to skip.</summary>
    public int Skip { get; init; }

    /// <summary>Maximum number of records to return.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Include expired records in results.</summary>
    public bool IncludeExpired { get; init; }
}

/// <summary>
/// Statistics for persistence backend operations.
/// </summary>
public sealed record PersistenceStatistics
{
    /// <summary>Backend identifier.</summary>
    public required string BackendId { get; init; }

    /// <summary>Total number of records stored.</summary>
    public long TotalRecords { get; init; }

    /// <summary>Total storage size in bytes.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Records per tier.</summary>
    public Dictionary<MemoryTier, long> RecordsByTier { get; init; } = new();

    /// <summary>Size per tier in bytes.</summary>
    public Dictionary<MemoryTier, long> SizeByTier { get; init; } = new();

    /// <summary>Number of pending writes.</summary>
    public long PendingWrites { get; init; }

    /// <summary>Number of pending deletes.</summary>
    public long PendingDeletes { get; init; }

    /// <summary>Total read operations.</summary>
    public long TotalReads { get; init; }

    /// <summary>Total write operations.</summary>
    public long TotalWrites { get; init; }

    /// <summary>Total delete operations.</summary>
    public long TotalDeletes { get; init; }

    /// <summary>Cache hit ratio (0.0-1.0).</summary>
    public double CacheHitRatio { get; init; }

    /// <summary>Average read latency in milliseconds.</summary>
    public double AvgReadLatencyMs { get; init; }

    /// <summary>Average write latency in milliseconds.</summary>
    public double AvgWriteLatencyMs { get; init; }

    /// <summary>P99 read latency in milliseconds.</summary>
    public double P99ReadLatencyMs { get; init; }

    /// <summary>P99 write latency in milliseconds.</summary>
    public double P99WriteLatencyMs { get; init; }

    /// <summary>Number of compactions performed.</summary>
    public long CompactionCount { get; init; }

    /// <summary>Last compaction timestamp.</summary>
    public DateTimeOffset? LastCompaction { get; init; }

    /// <summary>Last flush timestamp.</summary>
    public DateTimeOffset? LastFlush { get; init; }

    /// <summary>Number of active connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Connection pool size.</summary>
    public int ConnectionPoolSize { get; init; }

    /// <summary>Whether the backend is healthy.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>Health check timestamp.</summary>
    public DateTimeOffset HealthCheckTime { get; init; }

    /// <summary>Additional backend-specific metrics.</summary>
    public Dictionary<string, object>? CustomMetrics { get; init; }
}

#endregion

#region Backend Configuration

/// <summary>
/// Base configuration for persistence backends.
/// </summary>
public abstract record PersistenceBackendConfig
{
    /// <summary>Unique identifier for this backend instance.</summary>
    public required string BackendId { get; init; }

    /// <summary>Display name for the backend.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether to enable health checks.</summary>
    public bool EnableHealthChecks { get; init; } = true;

    /// <summary>Health check interval in seconds.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 30;

    /// <summary>Whether to enable metrics collection.</summary>
    public bool EnableMetrics { get; init; } = true;

    /// <summary>Whether to enable compression.</summary>
    public bool EnableCompression { get; init; } = true;

    /// <summary>Compression algorithm to use.</summary>
    public CompressionAlgorithm CompressionAlgorithm { get; init; } = CompressionAlgorithm.LZ4;

    /// <summary>Whether to enable encryption at rest.</summary>
    public bool EnableEncryption { get; init; }

    /// <summary>Encryption key (base64 encoded).</summary>
    public string? EncryptionKey { get; init; }

    /// <summary>Maximum retry attempts for failed operations.</summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Retry delay in milliseconds.</summary>
    public int RetryDelayMs { get; init; } = 100;

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeoutMs { get; init; } = 5000;

    /// <summary>Operation timeout in milliseconds.</summary>
    public int OperationTimeoutMs { get; init; } = 30000;
}

/// <summary>
/// Compression algorithms supported by backends.
/// </summary>
public enum CompressionAlgorithm
{
    /// <summary>No compression.</summary>
    None,

    /// <summary>LZ4 fast compression.</summary>
    LZ4,

    /// <summary>Zstandard compression.</summary>
    Zstd,

    /// <summary>Snappy compression.</summary>
    Snappy,

    /// <summary>Gzip compression.</summary>
    Gzip,

    /// <summary>Brotli compression.</summary>
    Brotli
}

#endregion

#region Circuit Breaker

/// <summary>
/// Circuit breaker state for backend health management.
/// </summary>
public enum CircuitBreakerState
{
    /// <summary>Circuit is closed, operations proceed normally.</summary>
    Closed,

    /// <summary>Circuit is open, operations fail fast.</summary>
    Open,

    /// <summary>Circuit is half-open, testing if backend recovered.</summary>
    HalfOpen
}

/// <summary>
/// Circuit breaker for managing backend health and preventing cascading failures.
/// </summary>
public sealed class PersistenceCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly object _lock = new();

    private int _failureCount;
    private DateTimeOffset _lastFailure;
    private DateTimeOffset _openedAt;
    private CircuitBreakerState _state = CircuitBreakerState.Closed;

    /// <summary>
    /// Gets the current circuit breaker state.
    /// </summary>
    public CircuitBreakerState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerState.Open &&
                    DateTimeOffset.UtcNow - _openedAt >= _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                }
                return _state;
            }
        }
    }

    /// <summary>
    /// Creates a new circuit breaker.
    /// </summary>
    /// <param name="failureThreshold">Number of failures before opening.</param>
    /// <param name="openDuration">How long to stay open before testing.</param>
    public PersistenceCircuitBreaker(int failureThreshold = 5, TimeSpan? openDuration = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerState.Closed;
        }
    }

    /// <summary>
    /// Records a failed operation.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailure = DateTimeOffset.UtcNow;

            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Checks if operations should proceed.
    /// </summary>
    /// <returns>True if operations can proceed.</returns>
    public bool AllowOperation()
    {
        var state = State;
        return state == CircuitBreakerState.Closed || state == CircuitBreakerState.HalfOpen;
    }

    /// <summary>
    /// Resets the circuit breaker to closed state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerState.Closed;
        }
    }
}

#endregion

#region Metrics Tracking

/// <summary>
/// Metrics collector for persistence operations.
/// </summary>
public sealed class PersistenceMetrics
{
    private long _totalReads;
    private long _totalWrites;
    private long _totalDeletes;
    private long _cacheHits;
    private long _cacheMisses;
    private long _totalReadLatencyTicks;
    private long _totalWriteLatencyTicks;
    private readonly List<double> _readLatencies = new();
    private readonly List<double> _writeLatencies = new();
    private readonly object _latencyLock = new();

    /// <summary>Total read operations.</summary>
    public long TotalReads => Interlocked.Read(ref _totalReads);

    /// <summary>Total write operations.</summary>
    public long TotalWrites => Interlocked.Read(ref _totalWrites);

    /// <summary>Total delete operations.</summary>
    public long TotalDeletes => Interlocked.Read(ref _totalDeletes);

    /// <summary>Cache hit ratio.</summary>
    public double CacheHitRatio
    {
        get
        {
            var hits = Interlocked.Read(ref _cacheHits);
            var misses = Interlocked.Read(ref _cacheMisses);
            var total = hits + misses;
            return total > 0 ? (double)hits / total : 0;
        }
    }

    /// <summary>Average read latency in milliseconds.</summary>
    public double AvgReadLatencyMs
    {
        get
        {
            var reads = Interlocked.Read(ref _totalReads);
            var ticks = Interlocked.Read(ref _totalReadLatencyTicks);
            return reads > 0 ? TimeSpan.FromTicks(ticks / reads).TotalMilliseconds : 0;
        }
    }

    /// <summary>Average write latency in milliseconds.</summary>
    public double AvgWriteLatencyMs
    {
        get
        {
            var writes = Interlocked.Read(ref _totalWrites);
            var ticks = Interlocked.Read(ref _totalWriteLatencyTicks);
            return writes > 0 ? TimeSpan.FromTicks(ticks / writes).TotalMilliseconds : 0;
        }
    }

    /// <summary>P99 read latency in milliseconds.</summary>
    public double P99ReadLatencyMs
    {
        get
        {
            lock (_latencyLock)
            {
                if (_readLatencies.Count == 0) return 0;
                var sorted = _readLatencies.OrderBy(x => x).ToList();
                var index = (int)(sorted.Count * 0.99);
                return sorted[Math.Min(index, sorted.Count - 1)];
            }
        }
    }

    /// <summary>P99 write latency in milliseconds.</summary>
    public double P99WriteLatencyMs
    {
        get
        {
            lock (_latencyLock)
            {
                if (_writeLatencies.Count == 0) return 0;
                var sorted = _writeLatencies.OrderBy(x => x).ToList();
                var index = (int)(sorted.Count * 0.99);
                return sorted[Math.Min(index, sorted.Count - 1)];
            }
        }
    }

    /// <summary>Records a read operation.</summary>
    public void RecordRead(TimeSpan latency)
    {
        Interlocked.Increment(ref _totalReads);
        Interlocked.Add(ref _totalReadLatencyTicks, latency.Ticks);

        lock (_latencyLock)
        {
            _readLatencies.Add(latency.TotalMilliseconds);
            if (_readLatencies.Count > 10000)
                _readLatencies.RemoveAt(0);
        }
    }

    /// <summary>Records a write operation.</summary>
    public void RecordWrite(TimeSpan latency)
    {
        Interlocked.Increment(ref _totalWrites);
        Interlocked.Add(ref _totalWriteLatencyTicks, latency.Ticks);

        lock (_latencyLock)
        {
            _writeLatencies.Add(latency.TotalMilliseconds);
            if (_writeLatencies.Count > 10000)
                _writeLatencies.RemoveAt(0);
        }
    }

    /// <summary>Records a delete operation.</summary>
    public void RecordDelete()
    {
        Interlocked.Increment(ref _totalDeletes);
    }

    /// <summary>Records a cache hit.</summary>
    public void RecordCacheHit()
    {
        Interlocked.Increment(ref _cacheHits);
    }

    /// <summary>Records a cache miss.</summary>
    public void RecordCacheMiss()
    {
        Interlocked.Increment(ref _cacheMisses);
    }

    /// <summary>Resets all metrics.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalReads, 0);
        Interlocked.Exchange(ref _totalWrites, 0);
        Interlocked.Exchange(ref _totalDeletes, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _totalReadLatencyTicks, 0);
        Interlocked.Exchange(ref _totalWriteLatencyTicks, 0);

        lock (_latencyLock)
        {
            _readLatencies.Clear();
            _writeLatencies.Clear();
        }
    }
}

#endregion
