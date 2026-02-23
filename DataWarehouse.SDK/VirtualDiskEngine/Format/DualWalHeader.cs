using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Identifies the type of write-ahead log in a dual-WAL configuration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 dual WAL type (VDE2-06)")]
public enum WalType : byte
{
    /// <summary>Metadata WAL: tracks superblock, region directory, inode, and index changes.</summary>
    MetadataWal = 0,

    /// <summary>Data WAL: tracks data block writes for crash-consistent streaming appends.</summary>
    DataWal = 1,
}

/// <summary>
/// Write-ahead log header for the DWVD v2.0 dual-WAL system. Each VDE has a Metadata WAL
/// (always present) and optionally a Data WAL (when the Streaming module is active).
/// The header fits within a single block payload and tracks sequence numbers, offsets,
/// capacity, and checkpoint state.
/// </summary>
/// <remarks>
/// Metadata WAL: sized at 0.5% of total blocks (min 64 blocks). Block type tag: MWAL.
/// Data WAL: sized at 1% of total blocks (min 128 blocks). Block type tag: DWAL.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 WAL header (VDE2-06)")]
public readonly struct WalHeader : IEquatable<WalHeader>
{
    /// <summary>Serialized size of the WAL header in bytes.</summary>
    public const int SerializedSize = 82;

    /// <summary>Minimum number of blocks for a Metadata WAL.</summary>
    public const long MinMetadataWalBlocks = 64;

    /// <summary>Minimum number of blocks for a Data WAL.</summary>
    public const long MinDataWalBlocks = 128;

    /// <summary>Metadata WAL sizing factor (0.5% of total blocks).</summary>
    public const double MetadataWalFactor = 0.005;

    /// <summary>Data WAL sizing factor (1% of total blocks).</summary>
    public const double DataWalFactor = 0.01;

    /// <summary>WAL type (Metadata or Data).</summary>
    public WalType Type { get; }

    /// <summary>WAL format version (currently 1).</summary>
    public uint Version { get; }

    /// <summary>First sequence number in the WAL.</summary>
    public long SequenceStart { get; }

    /// <summary>Last committed sequence number.</summary>
    public long SequenceEnd { get; }

    /// <summary>Byte offset of the oldest WAL entry.</summary>
    public long HeadOffset { get; }

    /// <summary>Byte offset of the write cursor (next write position).</summary>
    public long TailOffset { get; }

    /// <summary>Total number of entries written to this WAL.</summary>
    public long TotalEntries { get; }

    /// <summary>Maximum capacity of this WAL in blocks.</summary>
    public long MaxBlocks { get; }

    /// <summary>Number of blocks currently used by WAL entries.</summary>
    public long UsedBlocks { get; }

    /// <summary>WAL creation timestamp (UTC ticks).</summary>
    public long CreatedTimestampUtc { get; }

    /// <summary>Timestamp of the last checkpoint operation (UTC ticks).</summary>
    public long LastCheckpointTimestamp { get; }

    /// <summary>Number of entries between automatic checkpoints.</summary>
    public uint CheckpointInterval { get; }

    /// <summary>True if the WAL was properly closed (no crash recovery needed).</summary>
    public bool IsClean { get; }

    /// <summary>Creates a WAL header with all fields specified.</summary>
    public WalHeader(
        WalType type,
        uint version,
        long sequenceStart,
        long sequenceEnd,
        long headOffset,
        long tailOffset,
        long totalEntries,
        long maxBlocks,
        long usedBlocks,
        long createdTimestampUtc,
        long lastCheckpointTimestamp,
        uint checkpointInterval,
        bool isClean)
    {
        Type = type;
        Version = version;
        SequenceStart = sequenceStart;
        SequenceEnd = sequenceEnd;
        HeadOffset = headOffset;
        TailOffset = tailOffset;
        TotalEntries = totalEntries;
        MaxBlocks = maxBlocks;
        UsedBlocks = usedBlocks;
        CreatedTimestampUtc = createdTimestampUtc;
        LastCheckpointTimestamp = lastCheckpointTimestamp;
        CheckpointInterval = checkpointInterval;
        IsClean = isClean;
    }

    // ── Factory Methods ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh Metadata WAL header with default settings.
    /// </summary>
    /// <param name="maxBlocks">Maximum capacity in blocks.</param>
    public static WalHeader CreateMetadataWal(long maxBlocks)
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        return new WalHeader(
            type: WalType.MetadataWal,
            version: 1,
            sequenceStart: 0,
            sequenceEnd: 0,
            headOffset: 0,
            tailOffset: 0,
            totalEntries: 0,
            maxBlocks: maxBlocks,
            usedBlocks: 1, // header block itself
            createdTimestampUtc: now,
            lastCheckpointTimestamp: 0,
            checkpointInterval: 1024,
            isClean: true);
    }

    /// <summary>
    /// Creates a fresh Data WAL header with default settings.
    /// </summary>
    /// <param name="maxBlocks">Maximum capacity in blocks.</param>
    public static WalHeader CreateDataWal(long maxBlocks)
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        return new WalHeader(
            type: WalType.DataWal,
            version: 1,
            sequenceStart: 0,
            sequenceEnd: 0,
            headOffset: 0,
            tailOffset: 0,
            totalEntries: 0,
            maxBlocks: maxBlocks,
            usedBlocks: 1, // header block itself
            createdTimestampUtc: now,
            lastCheckpointTimestamp: 0,
            checkpointInterval: 4096,
            isClean: true);
    }

    /// <summary>
    /// Calculates the number of blocks for a Metadata WAL based on total VDE blocks.
    /// Returns 0.5% of totalBlocks, with a minimum of 64 blocks.
    /// </summary>
    public static long CalculateMetadataWalBlocks(long totalBlocks)
        => Math.Max(MinMetadataWalBlocks, (long)Math.Ceiling(totalBlocks * MetadataWalFactor));

    /// <summary>
    /// Calculates the number of blocks for a Data WAL based on total VDE blocks.
    /// Returns 1% of totalBlocks, with a minimum of 128 blocks.
    /// </summary>
    public static long CalculateDataWalBlocks(long totalBlocks)
        => Math.Max(MinDataWalBlocks, (long)Math.Ceiling(totalBlocks * DataWalFactor));

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes this WAL header into the target buffer.
    /// </summary>
    /// <param name="header">The header to serialize.</param>
    /// <param name="buffer">Destination buffer, must be at least <see cref="SerializedSize"/> bytes.</param>
    public static void Serialize(in WalHeader header, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        int offset = 0;

        buffer[offset] = (byte)header.Type;
        offset += 1;

        // 1 byte padding for alignment
        buffer[offset] = 0;
        offset += 1;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), header.Version);
        offset += 4;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.SequenceStart);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.SequenceEnd);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.HeadOffset);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.TailOffset);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.TotalEntries);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.MaxBlocks);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.UsedBlocks);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.CreatedTimestampUtc);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), header.LastCheckpointTimestamp);
        offset += 8;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), header.CheckpointInterval);
        offset += 4;

        buffer[offset] = header.IsClean ? (byte)1 : (byte)0;
        // offset += 1; // final byte
    }

    /// <summary>
    /// Deserializes a WAL header from the source buffer.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least <see cref="SerializedSize"/> bytes.</param>
    /// <returns>The deserialized WAL header.</returns>
    public static WalHeader Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        int offset = 0;

        var type = (WalType)buffer[offset];
        offset += 2; // type + padding

        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var sequenceStart = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var sequenceEnd = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var headOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var tailOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var totalEntries = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var maxBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var usedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var createdTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var lastCheckpointTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var checkpointInterval = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var isClean = buffer[offset] != 0;

        return new WalHeader(type, version, sequenceStart, sequenceEnd,
            headOffset, tailOffset, totalEntries, maxBlocks, usedBlocks,
            createdTimestamp, lastCheckpointTimestamp, checkpointInterval, isClean);
    }

    // ── Equality ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(WalHeader other) =>
        Type == other.Type
        && Version == other.Version
        && SequenceStart == other.SequenceStart
        && SequenceEnd == other.SequenceEnd
        && MaxBlocks == other.MaxBlocks
        && IsClean == other.IsClean;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WalHeader other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, Version, MaxBlocks, SequenceEnd);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(WalHeader left, WalHeader right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(WalHeader left, WalHeader right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"WAL({Type}, V{Version}, Seq={SequenceStart}-{SequenceEnd}, Blocks={UsedBlocks}/{MaxBlocks}, Clean={IsClean})";
}
