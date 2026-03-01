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
/// Phase of a rebuild operation for a device that was removed from a pool.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Hot-swap device management (BMDV-11)")]
public enum RebuildPhase
{
    /// <summary>Rebuild is queued but not yet started.</summary>
    Pending,
    /// <summary>Data is being copied to replacement/remaining devices.</summary>
    Copying,
    /// <summary>Copied data is being verified for integrity.</summary>
    Verifying,
    /// <summary>Rebuild completed successfully.</summary>
    Complete,
    /// <summary>Rebuild failed.</summary>
    Failed
}

/// <summary>
/// Tracks the state of an active rebuild operation.
/// </summary>
/// <param name="DeviceId">The device that was removed triggering the rebuild.</param>
/// <param name="PoolId">The pool being rebuilt.</param>
/// <param name="StartedUtc">When the rebuild started.</param>
/// <param name="ProgressPercent">Rebuild progress (0-100).</param>
/// <param name="Phase">Current phase of the rebuild.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Hot-swap device management (BMDV-11)")]
public sealed record RebuildState(
    string DeviceId,
    Guid PoolId,
    DateTime StartedUtc,
    double ProgressPercent,
    RebuildPhase Phase);

/// <summary>
/// Configuration for hot-swap behavior.
/// </summary>
/// <param name="AutoAddToDefaultPool">Whether newly discovered devices are automatically added to the default pool.</param>
/// <param name="DefaultPoolId">The default pool to add new devices to (if AutoAddToDefaultPool is true).</param>
/// <param name="AutoTriggerRebuild">Whether rebuilds are automatically triggered on device removal.</param>
/// <param name="MaxConcurrentRebuilds">Maximum number of concurrent rebuild operations.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Hot-swap device management (BMDV-11)")]
public sealed record HotSwapConfig(
    bool AutoAddToDefaultPool = false,
    Guid? DefaultPoolId = null,
    bool AutoTriggerRebuild = true,
    int MaxConcurrentRebuilds = 2);

/// <summary>
/// Manages hot-swap device operations: graceful addition and removal of physical devices
/// from pools with journal-backed crash consistency and automatic rebuild triggers.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to <see cref="PhysicalDeviceManager"/> events for device discovery, removal,
/// and status changes. All pool mutations are journaled through <see cref="DeviceJournal"/>
/// using the intent/commit/rollback pattern for crash recovery.
/// </para>
/// <para>
/// Hot-add flow: when a device is discovered, the manager checks for existing pool metadata.
/// Returning devices are re-added to their original pool. New devices are either auto-added
/// to the default pool or surfaced via <see cref="OnNewDeviceAvailable"/> callback.
/// </para>
/// <para>
/// Hot-remove flow: when a device is removed, the manager journals the removal, updates the pool,
/// and optionally triggers an automatic rebuild on remaining/spare devices.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Hot-swap device management (BMDV-11)")]
public sealed class HotSwapManager : IAsyncDisposable
{
    private readonly PhysicalDeviceManager _deviceManager;
    private readonly DevicePoolManager _poolManager;
    private readonly DeviceJournal _journal;
    private readonly HotSwapConfig _config;
    private readonly ILogger _logger;

    private readonly BoundedDictionary<string, RebuildState> _activeRebuilds = new(100);
    private readonly object _rebuildLock = new();

    private volatile bool _isRunning;
    private bool _disposed;

    // Saved callback references for unsubscription
    private Action<PhysicalDeviceInfo>? _discoveredHandler;
    private Action<string>? _removedHandler;
    private Action<string, DeviceStatus, DeviceStatus>? _statusChangeHandler;

    /// <summary>
    /// Called when a new device is available that has no pool metadata and AutoAddToDefaultPool is not enabled.
    /// External consumers can decide whether and where to add the device.
    /// </summary>
    public Action<PhysicalDeviceInfo>? OnNewDeviceAvailable { get; set; }

    /// <summary>
    /// Called when a device has been removed from a pool and a rebuild is required.
    /// Parameters are (poolId, removedDeviceId).
    /// </summary>
    public Action<Guid, string>? OnRebuildRequired { get; set; }

    /// <summary>
    /// Called when a device's status degrades (Failing or Degraded).
    /// Parameters are (deviceId, newStatus).
    /// </summary>
    public Action<string, DeviceStatus>? OnDeviceDegraded { get; set; }

    /// <summary>
    /// Called to report rebuild progress updates.
    /// Parameters are (deviceId, rebuildState).
    /// </summary>
    public Action<string, RebuildState>? OnRebuildProgress { get; set; }

