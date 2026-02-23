using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateResilience.Scaling;

/// <summary>
/// Computes optimal circuit breaker and bulkhead parameters from observed metrics
/// using sliding window analysis and exponential moving average (EMA) smoothing.
/// Integrates with <see cref="ResilienceScalingManager"/> to apply adaptive thresholds
/// on a configurable interval (default: 30 seconds).
/// </summary>
/// <remarks>
/// <para>
/// Adaptive threshold computation follows these rules:
/// <list type="bullet">
///   <item><description>Circuit breaker failure threshold increases when error rate &lt; 1% (healthy system tolerates more)</description></item>
///   <item><description>Circuit breaker failure threshold decreases when error rate &gt; 5% (trip faster to protect)</description></item>
///   <item><description>Reset timeout adjusts based on observed recovery time patterns</description></item>
///   <item><description>Bulkhead max concurrency increases when P99 latency drops (system can handle more)</description></item>
///   <item><description>Bulkhead max concurrency decreases when P99 latency rises (protect from overload)</description></item>
/// </list>
/// </para>
/// <para>
/// All adaptation parameters are configurable at runtime via <see cref="AdaptiveThresholdOptions"/>
/// and can be updated through <see cref="ScalingLimits"/> reconfiguration.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Adaptive resilience thresholds from observed metrics")]
public sealed class AdaptiveResilienceThresholds : IDisposable
{
    private readonly ResilienceScalingManager _manager;
    private readonly ConcurrentDictionary<string, StrategyAdaptiveState> _adaptiveStates = new();
    private readonly Timer _adaptationTimer;
    private readonly object _optionsLock = new();
    private AdaptiveThresholdOptions _options;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveResilienceThresholds"/> class.
    /// </summary>
    /// <param name="manager">The resilience scaling manager to apply thresholds to.</param>
    /// <param name="options">Adaptive threshold options. Uses defaults if <c>null</c>.</param>
    public AdaptiveResilienceThresholds(
        ResilienceScalingManager manager,
        AdaptiveThresholdOptions? options = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _options = options ?? new AdaptiveThresholdOptions();

        // Wire bidirectional reference
        _manager.AdaptiveThresholds = this;

        // Start adaptation timer
        _adaptationTimer = new Timer(
            _ => ComputeAndApplyThresholds(),
            null,
            _options.AdaptationIntervalMs,
            _options.AdaptationIntervalMs);
    }

    /// <summary>
    /// Computes optimal thresholds for all tracked strategies and applies them to the manager.
    /// Called automatically by the adaptation timer at the configured interval.
    /// Can also be called manually for immediate adaptation.
    /// </summary>
    public void ComputeAndApplyThresholds()
    {
        if (_disposed) return;

        AdaptiveThresholdOptions opts;
        lock (_optionsLock)
        {
            opts = _options;
        }

        foreach (var strategyId in _manager.TrackedStrategies)
        {
            var metrics = _manager.GetStrategyMetrics(strategyId);
            if (metrics == null || metrics.TotalExecutions < opts.MinSamplesBeforeAdaptation)
                continue;

            var state = _adaptiveStates.GetOrAdd(strategyId, _ => new StrategyAdaptiveState(opts));

            // Update sliding windows
            double errorRate = metrics.GetErrorRate();
            double p99Ms = metrics.GetP99Latency().TotalMilliseconds;
            state.RecordErrorRate(errorRate);
            state.RecordLatency(p99Ms);

            // Compute adaptive circuit breaker thresholds
            var (failureThreshold, breakDuration) = ComputeCircuitBreakerThresholds(state, opts);

            // Compute adaptive bulkhead sizing
            int maxConcurrency = ComputeBulkheadSize(state, opts);

            // Apply to manager
            _manager.ApplyAdaptiveThresholds(strategyId, failureThreshold, breakDuration, maxConcurrency);
        }
    }

    /// <summary>
    /// Reconfigures the adaptive threshold options at runtime.
    /// </summary>
    /// <param name="newOptions">The new options to apply.</param>
    public void Reconfigure(AdaptiveThresholdOptions newOptions)
    {
        lock (_optionsLock)
        {
            _options = newOptions ?? throw new ArgumentNullException(nameof(newOptions));
        }

        // Update timer interval
        _adaptationTimer.Change(newOptions.AdaptationIntervalMs, newOptions.AdaptationIntervalMs);
    }

    /// <summary>
    /// Gets the current adaptive state for a strategy. Returns <c>null</c> if not tracked.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    internal StrategyAdaptiveState? GetAdaptiveState(string strategyId)
    {
        return _adaptiveStates.TryGetValue(strategyId, out var state) ? state : null;
    }

