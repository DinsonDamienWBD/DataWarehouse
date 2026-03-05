using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Options for filtering and controlling device discovery behavior.
/// </summary>
/// <param name="IncludeRemovable">Whether to include removable devices (USB sticks, etc.).</param>
/// <param name="IncludeVirtual">Whether to include virtual block devices (VirtIO, loop, etc.).</param>
/// <param name="FilterMediaType">If set, only return devices of this media type.</param>
/// <param name="FilterBusType">If set, only return devices of this bus type.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device discovery options (BMDV-01/BMDV-02)")]
public sealed record DeviceDiscoveryOptions(
    bool IncludeRemovable = false,
    bool IncludeVirtual = true,
    MediaType? FilterMediaType = null,
    BusType? FilterBusType = null);

/// <summary>
/// Cross-platform device enumeration service that discovers physical block devices
/// on Linux (via /sys/block and sysfs) and Windows (via WMI Win32_DiskDrive).
/// Returns <see cref="PhysicalDeviceInfo"/> records with serial, model, media type,
/// bus type, sector sizes, TRIM support, NUMA node, and controller path for each device.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Cross-platform device discovery (BMDV-01/BMDV-02)")]
public sealed class DeviceDiscoveryService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DeviceDiscoveryService"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics. Uses NullLogger if not provided.</param>
    public DeviceDiscoveryService(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Discovers all physical block devices on the current platform.
    /// Delegates to platform-specific implementation based on runtime OS detection.
    /// </summary>
    /// <param name="options">Discovery filter options. Uses defaults if null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered physical device information records.</returns>
    public async Task<IReadOnlyList<PhysicalDeviceInfo>> DiscoverDevicesAsync(
        DeviceDiscoveryOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DeviceDiscoveryOptions();

        IReadOnlyList<PhysicalDeviceInfo> devices;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            devices = await DiscoverLinuxDevicesAsync(options, ct).ConfigureAwait(false);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            devices = await DiscoverWindowsDevicesAsync(options, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Device discovery is not supported on platform {Platform}. Returning empty list.",
                RuntimeInformation.OSDescription);
            devices = Array.Empty<PhysicalDeviceInfo>();
        }

        // Apply filters
        var filtered = devices.AsEnumerable();

        if (options.FilterMediaType.HasValue)
        {
            filtered = filtered.Where(d => d.MediaType == options.FilterMediaType.Value);
        }

        if (options.FilterBusType.HasValue)
        {
            filtered = filtered.Where(d => d.BusType == options.FilterBusType.Value);
        }

        return filtered.ToList().AsReadOnly();
    }

    /// <summary>
    /// Looks up a single device by its OS-specific device path.
    /// </summary>
    /// <param name="devicePath">Device path (e.g., /dev/nvme0n1 or \\.\PhysicalDrive0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Device info if found, null otherwise.</returns>
    public async Task<PhysicalDeviceInfo?> GetDeviceAsync(string devicePath, CancellationToken ct = default)
    {
        var allDevices = await DiscoverDevicesAsync(new DeviceDiscoveryOptions(IncludeRemovable: true, IncludeVirtual: true), ct)
            .ConfigureAwait(false);

        return allDevices.FirstOrDefault(d =>
            string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));
    }

    // ========================================================================
    // Linux implementation: enumerate via /sys/block/ sysfs entries
    // ========================================================================

    private async Task<IReadOnlyList<PhysicalDeviceInfo>> DiscoverLinuxDevicesAsync(
        DeviceDiscoveryOptions options, CancellationToken ct)
    {
        var devices = new List<PhysicalDeviceInfo>();
        const string sysBlockPath = "/sys/block";

        if (!Directory.Exists(sysBlockPath))
        {
            _logger.LogWarning("Linux sysfs /sys/block not found. Cannot enumerate devices.");
            return devices;
        }

        string[] blockDevices;
        try
        {
            blockDevices = Directory.GetDirectories(sysBlockPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate /sys/block directories.");
            return devices;
        }

        foreach (var blockDevDir in blockDevices)
        {
            ct.ThrowIfCancellationRequested();

            var devName = Path.GetFileName(blockDevDir);

            // Skip loop and ram devices
            if (devName.StartsWith("loop", StringComparison.Ordinal) ||
                devName.StartsWith("ram", StringComparison.Ordinal))
            {
                continue;
            }

            // Skip virtual devices if not requested
            if (!options.IncludeVirtual && devName.StartsWith("vd", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var info = await ReadLinuxDeviceInfoAsync(blockDevDir, devName, ct).ConfigureAwait(false);
                if (info != null)
                {
                    devices.Add(info);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read device info for {DeviceName}. Skipping.", devName);
            }
        }

        return devices;
    }

    private async Task<PhysicalDeviceInfo?> ReadLinuxDeviceInfoAsync(
        string sysBlockDir, string devName, CancellationToken ct)
    {
        var devicePath = $"/dev/{devName}";

        // Read capacity: size (in 512-byte sectors) from /sys/block/{dev}/size
        var sizeStr = await ReadSysfsFileAsync(Path.Combine(sysBlockDir, "size"), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(sizeStr) || !long.TryParse(sizeStr.Trim(), out var sizeIn512Sectors))
        {
            _logger.LogWarning("Cannot read size for {Device}. Skipping.", devName);
            return null;
        }

        // Sector sizes
        var physSectorStr = await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "queue", "physical_block_size"), ct).ConfigureAwait(false);
        var logSectorStr = await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "queue", "logical_block_size"), ct).ConfigureAwait(false);
        var optIoStr = await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "queue", "optimal_io_size"), ct).ConfigureAwait(false);

        int physSector = TryParseIntOrDefault(physSectorStr, 512);
        int logSector = TryParseIntOrDefault(logSectorStr, 512);
        int optIoSize = TryParseIntOrDefault(optIoStr, 0);
        long capacityBytes = sizeIn512Sectors * 512;

        // Model and serial
        var model = (await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "device", "model"), ct).ConfigureAwait(false))?.Trim() ?? "Unknown";
        var serial = (await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "device", "serial"), ct).ConfigureAwait(false))?.Trim() ?? "";

        // If serial empty, try /dev/disk/by-id/ for a stable ID
        var deviceId = !string.IsNullOrEmpty(serial) ? serial : devName;

        // Vendor
        var vendor = (await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "device", "vendor"), ct).ConfigureAwait(false))?.Trim() ?? "";

        // Firmware version (NVMe-specific path)
        var firmware = "";
        if (devName.StartsWith("nvme", StringComparison.Ordinal))
        {
            firmware = (await ReadSysfsFileAsync(
                Path.Combine(sysBlockDir, "device", "firmware_rev"), ct).ConfigureAwait(false))?.Trim() ?? "";
        }

        // Rotational flag: 0 = SSD/NVMe, 1 = HDD
        var rotationalStr = await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "queue", "rotational"), ct).ConfigureAwait(false);
        bool isRotational = rotationalStr?.Trim() == "1";

        // TRIM support: discard_max_bytes > 0
        var discardStr = await ReadSysfsFileAsync(
            Path.Combine(sysBlockDir, "queue", "discard_max_bytes"), ct).ConfigureAwait(false);
        bool supportsTrim = TryParseLongOrDefault(discardStr, 0) > 0;

        // Determine bus type and media type from device name prefix and sysfs attributes
        var (busType, mediaType, transport) = ClassifyLinuxDevice(devName, isRotational);

        // NVMe namespace ID: extract from device name (nvme0n1 -> 1)
        int nvmeNamespaceId = 0;
        if (devName.StartsWith("nvme", StringComparison.Ordinal))
        {
            var nIdx = devName.IndexOf('n', 4);
            if (nIdx >= 0)
            {
                var nsStr = devName.Substring(nIdx + 1);
                // Strip partition suffix if present (e.g., nvme0n1p1)
                var pIdx = nsStr.IndexOf('p');
                if (pIdx >= 0) nsStr = nsStr.Substring(0, pIdx);
                int.TryParse(nsStr, out nvmeNamespaceId);
            }
        }

        // Check for NVMe-oF transport
        if (devName.StartsWith("nvme", StringComparison.Ordinal))
        {
            var nvmeTransport = await ReadSysfsFileAsync(
                Path.Combine(sysBlockDir, "device", "transport"), ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(nvmeTransport?.Trim()) &&
                !string.Equals(nvmeTransport.Trim(), "pcie", StringComparison.OrdinalIgnoreCase))
            {
                busType = BusType.NVMeOF;
                transport = DeviceTransport.Network;
            }
        }

        // Check for iSCSI: presence of /sys/class/iscsi_session/ and sd* prefix
        if (devName.StartsWith("sd", StringComparison.Ordinal) &&
            Directory.Exists("/sys/class/iscsi_session"))
        {
            // Heuristic: if iscsi_session exists, SCSI devices may be iSCSI targets
            var targetPath = Path.Combine(sysBlockDir, "device", "../../iscsi_session");
            if (Directory.Exists(targetPath))
            {
                busType = BusType.iSCSI;
                transport = DeviceTransport.Network;
            }
        }

        // Controller path and NUMA node from sysfs device symlink
        string? controllerPath = null;
        int? numaNode = null;

        try
        {
            var deviceLink = Path.Combine(sysBlockDir, "device");
            if (Directory.Exists(deviceLink))
            {
                controllerPath = deviceLink;
            }

            var numaStr = await ReadSysfsFileAsync(
                Path.Combine(sysBlockDir, "device", "numa_node"), ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(numaStr) && int.TryParse(numaStr.Trim(), out var numa) && numa >= 0)
            {
                numaNode = numa;
            }
        }
        catch
        {
            // NUMA/controller info is optional
        }

        return new PhysicalDeviceInfo(
            DeviceId: deviceId,
            DevicePath: devicePath,
            SerialNumber: serial,
            ModelNumber: model,
            FirmwareVersion: firmware,
            MediaType: mediaType,
            BusType: busType,
            Transport: transport,
            CapacityBytes: capacityBytes,
            PhysicalSectorSize: physSector,
            LogicalSectorSize: logSector,
            OptimalIoSize: optIoSize,
            SupportsTrim: supportsTrim,
            // Cat 15 (finding 882): volatile write-cache detection on Linux requires
            // `hdparm -W /dev/<device>` or ATA IDENTIFY via ioctl(HDIO_GET_WCACHE).
            // Conservative false prevents fsync bypass; future: query sysfs
            // `/sys/block/<dev>/queue/write_cache` ("write back" / "write through").
            SupportsVolatileWriteCache: false,
            NvmeNamespaceId: nvmeNamespaceId,
            ControllerPath: controllerPath,
            NumaNode: numaNode);
    }

    private static (BusType bus, MediaType media, DeviceTransport transport) ClassifyLinuxDevice(
        string devName, bool isRotational)
    {
        if (devName.StartsWith("nvme", StringComparison.Ordinal))
        {
            return (BusType.NVMe, MediaType.NVMe, DeviceTransport.PCIe);
        }

        if (devName.StartsWith("vd", StringComparison.Ordinal))
        {
            return (BusType.VirtIO, MediaType.VirtIO, DeviceTransport.Virtual);
        }

        if (devName.StartsWith("sd", StringComparison.Ordinal))
        {
            // SCSI/SATA/SAS device
            if (isRotational)
            {
                return (BusType.SCSI, MediaType.HDD, DeviceTransport.SAS);
            }
            return (BusType.SATA, MediaType.SSD, DeviceTransport.SATA);
        }

        if (devName.StartsWith("hd", StringComparison.Ordinal))
        {
            // Legacy IDE/PATA - treat as SATA
            return (BusType.SATA, isRotational ? MediaType.HDD : MediaType.SSD, DeviceTransport.SATA);
        }

        if (devName.StartsWith("sr", StringComparison.Ordinal) || devName.StartsWith("cd", StringComparison.Ordinal))
        {
            return (BusType.SCSI, MediaType.Unknown, DeviceTransport.SATA);
        }

        if (devName.StartsWith("zram", StringComparison.Ordinal))
        {
            return (BusType.Unknown, MediaType.RAMDisk, DeviceTransport.Virtual);
        }

        return (BusType.Unknown, isRotational ? MediaType.HDD : MediaType.Unknown, DeviceTransport.Unknown);
    }

    private static async Task<string?> ReadSysfsFileAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation so callers can honor the token
        }
        catch
        {
            return null;
        }
    }

    // ========================================================================
    // Windows implementation: enumerate via WMI Win32_DiskDrive
    // ========================================================================

    private async Task<IReadOnlyList<PhysicalDeviceInfo>> DiscoverWindowsDevicesAsync(
        DeviceDiscoveryOptions options, CancellationToken ct)
    {
        var devices = new List<PhysicalDeviceInfo>();

        try
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                EnumerateWindowsWmiDevices(devices, options);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI device enumeration failed. Returning empty list.");
        }

        return devices;
    }

    private void EnumerateWindowsWmiDevices(List<PhysicalDeviceInfo> devices, DeviceDiscoveryOptions options)
    {
        // Use System.Management for WMI queries (Windows only)
        // This uses reflection-based invocation to avoid compile-time dependency on System.Management
        // which is only available on Windows platforms

        try
        {
            var managementType = Type.GetType("System.Management.ManagementObjectSearcher, System.Management");
            if (managementType == null)
            {
                _logger.LogWarning("System.Management assembly not available. Cannot enumerate Windows devices.");
                return;
            }

            using var searcher = (IDisposable?)Activator.CreateInstance(managementType,
                "SELECT DeviceID, SerialNumber, Model, FirmwareRevision, Size, BytesPerSector, " +
                "MediaType, InterfaceType, Index FROM Win32_DiskDrive");
            if (searcher == null) return;

            var getMethod = managementType.GetMethod("Get", Type.EmptyTypes);
            if (getMethod == null) return;

            var results = getMethod.Invoke(searcher, null);
            if (results == null) return;

            if (results is System.Collections.IEnumerable enumerable)
            {
                foreach (var obj in enumerable)
                {
                    try
                    {
                        var info = ParseWindowsWmiDevice(obj, options);
                        if (info != null)
                        {
                            devices.Add(info);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse WMI device entry. Skipping.");
                    }
                    finally
                    {
                        (obj as IDisposable)?.Dispose();
                    }
                }
            }

            (results as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI query for Win32_DiskDrive failed.");
        }
    }

    private PhysicalDeviceInfo? ParseWindowsWmiDevice(object wmiObj, DeviceDiscoveryOptions options)
    {
        var objType = wmiObj.GetType();
        var indexer = objType.GetProperty("Item", new[] { typeof(string) });
        if (indexer == null) return null;

        string GetProp(string name) =>
            indexer.GetValue(wmiObj, new object[] { name })?.ToString()?.Trim() ?? "";

        var deviceId = GetProp("DeviceID");
        var serial = GetProp("SerialNumber");
        var model = GetProp("Model");
        var firmware = GetProp("FirmwareRevision");
        var sizeStr = GetProp("Size");
        var bytesPerSectorStr = GetProp("BytesPerSector");
        var mediaTypeStr = GetProp("MediaType");
        var interfaceType = GetProp("InterfaceType");
        var indexStr = GetProp("Index");

        if (string.IsNullOrEmpty(deviceId)) return null;

        long.TryParse(sizeStr, out var capacityBytes);
        int.TryParse(bytesPerSectorStr, out var bytesPerSector);
        if (bytesPerSector <= 0) bytesPerSector = 512;

        // Build device path: \\.\PhysicalDriveN
        var drivePath = deviceId;
        if (!drivePath.StartsWith(@"\\.\", StringComparison.Ordinal) &&
            int.TryParse(indexStr, out var driveIndex))
        {
            drivePath = $@"\\.\PhysicalDrive{driveIndex}";
        }

        // Map InterfaceType to BusType
        var busType = MapWindowsInterfaceType(interfaceType);

        // Map MediaType string to MediaType enum
        var mediaType = MapWindowsMediaType(mediaTypeStr, busType);

        // Determine transport
        var transport = busType switch
        {
            BusType.NVMe => DeviceTransport.PCIe,
            BusType.SATA => DeviceTransport.SATA,
            BusType.SAS => DeviceTransport.SAS,
            BusType.USB => DeviceTransport.USB,
            BusType.SCSI => DeviceTransport.SAS,
            BusType.iSCSI => DeviceTransport.Network,
            BusType.FibreChannel => DeviceTransport.Network,
            _ => DeviceTransport.Unknown
        };

        // SupportsTrim: assume true for SSD/NVMe (precise detection requires IOCTL_STORAGE_QUERY_PROPERTY)
        bool supportsTrim = mediaType is MediaType.SSD or MediaType.NVMe;

        // Skip removable if not requested
        if (!options.IncludeRemovable && mediaTypeStr.Contains("Removable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stableId = !string.IsNullOrEmpty(serial) ? serial : drivePath;

        return new PhysicalDeviceInfo(
            DeviceId: stableId,
            DevicePath: drivePath,
            SerialNumber: serial,
            ModelNumber: model,
            FirmwareVersion: firmware,
            MediaType: mediaType,
            BusType: busType,
            Transport: transport,
            CapacityBytes: capacityBytes,
            PhysicalSectorSize: bytesPerSector, // WMI BytesPerSector is physical; precise via IOCTL as documented alternative
            LogicalSectorSize: bytesPerSector,
            OptimalIoSize: 0, // Not available via WMI; would need IOCTL_STORAGE_QUERY_PROPERTY
            SupportsTrim: supportsTrim,
            // Cat 15 (finding 882): volatile write-cache detection on Windows requires
            // IOCTL_STORAGE_QUERY_PROPERTY with StorageAdapterWriteCacheProperty.
            // Conservative false prevents unnecessary fsync bypass.
            SupportsVolatileWriteCache: false,
            NvmeNamespaceId: busType == BusType.NVMe ? 1 : 0, // Windows exposes NVMe as single namespace
            ControllerPath: null, // Would require SetupAPI for topology
            NumaNode: null); // Would require SetupAPI P/Invoke for NUMA affinity
    }

    private static BusType MapWindowsInterfaceType(string interfaceType)
    {
        return interfaceType.ToUpperInvariant() switch
        {
            "IDE" => BusType.SATA,
            "SCSI" => BusType.SCSI,
            "USB" => BusType.USB,
            "1394" => BusType.Unknown, // FireWire
            "SAS" => BusType.SAS,
            "17" => BusType.NVMe, // NVMe interface type code
            "NVME" => BusType.NVMe,
            _ => BusType.Unknown
        };
    }

    private static MediaType MapWindowsMediaType(string mediaTypeStr, BusType busType)
    {
        if (busType == BusType.NVMe) return MediaType.NVMe;

        var upper = mediaTypeStr.ToUpperInvariant();

        if (upper.Contains("FIXED HARD DISK") || upper.Contains("FIXED"))
        {
            // Cat 9 (finding 885): WMI MediaType does not distinguish SSD from HDD for SATA devices.
            // Without a rotational flag (which requires IOCTL_STORAGE_QUERY_PROPERTY), we cannot
            // determine whether a SATA "Fixed Hard Disk" is spinning or solid-state.
            // Return Unknown so callers do not incorrectly place spinning SATA disks on the warm tier.
            // NVMe is already handled above; SAS/SCSI without rotational data defaults to HDD.
            if (busType == BusType.NVMe) return MediaType.NVMe;
            if (busType == BusType.SAS || busType == BusType.SCSI) return MediaType.HDD;
            // SATA: cannot distinguish SSD vs HDD without IOCTL — return Unknown to avoid misclassification.
            return MediaType.Unknown;
        }

        if (upper.Contains("REMOVABLE"))
        {
            return MediaType.Unknown;
        }

        if (upper.Contains("EXTERNAL"))
        {
            return MediaType.Unknown;
        }

        return MediaType.Unknown;
    }

    private static int TryParseIntOrDefault(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return int.TryParse(value.Trim(), out var result) ? result : defaultValue;
    }

    private static long TryParseLongOrDefault(string? value, long defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return long.TryParse(value.Trim(), out var result) ? result : defaultValue;
    }
}
