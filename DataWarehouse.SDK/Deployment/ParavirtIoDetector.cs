using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Paravirtualized I/O device type classification.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Paravirtualized device types (ENV-02)")]
public enum ParavirtDeviceType
{
    /// <summary>KVM virtio-blk block device.</summary>
    VirtioBlock,

    /// <summary>KVM virtio-scsi SCSI controller.</summary>
    VirtioScsi,

    /// <summary>VMware PVSCSI paravirtualized SCSI controller.</summary>
    Pvscsi,

    /// <summary>Hyper-V storage VSC (Virtual Storage Controller).</summary>
    HyperVStorageVsc,

    /// <summary>Xen paravirtualized disk.</summary>
    XenVirtualDisk
}

/// <summary>
/// Detected paravirtualized I/O device record.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Paravirtualized device record (ENV-02)")]
public sealed record ParavirtDevice
{
    /// <summary>Gets the unique device identifier.</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Gets the human-readable device name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the paravirtualized device type classification.</summary>
    public ParavirtDeviceType Type { get; init; }

    /// <summary>Gets the driver name (e.g., "virtio_blk", "pvscsi").</summary>
    public string? DriverName { get; init; }

    /// <summary>Gets additional device properties (vendor ID, capabilities, etc.).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// Detects paravirtualized I/O devices (virtio-blk, virtio-scsi, PVSCSI, Hyper-V VSC, Xen).
/// Uses <see cref="IHardwareProbe"/> from Phase 32 for device enumeration.
/// </summary>
/// <remarks>
/// <para>
/// Paravirtualized I/O achieves near-native performance (95%+ of bare metal) by bypassing
/// emulated hardware. Detection enables DataWarehouse to prefer paravirt devices over emulated.
/// </para>
/// <para>
/// Platform-specific detection:
/// - Linux: Check for virtio-blk (/dev/vd*), virtio-scsi, vendor ID 0x1af4 (VirtIO PCI)
/// - Windows: Check PnP IDs for VEN_1AF4 (VirtIO), VEN_15AD&DEV_07C0 (VMware PVSCSI), ROOT\STORVSC (Hyper-V)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Paravirtualized I/O detection (ENV-02)")]
public sealed class ParavirtIoDetector
{
    private readonly IHardwareProbe? _hardwareProbe;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParavirtIoDetector"/> class.
    /// </summary>
    /// <param name="hardwareProbe">
    /// Optional hardware probe from Phase 32. If null, detection returns empty list.
    /// </param>
    public ParavirtIoDetector(IHardwareProbe? hardwareProbe = null)
    {
        _hardwareProbe = hardwareProbe;
    }

    /// <summary>
    /// Detects all paravirtualized I/O devices available on the system.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// List of detected paravirtualized devices. Returns empty list if none found or detection unavailable.
    /// </returns>
    /// <remarks>
    /// Detection heuristics:
    /// - VirtIO devices: Vendor ID 0x1AF4, driver name "virtio_blk" or "virtio_scsi"
    /// - VMware PVSCSI: Device ID VEN_15AD&DEV_07C0
    /// - Hyper-V VSC: Device ID ROOT\STORVSC
    /// - Xen: Device name contains "xvd"
    /// </remarks>
    public async Task<IReadOnlyList<ParavirtDevice>> DetectParavirtDevicesAsync(CancellationToken ct = default)
    {
        try
        {
            if (_hardwareProbe == null)
            {
                // No hardware probe available
                return Array.Empty<ParavirtDevice>();
            }

            // Discover all storage devices
            var allDevices = await _hardwareProbe.DiscoverAsync(
                HardwareDeviceType.NvmeController | HardwareDeviceType.ScsiController | HardwareDeviceType.BlockDevice,
                ct);

            if (allDevices == null || allDevices.Count == 0)
            {
                return Array.Empty<ParavirtDevice>();
            }

            var paravirtDevices = new List<ParavirtDevice>();

            foreach (var device in allDevices)
            {
                var paravirtDevice = ClassifyParavirtDevice(device);
                if (paravirtDevice != null)
                {
                    paravirtDevices.Add(paravirtDevice);
                }
            }

            return paravirtDevices;
        }
        catch
        {
            // Detection failure returns empty list (graceful degradation)
            return Array.Empty<ParavirtDevice>();
        }
    }

    private ParavirtDevice? ClassifyParavirtDevice(HardwareDevice device)
    {
        // Check for VirtIO devices (vendor ID 0x1AF4)
        var vendorId = device.Properties.GetValueOrDefault("VendorId", "");
        var deviceId = device.Properties.GetValueOrDefault("DeviceId", "");
        var driverName = device.Properties.GetValueOrDefault("DriverName", "");
        var devicePath = device.Properties.GetValueOrDefault("DevicePath", "");

        // VirtIO Block (Linux: /dev/vd*, Windows: VEN_1AF4&DEV_1001)
        if (vendorId.Contains("1AF4", StringComparison.OrdinalIgnoreCase) ||
            deviceId.Contains("VEN_1AF4&DEV_1001", StringComparison.OrdinalIgnoreCase) ||
            driverName.Contains("virtio_blk", StringComparison.OrdinalIgnoreCase) ||
            devicePath.Contains("/dev/vd", StringComparison.OrdinalIgnoreCase))
        {
            return new ParavirtDevice
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Type = ParavirtDeviceType.VirtioBlock,
                DriverName = driverName.Length > 0 ? driverName : "virtio_blk",
                Properties = device.Properties
            };
        }

        // VirtIO SCSI (driver: virtio_scsi)
        if (vendorId.Contains("1AF4", StringComparison.OrdinalIgnoreCase) ||
            driverName.Contains("virtio_scsi", StringComparison.OrdinalIgnoreCase))
        {
            return new ParavirtDevice
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Type = ParavirtDeviceType.VirtioScsi,
                DriverName = driverName.Length > 0 ? driverName : "virtio_scsi",
                Properties = device.Properties
            };
        }

        // VMware PVSCSI (VEN_15AD&DEV_07C0)
        if (deviceId.Contains("VEN_15AD&DEV_07C0", StringComparison.OrdinalIgnoreCase) ||
            driverName.Contains("pvscsi", StringComparison.OrdinalIgnoreCase))
        {
            return new ParavirtDevice
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Type = ParavirtDeviceType.Pvscsi,
                DriverName = driverName.Length > 0 ? driverName : "pvscsi",
                Properties = device.Properties
            };
        }

        // Hyper-V Storage VSC (ROOT\STORVSC)
        if (deviceId.Contains("ROOT\\STORVSC", StringComparison.OrdinalIgnoreCase) ||
            driverName.Contains("storvsc", StringComparison.OrdinalIgnoreCase))
        {
            return new ParavirtDevice
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Type = ParavirtDeviceType.HyperVStorageVsc,
                DriverName = driverName.Length > 0 ? driverName : "storvsc",
                Properties = device.Properties
            };
        }

        // Xen Virtual Disk (device path contains "xvd")
        if (devicePath.Contains("xvd", StringComparison.OrdinalIgnoreCase))
        {
            return new ParavirtDevice
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Type = ParavirtDeviceType.XenVirtualDisk,
                DriverName = driverName.Length > 0 ? driverName : "xen_vbd",
                Properties = device.Properties
            };
        }

        // Not a paravirtualized device
        return null;
    }
}
