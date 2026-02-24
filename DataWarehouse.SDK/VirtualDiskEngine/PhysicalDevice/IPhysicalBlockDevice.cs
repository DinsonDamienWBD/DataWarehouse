using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Extends <see cref="IBlockDevice"/> with physical-device-specific operations
/// including TRIM/UNMAP, scatter-gather aligned I/O, device identity, and SMART health monitoring.
/// This is the foundational contract for all physical block device access in Phase 90.
/// </summary>
/// <remarks>
/// <para>
/// All I/O methods require block-level alignment: blockNumber * BlockSize must be
/// aligned to <see cref="PhysicalSectorSize"/>. Misaligned operations may result in
/// degraded performance or <see cref="ArgumentException"/> on strict devices.
/// </para>
/// <para>
/// Scatter-gather operations (<see cref="ReadScatterAsync"/> and <see cref="WriteGatherAsync"/>)
/// enable efficient batched I/O by submitting multiple block operations in a single call,
/// reducing syscall overhead and enabling hardware-level command coalescing.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Physical block device abstraction (BMDV-01)")]
public interface IPhysicalBlockDevice : IBlockDevice
{
    /// <summary>
    /// Gets the metadata for this physical device including serial number,
    /// media type, bus type, capacity, and topology information.
    /// </summary>
    PhysicalDeviceInfo DeviceInfo { get; }

    /// <summary>
    /// Gets whether this device is currently online and accepting I/O operations.
    /// </summary>
    bool IsOnline { get; }

    /// <summary>
    /// Gets the physical sector size in bytes (typically 512 or 4096).
    /// All I/O should be aligned to this boundary for optimal performance.
    /// </summary>
    long PhysicalSectorSize { get; }

    /// <summary>
    /// Gets the logical sector size in bytes. May differ from physical sector size
    /// when the device emulates 512-byte sectors on 4K physical media (512e drives).
    /// </summary>
    long LogicalSectorSize { get; }

    /// <summary>
    /// Issues a TRIM/UNMAP command to the device, informing it that the specified
    /// blocks are no longer in use and may be reclaimed by the device controller.
    /// Essential for SSD wear leveling and garbage collection.
    /// </summary>
    /// <param name="blockNumber">Starting block number to trim. Must be aligned to PhysicalSectorSize.</param>
    /// <param name="blockCount">Number of contiguous blocks to trim.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">Thrown if the device does not support TRIM.</exception>
    Task TrimAsync(long blockNumber, int blockCount, CancellationToken ct = default);

    /// <summary>
    /// Performs scatter-gather aligned reads, issuing multiple block read operations
    /// in a single batched call for reduced syscall overhead and potential hardware coalescing.
    /// </summary>
    /// <param name="operations">
    /// List of (blockNumber, buffer) tuples. Each blockNumber * BlockSize must be
    /// aligned to <see cref="PhysicalSectorSize"/>. Each buffer must be at least BlockSize bytes.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of operations successfully completed.</returns>
    /// <exception cref="ArgumentException">Thrown if any operation has misaligned block number or undersized buffer.</exception>
    Task<int> ReadScatterAsync(IReadOnlyList<(long blockNumber, Memory<byte> buffer)> operations, CancellationToken ct = default);

    /// <summary>
    /// Performs gather-write operations, issuing multiple block write operations
    /// in a single batched call for reduced syscall overhead and potential hardware coalescing.
    /// </summary>
    /// <param name="operations">
    /// List of (blockNumber, data) tuples. Each blockNumber * BlockSize must be
    /// aligned to <see cref="PhysicalSectorSize"/>. Each data must be exactly BlockSize bytes.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of operations successfully completed.</returns>
    /// <exception cref="ArgumentException">Thrown if any operation has misaligned block number or incorrect data length.</exception>
    Task<int> WriteGatherAsync(IReadOnlyList<(long blockNumber, ReadOnlyMemory<byte> data)> operations, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a SMART health snapshot from the device, including temperature,
    /// wear level, error counters, and estimated remaining lifetime.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current health status of the physical device.</returns>
    Task<PhysicalDeviceHealth> GetHealthAsync(CancellationToken ct = default);
}
