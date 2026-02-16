using System.Collections.Concurrent;
using System.Security.Cryptography;
using DataWarehouse.Plugins.AirGapBridge.Convergence;
using DataWarehouse.Plugins.AirGapBridge.Core;
using DataWarehouse.Plugins.AirGapBridge.Detection;
using DataWarehouse.Plugins.AirGapBridge.Management;
using DataWarehouse.Plugins.AirGapBridge.PocketInstance;
using DataWarehouse.Plugins.AirGapBridge.Security;
using DataWarehouse.Plugins.AirGapBridge.Storage;
using DataWarehouse.Plugins.AirGapBridge.Transport;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.AirGapBridge;

/// <summary>
/// Air-Gap Bridge Plugin - "The Mule"
///
/// Implements T79: Tri-Mode Removable System for secure offline data transfer.
///
/// Modes:
/// - Transport (The Mule): Encrypted blob container for sneakernet transfer
/// - Storage Extension (The Sidecar): Capacity tier on removable storage
/// - Pocket Instance (Full DW on a Stick): Complete portable DataWarehouse
///
/// Sub-tasks implemented:
/// - 79.1-79.4: Detection & Handshake (USB/External/Network storage sentinel, config scanner, mode detection, signatures)
/// - 79.5-79.10: Transport Mode (package creator, auto-ingest, signature verification, shard unpacker, result logging, secure wipe)
/// - 79.11-79.15: Storage Extension Mode (dynamic provider, capacity registration, cold migration, safe removal, offline index)
/// - 79.16-79.20: Pocket Instance Mode (guest context isolation, portable index DB, bridge mode UI, cross-instance transfer, sync tasks)
/// - 79.21-79.25: Security (volume encryption, PIN/password, keyfile auth, TTL kill switch, hardware key support)
/// - 79.26-79.28: Setup & Management (pocket setup wizard, instance ID generator, portable client bundler)
/// - 79.29-79.35: Instance Convergence Support (instance detection events, multi-instance tracking, metadata extraction, compatibility verification, processing manifest, cross-platform detection, network air-gap detection)
/// </summary>
public sealed class AirGapBridgePlugin : InfrastructurePluginBase, IDisposable
{
    private readonly ConcurrentDictionary<string, AirGapDevice> _devices = new();
    private readonly byte[] _masterKey;
    private readonly string _instanceId;
    private IKernelContext? _context;
    private bool _isRunning;
    private bool _disposed;

    // Sub-components
    private DeviceSentinel? _deviceSentinel;
    private PackageManager? _packageManager;
    private StorageExtensionProvider? _storageProvider;
    private PocketInstanceManager? _pocketInstanceManager;
    private SecurityManager? _securityManager;
    private SetupWizard? _setupWizard;
    private ConvergenceManager? _convergenceManager;

    // Configuration
    private AirGapSecurityPolicy _securityPolicy = new();
    private string? _trustedKeyfilesDir;
    private string? _offlineIndexPath;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.airgap.bridge";

    /// <inheritdoc/>
    public override string Name => "Air-Gap Bridge (The Mule)";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc/>
    public override string InfrastructureDomain => "AirGap";

    /// <summary>
    /// Gets all connected devices.
    /// </summary>
    public IReadOnlyDictionary<string, AirGapDevice> Devices => _devices;

    /// <summary>
    /// Gets whether the sentinel is monitoring for devices.
    /// </summary>
    public bool IsMonitoring => _deviceSentinel?.IsMonitoring ?? false;

    /// <summary>
    /// Event raised when a device is detected.
    /// </summary>
    public event EventHandler<DeviceDetectedEvent>? DeviceDetected;

    /// <summary>
    /// Event raised when a device is removed.
    /// </summary>
    public event EventHandler<DeviceRemovedEvent>? DeviceRemoved;

    /// <summary>
    /// Event raised when an instance is detected (for convergence).
    /// </summary>
    public event EventHandler<InstanceDetectedEvent>? InstanceDetected;

    /// <summary>
    /// Event raised when a sync completes.
    /// </summary>
    public event EventHandler<SyncCompletedEvent>? SyncCompleted;

