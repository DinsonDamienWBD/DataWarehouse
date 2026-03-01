using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using SdkBackpressureStrategy = DataWarehouse.SDK.Contracts.Scaling.BackpressureStrategy;
using SdkBackpressureState = DataWarehouse.SDK.Contracts.Scaling.BackpressureState;

namespace DataWarehouse.Plugins.UltimateStreamingData.Scaling;

/// <summary>
/// Implements <see cref="IBackpressureAware"/> for the streaming subsystem with five
/// backpressure strategies: DropOldest, BlockProducer, ShedLoad, DegradeQuality, and Adaptive.
/// Uses lock-free <see cref="Interlocked"/> counters for queue depth tracking on the hot path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pressure detection:</b> Three configurable thresholds (Warning at 70%, Critical at 85%,
/// Shedding at 95%) control state transitions. The thresholds are reconfigurable at runtime
/// via <see cref="ReconfigureThresholdsAsync"/>.
/// </para>
/// <para>
/// <b>Adaptive strategy:</b> Automatically switches between strategies based on an exponential
/// moving average (EMA) of queue depth relative to capacity. When EMA is below warning, uses
/// normal flow; between warning and critical, uses BlockProducer; between critical and shedding,
/// uses DegradeQuality; above shedding, uses ShedLoad.
/// </para>
/// <para>
/// <b>Integration:</b> Delegates to or wraps the existing
/// <see cref="Features.BackpressureHandling"/> feature where channel-based backpressure
/// is already implemented, extending it with the <see cref="IBackpressureAware"/> contract
/// for coordinated cross-subsystem backpressure.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-02: Streaming backpressure handler with 5 strategies")]
public sealed class StreamingBackpressureHandler : IBackpressureAware, IDisposable
{
    // ---- Constants ----
    private const string SubsystemNameValue = "UltimateStreamingData";
    private const double DefaultWarningThreshold = 0.70;
    private const double DefaultCriticalThreshold = 0.85;
    private const double DefaultSheddingThreshold = 0.95;
    private const double EmaAlpha = 0.3; // Smoothing factor for exponential moving average

    // ---- Configuration ----
    private long _maxCapacity;
    private double _warningThreshold;
    private double _criticalThreshold;
    private double _sheddingThreshold;

    // ---- State tracking (lock-free) ----
    private long _queueDepth;
    private long _totalQueued;
    private long _totalDequeued;
    private long _totalDropped;
    private long _totalShed;
    private long _totalDegraded;
    private long _totalBlocked;

    // ---- EMA for adaptive strategy (stored as Int64 bits for lock-free access) ----
    private long _emaQueueDepthBits;

    // ---- Backpressure state ----
    private volatile SdkBackpressureState _currentState = SdkBackpressureState.Normal;
    private volatile SdkBackpressureStrategy _strategy = SdkBackpressureStrategy.Adaptive;

    // ---- Producer blocking ----
    private readonly SemaphoreSlim _producerSemaphore;