    #region Threshold Computation

    private static (int failureThreshold, TimeSpan breakDuration) ComputeCircuitBreakerThresholds(
        StrategyAdaptiveState state,
        AdaptiveThresholdOptions opts)
    {
        double emaErrorRate = state.EmaErrorRate;
        int currentThreshold = state.CurrentFailureThreshold;
        TimeSpan currentBreakDuration = state.CurrentBreakDuration;

        // Adaptive failure threshold
        int newThreshold;
        if (emaErrorRate < opts.HealthyErrorRateThreshold)
        {
            // System is healthy: increase tolerance (allow more failures before tripping)
            newThreshold = (int)Math.Ceiling(currentThreshold * opts.HealthyThresholdIncreaseFactor);
        }
        else if (emaErrorRate > opts.CriticalErrorRateThreshold)
        {
            // System under stress: decrease tolerance (trip faster)
            newThreshold = (int)Math.Floor(currentThreshold * opts.CriticalThresholdDecreaseFactor);
        }
        else
        {
            // Normal range: no change
            newThreshold = currentThreshold;
        }

        // Clamp within bounds
        newThreshold = Math.Clamp(newThreshold, opts.MinFailureThreshold, opts.MaxFailureThreshold);

        // Adaptive break duration based on recovery time pattern
        TimeSpan newBreakDuration;
        double avgRecoveryMs = state.EmaRecoveryTimeMs;
        if (avgRecoveryMs > 0 && avgRecoveryMs < opts.FastRecoveryThresholdMs)
        {
            // Fast recovery: shorter break duration
            newBreakDuration = TimeSpan.FromMilliseconds(
                Math.Max(avgRecoveryMs * opts.BreakDurationRecoveryMultiplier, opts.MinBreakDurationMs));
        }
        else if (avgRecoveryMs > opts.SlowRecoveryThresholdMs)
        {
            // Slow recovery: longer break duration
            newBreakDuration = TimeSpan.FromMilliseconds(
                Math.Min(avgRecoveryMs * opts.BreakDurationRecoveryMultiplier, opts.MaxBreakDurationMs));
        }
        else
        {
            newBreakDuration = currentBreakDuration;
        }

        // Clamp break duration
        double breakMs = Math.Clamp(newBreakDuration.TotalMilliseconds, opts.MinBreakDurationMs, opts.MaxBreakDurationMs);
        newBreakDuration = TimeSpan.FromMilliseconds(breakMs);

        // Update state
        state.CurrentFailureThreshold = newThreshold;
        state.CurrentBreakDuration = newBreakDuration;

        return (newThreshold, newBreakDuration);
    }

    private static int ComputeBulkheadSize(
        StrategyAdaptiveState state,
        AdaptiveThresholdOptions opts)
    {
        double emaP99 = state.EmaP99Latency;
        int currentMax = state.CurrentMaxConcurrency;

        if (emaP99 <= 0)
            return currentMax;

        double previousP99 = state.PreviousEmaP99Latency;
        if (previousP99 <= 0)
        {
            state.PreviousEmaP99Latency = emaP99;
            return currentMax;
        }

        int newMax;
        double latencyChange = (emaP99 - previousP99) / previousP99;

        if (latencyChange < -opts.LatencyDropThreshold)
        {
            // P99 dropping: increase concurrency (system can handle more)
            newMax = (int)Math.Ceiling(currentMax * opts.ConcurrencyIncreaseFactor);
        }
        else if (latencyChange > opts.LatencyRiseThreshold)
        {
            // P99 rising: decrease concurrency (protect from overload)
            newMax = (int)Math.Floor(currentMax * opts.ConcurrencyDecreaseFactor);
        }
        else
        {
            // Stable: no change
            newMax = currentMax;
        }

        // Clamp within bounds
        newMax = Math.Clamp(newMax, opts.MinConcurrency, opts.MaxConcurrency);

        state.PreviousEmaP99Latency = emaP99;
        state.CurrentMaxConcurrency = newMax;

        return newMax;
    }

    #endregion

    /// <summary>
    /// Disposes the adaptation timer and clears state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adaptationTimer.Dispose();
        _adaptiveStates.Clear();
    }
}

/// <summary>
/// Per-strategy adaptive state tracking using sliding window circular buffers
/// and exponential moving average (EMA) smoothing for stable threshold transitions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Per-strategy adaptive state with EMA smoothing")]
internal sealed class StrategyAdaptiveState
{
    private const int WindowSize = 128;