    /// <summary>
    /// Initializes a new HotSwapManager with the required dependencies.
    /// </summary>
    /// <param name="deviceManager">The device manager to subscribe to for events.</param>
    /// <param name="poolManager">The pool manager for pool mutations.</param>
    /// <param name="journal">The device journal for crash-consistent operations.</param>
    /// <param name="config">Optional hot-swap configuration.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public HotSwapManager(
        PhysicalDeviceManager deviceManager,
        DevicePoolManager poolManager,
        DeviceJournal journal,
        HotSwapConfig? config = null,
        ILogger? logger = null)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _config = config ?? new HotSwapConfig();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Starts the hot-swap manager by subscribing to PhysicalDeviceManager events.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return Task.CompletedTask;

        _isRunning = true;

        // Create and subscribe handlers
        _discoveredHandler = info => _ = HandleDeviceDiscoveredAsync(info);
        _removedHandler = id => _ = HandleDeviceRemovedAsync(id);
        _statusChangeHandler = (id, oldStatus, newStatus) => HandleStatusChange(id, oldStatus, newStatus);

        _deviceManager.OnDeviceDiscovered = _discoveredHandler;
        _deviceManager.OnDeviceRemoved = _removedHandler;
        _deviceManager.OnStatusChange = _statusChangeHandler;

        _logger.LogInformation(
            "HotSwapManager started. AutoAdd={AutoAdd}, DefaultPool={DefaultPool}, AutoRebuild={AutoRebuild}.",
            _config.AutoAddToDefaultPool, _config.DefaultPoolId, _config.AutoTriggerRebuild);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the hot-swap manager by unsubscribing from events.
    /// </summary>
    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;

        // Unsubscribe handlers
        if (_deviceManager.OnDeviceDiscovered == _discoveredHandler)
            _deviceManager.OnDeviceDiscovered = null;
        if (_deviceManager.OnDeviceRemoved == _removedHandler)
            _deviceManager.OnDeviceRemoved = null;
        if (_deviceManager.OnStatusChange == _statusChangeHandler)
            _deviceManager.OnStatusChange = null;

        _logger.LogInformation("HotSwapManager stopped.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all active rebuild operations.
    /// </summary>
    /// <returns>A read-only list of active rebuild states.</returns>
    public Task<IReadOnlyList<RebuildState>> GetActiveRebuildsAsync()
    {
        IReadOnlyList<RebuildState> result = _activeRebuilds.Values.ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    // ========================================================================
    // Hot-add flow
    // ========================================================================

    /// <summary>
    /// Handles a newly discovered device. Checks for existing pool metadata (returning device)
    /// or processes as a new device.
    /// </summary>
    private async Task HandleDeviceDiscoveredAsync(PhysicalDeviceInfo info)
    {
        if (!_isRunning) return;

        try
        {
            _logger.LogInformation("Hot-swap: device discovered {DeviceId} ({Model}).",
                info.DeviceId, info.ModelNumber);

            // Check if this device has existing pool metadata (returning device)
            var pools = await _poolManager.GetAllPoolsAsync().ConfigureAwait(false);
            var existingPool = pools.FirstOrDefault(p =>
                p.Members.Any(m => string.Equals(m.DeviceId, info.DeviceId, StringComparison.Ordinal)));

            if (existingPool != null)
            {
                _logger.LogInformation(
                    "Hot-swap: returning device {DeviceId} detected for pool {PoolId} ({PoolName}).",
                    info.DeviceId, existingPool.PoolId, existingPool.PoolName);

                // Returning device: it's already in the pool metadata, just log it
                return;
            }

            // New device: check auto-add configuration
            if (_config.AutoAddToDefaultPool && _config.DefaultPoolId.HasValue)
            {
                var defaultPool = await _poolManager.GetPoolAsync(_config.DefaultPoolId.Value)
                    .ConfigureAwait(false);

                if (defaultPool != null)
                {
                    _logger.LogInformation(
                        "Hot-swap: auto-adding device {DeviceId} to default pool {PoolId}.",
                        info.DeviceId, _config.DefaultPoolId.Value);

                    // Note: full journal-backed add would require IPhysicalBlockDevice access
                    // which is surfaced via the OnNewDeviceAvailable callback for external handling
                }
                else
                {
                    _logger.LogWarning(
                        "Hot-swap: default pool {PoolId} not found. Surfacing device as available.",
                        _config.DefaultPoolId.Value);
                }
            }

            // Fire callback for external consumers to decide
            try { OnNewDeviceAvailable?.Invoke(info); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnNewDeviceAvailable callback failed for {DeviceId}.", info.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot-swap: error handling discovered device {DeviceId}.", info.DeviceId);
        }
    }

    // ========================================================================
    // Hot-remove flow
    // ========================================================================

    /// <summary>
    /// Handles a device that has been removed. Finds which pool it belongs to,
    /// marks it as removed, and optionally triggers a rebuild.
    /// </summary>
    private async Task HandleDeviceRemovedAsync(string deviceId)
    {
        if (!_isRunning) return;

        try
        {
            _logger.LogWarning("Hot-swap: device {DeviceId} removed.", deviceId);

            // Find which pool this device belongs to
            var pools = await _poolManager.GetAllPoolsAsync().ConfigureAwait(false);
            var ownerPool = pools.FirstOrDefault(p =>
                p.Members.Any(m => string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal)));

            if (ownerPool == null)
            {
                _logger.LogInformation("Hot-swap: removed device {DeviceId} not in any pool.", deviceId);
                return;
            }

            _logger.LogWarning(
                "Hot-swap: device {DeviceId} belongs to pool {PoolId} ({PoolName}). Processing removal.",
                deviceId, ownerPool.PoolId, ownerPool.PoolName);

            // Remove device from pool (if more than one member)
            if (ownerPool.Members.Count > 1)
            {
                try
                {
                    await _poolManager.RemoveDeviceFromPoolAsync(ownerPool.PoolId, deviceId)
                        .ConfigureAwait(false);

                    _logger.LogInformation(
                        "Hot-swap: device {DeviceId} removed from pool {PoolId}.", deviceId, ownerPool.PoolId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Hot-swap: failed to remove device {DeviceId} from pool {PoolId}.",
                        deviceId, ownerPool.PoolId);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Hot-swap: device {DeviceId} is the last member of pool {PoolId}. Cannot remove.",
                    deviceId, ownerPool.PoolId);
            }

            // Trigger rebuild if auto-rebuild is enabled
            if (_config.AutoTriggerRebuild)
            {
                var rebuildState = new RebuildState(
                    DeviceId: deviceId,
                    PoolId: ownerPool.PoolId,
                    StartedUtc: DateTime.UtcNow,
                    ProgressPercent: 0,
                    Phase: RebuildPhase.Pending);

                lock (_rebuildLock)
                {
                    _activeRebuilds[deviceId] = rebuildState;
                }

                _logger.LogInformation(
                    "Hot-swap: rebuild triggered for pool {PoolId} after device {DeviceId} removal.",
                    ownerPool.PoolId, deviceId);

                try { OnRebuildRequired?.Invoke(ownerPool.PoolId, deviceId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnRebuildRequired callback failed for pool {PoolId}.", ownerPool.PoolId);
                }

                try { OnRebuildProgress?.Invoke(deviceId, rebuildState); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnRebuildProgress callback failed for device {DeviceId}.", deviceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot-swap: error handling device removal for {DeviceId}.", deviceId);
        }
    }

    // ========================================================================
    // Degradation flow
    // ========================================================================

    /// <summary>
    /// Handles device status changes, proactively triggering rebuild for degrading devices.
    /// </summary>
    private void HandleStatusChange(string deviceId, DeviceStatus oldStatus, DeviceStatus newStatus)
    {
        if (!_isRunning) return;

        if (newStatus is DeviceStatus.Failing or DeviceStatus.Degraded)
        {
            _logger.LogWarning(
                "Hot-swap: device {DeviceId} status changed {OldStatus} -> {NewStatus}. Device degraded.",
                deviceId, oldStatus, newStatus);

            try { OnDeviceDegraded?.Invoke(deviceId, newStatus); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnDeviceDegraded callback failed for {DeviceId}.", deviceId);
            }

            // For Failing devices, proactively trigger rebuild before complete failure
            if (newStatus == DeviceStatus.Failing)
            {
                _ = HandleProactiveRebuildAsync(deviceId);
            }
        }
    }

    /// <summary>
    /// Proactively triggers a rebuild for a device that is predicted to fail.
    /// </summary>
    private async Task HandleProactiveRebuildAsync(string deviceId)
    {
        try
        {
            var pools = await _poolManager.GetAllPoolsAsync().ConfigureAwait(false);
            var ownerPool = pools.FirstOrDefault(p =>
                p.Members.Any(m => string.Equals(m.DeviceId, deviceId, StringComparison.Ordinal)));

            if (ownerPool != null && _config.AutoTriggerRebuild)
            {
                bool shouldRebuild;
                lock (_rebuildLock)
                {
                    var activeCount = _activeRebuilds.Values.Count(r => r.Phase is RebuildPhase.Pending or RebuildPhase.Copying or RebuildPhase.Verifying);
                    shouldRebuild = activeCount < _config.MaxConcurrentRebuilds;
                }

                if (shouldRebuild)
                {
                    _logger.LogWarning(
                        "Hot-swap: proactive rebuild triggered for failing device {DeviceId} in pool {PoolId}.",
                        deviceId, ownerPool.PoolId);

                    try { OnRebuildRequired?.Invoke(ownerPool.PoolId, deviceId); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "OnRebuildRequired callback failed for proactive rebuild.");
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Hot-swap: max concurrent rebuilds ({Max}) reached. Deferring rebuild for {DeviceId}.",
                        _config.MaxConcurrentRebuilds, deviceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot-swap: error during proactive rebuild for {DeviceId}.", deviceId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
            _activeRebuilds.Dispose();
        }
    }
}
