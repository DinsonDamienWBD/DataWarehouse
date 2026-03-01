using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using DataWarehouse.SDK.Contracts.Resilience;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Infrastructure.InMemory;
using DataWarehouse.SDK.Utilities;
using InMemoryCircuitBreakerOpenException = DataWarehouse.SDK.Infrastructure.InMemory.CircuitBreakerOpenException;
using InMemoryBulkheadRejectedException = DataWarehouse.SDK.Infrastructure.InMemory.BulkheadRejectedException;

namespace DataWarehouse.Plugins.UltimateResilience.Scaling;

/// <summary>
/// Manages resilience scaling for the UltimateResilience plugin, implementing <see cref="IScalableSubsystem"/>.
/// Fixes the critical resilience bug where <c>ExecuteWithResilienceAsync</c> did not apply
/// circuit breaker, bulkhead, or retry logic from the active strategy configuration.
/// </summary>
/// <remarks>
/// <para>
/// The resilience pipeline wraps operations in the following order:
/// <list type="number">
///   <item><description>Bulkhead acquire (concurrency limiting)</description></item>
///   <item><description>Circuit breaker check (fail-fast on open circuit)</description></item>
///   <item><description>Retry loop with configurable backoff</description></item>
///   <item><description>Execute the actual operation</description></item>
///   <item><description>Record success/failure metrics</description></item>
///   <item><description>Release bulkhead slot</description></item>
/// </list>
/// </para>
/// <para>
/// Per-strategy circuit breaker and bulkhead instances track state independently.
/// All parameters are runtime-reconfigurable via <see cref="ReconfigureLimitsAsync"/>.
/// Distributed circuit breaker state sharing uses <see cref="IFederatedMessageBus"/> when available.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Resilience scaling manager with fixed ExecuteWithResilienceAsync")]
public sealed class ResilienceScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>
    /// Message bus topic for distributed circuit breaker state sharing.
    /// </summary>
    public const string CircuitBreakerStateTopic = "dw.resilience.circuit-breaker.state";

    private readonly ConcurrentDictionary<string, ICircuitBreaker> _circuitBreakers = new();
    private readonly ConcurrentDictionary<string, IBulkheadIsolation> _bulkheads = new();
    private readonly ConcurrentDictionary<string, RetryOptions> _retryOptions = new();
    private readonly ConcurrentDictionary<string, StrategyMetrics> _strategyMetrics = new();

    private readonly IFederatedMessageBus? _federatedBus;
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;
    private long _lamportClock;
    private bool _disposed;

    // Global metrics
    private long _totalExecutions;
    private long _successfulExecutions;
    private long _failedExecutions;
    private long _retryAttempts;
    private long _circuitBreakerTrips;
    private long _bulkheadRejections;

    /// <summary>
    /// Adaptive thresholds instance for periodic threshold recomputation.
    /// Set by the plugin during initialization.
    /// </summary>
    internal AdaptiveResilienceThresholds? AdaptiveThresholds { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilienceScalingManager"/> class.
    /// </summary>
    /// <param name="federatedBus">
    /// Optional federated message bus for distributed circuit breaker state sharing.
    /// When <c>null</c>, circuit breaker state is local-only.
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    public ResilienceScalingManager(
        IFederatedMessageBus? federatedBus = null,
        ScalingLimits? initialLimits = null)
    {
        _federatedBus = federatedBus;
        _currentLimits = initialLimits ?? new ScalingLimits();

        if (_federatedBus != null)
        {
            SubscribeToDistributedState();
        }
    }

    /// <summary>
    /// Executes an operation with the full resilience pipeline applied:
    /// bulkhead acquire, circuit breaker check, retry loop, execute, record metrics.
    /// </summary>
    /// <typeparam name="T">The return type of the protected operation.</typeparam>
    /// <param name="action">The operation to execute.</param>
    /// <param name="strategyId">The strategy identifier whose resilience configuration to apply.</param>
    /// <param name="circuitBreakerOptions">Circuit breaker options for this strategy. Uses defaults if <c>null</c>.</param>
    /// <param name="bulkheadOptions">Bulkhead options for this strategy. Uses defaults if <c>null</c>.</param>
    /// <param name="retryOptions">Retry options for this strategy. Uses defaults if <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the operation if successful.</returns>
    /// <exception cref="InMemoryCircuitBreakerOpenException">Thrown if the circuit breaker is open.</exception>
    /// <exception cref="InMemoryBulkheadRejectedException">Thrown if the bulkhead is at capacity.</exception>
    public async Task<T> ExecuteWithResilienceAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string strategyId,
        CircuitBreakerOptions? circuitBreakerOptions = null,
        BulkheadOptions? bulkheadOptions = null,
        RetryOptions? retryOptions = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _totalExecutions);

        var cbOptions = circuitBreakerOptions ?? new CircuitBreakerOptions();
        var bhOptions = bulkheadOptions ?? new BulkheadOptions
        {
            MaxConcurrency = _currentLimits.MaxConcurrentOperations
        };
        var rtOptions = retryOptions ?? GetOrCreateRetryOptions(strategyId);

        var circuitBreaker = GetOrCreateCircuitBreaker(strategyId, cbOptions);
        var bulkhead = GetOrCreateBulkhead(strategyId, bhOptions);
        var metrics = GetOrCreateMetrics(strategyId);
        var sw = Stopwatch.StartNew();

        // Step 1: Acquire bulkhead slot
        IBulkheadLease? lease = null;
        try
        {
            lease = await bulkhead.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (InMemoryBulkheadRejectedException)
        {
            Interlocked.Increment(ref _bulkheadRejections);
            metrics.RecordBulkheadRejection();
            throw;
        }

        try
        {
            // Step 2+3: Circuit breaker wraps the retry loop
            return await circuitBreaker.ExecuteAsync(async innerCt =>
            {
                return await ExecuteWithRetryAsync(action, rtOptions, metrics, innerCt)
                    .ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (InMemoryCircuitBreakerOpenException)
        {
            Interlocked.Increment(ref _circuitBreakerTrips);
            metrics.RecordCircuitBreakerTrip();
            throw;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failedExecutions);
            metrics.RecordFailure(sw.Elapsed);
            throw;
        }
        finally
        {
            if (lease != null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            sw.Stop();
            metrics.RecordLatency(sw.Elapsed);
        }
    }

    /// <summary>
    /// Configures resilience parameters for a specific strategy at runtime.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="circuitBreakerOptions">New circuit breaker options, or <c>null</c> to keep current.</param>
    /// <param name="bulkheadOptions">New bulkhead options, or <c>null</c> to keep current.</param>
    /// <param name="retryOptions">New retry options, or <c>null</c> to keep current.</param>
    public void ConfigureStrategy(
        string strategyId,
        CircuitBreakerOptions? circuitBreakerOptions = null,
        BulkheadOptions? bulkheadOptions = null,
        RetryOptions? retryOptions = null)
    {
        if (circuitBreakerOptions != null)
        {
            _circuitBreakers[strategyId] = new InMemoryCircuitBreaker(
                $"resilience.{strategyId}", circuitBreakerOptions);
            WireCircuitBreakerStateChange(strategyId, _circuitBreakers[strategyId]);
        }

        if (bulkheadOptions != null)
        {
            _bulkheads[strategyId] = new InMemoryBulkheadIsolation(
                $"resilience.{strategyId}", bulkheadOptions);
        }

        if (retryOptions != null)
        {
            _retryOptions[strategyId] = retryOptions;
        }
    }

    /// <summary>
    /// Applies adaptive threshold values computed by <see cref="AdaptiveResilienceThresholds"/>
    /// to the specified strategy's circuit breaker and bulkhead.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="newFailureThreshold">New circuit breaker failure threshold.</param>
    /// <param name="newBreakDuration">New circuit breaker break duration.</param>
    /// <param name="newMaxConcurrency">New bulkhead max concurrency.</param>
    internal void ApplyAdaptiveThresholds(
        string strategyId,
        int newFailureThreshold,
        TimeSpan newBreakDuration,
        int newMaxConcurrency)
    {
        var cbOptions = new CircuitBreakerOptions
        {
            FailureThreshold = newFailureThreshold,
            BreakDuration = newBreakDuration
        };

        var bhOptions = new BulkheadOptions
        {
            MaxConcurrency = newMaxConcurrency
        };

        ConfigureStrategy(strategyId, cbOptions, bhOptions);
    }

    #region IScalableSubsystem

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["resilience.totalExecutions"] = Interlocked.Read(ref _totalExecutions),
            ["resilience.successfulExecutions"] = Interlocked.Read(ref _successfulExecutions),
            ["resilience.failedExecutions"] = Interlocked.Read(ref _failedExecutions),
            ["resilience.retryAttempts"] = Interlocked.Read(ref _retryAttempts),
            ["resilience.circuitBreakerTrips"] = Interlocked.Read(ref _circuitBreakerTrips),
            ["resilience.bulkheadRejections"] = Interlocked.Read(ref _bulkheadRejections),
            ["resilience.activeCircuitBreakers"] = _circuitBreakers.Count,
            ["resilience.activeBulkheads"] = _bulkheads.Count
        };

        // Per-strategy circuit breaker states
        var cbStates = new Dictionary<string, string>();
        int openCount = 0, halfOpenCount = 0, closedCount = 0;
        foreach (var kvp in _circuitBreakers)
        {
            var state = kvp.Value.State;
            cbStates[kvp.Key] = state.ToString();
            switch (state)
            {
                case CircuitState.Open: openCount++; break;
                case CircuitState.HalfOpen: halfOpenCount++; break;
                case CircuitState.Closed: closedCount++; break;
            }
        }
        metrics["resilience.circuitBreaker.states"] = cbStates;
        metrics["resilience.circuitBreaker.openCount"] = openCount;
        metrics["resilience.circuitBreaker.halfOpenCount"] = halfOpenCount;
        metrics["resilience.circuitBreaker.closedCount"] = closedCount;

        // Per-strategy bulkhead utilization
        var bhUtil = new Dictionary<string, object>();
        foreach (var kvp in _bulkheads)
        {
            var stats = kvp.Value.GetStatistics();
            bhUtil[kvp.Key] = new Dictionary<string, object>
            {
                ["currentConcurrency"] = stats.CurrentConcurrency,
                ["maxConcurrency"] = stats.MaxConcurrency,
                ["totalExecuted"] = stats.TotalExecuted,
                ["totalRejected"] = stats.TotalRejected
            };
        }
        metrics["resilience.bulkhead.utilization"] = bhUtil;

        // Per-strategy latency percentiles
        foreach (var kvp in _strategyMetrics)
        {
            var sm = kvp.Value;
            metrics[$"resilience.strategy.{kvp.Key}.p50Ms"] = sm.GetP50Latency().TotalMilliseconds;
            metrics[$"resilience.strategy.{kvp.Key}.p95Ms"] = sm.GetP95Latency().TotalMilliseconds;
            metrics[$"resilience.strategy.{kvp.Key}.p99Ms"] = sm.GetP99Latency().TotalMilliseconds;
            metrics[$"resilience.strategy.{kvp.Key}.successRate"] = sm.GetSuccessRate();
            metrics[$"resilience.strategy.{kvp.Key}.retryRate"] = sm.GetRetryRate();
        }

        return metrics;
    }

    /// <inheritdoc />
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
        }

        // Reconfigure all bulkheads to respect the new max concurrent operations
        foreach (var kvp in _bulkheads)
        {
            var newBhOptions = new BulkheadOptions
            {
                MaxConcurrency = limits.MaxConcurrentOperations
            };
            _bulkheads[kvp.Key] = new InMemoryBulkheadIsolation(
                $"resilience.{kvp.Key}", newBhOptions);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ScalingLimits CurrentLimits
    {
        get
        {
            lock (_configLock)
            {
                return _currentLimits;
            }
        }
    }

    /// <inheritdoc />
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            // Compute backpressure from bulkhead utilization
            int totalCurrent = 0;
            int totalMax = 0;
            foreach (var kvp in _bulkheads)
            {
                var stats = kvp.Value.GetStatistics();
                totalCurrent += stats.CurrentConcurrency;
                totalMax += stats.MaxConcurrency;
            }

            if (totalMax == 0)
                return BackpressureState.Normal;

            double utilization = (double)totalCurrent / totalMax;
            return utilization switch
            {
                >= 0.80 => BackpressureState.Critical,
                >= 0.50 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    #endregion

    #region Strategy Metrics Access

    /// <summary>
    /// Gets the metrics tracker for a specific strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <returns>The metrics tracker, or <c>null</c> if the strategy has not been used.</returns>
    internal StrategyMetrics? GetStrategyMetrics(string strategyId)
    {
        return _strategyMetrics.TryGetValue(strategyId, out var m) ? m : null;
    }

    /// <summary>
    /// Gets all tracked strategy identifiers.
    /// </summary>
    // Return Keys directly (ICollection<TKey> is already IReadOnlyCollection<string>-compatible
    // via explicit cast) to avoid the ToArray() allocation on every adaptation cycle.
    internal IReadOnlyCollection<string> TrackedStrategies => (IReadOnlyCollection<string>)_strategyMetrics.Keys;

    #endregion

    #region Private Helpers

    private ICircuitBreaker GetOrCreateCircuitBreaker(string strategyId, CircuitBreakerOptions options)
    {
        return _circuitBreakers.GetOrAdd(strategyId, id =>
        {
            var cb = new InMemoryCircuitBreaker($"resilience.{id}", options);
            WireCircuitBreakerStateChange(id, cb);
            return cb;
        });
    }

    private IBulkheadIsolation GetOrCreateBulkhead(string strategyId, BulkheadOptions options)
    {
        return _bulkheads.GetOrAdd(strategyId, id =>
            new InMemoryBulkheadIsolation($"resilience.{id}", options));
    }

    private RetryOptions GetOrCreateRetryOptions(string strategyId)
    {
        return _retryOptions.GetOrAdd(strategyId, _ => new RetryOptions());
    }

    private StrategyMetrics GetOrCreateMetrics(string strategyId)
    {
        return _strategyMetrics.GetOrAdd(strategyId, _ => new StrategyMetrics());
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        RetryOptions options,
        StrategyMetrics metrics,
        CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await action(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _successfulExecutions);
                metrics.RecordSuccess();
                return result;
            }
            catch (Exception) when (attempt < options.MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                Interlocked.Increment(ref _retryAttempts);
                metrics.RecordRetry();

                var delay = ComputeRetryDelay(options, attempt);
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                // If this was the last retry, re-throw
                if (attempt >= options.MaxRetries)
                {
                    throw;
                }
            }
        }
    }

    private static TimeSpan ComputeRetryDelay(RetryOptions options, int attempt)
    {
        double baseMs = options.BaseDelayMs;
        double delayMs = options.BackoffType switch
        {
            RetryBackoffType.Fixed => baseMs,
            RetryBackoffType.Linear => baseMs * attempt,
            RetryBackoffType.Exponential => baseMs * Math.Pow(2, attempt - 1),
            RetryBackoffType.DecorrelatedJitter => baseMs * (1 + Random.Shared.NextDouble() * (Math.Pow(2, attempt) - 1)),
            _ => baseMs * Math.Pow(2, attempt - 1)
        };

        // Apply jitter if enabled
        if (options.UseJitter && options.BackoffType != RetryBackoffType.DecorrelatedJitter)
        {
            delayMs *= (0.5 + Random.Shared.NextDouble());
        }

        // Clamp to max delay
        delayMs = Math.Min(delayMs, options.MaxDelayMs);

        return TimeSpan.FromMilliseconds(delayMs);
    }

    private void WireCircuitBreakerStateChange(string strategyId, ICircuitBreaker cb)
    {
        cb.OnStateChanged += change =>
        {
            PublishCircuitBreakerStateAsync(strategyId, change).ConfigureAwait(false);
        };
    }

    private async Task PublishCircuitBreakerStateAsync(string strategyId, CircuitBreakerStateChanged change)
    {
        if (_federatedBus == null || _disposed) return;

        var timestamp = Interlocked.Increment(ref _lamportClock);

        try
        {
            await _federatedBus.PublishToAllNodesAsync(CircuitBreakerStateTopic, new PluginMessage
            {
                Type = "circuit-breaker.state-change",
                Source = "resilience-scaling-manager",
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = strategyId,
                    ["previousState"] = change.PreviousState.ToString(),
                    ["newState"] = change.NewState.ToString(),
                    ["reason"] = change.Reason ?? string.Empty,
                    ["lamportTimestamp"] = timestamp,
                    ["timestamp"] = change.Timestamp.ToString("o")
                }
            }).ConfigureAwait(false);
        }
        catch
        {

            // Best-effort distributed state sharing -- do not fail the operation
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    private void SubscribeToDistributedState()
    {
        if (_federatedBus == null) return;

        _federatedBus.Subscribe(CircuitBreakerStateTopic, msg =>
        {
            if (!msg.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
                return Task.CompletedTask;
            if (!msg.Payload.TryGetValue("newState", out var stateObj) || stateObj is not string stateStr)
                return Task.CompletedTask;
            if (!msg.Payload.TryGetValue("lamportTimestamp", out var tsObj))
                return Task.CompletedTask;

            long remoteTimestamp = tsObj is long lt ? lt : Convert.ToInt64(tsObj);

            // Last-writer-wins with Lamport timestamp
            long currentTimestamp;
            long newTimestamp;
            do
            {
                currentTimestamp = Interlocked.Read(ref _lamportClock);
                newTimestamp = Math.Max(currentTimestamp, remoteTimestamp) + 1;
            } while (Interlocked.CompareExchange(ref _lamportClock, newTimestamp, currentTimestamp) != currentTimestamp);

            // Apply remote state if it has a higher timestamp
            if (remoteTimestamp > currentTimestamp && _circuitBreakers.TryGetValue(strategyId, out var cb))
            {
                if (Enum.TryParse<CircuitState>(stateStr, out var newState))
                {
                    switch (newState)
                    {
                        case CircuitState.Open:
                            cb.Trip($"Remote state change (Lamport: {remoteTimestamp})");
                            break;
                        case CircuitState.Closed:
                            cb.Reset();
                            break;
                        // HalfOpen transitions are handled by the local circuit breaker timer
                    }
                }
            }

            return Task.CompletedTask;
        });
    }

    #endregion

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _circuitBreakers.Clear();
        _bulkheads.Clear();
        _retryOptions.Clear();
        _strategyMetrics.Clear();
    }
}

