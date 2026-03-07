// Licensed to the DataWarehouse project. All rights reserved.
// GC event counter monitoring via System.Diagnostics.Tracing.EventListener.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace DataWarehouse.Hardening.Tests.Soak;

/// <summary>
/// A single GC metric sample captured at a point in time.
/// </summary>
public sealed record GcSample(
    DateTime Timestamp,
    long Gen0Count,
    long Gen1Count,
    long Gen2Count,
    long HeapSizeBytes,
    long WorkingSetBytes,
    long AllocRateBytes);

/// <summary>
/// Monitors GC event counters via <see cref="EventListener"/> and collects periodic samples
/// of Gen0/1/2 counts, heap size, working set, and allocation rate.
/// Provides analysis methods for Gen2 rate, working set growth, and monotonic growth detection.
/// </summary>
public sealed class GcEventMonitor : EventListener, IDisposable
{
    private readonly ConcurrentQueue<GcSample> _samples = new();
    private readonly TimeSpan _sampleInterval;
    private readonly Timer _sampleTimer;
    private volatile bool _disposed;

    // Latest values from event counters (updated by event callbacks)
    private long _lastGen0Count;
    private long _lastGen1Count;
    private long _lastGen2Count;
    private long _lastHeapSizeBytes;
    private long _lastAllocRateBytes;

    /// <summary>
    /// Creates a new GC event monitor that samples at the specified interval.
    /// </summary>
    /// <param name="sampleInterval">How often to capture a GC sample.</param>
    public GcEventMonitor(TimeSpan sampleInterval)
    {
        _sampleInterval = sampleInterval;

        // Timer to periodically capture a snapshot combining event counter data with working set
        _sampleTimer = new Timer(
            _ => CaptureSnapshot(),
            null,
            TimeSpan.Zero,
            _sampleInterval);
    }

    /// <summary>
    /// Called when an event source is created. We subscribe to "System.Runtime" for GC counters.
    /// </summary>
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Runtime")
        {
            // Request counter updates at 1-second interval for responsive tracking
            var args = new Dictionary<string, string?>
            {
                ["EventCounterIntervalSec"] = "1"
            };
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All, args);
        }

        base.OnEventSourceCreated(eventSource);
    }

    /// <summary>
    /// Called when an event is written. Extracts GC counter values from event payload.
    /// </summary>
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_disposed || eventData.EventName != "EventCounters" || eventData.Payload == null)
            return;

        foreach (var payload in eventData.Payload)
        {
            if (payload is not IDictionary<string, object> data)
                continue;

            if (!data.TryGetValue("Name", out var nameObj) || nameObj is not string name)
                continue;

            // Extract the counter value (Mean for rate counters, Increment for cumulative)
            double value = 0;
            if (data.TryGetValue("Mean", out var meanObj) && meanObj is double mean)
                value = mean;
            else if (data.TryGetValue("Increment", out var incObj) && incObj is double inc)
                value = inc;

            switch (name)
            {
                case "gen-0-gc-count":
                    Interlocked.Exchange(ref _lastGen0Count, (long)value);
                    break;
                case "gen-1-gc-count":
                    Interlocked.Exchange(ref _lastGen1Count, (long)value);
                    break;
                case "gen-2-gc-count":
                    Interlocked.Exchange(ref _lastGen2Count, (long)value);
                    break;
                case "gc-heap-size":
                    // Reported in MB, convert to bytes
                    Interlocked.Exchange(ref _lastHeapSizeBytes, (long)(value * 1_048_576));
                    break;
                case "alloc-rate":
                    Interlocked.Exchange(ref _lastAllocRateBytes, (long)value);
                    break;
            }
        }
    }

    /// <summary>
    /// Captures a snapshot of current GC metrics and working set into the sample queue.
    /// </summary>
    private void CaptureSnapshot()
    {
        if (_disposed) return;

        try
        {
            var workingSet = Process.GetCurrentProcess().WorkingSet64;

            var sample = new GcSample(
                Timestamp: DateTime.UtcNow,
                Gen0Count: Interlocked.Read(ref _lastGen0Count),
                Gen1Count: Interlocked.Read(ref _lastGen1Count),
                Gen2Count: Interlocked.Read(ref _lastGen2Count),
                HeapSizeBytes: Interlocked.Read(ref _lastHeapSizeBytes),
                WorkingSetBytes: workingSet,
                AllocRateBytes: Interlocked.Read(ref _lastAllocRateBytes));

            _samples.Enqueue(sample);
        }
        catch (InvalidOperationException)
        {
            // Process may have exited during shutdown
        }
    }

    /// <summary>
    /// Returns all collected GC samples in chronological order.
    /// </summary>
    public IReadOnlyList<GcSample> GetSamples() => _samples.ToArray();

    /// <summary>
    /// Calculates the Gen2 collection rate per minute over the entire monitoring duration.
    /// Uses the difference between the last and first Gen2 counts divided by elapsed minutes.
    /// </summary>
    public double GetGen2RatePerMinute()
    {
        var samples = _samples.ToArray();
        if (samples.Length < 2) return 0;

        var first = samples[0];
        var last = samples[^1];
        var elapsedMinutes = (last.Timestamp - first.Timestamp).TotalMinutes;

        if (elapsedMinutes <= 0) return 0;

        var gen2Delta = last.Gen2Count - first.Gen2Count;
        return gen2Delta / elapsedMinutes;
    }

    /// <summary>
    /// Determines whether working set is monotonically increasing using simple linear regression.
    /// Returns true if the slope is significantly positive (i.e., working set is consistently growing).
    /// </summary>
    public bool IsWorkingSetMonotonic()
    {
        var samples = _samples.ToArray();
        if (samples.Length < 10) return false; // Need sufficient data points

        // Simple linear regression on working set over time
        var startTime = samples[0].Timestamp;
        double n = samples.Length;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;

        foreach (var s in samples)
        {
            double x = (s.Timestamp - startTime).TotalSeconds;
            double y = s.WorkingSetBytes;
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        double denominator = n * sumXx - sumX * sumX;
        if (Math.Abs(denominator) < double.Epsilon) return false;

        double slope = (n * sumXy - sumX * sumY) / denominator;

        // Consider monotonic if slope indicates > 1 MB/minute growth
        double slopePerMinute = slope * 60;
        return slopePerMinute > 1_048_576; // > 1 MB/min growth
    }

    /// <summary>
    /// Calculates working set growth as a percentage: (final - initial) / initial * 100.
    /// Returns 0 if insufficient samples.
    /// </summary>
    public double GetWorkingSetGrowthPercent()
    {
        var samples = _samples.ToArray();
        if (samples.Length < 2) return 0;

        var initial = samples[0].WorkingSetBytes;
        var final_ = samples[^1].WorkingSetBytes;

        if (initial <= 0) return 0;

        return (double)(final_ - initial) / initial * 100.0;
    }

    /// <summary>
    /// Forces a final snapshot capture for end-of-test analysis.
    /// </summary>
    public void CaptureFinalsnapshot()
    {
        CaptureSnapshot();
    }

    /// <summary>
    /// Disposes the monitor, stopping the sample timer and disabling event listening.
    /// </summary>
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sampleTimer.Dispose();
        base.Dispose();
    }
}
