using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies.AdaptiveTransport;

/// <summary>
/// Configuration options for the bandwidth-aware sync monitor.
/// </summary>
/// <param name="ProbeIntervalMs">Interval in milliseconds between bandwidth probes. Default is 5000ms (5 seconds).</param>
/// <param name="ProbeEndpoint">Target endpoint for bandwidth measurements in format "host:port".</param>
/// <param name="EnableAutoAdjust">Whether to automatically adjust sync parameters based on bandwidth. Default is true.</param>
/// <param name="HysteresisThreshold">Confidence threshold for link classification changes (0.0-1.0). Default is 0.7.</param>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor (Phase 65.5-12 consolidation).
/// </remarks>
public sealed record BandwidthMonitorOptions(
    int ProbeIntervalMs = 5000,
    string ProbeEndpoint = "8.8.8.8:80",
    bool EnableAutoAdjust = true,
    double HysteresisThreshold = 0.7);

/// <summary>
/// Central monitor that coordinates bandwidth probing, link classification, and sync parameter adjustment.
/// Monitors network conditions in the background and provides adaptive sync configuration.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor (Phase 65.5-12 consolidation).
/// </remarks>
public sealed class BandwidthAwareSyncMonitor : IDisposable
{
    private readonly BandwidthMonitorOptions _options;
    private readonly BandwidthProbe _probe;
    private readonly LinkClassifier _classifier;
    private readonly SyncParameterAdjuster _adjuster;
    private readonly SyncPriorityQueue _syncQueue;
    private readonly Timer _probeTimer;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private bool _isRunning;
    private LinkClassification? _currentClassification;
    private SyncParameters? _currentParameters;

    /// <summary>
    /// Event raised when the link classification changes.
    /// </summary>
    public event EventHandler<LinkClassificationChangedEventArgs>? OnLinkClassChanged;