/// <summary>
/// Configuration options for retry behavior in the resilience pipeline.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Retry options for resilience scaling")]
public sealed record RetryOptions
{
    /// <summary>
    /// Maximum number of retry attempts. Default: 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Base delay in milliseconds between retries. Default: 200ms.
    /// </summary>
    public double BaseDelayMs { get; init; } = 200;

    /// <summary>
    /// Maximum delay in milliseconds between retries. Default: 30,000ms (30s).
    /// </summary>
    public double MaxDelayMs { get; init; } = 30_000;

    /// <summary>
    /// The backoff strategy type. Default: <see cref="RetryBackoffType.Exponential"/>.
    /// </summary>
    public RetryBackoffType BackoffType { get; init; } = RetryBackoffType.Exponential;

    /// <summary>
    /// Whether to apply jitter to retry delays to prevent thundering herd.
    /// Default: <c>true</c>.
    /// </summary>
    public bool UseJitter { get; init; } = true;
}

/// <summary>
/// Retry backoff type enumeration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Retry backoff types")]
public enum RetryBackoffType
{
    /// <summary>Fixed delay between retries.</summary>
    Fixed,

    /// <summary>Linearly increasing delay between retries.</summary>
    Linear,

    /// <summary>Exponentially increasing delay between retries (default).</summary>
    Exponential,

