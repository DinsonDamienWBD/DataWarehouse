namespace DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

/// <summary>
/// Configuration options for the bandwidth-aware sync monitor.
/// </summary>
/// <param name="ProbeIntervalMs">Interval in milliseconds between bandwidth probes. Default is 5000ms (5 seconds).</param>
/// <param name="ProbeEndpoint">Target endpoint for bandwidth measurements in format "host:port".</param>
/// <param name="EnableAutoAdjust">Whether to automatically adjust sync parameters based on bandwidth. Default is true.</param>
/// <param name="HysteresisThreshold">Confidence threshold for link classification changes (0.0-1.0). Default is 0.7.</param>
public sealed record BandwidthMonitorOptions(
    int ProbeIntervalMs = 5000,
    string ProbeEndpoint = "8.8.8.8:80",
    bool EnableAutoAdjust = true,
    double HysteresisThreshold = 0.7);

/// <summary>
/// Central monitor that coordinates bandwidth probing, link classification, and sync parameter adjustment.
/// Monitors network conditions in the background and provides adaptive sync configuration.
/// </summary>
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
            async _ => await ProbeAndClassifyAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <summary>
    /// Starts the background monitoring loop.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the start operation.</returns>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            return Task.CompletedTask;

        _isRunning = true;

        // Start periodic probing
        _probeTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(_options.ProbeIntervalMs));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background monitoring loop.
    /// </summary>
    /// <returns>Task representing the stop operation.</returns>
    public Task StopAsync()
    {
        _isRunning = false;
        _probeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current synchronization parameters based on the latest link classification.
    /// </summary>
    /// <returns>Current sync parameters, or safe defaults if not yet classified.</returns>
    public SyncParameters GetCurrentParameters()
    {
        if (_currentParameters != null)
            return _currentParameters;

        // Return safe defaults until first classification
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
    /// <returns>Current link classification, or null if not yet classified.</returns>
    public LinkClassification? GetCurrentClassification()
    {
        return _currentClassification;
    }

    /// <summary>
    /// Enqueues a synchronization operation for priority-based processing.
    /// </summary>
    /// <param name="operation">The sync operation to enqueue.</param>
    public void EnqueueSync(SyncOperation operation)
    {
        _syncQueue.Enqueue(operation);
    }

    /// <summary>
    /// Retrieves and removes the next synchronization operation from the priority queue.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next sync operation, or null if the queue is empty.</returns>
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

        // Only one probe at a time
        if (!await _probeLock.WaitAsync(0))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Measure bandwidth
            var measurement = await _probe.MeasureAsync(_options.ProbeEndpoint, cts.Token);

            // Classify the link
            var previousClass = _currentClassification?.Class;
            var classification = _classifier.Classify(measurement);
            _currentClassification = classification;

            // Adjust parameters if auto-adjust is enabled
            if (_options.EnableAutoAdjust)
            {
                _currentParameters = _adjuster.GetParameters(classification);
            }

            // Fire event if classification changed
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
    /// <summary>
    /// Gets the previous link class.
    /// </summary>
    public LinkClass PreviousClass { get; }

    /// <summary>
    /// Gets the new link class.
    /// </summary>
    public LinkClass NewClass { get; }

    /// <summary>
    /// Gets the full classification details.
    /// </summary>
    public LinkClassification Classification { get; }

    /// <summary>
    /// Initializes a new instance of the LinkClassificationChangedEventArgs.
    /// </summary>
    /// <param name="previousClass">The previous link class.</param>
    /// <param name="newClass">The new link class.</param>
    /// <param name="classification">The full classification details.</param>
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