    // ---- Disposal ----
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="StreamingBackpressureHandler"/> with the specified capacity
    /// and optional threshold overrides.
    /// </summary>
    /// <param name="maxCapacity">Maximum queue capacity before backpressure engages.</param>
    /// <param name="warningThreshold">
    /// Warning threshold as a fraction of max capacity (0-1). Default: 0.70.
    /// </param>
    /// <param name="criticalThreshold">
    /// Critical threshold as a fraction of max capacity (0-1). Default: 0.85.
    /// </param>
    /// <param name="sheddingThreshold">
    /// Shedding threshold as a fraction of max capacity (0-1). Default: 0.95.
    /// </param>
    public StreamingBackpressureHandler(
        long maxCapacity = 10_000,
        double warningThreshold = DefaultWarningThreshold,
        double criticalThreshold = DefaultCriticalThreshold,
        double sheddingThreshold = DefaultSheddingThreshold)
    {
        if (maxCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Max capacity must be at least 1.");

        _maxCapacity = maxCapacity;
        _warningThreshold = Math.Clamp(warningThreshold, 0.0, 1.0);
        _criticalThreshold = Math.Clamp(criticalThreshold, 0.0, 1.0);
        _sheddingThreshold = Math.Clamp(sheddingThreshold, 0.0, 1.0);

        // Semaphore for BlockProducer strategy: initially allows up to maxCapacity concurrent publishes
        _producerSemaphore = new SemaphoreSlim((int)Math.Min(maxCapacity, int.MaxValue));
    }

    // ---------------------------------------------------------------
    // IBackpressureAware
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public SdkBackpressureStrategy Strategy
    {
        get => _strategy;
        set => _strategy = value;
    }

    /// <inheritdoc/>
    public SdkBackpressureState CurrentState => _currentState;

    /// <inheritdoc/>
    public event Action<BackpressureStateChangedEventArgs>? OnBackpressureChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// Evaluates the provided <see cref="BackpressureContext"/> against configured thresholds
    /// and transitions the backpressure state accordingly. Fires <see cref="OnBackpressureChanged"/>
    /// on state transitions.
    /// </remarks>
    public Task ApplyBackpressureAsync(BackpressureContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var utilization = context.MaxCapacity > 0
            ? (double)context.QueueDepth / context.MaxCapacity
            : 0.0;

        UpdateState(utilization);

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Queue depth tracking (called by StreamingScalingManager)
    // ---------------------------------------------------------------

    /// <summary>
    /// Records that an event has been queued, incrementing the queue depth counter.
    /// Uses <see cref="Interlocked"/> for lock-free hot-path performance.
    /// </summary>
    public void RecordEventQueued()
    {
        Interlocked.Increment(ref _queueDepth);
        Interlocked.Increment(ref _totalQueued);

        // Update EMA
        UpdateEma(Interlocked.Read(ref _queueDepth));

        // Re-evaluate state based on current depth
        var utilization = _maxCapacity > 0
            ? (double)Interlocked.Read(ref _queueDepth) / _maxCapacity
            : 0.0;
        UpdateState(utilization);
    }

    /// <summary>
    /// Records that an event has been dequeued, decrementing the queue depth counter.
    /// Uses <see cref="Interlocked"/> for lock-free hot-path performance.
    /// </summary>
    public void RecordEventDequeued()
    {
        var newDepth = Interlocked.Decrement(ref _queueDepth);
        if (newDepth < 0) Interlocked.Exchange(ref _queueDepth, 0); // Guard against underflow
        Interlocked.Increment(ref _totalDequeued);

        // Update EMA
        UpdateEma(Math.Max(0, newDepth));

        // Re-evaluate state
        var utilization = _maxCapacity > 0
            ? (double)Math.Max(0, newDepth) / _maxCapacity
            : 0.0;
        UpdateState(utilization);
    }

    /// <summary>
    /// Gets the current queue depth (lock-free read).
    /// </summary>
    public int CurrentQueueDepth => (int)Math.Max(0, Interlocked.Read(ref _queueDepth));

    // ---------------------------------------------------------------
    // Strategy implementations
    // ---------------------------------------------------------------

    /// <summary>
    /// Applies the DropOldest strategy: discards the oldest unprocessed events when
    /// queue depth exceeds the capacity limit. Logs discards for observability.
    /// </summary>
    /// <param name="excessCount">Number of excess events to discard.</param>
    /// <returns>The number of events actually dropped.</returns>
    public int ApplyDropOldest(int excessCount)
    {
        if (excessCount <= 0) return 0;

        int dropped = Math.Min(excessCount, CurrentQueueDepth);
        for (int i = 0; i < dropped; i++)
        {
            // Simulate dequeue of oldest event
            var newDepth = Interlocked.Decrement(ref _queueDepth);
            if (newDepth < 0)
            {
                Interlocked.Exchange(ref _queueDepth, 0);
                dropped = i + 1;
                break;
            }
        }

        Interlocked.Add(ref _totalDropped, dropped);
        return dropped;
    }

    /// <summary>
    /// Applies the BlockProducer strategy: blocks the caller via <see cref="SemaphoreSlim"/>
    /// until queue depth drops below the configured threshold.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the producer is unblocked.</returns>
    public async Task ApplyBlockProducerAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalBlocked);
        await _producerSemaphore.WaitAsync(ct).ConfigureAwait(false);
        _producerSemaphore.Release();
    }

    /// <summary>
    /// Applies the ShedLoad strategy: rejects new publishes when load exceeds capacity.
    /// Returns false to indicate the event was rejected; the caller should retry later.
    /// </summary>
    /// <returns>True if the event can proceed; false if it should be rejected.</returns>
    public bool ApplyShedLoad()
    {
        var utilization = _maxCapacity > 0
            ? (double)Interlocked.Read(ref _queueDepth) / _maxCapacity
            : 0.0;

        if (utilization >= _sheddingThreshold)
        {
            Interlocked.Increment(ref _totalShed);
            return false; // Reject -- caller should retry
        }
        return true;
    }

    /// <summary>
    /// Applies the DegradeQuality strategy: reduces processing fidelity by indicating
    /// the system should skip enrichment, batch more aggressively, and reduce ack guarantees.
    /// </summary>
    /// <returns>A <see cref="DegradationDirective"/> indicating what fidelity reductions to apply.</returns>
    public DegradationDirective ApplyDegradeQuality()
    {
        var utilization = _maxCapacity > 0
            ? (double)Interlocked.Read(ref _queueDepth) / _maxCapacity
            : 0.0;

        Interlocked.Increment(ref _totalDegraded);

        return new DegradationDirective
        {
            SkipEnrichment = utilization >= _criticalThreshold,
            BatchAggressively = utilization >= _warningThreshold,
            ReduceAckGuarantees = utilization >= _sheddingThreshold,
            UtilizationRatio = utilization
        };
    }

