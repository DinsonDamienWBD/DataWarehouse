// FUTURE: Hypervisor integration -- interfaces preserved for hypervisor detection and balloon
// driver support per AD-06. These types have zero current implementations but define contracts
// for future hypervisor-aware storage plugins. Do NOT delete during dead code cleanup.

namespace DataWarehouse.SDK.Virtualization;

/// <summary>
/// Supported hypervisor platforms for virtualized deployment.
/// </summary>
public enum HypervisorType
{
    /// <summary>Unknown or undetected hypervisor.</summary>
    Unknown,

    /// <summary>VMware ESXi hypervisor.</summary>
    VMwareESXi,

    /// <summary>Microsoft Hyper-V hypervisor.</summary>
    HyperV,

    /// <summary>KVM/QEMU hypervisor (Linux).</summary>
    KvmQemu,

    /// <summary>Xen hypervisor.</summary>
    Xen,

    /// <summary>Proxmox VE hypervisor.</summary>
    Proxmox,

    /// <summary>oVirt/RHV hypervisor.</summary>
    OVirtRHV,

    /// <summary>Nutanix AHV hypervisor.</summary>
    NutanixAHV,

    /// <summary>Bare metal (no virtualization).</summary>
    Bare
}

/// <summary>
/// Virtual machine state enumeration.
/// </summary>
public enum VmState
{
    /// <summary>VM is running.</summary>
    Running,

    /// <summary>VM is paused.</summary>
    Paused,

    /// <summary>VM is stopped.</summary>
    Stopped,

    /// <summary>VM is being migrated.</summary>
    Migrating,

    /// <summary>VM is suspended to disk.</summary>
    Suspended,

    /// <summary>VM state is unknown.</summary>
    Unknown
}

/// <summary>
/// Hypervisor detection and optimization interface.
/// Provides runtime detection of virtualization platform and VM information.
/// </summary>
public interface IHypervisorDetector
{
    /// <summary>
    /// Gets the detected hypervisor type.
    /// Cached result from hypervisor detection.
    /// </summary>
    HypervisorType DetectedHypervisor { get; }

    /// <summary>
    /// Indicates whether the system is running virtualized.
    /// Returns false only for bare metal deployments.
    /// </summary>
    bool IsVirtualized { get; }

    /// <summary>
    /// Gets the capabilities available in the detected hypervisor.
    /// </summary>
    /// <returns>Hypervisor capabilities including driver support and features.</returns>
    Task<HypervisorCapabilities> GetCapabilitiesAsync();

    /// <summary>
    /// Gets current virtual machine information.
    /// </summary>
    /// <returns>VM metadata including resource allocation and state.</returns>
    Task<VmInfo> GetVmInfoAsync();
}

/// <summary>
/// Capabilities available in the detected hypervisor.
/// Used for feature detection and optimization decisions.
/// </summary>
/// <param name="SupportsBalloonDriver">True if balloon memory driver is available.</param>
/// <param name="SupportsTrimDiscard">True if TRIM/Discard is supported for storage optimization.</param>
/// <param name="SupportsHotAddCpu">True if CPU hot-add is supported.</param>
/// <param name="SupportsHotAddMemory">True if memory hot-add is supported.</param>
/// <param name="SupportsLiveMigration">True if live migration is supported.</param>
/// <param name="SupportsSnapshots">True if VM snapshots are supported.</param>
/// <param name="SupportsBackupApi">True if backup API integration is available.</param>
/// <param name="ParavirtDrivers">List of available paravirtualization drivers (e.g., virtio, vmxnet3, pvscsi).</param>
public record HypervisorCapabilities(
    bool SupportsBalloonDriver,
    bool SupportsTrimDiscard,
    bool SupportsHotAddCpu,
    bool SupportsHotAddMemory,
    bool SupportsLiveMigration,
    bool SupportsSnapshots,
    bool SupportsBackupApi,
    string[] ParavirtDrivers
);

/// <summary>
/// Current virtual machine information and resource allocation.
/// </summary>
/// <param name="VmId">Unique VM identifier assigned by hypervisor.</param>
/// <param name="VmName">Human-readable VM name.</param>
/// <param name="Hypervisor">The hypervisor type hosting this VM.</param>
/// <param name="State">Current VM state.</param>
/// <param name="CpuCount">Number of virtual CPUs allocated.</param>
/// <param name="MemoryBytes">Total memory allocated in bytes.</param>
/// <param name="NetworkInterfaces">List of network interface identifiers.</param>
/// <param name="StorageDevices">List of storage device identifiers.</param>
public record VmInfo(
    string VmId,
    string VmName,
    HypervisorType Hypervisor,
    VmState State,
    int CpuCount,
    long MemoryBytes,
    string[] NetworkInterfaces,
    string[] StorageDevices
);

/// <summary>
/// Balloon driver control for dynamic memory optimization.
/// Allows the hypervisor to reclaim unused memory from the guest.
/// </summary>
public interface IBalloonDriver
{
    /// <summary>
    /// Gets whether the balloon driver is available and loaded.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the current target memory size for the balloon driver.
    /// </summary>
    /// <returns>Target memory in bytes.</returns>
    Task<long> GetTargetMemoryAsync();