    // Circular buffers for sliding window observations
    private readonly double[] _errorRateWindow = new double[WindowSize];
    private readonly double[] _latencyWindow = new double[WindowSize];
    private long _errorRateIndex;
    private long _latencyIndex;
    private long _errorRateCount;
    private long _latencyCount;

    // EMA values
    private readonly double _emaAlpha;

    /// <summary>Current EMA error rate.</summary>
    internal double EmaErrorRate { get; private set; }

    /// <summary>Current EMA P99 latency in milliseconds.</summary>
    internal double EmaP99Latency { get; private set; }

    /// <summary>Previous EMA P99 latency for trend detection.</summary>
    internal double PreviousEmaP99Latency { get; set; }

    /// <summary>EMA recovery time in milliseconds.</summary>
    internal double EmaRecoveryTimeMs { get; private set; }

    // Current adaptive parameter values
    /// <summary>Current adaptive circuit breaker failure threshold.</summary>
    internal int CurrentFailureThreshold { get; set; }

    /// <summary>Current adaptive circuit breaker break duration.</summary>
    internal TimeSpan CurrentBreakDuration { get; set; }

    /// <summary>Current adaptive bulkhead max concurrency.</summary>
    internal int CurrentMaxConcurrency { get; set; }

    /// <summary>
    /// Initializes a new strategy adaptive state with default values from options.
    /// </summary>
    /// <param name="opts">The adaptive threshold options providing defaults and EMA alpha.</param>
    public StrategyAdaptiveState(AdaptiveThresholdOptions opts)
    {
        _emaAlpha = opts.EmaAlpha;
        CurrentFailureThreshold = opts.DefaultFailureThreshold;
        CurrentBreakDuration = TimeSpan.FromMilliseconds(opts.DefaultBreakDurationMs);
        CurrentMaxConcurrency = opts.DefaultMaxConcurrency;
    }

    /// <summary>
    /// Records an error rate observation and updates EMA.
    /// </summary>
    /// <param name="errorRate">The observed error rate [0, 1].</param>
    public void RecordErrorRate(double errorRate)
    {
        var idx = Interlocked.Increment(ref _errorRateIndex) - 1;
        _errorRateWindow[idx % WindowSize] = errorRate;
        Interlocked.Increment(ref _errorRateCount);

        // Update EMA: EMA = alpha * new + (1 - alpha) * old
        EmaErrorRate = _emaAlpha * errorRate + (1.0 - _emaAlpha) * EmaErrorRate;
    }

    /// <summary>
    /// Records a P99 latency observation and updates EMA.
    /// </summary>
    /// <param name="latencyMs">The observed P99 latency in milliseconds.</param>
    public void RecordLatency(double latencyMs)
    {
        var idx = Interlocked.Increment(ref _latencyIndex) - 1;
        _latencyWindow[idx % WindowSize] = latencyMs;
        Interlocked.Increment(ref _latencyCount);

        // Update EMA
        EmaP99Latency = _emaAlpha * latencyMs + (1.0 - _emaAlpha) * EmaP99Latency;
    }

    /// <summary>
    /// Records a recovery time observation (time from circuit-open to circuit-closed).
    /// </summary>
    /// <param name="recoveryMs">The observed recovery time in milliseconds.</param>
    public void RecordRecoveryTime(double recoveryMs)
    {
        EmaRecoveryTimeMs = _emaAlpha * recoveryMs + (1.0 - _emaAlpha) * EmaRecoveryTimeMs;
    }

    /// <summary>
    /// Gets the windowed average error rate from the circular buffer.
    /// </summary>
    public double GetWindowedErrorRate()
    {
        long count = Interlocked.Read(ref _errorRateCount);
        if (count == 0) return 0.0;

        int sampleCount = (int)Math.Min(count, WindowSize);
        double sum = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            sum += _errorRateWindow[i];
        }
        return sum / sampleCount;
    }

    /// <summary>
    /// Gets the windowed average P99 latency from the circular buffer.
    /// </summary>
    public double GetWindowedP99Latency()
    {
        long count = Interlocked.Read(ref _latencyCount);
        if (count == 0) return 0.0;

        int sampleCount = (int)Math.Min(count, WindowSize);
        double sum = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            sum += _latencyWindow[i];
        }
        return sum / sampleCount;
    }
}

/// <summary>
/// Configuration options for adaptive resilience threshold computation.
/// All parameters are runtime-reconfigurable.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Adaptive threshold configuration")]
public sealed record AdaptiveThresholdOptions
{
    // --- Adaptation interval ---

    /// <summary>
    /// Interval in milliseconds between adaptive threshold recomputations. Default: 30,000ms (30s).
    /// </summary>
    public int AdaptationIntervalMs { get; init; } = 30_000;