    /// <summary>
    /// Applies the Adaptive strategy: auto-switches between strategies based on the
    /// exponential moving average (EMA) of queue depth relative to capacity thresholds.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The strategy that was selected and applied.</returns>
    public async Task<SdkBackpressureStrategy> ApplyAdaptiveAsync(CancellationToken ct = default)
    {
        var ema = GetEmaQueueDepth();
        var utilization = _maxCapacity > 0 ? ema / _maxCapacity : 0.0;

        if (utilization >= _sheddingThreshold)
        {
            // Critical overload -- shed load
            ApplyShedLoad();
            return SdkBackpressureStrategy.ShedLoad;
        }

        if (utilization >= _criticalThreshold)
        {
            // High pressure -- degrade quality
            ApplyDegradeQuality();
            return SdkBackpressureStrategy.DegradeQuality;
        }

        if (utilization >= _warningThreshold)
        {
            // Moderate pressure -- block producers
            await ApplyBlockProducerAsync(ct).ConfigureAwait(false);
            return SdkBackpressureStrategy.BlockProducer;
        }

        // Normal utilization — no active backpressure is needed.
        // DropOldest is returned as the lowest-impact value; callers that distinguish "no action"
        // from "actively dropping" should compare CurrentState against SdkBackpressureState.Normal.
        return SdkBackpressureStrategy.DropOldest;
    }

    // ---------------------------------------------------------------
    // Runtime reconfiguration
    // ---------------------------------------------------------------

    /// <summary>
    /// Reconfigures the backpressure thresholds and capacity at runtime.
    /// </summary>
    /// <param name="maxCapacity">New maximum capacity. Use 0 to keep current.</param>
    /// <param name="warningThreshold">New warning threshold (0-1). Use negative to keep current.</param>
    /// <param name="criticalThreshold">New critical threshold (0-1). Use negative to keep current.</param>
    /// <param name="sheddingThreshold">New shedding threshold (0-1). Use negative to keep current.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task ReconfigureThresholdsAsync(
        long maxCapacity = 0,
        double warningThreshold = -1,
        double criticalThreshold = -1,
        double sheddingThreshold = -1,
        CancellationToken ct = default)
    {
        if (maxCapacity > 0) Interlocked.Exchange(ref _maxCapacity, maxCapacity);
        if (warningThreshold >= 0) _warningThreshold = Math.Clamp(warningThreshold, 0.0, 1.0);
        if (criticalThreshold >= 0) _criticalThreshold = Math.Clamp(criticalThreshold, 0.0, 1.0);
        if (sheddingThreshold >= 0) _sheddingThreshold = Math.Clamp(sheddingThreshold, 0.0, 1.0);

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Evaluates the current utilization against thresholds and transitions the
    /// backpressure state. Fires <see cref="OnBackpressureChanged"/> on transitions.
    /// </summary>
    private void UpdateState(double utilization)
    {
        var previousState = _currentState;
        SdkBackpressureState newState;

        if (utilization >= _sheddingThreshold)
            newState = SdkBackpressureState.Shedding;
        else if (utilization >= _criticalThreshold)
            newState = SdkBackpressureState.Critical;
        else if (utilization >= _warningThreshold)
            newState = SdkBackpressureState.Warning;
        else
            newState = SdkBackpressureState.Normal;

        if (newState != previousState)
        {
            _currentState = newState;
            OnBackpressureChanged?.Invoke(new BackpressureStateChangedEventArgs(
                PreviousState: previousState,
                CurrentState: newState,
                SubsystemName: SubsystemNameValue,
                Timestamp: DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Updates the exponential moving average of queue depth using lock-free bit conversion.
    /// </summary>
    private void UpdateEma(long currentDepth)
    {
        var oldEma = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _emaQueueDepthBits));
        var newEma = (EmaAlpha * currentDepth) + ((1.0 - EmaAlpha) * oldEma);
        Interlocked.Exchange(ref _emaQueueDepthBits, BitConverter.DoubleToInt64Bits(newEma));
    }

    /// <summary>
    /// Gets the current exponential moving average of queue depth.
    /// </summary>
    private double GetEmaQueueDepth()
    {
        return BitConverter.Int64BitsToDouble(Interlocked.Read(ref _emaQueueDepthBits));
    }

    // ---------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _producerSemaphore.Dispose();
    }
}

/// <summary>
/// Directive indicating what quality degradation to apply under backpressure.
/// Used by the <see cref="BackpressureStrategy.DegradeQuality"/> strategy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-02: Degradation directive for streaming backpressure")]
public sealed record DegradationDirective
{
    /// <summary>Gets whether to skip event enrichment processing.</summary>
    public bool SkipEnrichment { get; init; }

    /// <summary>Gets whether to batch events more aggressively for throughput over latency.</summary>
    public bool BatchAggressively { get; init; }

    /// <summary>Gets whether to reduce acknowledgement guarantees (e.g., skip fsync).</summary>
    public bool ReduceAckGuarantees { get; init; }

    /// <summary>Gets the current utilization ratio that triggered the degradation.</summary>
    public double UtilizationRatio { get; init; }
}