    /// <summary>
    /// Initializes a new instance of the BandwidthAwareSyncMonitor.
    /// </summary>
    /// <param name="options">Configuration options for the monitor.</param>
    public BandwidthAwareSyncMonitor(BandwidthMonitorOptions? options = null)
    {
        _options = options ?? new BandwidthMonitorOptions();
        _probe = new BandwidthProbe();
        _classifier = new LinkClassifier(_options.HysteresisThreshold);
        _adjuster = new SyncParameterAdjuster();
        _syncQueue = new SyncPriorityQueue();
        _probeTimer = new Timer(
            _ =>
            {
                _ = ProbeAndClassifyAsync().ContinueWith(
                    t => System.Diagnostics.Debug.WriteLine(
                        $"[BandwidthAwareSyncMonitor] Probe failed: {t.Exception?.GetBaseException().Message}"),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <summary>
    /// Starts the background monitoring loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            return Task.CompletedTask;

        _isRunning = true;
        _probeTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(_options.ProbeIntervalMs));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background monitoring loop.
    /// </summary>
    public Task StopAsync()
    {
        _isRunning = false;
        _probeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current synchronization parameters based on the latest link classification.
    /// </summary>
    public SyncParameters GetCurrentParameters()
    {
        if (_currentParameters != null)
            return _currentParameters;

        return _adjuster.GetParameters(
            new LinkClassification(
                LinkClass.Unknown,
                0.5,
                new BandwidthMeasurement(
                    DateTimeOffset.UtcNow,
                    0,
                    0,
                    100,
                    10,
                    0),
                DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Gets the current link classification.
    /// </summary>
    public LinkClassification? GetCurrentClassification() => _currentClassification;

    /// <summary>
    /// Enqueues a synchronization operation for priority-based processing.
    /// </summary>
    public void EnqueueSync(SyncOperation operation) => _syncQueue.Enqueue(operation);

    /// <summary>
    /// Retrieves and removes the next synchronization operation from the priority queue.
    /// </summary>
    public Task<SyncOperation?> ProcessNextSyncAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _syncQueue.TryDequeue(out var operation);
        return Task.FromResult(operation);
    }

    private async Task ProbeAndClassifyAsync()
    {
        if (!_isRunning)
            return;

        if (!await _probeLock.WaitAsync(0))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var measurement = await _probe.MeasureAsync(_options.ProbeEndpoint, cts.Token);

            var previousClass = _currentClassification?.Class;
            var classification = _classifier.Classify(measurement);
            _currentClassification = classification;

            if (_options.EnableAutoAdjust)
            {
                _currentParameters = _adjuster.GetParameters(classification);
            }

            if (previousClass != null && previousClass != classification.Class)
            {
                OnLinkClassChanged?.Invoke(this, new LinkClassificationChangedEventArgs(
                    previousClass.Value,
                    classification.Class,
                    classification));
            }
        }
        catch
        {

            // Probe failed - continue monitoring
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <summary>
    /// Disposes resources used by the monitor.
    /// </summary>
    public void Dispose()
    {
        _isRunning = false;
        _probeTimer.Dispose();
        _probeLock.Dispose();
    }
}

/// <summary>
/// Event arguments for link classification changes.
/// </summary>
public sealed class LinkClassificationChangedEventArgs : EventArgs
{
    public LinkClass PreviousClass { get; }
    public LinkClass NewClass { get; }
    public LinkClassification Classification { get; }

    public LinkClassificationChangedEventArgs(
        LinkClass previousClass,
        LinkClass newClass,
        LinkClassification classification)
    {
        PreviousClass = previousClass;
        NewClass = newClass;
        Classification = classification;
    }
}

/// <summary>
/// Probes network bandwidth using TCP window analysis and active throughput measurement.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor (Phase 65.5-12 consolidation).
/// </remarks>
public sealed class BandwidthProbe
{
    private readonly ConcurrentQueue<BandwidthMeasurement> _measurements = new();
    private readonly int _maxHistorySize = 60;

    /// <summary>
    /// Measures bandwidth to the specified endpoint using TCP-based probing.
    /// </summary>
    public async Task<BandwidthMeasurement> MeasureAsync(string endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));

        var parts = endpoint.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 80;

        BandwidthMeasurement measurement;

        try
        {
            var tcpResult = await MeasureTcpBandwidthAsync(host, port, ct);
            measurement = new BandwidthMeasurement(
                DateTimeOffset.UtcNow,
                tcpResult.DownloadBps,
                tcpResult.UploadBps,
                tcpResult.LatencyMs,
                tcpResult.JitterMs,
                tcpResult.PacketLossPercent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            measurement = new BandwidthMeasurement(
                DateTimeOffset.UtcNow,
                DownloadBytesPerSecond: 0,
                UploadBytesPerSecond: 0,
                LatencyMs: double.MaxValue,
                JitterMs: 0,
                PacketLossPercent: 100);
        }

        AddMeasurement(measurement);
        return measurement;
    }

    /// <summary>
    /// Gets the average bandwidth measurement over the specified time window.
    /// </summary>
    public BandwidthMeasurement? GetAverageMeasurement(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var recentMeasurements = _measurements
            .Where(m => m.Timestamp >= cutoff)
            .ToList();

        if (recentMeasurements.Count == 0)
            return null;

        return new BandwidthMeasurement(
            DateTimeOffset.UtcNow,
            (long)recentMeasurements.Average(m => m.DownloadBytesPerSecond),
            (long)recentMeasurements.Average(m => m.UploadBytesPerSecond),
            recentMeasurements.Average(m => m.LatencyMs),
            recentMeasurements.Average(m => m.JitterMs),
            recentMeasurements.Average(m => m.PacketLossPercent));
    }

    private void AddMeasurement(BandwidthMeasurement measurement)
    {
        _measurements.Enqueue(measurement);
        while (_measurements.Count > _maxHistorySize)
            _measurements.TryDequeue(out _);
    }

    private async Task<TcpMeasurementResult> MeasureTcpBandwidthAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(host, port, ct);
            sw.Stop();
            var rttMs = sw.Elapsed.TotalMilliseconds;

            var receiveBufferSize = client.ReceiveBufferSize;
            var sendBufferSize = client.SendBufferSize;

            var estimatedDownloadBps = rttMs > 0
                ? (long)(receiveBufferSize / (rttMs / 1000.0))
                : 0;

            var estimatedUploadBps = rttMs > 0
                ? (long)(sendBufferSize / (rttMs / 1000.0))
                : 0;

            var (actualDownloadBps, actualUploadBps, jitter) = await ActiveProbeAsync(client, ct);

            var downloadBps = Math.Max(estimatedDownloadBps, actualDownloadBps);
            var uploadBps = Math.Max(estimatedUploadBps, actualUploadBps);

            return new TcpMeasurementResult(downloadBps, uploadBps, rttMs, jitter, PacketLossPercent: 0);
        }
        catch (SocketException)
        {
            return new TcpMeasurementResult(0, 0, double.MaxValue, 0, 100);
        }
    }

    private async Task<(long DownloadBps, long UploadBps, double JitterMs)> ActiveProbeAsync(
        TcpClient client, CancellationToken ct)
    {
        const int probeSize = 8192;
        const int probeCount = 3;

        try
        {
            // NetworkStream is owned by the TcpClient and will be disposed when the client disposes,
            // but we explicitly wrap it in a using to ensure prompt disposal if an exception occurs.
            using var stream = client.GetStream();
            var sendData = new byte[probeSize];
            var receiveData = new byte[probeSize];
            var latencies = new List<double>();

            long totalSent = 0;
            long totalReceived = 0;
            var totalSw = Stopwatch.StartNew();

            for (var i = 0; i < probeCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sendSw = Stopwatch.StartNew();
                await stream.WriteAsync(sendData, ct);
                await stream.FlushAsync(ct);
                sendSw.Stop();
                totalSent += probeSize;

                var recvSw = Stopwatch.StartNew();
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                    var bytesRead = await stream.ReadAsync(receiveData.AsMemory(0, probeSize), timeoutCts.Token);
                    recvSw.Stop();
                    totalReceived += bytesRead;
                    latencies.Add(recvSw.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - no echo response
                }
            }

            totalSw.Stop();

            var elapsedSeconds = totalSw.Elapsed.TotalSeconds;
            var uploadBps = elapsedSeconds > 0 ? (long)(totalSent / elapsedSeconds) : 0;
            var downloadBps = elapsedSeconds > 0 ? (long)(totalReceived / elapsedSeconds) : 0;

            var jitter = CalculateJitter(latencies);

            return (downloadBps, uploadBps, jitter);
        }
        catch (Exception ex)
        {
            // Probe failed (network error, remote closed connection, etc.).
            // Return zero measurements so the caller can fall back to estimated values.
            System.Diagnostics.Debug.WriteLine(
                $"[AdaptiveTransportStrategy] ActiveProbeAsync failed: {ex.GetType().Name}: {ex.Message}");
            return (0, 0, 0);
        }
    }

    private double CalculateJitter(List<double> latencies)
    {
        if (latencies.Count < 2)
            return 0;

        var diffs = new List<double>();
        for (var i = 1; i < latencies.Count; i++)
            diffs.Add(Math.Abs(latencies[i] - latencies[i - 1]));

        return diffs.Any() ? diffs.Average() : 0;
    }

    private sealed record TcpMeasurementResult(
        long DownloadBps,
        long UploadBps,
        double LatencyMs,
        double JitterMs,
        double PacketLossPercent);
}

/// <summary>
/// Classifies network links based on bandwidth and latency characteristics.
/// Implements hysteresis to prevent classification flapping.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor (Phase 65.5-12 consolidation).
/// </remarks>
public sealed class LinkClassifier
{
    private LinkClassification? _currentClassification;
    private readonly double _hysteresisThreshold;

    public LinkClassification? CurrentClass => _currentClassification;

    public LinkClassifier(double hysteresisThreshold = 0.7)
    {
        if (hysteresisThreshold < 0.0 || hysteresisThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(hysteresisThreshold), "Must be between 0.0 and 1.0");

        _hysteresisThreshold = hysteresisThreshold;
    }

    public LinkClassification Classify(BandwidthMeasurement measurement)
    {
        var (linkClass, confidence) = DetermineClassification(measurement);

        if (_currentClassification != null &&
            _currentClassification.Class != linkClass &&
            confidence < _hysteresisThreshold)
        {
            return _currentClassification;
        }

        _currentClassification = new LinkClassification(
            linkClass,
            confidence,
            measurement,
            DateTimeOffset.UtcNow);

        return _currentClassification;
    }

    private (LinkClass Class, double Confidence) DetermineClassification(BandwidthMeasurement measurement)
    {
        var downloadMbps = measurement.DownloadBytesPerSecond * 8.0 / 1_000_000;
        var latencyMs = measurement.LatencyMs;
        var packetLoss = measurement.PacketLossPercent;

        if (packetLoss > 5.0)
        {
            var confidence = Math.Min(1.0, packetLoss / 20.0);
            return (LinkClass.Intermittent, confidence);
        }

        if (latencyMs > 500)
        {
            var confidence = Math.Min(1.0, (latencyMs - 500) / 500.0 + 0.8);
            return (LinkClass.Satellite, Math.Min(confidence, 1.0));
        }

        if (downloadMbps > 100 && latencyMs < 5)
        {
            var bandwidthConfidence = Math.Min(1.0, downloadMbps / 200.0);
            var latencyConfidence = Math.Min(1.0, (5 - latencyMs) / 5.0 + 0.5);
            var confidence = (bandwidthConfidence + latencyConfidence) / 2.0;
            return (LinkClass.Fiber, Math.Min(confidence, 1.0));
        }

        if (downloadMbps >= 50 && downloadMbps <= 500 && latencyMs >= 10 && latencyMs <= 30)
        {
            var bandwidthScore = 1.0 - Math.Abs(downloadMbps - 150) / 150.0;
            var latencyScore = 1.0 - Math.Abs(latencyMs - 20) / 20.0;
            var confidence = Math.Max(0.6, (bandwidthScore + latencyScore) / 2.0);
            return (LinkClass.Mobile5G, Math.Min(confidence, 1.0));
        }

        if (downloadMbps >= 10 && downloadMbps <= 100 && latencyMs < 50)
        {
            var bandwidthScore = Math.Min(1.0, downloadMbps / 50.0);
            var latencyScore = Math.Min(1.0, (50 - latencyMs) / 50.0);
            var confidence = Math.Max(0.6, (bandwidthScore + latencyScore) / 2.0);
            return (LinkClass.Broadband, Math.Min(confidence, 1.0));
        }

        if (downloadMbps >= 1 && downloadMbps <= 50 && latencyMs >= 30 && latencyMs <= 100)
        {
            var bandwidthScore = Math.Min(1.0, downloadMbps / 25.0);
            var latencyScore = 1.0 - Math.Abs(latencyMs - 65) / 65.0;
            var confidence = Math.Max(0.6, (bandwidthScore + latencyScore) / 2.0);
            return (LinkClass.Mobile4G, Math.Min(confidence, 1.0));
        }

        if (downloadMbps > 100)
            return (LinkClass.Fiber, 0.5);

        if (downloadMbps >= 50)
            return latencyMs < 30 ? (LinkClass.Mobile5G, 0.5) : (LinkClass.Broadband, 0.5);

        if (downloadMbps >= 10)
            return (LinkClass.Broadband, 0.5);

        if (downloadMbps >= 1)
            return (LinkClass.Mobile4G, 0.5);

        return (LinkClass.Unknown, 0.3);
    }
}

/// <summary>
/// Dynamically adjusts synchronization parameters based on classified link type.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor (Phase 65.5-12 consolidation).
/// </remarks>
public sealed class SyncParameterAdjuster
{
    public SyncParameters GetParameters(LinkClassification link)
    {
        return link.Class switch
        {
            LinkClass.Fiber => new SyncParameters(SyncMode.FullReplication, 16, 4 * 1024 * 1024,
                new RetryPolicy(2, 100, 1000, 2.0), false),
            LinkClass.Broadband => new SyncParameters(SyncMode.FullReplication, 8, 1 * 1024 * 1024,
                new RetryPolicy(3, 200, 2000, 2.0), true),
            LinkClass.Mobile5G => new SyncParameters(SyncMode.DeltaOnly, 4, 256 * 1024,
                new RetryPolicy(4, 300, 3000, 2.0), true),
            LinkClass.Mobile4G => new SyncParameters(SyncMode.DeltaOnly, 4, 256 * 1024,
                new RetryPolicy(5, 500, 5000, 2.0), true),
            LinkClass.Satellite => new SyncParameters(SyncMode.DeltaOnly, 2, 64 * 1024,
                new RetryPolicy(10, 1000, 10000, 1.5), true),
            LinkClass.Intermittent => new SyncParameters(SyncMode.StoreAndForward, 1, 16 * 1024,
                new RetryPolicy(15, 2000, 30000, 2.0), true),
            _ => new SyncParameters(SyncMode.DeltaOnly, 2, 128 * 1024,
                new RetryPolicy(5, 500, 5000, 2.0), true),
        };
    }
}

/// <summary>
/// Thread-safe priority queue for synchronization operations.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor (Phase 65.5-12 consolidation).
/// </remarks>
public sealed class SyncPriorityQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<SyncPriority, Queue<SyncOperation>> _queues = new()
    {
        [SyncPriority.Critical] = new Queue<SyncOperation>(),
        [SyncPriority.High] = new Queue<SyncOperation>(),
        [SyncPriority.Normal] = new Queue<SyncOperation>(),
        [SyncPriority.Low] = new Queue<SyncOperation>(),
        [SyncPriority.Background] = new Queue<SyncOperation>()
    };

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queues.Values.Sum(q => q.Count);
            }
        }
    }

    public void Enqueue(SyncOperation operation)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        lock (_lock)
        {
            _queues[operation.Priority].Enqueue(operation);
        }
    }

    public bool TryDequeue(out SyncOperation? operation)
    {
        lock (_lock)
        {
            foreach (var priority in GetPriorityOrder())
            {
                if (_queues[priority].Count > 0)
                {
                    operation = _queues[priority].Dequeue();
                    return true;
                }
            }

            operation = null;
            return false;
        }
    }

    public SyncOperation? Peek()
    {
        lock (_lock)
        {
            foreach (var priority in GetPriorityOrder())
            {
                if (_queues[priority].Count > 0)
                    return _queues[priority].Peek();
            }

            return null;
        }
    }

    public Dictionary<SyncPriority, int> CountByPriority()
    {
        lock (_lock)
        {
            return _queues.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Count);
        }
    }

    private static IEnumerable<SyncPriority> GetPriorityOrder()
    {
        yield return SyncPriority.Critical;
        yield return SyncPriority.High;
        yield return SyncPriority.Normal;
        yield return SyncPriority.Low;
        yield return SyncPriority.Background;
    }
}
