using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Specifies the block distribution layout for a compound block device.
/// Each value maps to a well-known RAID level.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: CompoundBlockDevice (CBDV-01)")]
public enum DeviceLayoutType
{
    /// <summary>RAID 0 — blocks striped across all devices for maximum throughput.</summary>
    Striped = 0,

    /// <summary>RAID 1 — every block written to all mirror devices for redundancy.</summary>
    Mirrored = 1,

    /// <summary>RAID 5 — striped with single rotating parity device per stripe group.</summary>
    Parity = 5,

    /// <summary>RAID 6 — striped with dual rotating parity for double-fault tolerance.</summary>
    DoubleParity = 6,

    /// <summary>RAID 10 — mirror pairs striped across the array (stripe of mirrors).</summary>
    StripedMirror = 10
}

/// <summary>
/// Result of mapping a single logical block address to its physical location
/// on a specific device within a compound block device array.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: CompoundBlockDevice (CBDV-01)")]
public readonly struct BlockMapping : IEquatable<BlockMapping>
{
    /// <summary>
    /// Gets the zero-based index of the target device in the compound device array.
    /// </summary>
    public int DeviceIndex { get; }

    /// <summary>
    /// Gets the physical block number on the target device.
    /// </summary>
    public long PhysicalBlock { get; }

    /// <summary>
    /// Initializes a new <see cref="BlockMapping"/>.
    /// </summary>
    /// <param name="deviceIndex">Zero-based device index.</param>
    /// <param name="physicalBlock">Physical block number on the target device.</param>
    public BlockMapping(int deviceIndex, long physicalBlock)
    {
        DeviceIndex = deviceIndex;
        PhysicalBlock = physicalBlock;
    }

    /// <inheritdoc />
    public bool Equals(BlockMapping other) =>
        DeviceIndex == other.DeviceIndex && PhysicalBlock == other.PhysicalBlock;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BlockMapping other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(DeviceIndex, PhysicalBlock);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(BlockMapping left, BlockMapping right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(BlockMapping left, BlockMapping right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"Device[{DeviceIndex}]:Block[{PhysicalBlock}]";
}

/// <summary>
/// Configuration record for compound block device initialization,
/// specifying the RAID layout, stripe geometry, mirror depth, and parity parameters.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: CompoundBlockDevice (CBDV-01)")]
public sealed record CompoundDeviceConfiguration
{
    /// <summary>
    /// Gets the block distribution layout (RAID level).
    /// </summary>
    public DeviceLayoutType Layout { get; }

    /// <summary>
    /// Gets the stripe size in blocks. Must be a power of two. Default is 256.
    /// </summary>
    public int StripeSizeBlocks { get; init; } = 256;

    /// <summary>
    /// Gets the number of mirror copies for <see cref="DeviceLayoutType.Mirrored"/>
    /// and <see cref="DeviceLayoutType.StripedMirror"/> layouts. Default is 2.
    /// </summary>
    public int MirrorCount { get; init; } = 2;

    /// <summary>
    /// Gets the number of parity devices for <see cref="DeviceLayoutType.Parity"/>
    /// (1) and <see cref="DeviceLayoutType.DoubleParity"/> (2) layouts.
    /// </summary>
    public int ParityDeviceCount { get; init; } = 1;

    /// <summary>
    /// Gets whether write-back caching is enabled. When false (default),
    /// writes are write-through and durability is guaranteed on flush.
    /// </summary>
    public bool EnableWriteBackCache { get; init; }

    /// <summary>
    /// Gets an optional override for the logical block size in bytes.
    /// When null, the block size is inherited from the underlying physical devices.
    /// </summary>
    public long? OverrideBlockSize { get; init; }

    /// <summary>
    /// Initializes a new <see cref="CompoundDeviceConfiguration"/> with the specified layout.
    /// </summary>
    /// <param name="layout">The RAID layout type.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="layout"/> is not a defined <see cref="DeviceLayoutType"/> value.
    /// </exception>
    public CompoundDeviceConfiguration(DeviceLayoutType layout)
    {
        if (!Enum.IsDefined(layout))
            throw new ArgumentException($"Undefined layout type: {layout}", nameof(layout));

        Layout = layout;

        // Set sensible parity defaults based on layout
        ParityDeviceCount = layout switch
        {
            DeviceLayoutType.DoubleParity => 2,
            DeviceLayoutType.Parity => 1,
            _ => 1
        };
    }
}
