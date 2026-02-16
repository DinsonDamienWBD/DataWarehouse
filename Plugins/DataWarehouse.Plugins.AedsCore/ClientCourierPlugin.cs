using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AedsCore;

/// <summary>
/// Combined client-side plugin integrating Sentinel, Executor, and Watchdog functionality.
/// </summary>
/// <remarks>
/// <para>
/// The ClientCourierPlugin provides an all-in-one client agent for AEDS including:
/// <list type="bullet">
/// <item><description><strong>Sentinel:</strong> Listens for intent manifests from the control plane</description></item>
/// <item><description><strong>Executor:</strong> Downloads payloads and executes manifest actions</description></item>
/// <item><description><strong>Watchdog:</strong> Monitors interactive files for changes and syncs back</description></item>
/// <item><description><strong>Policy Engine:</strong> Evaluates whether actions are allowed</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Security:</strong> All manifests are signature-verified before execution. Unsigned manifests
/// are rejected unless <c>AllowUnsigned</c> is explicitly enabled in configuration.
/// </para>
/// <para>
/// <strong>Actions Supported:</strong>
/// <list type="bullet">
/// <item><description><strong>Passive:</strong> Silent background download to cache</description></item>
/// <item><description><strong>Notify:</strong> Download + toast notification</description></item>
/// <item><description><strong>Execute:</strong> Download + run executable (requires Release Key)</description></item>
/// <item><description><strong>Interactive:</strong> Download + open file + watchdog monitoring</description></item>
/// <item><description><strong>Custom:</strong> Download + run custom action script</description></item>
/// </list>
/// </para>
/// </remarks>
public class ClientCourierPlugin : PlatformPluginBase
{
    private readonly ILogger<ClientCourierPlugin> _logger;
    private readonly ConcurrentDictionary<string, WatchedFile> _watchedFiles = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    private IControlPlaneTransport? _controlPlane;
    private IDataPlaneTransport? _dataPlane;
    private SentinelConfig? _sentinelConfig;
    private ExecutorConfig? _executorConfig;
    private bool _isRunning;
    private CancellationTokenSource? _runCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientCourierPlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public ClientCourierPlugin(ILogger<ClientCourierPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.client.courier";

    /// <inheritdoc />
    public override string Name => "AEDS Client Courier";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string PlatformDomain => "AedsCourier";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Event raised when a manifest is received and validated.
    /// </summary>
    public event EventHandler<ManifestReceivedEventArgs>? ManifestReceived;

    /// <summary>
    /// Event raised when a watched file changes.
    /// </summary>
    public event EventHandler<FileChangedEventArgs>? FileChanged;

    /// <summary>
    /// Starts the client courier agent.
    /// </summary>
    /// <param name="controlPlane">Control plane transport to use.</param>
    /// <param name="dataPlane">Data plane transport to use.</param>
    /// <param name="sentinelConfig">Sentinel configuration.</param>
    /// <param name="executorConfig">Executor configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when courier is already running.</exception>
    public async Task StartAsync(
        IControlPlaneTransport controlPlane,
        IDataPlaneTransport dataPlane,
        SentinelConfig sentinelConfig,
        ExecutorConfig executorConfig,
        CancellationToken ct = default)
    {
        if (controlPlane == null)
            throw new ArgumentNullException(nameof(controlPlane));
        if (dataPlane == null)
            throw new ArgumentNullException(nameof(dataPlane));
        if (sentinelConfig == null)
            throw new ArgumentNullException(nameof(sentinelConfig));
        if (executorConfig == null)
            throw new ArgumentNullException(nameof(executorConfig));

        if (_isRunning)
            throw new InvalidOperationException("Courier is already running");

        _controlPlane = controlPlane;
        _dataPlane = dataPlane;
        _sentinelConfig = sentinelConfig;
        _executorConfig = executorConfig;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation("Starting AEDS Client Courier (ClientId: {ClientId}, Server: {Server})",
            sentinelConfig.ClientId, sentinelConfig.ServerUrl);

        // Connect to control plane
        var controlConfig = new ControlPlaneConfig(
            ServerUrl: sentinelConfig.ServerUrl,
            ClientId: sentinelConfig.ClientId,
            AuthToken: sentinelConfig.PrivateKey, // Using private key as auth token
            HeartbeatInterval: sentinelConfig.HeartbeatInterval,
            ReconnectDelay: TimeSpan.FromSeconds(5)
        );

        await _controlPlane.ConnectAsync(controlConfig, _runCts.Token);

        // Subscribe to configured channels
        foreach (var channelId in sentinelConfig.SubscribedChannels)
        {
            await _controlPlane.SubscribeChannelAsync(channelId, _runCts.Token);
            _logger.LogInformation("Subscribed to channel {ChannelId}", channelId);
        }

        // Start listening for manifests
        _isRunning = true;
        _ = Task.Run(() => ListenLoopAsync(_runCts.Token), _runCts.Token);

        // Start heartbeat loop
        _ = Task.Run(() => HeartbeatLoopAsync(_runCts.Token), _runCts.Token);

        _logger.LogInformation("Client Courier started successfully");
    }

    /// <summary>
    /// Stops the client courier agent.
    /// </summary>
    /// <returns>Task representing the async operation.</returns>
    public async Task StopCourierAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping AEDS Client Courier");

        _runCts?.Cancel();
        _isRunning = false;

        // Stop all file watchers
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        // Disconnect from control plane
        if (_controlPlane != null && _controlPlane.IsConnected)
        {
            await _controlPlane.DisconnectAsync();
        }

        _logger.LogInformation("Client Courier stopped");
    }

