using System.Buffers.Binary;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A record of a single write operation in the WORM region, including the
/// block index, timestamp, data length, and SHA-256 content hash for
/// tamper detection.
/// </summary>
/// <remarks>
/// Serialized layout: [BlockIndex:8 LE][TimestampUtcTicks:8 LE][DataLength:4 LE][ContentHash:32] = 52 bytes.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- WORM Immutable (VREG-08)")]
public readonly struct WormWriteRecord : IEquatable<WormWriteRecord>
{
    /// <summary>Serialized size per record in bytes.</summary>
    public const int SerializedSize = 52;

    /// <summary>Block index where the data was written.</summary>
    public long BlockIndex { get; }

    /// <summary>UTC ticks when the write occurred.</summary>
    public long TimestampUtcTicks { get; }

    /// <summary>Length of the data written in bytes.</summary>
    public int DataLength { get; }

    /// <summary>SHA-256 hash of the written data (32 bytes).</summary>
    public byte[] ContentHash { get; }

    /// <summary>Creates a new WORM write record.</summary>
    /// <param name="blockIndex">Block index of the write.</param>
    /// <param name="timestampUtcTicks">UTC ticks at time of write.</param>
    /// <param name="dataLength">Length of data written.</param>
    /// <param name="contentHash">SHA-256 hash of the data (must be 32 bytes).</param>
    public WormWriteRecord(long blockIndex, long timestampUtcTicks, int dataLength, byte[] contentHash)
    {
        if (contentHash is null)
            throw new ArgumentNullException(nameof(contentHash));
        if (contentHash.Length != 32)
            throw new ArgumentException("Content hash must be exactly 32 bytes (SHA-256).", nameof(contentHash));

        BlockIndex = blockIndex;
        TimestampUtcTicks = timestampUtcTicks;
        DataLength = dataLength;
        ContentHash = contentHash;
    }

    /// <summary>Writes this record to the buffer at the given offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), BlockIndex);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 8), TimestampUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 16), DataLength);
        ContentHash.AsSpan().CopyTo(buffer.Slice(offset + 20, 32));
    }

    /// <summary>Reads a record from the buffer at the given offset.</summary>
    internal static WormWriteRecord ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        long blockIndex = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 8));
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 16));
        byte[] hash = buffer.Slice(offset + 20, 32).ToArray();
        return new WormWriteRecord(blockIndex, timestamp, dataLength, hash);
    }

    /// <inheritdoc />
    public bool Equals(WormWriteRecord other) =>
        BlockIndex == other.BlockIndex
        && TimestampUtcTicks == other.TimestampUtcTicks
        && DataLength == other.DataLength
        && ContentHash.AsSpan().SequenceEqual(other.ContentHash);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WormWriteRecord other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BlockIndex, TimestampUtcTicks, DataLength);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(WormWriteRecord left, WormWriteRecord right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(WormWriteRecord left, WormWriteRecord right) => !left.Equals(right);
}

/// <summary>
/// WORM (Write-Once-Read-Many) Immutable Region: enforces append-only semantics
/// with a high-water mark (HWM). All blocks below the HWM are immutable and
/// cannot be overwritten. Writes are only permitted at the HWM position (sequential append).
/// Each write is recorded with a SHA-256 content hash for tamper detection.
/// Serialized using <see cref="BlockTypeTags.WORM"/> type tag.
/// </summary>
/// <remarks>
/// Header block layout:
/// [HighWaterMark:8 LE][TotalCapacityBlocks:8 LE][RetentionPeriodTicks:8 LE]
/// [CreatedUtcTicks:8 LE][WriteLogCount:4 LE][Reserved:4]
/// [WormWriteRecord entries x WriteLogCount]
/// [UniversalBlockTrailer]
///
/// Write log entries follow header fields and overflow to additional blocks if needed.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- WORM Immutable (VREG-08)")]
public sealed class WormImmutableRegion
{
    /// <summary>Size of the fixed header fields: 8+8+8+8+4+4 = 40 bytes.</summary>
    private const int HeaderFieldsSize = 40;

    private readonly List<WormWriteRecord> _writeLog = new();

    /// <summary>All blocks below this value are immutable (0 = empty region).</summary>
    public long HighWaterMark { get; private set; }

    /// <summary>Total blocks in this region.</summary>
    public long TotalCapacityBlocks { get; }

    /// <summary>Number of blocks that have been written (equals HighWaterMark).</summary>
    public long UsedBlocks => HighWaterMark;

    /// <summary>Number of blocks remaining for writes.</summary>
    public long FreeBlocks => TotalCapacityBlocks - HighWaterMark;

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Minimum retention period (in ticks) before any cleanup is allowed. 0 = forever.</summary>
    public long RetentionPeriodTicks { get; set; }

    /// <summary>UTC ticks when this region was created.</summary>
    public long CreatedUtcTicks { get; set; }

    /// <summary>
    /// Creates a new WORM immutable region with the specified capacity.
    /// </summary>
    /// <param name="totalCapacityBlocks">Total number of blocks in this region.</param>
    public WormImmutableRegion(long totalCapacityBlocks)
    {
        if (totalCapacityBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalCapacityBlocks),
                "Total capacity must be positive.");

