using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Status of a managed physical device.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device status (BMDV-03)")]
public enum DeviceStatus
{
    /// <summary>Device is online and healthy.</summary>
    Online,
    /// <summary>Device is experiencing degraded performance or medium-risk prediction.</summary>
    Degraded,
    /// <summary>Device has high/critical failure prediction.</summary>
    Failing,
    /// <summary>Device is offline or unresponsive.</summary>
    Offline,
    /// <summary>Device was previously known but no longer discovered.</summary>
    Removed
}

/// <summary>
/// Represents a physical device under active management with its latest health and prediction state.
/// </summary>
/// <param name="Info">Physical device metadata.</param>
/// <param name="LastHealth">Most recent SMART health snapshot.</param>
/// <param name="LastPrediction">Most recent failure prediction.</param>
/// <param name="LastHealthCheck">Timestamp of the last health check.</param>
/// <param name="Status">Current device status.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Managed device state (BMDV-03/BMDV-04)")]
public sealed record ManagedDevice(
    PhysicalDeviceInfo Info,
    PhysicalDeviceHealth LastHealth,
    FailurePrediction LastPrediction,
    DateTime LastHealthCheck,
    DeviceStatus Status);

/// <summary>
/// Configuration for the PhysicalDeviceManager including polling intervals and prediction settings.
/// </summary>
/// <param name="HealthPollInterval">Interval between health polling cycles.</param>
/// <param name="DiscoveryInterval">Interval between device discovery cycles.</param>
/// <param name="AutoDiscoveryEnabled">Whether automatic device discovery is enabled.</param>
/// <param name="PredictionConfig">Configuration for the failure prediction engine.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device manager config (BMDV-03/BMDV-04)")]
public sealed record DeviceManagerConfig(
    TimeSpan? HealthPollInterval = null,
    TimeSpan? DiscoveryInterval = null,
    bool AutoDiscoveryEnabled = true,
    FailurePredictionConfig? PredictionConfig = null)
{
    /// <summary>Effective health poll interval (default 5 minutes).</summary>
    public TimeSpan EffectiveHealthPollInterval => HealthPollInterval ?? TimeSpan.FromMinutes(5);
    /// <summary>Effective discovery interval (default 30 minutes).</summary>
    public TimeSpan EffectiveDiscoveryInterval => DiscoveryInterval ?? TimeSpan.FromMinutes(30);
}

/// <summary>
/// Central device lifecycle and health orchestrator that manages registered physical devices,
/// performing periodic SMART health monitoring, EWMA-based failure prediction, and device
/// discovery. Fires callbacks for health updates, failure predictions, status changes,
/// and device appearance/removal events.
/// </summary>
/// <remarks>
/// <para>
/// The manager runs two periodic loops: a discovery loop that enumerates devices via
/// <see cref="DeviceDiscoveryService"/> and a health polling loop that reads SMART attributes
/// via <see cref="SmartMonitor"/> and feeds them to <see cref="FailurePredictionEngine"/>.
/// </para>
/// <para>
/// Status transitions follow the pattern: Online -> Degraded -> Failing when predictions
/// worsen, and back to Online when conditions improve. Devices that become unreachable
/// transition to Offline. Devices removed between discovery cycles become Removed.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle and health orchestration (BMDV-03/BMDV-04)")]
public sealed class PhysicalDeviceManager : IAsyncDisposable
{
    private readonly DeviceDiscoveryService _discoveryService;
    private readonly SmartMonitor _smartMonitor;
    private readonly FailurePredictionEngine _predictionEngine;
    private readonly DeviceManagerConfig _config;
    private readonly ILogger _logger;

    private readonly BoundedDictionary<string, ManagedDevice> _managedDevices;

    private PeriodicTimer? _healthTimer;
    private PeriodicTimer? _discoveryTimer;
    private CancellationTokenSource? _cts;
    private Task? _healthPollingTask;
    private Task? _discoveryPollingTask;
    private volatile bool _isRunning;

    /// <summary>
    /// Called after each health poll for a device with the device ID and health snapshot.
    /// </summary>
    public Action<string, PhysicalDeviceHealth>? OnHealthUpdate { get; set; }

    /// <summary>
    /// Called when a device's failure prediction reaches Medium risk or above.
    /// </summary>
    public Action<string, FailurePrediction>? OnFailurePrediction { get; set; }

    /// <summary>
    /// Called when a device's status transitions (deviceId, oldStatus, newStatus).
    /// </summary>
    public Action<string, DeviceStatus, DeviceStatus>? OnStatusChange { get; set; }