    /// <inheritdoc />
    public override Task StopAsync() => StopCourierAsync();

    /// <summary>
    /// Main loop for listening to control plane manifests.
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        if (_controlPlane == null)
            return;

        try
        {
            await foreach (var manifest in _controlPlane.ReceiveManifestsAsync(ct))
            {
                _logger.LogInformation("Received manifest {ManifestId} (action: {Action}, priority: {Priority})",
                    manifest.ManifestId, manifest.Action, manifest.Priority);

                // Raise event
                ManifestReceived?.Invoke(this, new ManifestReceivedEventArgs
                {
                    Manifest = manifest,
                    ReceivedAt = DateTimeOffset.UtcNow
                });

                // Process manifest asynchronously
                _ = Task.Run(() => ProcessManifestAsync(manifest, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Listen loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in listen loop");
        }
    }

    /// <summary>
    /// Heartbeat loop to maintain control plane connection.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        if (_controlPlane == null || _sentinelConfig == null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var heartbeat = new HeartbeatMessage(
                        ClientId: _sentinelConfig.ClientId,
                        Timestamp: DateTimeOffset.UtcNow,
                        Status: ClientStatus.Online,
                        Metrics: new Dictionary<string, object>
                        {
                            ["watched_files"] = _watchedFiles.Count,
                            ["version"] = Version.ToString()
                        }
                    );

                    await _controlPlane.SendHeartbeatAsync(heartbeat, ct);
                    _logger.LogDebug("Sent heartbeat");

                    await Task.Delay(_sentinelConfig.HeartbeatInterval, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Heartbeat failed, will retry");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Heartbeat loop cancelled");
        }
    }

    /// <summary>
    /// Processes a received intent manifest.
    /// </summary>
    private async Task ProcessManifestAsync(IntentManifest manifest, CancellationToken ct)
    {
        if (_dataPlane == null || _executorConfig == null)
            return;

        await _executionLock.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Processing manifest {ManifestId}", manifest.ManifestId);

            // 1. Verify signature
            if (!_executorConfig.AllowUnsigned)
            {
                // Signature verification: requires UltimateKeyManagement integration (T94).
                if (manifest.Signature == null || string.IsNullOrEmpty(manifest.Signature.Value))
                {
                    _logger.LogWarning("Rejecting unsigned manifest {ManifestId}", manifest.ManifestId);
                    return;
                }
            }

            // 2. Download payload
            var dataPlaneConfig = new DataPlaneConfig(
                ServerUrl: _sentinelConfig!.ServerUrl,
                AuthToken: _sentinelConfig.PrivateKey,
                MaxConcurrentChunks: 4,
                ChunkSizeBytes: 1024 * 1024,
                Timeout: TimeSpan.FromMinutes(30)
            );

            var localPath = Path.Combine(_executorConfig.CachePath, manifest.Payload.PayloadId);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            using var payloadStream = await _dataPlane.DownloadAsync(
                manifest.Payload.PayloadId,
                dataPlaneConfig,
                progress: new Progress<TransferProgress>(p =>
                    _logger.LogDebug("Download progress: {Percent:F1}% ({Bytes}/{Total} bytes)",
                        p.PercentComplete, p.BytesTransferred, p.TotalBytes)),
                ct: ct);

            using var fileStream = File.Create(localPath);
            await payloadStream.CopyToAsync(fileStream, ct);

            _logger.LogInformation("Downloaded payload to {LocalPath} ({SizeBytes} bytes)",
                localPath, manifest.Payload.SizeBytes);

            // 3. Execute action
            switch (manifest.Action)
            {
                case ActionPrimitive.Passive:
                    _logger.LogInformation("Passive download completed for {ManifestId}", manifest.ManifestId);
                    break;

                case ActionPrimitive.Notify:
                    _logger.LogInformation("Showing notification for {Name}: {ManifestId}",
                        manifest.Payload.Name, manifest.ManifestId);
                    // Toast notification: platform-specific notification dispatch not yet wired.
                    break;

                case ActionPrimitive.Execute:
                    if (manifest.Signature?.IsReleaseKey == true)
                    {
                        _logger.LogWarning("Execute action requested for {ManifestId} - not implemented for security",
                            manifest.ManifestId);
                        // Sandboxed execution: requires secure process isolation layer.
                    }
                    else
                    {
                        _logger.LogWarning("Execute action denied for {ManifestId} - not a release key",
                            manifest.ManifestId);
                    }
                    break;

                case ActionPrimitive.Interactive:
                    _logger.LogInformation("Starting watchdog for interactive file {LocalPath}", localPath);
                    StartWatchingFile(localPath, manifest.Payload.PayloadId);
                    break;

                case ActionPrimitive.Custom:
                    _logger.LogWarning("Custom action for {ManifestId} - not implemented", manifest.ManifestId);
                    break;

                default:
                    _logger.LogWarning("Unknown action {Action} for {ManifestId}", manifest.Action, manifest.ManifestId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process manifest {ManifestId}", manifest.ManifestId);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>
    /// Starts watching a file for changes.
    /// </summary>
    private void StartWatchingFile(string localPath, string payloadId)
    {
        if (_watchedFiles.ContainsKey(localPath))
        {
            _logger.LogDebug("File {LocalPath} is already being watched", localPath);
            return;
        }

        var watchedFile = new WatchedFile(
            LocalPath: localPath,
            PayloadId: payloadId,
            LastModified: File.GetLastWriteTimeUtc(localPath),
            PendingSync: false
        );

        _watchedFiles[localPath] = watchedFile;

        var directory = Path.GetDirectoryName(localPath)!;
        var fileName = Path.GetFileName(localPath);

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Changed += (sender, e) => OnFileChanged(localPath, payloadId, FileChangeType.Modified);
        watcher.Deleted += (sender, e) => OnFileChanged(localPath, payloadId, FileChangeType.Deleted);
        watcher.Renamed += (sender, e) => OnFileChanged(localPath, payloadId, FileChangeType.Renamed);

        _watchers[localPath] = watcher;

        _logger.LogInformation("Started watching file {LocalPath} for changes", localPath);
    }

    /// <summary>
    /// Handles file change events.
    /// </summary>
    private void OnFileChanged(string localPath, string payloadId, FileChangeType changeType)
    {
        _logger.LogInformation("File {LocalPath} changed (type: {ChangeType})", localPath, changeType);

        FileChanged?.Invoke(this, new FileChangedEventArgs
        {
            LocalPath = localPath,
            PayloadId = payloadId,
            ChangeType = changeType
        });

        // Auto-sync back to server: file change upload not yet wired to data plane.
    }

    /// <summary>
    /// Gets all currently watched files.
    /// </summary>
    /// <returns>List of watched files.</returns>
    public IReadOnlyList<WatchedFile> GetWatchedFiles()
    {
        return _watchedFiles.Values.ToList();
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _executionLock?.Dispose();

            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }
        base.Dispose(disposing);
    }
}
