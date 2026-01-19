using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.SDK.Infrastructure
{
    #region Log Level

    /// <summary>
    /// Defines the severity levels for structured logging.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// Trace-level logging for detailed diagnostic information.
        /// </summary>
        Trace = 0,

        /// <summary>
        /// Debug-level logging for development and troubleshooting.
        /// </summary>
        Debug = 1,

        /// <summary>
        /// Informational messages about normal application flow.
        /// </summary>
        Information = 2,

        /// <summary>
        /// Warning messages for potentially harmful situations.
        /// </summary>
        Warning = 3,

        /// <summary>
        /// Error messages for failure events that don't stop the application.
        /// </summary>
        Error = 4,

        /// <summary>
        /// Critical messages for severe failures requiring immediate attention.
        /// </summary>
        Critical = 5
    }

    #endregion

    #region Health Check

    /// <summary>
    /// Defines the health status of a component or service.
    /// </summary>
    public enum ObsHealthStatus
    {
        /// <summary>
        /// The component is fully healthy and operational.
        /// </summary>
        Healthy = 0,

        /// <summary>
        /// The component is operational but experiencing issues.
        /// </summary>
        Degraded = 1,

        /// <summary>
        /// The component is not operational or has critical issues.
        /// </summary>
        Unhealthy = 2
    }

    /// <summary>
    /// Represents the result of a health check operation.
    /// This class is immutable and thread-safe.
    /// </summary>
    public sealed class ObsHealthCheckResult
    {
        /// <summary>
        /// Gets the health status of the checked component.
        /// </summary>
        public ObsHealthStatus Status { get; }

        /// <summary>
        /// Gets the human-readable message describing the health state.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the duration of the health check operation.
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets additional data associated with the health check result.
        /// The returned dictionary is a copy; modifications do not affect the original.
        /// </summary>
        public IReadOnlyDictionary<string, object> Data { get; }

        /// <summary>
        /// Gets the timestamp when this health check was performed.
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Gets the exception that caused the health check to fail, if any.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Gets the name of the health check that produced this result.
        /// </summary>
        public string? CheckName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObsHealthCheckResult"/> class.
        /// </summary>
        /// <param name="status">The health status.</param>
        /// <param name="message">A descriptive message about the health state.</param>
        /// <param name="duration">The duration of the health check operation.</param>
        /// <param name="data">Optional additional data associated with the result.</param>
        /// <param name="exception">Optional exception if the check failed.</param>
        /// <param name="checkName">Optional name of the health check.</param>
        public ObsHealthCheckResult(
            ObsHealthStatus status,
            string message,
            TimeSpan duration,
            IReadOnlyDictionary<string, object>? data = null,
            Exception? exception = null,
            string? checkName = null)
        {
            Status = status;
            Message = message ?? string.Empty;
            Duration = duration;
            Data = data != null
                ? new Dictionary<string, object>(data)
                : new Dictionary<string, object>();
            Timestamp = DateTimeOffset.UtcNow;
            Exception = exception;
            CheckName = checkName;
        }

        /// <summary>
        /// Creates a healthy result with the specified message.
        /// </summary>
        /// <param name="message">Optional message describing the healthy state.</param>
        /// <param name="duration">The duration of the health check.</param>
        /// <param name="data">Optional additional data.</param>
        /// <returns>A healthy <see cref="ObsHealthCheckResult"/>.</returns>
        public static ObsHealthCheckResult Healthy(
            string message = "Component is healthy",
            TimeSpan? duration = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new ObsHealthCheckResult(
                ObsHealthStatus.Healthy,
                message,
                duration ?? TimeSpan.Zero,
                data);
        }

        /// <summary>
        /// Creates a degraded result with the specified message.
        /// </summary>
        /// <param name="message">Message describing the degraded state.</param>
        /// <param name="duration">The duration of the health check.</param>
        /// <param name="data">Optional additional data.</param>
        /// <param name="exception">Optional exception that caused degradation.</param>
        /// <returns>A degraded <see cref="ObsHealthCheckResult"/>.</returns>
        public static ObsHealthCheckResult Degraded(
            string message,
            TimeSpan? duration = null,
            IReadOnlyDictionary<string, object>? data = null,
            Exception? exception = null)
        {
            return new ObsHealthCheckResult(
                ObsHealthStatus.Degraded,
                message,
                duration ?? TimeSpan.Zero,
                data,
                exception);
        }

        /// <summary>
        /// Creates an unhealthy result with the specified message.
        /// </summary>
        /// <param name="message">Message describing the unhealthy state.</param>
        /// <param name="duration">The duration of the health check.</param>
        /// <param name="data">Optional additional data.</param>
        /// <param name="exception">Optional exception that caused the failure.</param>
        /// <returns>An unhealthy <see cref="ObsHealthCheckResult"/>.</returns>
        public static ObsHealthCheckResult Unhealthy(
            string message,
            TimeSpan? duration = null,
            IReadOnlyDictionary<string, object>? data = null,
            Exception? exception = null)
        {
            return new ObsHealthCheckResult(
                ObsHealthStatus.Unhealthy,
                message,
                duration ?? TimeSpan.Zero,
                data,
                exception);
        }
    }

    /// <summary>
    /// Defines a contract for performing health checks on a component or service.
    /// Implementations should be thread-safe and handle their own timeouts.
    /// </summary>
    public interface IObsHealthCheck
    {
        /// <summary>
        /// Gets the unique name of this health check.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Performs the health check asynchronously.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A <see cref="ObsHealthCheckResult"/> describing the component's health.</returns>
        Task<ObsHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the type of health check for Kubernetes-style probes.
    /// </summary>
    public enum HealthCheckType
    {
        /// <summary>
        /// Liveness check - determines if the application should be restarted.
        /// </summary>
        Liveness,

        /// <summary>
        /// Readiness check - determines if the application can accept traffic.
        /// </summary>
        Readiness,

        /// <summary>
        /// Startup check - determines if the application has started successfully.
        /// </summary>
        Startup
    }

    /// <summary>
    /// Configuration options for the health check aggregator.
    /// </summary>
    public class ObsObsHealthCheckAggregatorOptions
    {
        /// <summary>
        /// Gets or sets the cache time-to-live for health check results.
        /// Default is 30 seconds.
        /// </summary>
        public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the timeout for individual health checks.
        /// Default is 30 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets whether to run health checks in parallel.
        /// Default is true.
        /// </summary>
        public bool RunChecksInParallel { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of concurrent health checks.
        /// Default is 10. Only applies when RunChecksInParallel is true.
        /// </summary>
        public int MaxConcurrentChecks { get; set; } = 10;
    }

    /// <summary>
    /// Aggregates multiple health checks and provides overall health status.
    /// Supports caching, parallel execution, and Kubernetes-style liveness/readiness probes.
    /// Thread-safe implementation suitable for production use.
    /// </summary>
    public sealed class ObsHealthCheckAggregator : IDisposable
    {
        private readonly ConcurrentDictionary<string, IObsHealthCheck> _livenessChecks;
        private readonly ConcurrentDictionary<string, IObsHealthCheck> _readinessChecks;
        private readonly ConcurrentDictionary<string, IObsHealthCheck> _startupChecks;
        private readonly ConcurrentDictionary<string, CachedHealthResult> _cache;
        private readonly ObsObsHealthCheckAggregatorOptions _options;
        private readonly SemaphoreSlim _concurrencyLimiter;
        private readonly object _disposeLock = new();
        private bool _disposed;

        /// <summary>
        /// Represents a cached health check result with expiration.
        /// </summary>
        private sealed class CachedHealthResult
        {
            public ObsHealthCheckResult Result { get; }
            public DateTimeOffset ExpiresAt { get; }

            public CachedHealthResult(ObsHealthCheckResult result, TimeSpan ttl)
            {
                Result = result;
                ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            }

            public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObsHealthCheckAggregator"/> class.
        /// </summary>
        /// <param name="options">Configuration options for the aggregator. If null, defaults are used.</param>
        public ObsHealthCheckAggregator(ObsObsHealthCheckAggregatorOptions? options = null)
        {
            _options = options ?? new ObsObsHealthCheckAggregatorOptions();
            _livenessChecks = new ConcurrentDictionary<string, IObsHealthCheck>(StringComparer.OrdinalIgnoreCase);
            _readinessChecks = new ConcurrentDictionary<string, IObsHealthCheck>(StringComparer.OrdinalIgnoreCase);
            _startupChecks = new ConcurrentDictionary<string, IObsHealthCheck>(StringComparer.OrdinalIgnoreCase);
            _cache = new ConcurrentDictionary<string, CachedHealthResult>(StringComparer.OrdinalIgnoreCase);
            _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentChecks, _options.MaxConcurrentChecks);
        }

        /// <summary>
        /// Registers a health check for the specified check type.
        /// </summary>
        /// <param name="healthCheck">The health check to register.</param>
        /// <param name="checkType">The type of health check (liveness, readiness, or startup).</param>
        /// <exception cref="ArgumentNullException">Thrown when healthCheck is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the aggregator has been disposed.</exception>
        public void RegisterCheck(IObsHealthCheck healthCheck, HealthCheckType checkType = HealthCheckType.Readiness)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(healthCheck);

            var checks = GetCheckCollection(checkType);
            checks[healthCheck.Name] = healthCheck;
        }

        /// <summary>
        /// Unregisters a health check by name.
        /// </summary>
        /// <param name="name">The name of the health check to remove.</param>
        /// <param name="checkType">The type of health check.</param>
        /// <returns>True if the check was removed; false if it was not found.</returns>
        public bool UnregisterCheck(string name, HealthCheckType checkType = HealthCheckType.Readiness)
        {
            ThrowIfDisposed();

            var checks = GetCheckCollection(checkType);
            var removed = checks.TryRemove(name, out _);

            // Also remove from cache
            var cacheKey = $"{checkType}:{name}";
            _cache.TryRemove(cacheKey, out _);

            return removed;
        }

        /// <summary>
        /// Performs all liveness checks and returns the aggregated result.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A composite <see cref="ObsHealthCheckResult"/> representing overall liveness.</returns>
        public Task<ObsHealthCheckResult> CheckLivenessAsync(CancellationToken cancellationToken = default)
        {
            return CheckHealthInternalAsync(HealthCheckType.Liveness, cancellationToken);
        }

        /// <summary>
        /// Performs all readiness checks and returns the aggregated result.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A composite <see cref="ObsHealthCheckResult"/> representing overall readiness.</returns>
        public Task<ObsHealthCheckResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            return CheckHealthInternalAsync(HealthCheckType.Readiness, cancellationToken);
        }

        /// <summary>
        /// Performs all startup checks and returns the aggregated result.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A composite <see cref="ObsHealthCheckResult"/> representing startup health.</returns>
        public Task<ObsHealthCheckResult> CheckStartupAsync(CancellationToken cancellationToken = default)
        {
            return CheckHealthInternalAsync(HealthCheckType.Startup, cancellationToken);
        }

        /// <summary>
        /// Performs all health checks of the specified type and returns detailed results.
        /// </summary>
        /// <param name="checkType">The type of health checks to run.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A dictionary of check names to their individual results.</returns>
        public async Task<IReadOnlyDictionary<string, ObsHealthCheckResult>> CheckAllAsync(
            HealthCheckType checkType = HealthCheckType.Readiness,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var checks = GetCheckCollection(checkType);
            var results = new ConcurrentDictionary<string, ObsHealthCheckResult>(StringComparer.OrdinalIgnoreCase);

            if (checks.IsEmpty)
            {
                return results;
            }

            if (_options.RunChecksInParallel)
            {
                await Parallel.ForEachAsync(
                    checks,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = _options.MaxConcurrentChecks
                    },
                    async (kvp, ct) =>
                    {
                        var result = await RunCheckWithCachingAsync(kvp.Value, checkType, ct).ConfigureAwait(false);
                        results[kvp.Key] = result;
                    }).ConfigureAwait(false);
            }
            else
            {
                foreach (var kvp in checks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await RunCheckWithCachingAsync(kvp.Value, checkType, cancellationToken).ConfigureAwait(false);
                    results[kvp.Key] = result;
                }
            }

            return results;
        }

        /// <summary>
        /// Clears all cached health check results.
        /// </summary>
        public void ClearCache()
        {
            ThrowIfDisposed();
            _cache.Clear();
        }

        /// <summary>
        /// Gets the number of registered health checks for the specified type.
        /// </summary>
        /// <param name="checkType">The type of health checks to count.</param>
        /// <returns>The number of registered checks.</returns>
        public int GetCheckCount(HealthCheckType checkType = HealthCheckType.Readiness)
        {
            return GetCheckCollection(checkType).Count;
        }

        private ConcurrentDictionary<string, IObsHealthCheck> GetCheckCollection(HealthCheckType checkType)
        {
            return checkType switch
            {
                HealthCheckType.Liveness => _livenessChecks,
                HealthCheckType.Startup => _startupChecks,
                _ => _readinessChecks
            };
        }

        private async Task<ObsHealthCheckResult> CheckHealthInternalAsync(
            HealthCheckType checkType,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            var stopwatch = Stopwatch.StartNew();
            var results = await CheckAllAsync(checkType, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (results.Count == 0)
            {
                return ObsHealthCheckResult.Healthy(
                    $"No {checkType} checks registered",
                    stopwatch.Elapsed);
            }

            // Aggregate status - worst status wins
            var worstStatus = ObsHealthStatus.Healthy;
            var messages = new List<string>();
            var aggregatedData = new Dictionary<string, object>();

            foreach (var kvp in results)
            {
                var result = kvp.Value;

                if (result.Status > worstStatus)
                {
                    worstStatus = result.Status;
                }

                if (result.Status != ObsHealthStatus.Healthy)
                {
                    messages.Add($"{kvp.Key}: {result.Message}");
                }

                aggregatedData[$"{kvp.Key}_status"] = result.Status.ToString();
                aggregatedData[$"{kvp.Key}_duration_ms"] = result.Duration.TotalMilliseconds;
            }

            var aggregatedMessage = worstStatus == ObsHealthStatus.Healthy
                ? $"All {results.Count} {checkType} checks passed"
                : string.Join("; ", messages);

            return new ObsHealthCheckResult(
                worstStatus,
                aggregatedMessage,
                stopwatch.Elapsed,
                aggregatedData);
        }

        private async Task<ObsHealthCheckResult> RunCheckWithCachingAsync(
            IObsHealthCheck check,
            HealthCheckType checkType,
            CancellationToken cancellationToken)
        {
            var cacheKey = $"{checkType}:{check.Name}";

            // Check cache first
            if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            {
                return cached.Result;
            }

            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-check cache after acquiring semaphore
                if (_cache.TryGetValue(cacheKey, out cached) && !cached.IsExpired)
                {
                    return cached.Result;
                }

                var result = await RunCheckWithTimeoutAsync(check, cancellationToken).ConfigureAwait(false);

                // Cache the result
                _cache[cacheKey] = new CachedHealthResult(result, _options.CacheTtl);

                return result;
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        }

        private async Task<ObsHealthCheckResult> RunCheckWithTimeoutAsync(
            IObsHealthCheck check,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            using var timeoutCts = new CancellationTokenSource(_options.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var result = await check.CheckHealthAsync(linkedCts.Token).ConfigureAwait(false);
                stopwatch.Stop();

                // Ensure the result has the check name
                if (result.CheckName != check.Name)
                {
                    return new ObsHealthCheckResult(
                        result.Status,
                        result.Message,
                        stopwatch.Elapsed,
                        result.Data,
                        result.Exception,
                        check.Name);
                }

                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return ObsHealthCheckResult.Unhealthy(
                    $"Health check '{check.Name}' timed out after {_options.Timeout.TotalSeconds}s",
                    stopwatch.Elapsed,
                    new Dictionary<string, object> { ["timeout_seconds"] = _options.Timeout.TotalSeconds });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return ObsHealthCheckResult.Unhealthy(
                    $"Health check '{check.Name}' failed: {ex.Message}",
                    stopwatch.Elapsed,
                    exception: ex);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ObsHealthCheckAggregator));
            }
        }

        /// <summary>
        /// Disposes the health check aggregator and releases resources.
        /// </summary>
        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _concurrencyLimiter.Dispose();
                _cache.Clear();
            }
        }
    }

    #endregion

    #region Metrics

    /// <summary>
    /// Represents histogram statistics with percentile calculations.
    /// </summary>
    public sealed class HistogramStatistics
    {
        /// <summary>
        /// Gets the total number of recorded values.
        /// </summary>
        public long Count { get; }

        /// <summary>
        /// Gets the sum of all recorded values.
        /// </summary>
        public double Sum { get; }

        /// <summary>
        /// Gets the minimum recorded value.
        /// </summary>
        public double Min { get; }

        /// <summary>
        /// Gets the maximum recorded value.
        /// </summary>
        public double Max { get; }

        /// <summary>
        /// Gets the mean (average) of recorded values.
        /// </summary>
        public double Mean { get; }

        /// <summary>
        /// Gets the 50th percentile (median) value.
        /// </summary>
        public double P50 { get; }

        /// <summary>
        /// Gets the 95th percentile value.
        /// </summary>
        public double P95 { get; }

        /// <summary>
        /// Gets the 99th percentile value.
        /// </summary>
        public double P99 { get; }

        /// <summary>
        /// Gets the 99.9th percentile value.
        /// </summary>
        public double P999 { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HistogramStatistics"/> class.
        /// </summary>
        public HistogramStatistics(
            long count,
            double sum,
            double min,
            double max,
            double mean,
            double p50,
            double p95,
            double p99,
            double p999)
        {
            Count = count;
            Sum = sum;
            Min = min;
            Max = max;
            Mean = mean;
            P50 = p50;
            P95 = p95;
            P99 = p99;
            P999 = p999;
        }

        /// <summary>
        /// Creates an empty histogram statistics instance.
        /// </summary>
        public static HistogramStatistics Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Represents a point-in-time snapshot of all collected metrics.
    /// This class is immutable and thread-safe.
    /// </summary>
    public sealed class ObsMetricsSnapshot
    {
        /// <summary>
        /// Gets the timestamp when this snapshot was taken.
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Gets the counter metrics as a dictionary of name (with tags) to value.
        /// </summary>
        public IReadOnlyDictionary<string, long> Counters { get; }

        /// <summary>
        /// Gets the gauge metrics as a dictionary of name (with tags) to value.
        /// </summary>
        public IReadOnlyDictionary<string, double> Gauges { get; }

        /// <summary>
        /// Gets the histogram metrics with percentile statistics.
        /// </summary>
        public IReadOnlyDictionary<string, HistogramStatistics> Histograms { get; }

        /// <summary>
        /// Gets the duration of the collection period this snapshot represents.
        /// </summary>
        public TimeSpan CollectionDuration { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObsMetricsSnapshot"/> class.
        /// </summary>
        /// <param name="counters">The counter metrics.</param>
        /// <param name="gauges">The gauge metrics.</param>
        /// <param name="histograms">The histogram metrics.</param>
        /// <param name="collectionDuration">The duration of the collection period.</param>
        public ObsMetricsSnapshot(
            IReadOnlyDictionary<string, long> counters,
            IReadOnlyDictionary<string, double> gauges,
            IReadOnlyDictionary<string, HistogramStatistics> histograms,
            TimeSpan collectionDuration)
        {
            Timestamp = DateTimeOffset.UtcNow;
            Counters = counters ?? new Dictionary<string, long>();
            Gauges = gauges ?? new Dictionary<string, double>();
            Histograms = histograms ?? new Dictionary<string, HistogramStatistics>();
            CollectionDuration = collectionDuration;
        }

        /// <summary>
        /// Creates an empty metrics snapshot.
        /// </summary>
        public static ObsMetricsSnapshot Empty => new(
            new Dictionary<string, long>(),
            new Dictionary<string, double>(),
            new Dictionary<string, HistogramStatistics>(),
            TimeSpan.Zero);
    }

    /// <summary>
    /// Defines a contract for collecting application metrics.
    /// Implementations should be thread-safe for concurrent access.
    /// </summary>
    public interface IMetricsCollector
    {
        /// <summary>
        /// Increments a counter metric by 1.
        /// </summary>
        /// <param name="name">The name of the counter.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        void IncrementCounter(string name, IReadOnlyDictionary<string, string>? tags = null);

        /// <summary>
        /// Increments a counter metric by the specified amount.
        /// </summary>
        /// <param name="name">The name of the counter.</param>
        /// <param name="amount">The amount to increment by (must be positive).</param>
        /// <param name="tags">Optional tags for the metric.</param>
        void IncrementCounter(string name, long amount, IReadOnlyDictionary<string, string>? tags = null);

        /// <summary>
        /// Records a value for a gauge or histogram metric.
        /// </summary>
        /// <param name="name">The name of the metric.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        void RecordValue(string name, double value, IReadOnlyDictionary<string, string>? tags = null);

        /// <summary>
        /// Starts a timer that records duration when disposed.
        /// </summary>
        /// <param name="name">The name of the timing metric.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        /// <returns>A disposable timer that records the elapsed time on disposal.</returns>
        IDisposable StartTimer(string name, IReadOnlyDictionary<string, string>? tags = null);

        /// <summary>
        /// Gets a snapshot of all currently collected metrics.
        /// </summary>
        /// <returns>A <see cref="ObsMetricsSnapshot"/> containing all metrics.</returns>
        ObsMetricsSnapshot GetSnapshot();

        /// <summary>
        /// Resets all metrics to their initial state.
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// A thread-safe, in-memory metrics collector implementation.
    /// Uses lock-free algorithms where possible for high-performance scenarios.
    /// </summary>
    public sealed class InMemoryMetricsCollector : IMetricsCollector, IDisposable
    {
        private readonly ConcurrentDictionary<string, long> _counters;
        private readonly ConcurrentDictionary<string, double> _gauges;
        private readonly ConcurrentDictionary<string, HistogramData> _histograms;
        private readonly int _histogramMaxSamples;
        private readonly object _snapshotLock = new();
        private DateTimeOffset _lastSnapshotTime;
        private bool _disposed;

        /// <summary>
        /// Thread-safe histogram data structure using a lock for value storage.
        /// </summary>
        private sealed class HistogramData
        {
            private readonly object _lock = new();
            private readonly int _maxSamples;
            private double[] _values;
            private int _count;
            private double _sum;
            private double _min = double.MaxValue;
            private double _max = double.MinValue;

            public HistogramData(int maxSamples)
            {
                _maxSamples = maxSamples;
                _values = new double[Math.Min(1000, maxSamples)];
            }

            public void Record(double value)
            {
                lock (_lock)
                {
                    _sum += value;
                    _count++;

                    if (value < _min) _min = value;
                    if (value > _max) _max = value;

                    // Store sample for percentile calculation
                    if (_count <= _values.Length)
                    {
                        _values[_count - 1] = value;
                    }
                    else if (_count <= _maxSamples)
                    {
                        // Grow array
                        var newSize = Math.Min(_values.Length * 2, _maxSamples);
                        var newValues = new double[newSize];
                        Array.Copy(_values, newValues, _values.Length);
                        newValues[_count - 1] = value;
                        _values = newValues;
                    }
                    else
                    {
                        // Reservoir sampling for bounded memory
                        // Replace a random element with decreasing probability
                        var random = Random.Shared.Next(_count);
                        if (random < _values.Length)
                        {
                            _values[random] = value;
                        }
                    }
                }
            }

            public HistogramStatistics GetStatistics()
            {
                lock (_lock)
                {
                    if (_count == 0)
                    {
                        return HistogramStatistics.Empty;
                    }

                    var sampleCount = Math.Min(_count, _values.Length);
                    var sortedValues = new double[sampleCount];
                    Array.Copy(_values, sortedValues, sampleCount);
                    Array.Sort(sortedValues);

                    return new HistogramStatistics(
                        count: _count,
                        sum: _sum,
                        min: _min,
                        max: _max,
                        mean: _sum / _count,
                        p50: GetPercentile(sortedValues, 0.50),
                        p95: GetPercentile(sortedValues, 0.95),
                        p99: GetPercentile(sortedValues, 0.99),
                        p999: GetPercentile(sortedValues, 0.999));
                }
            }

            public void Reset()
            {
                lock (_lock)
                {
                    _count = 0;
                    _sum = 0;
                    _min = double.MaxValue;
                    _max = double.MinValue;
                    Array.Clear(_values);
                }
            }

            private static double GetPercentile(double[] sortedValues, double percentile)
            {
                if (sortedValues.Length == 0)
                {
                    return 0;
                }

                if (sortedValues.Length == 1)
                {
                    return sortedValues[0];
                }

                var index = (sortedValues.Length - 1) * percentile;
                var lower = (int)Math.Floor(index);
                var upper = (int)Math.Ceiling(index);

                if (lower == upper)
                {
                    return sortedValues[lower];
                }

                // Linear interpolation
                var weight = index - lower;
                return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
            }
        }

        /// <summary>
        /// Timer implementation that records elapsed time on disposal.
        /// </summary>
        private sealed class MetricTimer : IDisposable
        {
            private readonly InMemoryMetricsCollector _collector;
            private readonly string _name;
            private readonly IReadOnlyDictionary<string, string>? _tags;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public MetricTimer(
                InMemoryMetricsCollector collector,
                string name,
                IReadOnlyDictionary<string, string>? tags)
            {
                _collector = collector;
                _name = name;
                _tags = tags;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopwatch.Stop();
                _collector.RecordValue(_name, _stopwatch.Elapsed.TotalMilliseconds, _tags);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryMetricsCollector"/> class.
        /// </summary>
        /// <param name="histogramMaxSamples">Maximum number of samples to retain for histogram percentile calculation. Default is 10000.</param>
        public InMemoryMetricsCollector(int histogramMaxSamples = 10000)
        {
            if (histogramMaxSamples <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(histogramMaxSamples), "Max samples must be positive.");
            }

            _histogramMaxSamples = histogramMaxSamples;
            _counters = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _gauges = new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _histograms = new ConcurrentDictionary<string, HistogramData>(StringComparer.OrdinalIgnoreCase);
            _lastSnapshotTime = DateTimeOffset.UtcNow;
        }

        /// <inheritdoc />
        public void IncrementCounter(string name, IReadOnlyDictionary<string, string>? tags = null)
        {
            IncrementCounter(name, 1, tags);
        }

        /// <inheritdoc />
        public void IncrementCounter(string name, long amount, IReadOnlyDictionary<string, string>? tags = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "Counter increment amount must be non-negative.");
            }

            var key = BuildMetricKey(name, tags);
            _counters.AddOrUpdate(key, amount, (_, existing) => existing + amount);
        }

        /// <inheritdoc />
        public void RecordValue(string name, double value, IReadOnlyDictionary<string, string>? tags = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            var key = BuildMetricKey(name, tags);

            // Update gauge (latest value)
            _gauges[key] = value;

            // Update histogram (for percentile calculation)
            var histogram = _histograms.GetOrAdd(key, _ => new HistogramData(_histogramMaxSamples));
            histogram.Record(value);
        }

        /// <inheritdoc />
        public IDisposable StartTimer(string name, IReadOnlyDictionary<string, string>? tags = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            return new MetricTimer(this, name, tags);
        }

        /// <inheritdoc />
        public ObsMetricsSnapshot GetSnapshot()
        {
            ThrowIfDisposed();

            lock (_snapshotLock)
            {
                var now = DateTimeOffset.UtcNow;
                var duration = now - _lastSnapshotTime;
                _lastSnapshotTime = now;

                var counters = new Dictionary<string, long>(_counters, StringComparer.OrdinalIgnoreCase);
                var gauges = new Dictionary<string, double>(_gauges, StringComparer.OrdinalIgnoreCase);
                var histograms = new Dictionary<string, HistogramStatistics>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in _histograms)
                {
                    histograms[kvp.Key] = kvp.Value.GetStatistics();
                }

                return new ObsMetricsSnapshot(counters, gauges, histograms, duration);
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            ThrowIfDisposed();

            lock (_snapshotLock)
            {
                _counters.Clear();
                _gauges.Clear();

                foreach (var histogram in _histograms.Values)
                {
                    histogram.Reset();
                }

                _histograms.Clear();
                _lastSnapshotTime = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Gets the current value of a counter.
        /// </summary>
        /// <param name="name">The name of the counter.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        /// <returns>The counter value, or 0 if not found.</returns>
        public long GetCounter(string name, IReadOnlyDictionary<string, string>? tags = null)
        {
            var key = BuildMetricKey(name, tags);
            return _counters.TryGetValue(key, out var value) ? value : 0;
        }

        /// <summary>
        /// Gets the current value of a gauge.
        /// </summary>
        /// <param name="name">The name of the gauge.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        /// <returns>The gauge value, or null if not found.</returns>
        public double? GetGauge(string name, IReadOnlyDictionary<string, string>? tags = null)
        {
            var key = BuildMetricKey(name, tags);
            return _gauges.TryGetValue(key, out var value) ? value : null;
        }

        private static string BuildMetricKey(string name, IReadOnlyDictionary<string, string>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return name;
            }

            // Build a consistent key by sorting tags
            var sortedTags = tags
                .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Select(t => $"{t.Key}={t.Value}");

            return $"{name}{{{string.Join(",", sortedTags)}}}";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InMemoryMetricsCollector));
            }
        }

        /// <summary>
        /// Disposes the metrics collector.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Reset();
        }
    }

    #endregion

    #region Structured Logging

    /// <summary>
    /// Represents a structured log entry with associated metadata.
    /// </summary>
    public sealed class LogEntry
    {
        /// <summary>
        /// Gets the log level of this entry.
        /// </summary>
        public LogLevel Level { get; }

        /// <summary>
        /// Gets the log message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the exception associated with this log entry, if any.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Gets the structured properties associated with this log entry.
        /// </summary>
        public IReadOnlyDictionary<string, object> Properties { get; }

        /// <summary>
        /// Gets the timestamp when this log entry was created.
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Gets the correlation ID for distributed tracing.
        /// </summary>
        public string? CorrelationId { get; }

        /// <summary>
        /// Gets the source context (typically the class name).
        /// </summary>
        public string? SourceContext { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="LogEntry"/> class.
        /// </summary>
        public LogEntry(
            LogLevel level,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, object>? properties = null,
            string? correlationId = null,
            string? sourceContext = null)
        {
            Level = level;
            Message = message ?? string.Empty;
            Exception = exception;
            Properties = properties != null
                ? new Dictionary<string, object>(properties)
                : new Dictionary<string, object>();
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = correlationId;
            SourceContext = sourceContext;
        }
    }

    /// <summary>
    /// Defines a contract for structured logging with support for log levels,
    /// properties, and exception tracking.
    /// Implementations should be thread-safe.
    /// </summary>
    public interface IStructuredLogger
    {
        /// <summary>
        /// Gets the minimum log level that will be processed.
        /// </summary>
        LogLevel MinimumLevel { get; }

        /// <summary>
        /// Logs a message with the specified level and properties.
        /// </summary>
        /// <param name="level">The log level.</param>
        /// <param name="message">The log message (may contain structured placeholders).</param>
        /// <param name="exception">Optional exception to include.</param>
        /// <param name="properties">Optional structured properties.</param>
        void Log(
            LogLevel level,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, object>? properties = null);

        /// <summary>
        /// Logs a message with the specified level, message template, and arguments.
        /// </summary>
        /// <param name="level">The log level.</param>
        /// <param name="messageTemplate">The message template with placeholders.</param>
        /// <param name="args">Arguments to fill the template placeholders.</param>
        void Log(LogLevel level, string messageTemplate, params object[] args);

        /// <summary>
        /// Checks if the specified log level is enabled.
        /// </summary>
        /// <param name="level">The log level to check.</param>
        /// <returns>True if the level is enabled; false otherwise.</returns>
        bool IsEnabled(LogLevel level);

        /// <summary>
        /// Creates a new logger with additional properties that will be included in all log entries.
        /// </summary>
        /// <param name="properties">The properties to add to the logger context.</param>
        /// <returns>A new logger with the enriched context.</returns>
        IStructuredLogger WithProperties(IReadOnlyDictionary<string, object> properties);

        /// <summary>
        /// Creates a new logger with a source context (typically the class name).
        /// </summary>
        /// <param name="sourceContext">The source context name.</param>
        /// <returns>A new logger with the source context set.</returns>
        IStructuredLogger ForContext(string sourceContext);

        /// <summary>
        /// Creates a new logger with a source context derived from the specified type.
        /// </summary>
        /// <typeparam name="T">The type to use as the source context.</typeparam>
        /// <returns>A new logger with the source context set.</returns>
        IStructuredLogger ForContext<T>();
    }

    /// <summary>
    /// Extension methods for <see cref="IStructuredLogger"/> providing convenience logging methods.
    /// </summary>
    public static class StructuredLoggerExtensions
    {
        /// <summary>
        /// Logs a trace-level message.
        /// </summary>
        public static void LogTrace(
            this IStructuredLogger logger,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Trace, message, null, properties);
        }

        /// <summary>
        /// Logs a debug-level message.
        /// </summary>
        public static void LogDebug(
            this IStructuredLogger logger,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Debug, message, null, properties);
        }

        /// <summary>
        /// Logs an information-level message.
        /// </summary>
        public static void LogInformation(
            this IStructuredLogger logger,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Information, message, null, properties);
        }

        /// <summary>
        /// Logs a warning-level message.
        /// </summary>
        public static void LogWarning(
            this IStructuredLogger logger,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Warning, message, null, properties);
        }

        /// <summary>
        /// Logs a warning-level message with an exception.
        /// </summary>
        public static void LogWarning(
            this IStructuredLogger logger,
            Exception exception,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Warning, message, exception, properties);
        }

        /// <summary>
        /// Logs an error-level message.
        /// </summary>
        public static void LogError(
            this IStructuredLogger logger,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Error, message, null, properties);
        }

        /// <summary>
        /// Logs an error-level message with an exception.
        /// </summary>
        public static void LogError(
            this IStructuredLogger logger,
            Exception exception,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Error, message, exception, properties);
        }

        /// <summary>
        /// Logs a critical-level message.
        /// </summary>
        public static void LogCritical(
            this IStructuredLogger logger,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Critical, message, null, properties);
        }

        /// <summary>
        /// Logs a critical-level message with an exception.
        /// </summary>
        public static void LogCritical(
            this IStructuredLogger logger,
            Exception exception,
            string message,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            logger.Log(LogLevel.Critical, message, exception, properties);
        }

        /// <summary>
        /// Creates a scope that adds properties to all log entries within the scope.
        /// </summary>
        public static IDisposable BeginScope(
            this IStructuredLogger logger,
            IReadOnlyDictionary<string, object> properties)
        {
            return new LoggingScope(logger.WithProperties(properties));
        }

        private sealed class LoggingScope : IDisposable
        {
            private readonly IStructuredLogger _logger;

            public LoggingScope(IStructuredLogger logger)
            {
                _logger = logger;
            }

            public void Dispose()
            {
                // Scope cleanup - the logger reference goes out of scope
            }
        }
    }

    /// <summary>
    /// A thread-safe, in-memory structured logger implementation.
    /// Useful for testing, development, or as a base for custom implementations.
    /// </summary>
    public sealed class InMemoryStructuredLogger : IStructuredLogger
    {
        private readonly ConcurrentQueue<LogEntry> _entries;
        private readonly int _maxEntries;
        private readonly IReadOnlyDictionary<string, object> _contextProperties;
        private readonly string? _sourceContext;
        private int _entryCount;

        /// <inheritdoc />
        public LogLevel MinimumLevel { get; }

        /// <summary>
        /// Event raised when a new log entry is added.
        /// </summary>
        public event Action<LogEntry>? LogEntryAdded;

        /// <summary>
        /// Gets all log entries currently stored.
        /// </summary>
        public IEnumerable<LogEntry> Entries => _entries.ToArray();

        /// <summary>
        /// Gets the number of log entries currently stored.
        /// </summary>
        public int EntryCount => Volatile.Read(ref _entryCount);

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryStructuredLogger"/> class.
        /// </summary>
        /// <param name="minimumLevel">The minimum log level to capture. Default is Information.</param>
        /// <param name="maxEntries">Maximum number of entries to retain. Default is 10000.</param>
        public InMemoryStructuredLogger(
            LogLevel minimumLevel = LogLevel.Information,
            int maxEntries = 10000)
            : this(minimumLevel, maxEntries, new Dictionary<string, object>(), null)
        {
        }

        private InMemoryStructuredLogger(
            LogLevel minimumLevel,
            int maxEntries,
            IReadOnlyDictionary<string, object> contextProperties,
            string? sourceContext)
        {
            if (maxEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries), "Max entries must be positive.");
            }

            MinimumLevel = minimumLevel;
            _maxEntries = maxEntries;
            _entries = new ConcurrentQueue<LogEntry>();
            _contextProperties = contextProperties;
            _sourceContext = sourceContext;
        }

        /// <inheritdoc />
        public void Log(
            LogLevel level,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            // Merge context properties with log properties
            var mergedProperties = MergeProperties(properties);

            var entry = new LogEntry(
                level,
                message,
                exception,
                mergedProperties,
                GetCorrelationId(),
                _sourceContext);

            AddEntry(entry);
        }

        /// <inheritdoc />
        public void Log(LogLevel level, string messageTemplate, params object[] args)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            var message = args.Length > 0
                ? string.Format(messageTemplate, args)
                : messageTemplate;

            Log(level, message);
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel level)
        {
            return level >= MinimumLevel;
        }

        /// <inheritdoc />
        public IStructuredLogger WithProperties(IReadOnlyDictionary<string, object> properties)
        {
            ArgumentNullException.ThrowIfNull(properties);

            var mergedProperties = MergeProperties(properties);
            return new InMemoryStructuredLogger(MinimumLevel, _maxEntries, mergedProperties, _sourceContext)
            {
                LogEntryAdded = LogEntryAdded
            };
        }

        /// <inheritdoc />
        public IStructuredLogger ForContext(string sourceContext)
        {
            return new InMemoryStructuredLogger(MinimumLevel, _maxEntries, _contextProperties, sourceContext)
            {
                LogEntryAdded = LogEntryAdded
            };
        }

        /// <inheritdoc />
        public IStructuredLogger ForContext<T>()
        {
            return ForContext(typeof(T).FullName ?? typeof(T).Name);
        }

        /// <summary>
        /// Clears all stored log entries.
        /// </summary>
        public void Clear()
        {
            while (_entries.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _entryCount);
            }
        }

        /// <summary>
        /// Gets log entries filtered by level.
        /// </summary>
        /// <param name="level">The minimum level to filter by.</param>
        /// <returns>Log entries at or above the specified level.</returns>
        public IEnumerable<LogEntry> GetEntriesByLevel(LogLevel level)
        {
            return _entries.Where(e => e.Level >= level);
        }

        /// <summary>
        /// Gets log entries that contain an exception.
        /// </summary>
        /// <returns>Log entries with exceptions.</returns>
        public IEnumerable<LogEntry> GetExceptionEntries()
        {
            return _entries.Where(e => e.Exception != null);
        }

        private void AddEntry(LogEntry entry)
        {
            _entries.Enqueue(entry);
            var count = Interlocked.Increment(ref _entryCount);

            // Trim old entries if we exceed the maximum
            while (count > _maxEntries && _entries.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _entryCount);
            }

            LogEntryAdded?.Invoke(entry);
        }

        private Dictionary<string, object> MergeProperties(IReadOnlyDictionary<string, object>? properties)
        {
            var merged = new Dictionary<string, object>(_contextProperties);

            if (properties != null)
            {
                foreach (var kvp in properties)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            return merged;
        }

        private static string? GetCorrelationId()
        {
            // Try to get correlation ID from current activity (if using System.Diagnostics.Activity)
            return Activity.Current?.TraceId.ToString();
        }
    }

    /// <summary>
    /// A null logger implementation that discards all log entries.
    /// Useful for testing or when logging should be disabled.
    /// </summary>
    public sealed class NullStructuredLogger : IStructuredLogger
    {
        /// <summary>
        /// Gets the singleton instance of the null logger.
        /// </summary>
        public static readonly NullStructuredLogger Instance = new();

        /// <inheritdoc />
        public LogLevel MinimumLevel => LogLevel.Critical + 1; // Above all levels

        private NullStructuredLogger() { }

        /// <inheritdoc />
        public void Log(
            LogLevel level,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            // Intentionally empty
        }

        /// <inheritdoc />
        public void Log(LogLevel level, string messageTemplate, params object[] args)
        {
            // Intentionally empty
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel level) => false;

        /// <inheritdoc />
        public IStructuredLogger WithProperties(IReadOnlyDictionary<string, object> properties) => this;

        /// <inheritdoc />
        public IStructuredLogger ForContext(string sourceContext) => this;

        /// <inheritdoc />
        public IStructuredLogger ForContext<T>() => this;
    }

    /// <summary>
    /// A composite logger that writes to multiple underlying loggers.
    /// </summary>
    public sealed class CompositeStructuredLogger : IStructuredLogger
    {
        private readonly IReadOnlyList<IStructuredLogger> _loggers;

        /// <inheritdoc />
        public LogLevel MinimumLevel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeStructuredLogger"/> class.
        /// </summary>
        /// <param name="loggers">The loggers to write to.</param>
        public CompositeStructuredLogger(params IStructuredLogger[] loggers)
            : this((IEnumerable<IStructuredLogger>)loggers)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeStructuredLogger"/> class.
        /// </summary>
        /// <param name="loggers">The loggers to write to.</param>
        public CompositeStructuredLogger(IEnumerable<IStructuredLogger> loggers)
        {
            _loggers = loggers?.ToList() ?? throw new ArgumentNullException(nameof(loggers));

            // Minimum level is the lowest minimum across all loggers
            MinimumLevel = _loggers.Count > 0
                ? _loggers.Min(l => l.MinimumLevel)
                : LogLevel.Information;
        }

        /// <inheritdoc />
        public void Log(
            LogLevel level,
            string message,
            Exception? exception = null,
            IReadOnlyDictionary<string, object>? properties = null)
        {
            foreach (var logger in _loggers)
            {
                if (logger.IsEnabled(level))
                {
                    logger.Log(level, message, exception, properties);
                }
            }
        }

        /// <inheritdoc />
        public void Log(LogLevel level, string messageTemplate, params object[] args)
        {
            foreach (var logger in _loggers)
            {
                if (logger.IsEnabled(level))
                {
                    logger.Log(level, messageTemplate, args);
                }
            }
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel level)
        {
            return _loggers.Any(l => l.IsEnabled(level));
        }

        /// <inheritdoc />
        public IStructuredLogger WithProperties(IReadOnlyDictionary<string, object> properties)
        {
            var enrichedLoggers = _loggers.Select(l => l.WithProperties(properties));
            return new CompositeStructuredLogger(enrichedLoggers);
        }

        /// <inheritdoc />
        public IStructuredLogger ForContext(string sourceContext)
        {
            var contextualLoggers = _loggers.Select(l => l.ForContext(sourceContext));
            return new CompositeStructuredLogger(contextualLoggers);
        }

        /// <inheritdoc />
        public IStructuredLogger ForContext<T>()
        {
            return ForContext(typeof(T).FullName ?? typeof(T).Name);
        }
    }

    #endregion

    #region Observability Facade

    /// <summary>
    /// Provides a unified facade for observability features including metrics, health checks, and logging.
    /// Thread-safe and suitable for use as a singleton.
    /// </summary>
    public sealed class ObservabilityFacade : IDisposable
    {
        private readonly IMetricsCollector _metrics;
        private readonly ObsHealthCheckAggregator _healthChecks;
        private readonly IStructuredLogger _logger;
        private bool _disposed;

        /// <summary>
        /// Gets the metrics collector.
        /// </summary>
        public IMetricsCollector Metrics => _metrics;

        /// <summary>
        /// Gets the health check aggregator.
        /// </summary>
        public ObsHealthCheckAggregator HealthChecks => _healthChecks;

        /// <summary>
        /// Gets the structured logger.
        /// </summary>
        public IStructuredLogger Logger => _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservabilityFacade"/> class with default implementations.
        /// </summary>
        public ObservabilityFacade()
            : this(new InMemoryMetricsCollector(), new ObsHealthCheckAggregator(), new InMemoryStructuredLogger())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservabilityFacade"/> class with custom implementations.
        /// </summary>
        /// <param name="metrics">The metrics collector to use.</param>
        /// <param name="healthChecks">The health check aggregator to use.</param>
        /// <param name="logger">The structured logger to use.</param>
        public ObservabilityFacade(
            IMetricsCollector metrics,
            ObsHealthCheckAggregator healthChecks,
            IStructuredLogger logger)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _healthChecks = healthChecks ?? throw new ArgumentNullException(nameof(healthChecks));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Records a timed operation with automatic metrics and logging.
        /// </summary>
        /// <typeparam name="T">The result type of the operation.</typeparam>
        /// <param name="operationName">The name of the operation.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        /// <returns>The result of the operation.</returns>
        public async Task<T> TrackOperationAsync<T>(
            string operationName,
            Func<Task<T>> operation,
            IReadOnlyDictionary<string, string>? tags = null)
        {
            ThrowIfDisposed();

            var properties = new Dictionary<string, object> { ["operation"] = operationName };
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    properties[tag.Key] = tag.Value;
                }
            }

            _logger.Log(LogLevel.Debug, $"Starting operation: {operationName}", properties: properties);
            _metrics.IncrementCounter($"{operationName}.started", tags);

            using var timer = _metrics.StartTimer($"{operationName}.duration", tags);

            try
            {
                var result = await operation().ConfigureAwait(false);
                _metrics.IncrementCounter($"{operationName}.succeeded", tags);
                _logger.Log(LogLevel.Debug, $"Completed operation: {operationName}", properties: properties);
                return result;
            }
            catch (Exception ex)
            {
                _metrics.IncrementCounter($"{operationName}.failed", tags);
                _logger.Log(LogLevel.Error, $"Failed operation: {operationName}", ex, properties);
                throw;
            }
        }

        /// <summary>
        /// Records a timed operation with automatic metrics and logging (no return value).
        /// </summary>
        /// <param name="operationName">The name of the operation.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="tags">Optional tags for the metric.</param>
        public async Task TrackOperationAsync(
            string operationName,
            Func<Task> operation,
            IReadOnlyDictionary<string, string>? tags = null)
        {
            await TrackOperationAsync(operationName, async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }, tags).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ObservabilityFacade));
            }
        }

        /// <summary>
        /// Disposes the observability facade and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_metrics is IDisposable disposableMetrics)
            {
                disposableMetrics.Dispose();
            }

            _healthChecks.Dispose();
        }
    }

    #endregion
}
