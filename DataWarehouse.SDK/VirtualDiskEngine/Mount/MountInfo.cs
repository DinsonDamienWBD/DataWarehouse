using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Read-only status information about an active or recently active VDE mount.
/// Returned by <see cref="IVdeMountProvider.ListMountsAsync"/> for mount enumeration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount provider abstraction (VOPT-83)")]
public sealed class MountInfo
{
    /// <summary>
    /// The OS path or drive letter where the VDE volume is mounted.
    /// </summary>
    public required string MountPoint { get; init; }

    /// <summary>
    /// The file path to the VDE volume file.
    /// </summary>
    public required string VdePath { get; init; }

    /// <summary>
    /// Whether the mount is currently active and serving filesystem operations.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// UTC timestamp when the mount was established.
    /// </summary>
    public DateTimeOffset MountedAtUtc { get; init; }

    /// <summary>
    /// Whether the mount is read-only.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Block size of the mounted VDE volume in bytes.
    /// </summary>
    public long BlockSize { get; init; }

    /// <summary>
    /// Total number of blocks in the mounted VDE volume.
    /// </summary>
    public long TotalBlocks { get; init; }

    /// <summary>
    /// Number of free (unallocated) blocks in the mounted VDE volume.
    /// </summary>
    public long FreeBlocks { get; init; }

    /// <summary>
    /// Total number of allocated inodes in the mounted VDE volume.
    /// </summary>
    public long InodeCount { get; init; }
}
