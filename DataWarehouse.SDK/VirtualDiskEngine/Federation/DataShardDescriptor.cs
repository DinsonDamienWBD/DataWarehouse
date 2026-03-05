using System;
using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Health status of a data shard VDE 2.0A instance.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Data Shard (VFED-11)")]
public enum DataShardHealth : byte
{
    /// <summary>Shard is fully operational.</summary>
    Healthy = 0,

    /// <summary>Shard is operational but experiencing issues (e.g., degraded RAID).</summary>
    Degraded = 1,

    /// <summary>Shard is rebuilding data (e.g., after disk replacement).</summary>
    Rebuilding = 2,

    /// <summary>Shard is unreachable or shut down.</summary>
    Offline = 3
}

/// <summary>
/// Level 3 data shard descriptor: points to an actual VDE 2.0A instance with capacity and health tracking.
/// Binary-serializable to a fixed 56-byte layout using BinaryPrimitives.
/// </summary>
/// <remarks>
/// <para>Binary layout (56 bytes, little-endian):</para>
/// <list type="table">
/// <item><term>[0..15]</term><description>DataVdeId (GUID, 16 bytes)</description></item>
/// <item><term>[16..19]</term><description>StartSlot (int32 LE, inclusive)</description></item>
/// <item><term>[20..23]</term><description>EndSlot (int32 LE, exclusive)</description></item>
/// <item><term>[24..31]</term><description>CapacityBlocks (int64 LE)</description></item>
/// <item><term>[32..39]</term><description>UsedBlocks (int64 LE)</description></item>
/// <item><term>[40]</term><description>Health (byte)</description></item>
/// <item><term>[41..47]</term><description>Reserved (7 bytes, zeroed)</description></item>
/// <item><term>[48..55]</term><description>LastHeartbeatUtcTicks (int64 LE)</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Data Shard (VFED-11)")]
public readonly record struct DataShardDescriptor
{
    /// <summary>
    /// Serialized size of a single data shard descriptor in bytes.
    /// </summary>
    public const int SerializedSize = 56;

    /// <summary>
    /// The VDE 2.0A instance identifier for this data shard.
    /// </summary>
    public Guid DataVdeId { get; init; }

    /// <summary>
    /// Beginning of the key-range slot (inclusive).
    /// </summary>
    public int StartSlot { get; init; }

    /// <summary>
    /// End of the key-range slot (exclusive).
    /// </summary>
    public int EndSlot { get; init; }

    /// <summary>
    /// Total block capacity of this data VDE instance.
    /// </summary>
    public long CapacityBlocks { get; init; }

    /// <summary>
    /// Current number of blocks in use.
    /// </summary>
    public long UsedBlocks { get; init; }

    /// <summary>
    /// Current health status of the data shard.
    /// </summary>
    public DataShardHealth Health { get; init; }

    /// <summary>
    /// Last heartbeat timestamp from the data VDE, as UTC ticks.
    /// </summary>
    public long LastHeartbeatUtcTicks { get; init; }

    /// <summary>
    /// Gets the current utilization percentage (0.0 to 100.0).
    /// Returns 0.0 if capacity is zero.
    /// </summary>
    public double UtilizationPercent => CapacityBlocks > 0
        ? (double)UsedBlocks / CapacityBlocks * 100.0
        : 0.0;

    /// <summary>
    /// Returns <c>true</c> if this shard is available for serving requests
    /// (either <see cref="DataShardHealth.Healthy"/> or <see cref="DataShardHealth.Degraded"/>).
    /// </summary>
    public bool IsAvailable => Health == DataShardHealth.Healthy || Health == DataShardHealth.Degraded;

    /// <summary>
    /// Returns <c>true</c> if the given slot falls within this descriptor's [StartSlot, EndSlot) range.
    /// </summary>
    /// <param name="slot">The hash slot to test.</param>
    /// <returns><c>true</c> if the slot is contained; otherwise <c>false</c>.</returns>
    public bool ContainsSlot(int slot) => slot >= StartSlot && slot < EndSlot;

    /// <summary>
    /// Serializes this descriptor to the specified destination span using BinaryPrimitives.
    /// </summary>
    /// <param name="dest">A span of at least <see cref="SerializedSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="dest"/> is smaller than <see cref="SerializedSize"/> bytes.</exception>
    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < SerializedSize)
            throw new ArgumentException($"Destination must be at least {SerializedSize} bytes.", nameof(dest));

        // [0..15] DataVdeId
        DataVdeId.TryWriteBytes(dest[..16]);

        // [16..19] StartSlot
        BinaryPrimitives.WriteInt32LittleEndian(dest[16..], StartSlot);

        // [20..23] EndSlot
        BinaryPrimitives.WriteInt32LittleEndian(dest[20..], EndSlot);

        // [24..31] CapacityBlocks
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..], CapacityBlocks);

        // [32..39] UsedBlocks
        BinaryPrimitives.WriteInt64LittleEndian(dest[32..], UsedBlocks);

        // [40] Health
        dest[40] = (byte)Health;

        // [41..47] Reserved
        dest[41..48].Clear();

        // [48..55] LastHeartbeatUtcTicks
        BinaryPrimitives.WriteInt64LittleEndian(dest[48..], LastHeartbeatUtcTicks);
    }

    /// <summary>
    /// Deserializes a <see cref="DataShardDescriptor"/> from the specified source span.
    /// </summary>
    /// <param name="src">A read-only span of at least <see cref="SerializedSize"/> bytes.</param>
    /// <returns>The deserialized data shard descriptor.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="src"/> is smaller than <see cref="SerializedSize"/> bytes.</exception>
    public static DataShardDescriptor ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < SerializedSize)
            throw new ArgumentException($"Source must be at least {SerializedSize} bytes.", nameof(src));

        return new DataShardDescriptor
        {
            DataVdeId = new Guid(src[..16]),
            StartSlot = BinaryPrimitives.ReadInt32LittleEndian(src[16..]),
            EndSlot = BinaryPrimitives.ReadInt32LittleEndian(src[20..]),
            CapacityBlocks = BinaryPrimitives.ReadInt64LittleEndian(src[24..]),
            UsedBlocks = BinaryPrimitives.ReadInt64LittleEndian(src[32..]),
            Health = (DataShardHealth)src[40],
            LastHeartbeatUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[48..])
        };
    }
}
