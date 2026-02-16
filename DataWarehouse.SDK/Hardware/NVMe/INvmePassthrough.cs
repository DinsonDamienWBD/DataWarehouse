using DataWarehouse.SDK.Contracts;
using System;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware.NVMe;

/// <summary>
/// Interface for NVMe command passthrough (direct NVMe access bypassing OS filesystem).
/// </summary>
/// <remarks>
/// <para>
/// NVMe passthrough enables direct submission of admin and I/O commands to NVMe devices,
/// bypassing the OS filesystem layer. This provides maximum throughput (>90% of hardware
/// line rate) for applications that manage their own storage (e.g., DataWarehouse VDE).
/// </para>
/// <para>
/// <strong>Use Cases:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>User-space filesystems:</strong> DataWarehouse VDE (Phase 33) manages its own
///       storage layout and uses NVMe passthrough for direct block access.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Firmware management:</strong> Update NVMe firmware, retrieve SMART data, format namespaces.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Performance benchmarking:</strong> Measure raw device performance without filesystem overhead.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Availability:</strong> NVMe passthrough is only available on:
/// </para>
/// <list type="bullet">
///   <item><description>Bare metal with direct NVMe device access</description></item>
///   <item><description>VMs with NVMe device passthrough configured (PCI passthrough/SR-IOV)</description></item>
/// </list>
/// <para>
/// Passthrough is NOT available on standard VMs using virtualized storage (virtio-blk, PVSCSI, etc.).
/// </para>
/// <para>
/// <strong>Namespace Isolation:</strong> Commands must target a specific NVMe namespace.
/// Ensure the namespace is NOT mounted by the OS filesystem to avoid data corruption.
/// Use dedicated namespaces for DataWarehouse or unmount namespaces before using passthrough.
/// </para>
/// <para>
/// <strong>Permissions:</strong> Requires elevated privileges:
/// </para>
/// <list type="bullet">
///   <item><description>Windows: Administrator privileges (to open \\.\PhysicalDriveX)</description></item>
///   <item><description>Linux: root or CAP_SYS_ADMIN (to open /dev/nvme0, /dev/nvme0n1, etc.)</description></item>
/// </list>
/// <para>
/// <strong>Thread Safety:</strong> Implementations must be thread-safe. Multiple threads can
/// submit commands concurrently (submission queue is multi-threaded).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe passthrough interface (HW-07)")]
public interface INvmePassthrough : IDisposable
{
    /// <summary>
    /// Gets whether NVMe passthrough is available for the specified device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns true when:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Running on bare metal with direct NVMe access, OR</description></item>
    ///   <item><description>Running in VM with NVMe device passthrough configured</description></item>
    /// </list>
    /// <para>
    /// Returns false when:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Device cannot be opened (missing permissions, device not found)</description></item>
    ///   <item><description>Running in VM without NVMe passthrough (virtualized storage only)</description></item>
    /// </list>
    /// <para>
    /// This property is cached after initialization and does not require repeated checks.
    /// </para>
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the NVMe controller ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Controller ID extracted from the device path:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Windows: "\\.\PhysicalDrive0" → controller 0</description></item>
    ///   <item><description>Linux: "/dev/nvme0" → controller 0, "/dev/nvme1" → controller 1</description></item>
    /// </list>
    /// </remarks>
    int ControllerId { get; }

    /// <summary>
    /// Submits an admin command to the NVMe controller.
    /// </summary>
    /// <param name="opcode">Admin command opcode (Identify, GetLogPage, etc.).</param>
    /// <param name="nsid">Namespace ID (0 for controller-level commands).</param>
    /// <param name="dataBuffer">
    /// Data buffer for command input/output (may be null for commands without data transfer).
    /// For commands that return data (Identify, GetLogPage), this buffer receives the data.
    /// </param>
    /// <param name="commandDwords">
    /// Command-specific dwords (CDW10-CDW15). Must be exactly 6 elements.
    /// Layout varies by command opcode (see NVMe specification).
    /// </param>
    /// <returns>NVMe completion structure with command result and status.</returns>
    /// <remarks>
    /// <para>
    /// Admin commands operate on the controller or namespaces and do not perform I/O.
    /// Common admin commands:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Identify: Get controller/namespace info (4KB data transfer)</description></item>
    ///   <item><description>GetLogPage: Retrieve SMART data, error log, etc.</description></item>
    ///   <item><description>FirmwareCommit: Activate a firmware image</description></item>
    ///   <item><description>FormatNvm: Low-level format a namespace (WARNING: data loss)</description></item>
    /// </list>
    /// <para>
    /// The method is async to avoid blocking the caller during device I/O. Command submission
    /// and completion are handled by the OS NVMe driver.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="IsAvailable"/> is false (passthrough not available).
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="commandDwords"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="commandDwords"/> length is not exactly 6.
    /// </exception>
    Task<NvmeCompletion> SubmitAdminCommandAsync(
        NvmeAdminCommand opcode,
        uint nsid,
        byte[]? dataBuffer,
        uint[] commandDwords);

    /// <summary>
    /// Submits an I/O command to the NVMe namespace.
    /// </summary>
    /// <param name="opcode">I/O command opcode (Read, Write, DatasetManagement, etc.).</param>
    /// <param name="nsid">Namespace ID (must be > 0). Use 1 for the first namespace.</param>
    /// <param name="startLba">Starting logical block address (0-based).</param>
    /// <param name="blockCount">
    /// Number of blocks to read/write (0-based: 0 means 1 block, 1 means 2 blocks, etc.).
    /// Maximum block count is device-specific (typically 256-1024 blocks per command).
    /// </param>
    /// <param name="dataBuffer">
    /// Data buffer for I/O (required for Read/Write commands, may be null for other commands).
    /// Buffer size must be at least (blockCount + 1) * LBA_size.
    /// </param>
    /// <returns>NVMe completion structure with command result and status.</returns>
    /// <remarks>
    /// <para>
    /// I/O commands operate on NVMe namespaces and perform data transfer.
    /// Common I/O commands:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Read: Read logical blocks from the device</description></item>
    ///   <item><description>Write: Write logical blocks to the device</description></item>
    ///   <item><description>DatasetManagement: TRIM/deallocate blocks for SSD garbage collection</description></item>
    ///   <item><description>WriteZeroes: Write zeros without data transfer (device generates zeros)</description></item>
    /// </list>
    /// <para>
    /// <strong>Block Count Encoding:</strong> NVMe uses 0-based block count (0 = 1 block, 255 = 256 blocks).
    /// This encoding maximizes the range of a 16-bit field.
    /// </para>
    /// <para>
    /// <strong>LBA Size:</strong> Logical block size is namespace-specific (typically 512 bytes or 4096 bytes).
    /// Query the namespace via Identify command to determine LBA size.
    /// </para>
    /// <para>
    /// The method is async to avoid blocking the caller during device I/O. Command submission
    /// and completion are handled by the OS NVMe driver.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="IsAvailable"/> is false (passthrough not available).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="nsid"/> is 0 (namespaces are 1-based).
    /// </exception>
    Task<NvmeCompletion> SubmitIoCommandAsync(
        NvmeIoCommand opcode,
        uint nsid,
        ulong startLba,
        ushort blockCount,
        byte[]? dataBuffer);
}