    /// <summary>Decorrelated jitter backoff (AWS-style).</summary>
    DecorrelatedJitter
}

/// <summary>
/// Tracks per-strategy execution metrics using a circular buffer for latency percentile computation.
/// Provides bounded-memory sliding window metrics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Per-strategy metrics with circular buffer")]
internal sealed class StrategyMetrics
{
    private const int LatencyBufferSize = 1024;
    private readonly double[] _latencyBuffer = new double[LatencyBufferSize];
    private long _latencyIndex;
    private long _latencyCount;

    private long _successCount;
    private long _failureCount;
    private long _retryCount;
    private long _circuitBreakerTrips;
    private long _bulkheadRejections;

    /// <summary>Records a successful execution.</summary>
    public void RecordSuccess()
    {
        Interlocked.Increment(ref _successCount);
    }

    /// <summary>Records a failed execution.</summary>
    public void RecordFailure(TimeSpan latency)
    {
        Interlocked.Increment(ref _failureCount);
        RecordLatency(latency);
    }

    /// <summary>Records a retry attempt.</summary>
    public void RecordRetry()
    {
        Interlocked.Increment(ref _retryCount);
    }

    /// <summary>Records a circuit breaker trip.</summary>
    public void RecordCircuitBreakerTrip()
    {
        Interlocked.Increment(ref _circuitBreakerTrips);
    }

