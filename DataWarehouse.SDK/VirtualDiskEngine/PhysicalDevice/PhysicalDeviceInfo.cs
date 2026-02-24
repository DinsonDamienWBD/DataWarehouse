using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Physical storage media type classification.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Physical device metadata (BMDV-01)")]
public enum MediaType
{
    /// <summary>NVMe solid-state drive.</summary>
    NVMe,
    /// <summary>SATA/SAS solid-state drive.</summary>
    SSD,
    /// <summary>Hard disk drive (rotational).</summary>
    HDD,
    /// <summary>Tape storage device.</summary>
    Tape,
    /// <summary>VirtIO virtual block device.</summary>
    VirtIO,
    /// <summary>RAM-backed block device.</summary>
    RAMDisk,
    /// <summary>Unknown or undetectable media type.</summary>
    Unknown
}

/// <summary>
/// Physical bus/interface type for block device connectivity.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Physical device metadata (BMDV-01)")]
public enum BusType
{
    /// <summary>NVMe over PCIe.</summary>
    NVMe,
    /// <summary>SCSI bus.</summary>
    SCSI,
    /// <summary>Serial ATA.</summary>
    SATA,
    /// <summary>Serial Attached SCSI.</summary>
    SAS,
    /// <summary>Universal Serial Bus.</summary>
    USB,
    /// <summary>VirtIO paravirtualized bus.</summary>
    VirtIO,
    /// <summary>iSCSI (SCSI over TCP/IP).</summary>
    iSCSI,
    /// <summary>NVMe over Fabrics (RDMA, TCP, FC).</summary>
    NVMeOF,
    /// <summary>Fibre Channel.</summary>
    FibreChannel,
    /// <summary>Unknown bus type.</summary>
    Unknown
}

/// <summary>
/// Physical transport layer for device communication.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Physical device metadata (BMDV-01)")]
public enum DeviceTransport
{
    /// <summary>PCI Express.</summary>
    PCIe,
    /// <summary>SATA cable/connector.</summary>
    SATA,
    /// <summary>SAS cable/connector.</summary>
    SAS,
    /// <summary>USB cable/connector.</summary>
    USB,
    /// <summary>Network-attached (iSCSI, NVMe-oF, FC).</summary>
    Network,
    /// <summary>Virtual/emulated transport.</summary>
    Virtual,
    /// <summary>Unknown transport.</summary>
    Unknown
}

/// <summary>
/// Metadata describing a discovered physical block device including identity,
/// geometry, media characteristics, topology, and capability flags.
/// </summary>
/// <param name="DeviceId">Unique stable identifier for this device (e.g., serial-based or by-id path).</param>
/// <param name="DevicePath">OS-specific device path (e.g., /dev/nvme0n1 or \\.\PhysicalDrive0).</param>
/// <param name="SerialNumber">Device serial number as reported by firmware.</param>
/// <param name="ModelNumber">Device model name/number.</param>
/// <param name="FirmwareVersion">Firmware revision string.</param>
/// <param name="MediaType">Storage media classification.</param>
/// <param name="BusType">Physical bus/interface type.</param>
/// <param name="Transport">Physical transport layer.</param>
/// <param name="CapacityBytes">Total device capacity in bytes.</param>
/// <param name="PhysicalSectorSize">Physical sector size in bytes (typically 512 or 4096).</param>
/// <param name="LogicalSectorSize">Logical sector size in bytes.</param>
/// <param name="OptimalIoSize">Optimal I/O transfer size in bytes for alignment (0 if unknown).</param>
/// <param name="SupportsTrim">Whether the device supports TRIM/UNMAP commands.</param>
/// <param name="SupportsVolatileWriteCache">Whether the device has a volatile write cache.</param>
/// <param name="NvmeNamespaceId">NVMe namespace ID (0 if not an NVMe device).</param>
/// <param name="ControllerPath">Path to the parent controller for topology mapping (null if unknown).</param>
/// <param name="NumaNode">NUMA node affinity for this device (null if unknown or not applicable).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Physical device metadata (BMDV-01)")]
public sealed record PhysicalDeviceInfo(
    string DeviceId,
    string DevicePath,
    string SerialNumber,
    string ModelNumber,
    string FirmwareVersion,
    MediaType MediaType,
    BusType BusType,
    DeviceTransport Transport,
    long CapacityBytes,
    int PhysicalSectorSize,
    int LogicalSectorSize,
    int OptimalIoSize,
    bool SupportsTrim,
    bool SupportsVolatileWriteCache,
    int NvmeNamespaceId,
    string? ControllerPath,
    int? NumaNode);

/// <summary>
/// SMART health snapshot for a physical block device, providing wear indicators,
/// error counters, and raw attribute data for monitoring and predictive maintenance.
/// </summary>
/// <param name="IsHealthy">Overall health status from device self-assessment.</param>
/// <param name="TemperatureCelsius">Current device temperature in degrees Celsius.</param>
/// <param name="WearLevelPercent">SSD/NVMe wear level as percentage (0-100). 0 for HDDs.</param>
/// <param name="TotalBytesWritten">Lifetime bytes written to the device.</param>
/// <param name="TotalBytesRead">Lifetime bytes read from the device.</param>
/// <param name="UncorrectableErrors">Count of uncorrectable read/write errors.</param>
/// <param name="ReallocatedSectors">Count of reallocated (bad) sectors.</param>
/// <param name="PowerOnHours">Total power-on hours.</param>
/// <param name="EstimatedRemainingLife">Estimated remaining device lifetime (null if unavailable).</param>
/// <param name="RawSmartAttributes">Raw SMART attribute key-value pairs for vendor-specific data.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Physical device health (BMDV-01)")]
public sealed record PhysicalDeviceHealth(
    bool IsHealthy,
    double TemperatureCelsius,
    double WearLevelPercent,
    long TotalBytesWritten,
    long TotalBytesRead,
    long UncorrectableErrors,
    long ReallocatedSectors,
    int PowerOnHours,
    TimeSpan? EstimatedRemainingLife,
    IReadOnlyDictionary<string, string> RawSmartAttributes);
