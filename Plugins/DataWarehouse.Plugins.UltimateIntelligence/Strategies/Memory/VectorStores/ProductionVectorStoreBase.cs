using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Circuit breaker states for resilient operations.
/// </summary>
public enum CircuitState
{
    /// <summary>Circuit is closed, operations proceed normally.</summary>
    Closed,

    /// <summary>Circuit is open, operations fail fast.</summary>
    Open,

    /// <summary>Circuit is testing, allowing one operation through.</summary>
    HalfOpen
}

/// <summary>
/// Thread-safe circuit breaker implementation for resilient vector store operations.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _resetTimeout;
    private int _failureCount;
    private DateTime _lastFailureTime;
    private CircuitState _state = CircuitState.Closed;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new circuit breaker.
    /// </summary>
    /// <param name="failureThreshold">Number of failures before opening.</param>
    /// <param name="resetTimeout">Time before attempting to close.</param>
    public CircuitBreaker(int failureThreshold, TimeSpan resetTimeout)
    {
        _failureThreshold = failureThreshold;
        _resetTimeout = resetTimeout;
    }

    /// <summary>
    /// Gets the current circuit state.
    /// </summary>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitState.Open && DateTime.UtcNow - _lastFailureTime >= _resetTimeout)
                {
                    _state = CircuitState.HalfOpen;
                }
                return _state;
            }
        }
    }

    /// <summary>
    /// Checks if the circuit allows an operation.
    /// </summary>
    public bool AllowRequest()
    {
        lock (_lock)
        {
            var state = State;
            return state == CircuitState.Closed || state == CircuitState.HalfOpen;
        }
    }

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitState.Closed;
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
            _lastFailureTime = DateTime.UtcNow;
            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitState.Open;
            }
        }
    }
}

/// <summary>
/// Metrics collector for vector store operations.
/// </summary>
public sealed class VectorStoreMetrics
{
    private long _queryCount;
    private long _upsertCount;
    private long _deleteCount;
    private long _totalQueryLatencyTicks;
    private long _totalUpsertLatencyTicks;
    private long _errorCount;
    private readonly ConcurrentQueue<(DateTime time, double latencyMs)> _recentLatencies = new();
    private const int MaxRecentLatencies = 1000;

    /// <summary>Total queries performed.</summary>
    public long QueryCount => Interlocked.Read(ref _queryCount);

    /// <summary>Total upserts performed.</summary>
    public long UpsertCount => Interlocked.Read(ref _upsertCount);

    /// <summary>Total deletes performed.</summary>
    public long DeleteCount => Interlocked.Read(ref _deleteCount);

    /// <summary>Total errors encountered.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>Average query latency in milliseconds.</summary>
    public double AverageQueryLatencyMs
    {
        get
        {
            var count = Interlocked.Read(ref _queryCount);
            return count > 0 ? Interlocked.Read(ref _totalQueryLatencyTicks) / 10000.0 / count : 0;
        }
    }

    /// <summary>Average upsert latency in milliseconds.</summary>
    public double AverageUpsertLatencyMs
    {
        get
        {
            var count = Interlocked.Read(ref _upsertCount);
            return count > 0 ? Interlocked.Read(ref _totalUpsertLatencyTicks) / 10000.0 / count : 0;
        }
    }

    /// <summary>Records a query operation.</summary>
    public void RecordQuery(TimeSpan latency)
    {
        Interlocked.Increment(ref _queryCount);
        Interlocked.Add(ref _totalQueryLatencyTicks, latency.Ticks);
        AddRecentLatency(latency.TotalMilliseconds);
    }

    /// <summary>Records an upsert operation.</summary>
    public void RecordUpsert(TimeSpan latency, int count = 1)
    {
        Interlocked.Add(ref _upsertCount, count);
        Interlocked.Add(ref _totalUpsertLatencyTicks, latency.Ticks);
    }

    /// <summary>Records a delete operation.</summary>
    public void RecordDelete(int count = 1)
    {
        Interlocked.Add(ref _deleteCount, count);
    }

    /// <summary>Records an error.</summary>
    public void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
    }

    private void AddRecentLatency(double latencyMs)
    {
        _recentLatencies.Enqueue((DateTime.UtcNow, latencyMs));
        while (_recentLatencies.Count > MaxRecentLatencies)
        {
            _recentLatencies.TryDequeue(out _);
        }
    }

    /// <summary>Gets percentile latency from recent queries.</summary>
    public double GetPercentileLatency(int percentile)
    {
        var latencies = _recentLatencies.Select(x => x.latencyMs).OrderBy(x => x).ToList();
        if (latencies.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * latencies.Count) - 1;
        return latencies[Math.Max(0, Math.Min(index, latencies.Count - 1))];
    }
}

/// <summary>
/// Abstract base class for production vector stores with common resilience patterns.
/// </summary>
public abstract class ProductionVectorStoreBase : IProductionVectorStore
{
    /// <summary>Configuration options.</summary>
    protected readonly VectorStoreOptions Options;

    /// <summary>HTTP client for REST-based stores.</summary>
    protected readonly HttpClient HttpClient;