    /// <summary>
    /// Minimum number of samples required before adaptation begins. Default: 10.
    /// </summary>
    public int MinSamplesBeforeAdaptation { get; init; } = 10;

    // --- EMA smoothing ---

    /// <summary>
    /// Alpha value for exponential moving average smoothing. Range: (0, 1].
    /// Higher values weight recent observations more heavily.
    /// Default: 0.3.
    /// </summary>
    public double EmaAlpha { get; init; } = 0.3;

    // --- Circuit breaker threshold adaptation ---

    /// <summary>
    /// Error rate below which the system is considered healthy.
    /// Failure threshold is increased (more tolerant). Default: 0.01 (1%).
    /// </summary>
    public double HealthyErrorRateThreshold { get; init; } = 0.01;

    /// <summary>
    /// Error rate above which the system is considered under stress.
    /// Failure threshold is decreased (trip faster). Default: 0.05 (5%).
    /// </summary>
    public double CriticalErrorRateThreshold { get; init; } = 0.05;

    /// <summary>
    /// Factor by which to increase failure threshold when healthy. Default: 1.1 (10% increase).
    /// </summary>
    public double HealthyThresholdIncreaseFactor { get; init; } = 1.1;

    /// <summary>
    /// Factor by which to decrease failure threshold when critical. Default: 0.8 (20% decrease).
    /// </summary>
    public double CriticalThresholdDecreaseFactor { get; init; } = 0.8;

    /// <summary>
    /// Minimum allowed circuit breaker failure threshold. Default: 2.
    /// </summary>
    public int MinFailureThreshold { get; init; } = 2;

    /// <summary>
    /// Maximum allowed circuit breaker failure threshold. Default: 50.
    /// </summary>
    public int MaxFailureThreshold { get; init; } = 50;

    /// <summary>
    /// Default circuit breaker failure threshold before adaptation. Default: 5.
    /// </summary>
    public int DefaultFailureThreshold { get; init; } = 5;

    // --- Break duration adaptation ---

    /// <summary>
    /// Recovery time in milliseconds below which recovery is considered fast. Default: 5000ms (5s).
    /// </summary>
    public double FastRecoveryThresholdMs { get; init; } = 5_000;

    /// <summary>
    /// Recovery time in milliseconds above which recovery is considered slow. Default: 30,000ms (30s).
    /// </summary>
    public double SlowRecoveryThresholdMs { get; init; } = 30_000;

    /// <summary>
    /// Multiplier applied to recovery time to compute break duration. Default: 2.0.
    /// </summary>
    public double BreakDurationRecoveryMultiplier { get; init; } = 2.0;

    /// <summary>
    /// Minimum break duration in milliseconds. Default: 5,000ms (5s).
    /// </summary>
    public double MinBreakDurationMs { get; init; } = 5_000;

    /// <summary>
    /// Maximum break duration in milliseconds. Default: 120,000ms (2 min).
    /// </summary>
    public double MaxBreakDurationMs { get; init; } = 120_000;

    /// <summary>
    /// Default break duration in milliseconds before adaptation. Default: 30,000ms (30s).
    /// </summary>
    public double DefaultBreakDurationMs { get; init; } = 30_000;

    // --- Bulkhead concurrency adaptation ---

    /// <summary>
    /// P99 latency drop percentage threshold to trigger concurrency increase. Default: 0.10 (10%).
    /// </summary>
    public double LatencyDropThreshold { get; init; } = 0.10;

    /// <summary>
    /// P99 latency rise percentage threshold to trigger concurrency decrease. Default: 0.15 (15%).
    /// </summary>
    public double LatencyRiseThreshold { get; init; } = 0.15;

    /// <summary>
    /// Factor by which to increase max concurrency when latency drops. Default: 1.15 (15% increase).
    /// </summary>
    public double ConcurrencyIncreaseFactor { get; init; } = 1.15;

    /// <summary>
    /// Factor by which to decrease max concurrency when latency rises. Default: 0.85 (15% decrease).
    /// </summary>
    public double ConcurrencyDecreaseFactor { get; init; } = 0.85;

    /// <summary>
    /// Minimum allowed bulkhead concurrency. Default: 2.
    /// </summary>
    public int MinConcurrency { get; init; } = 2;

    /// <summary>
    /// Maximum allowed bulkhead concurrency. Default: 512.
    /// </summary>
    public int MaxConcurrency { get; init; } = 512;

    /// <summary>
    /// Default bulkhead max concurrency before adaptation. Default: 64.
    /// </summary>
    public int DefaultMaxConcurrency { get; init; } = 64;
}