    /// <summary>
    /// Sets the target memory size for the balloon driver.
    /// The driver will inflate/deflate to reach this target.
    /// </summary>
    /// <param name="bytes">Target memory in bytes.</param>
    Task SetTargetMemoryAsync(long bytes);

    /// <summary>
    /// Gets current balloon driver statistics.
    /// </summary>
    /// <returns>Statistics including current inflation and swap usage.</returns>
    Task<BalloonStatistics> GetStatisticsAsync();
}

/// <summary>
/// Balloon driver statistics for monitoring memory optimization.
/// </summary>
/// <param name="CurrentMemoryBytes">Current balloon size (inflated memory).</param>
/// <param name="MaxMemoryBytes">Maximum possible balloon size.</param>
/// <param name="SwapInBytes">Bytes swapped in from disk.</param>
/// <param name="SwapOutBytes">Bytes swapped out to disk.</param>
public record BalloonStatistics(
    long CurrentMemoryBytes,
    long MaxMemoryBytes,
    long SwapInBytes,
    long SwapOutBytes
);

/// <summary>
/// Storage optimization interface for TRIM/Discard operations.
/// Helps reclaim unused space on thin-provisioned virtual disks.
/// </summary>
public interface IStorageOptimizer
{
    /// <summary>
    /// Checks if a device supports TRIM/Discard operations.
    /// </summary>
    /// <param name="devicePath">Path to the storage device.</param>
    /// <returns>True if TRIM is supported.</returns>
    Task<bool> SupportsTrimAsync(string devicePath);

    /// <summary>
    /// Sends a TRIM command to discard unused blocks.
    /// </summary>
    /// <param name="devicePath">Path to the storage device.</param>
    /// <param name="offset">Offset in bytes to start trimming.</param>
    /// <param name="length">Length in bytes to trim.</param>
    Task TrimAsync(string devicePath, long offset, long length);

    /// <summary>
    /// Performs a full scan to discard all unused blocks.
    /// This can reclaim significant space on thin-provisioned disks.
    /// </summary>
    Task DiscardUnusedBlocksAsync();
}

/// <summary>
/// VM snapshot integration provider.
/// Coordinates with hypervisor snapshot mechanisms for consistent backups.
/// </summary>
public interface IVmSnapshotProvider
{
    /// <summary>
    /// Checks if application-consistent snapshots are supported.
    /// Application-consistent snapshots require quiescing I/O and flushing caches.
    /// </summary>
    /// <returns>True if application-consistent snapshots are available.</returns>
    Task<bool> SupportsApplicationConsistentAsync();

    /// <summary>
    /// Quiesces the VM in preparation for a snapshot.
    /// Flushes caches, pauses I/O, and ensures consistent state.
    /// </summary>
    Task QuiesceAsync();

    /// <summary>
    /// Resumes normal operation after snapshot completion.
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// Creates a VM snapshot with optional application consistency.
    /// </summary>
    /// <param name="name">Snapshot name.</param>
    /// <param name="applicationConsistent">Whether to create an application-consistent snapshot.</param>
    /// <returns>Metadata for the created snapshot.</returns>
    Task<SnapshotMetadata> CreateSnapshotAsync(string name, bool applicationConsistent);
}

/// <summary>
/// Metadata for a VM snapshot.
/// </summary>
/// <param name="SnapshotId">Unique snapshot identifier.</param>
/// <param name="Name">Snapshot name.</param>
/// <param name="CreatedAt">Snapshot creation timestamp.</param>
/// <param name="ApplicationConsistent">Whether the snapshot is application-consistent.</param>
public record SnapshotMetadata(
    string SnapshotId,
    string Name,
    DateTimeOffset CreatedAt,
    bool ApplicationConsistent
);

/// <summary>
/// Backup API integration types.
/// </summary>
public enum BackupApiType
{
    /// <summary>No backup API integration.</summary>
    None,

    /// <summary>VMware VADP (vStorage APIs for Data Protection).</summary>
    VMwareVADP,

    /// <summary>Hyper-V VSS (Volume Shadow Copy Service).</summary>
    HyperVVSS,

    /// <summary>Proxmox Backup Server integration.</summary>
    ProxmoxBackup,

    /// <summary>Custom backup API.</summary>
    Custom
}

/// <summary>
/// Backup API integration for hypervisor-aware backups.
/// Provides efficient changed-block tracking and incremental backups.
/// </summary>
public interface IBackupApiIntegration
{
    /// <summary>
    /// Gets the backup API type supported by this provider.
    /// </summary>
    BackupApiType ApiType { get; }

    /// <summary>
    /// Prepares the system for backup operations.
    /// May create a snapshot or enable change tracking.
    /// </summary>
    Task PrepareForBackupAsync();

    /// <summary>
    /// Gets a stream for reading backup data.
    /// Stream may use hypervisor-specific optimization like CBT (Changed Block Tracking).
    /// </summary>
    /// <returns>Stream for reading backup data.</returns>
    Task<Stream> GetBackupStreamAsync();

    /// <summary>
    /// Completes the backup operation and cleans up resources.
    /// </summary>
    /// <param name="success">Whether the backup completed successfully.</param>
    Task CompleteBackupAsync(bool success);
}
