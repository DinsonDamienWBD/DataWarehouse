using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Identifies a device group within a <see cref="CompoundBlockDevice"/> array.
/// Typically represents a single physical device index or a logical group
/// (e.g., a mirror pair in RAID 10).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device-aware free extents (CBDV-04)")]
public readonly struct DeviceGroupId : IEquatable<DeviceGroupId>, IComparable<DeviceGroupId>
{
    /// <summary>
    /// Gets the numeric identifier for this device group.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Initializes a new <see cref="DeviceGroupId"/> with the given numeric identifier.
    /// </summary>
    /// <param name="value">The device group index. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="value"/> is negative.
    /// </exception>
    public DeviceGroupId(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), $"DeviceGroupId must be non-negative, got {value}.");

        Value = value;
    }

    /// <inheritdoc />
    public bool Equals(DeviceGroupId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeviceGroupId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(DeviceGroupId other) => Value.CompareTo(other.Value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DeviceGroupId left, DeviceGroupId right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DeviceGroupId left, DeviceGroupId right) => !left.Equals(right);

    /// <summary>Less-than operator.</summary>
    public static bool operator <(DeviceGroupId left, DeviceGroupId right) => left.Value < right.Value;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(DeviceGroupId left, DeviceGroupId right) => left.Value > right.Value;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(DeviceGroupId left, DeviceGroupId right) => left.Value <= right.Value;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(DeviceGroupId left, DeviceGroupId right) => left.Value >= right.Value;

    /// <summary>
    /// Implicitly converts an <see cref="int"/> to a <see cref="DeviceGroupId"/>.
    /// </summary>
    public static implicit operator DeviceGroupId(int value) => new(value);

    /// <summary>
    /// Implicitly converts a <see cref="DeviceGroupId"/> to an <see cref="int"/>.
    /// </summary>
    public static implicit operator int(DeviceGroupId id) => id.Value;

    /// <inheritdoc />
    public override string ToString() => $"DevGroup{Value}";
}

/// <summary>
/// Represents a contiguous range of free blocks on a specific device group within
/// a <see cref="CompoundBlockDevice"/> array. Used for device-aware extent allocation
/// to ensure optimal data placement and stripe alignment.
/// </summary>
/// <remarks>
/// <para>
/// Device-aware free extent tracking enables the allocator to make placement decisions
/// that respect the physical layout of the RAID array. For example, allocating related
/// data on the same device group reduces cross-device I/O for sequential reads, while
/// spreading allocations across groups maximizes parallelism for random workloads.
/// </para>
/// <para>
/// Instances are sorted by (<see cref="GroupId"/>, <see cref="StartBlock"/>) to enable
/// efficient coalescing and lookup in sorted collections.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Device-aware free extents (CBDV-04)")]
public readonly struct DeviceAwareFreeExtent
    : IEquatable<DeviceAwareFreeExtent>, IComparable<DeviceAwareFreeExtent>
{
    /// <summary>
    /// Gets the device group containing this free extent.
    /// </summary>
    public DeviceGroupId GroupId { get; }

    /// <summary>
    /// Gets the starting block number of the free extent within the device group.
    /// </summary>
    public long StartBlock { get; }

    /// <summary>
    /// Gets the number of contiguous free blocks in this extent.
    /// </summary>
    public int BlockCount { get; }

    /// <summary>
    /// Initializes a new <see cref="DeviceAwareFreeExtent"/>.
    /// </summary>
    /// <param name="groupId">The device group identifier.</param>
    /// <param name="startBlock">Starting block number. Must be non-negative.</param>
    /// <param name="blockCount">Number of contiguous free blocks. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="startBlock"/> is negative or
    /// <paramref name="blockCount"/> is not positive.
    /// </exception>
    public DeviceAwareFreeExtent(DeviceGroupId groupId, long startBlock, int blockCount)
    {
        if (startBlock < 0)
            throw new ArgumentOutOfRangeException(nameof(startBlock), $"StartBlock must be non-negative, got {startBlock}.");
        if (blockCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockCount), $"BlockCount must be positive, got {blockCount}.");

        GroupId = groupId;
        StartBlock = startBlock;
        BlockCount = blockCount;
    }

    /// <summary>
    /// Gets the exclusive end block (StartBlock + BlockCount).
    /// </summary>
    public long EndBlock => StartBlock + BlockCount;

    /// <inheritdoc />
    public bool Equals(DeviceAwareFreeExtent other) =>
        GroupId == other.GroupId && StartBlock == other.StartBlock && BlockCount == other.BlockCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeviceAwareFreeExtent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GroupId, StartBlock, BlockCount);

    /// <summary>
    /// Compares by (<see cref="GroupId"/>, <see cref="StartBlock"/>) for sorted tracking.
    /// </summary>
    public int CompareTo(DeviceAwareFreeExtent other)
    {
        int groupCmp = GroupId.CompareTo(other.GroupId);
        if (groupCmp != 0)
            return groupCmp;

        return StartBlock.CompareTo(other.StartBlock);
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DeviceAwareFreeExtent left, DeviceAwareFreeExtent right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DeviceAwareFreeExtent left, DeviceAwareFreeExtent right) => !left.Equals(right);

    /// <summary>Less-than operator.</summary>
    public static bool operator <(DeviceAwareFreeExtent left, DeviceAwareFreeExtent right) => left.CompareTo(right) < 0;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(DeviceAwareFreeExtent left, DeviceAwareFreeExtent right) => left.CompareTo(right) > 0;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(DeviceAwareFreeExtent left, DeviceAwareFreeExtent right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(DeviceAwareFreeExtent left, DeviceAwareFreeExtent right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        $"DevGroup{GroupId.Value}:[{StartBlock}..{StartBlock + BlockCount})";
}
