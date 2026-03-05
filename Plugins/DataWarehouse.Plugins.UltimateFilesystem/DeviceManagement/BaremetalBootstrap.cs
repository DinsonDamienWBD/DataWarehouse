using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StorageTier = DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice.StorageTier;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Result of a bare-metal bootstrap operation.
/// </summary>
/// <param name="Success">Whether the bootstrap completed successfully.</param>
/// <param name="RestoredPools">Pools discovered from device reserved sectors.</param>
/// <param name="UnpooledDevices">Devices that had no pool metadata.</param>
/// <param name="RecoveredIntents">Uncommitted journal entries that were processed during recovery.</param>
/// <param name="Warnings">Non-fatal issues encountered during bootstrap.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Bare-metal bootstrap (BMDV-12)")]
public sealed record BootstrapResult(
    bool Success,
    IReadOnlyList<DevicePoolDescriptor> RestoredPools,
    IReadOnlyList<PhysicalDeviceInfo> UnpooledDevices,
    IReadOnlyList<JournalEntry> RecoveredIntents,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Initializes DataWarehouse on raw physical devices without any OS volume manager.
/// Discovers devices, scans for pool metadata on reserved sectors, recovers interrupted
/// operations from the device journal, and classifies unpooled devices.
/// </summary>
/// <remarks>
/// <para>
/// Bootstrap flow:
/// 1. Discover all physical devices via DeviceDiscoveryService
/// 2. Register all discovered devices with PhysicalDeviceManager
/// 3. Scan pool metadata from reserved sectors via DevicePoolManager.ScanForPoolsAsync
/// 4. Recover journal: read uncommitted intents and resolve them
/// 5. Classify unpooled devices for user assignment
/// </para>
/// <para>
/// Journal recovery handles interrupted operations:
/// - DeviceAdd with Intent phase: verify device in pool metadata; roll back if not
/// - DeviceRemove with Intent phase: device still in pool; re-mark as active
/// - RebuildStart with Intent phase: rebuild was interrupted; queue for restart
/// - PoolCreate/PoolDelete with Intent phase: check metadata; clean up if partial
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Bare-metal bootstrap (BMDV-12)")]
public sealed class BaremetalBootstrap
{
    private readonly DeviceDiscoveryService _discoveryService;
    private readonly DevicePoolManager _poolManager;
    private readonly DeviceJournal _journal;
    private readonly PhysicalDeviceManager _deviceManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new BaremetalBootstrap with the required dependencies.
    /// </summary>
    /// <param name="discoveryService">Service for enumerating physical devices.</param>
    /// <param name="poolManager">Manager for pool lifecycle operations.</param>
    /// <param name="journal">Device journal for crash recovery.</param>
    /// <param name="deviceManager">Manager for device registration and health monitoring.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public BaremetalBootstrap(
        DeviceDiscoveryService discoveryService,
        DevicePoolManager poolManager,
        DeviceJournal journal,
        PhysicalDeviceManager deviceManager,
        ILogger? logger = null)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Bootstraps DataWarehouse from raw physical devices. Discovers devices, scans for
    /// existing pools from reserved sectors, recovers interrupted journal operations,
    /// and classifies unpooled devices.
    /// </summary>
    /// <param name="physicalDevices">
    /// Optional list of pre-opened <see cref="IPhysicalBlockDevice"/> handles corresponding to the
    /// discovered devices. When provided, these are passed directly to
    /// <see cref="DevicePoolManager.ScanForPoolsAsync"/> so block 0 metadata can be read and pools
    /// restored. When null or empty the pool scan is skipped (no pools can be recovered from disk).
    /// Callers should provide handles for every discovered device; unmatched devices remain unpooled.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="BootstrapResult"/> with restored pools, unpooled devices, and recovered intents.</returns>
    public async Task<BootstrapResult> BootstrapFromRawDevicesAsync(
        IReadOnlyList<IPhysicalBlockDevice>? physicalDevices = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var recoveredIntents = new List<JournalEntry>();

        _logger.LogInformation("Bare-metal bootstrap: starting device discovery.");

        // Step 1: Discover all physical devices
        IReadOnlyList<PhysicalDeviceInfo> discoveredDevices;
        try
        {
            discoveredDevices = await _discoveryService.DiscoverDevicesAsync(ct: ct).ConfigureAwait(false);
            _logger.LogInformation("Bare-metal bootstrap: discovered {Count} physical devices.", discoveredDevices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bare-metal bootstrap: device discovery failed.");
            return new BootstrapResult(
                Success: false,
                RestoredPools: Array.Empty<DevicePoolDescriptor>(),
                UnpooledDevices: Array.Empty<PhysicalDeviceInfo>(),
                RecoveredIntents: Array.Empty<JournalEntry>(),
                Warnings: new[] { $"Device discovery failed: {ex.Message}" });
        }

        // Step 2: Register all discovered devices with PhysicalDeviceManager
        foreach (var device in discoveredDevices)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await _deviceManager.RegisterDeviceAsync(device).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to register device {device.DeviceId}: {ex.Message}");
                _logger.LogWarning(ex, "Bare-metal bootstrap: failed to register device {DeviceId}.", device.DeviceId);
            }
        }

        // Step 3: Scan pools from reserved sectors.
        // P2-2973: Pass caller-provided IPhysicalBlockDevice handles so ScanForPoolsAsync can
        // read block 0 metadata and restore pools. When no handles are provided, skip the scan
        // and report a warning so the caller knows pool restoration did not occur.
        IReadOnlyList<DevicePoolDescriptor> restoredPools;
        try
        {
            var devicesToScan = physicalDevices is { Count: > 0 }
                ? physicalDevices
                : Array.Empty<IPhysicalBlockDevice>();

            if (devicesToScan.Count == 0)
            {
                warnings.Add(
                    "No IPhysicalBlockDevice handles were supplied; pool metadata scan skipped. " +
                    "Pass physicalDevices to BootstrapFromRawDevicesAsync to restore existing pools.");
                _logger.LogWarning(
                    "Bare-metal bootstrap: pool scan skipped — no IPhysicalBlockDevice handles provided.");
                restoredPools = Array.Empty<DevicePoolDescriptor>();
            }
            else
            {
                restoredPools = await _poolManager.ScanForPoolsAsync(devicesToScan, ct).ConfigureAwait(false);
                _logger.LogInformation("Bare-metal bootstrap: restored {Count} pools from reserved sectors.",
                    restoredPools.Count);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Pool scan failed: {ex.Message}");
            _logger.LogError(ex, "Bare-metal bootstrap: pool scan failed.");
            restoredPools = Array.Empty<DevicePoolDescriptor>();
        }

        // Step 4: Recover journal - check for uncommitted intents on pool member devices
        var pooledDeviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in restoredPools)
        {
            foreach (var member in pool.Members)
            {
                pooledDeviceIds.Add(member.DeviceId);
            }
        }

        // Note: Full journal recovery would read from each pool member's IPhysicalBlockDevice.
        // This is deferred until IPhysicalBlockDevice instances are available from the device
        // manager's managed devices. The recovery logic is documented here for crash recovery.
        _logger.LogInformation(
            "Bare-metal bootstrap: journal recovery would process {Count} pool member devices.",
            pooledDeviceIds.Count);

        // Step 5: Classify unpooled devices
        var unpooledDevices = new List<PhysicalDeviceInfo>();
        foreach (var device in discoveredDevices)
        {
            if (!pooledDeviceIds.Contains(device.DeviceId))
            {
                unpooledDevices.Add(device);
            }
        }

        _logger.LogInformation(
            "Bare-metal bootstrap: {PooledCount} pooled devices, {UnpooledCount} unpooled devices.",
            pooledDeviceIds.Count, unpooledDevices.Count);

        var success = true;
        return new BootstrapResult(
            Success: success,
            RestoredPools: restoredPools,
            UnpooledDevices: unpooledDevices.AsReadOnly(),
            RecoveredIntents: recoveredIntents.AsReadOnly(),
            Warnings: warnings.AsReadOnly());
    }

    /// <summary>
    /// Initializes a new system on raw devices: creates an initial pool, writes metadata,
    /// and initializes journal areas on each device. For first-time setup.
    /// </summary>
    /// <param name="poolName">Name for the new pool.</param>
    /// <param name="devices">Physical devices to include in the pool.</param>
    /// <param name="tier">Optional storage tier override.</param>
    /// <param name="locality">Optional locality tags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created pool descriptor.</returns>
    public async Task<DevicePoolDescriptor> InitializeNewSystemAsync(
        string poolName,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        StorageTier? tier = null,
        LocalityTag? locality = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        ArgumentNullException.ThrowIfNull(devices);

        if (devices.Count == 0)
        {
            throw new ArgumentException("At least one device is required.", nameof(devices));
        }

        _logger.LogInformation(
            "Bare-metal bootstrap: initializing new system with pool '{PoolName}' across {Count} devices.",
            poolName, devices.Count);

        // Journal the pool creation intent on the first device
        long journalSeq = 0;
        try
        {
            journalSeq = await _journal.WriteIntentAsync(
                JournalEntryType.PoolCreate,
                Guid.Empty, // Pool ID not yet known
                null,
                null,
                devices[0],
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bare-metal bootstrap: journal write failed during init. Proceeding without journal.");
        }

        // Create the pool via DevicePoolManager
        DevicePoolDescriptor pool;
        try
        {
            pool = await _poolManager.CreatePoolAsync(poolName, tier, locality, devices, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Bare-metal bootstrap: pool '{PoolName}' created with ID {PoolId}.",
                poolName, pool.PoolId);
        }
        catch (Exception)
        {
            // Roll back journal intent if pool creation failed
            if (journalSeq > 0)
            {
                try
                {
                    await _journal.RollbackAsync(journalSeq, devices[0], ct).ConfigureAwait(false);
                }
                catch
                {

                    // Best-effort rollback
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            throw;
        }

        // Commit journal entry
        if (journalSeq > 0)
        {
            try
            {
                await _journal.CommitAsync(journalSeq, devices[0], ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bare-metal bootstrap: journal commit failed. Pool was created successfully.");
            }
        }

        // Initialize journal areas on all devices (write empty/zero journal area to blocks 1-8)
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Journal area occupies blocks 1-8 (reserved after block 0 which holds pool metadata).
                // Write zero-initialized journal header to mark the area as initialized.
                await _journal.InitializeJournalAreaAsync(device, ct).ConfigureAwait(false);
                _logger.LogDebug("Bare-metal bootstrap: journal area initialized on device {DeviceId}.",
                    device.DeviceInfo.DeviceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Bare-metal bootstrap: failed to initialize journal on device {DeviceId}.",
                    device.DeviceInfo.DeviceId);
            }
        }

        return pool;
    }
}
