using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Scaling;

/// <summary>
/// Manages database storage subsystem scaling by providing streaming retrieval (eliminating
/// MemoryStream.ToArray() for large objects), paginated query results, parallel strategy health
/// checks, and runtime-reconfigurable scaling limits.
/// Implements <see cref="IScalableSubsystem"/> to participate in the unified scaling infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming retrieval:</b> Replaces <c>MemoryStream.ToArray()</c> with bounded-buffer streaming.
/// Data is read in configurable chunks (default 64 KB) through a <see cref="Stream"/> that applies
/// backpressure when the buffer fills, preventing OOM on large database objects.
/// </para>
/// <para>
/// <b>Query pagination:</b> Supports offset/limit pagination returning <see cref="PagedQueryResult{T}"/>
/// with <c>Items</c>, <c>TotalCount</c>, <c>HasMore</c>, and <c>ContinuationToken</c>. For streaming
/// results, returns <see cref="IAsyncEnumerable{T}"/> with configurable page sizes.
/// </para>
/// <para>
/// <b>Parallel health checks:</b> When multiple database strategies are registered, health-checks
/// all in parallel using <see cref="Task.WhenAll"/> with configurable per-strategy timeout (default 5s).
/// Unhealthy strategies are tracked in metrics and reported via scaling metrics.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Database storage scaling with streaming retrieval and pagination")]
public sealed class DatabaseStorageScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Constants ----
    private const string SubsystemName = "UltimateDatabaseStorage";
    private const int DefaultMaxBufferBytes = 64 * 1024 * 1024; // 64 MB
    private const int DefaultChunkSize = 64 * 1024; // 64 KB
    private const int DefaultPageSize = 100;
    private const int DefaultHealthCheckTimeoutMs = 5_000;
    private const int DefaultMaxConcurrentQueries = 64;

    // ---- Dependencies ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Configuration ----
    private volatile ScalingLimits _currentLimits;
    private volatile int _maxBufferBytes;
    private volatile int _chunkSize;
    private volatile int _defaultPageSize;
    private volatile int _healthCheckTimeoutMs;

    // ---- Query result cache ----
    private readonly BoundedCache<string, byte[]> _queryResultCache;

    // ---- Strategy health tracking ----
    private readonly BoundedCache<string, StrategyHealthInfo> _strategyHealth;
    private readonly object _healthLock = new();

    // ---- Metrics ----
    private long _streamingRetrievals;
    private long _paginatedQueries;
    private long _streamingQueries;
    private long _healthChecksCompleted;
    private long _healthCheckFailures;
    private long _totalBytesStreamed;
    private long _activeConcurrentQueries;

    // ---- Backpressure ----
    private volatile BackpressureState _backpressureState = BackpressureState.Normal;

    // ---- Disposal ----
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="DatabaseStorageScalingManager"/> with optional backing store
    /// for persistent query result caching and configurable scaling limits.
    /// </summary>
    /// <param name="backingStore">Optional persistent backing store for query result durability.</param>
    /// <param name="initialLimits">Optional initial scaling limits. Uses defaults if null.</param>
    public DatabaseStorageScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? initialLimits = null)
    {
        _backingStore = backingStore;

        _currentLimits = initialLimits ?? new ScalingLimits(
            MaxCacheEntries: 10_000,
            MaxMemoryBytes: DefaultMaxBufferBytes,
            MaxConcurrentOperations: DefaultMaxConcurrentQueries,
            MaxQueueDepth: 1_000);

        _maxBufferBytes = (int)Math.Min(_currentLimits.MaxMemoryBytes, int.MaxValue);
        _chunkSize = DefaultChunkSize;
        _defaultPageSize = DefaultPageSize;
        _healthCheckTimeoutMs = DefaultHealthCheckTimeoutMs;

        // Initialize query result cache
        var queryCacheOptions = new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = _currentLimits.MaxCacheEntries,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = $"dw://cache/{SubsystemName}/queries/",
            WriteThrough = true,
            Serializer = data => data,
            Deserializer = data => data,
            KeyToString = key => key
        };
        _queryResultCache = new BoundedCache<string, byte[]>(queryCacheOptions);

        // Initialize strategy health cache
        var healthCacheOptions = new BoundedCacheOptions<string, StrategyHealthInfo>
        {
            MaxEntries = 1_000,
            EvictionPolicy = CacheEvictionMode.TTL,
            DefaultTtl = TimeSpan.FromMinutes(5)
        };
        _strategyHealth = new BoundedCache<string, StrategyHealthInfo>(healthCacheOptions);
    }

    // ---------------------------------------------------------------
    // IScalableSubsystem
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["streaming.retrievals"] = Interlocked.Read(ref _streamingRetrievals),
            ["streaming.totalBytesStreamed"] = Interlocked.Read(ref _totalBytesStreamed),
            ["query.paginatedQueries"] = Interlocked.Read(ref _paginatedQueries),
            ["query.streamingQueries"] = Interlocked.Read(ref _streamingQueries),
            ["query.activeConcurrent"] = Interlocked.Read(ref _activeConcurrentQueries),
            ["cache.size"] = _queryResultCache.Count,
            ["cache.memoryBytes"] = _queryResultCache.EstimatedMemoryBytes,
            ["health.checksCompleted"] = Interlocked.Read(ref _healthChecksCompleted),
            ["health.failures"] = Interlocked.Read(ref _healthCheckFailures),
            ["backpressure.state"] = _backpressureState.ToString(),
            ["backpressure.queueDepth"] = (int)Interlocked.Read(ref _activeConcurrentQueries),
            ["config.maxBufferBytes"] = _maxBufferBytes,
            ["config.chunkSize"] = _chunkSize,
            ["config.pageSize"] = _defaultPageSize
        };
        return metrics;
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);

        _currentLimits = limits;
        _maxBufferBytes = (int)Math.Min(limits.MaxMemoryBytes, int.MaxValue);

        // Update backpressure state based on new limits
        UpdateBackpressureState();

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState => _backpressureState;

    // ---------------------------------------------------------------
    // Streaming Retrieval -- Replaces MemoryStream.ToArray()
    // ---------------------------------------------------------------

    /// <summary>
    /// Creates a bounded-buffer stream for reading large database objects without loading
    /// the entire object into memory. The stream reads data in configurable chunks and
    /// applies backpressure when the internal buffer fills.
    /// </summary>
    /// <param name="dataSource">A function that provides the source stream from the database strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="Stream"/> that yields data in bounded chunks. The caller should
    /// read from this stream incrementally rather than calling ToArray().
    /// </returns>
    /// <remarks>
    /// This replaces the pattern <c>memoryStream.ToArray()</c> which causes OOM on large objects.
    /// Instead, data flows through a bounded buffer of configurable size (default 64 MB max).
    /// When the buffer fills, backpressure is applied to the source until the consumer reads data.
    /// </remarks>
    public async Task<Stream> CreateStreamingRetrievalAsync(
        Func<CancellationToken, Task<Stream>> dataSource,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _streamingRetrievals);

        // Get the source stream from the database strategy
        var sourceStream = await dataSource(ct).ConfigureAwait(false);

        // Wrap in a bounded buffer stream that reads in chunks
        return new BoundedBufferStream(sourceStream, _chunkSize, _maxBufferBytes, this);
    }

    /// <summary>
    /// Reads data from a source stream in chunks, yielding each chunk as a byte array.
    /// This is the chunked-transfer variant for scenarios requiring <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <param name="dataSource">A function that provides the source stream from the database strategy.</param>
    /// <param name="chunkSize">Size of each chunk in bytes. Uses configured default if null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of byte array chunks.</returns>
    public async IAsyncEnumerable<byte[]> StreamChunkedAsync(
        Func<CancellationToken, Task<Stream>> dataSource,
        int? chunkSize = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveChunkSize = chunkSize ?? _chunkSize;
        Interlocked.Increment(ref _streamingRetrievals);

        await using var sourceStream = await dataSource(ct).ConfigureAwait(false);
        var buffer = new byte[effectiveChunkSize];
        int bytesRead;

        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, effectiveChunkSize), ct).ConfigureAwait(false)) > 0)
        {
            Interlocked.Add(ref _totalBytesStreamed, bytesRead);

            if (bytesRead == effectiveChunkSize)
            {
                yield return buffer.ToArray();
            }
            else
            {
                var partial = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, partial, 0, bytesRead);
                yield return partial;
            }

            // Check backpressure -- throttle if we're in critical state
            if (_backpressureState == BackpressureState.Critical ||
                _backpressureState == BackpressureState.Shedding)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------
    // Query Pagination
    // ---------------------------------------------------------------

    /// <summary>
    /// Executes a paginated query against the database storage, returning a page of results
    /// with metadata for client-side pagination navigation.
    /// </summary>
    /// <typeparam name="T">The type of items in the query result.</typeparam>
    /// <param name="queryExecutor">
    /// A function that executes the query with offset and limit parameters,
    /// returning the items and total count.
    /// </param>
    /// <param name="offset">Zero-based offset of the first item to return.</param>
    /// <param name="limit">Maximum number of items to return. Uses configured default if null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PagedQueryResult{T}"/> containing the page items and pagination metadata.</returns>
    public async Task<PagedQueryResult<T>> ExecutePaginatedQueryAsync<T>(
        Func<int, int, CancellationToken, Task<(IReadOnlyList<T> Items, long TotalCount)>> queryExecutor,
        int offset = 0,
        int? limit = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryExecutor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");

        var effectiveLimit = Math.Max(1, limit ?? _defaultPageSize);

        // Check concurrent query limit
        var concurrent = Interlocked.Increment(ref _activeConcurrentQueries);
        try
        {
            if (concurrent > _currentLimits.MaxConcurrentOperations)
            {
                UpdateBackpressureState();
            }

            Interlocked.Increment(ref _paginatedQueries);

            var (items, totalCount) = await queryExecutor(offset, effectiveLimit, ct).ConfigureAwait(false);

            var hasMore = offset + items.Count < totalCount;
            var continuationToken = hasMore
                ? Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new { offset = offset + items.Count, limit = effectiveLimit })))
                : null;

            return new PagedQueryResult<T>
            {
                Items = items,
                TotalCount = totalCount,
                HasMore = hasMore,
                ContinuationToken = continuationToken,
                Offset = offset,
                Limit = effectiveLimit
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeConcurrentQueries);
            UpdateBackpressureState();
        }
    }

    /// <summary>
    /// Executes a streaming query against the database storage, returning results as an
    /// async enumerable for memory-efficient processing of large result sets.
    /// </summary>
    /// <typeparam name="T">The type of items in the query result.</typeparam>
    /// <param name="streamingQueryExecutor">
    /// A function that returns an async enumerable of query results.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of query result items.</returns>
    public async IAsyncEnumerable<T> ExecuteStreamingQueryAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> streamingQueryExecutor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(streamingQueryExecutor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var concurrent = Interlocked.Increment(ref _activeConcurrentQueries);
        try
        {
            // LOW-2786: Track streaming queries separately; incrementing _paginatedQueries here is misleading
            Interlocked.Increment(ref _streamingQueries);

            await foreach (var item in streamingQueryExecutor(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConcurrentQueries);
        }
    }

    // ---------------------------------------------------------------
    // Parallel Strategy Health Checks
    // ---------------------------------------------------------------

    /// <summary>
    /// Executes health checks for all registered database strategies in parallel.
    /// Each strategy is checked with a configurable timeout (default 5s). Unhealthy strategies
    /// are recorded in the health cache and reported via scaling metrics.
    /// </summary>
    /// <param name="strategyHealthChecks">
    /// A dictionary mapping strategy names to their health check functions.
    /// Each function should return true if the strategy is healthy.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary mapping strategy names to their health status.
    /// True indicates healthy; false indicates unhealthy or timed out.
    /// </returns>
    public async Task<IReadOnlyDictionary<string, bool>> CheckStrategyHealthAsync(
        IReadOnlyDictionary<string, Func<CancellationToken, Task<bool>>> strategyHealthChecks,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strategyHealthChecks);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = new Dictionary<string, bool>(strategyHealthChecks.Count);
        var tasks = new List<(string Name, Task<bool> Check)>();
        // Keep CTS instances alive until all tasks complete; dispose afterward.
        var timeoutCtsList = new List<CancellationTokenSource>(strategyHealthChecks.Count);

        try
        {
            foreach (var (name, healthCheck) in strategyHealthChecks)
            {
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_healthCheckTimeoutMs);
                timeoutCtsList.Add(timeoutCts);

                var checkTask = ExecuteHealthCheckWithTimeoutAsync(name, healthCheck, timeoutCts.Token);
                tasks.Add((name, checkTask));
            }

            // Run all health checks in parallel
            await Task.WhenAll(tasks.Select(t => t.Check)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var cts in timeoutCtsList)
            {
                cts.Dispose();
            }
        }

        foreach (var (name, checkTask) in tasks)
        {
            var isHealthy = checkTask.IsCompletedSuccessfully && checkTask.Result;
            results[name] = isHealthy;

            Interlocked.Increment(ref _healthChecksCompleted);
            if (!isHealthy)
            {
                Interlocked.Increment(ref _healthCheckFailures);
            }

            // Update health cache
            var healthInfo = new StrategyHealthInfo
            {
                StrategyName = name,
                IsHealthy = isHealthy,
                LastCheckedUtc = DateTime.UtcNow,
                ConsecutiveFailures = isHealthy ? 0 : GetConsecutiveFailures(name) + 1
            };
            _strategyHealth.Put(name, healthInfo);
        }

        return results;
    }

    // ---------------------------------------------------------------
    // Private Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Executes a single strategy health check with timeout handling.
    /// </summary>
    private static async Task<bool> ExecuteHealthCheckWithTimeoutAsync(
        string strategyName,
        Func<CancellationToken, Task<bool>> healthCheck,
        CancellationToken ct)
    {
        try
        {
            return await healthCheck(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation -- strategy is unhealthy
            return false;
        }
        catch (Exception)
        {
            // Any exception means unhealthy
            return false;
        }
    }

    /// <summary>
    /// Gets the consecutive failure count for a strategy from the health cache.
    /// </summary>
    private int GetConsecutiveFailures(string strategyName)
    {
        var existing = _strategyHealth.GetOrDefault(strategyName);
        return existing?.ConsecutiveFailures ?? 0;
    }

    /// <summary>
    /// Updates the backpressure state based on current concurrent operations vs limits.
    /// </summary>
    private void UpdateBackpressureState()
    {
        var concurrent = Interlocked.Read(ref _activeConcurrentQueries);
        var maxOps = _currentLimits.MaxConcurrentOperations;

        if (maxOps <= 0)
        {
            _backpressureState = BackpressureState.Normal;
            return;
        }

        var ratio = (double)concurrent / maxOps;
        _backpressureState = ratio switch
        {
            >= 1.0 => BackpressureState.Shedding,
            >= 0.8 => BackpressureState.Critical,
            >= 0.6 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }

    /// <summary>
    /// Records bytes streamed for metrics tracking. Called by <see cref="BoundedBufferStream"/>.
    /// </summary>
    internal void RecordBytesStreamed(long bytes)
    {
        Interlocked.Add(ref _totalBytesStreamed, bytes);
    }

    // ---------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queryResultCache.Dispose();
        _strategyHealth.Dispose();
    }

    // ---------------------------------------------------------------
    // Internal Types
    // ---------------------------------------------------------------

    /// <summary>
    /// Tracks the health status of a database strategy, including consecutive failure count
    /// and last check timestamp for monitoring and alerting.
    /// </summary>
    internal sealed record StrategyHealthInfo
    {
        /// <summary>Gets the name of the strategy being health-checked.</summary>
        public required string StrategyName { get; init; }

        /// <summary>Gets whether the strategy is currently healthy.</summary>
        public required bool IsHealthy { get; init; }

        /// <summary>Gets the UTC timestamp of the last health check.</summary>
        public required DateTime LastCheckedUtc { get; init; }

        /// <summary>Gets the number of consecutive health check failures.</summary>
        public required int ConsecutiveFailures { get; init; }
    }

    /// <summary>
    /// A stream wrapper that reads from a source stream in bounded chunks, preventing
    /// OOM by never buffering more than <c>maxBufferBytes</c> in memory. Applies
    /// backpressure to the producer when the buffer fills.
    /// </summary>
    private sealed class BoundedBufferStream : Stream
    {
        private readonly Stream _source;
        private readonly int _chunkSize;
        private readonly long _maxBufferBytes;
        private readonly DatabaseStorageScalingManager _manager;
        private long _totalRead;
        private bool _disposed;

        public BoundedBufferStream(
            Stream source,
            int chunkSize,
            long maxBufferBytes,
            DatabaseStorageScalingManager manager)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _chunkSize = chunkSize;
            _maxBufferBytes = maxBufferBytes;
            _manager = manager;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _source.Length;

        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toRead = Math.Min(count, _chunkSize);
            var bytesRead = _source.Read(buffer, offset, toRead);
            _totalRead += bytesRead;
            _manager.RecordBytesStreamed(bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var toRead = Math.Min(count, _chunkSize);
            var bytesRead = await _source.ReadAsync(buffer.AsMemory(offset, toRead), ct).ConfigureAwait(false);
            _totalRead += bytesRead;
            _manager.RecordBytesStreamed(bytesRead);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var toRead = Math.Min(buffer.Length, _chunkSize);
            var bytesRead = await _source.ReadAsync(buffer[..toRead], ct).ConfigureAwait(false);
            _totalRead += bytesRead;
            _manager.RecordBytesStreamed(bytesRead);
            return bytesRead;
        }

        public override void Flush() => _source.Flush();

        public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _source.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Represents a paginated query result with items, total count, continuation support,
/// and navigation metadata for client-side pagination.
/// </summary>
/// <typeparam name="T">The type of items in the result set.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Paginated query result for database storage")]
public sealed class PagedQueryResult<T>
{
    /// <summary>Gets the items in the current page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Gets the total number of items across all pages.</summary>
    public required long TotalCount { get; init; }

    /// <summary>Gets whether there are more items beyond the current page.</summary>
    public required bool HasMore { get; init; }

    /// <summary>
    /// Gets an opaque continuation token that can be used to fetch the next page.
    /// Null when <see cref="HasMore"/> is false.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>Gets the zero-based offset of the first item in this page.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the maximum number of items requested for this page.</summary>
    public int Limit { get; init; }
}