    /// <summary>Records a bulkhead rejection.</summary>
    public void RecordBulkheadRejection()
    {
        Interlocked.Increment(ref _bulkheadRejections);
    }

    /// <summary>Records a latency observation into the circular buffer.</summary>
    /// <param name="latency">The observed latency.</param>
    public void RecordLatency(TimeSpan latency)
    {
        var idx = Interlocked.Increment(ref _latencyIndex) - 1;
        _latencyBuffer[idx % LatencyBufferSize] = latency.TotalMilliseconds;
        Interlocked.Increment(ref _latencyCount);
    }

    /// <summary>Gets the P50 (median) latency.</summary>
    public TimeSpan GetP50Latency() => GetPercentileLatency(0.50);

    /// <summary>Gets the P95 latency.</summary>
    public TimeSpan GetP95Latency() => GetPercentileLatency(0.95);

    /// <summary>Gets the P99 latency.</summary>
    public TimeSpan GetP99Latency() => GetPercentileLatency(0.99);

    /// <summary>Gets the success rate as a fraction [0, 1].</summary>
    public double GetSuccessRate()
    {
        long success = Interlocked.Read(ref _successCount);
        long failure = Interlocked.Read(ref _failureCount);
        long total = success + failure;
        return total == 0 ? 1.0 : (double)success / total;
    }