    /// <summary>
    /// Called when a new device is discovered during a discovery cycle.
    /// </summary>
    public Action<PhysicalDeviceInfo>? OnDeviceDiscovered { get; set; }

    /// <summary>
    /// Called when a previously known device is no longer found during discovery.
    /// </summary>
    public Action<string>? OnDeviceRemoved { get; set; }

    /// <summary>
    /// Initializes a new PhysicalDeviceManager with the required dependencies.
    /// </summary>
    /// <param name="discoveryService">Service for enumerating physical devices.</param>
    /// <param name="smartMonitor">Monitor for reading SMART health attributes.</param>
    /// <param name="predictionEngine">Engine for EWMA-based failure prediction.</param>
    /// <param name="config">Optional configuration. Uses defaults if null.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PhysicalDeviceManager(
        DeviceDiscoveryService discoveryService,
        SmartMonitor smartMonitor,
        FailurePredictionEngine predictionEngine,
        DeviceManagerConfig? config = null,
        ILogger? logger = null)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _smartMonitor = smartMonitor ?? throw new ArgumentNullException(nameof(smartMonitor));
        _predictionEngine = predictionEngine ?? throw new ArgumentNullException(nameof(predictionEngine));
        _config = config ?? new DeviceManagerConfig();
        _logger = logger ?? NullLogger.Instance;
        _managedDevices = new BoundedDictionary<string, ManagedDevice>(500);
    }

    /// <summary>
    /// Starts the device discovery and health polling loops.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the manager.</param>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        // Start health polling loop
        _healthTimer = new PeriodicTimer(_config.EffectiveHealthPollInterval);
        _healthPollingTask = RunHealthPollingLoopAsync(_cts.Token);

        // Start discovery loop if auto-discovery is enabled
        if (_config.AutoDiscoveryEnabled)
        {
            _discoveryTimer = new PeriodicTimer(_config.EffectiveDiscoveryInterval);
            _discoveryPollingTask = RunDiscoveryLoopAsync(_cts.Token);
        }

        _logger.LogInformation("PhysicalDeviceManager started. Health poll: {HealthInterval}, Discovery: {DiscoveryInterval}, AutoDiscovery: {AutoDiscovery}.",
            _config.EffectiveHealthPollInterval, _config.EffectiveDiscoveryInterval, _config.AutoDiscoveryEnabled);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops all polling loops and marks all devices as Offline.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        // Wait for loops to complete
        if (_healthPollingTask != null)
        {
            try { await _healthPollingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Expected */ }
        }

        if (_discoveryPollingTask != null)
        {
            try { await _discoveryPollingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Expected */ }
        }

        // Mark all devices offline
        foreach (var kvp in _managedDevices)
        {
            var device = kvp.Value;
            if (device.Status != DeviceStatus.Offline)
            {
                var oldStatus = device.Status;
                var updated = device with { Status = DeviceStatus.Offline };
                _managedDevices[kvp.Key] = updated;

                try { OnStatusChange?.Invoke(kvp.Key, oldStatus, DeviceStatus.Offline); }
                catch { /* Callback errors should not prevent shutdown */ }
            }
        }

        _logger.LogInformation("PhysicalDeviceManager stopped.");
    }

    /// <summary>
    /// Gets a snapshot of all managed devices.
    /// </summary>
    /// <returns>Read-only list of all currently managed devices.</returns>
    public Task<IReadOnlyList<ManagedDevice>> GetDevicesAsync()
    {
        var devices = _managedDevices.Select(kvp => kvp.Value).ToList();
        return Task.FromResult<IReadOnlyList<ManagedDevice>>(devices.AsReadOnly());
    }

    /// <summary>
    /// Gets a single managed device by its ID.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <returns>The managed device if found, null otherwise.</returns>
    public Task<ManagedDevice?> GetDeviceAsync(string deviceId)
    {
        _managedDevices.TryGetValue(deviceId, out var device);
        return Task.FromResult<ManagedDevice?>(device);
    }

    /// <summary>
    /// Forces an immediate health check for a specific device.
    /// </summary>
    /// <param name="deviceId">Device identifier to check.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ForceHealthCheckAsync(string deviceId, CancellationToken ct = default)
    {
        if (!_managedDevices.TryGetValue(deviceId, out var device))
        {
            _logger.LogWarning("Cannot force health check: device {DeviceId} not found.", deviceId);
            return;
        }

        await CheckDeviceHealthAsync(deviceId, device, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forces an immediate device discovery cycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ForceDiscoveryAsync(CancellationToken ct = default)
    {
        await RunDiscoveryCycleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Manually registers a device for management (for testing or remote devices).
    /// </summary>
    /// <param name="info">Physical device information to register.</param>
    public Task RegisterDeviceAsync(PhysicalDeviceInfo info)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));

        if (!_managedDevices.ContainsKey(info.DeviceId))
        {
            var defaultHealth = new PhysicalDeviceHealth(
                IsHealthy: true, TemperatureCelsius: -1, WearLevelPercent: 0,
                TotalBytesWritten: 0, TotalBytesRead: 0, UncorrectableErrors: 0,
                ReallocatedSectors: 0, PowerOnHours: 0, EstimatedRemainingLife: null,
                RawSmartAttributes: new Dictionary<string, string>());

            var defaultPrediction = new FailurePrediction(
                DeviceId: info.DeviceId, IsAtRisk: false,
                EstimatedTimeToFailure: null, RiskLevel: "None",
                RiskFactors: Array.Empty<string>());

            var managed = new ManagedDevice(
                Info: info,
                LastHealth: defaultHealth,
                LastPrediction: defaultPrediction,
                LastHealthCheck: DateTime.UtcNow,
                Status: DeviceStatus.Online);

            _managedDevices[info.DeviceId] = managed;

            _logger.LogInformation("Device {DeviceId} ({Model}) manually registered.",
                info.DeviceId, info.ModelNumber);

            try { OnDeviceDiscovered?.Invoke(info); }
            catch { /* Callback errors are non-fatal */ }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes a device from management.
    /// </summary>
    /// <param name="deviceId">Device identifier to remove.</param>
    public Task UnregisterDeviceAsync(string deviceId)
    {
        if (_managedDevices.TryRemove(deviceId, out _))
        {
            _predictionEngine.Reset(deviceId);
            _logger.LogInformation("Device {DeviceId} unregistered.", deviceId);

            try { OnDeviceRemoved?.Invoke(deviceId); }
            catch { /* Callback errors are non-fatal */ }
        }

        return Task.CompletedTask;
    }

    // ========================================================================
    // Health polling loop
    // ========================================================================

    private async Task RunHealthPollingLoopAsync(CancellationToken ct)
    {
        // Initial health check for existing devices
        await RunHealthCycleAsync(ct).ConfigureAwait(false);

        while (_isRunning && _healthTimer != null)
        {
            try
            {
                if (!await _healthTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                await RunHealthCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health polling cycle failed. Will retry on next tick.");
            }
        }
    }

    private async Task RunHealthCycleAsync(CancellationToken ct)
    {
        foreach (var kvp in _managedDevices)
        {
            ct.ThrowIfCancellationRequested();

            if (kvp.Value.Status == DeviceStatus.Removed) continue;

            try
            {
                await CheckDeviceHealthAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for device {DeviceId}.", kvp.Key);
                TransitionStatus(kvp.Key, kvp.Value, DeviceStatus.Offline);
            }
        }
    }

    private async Task CheckDeviceHealthAsync(string deviceId, ManagedDevice device, CancellationToken ct)
    {
        PhysicalDeviceHealth health;
        try
        {
            health = await _smartMonitor.ReadSmartAttributesAsync(
                device.Info.DevicePath, device.Info.BusType, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMART read failed for {DeviceId}. Marking offline.", deviceId);
            TransitionStatus(deviceId, device, DeviceStatus.Offline);
            return;
        }

        var prediction = _predictionEngine.UpdateAndPredict(deviceId, health);

        // Determine new status based on health and prediction
        DeviceStatus newStatus;
        if (health.IsHealthy && prediction.RiskLevel is "None" or "Low")
        {
            newStatus = DeviceStatus.Online;
        }
        else if (prediction.RiskLevel == "Medium")
        {
            newStatus = DeviceStatus.Degraded;
        }
        else if (prediction.RiskLevel is "High" or "Critical")
        {
            newStatus = DeviceStatus.Failing;
        }
        else
        {
            newStatus = DeviceStatus.Online;
        }

        var updated = new ManagedDevice(
            Info: device.Info,
            LastHealth: health,
            LastPrediction: prediction,
            LastHealthCheck: DateTime.UtcNow,
            Status: newStatus);

        _managedDevices[deviceId] = updated;

        // Fire health update callback
        try { OnHealthUpdate?.Invoke(deviceId, health); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnHealthUpdate callback failed for {DeviceId}.", deviceId);
        }

        // Fire failure prediction callback if risk is Medium or above
        if (prediction.IsAtRisk)
        {
            try { OnFailurePrediction?.Invoke(deviceId, prediction); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnFailurePrediction callback failed for {DeviceId}.", deviceId);
            }
        }

        // Fire status change callback if status changed
        if (device.Status != newStatus)
        {
            _logger.LogInformation("Device {DeviceId} status: {OldStatus} -> {NewStatus}.",
                deviceId, device.Status, newStatus);

            try { OnStatusChange?.Invoke(deviceId, device.Status, newStatus); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnStatusChange callback failed for {DeviceId}.", deviceId);
            }
        }
    }

    private void TransitionStatus(string deviceId, ManagedDevice device, DeviceStatus newStatus)
    {
        if (device.Status == newStatus) return;

        var oldStatus = device.Status;
        var updated = device with { Status = newStatus, LastHealthCheck = DateTime.UtcNow };
        _managedDevices[deviceId] = updated;

        _logger.LogWarning("Device {DeviceId} status: {OldStatus} -> {NewStatus}.",
            deviceId, oldStatus, newStatus);

        try { OnStatusChange?.Invoke(deviceId, oldStatus, newStatus); }
        catch { /* Callback errors are non-fatal */ }
    }

    // ========================================================================
    // Discovery loop
    // ========================================================================

    private async Task RunDiscoveryLoopAsync(CancellationToken ct)
    {
        // Initial discovery
        await RunDiscoveryCycleAsync(ct).ConfigureAwait(false);

        while (_isRunning && _discoveryTimer != null)
        {
            try
            {
                if (!await _discoveryTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                await RunDiscoveryCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discovery cycle failed. Will retry on next tick.");
            }
        }
    }

    private async Task RunDiscoveryCycleAsync(CancellationToken ct)
    {
        IReadOnlyList<PhysicalDeviceInfo> discoveredDevices;
        try
        {
            discoveredDevices = await _discoveryService.DiscoverDevicesAsync(ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device discovery failed.");
            return;
        }

        var discoveredIds = new HashSet<string>(discoveredDevices.Select(d => d.DeviceId));

        // Add new devices
        foreach (var device in discoveredDevices)
        {
            ct.ThrowIfCancellationRequested();

            if (!_managedDevices.ContainsKey(device.DeviceId))
            {
                var defaultHealth = new PhysicalDeviceHealth(
                    IsHealthy: true, TemperatureCelsius: -1, WearLevelPercent: 0,
                    TotalBytesWritten: 0, TotalBytesRead: 0, UncorrectableErrors: 0,
                    ReallocatedSectors: 0, PowerOnHours: 0, EstimatedRemainingLife: null,
                    RawSmartAttributes: new Dictionary<string, string>());

                var defaultPrediction = new FailurePrediction(
                    DeviceId: device.DeviceId, IsAtRisk: false,
                    EstimatedTimeToFailure: null, RiskLevel: "None",
                    RiskFactors: Array.Empty<string>());

                var managed = new ManagedDevice(
                    Info: device,
                    LastHealth: defaultHealth,
                    LastPrediction: defaultPrediction,
                    LastHealthCheck: DateTime.UtcNow,
                    Status: DeviceStatus.Online);

                _managedDevices[device.DeviceId] = managed;

                _logger.LogInformation("New device discovered: {DeviceId} ({Model}, {MediaType}).",
                    device.DeviceId, device.ModelNumber, device.MediaType);

                try { OnDeviceDiscovered?.Invoke(device); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnDeviceDiscovered callback failed for {DeviceId}.", device.DeviceId);
                }
            }
        }

        // Detect removed devices
        var currentIds = _managedDevices.Select(kvp => kvp.Key).ToList();
        foreach (var existingId in currentIds)
        {
            if (!discoveredIds.Contains(existingId))
            {
                if (_managedDevices.TryGetValue(existingId, out var existing) &&
                    existing.Status != DeviceStatus.Removed)
                {
                    var oldStatus = existing.Status;
                    var updated = existing with { Status = DeviceStatus.Removed };
                    _managedDevices[existingId] = updated;

                    _logger.LogWarning("Device {DeviceId} no longer discovered. Marking as Removed.", existingId);

                    try { OnStatusChange?.Invoke(existingId, oldStatus, DeviceStatus.Removed); }
                    catch { /* Callback errors are non-fatal */ }

                    try { OnDeviceRemoved?.Invoke(existingId); }
                    catch { /* Callback errors are non-fatal */ }
                }
            }
        }
    }

    /// <summary>
    /// Disposes the manager, stopping all polling loops and clearing state.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isRunning)
        {
            await StopAsync().ConfigureAwait(false);
        }

        _healthTimer?.Dispose();
        _discoveryTimer?.Dispose();
        _cts?.Dispose();
        _managedDevices.Dispose();
    }
}