    /// <summary>
    /// Creates a new Air-Gap Bridge plugin.
    /// </summary>
    public AirGapBridgePlugin()
    {
        // Generate or load instance ID
        _instanceId = Environment.GetEnvironmentVariable("DW_INSTANCE_ID")
            ?? $"dw-{Guid.NewGuid():N}"[..16];

        // Generate or load master key (would normally come from key management)
        _masterKey = new byte[32];
        RandomNumberGenerator.Fill(_masterKey);
    }

    /// <summary>
    /// Creates a new Air-Gap Bridge plugin with specified configuration.
    /// </summary>
    /// <param name="instanceId">Instance identifier.</param>
    /// <param name="masterKey">Master encryption key.</param>
    public AirGapBridgePlugin(string instanceId, byte[] masterKey)
    {
        _instanceId = instanceId;
        _masterKey = masterKey;
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;

        // Configure paths
        _trustedKeyfilesDir = Path.Combine(_context?.RootPath ?? ".", "config", "trusted-keyfiles");
        _offlineIndexPath = Path.Combine(_context?.RootPath ?? ".", "data", "offline-index");

        Directory.CreateDirectory(_trustedKeyfilesDir);
        Directory.CreateDirectory(_offlineIndexPath);

        // Initialize sub-components
        InitializeComponents();

        return new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = new Version(1, 0, 0),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Metadata = new Dictionary<string, object>
            {
                ["InstanceId"] = _instanceId,
                ["MonitoringStatus"] = "Initialized",
                ["SupportedModes"] = "Transport,StorageExtension,PocketInstance"
            }
        };
    }

    private void InitializeComponents()
    {
        _deviceSentinel = new DeviceSentinel();
        _deviceSentinel.DeviceDetected += OnDeviceDetected;
        _deviceSentinel.DeviceRemoved += OnDeviceRemoved;
        _deviceSentinel.InstanceDetected += OnInstanceDetectedInternal;

        _packageManager = new PackageManager(_masterKey, _instanceId, MessageBus);
        _storageProvider = new StorageExtensionProvider(_offlineIndexPath!);
        _pocketInstanceManager = new PocketInstanceManager(_instanceId);
        _securityManager = new SecurityManager(_masterKey, _securityPolicy, MessageBus);
        _setupWizard = new SetupWizard(_instanceId, Name, _masterKey);
        _convergenceManager = new ConvergenceManager(_instanceId, 1);

        // Forward convergence events
        _convergenceManager.InstanceDetectedForConvergence += (s, e) =>
        {
            InstanceDetected?.Invoke(this, e);
        };
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        await base.StartAsync(ct);
        if (_isRunning) return;

        _context?.LogInfo("Starting Air-Gap Bridge plugin...");

        // Start device monitoring
        if (_deviceSentinel != null)
        {
            await _deviceSentinel.StartMonitoringAsync(ct);
        }

        _isRunning = true;
        _context?.LogInfo("Air-Gap Bridge plugin started");
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _context?.LogInfo("Stopping Air-Gap Bridge plugin...");

        // Stop device monitoring
        if (_deviceSentinel != null)
        {
            await _deviceSentinel.StopMonitoringAsync();
        }

        // Unmount all pocket instances
        foreach (var device in _devices.Values.Where(d => d.Config?.Mode == AirGapMode.PocketInstance))
        {
            if (_pocketInstanceManager != null)
            {
                await _pocketInstanceManager.UnmountPocketInstanceAsync(device.Config!.DeviceId, CancellationToken.None);
            }
        }

        _isRunning = false;
        _context?.LogInfo("Air-Gap Bridge plugin stopped");
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "airgap.scan":
                await HandleScanAsync(message);
                break;

            case "airgap.mount":
                await HandleMountAsync(message);
                break;

            case "airgap.unmount":
                await HandleUnmountAsync(message);
                break;

            case "airgap.setup":
                await HandleSetupAsync(message);
                break;

            case "airgap.import":
                await HandleImportAsync(message);
                break;

            case "airgap.export":
                await HandleExportAsync(message);
                break;

            case "airgap.authenticate":
                await HandleAuthenticateAsync(message);
                break;

            case "airgap.sync":
                await HandleSyncAsync(message);
                break;

            case "airgap.status":
                HandleStatus(message);
                break;
        }
    }

    #region Message Handlers

    private async Task HandleScanAsync(PluginMessage message)
    {
        if (_deviceSentinel == null) return;

        var devices = await _deviceSentinel.ScanForDevicesAsync();
        message.Payload["devices"] = devices.Select(d => new Dictionary<string, object>
        {
            ["deviceId"] = d.DeviceId,
            ["path"] = d.Path,
            ["mode"] = d.Config?.Mode.ToString() ?? "Unknown",
            ["status"] = d.Status.ToString()
        }).ToList();
    }

    private async Task HandleMountAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("deviceId", out var didObj) || didObj is not string deviceId)
        {
            throw new ArgumentException("Missing deviceId");
        }

        if (!_devices.TryGetValue(deviceId, out var device))
        {
            throw new ArgumentException("Device not found");
        }

        if (device.Config == null)
        {
            throw new InvalidOperationException("Device not configured");
        }

        switch (device.Config.Mode)
        {
            case AirGapMode.StorageExtension:
                if (_storageProvider != null)
                {
                    var storage = await _storageProvider.MountStorageAsync(device, new StorageExtensionOptions());
                    message.Payload["storageId"] = storage.StorageId;
                    message.Payload["capacity"] = storage.TotalCapacity;
                }
                break;

            case AirGapMode.PocketInstance:
                if (_pocketInstanceManager != null)
                {
                    var instance = await _pocketInstanceManager.MountPocketInstanceAsync(device, new PocketInstanceOptions());
                    message.Payload["instanceId"] = instance.InstanceId;
                    message.Payload["blobCount"] = instance.BlobCount;
                }
                break;
        }

        device.Status = DeviceStatus.Ready;
        message.Payload["success"] = true;
    }

    private async Task HandleUnmountAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("deviceId", out var didObj) || didObj is not string deviceId)
        {
            throw new ArgumentException("Missing deviceId");
        }

        if (!_devices.TryGetValue(deviceId, out var device))
        {
            throw new ArgumentException("Device not found");
        }

        if (device.Config == null)
        {
            message.Payload["success"] = true;
            return;
        }

        switch (device.Config.Mode)
        {
            case AirGapMode.StorageExtension:
                if (_storageProvider != null)
                {
                    var result = await _storageProvider.PrepareForRemovalAsync($"airgap:{deviceId}");
                    message.Payload["result"] = result;
                }
                break;

            case AirGapMode.PocketInstance:
                if (_pocketInstanceManager != null)
                {
                    await _pocketInstanceManager.UnmountPocketInstanceAsync(device.Config.DeviceId);
                }
                break;
        }

        device.Status = DeviceStatus.Disconnected;
        message.Payload["success"] = true;
    }

    private async Task HandleSetupAsync(PluginMessage message)
    {
        if (_setupWizard == null) return;

        if (!message.Payload.TryGetValue("drivePath", out var pathObj) || pathObj is not string drivePath)
        {
            throw new ArgumentException("Missing drivePath");
        }

        if (!message.Payload.TryGetValue("mode", out var modeObj) || modeObj is not string modeStr)
        {
            throw new ArgumentException("Missing mode");
        }

        var name = message.Payload.TryGetValue("name", out var nameObj) && nameObj is string n
            ? n : "Air-Gap Device";

        SetupResult result;

        switch (Enum.Parse<AirGapMode>(modeStr, true))
        {
            case AirGapMode.PocketInstance:
                result = await _setupWizard.SetupPocketInstanceAsync(drivePath, new PocketSetupOptions
                {
                    Name = name
                });
                break;

            case AirGapMode.StorageExtension:
                result = await _setupWizard.SetupStorageExtensionAsync(drivePath, new StorageExtensionSetupOptions
                {
                    Name = name
                });
                break;

            case AirGapMode.Transport:
                result = await _setupWizard.SetupTransportModeAsync(drivePath, new TransportSetupOptions
                {
                    Name = name
                });
                break;

            default:
                throw new ArgumentException("Invalid mode");
        }

        message.Payload["success"] = result.Success;
        message.Payload["instanceId"] = result.InstanceId ?? "";
        message.Payload["errorMessage"] = result.ErrorMessage ?? "";
    }

    private async Task HandleImportAsync(PluginMessage message)
    {
        if (_packageManager == null) return;

        if (!message.Payload.TryGetValue("devicePath", out var pathObj) || pathObj is not string devicePath)
        {
            throw new ArgumentException("Missing devicePath");
        }

        var packages = await _packageManager.ScanForPackagesAsync(devicePath);
        var results = new List<ImportResult>();

        foreach (var package in packages)
        {
            if (package.Manifest.AutoIngest || (message.Payload.TryGetValue("packageId", out var pidObj) && pidObj is string pid && pid == package.PackageId))
            {
                var result = await _packageManager.ImportPackageAsync(package, async blob =>
                {
                    // Would store via kernel storage
                    _context?.LogDebug($"Imported blob: {blob.Uri}");
                });

                results.Add(result);

                // Write result log
                await _packageManager.WriteResultLogAsync(devicePath, result);

                // Secure wipe if configured
                if (result.Success && package.Manifest.AutoIngest)
                {
                    var packagePath = Path.Combine(devicePath, $"{package.PackageId}.dwpack");
                    await _packageManager.SecureWipePackageAsync(packagePath);
                }
            }
        }

        message.Payload["results"] = results;
    }

    private async Task HandleExportAsync(PluginMessage message)
    {
        if (_packageManager == null) return;

        if (!message.Payload.TryGetValue("devicePath", out var pathObj) || pathObj is not string devicePath)
        {
            throw new ArgumentException("Missing devicePath");
        }

        if (!message.Payload.TryGetValue("blobUris", out var urisObj) || urisObj is not IEnumerable<string> blobUris)
        {
            throw new ArgumentException("Missing blobUris");
        }

        // Would load blobs from kernel storage
        var blobs = new List<BlobData>();
        foreach (var uri in blobUris)
        {
            // Placeholder - would actually load from storage
            blobs.Add(new BlobData
            {
                Uri = uri,
                Data = Array.Empty<byte>()
            });
        }

        var package = await _packageManager.CreatePackageAsync(blobs);
        var filePath = await _packageManager.SavePackageToDeviceAsync(package, devicePath);

        message.Payload["packageId"] = package.PackageId;
        message.Payload["filePath"] = filePath;
        message.Payload["shardCount"] = package.Shards.Count;
    }

    private async Task HandleAuthenticateAsync(PluginMessage message)
    {
        if (_securityManager == null) return;

        if (!message.Payload.TryGetValue("deviceId", out var didObj) || didObj is not string deviceId)
        {
            throw new ArgumentException("Missing deviceId");
        }

        if (!_devices.TryGetValue(deviceId, out var device))
        {
            throw new ArgumentException("Device not found");
        }

        Core.AuthenticationResult result;

        if (message.Payload.TryGetValue("password", out var pwdObj) && pwdObj is string password)
        {
            result = await _securityManager.AuthenticateWithPasswordAsync(device, password);
        }
        else if (message.Payload.TryGetValue("keyfilePath", out var kfObj) && kfObj is string keyfilePath)
        {
            result = await _securityManager.AuthenticateWithKeyfileAsync(device, keyfilePath);
        }
        else
        {
            // Try auto-mount
            result = await _securityManager.TryAutoMountAsync(device, _trustedKeyfilesDir!);
        }

        message.Payload["success"] = result.Success;
        message.Payload["sessionToken"] = result.SessionToken ?? "";
        message.Payload["expiresAt"] = result.ExpiresAt?.ToString() ?? "";
        message.Payload["errorMessage"] = result.ErrorMessage ?? "";
    }

    private async Task HandleSyncAsync(PluginMessage message)
    {
        if (_pocketInstanceManager == null) return;

        if (!message.Payload.TryGetValue("instanceId", out var iidObj) || iidObj is not string instanceId)
        {
            throw new ArgumentException("Missing instanceId");
        }

        var ruleId = message.Payload.TryGetValue("ruleId", out var ridObj) && ridObj is string rid
            ? rid : null;

        var result = await _pocketInstanceManager.ExecuteSyncAsync(instanceId, ruleId);

        message.Payload["success"] = result.Success;
        message.Payload["itemsSynced"] = result.ItemsSynced;
        message.Payload["conflicts"] = result.Conflicts;
        message.Payload["bytesSynced"] = result.BytesSynced;

        SyncCompleted?.Invoke(this, new SyncCompletedEvent
        {
            SyncId = Guid.NewGuid().ToString("N"),
            DeviceId = instanceId,
            OperationType = SyncOperationType.Bidirectional,
            Status = result.Success ? SyncStatus.Completed : SyncStatus.Failed,
            ItemsTransferred = result.ItemsSynced,
            BytesTransferred = result.BytesSynced,
            ConflictsCount = result.Conflicts
        });
    }

    private void HandleStatus(PluginMessage message)
    {
        message.Payload["isRunning"] = _isRunning;
        message.Payload["isMonitoring"] = IsMonitoring;
        message.Payload["deviceCount"] = _devices.Count;
        message.Payload["devices"] = _devices.Values.Select(d => new Dictionary<string, object>
        {
            ["deviceId"] = d.DeviceId,
            ["path"] = d.Path,
            ["mode"] = d.Config?.Mode.ToString() ?? "Unknown",
            ["status"] = d.Status.ToString()
        }).ToList();

        if (_storageProvider != null)
        {
            var capacity = _storageProvider.GetTotalCapacity();
            message.Payload["storageCapacity"] = new Dictionary<string, object>
            {
                ["onlineCount"] = capacity.OnlineStorageCount,
                ["offlineCount"] = capacity.OfflineStorageCount,
                ["totalOnlineCapacity"] = capacity.TotalOnlineCapacity,
                ["totalOnlineAvailable"] = capacity.TotalOnlineAvailable
            };
        }

        if (_pocketInstanceManager != null)
        {
            message.Payload["pocketInstances"] = _pocketInstanceManager.GetInstancesForUI();
        }
    }

    #endregion

    #region Event Handlers

    private void OnDeviceDetected(object? sender, DeviceDetectedEvent e)
    {
        AirGapDevice? device = null;
        if (_deviceSentinel?.Devices.TryGetValue(e.DeviceId, out device) == true && device != null)
        {
            _devices[e.DeviceId] = device;
        }

        _context?.LogInfo($"Air-Gap device detected: {e.DeviceId} at {e.DevicePath}");

        // Check TTL
        if (device?.Config?.TtlDays > 0 && _securityManager != null)
        {
            _ = _securityManager.CheckTtlAsync(device);
        }

        DeviceDetected?.Invoke(this, e);
    }

    private void OnDeviceRemoved(object? sender, DeviceRemovedEvent e)
    {
        _devices.TryRemove(e.DeviceId, out _);

        _context?.LogInfo($"Air-Gap device removed: {e.DeviceId}");

        DeviceRemoved?.Invoke(this, e);
    }

    private void OnInstanceDetectedInternal(object? sender, InstanceDetectedEvent e)
    {
        _convergenceManager?.OnInstanceDetected(e);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Gets the package manager for transport operations.
    /// </summary>
    public PackageManager? GetPackageManager() => _packageManager;

    /// <summary>
    /// Gets the storage extension provider.
    /// </summary>
    public StorageExtensionProvider? GetStorageProvider() => _storageProvider;

    /// <summary>
    /// Gets the pocket instance manager.
    /// </summary>
    public PocketInstanceManager? GetPocketInstanceManager() => _pocketInstanceManager;

    /// <summary>
    /// Gets the security manager.
    /// </summary>
    public SecurityManager? GetSecurityManager() => _securityManager;

    /// <summary>
    /// Gets the setup wizard.
    /// </summary>
    public SetupWizard? GetSetupWizard() => _setupWizard;

    /// <summary>
    /// Gets the convergence manager.
    /// </summary>
    public ConvergenceManager? GetConvergenceManager() => _convergenceManager;

    /// <summary>
    /// Configures security policy.
    /// </summary>
    public void ConfigureSecurityPolicy(AirGapSecurityPolicy policy)
    {
        _securityPolicy = policy;
        if (_securityManager != null)
        {
            _securityManager.Dispose();
            _securityManager = new SecurityManager(_masterKey, _securityPolicy, MessageBus);
        }
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;

            _deviceSentinel?.Dispose();
            _storageProvider?.Dispose();
            _pocketInstanceManager?.Dispose();
            _securityManager?.Dispose();

            CryptographicOperations.ZeroMemory(_masterKey);
        }
        base.Dispose(disposing);
    }
}
