using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Contract for AI advisors that consume observation batches from the pipeline.
/// Plans 02-04 implement concrete advisors (performance, anomaly, resource).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01)")]
public interface IAiAdvisor
{
    /// <summary>
    /// Unique identifier for this advisor.
    /// </summary>
    string AdvisorId { get; }

    /// <summary>
    /// Process a batch of observation events. Called by the pipeline on each drain cycle.
    /// Implementations should be idempotent and handle partial batches gracefully.
    /// </summary>
    /// <param name="batch">The batch of observation events to process.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessObservationsAsync(IReadOnlyList<ObservationEvent> batch, CancellationToken ct);
}

/// <summary>
/// Async consumer pipeline that drains observations from the ring buffer and dispatches
/// them to registered IAiAdvisor instances. Integrates with OverheadThrottle to
/// auto-drop batches when CPU overhead exceeds the configured limit.
/// </summary>
/// <remarks>
/// The pipeline runs a background loop that: (1) measures CPU, (2) checks throttle,
/// (3) drains ring buffer, (4) fans out to all advisors. Each advisor is invoked
/// independently — one failing advisor does not block others.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01)")]
public sealed class AiObservationPipeline : IAsyncDisposable
{
    private readonly AiObservationRingBuffer _buffer;
    private readonly OverheadThrottle _throttle;
    private readonly TimeSpan _drainInterval;
    private readonly List<IAiAdvisor> _advisors = new();
    private readonly List<ObservationEvent> _batchBuffer = new();
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private volatile bool _disposed;

    /// <summary>
    /// Maximum number of events to drain per cycle.
    /// </summary>
    private const int MaxDrainBatchSize = 512;

    /// <summary>
    /// Creates a new AI observation pipeline.
    /// </summary>
    /// <param name="buffer">The ring buffer to drain observations from.</param>
    /// <param name="throttle">The overhead throttle controlling auto-drop behavior.</param>
    /// <param name="drainInterval">How often to drain the buffer. Default 100ms.</param>
    public AiObservationPipeline(
        AiObservationRingBuffer buffer,
        OverheadThrottle throttle,
        TimeSpan? drainInterval = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        _drainInterval = drainInterval ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// Registered advisors that receive observation batches. Add advisors before calling StartAsync.
    /// </summary>
    public IList<IAiAdvisor> Advisors => _advisors;

    /// <summary>
    /// Total number of observation batches dispatched to advisors since pipeline start.
    /// </summary>
    public long DispatchedBatches { get; private set; }

    /// <summary>
    /// Total number of batches skipped due to throttle activation.
    /// </summary>
    public long ThrottledBatches { get; private set; }

    /// <summary>
    /// Starts the background drain loop. Call StopAsync or DisposeAsync to stop.
    /// </summary>
    /// <param name="ct">Cancellation token for the pipeline lifetime.</param>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AiObservationPipeline));
        if (_backgroundTask is not null) return Task.CompletedTask; // Already started

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _backgroundTask = Task.Run(() => DrainLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background loop, drains any remaining events, and dispatches them.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        // Final drain — process any remaining observations regardless of throttle
        await DrainAndDispatchAsync(CancellationToken.None).ConfigureAwait(false);

        _backgroundTask = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Measure CPU usage for throttle
                double cpuUsage = _throttle.MeasureCpuUsage();
                _throttle.UpdateCpuUsage(cpuUsage);

                if (_throttle.IsThrottled)
                {
                    // Throttled — count how many we'd drain and record as dropped
                    int approximateCount = _buffer.Count;
                    if (approximateCount > 0)
                    {
                        _throttle.RecordDrop(approximateCount);
                        ThrottledBatches++;
                    }
                }
                else
                {
                    await DrainAndDispatchAsync(ct).ConfigureAwait(false);
                }

                await Task.Delay(_drainInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task DrainAndDispatchAsync(CancellationToken ct)
    {
        _batchBuffer.Clear();
        int drained = _buffer.DrainTo(_batchBuffer, MaxDrainBatchSize);

        if (drained == 0) return;

        DispatchedBatches++;

        // Fan-out to all advisors — each advisor handles exceptions independently
        for (int i = 0; i < _advisors.Count; i++)
        {
            IAiAdvisor advisor = _advisors[i];
            try
            {
                await advisor.ProcessObservationsAsync(_batchBuffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate cancellation
            }
            catch (Exception)
            {
                // One failing advisor does not block others.
                // Production telemetry would log this; for now, swallow and continue.
            }
        }
    }
}
