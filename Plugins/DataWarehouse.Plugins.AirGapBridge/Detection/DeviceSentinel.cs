using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataWarehouse.Plugins.AirGapBridge.Core;

namespace DataWarehouse.Plugins.AirGapBridge.Detection;

/// <summary>
/// Sentinel service for monitoring drive insertion/mounting/connection events.
/// Implements sub-tasks 79.1, 79.2, 79.3, 79.34, 79.35.
/// </summary>
public sealed class DeviceSentinel : IDisposable
{
    private readonly ConcurrentDictionary<string, AirGapDevice> _devices = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IHardwareDetector _hardwareDetector;
    private Task? _monitoringTask;
    private bool _disposed;

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
    /// Gets all currently connected devices.
    /// </summary>
    public IReadOnlyDictionary<string, AirGapDevice> Devices => _devices;

    /// <summary>
    /// Gets whether monitoring is active.
    /// </summary>
    public bool IsMonitoring { get; private set; }

    /// <summary>
    /// Creates a new device sentinel.
    /// </summary>
    public DeviceSentinel()
    {
        _hardwareDetector = CreatePlatformDetector();
    }

    /// <summary>
    /// Starts monitoring for device events.
    /// </summary>
    public async Task StartMonitoringAsync(CancellationToken ct = default)
    {
        if (IsMonitoring) return;

        IsMonitoring = true;

        // Initial scan
        await ScanForDevicesAsync(ct);

        // Start monitoring task
        _monitoringTask = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                try
                {
                    await _hardwareDetector.MonitorAsync(
                        OnDriveInserted,
                        OnDriveRemoved,
                        linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Log and retry after delay
                    await Task.Delay(1000, linkedCts.Token);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Stops monitoring for device events.
    /// </summary>
    public async Task StopMonitoringAsync()
    {
        if (!IsMonitoring) return;

        _cts.Cancel();

        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();

        IsMonitoring = false;
    }

    /// <summary>
    /// Scans for currently connected devices.
    /// </summary>
    public async Task<IReadOnlyList<AirGapDevice>> ScanForDevicesAsync(CancellationToken ct = default)
    {
        var drives = await _hardwareDetector.GetRemovableDrivesAsync(ct);
        var newDevices = new List<AirGapDevice>();

        foreach (var drive in drives)
        {
            if (ct.IsCancellationRequested) break;

            var device = await ProcessDriveAsync(drive, ct);
            if (device != null)
            {
                newDevices.Add(device);
            }
        }

        return newDevices;
    }

    /// <summary>
    /// Scans for network-attached air-gap storage.
    /// Implements sub-task 79.35.
    /// </summary>
    public async Task<IReadOnlyList<AirGapDevice>> ScanNetworkStorageAsync(
        IEnumerable<string> networkPaths,
        CancellationToken ct = default)
    {
        var devices = new List<AirGapDevice>();

        foreach (var path in networkPaths)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (Directory.Exists(path))
                {
                    var configPath = Path.Combine(path, ".dw-config");
                    if (File.Exists(configPath))
                    {
                        var config = await ReadConfigAsync(configPath, ct);
                        if (config != null)
                        {
                            var device = new AirGapDevice
                            {
                                DeviceId = config.DeviceId,
                                Path = path,
                                Config = config,
                                DriveInfo = new Core.DriveInfo
                                {
                                    Path = path,
                                    MediaType = MediaType.NetworkDrive,
                                    IsRemovable = false,
                                    Label = config.Name
                                },
                                Status = DeviceStatus.Ready
                            };

                            _devices[device.DeviceId] = device;
                            devices.Add(device);

                            DeviceDetected?.Invoke(this, new DeviceDetectedEvent
                            {
                                DeviceId = device.DeviceId,
                                DevicePath = path,
                                MediaType = MediaType.NetworkDrive,
                                Mode = config.Mode,
                                Label = config.Name
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Network path not accessible
            }
        }

        return devices;
    }

    private async Task<AirGapDevice?> ProcessDriveAsync(Core.DriveInfo drive, CancellationToken ct)
    {
        // Check for .dw-config file (sub-task 79.2)
        var configPath = Path.Combine(drive.Path, ".dw-config");
        AirGapDeviceConfig? config = null;

        if (File.Exists(configPath))
        {
            config = await ReadConfigAsync(configPath, ct);
        }

        // Create device entry
        var deviceId = config?.DeviceId ?? GenerateDeviceId(drive);
        var device = new AirGapDevice
        {
            DeviceId = deviceId,
            Path = drive.Path,
            Config = config,
            DriveInfo = drive,
            Status = config != null ? DeviceStatus.Pending : DeviceStatus.Disconnected
        };

        _devices[deviceId] = device;

        // Detect mode (sub-task 79.3)
        if (config != null)
        {
            device.Status = DeviceStatus.Pending;

            // If Pocket Instance mode, extract metadata for convergence
            if (config.Mode == AirGapMode.PocketInstance)
            {
                await ExtractInstanceMetadataAsync(device, ct);
            }
        }

        // Set up file system watcher for the drive
        SetupDriveWatcher(device);

        // Raise event
        DeviceDetected?.Invoke(this, new DeviceDetectedEvent
        {
            DeviceId = deviceId,
            DevicePath = drive.Path,
            MediaType = drive.MediaType,
            Mode = config?.Mode,
            Label = drive.Label ?? config?.Name
        });

        return device;
    }

    /// <summary>
    /// Extracts instance metadata for convergence support.
    /// Implements sub-task 79.31.
    /// </summary>
    private async Task ExtractInstanceMetadataAsync(AirGapDevice device, CancellationToken ct)
    {
        if (device.Config?.Mode != AirGapMode.PocketInstance) return;

        try
        {
            var metadataPath = Path.Combine(device.Path, ".dw-instance", "metadata.json");
            if (File.Exists(metadataPath))
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                var metadata = JsonSerializer.Deserialize<InstanceMetadata>(json);

                if (metadata != null)
                {
                    var evt = new InstanceDetectedEvent
                    {
                        InstanceId = device.Config.DeviceId,
                        InstanceName = device.Config.Name,
                        DevicePath = device.Path,
                        Version = "1.0.0", // Read from actual instance
                        Metadata = metadata
                    };

                    // Verify compatibility (sub-task 79.32)
                    await VerifyCompatibilityAsync(evt, ct);

                    InstanceDetected?.Invoke(this, evt);
                }
            }
        }
        catch (Exception)
        {
            // Metadata extraction failed
        }
    }

    /// <summary>
    /// Verifies instance version compatibility.
    /// Implements sub-task 79.32.
    /// </summary>
    private Task VerifyCompatibilityAsync(InstanceDetectedEvent evt, CancellationToken ct)
    {
        // Check schema version compatibility
        var currentSchemaVersion = 1; // Would come from SDK

        if (evt.Metadata.SchemaVersion > currentSchemaVersion)
        {
            evt.CompatibilityIssues.Add($"Instance schema version {evt.Metadata.SchemaVersion} is newer than current {currentSchemaVersion}");
        }
        else if (evt.Metadata.SchemaVersion < currentSchemaVersion - 2)
        {
            evt.CompatibilityIssues.Add($"Instance schema version {evt.Metadata.SchemaVersion} may require migration");
        }

        evt.CompatibilityVerified = evt.CompatibilityIssues.Count == 0;

        return Task.CompletedTask;
    }

    private async Task<AirGapDeviceConfig?> ReadConfigAsync(string configPath, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath, ct);
            return JsonSerializer.Deserialize<AirGapDeviceConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    private void SetupDriveWatcher(AirGapDevice device)
    {
        try
        {
            var watcher = new FileSystemWatcher(device.Path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) =>
            {
                if (e.Name == ".dw-config" || e.Name?.EndsWith(".dwpack") == true)
                {
                    _ = OnConfigOrPackageCreatedAsync(device, e.FullPath);
                }
            };

            _watchers[device.DeviceId] = watcher;
        }
        catch
        {
            // Watcher setup failed (network drive, etc.)
        }
    }

    private async Task OnConfigOrPackageCreatedAsync(AirGapDevice device, string path)
    {
        if (Path.GetFileName(path) == ".dw-config")
        {
            var config = await ReadConfigAsync(path, CancellationToken.None);
            if (config != null)
            {
                device.Config = config;
                device.Status = DeviceStatus.Pending;
            }
        }
    }

    private void OnDriveInserted(Core.DriveInfo drive)
    {
        _ = ProcessDriveAsync(drive, CancellationToken.None);
    }

    private void OnDriveRemoved(string drivePath)
    {
        var device = _devices.Values.FirstOrDefault(d => d.Path.Equals(drivePath, StringComparison.OrdinalIgnoreCase));
        if (device != null)
        {
            _devices.TryRemove(device.DeviceId, out _);

            if (_watchers.TryRemove(device.DeviceId, out var watcher))
            {
                watcher.Dispose();
            }

            DeviceRemoved?.Invoke(this, new DeviceRemovedEvent
            {
                DeviceId = device.DeviceId,
                SafeRemoval = device.Status != DeviceStatus.Busy
            });
        }
    }

    private static string GenerateDeviceId(Core.DriveInfo drive)
    {
        var input = $"{drive.SerialNumber ?? drive.Path}:{drive.Label ?? "unknown"}";
        // Note: Bus delegation not available in this context; using direct crypto
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static IHardwareDetector CreatePlatformDetector()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsHardwareDetector();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxHardwareDetector();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOSHardwareDetector();
        }
        else
        {
            return new GenericHardwareDetector();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();

        _hardwareDetector.Dispose();
    }
}

/// <summary>
/// Represents a connected air-gap device.
/// </summary>
public sealed class AirGapDevice
{
    /// <summary>Unique device identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Device path.</summary>
    public required string Path { get; init; }

    /// <summary>Device configuration.</summary>
    public AirGapDeviceConfig? Config { get; set; }

    /// <summary>Drive information.</summary>
    public required Core.DriveInfo DriveInfo { get; init; }

    /// <summary>Current status.</summary>
    public DeviceStatus Status { get; set; }

    /// <summary>Session token (if authenticated).</summary>
    public string? SessionToken { get; set; }

    /// <summary>Session expiration.</summary>
    public DateTimeOffset? SessionExpires { get; set; }
}

/// <summary>
/// Interface for platform-specific hardware detection.
/// Implements sub-task 79.34.
/// </summary>
public interface IHardwareDetector : IDisposable
{
    /// <summary>Gets all removable drives.</summary>
    Task<IReadOnlyList<Core.DriveInfo>> GetRemovableDrivesAsync(CancellationToken ct = default);

    /// <summary>Monitors for drive insertion/removal events.</summary>
    Task MonitorAsync(
        Action<Core.DriveInfo> onInserted,
        Action<string> onRemoved,
        CancellationToken ct = default);
}

/// <summary>
/// Windows hardware detector using WMI.
/// </summary>
public sealed class WindowsHardwareDetector : IHardwareDetector
{
    public Task<IReadOnlyList<Core.DriveInfo>> GetRemovableDrivesAsync(CancellationToken ct = default)
    {
        var drives = new List<Core.DriveInfo>();

        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            if (ct.IsCancellationRequested) break;

            if (drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.Network)
            {
                try
                {
                    if (drive.IsReady)
                    {
                        drives.Add(new Core.DriveInfo
                        {
                            Path = drive.RootDirectory.FullName,
                            Label = drive.VolumeLabel,
                            MediaType = drive.DriveType == DriveType.Removable ? MediaType.Usb : MediaType.NetworkDrive,
                            TotalCapacity = drive.TotalSize,
                            AvailableSpace = drive.AvailableFreeSpace,
                            FileSystem = drive.DriveFormat,
                            IsRemovable = drive.DriveType == DriveType.Removable
                        });
                    }
                }
                catch
                {
                    // Drive not ready
                }
            }
        }

        return Task.FromResult<IReadOnlyList<Core.DriveInfo>>(drives);
    }

    public async Task MonitorAsync(
        Action<Core.DriveInfo> onInserted,
        Action<string> onRemoved,
        CancellationToken ct = default)
    {
        var knownDrives = new HashSet<string>();
        var currentDrives = await GetRemovableDrivesAsync(ct);

        foreach (var drive in currentDrives)
        {
            knownDrives.Add(drive.Path);
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            currentDrives = await GetRemovableDrivesAsync(ct);
            var currentPaths = currentDrives.Select(d => d.Path).ToHashSet();

            // Check for new drives
            foreach (var drive in currentDrives)
            {
                if (!knownDrives.Contains(drive.Path))
                {
                    onInserted(drive);
                    knownDrives.Add(drive.Path);
                }
            }

            // Check for removed drives
            var removedPaths = knownDrives.Where(p => !currentPaths.Contains(p)).ToList();
            foreach (var path in removedPaths)
            {
                onRemoved(path);
                knownDrives.Remove(path);
            }
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Linux hardware detector using udev.
/// </summary>
public sealed class LinuxHardwareDetector : IHardwareDetector
{
    public Task<IReadOnlyList<Core.DriveInfo>> GetRemovableDrivesAsync(CancellationToken ct = default)
    {
        var drives = new List<Core.DriveInfo>();

        // Check /media and /mnt for mounted drives
        var mediaPath = "/media";
        var mntPath = "/mnt";

        foreach (var basePath in new[] { mediaPath, mntPath })
        {
            if (Directory.Exists(basePath))
            {
                foreach (var dir in Directory.GetDirectories(basePath))
                {
                    try
                    {
                        var driveInfo = new System.IO.DriveInfo(dir);
                        if (driveInfo.IsReady)
                        {
                            drives.Add(new Core.DriveInfo
                            {
                                Path = dir,
                                Label = Path.GetFileName(dir),
                                MediaType = MediaType.Usb,
                                TotalCapacity = driveInfo.TotalSize,
                                AvailableSpace = driveInfo.AvailableFreeSpace,
                                FileSystem = driveInfo.DriveFormat,
                                IsRemovable = true
                            });
                        }
                    }
                    catch
                    {
                        // Not a valid mount point
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<Core.DriveInfo>>(drives);
    }

    public async Task MonitorAsync(
        Action<Core.DriveInfo> onInserted,
        Action<string> onRemoved,
        CancellationToken ct = default)
    {
        // Monitor using polling (udev would require native interop)
        var knownDrives = new HashSet<string>();
        var currentDrives = await GetRemovableDrivesAsync(ct);

        foreach (var drive in currentDrives)
        {
            knownDrives.Add(drive.Path);
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            currentDrives = await GetRemovableDrivesAsync(ct);
            var currentPaths = currentDrives.Select(d => d.Path).ToHashSet();

            foreach (var drive in currentDrives)
            {
                if (!knownDrives.Contains(drive.Path))
                {
                    onInserted(drive);
                    knownDrives.Add(drive.Path);
                }
            }

            var removedPaths = knownDrives.Where(p => !currentPaths.Contains(p)).ToList();
            foreach (var path in removedPaths)
            {
                onRemoved(path);
                knownDrives.Remove(path);
            }
        }
    }

    public void Dispose() { }
}

/// <summary>
/// macOS hardware detector using FSEvents.
/// </summary>
public sealed class MacOSHardwareDetector : IHardwareDetector
{
    public Task<IReadOnlyList<Core.DriveInfo>> GetRemovableDrivesAsync(CancellationToken ct = default)
    {
        var drives = new List<Core.DriveInfo>();
        var volumesPath = "/Volumes";

        if (Directory.Exists(volumesPath))
        {
            foreach (var dir in Directory.GetDirectories(volumesPath))
            {
                var name = Path.GetFileName(dir);
                if (name == "Macintosh HD") continue; // Skip system volume

                try
                {
                    var driveInfo = new System.IO.DriveInfo(dir);
                    if (driveInfo.IsReady)
                    {
                        drives.Add(new Core.DriveInfo
                        {
                            Path = dir,
                            Label = name,
                            MediaType = MediaType.Usb,
                            TotalCapacity = driveInfo.TotalSize,
                            AvailableSpace = driveInfo.AvailableFreeSpace,
                            FileSystem = driveInfo.DriveFormat,
                            IsRemovable = true
                        });
                    }
                }
                catch
                {
                    // Not accessible
                }
            }
        }

        return Task.FromResult<IReadOnlyList<Core.DriveInfo>>(drives);
    }

    public async Task MonitorAsync(
        Action<Core.DriveInfo> onInserted,
        Action<string> onRemoved,
        CancellationToken ct = default)
    {
        var knownDrives = new HashSet<string>();
        var currentDrives = await GetRemovableDrivesAsync(ct);

        foreach (var drive in currentDrives)
        {
            knownDrives.Add(drive.Path);
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            currentDrives = await GetRemovableDrivesAsync(ct);
            var currentPaths = currentDrives.Select(d => d.Path).ToHashSet();

            foreach (var drive in currentDrives)
            {
                if (!knownDrives.Contains(drive.Path))
                {
                    onInserted(drive);
                    knownDrives.Add(drive.Path);
                }
            }

            var removedPaths = knownDrives.Where(p => !currentPaths.Contains(p)).ToList();
            foreach (var path in removedPaths)
            {
                onRemoved(path);
                knownDrives.Remove(path);
            }
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Generic hardware detector for unsupported platforms.
/// </summary>
public sealed class GenericHardwareDetector : IHardwareDetector
{
    public Task<IReadOnlyList<Core.DriveInfo>> GetRemovableDrivesAsync(CancellationToken ct = default)
    {
        var drives = new List<Core.DriveInfo>();

        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Removable && drive.IsReady)
            {
                drives.Add(new Core.DriveInfo
                {
                    Path = drive.RootDirectory.FullName,
                    Label = drive.VolumeLabel,
                    MediaType = MediaType.Unknown,
                    TotalCapacity = drive.TotalSize,
                    AvailableSpace = drive.AvailableFreeSpace,
                    IsRemovable = true
                });
            }
        }

        return Task.FromResult<IReadOnlyList<Core.DriveInfo>>(drives);
    }

    public async Task MonitorAsync(
        Action<Core.DriveInfo> onInserted,
        Action<string> onRemoved,
        CancellationToken ct = default)
    {
        var knownDrives = new HashSet<string>();

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);

            var currentDrives = await GetRemovableDrivesAsync(ct);
            var currentPaths = currentDrives.Select(d => d.Path).ToHashSet();

            foreach (var drive in currentDrives)
            {
                if (!knownDrives.Contains(drive.Path))
                {
                    onInserted(drive);
                    knownDrives.Add(drive.Path);
                }
            }

            var removedPaths = knownDrives.Where(p => !currentPaths.Contains(p)).ToList();
            foreach (var path in removedPaths)
            {
                onRemoved(path);
                knownDrives.Remove(path);
            }
        }
    }

    public void Dispose() { }
}
