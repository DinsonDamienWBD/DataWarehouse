using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Moonshots;

/// <summary>
/// Collects and aggregates metrics for all 10 moonshot features by subscribing to
/// pipeline completion events on the message bus. Maintains per-moonshot counters,
/// latency percentiles, and time-series trend data for dashboard consumption.
///
/// Thread-safe: uses Interlocked for counters, ConcurrentQueue for samples,
/// ConcurrentDictionary for all keyed collections.
/// </summary>
public sealed class MoonshotMetricsCollector : IDisposable
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<MoonshotMetricsCollector> _logger;
    private readonly BoundedDictionary<MoonshotId, MoonshotMetricState> _states = new BoundedDictionary<MoonshotId, MoonshotMetricState>(1000);
    private readonly BoundedDictionary<(MoonshotId, string), ConcurrentQueue<MoonshotTrendPoint>> _trendBuffers = new BoundedDictionary<(MoonshotId, string), ConcurrentQueue<MoonshotTrendPoint>>(1000);
    // P2-3256: Use ConcurrentBag to allow concurrent Add from subscription callbacks and
    // safe enumeration from Dispose, eliminating the need for a separate lock.
    private readonly System.Collections.Concurrent.ConcurrentBag<IDisposable> _subscriptions = new();
    private Timer? _trendTimer;
    private bool _disposed;

    /// <summary>Maximum latency samples per moonshot in the rolling window.</summary>
    private const int MaxLatencySamples = 1000;

    /// <summary>Maximum trend points per metric (24 hours at 1-minute resolution).</summary>
    private const int MaxTrendPoints = 1440;

    /// <summary>Trend snapshot interval in milliseconds (60 seconds).</summary>
    private const int TrendSnapshotIntervalMs = 60_000;

    /// <summary>Metrics window duration before reset (5 minutes).</summary>
    private static readonly TimeSpan MetricsWindowDuration = TimeSpan.FromMinutes(5);

    /// <summary>Supported metric names for trend tracking.</summary>
    private static readonly string[] SupportedMetricNames = { "latency_ms", "throughput_per_min", "success_rate" };

    /// <summary>
    /// Initializes a new instance of the <see cref="MoonshotMetricsCollector"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for subscribing to pipeline events.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public MoonshotMetricsCollector(IMessageBus messageBus, ILogger<MoonshotMetricsCollector> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Pre-initialize state for all 10 moonshots
        foreach (MoonshotId id in Enum.GetValues(typeof(MoonshotId)))
        {
            _states[id] = new MoonshotMetricState();
            foreach (var metricName in SupportedMetricNames)
            {
                _trendBuffers[(id, metricName)] = new ConcurrentQueue<MoonshotTrendPoint>();
            }
        }
    }

    /// <summary>
    /// Starts collecting metrics by subscribing to moonshot pipeline bus topics
    /// and initiating the background trend snapshot timer.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A completed task once subscriptions are established.</returns>
    public Task StartCollectingAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Subscribe to individual stage completions
        var stageSub = _messageBus.Subscribe("moonshot.pipeline.stage.completed", async msg =>
        {
            try
            {
                if (msg.Payload.TryGetValue("stage", out var stageObj) &&
                    msg.Payload.TryGetValue("success", out var successObj) &&
                    msg.Payload.TryGetValue("durationMs", out var durationObj))
                {
                    MoonshotId stageId;
                    if (stageObj is MoonshotId mid)
                    {
                        stageId = mid;
                    }
                    else if (stageObj is int intVal && Enum.IsDefined(typeof(MoonshotId), intVal))
                    {
                        stageId = (MoonshotId)intVal;
                    }
                    else if (stageObj is string strVal && Enum.TryParse<MoonshotId>(strVal, true, out var parsed))
                    {
                        stageId = parsed;
                    }
                    else
                    {
                        return;
                    }

                    var success = successObj is bool b ? b : successObj?.ToString() == "True";
                    var durationMs = durationObj switch
                    {
                        double d => d,
                        int i => (double)i,
                        long l => (double)l,
                        _ => double.TryParse(durationObj?.ToString(), out var p) ? p : 0.0
                    };

                    RecordStageCompletion(stageId, success, durationMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process moonshot.pipeline.stage.completed event");
            }
        });
        _subscriptions.Add(stageSub);

        // Subscribe to aggregate pipeline completions
        var pipelineSub = _messageBus.Subscribe("moonshot.pipeline.completed", async msg =>
        {
            try
            {
                if (msg.Payload.TryGetValue("stageResults", out var resultsObj) &&
                    resultsObj is IReadOnlyList<MoonshotStageResult> results)
                {
                    foreach (var result in results)
                    {
                        RecordStageCompletion(result.Stage, result.Success, result.Duration.TotalMilliseconds);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process moonshot.pipeline.completed event");
            }
        });
        _subscriptions.Add(pipelineSub);

        // Start background trend snapshot timer (every 60 seconds)
        _trendTimer = new Timer(
            _ => SnapshotTrends(),
            null,
            TrendSnapshotIntervalMs,
            TrendSnapshotIntervalMs);

        _logger.LogInformation(
            "MoonshotMetricsCollector started: subscribed to pipeline events, trend timer active at {IntervalSec}s intervals",
            TrendSnapshotIntervalMs / 1000);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets current metrics for a specific moonshot.
    /// </summary>
    /// <param name="id">The moonshot to query.</param>
    /// <returns>Aggregated metrics including counters and latency percentiles.</returns>
    public MoonshotMetrics GetMetrics(MoonshotId id)
    {
        var state = _states.GetOrAdd(id, _ => new MoonshotMetricState());
        return BuildMetrics(id, state);
    }

    /// <summary>
    /// Gets current metrics for all 10 moonshots.
    /// </summary>
    /// <returns>List of metrics for every registered moonshot.</returns>
    public IReadOnlyList<MoonshotMetrics> GetAllMetrics()
    {
        var result = new List<MoonshotMetrics>();
        foreach (MoonshotId id in Enum.GetValues(typeof(MoonshotId)))
        {
            var state = _states.GetOrAdd(id, _ => new MoonshotMetricState());
            result.Add(BuildMetrics(id, state));
        }
        return result;
    }

    /// <summary>
    /// Gets trend data for a specific moonshot metric within a time range.
    /// </summary>
    /// <param name="id">The moonshot to query.</param>
    /// <param name="metricName">Metric name: "latency_ms", "throughput_per_min", or "success_rate".</param>
    /// <param name="from">Start of time range (inclusive).</param>
    /// <param name="to">End of time range (inclusive).</param>
    /// <returns>Ordered list of trend points within the time range.</returns>
    public IReadOnlyList<MoonshotTrendPoint> GetTrends(MoonshotId id, string metricName, DateTimeOffset from, DateTimeOffset to)
    {
        if (!_trendBuffers.TryGetValue((id, metricName), out var buffer))
            return Array.Empty<MoonshotTrendPoint>();

        return buffer
            .Where(p => p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Records a stage completion with counters and latency sample.
    /// </summary>
    private void RecordStageCompletion(MoonshotId stageId, bool success, double durationMs)
    {
        var state = _states.GetOrAdd(stageId, _ => new MoonshotMetricState());

        // Reset window if expired — P2-3257: Interlocked.CompareExchange ensures only the
        // first thread to observe the expired window resets it; subsequent threads skip.
        var now = DateTimeOffset.UtcNow;
        var oldTicks = Interlocked.Read(ref state.WindowStartTicks);
        if (now.Ticks - oldTicks > MetricsWindowDuration.Ticks)
        {
            // Only reset if no other thread has already done so (first-writer wins).
            Interlocked.CompareExchange(ref state.WindowStartTicks, now.Ticks, oldTicks);
        }

        Interlocked.Increment(ref state.TotalInvocations);

        if (success)
            Interlocked.Increment(ref state.SuccessCount);
        else
            Interlocked.Increment(ref state.FailureCount);

        // Add latency sample with bounded queue
        state.LatencySamples.Enqueue(durationMs);
        while (state.LatencySamples.Count > MaxLatencySamples)
        {
            state.LatencySamples.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Builds a MoonshotMetrics record from the current state.
    /// </summary>
    private static MoonshotMetrics BuildMetrics(MoonshotId id, MoonshotMetricState state)
    {
        var samples = state.LatencySamples.ToArray();
        var avgLatency = samples.Length > 0 ? samples.Average() : 0.0;
        var p99Latency = ComputePercentile(samples, 0.99);

        return new MoonshotMetrics(
            Id: id,
            TotalInvocations: Interlocked.Read(ref state.TotalInvocations),
            SuccessCount: Interlocked.Read(ref state.SuccessCount),
            FailureCount: Interlocked.Read(ref state.FailureCount),
            AverageLatencyMs: Math.Round(avgLatency, 2),
            P99LatencyMs: Math.Round(p99Latency, 2),
            WindowStart: state.WindowStart,
            WindowEnd: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Computes a percentile value from an array of samples.
    /// </summary>
    private static double ComputePercentile(double[] samples, double percentile)
    {
        if (samples.Length == 0) return 0.0;

        var sorted = samples.OrderBy(s => s).ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Length - 1))];
    }

    /// <summary>
    /// Snapshots current metrics into trend buffers (called every 60 seconds).
    /// </summary>
    private void SnapshotTrends()
    {
        if (_disposed) return;

        var now = DateTimeOffset.UtcNow;

        try
        {
            foreach (MoonshotId id in Enum.GetValues(typeof(MoonshotId)))
            {
                var state = _states.GetOrAdd(id, _ => new MoonshotMetricState());
                var totalInvocations = Interlocked.Read(ref state.TotalInvocations);
                var successCount = Interlocked.Read(ref state.SuccessCount);

                // Latency trend
                var samples = state.LatencySamples.ToArray();
                var avgLatency = samples.Length > 0 ? samples.Average() : 0.0;
                AppendTrendPoint(id, "latency_ms", new MoonshotTrendPoint(now, Math.Round(avgLatency, 2), "latency_ms"));

                // Throughput trend (invocations per minute window)
                AppendTrendPoint(id, "throughput_per_min", new MoonshotTrendPoint(now, totalInvocations, "throughput_per_min"));

                // Success rate trend
                var successRate = totalInvocations > 0 ? (double)successCount / totalInvocations * 100.0 : 100.0;
                AppendTrendPoint(id, "success_rate", new MoonshotTrendPoint(now, Math.Round(successRate, 2), "success_rate"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to snapshot trend data");
        }
    }

    /// <summary>
    /// Appends a trend point to the bounded buffer.
    /// </summary>
    private void AppendTrendPoint(MoonshotId id, string metricName, MoonshotTrendPoint point)
    {
        var buffer = _trendBuffers.GetOrAdd((id, metricName), _ => new ConcurrentQueue<MoonshotTrendPoint>());
        buffer.Enqueue(point);

        // Enforce maximum buffer size (MEM-03 bounded)
        while (buffer.Count > MaxTrendPoints)
        {
            buffer.TryDequeue(out _);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _trendTimer?.Dispose();

        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
        _states.Clear();
        _trendBuffers.Clear();
    }

    /// <summary>
    /// Internal mutable state for a single moonshot's metrics.
    /// Thread-safe through Interlocked operations and ConcurrentQueue.
    /// </summary>
    private sealed class MoonshotMetricState
    {
        public long TotalInvocations;
        public long SuccessCount;
        public long FailureCount;
        public readonly ConcurrentQueue<double> LatencySamples = new();
        // P2-3257: Store WindowStart as ticks (long) for Interlocked.CompareExchange-based
        // atomic reset, preventing two concurrent callers from both resetting the window
        // and losing counter data.
        public long WindowStartTicks = DateTimeOffset.UtcNow.Ticks;
        public DateTimeOffset WindowStart
        {
            get => new DateTimeOffset(Interlocked.Read(ref WindowStartTicks), TimeSpan.Zero);
        }
    }
}