        TotalCapacityBlocks = totalCapacityBlocks;
        HighWaterMark = 0;
        CreatedUtcTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Appends data at the current HighWaterMark position, advancing HWM by 1.
    /// Records a WormWriteRecord with a SHA-256 hash of the data.
    /// </summary>
    /// <param name="data">Data to write.</param>
    /// <param name="blockSize">Block size in bytes for validation.</param>
    /// <returns>The block index where data was written.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the region is full.</exception>
    public long Append(ReadOnlySpan<byte> data, int blockSize)
    {
        if (HighWaterMark >= TotalCapacityBlocks)
            throw new InvalidOperationException(
                $"WORM region is full. Capacity: {TotalCapacityBlocks} blocks.");

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (data.Length > payloadSize)
            throw new ArgumentException(
                $"Data length {data.Length} exceeds block payload size {payloadSize}.", nameof(data));

        long blockIndex = HighWaterMark;
        byte[] hash = SHA256.HashData(data);

        _writeLog.Add(new WormWriteRecord(
            blockIndex,
            DateTime.UtcNow.Ticks,
            data.Length,
            hash));

        HighWaterMark++;
        return blockIndex;
    }

    /// <summary>
    /// Enforces WORM high-water mark. Only allows writing at exactly the current
    /// HighWaterMark position (sequential append). Writes below HWM are rejected.
    /// </summary>
    /// <param name="blockIndex">Target block index.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <exception cref="InvalidOperationException">Thrown if blockIndex is below HWM or not at HWM.</exception>
    public void Write(long blockIndex, ReadOnlySpan<byte> data, int blockSize)
    {
        if (blockIndex < HighWaterMark)
            throw new InvalidOperationException(
                $"Cannot write below WORM high-water mark {HighWaterMark}. Block {blockIndex} is immutable.");

        if (blockIndex != HighWaterMark)
            throw new InvalidOperationException(
                $"WORM region requires sequential append. Expected block {HighWaterMark}, got {blockIndex}.");

        Append(data, blockSize);
    }

    /// <summary>
    /// Returns whether the specified block is immutable (below the high-water mark).
    /// </summary>
    /// <param name="blockIndex">Block index to check.</param>
    /// <returns>True if the block is below HWM and thus immutable.</returns>
    public bool IsImmutable(long blockIndex) => blockIndex < HighWaterMark;

    /// <summary>
    /// Returns the recorded write log entries.
    /// </summary>
    public IReadOnlyList<WormWriteRecord> GetWriteLog() => _writeLog.AsReadOnly();

    /// <summary>
    /// Computes the number of blocks required to serialize this region
    /// (header + overflow blocks for the write log).
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Total number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int writeLogBytes = _writeLog.Count * WormWriteRecord.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;

        if (writeLogBytes <= availableInBlock0)
            return 1;

        int overflowBytes = writeLogBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the WORM region into blocks with UniversalBlockTrailer on each block.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least RequiredBlocks * blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int requiredBlocks = RequiredBlocks(blockSize);
        int totalSize = blockSize * requiredBlocks;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        buffer.Slice(0, totalSize).Clear();

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // ── Block 0: Header + write log entries ──
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt64LittleEndian(block0, HighWaterMark);
        BinaryPrimitives.WriteInt64LittleEndian(block0.Slice(8), TotalCapacityBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(block0.Slice(16), RetentionPeriodTicks);
        BinaryPrimitives.WriteInt64LittleEndian(block0.Slice(24), CreatedUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(block0.Slice(32), _writeLog.Count);
        // bytes 36..39 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int recordIndex = 0;
        int currentBlock = 0;

        while (recordIndex < _writeLog.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;
            int startOffset = currentBlock == 0 ? HeaderFieldsSize : 0;

            if (currentBlock > 0)
                offset = 0;

            while (recordIndex < _writeLog.Count && offset + WormWriteRecord.SerializedSize <= blockPayloadEnd)
            {
                _writeLog[recordIndex].WriteTo(block, offset);
                offset += WormWriteRecord.SerializedSize;
                recordIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.WORM, Generation);

            if (recordIndex < _writeLog.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no records were written
        if (_writeLog.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.WORM, Generation);

        // Write trailers for any remaining blocks that may not have had records
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.WORM, Generation);
        }
    }

    /// <summary>
    /// Deserializes a WORM region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="WormImmutableRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static WormImmutableRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"WORM region block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        long highWaterMark = BinaryPrimitives.ReadInt64LittleEndian(block0);
        long totalCapacity = BinaryPrimitives.ReadInt64LittleEndian(block0.Slice(8));
        long retentionTicks = BinaryPrimitives.ReadInt64LittleEndian(block0.Slice(16));
        long createdTicks = BinaryPrimitives.ReadInt64LittleEndian(block0.Slice(24));
        int writeLogCount = BinaryPrimitives.ReadInt32LittleEndian(block0.Slice(32));

        var region = new WormImmutableRegion(totalCapacity)
        {
            Generation = trailer.GenerationNumber,
            RetentionPeriodTicks = retentionTicks
        };
        region.CreatedUtcTicks = createdTicks;
        region.HighWaterMark = highWaterMark;

        // Read write log entries across blocks
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int recordsRead = 0;

        while (recordsRead < writeLogCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (recordsRead < writeLogCount && offset + WormWriteRecord.SerializedSize <= blockPayloadEnd)
            {
                var record = WormWriteRecord.ReadFrom(block, offset);
                region._writeLog.Add(record);
                offset += WormWriteRecord.SerializedSize;
                recordsRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }
}