    /// <summary>Circuit breaker for fault tolerance.</summary>
    protected readonly CircuitBreaker CircuitBreaker;

    /// <summary>Metrics collector.</summary>
    protected readonly VectorStoreMetrics Metrics = new();

    /// <summary>Random for jitter in retry delays.</summary>
    private readonly Random _jitter = new();

    /// <summary>Whether the store has been disposed.</summary>
    protected bool IsDisposed;

    /// <inheritdoc/>
    public abstract string StoreId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract int VectorDimensions { get; }

    /// <summary>
    /// Creates a new instance of the vector store base.
    /// </summary>
    /// <param name="httpClient">HTTP client for REST operations.</param>
    /// <param name="options">Configuration options.</param>
    protected ProductionVectorStoreBase(HttpClient? httpClient = null, VectorStoreOptions? options = null)
    {
        Options = options ?? new VectorStoreOptions();
        HttpClient = httpClient ?? CreateDefaultHttpClient();
        CircuitBreaker = new CircuitBreaker(
            Options.CircuitBreakerThreshold,
            TimeSpan.FromMilliseconds(Options.CircuitBreakerResetMs));
    }

    private HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            MaxConnectionsPerServer = 20
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(Options.OperationTimeoutMs)
        };
    }

    /// <summary>
    /// Executes an operation with retry, circuit breaker, and metrics.
    /// </summary>
    protected async Task<T> ExecuteWithResilienceAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (!CircuitBreaker.AllowRequest())
        {
            throw new InvalidOperationException($"Circuit breaker is open for {StoreId}. Operation: {operationName}");
        }

        var sw = Stopwatch.StartNew();
        var lastException = default(Exception);

        for (int attempt = 0; attempt <= Options.MaxRetries; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var result = await operation(ct);
                sw.Stop();

                CircuitBreaker.RecordSuccess();

                if (Options.EnableMetrics)
                {
                    if (operationName.Contains("Search", StringComparison.OrdinalIgnoreCase))
                    {
                        Metrics.RecordQuery(sw.Elapsed);
                    }
                    else if (operationName.Contains("Upsert", StringComparison.OrdinalIgnoreCase))
                    {
                        Metrics.RecordUpsert(sw.Elapsed);
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < Options.MaxRetries)
            {
                lastException = ex;
                var delay = CalculateRetryDelay(attempt);
                await Task.Delay(delay, ct);
            }
            catch
            {
                sw.Stop();
                CircuitBreaker.RecordFailure();
                Metrics.RecordError();
                throw;
            }
        }

        CircuitBreaker.RecordFailure();
        Metrics.RecordError();
        throw new AggregateException($"Operation {operationName} failed after {Options.MaxRetries + 1} attempts", lastException!);
    }

    /// <summary>
    /// Executes a void operation with retry and circuit breaker.
    /// </summary>
    protected async Task ExecuteWithResilienceAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            await operation(token);
            return true;
        }, operationName, ct);
    }

    /// <summary>
    /// Determines if an exception is transient and should be retried.
    /// </summary>
    protected virtual bool IsTransientError(Exception ex)
    {
        return ex is HttpRequestException ||
               ex is TimeoutException ||
               ex is TaskCanceledException ||
               (ex is AggregateException agg && agg.InnerExceptions.Any(IsTransientError));
    }

    private TimeSpan CalculateRetryDelay(int attempt)
    {
        var baseDelay = Options.InitialRetryDelayMs * Math.Pow(2, attempt);
        var jitter = _jitter.Next(0, (int)(baseDelay * 0.3));
        var delay = Math.Min(baseDelay + jitter, Options.MaxRetryDelayMs);
        return TimeSpan.FromMilliseconds(delay);
    }

    /// <summary>
    /// Splits a collection into batches for bulk operations.
    /// </summary>
    protected IEnumerable<IEnumerable<T>> BatchItems<T>(IEnumerable<T> items, int batchSize)
    {
        var batch = new List<T>(batchSize);
        foreach (var item in items)
        {
            batch.Add(item);
            if (batch.Count >= batchSize)
            {
                yield return batch;
                batch = new List<T>(batchSize);
            }
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    #region Abstract Methods

    /// <inheritdoc/>
    public abstract Task UpsertAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DeleteAsync(string id, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IEnumerable<VectorSearchResult>> SearchAsync(float[] query, int topK = 10, float minScore = 0f, Dictionary<string, object>? filter = null, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task CreateCollectionAsync(string name, int dimensions, DistanceMetric metric = DistanceMetric.Cosine, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task DeleteCollectionAsync(string name, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<bool> IsHealthyAsync(CancellationToken ct = default);

    #endregion

    /// <inheritdoc/>
    public virtual async Task<IEnumerable<VectorSearchResult>> SearchByTextAsync(
        string text,
        IEmbeddingProvider embedder,
        int topK = 10,
        CancellationToken ct = default)
    {
        var vector = await embedder.EmbedAsync(text, ct);
        return await SearchAsync(vector, topK, ct: ct);
    }

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            HttpClient.Dispose();
        }
        await ValueTask.CompletedTask;
    }
}
