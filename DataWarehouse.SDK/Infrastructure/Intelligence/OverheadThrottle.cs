using System;
using System.Diagnostics;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Monitors CPU overhead from the AI observation pipeline and auto-throttles
/// when the configured maximum is exceeded. Uses a sliding window of CPU samples
/// with hysteresis to avoid flapping between throttled and unthrottled states.
/// </summary>
/// <remarks>
/// AIPI-10: CPU overhead from AI observation stays at or below maxCpuOverhead (default 1%).
/// The throttle uses process-level CPU measurement (TotalProcessorTime delta / wall-clock delta)
/// normalized by processor count. Hysteresis: throttle activates when average exceeds threshold,
/// deactivates when average drops below 80% of threshold.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-10)")]
public sealed class OverheadThrottle
{
    private readonly double _maxCpuOverheadPercent;
    private readonly double _hysteresisLowerBound;
    private readonly double[] _cpuSamples;
    private int _sampleIndex;
    private int _sampleCount;
    private volatile bool _isThrottled;
    private long _droppedObservations;

    // CPU measurement state
    private TimeSpan _lastProcessorTime;
    private long _lastWallClockTicks;
    private readonly int _processorCount;

    /// <summary>
    /// Number of CPU samples in the sliding window.
    /// </summary>
    private const int SlidingWindowSize = 10;

    /// <summary>
    /// Creates an overhead throttle with the specified CPU overhead limit.
    /// </summary>
    /// <param name="maxCpuOverheadPercent">
    /// Maximum allowed CPU overhead as a percentage (0-100). Default 1.0 (1%).
    /// When the sliding window average exceeds this, observations are dropped.
    /// </param>
    public OverheadThrottle(double maxCpuOverheadPercent = 1.0)
    {
        _maxCpuOverheadPercent = maxCpuOverheadPercent > 0 ? maxCpuOverheadPercent : 1.0;
        _hysteresisLowerBound = _maxCpuOverheadPercent * 0.8;
        _cpuSamples = new double[SlidingWindowSize];
        _sampleIndex = 0;
        _sampleCount = 0;
        _isThrottled = false;
        _droppedObservations = 0;
        _processorCount = Environment.ProcessorCount;

        // Initialize CPU measurement baseline
        using var process = Process.GetCurrentProcess();
        _lastProcessorTime = process.TotalProcessorTime;
        _lastWallClockTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Whether the pipeline is currently throttled. When true, observation batches
    /// should be dropped to reduce CPU overhead.
    /// </summary>
    public bool IsThrottled => _isThrottled;

    /// <summary>
    /// Total number of observations dropped due to throttling.
    /// </summary>
    public long DroppedObservations => Interlocked.Read(ref _droppedObservations);

    /// <summary>
    /// The configured maximum CPU overhead percentage.
    /// </summary>
    public double MaxCpuOverheadPercent => _maxCpuOverheadPercent;

    /// <summary>
    /// Updates the CPU usage tracking with a new sample and evaluates the throttle state.
    /// Called by the pipeline's background loop on each drain cycle.
    /// </summary>
    /// <param name="currentCpuPercent">Current CPU usage percentage (0-100).</param>
    public void UpdateCpuUsage(double currentCpuPercent)
    {
        // Record sample in sliding window
        _cpuSamples[_sampleIndex] = currentCpuPercent;
        _sampleIndex = (_sampleIndex + 1) % SlidingWindowSize;
        if (_sampleCount < SlidingWindowSize) _sampleCount++;

        // Calculate sliding window average
        double sum = 0;
        for (int i = 0; i < _sampleCount; i++)
        {
            sum += _cpuSamples[i];
        }
        double average = sum / _sampleCount;

        // Hysteresis: activate when above threshold, deactivate when below 80% of threshold
        if (!_isThrottled && average > _maxCpuOverheadPercent)
        {
            _isThrottled = true;
        }
        else if (_isThrottled && average < _hysteresisLowerBound)
        {
            _isThrottled = false;
        }
    }

    /// <summary>
    /// Increments the count of observations dropped due to throttling.
    /// </summary>
    /// <param name="count">Number of observations dropped.</param>
    public void RecordDrop(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _droppedObservations, count);
        }
    }

    /// <summary>
    /// Measures the current process CPU usage as a percentage (0-100).
    /// Uses the delta of TotalProcessorTime over wall-clock time, normalized by processor count.
    /// Called by the pipeline before each drain cycle.
    /// </summary>
    /// <returns>CPU usage percentage (0-100) since the last measurement.</returns>
    public double MeasureCpuUsage()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            TimeSpan currentProcessorTime = process.TotalProcessorTime;
            long currentWallClockTicks = Stopwatch.GetTimestamp();

            TimeSpan cpuDelta = currentProcessorTime - _lastProcessorTime;
            double wallClockSeconds = (double)(currentWallClockTicks - _lastWallClockTicks)
                / Stopwatch.Frequency;

            _lastProcessorTime = currentProcessorTime;
            _lastWallClockTicks = currentWallClockTicks;

            if (wallClockSeconds <= 0) return 0;

            // CPU percent = (cpuTime / wallTime / processorCount) * 100
            double cpuPercent = (cpuDelta.TotalSeconds / wallClockSeconds / _processorCount) * 100.0;

            // Clamp to valid range
            return Math.Max(0, Math.Min(100, cpuPercent));
        }
        catch (Exception)
        {
            // Process info may fail in restricted environments; return 0 (no throttle)
            return 0;
        }
    }
}
