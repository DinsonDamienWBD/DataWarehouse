using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Flags describing the state and capabilities of a region.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 region flags (VDE2-02)")]
public enum RegionFlags : uint
{
    /// <summary>No flags set (free slot).</summary>
    None = 0,

    /// <summary>Region is active and in use.</summary>
    Active = 1 << 0,

    /// <summary>Region data is encrypted.</summary>
    Encrypted = 1 << 1,

    /// <summary>Region data is compressed.</summary>
    Compressed = 1 << 2,

    /// <summary>Region is write-once-read-many.</summary>
    Worm = 1 << 3,

    /// <summary>Region is read-only.</summary>
    ReadOnly = 1 << 4,

    /// <summary>Region is currently being migrated.</summary>
    Migrating = 1 << 5,
}

/// <summary>
/// A single 32-byte region pointer entry describing a contiguous region of blocks.
/// Each slot identifies a region by its block type tag and tracks its block allocation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 region pointer (VDE2-02)")]
public readonly struct RegionPointer : IEquatable<RegionPointer>
{
    /// <summary>Serialized size of a single region pointer in bytes.</summary>
    public const int SerializedSize = 32;

    /// <summary>Block type tag identifying the region type (e.g. INOD, TAGI, BMAP).</summary>
    public uint RegionTypeId { get; }

    /// <summary>Region state and capability flags.</summary>
    public RegionFlags Flags { get; }

    /// <summary>First block number of the region.</summary>
    public long StartBlock { get; }

    /// <summary>Total number of blocks in the region.</summary>
    public long BlockCount { get; }

    /// <summary>Number of blocks actually used within the region.</summary>
    public long UsedBlocks { get; }

    /// <summary>Creates a new region pointer with all fields specified.</summary>
    public RegionPointer(uint regionTypeId, RegionFlags flags, long startBlock, long blockCount, long usedBlocks)
    {
        RegionTypeId = regionTypeId;
        Flags = flags;
        StartBlock = startBlock;
        BlockCount = blockCount;
        UsedBlocks = usedBlocks;
    }

    /// <summary>Returns true if this slot is in use (Active flag set).</summary>
    public bool IsActive => (Flags & RegionFlags.Active) != 0;

    /// <summary>Returns true if this is a free (unused) slot.</summary>
    public bool IsFree => Flags == RegionFlags.None && RegionTypeId == 0;

    /// <summary>
    /// Serializes this region pointer into exactly 32 bytes.
    /// Layout: [RegionTypeId:4][Flags:4][StartBlock:8][BlockCount:8][UsedBlocks:8]
    /// </summary>
    public static void Serialize(in RegionPointer rp, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, rp.RegionTypeId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4), (uint)rp.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(8), rp.StartBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(16), rp.BlockCount);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(24), rp.UsedBlocks);
    }

    /// <summary>
    /// Deserializes a region pointer from a 32-byte buffer.
    /// </summary>
    public static RegionPointer Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        var regionTypeId = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var flags = (RegionFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        var startBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8));
        var blockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(16));
        var usedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(24));

        return new RegionPointer(regionTypeId, flags, startBlock, blockCount, usedBlocks);
    }

    /// <inheritdoc />
    public bool Equals(RegionPointer other) =>
        RegionTypeId == other.RegionTypeId
        && Flags == other.Flags
        && StartBlock == other.StartBlock
        && BlockCount == other.BlockCount
        && UsedBlocks == other.UsedBlocks;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RegionPointer other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RegionTypeId, Flags, StartBlock, BlockCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RegionPointer left, RegionPointer right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RegionPointer left, RegionPointer right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"Region({BlockTypeTags.TagToString(RegionTypeId)}, Start={StartBlock}, Count={BlockCount}, Used={UsedBlocks}, Flags={Flags})";
}

/// <summary>
/// Block 1 of the superblock group: Region Pointer Table.
/// Contains <see cref="MaxSlots"/> (127) region pointer slots, each 32 bytes,
/// occupying 4064 bytes of a block. The remaining 16 bytes before the universal
/// block trailer are reserved (zero-filled).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 region pointer table (VDE2-02)")]
public sealed class RegionPointerTable
{
    /// <summary>Maximum number of region pointer slots (127).</summary>
    public const int MaxSlots = FormatConstants.MaxRegionSlots;

    private readonly RegionPointer[] _entries;

    /// <summary>
    /// Creates a new region pointer table with all slots initialized to empty.
    /// </summary>
    public RegionPointerTable()
    {
        _entries = new RegionPointer[MaxSlots];
    }

    /// <summary>
    /// Creates a region pointer table from an existing array of entries.
    /// </summary>
    /// <param name="entries">Array of region pointers (must have exactly <see cref="MaxSlots"/> elements).</param>
    public RegionPointerTable(RegionPointer[] entries)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (entries.Length != MaxSlots)
            throw new ArgumentException($"Entries array must have exactly {MaxSlots} elements.", nameof(entries));
        _entries = entries;
    }

    /// <summary>
    /// Gets the region pointer at the specified slot index.
    /// </summary>
    /// <param name="index">Slot index (0 to <see cref="MaxSlots"/> - 1).</param>
    /// <returns>The region pointer at the specified index.</returns>
    public RegionPointer GetSlot(int index)
    {
        if ((uint)index >= MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index must be 0..{MaxSlots - 1}.");
        return _entries[index];
    }

    /// <summary>
    /// Sets the region pointer at the specified slot index.
    /// </summary>
    /// <param name="index">Slot index (0 to <see cref="MaxSlots"/> - 1).</param>
    /// <param name="pointer">The region pointer to set.</param>
    public void SetSlot(int index, RegionPointer pointer)
    {
        if ((uint)index >= MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index must be 0..{MaxSlots - 1}.");
        _entries[index] = pointer;
    }

    /// <summary>
    /// Finds the first slot containing a region with the specified type ID.
    /// </summary>
    /// <param name="regionTypeId">The block type tag to search for.</param>
    /// <returns>Slot index if found, or -1 if not found.</returns>
    public int FindRegion(uint regionTypeId)
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            if (_entries[i].RegionTypeId == regionTypeId && _entries[i].IsActive)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Finds the first free (unused) slot in the table.
    /// </summary>
    /// <returns>Slot index if found, or -1 if the table is full.</returns>
    public int FindFreeSlot()
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            if (_entries[i].IsFree)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Gets the count of active (in-use) region slots.
    /// </summary>
    public int ActiveRegionCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (_entries[i].IsActive) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Serializes the region pointer table into a block-sized buffer.
    /// Writes 127 slots x 32 bytes = 4064 bytes, followed by 16 reserved bytes,
    /// leaving the last <see cref="FormatConstants.UniversalBlockTrailerSize"/> bytes
    /// for the universal block trailer.
    /// </summary>
    public static void Serialize(in RegionPointerTable table, Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        buffer.Slice(0, blockSize).Clear();

        for (int i = 0; i < MaxSlots; i++)
        {
            int offset = i * RegionPointer.SerializedSize;
            RegionPointer.Serialize(table._entries[i], buffer.Slice(offset));
        }
        // Remaining bytes between slot data and trailer are already zero (reserved).
    }

    /// <summary>
    /// Deserializes a region pointer table from a block-sized buffer.
    /// </summary>
    public static RegionPointerTable Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        var entries = new RegionPointer[MaxSlots];
        for (int i = 0; i < MaxSlots; i++)
        {
            int offset = i * RegionPointer.SerializedSize;
            entries[i] = RegionPointer.Deserialize(buffer.Slice(offset));
        }

        return new RegionPointerTable(entries);
    }
}