    /// <summary>Gets the retry rate as a fraction of retries per total execution.</summary>
    public double GetRetryRate()
    {
        long retries = Interlocked.Read(ref _retryCount);
        long success = Interlocked.Read(ref _successCount);
        long failure = Interlocked.Read(ref _failureCount);
        long total = success + failure;
        return total == 0 ? 0.0 : (double)retries / total;
    }

    /// <summary>Gets the error rate as a fraction [0, 1].</summary>
    public double GetErrorRate()
    {
        long success = Interlocked.Read(ref _successCount);
        long failure = Interlocked.Read(ref _failureCount);
        long total = success + failure;
        return total == 0 ? 0.0 : (double)failure / total;
    }

    /// <summary>Gets the total number of executions (success + failure).</summary>
    public long TotalExecutions => Interlocked.Read(ref _successCount) + Interlocked.Read(ref _failureCount);

    private TimeSpan GetPercentileLatency(double percentile)
    {
        long count = Interlocked.Read(ref _latencyCount);
        if (count == 0) return TimeSpan.Zero;

        int sampleCount = (int)Math.Min(count, LatencyBufferSize);
        var samples = new double[sampleCount];
        Array.Copy(_latencyBuffer, samples, sampleCount);
        Array.Sort(samples);

        int index = Math.Max(0, (int)Math.Ceiling(percentile * sampleCount) - 1);
        return TimeSpan.FromMilliseconds(samples[index]);
    }
}
