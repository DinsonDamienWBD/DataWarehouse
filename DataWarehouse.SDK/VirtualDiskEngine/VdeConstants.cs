using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// Constants for the Virtual Disk Engine (VDE) container format.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public static class VdeConstants
{
    /// <summary>
    /// Magic bytes for DWVD container files: 0x44575644 ("DWVD" in little-endian).
    /// </summary>
    public const uint MagicBytes = 0x44575644u;

    /// <summary>
    /// Current container format version.
    /// </summary>
    public const ushort FormatVersion = 1;

    /// <summary>
    /// Default block size (4 KiB).
    /// </summary>
    public const int DefaultBlockSize = 4096;

    /// <summary>
    /// Minimum supported block size (512 bytes).
    /// </summary>
    public const int MinBlockSize = 512;

    /// <summary>
    /// Maximum supported block size (64 KiB).
    /// </summary>
    public const int MaxBlockSize = 65536;

    /// <summary>
    /// Superblock mirror offset: block 1 (adjacent to primary at block 0).
    /// </summary>
    public const long SuperblockMirrorOffset = 1;

    /// <summary>
    /// Fixed size of a serialized superblock in bytes (512 bytes).
    /// </summary>
    public const int SuperblockSize = 512;
}
