using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Streaming Append Region: a ring buffer region that starts at 0% allocation
/// and grows on demand up to a configurable maximum capacity. WriteHead and ReadTail
/// are absolute counters; actual block index = counter % AllocatedBlocks.
/// When the buffer wraps and overwrites old data, ReadTail advances automatically.
/// Serialized as 1 header block using <see cref="BlockTypeTags.STRE"/> type tag.
/// </summary>
/// <remarks>
/// Header block layout:
/// [MaxCapacityBlocks:8 LE][AllocatedBlocks:8 LE][WriteHead:8 LE][ReadTail:8 LE]
/// [EntryCount:8 LE][GrowthIncrement:4 LE][Reserved:zero-fill][UniversalBlockTrailer]
///
/// This region tracks metadata only; actual ring buffer data lives in the
/// allocated block range managed externally.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Streaming Append (VREG-07)")]
public sealed class StreamingAppendRegion
{
    /// <summary>Size of the header fields before reserved area: 8+8+8+8+8+4 = 44 bytes.</summary>
    private const int HeaderFieldsSize = 44;

    /// <summary>Maximum blocks this region can grow to.</summary>
    public long MaxCapacityBlocks { get; }

    /// <summary>Currently allocated blocks (starts at 0).</summary>
    public long AllocatedBlocks { get; private set; }

    /// <summary>Next block to write (absolute position, wraps via modulo).</summary>
    public long WriteHead { get; private set; }

    /// <summary>Oldest unread block (absolute position).</summary>
    public long ReadTail { get; private set; }

    /// <summary>Number of valid entries in the buffer.</summary>
    public long EntryCount { get; private set; }

    /// <summary>Number of blocks to grow by when more space is needed.</summary>
    public int GrowthIncrement { get; }

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Whether the buffer contains no valid entries.</summary>
    public bool IsEmpty => EntryCount == 0;

    /// <summary>Whether the buffer is full and cannot grow further.</summary>
    public bool IsFull => EntryCount == AllocatedBlocks && AllocatedBlocks == MaxCapacityBlocks;

    /// <summary>
    /// Percentage of MaxCapacityBlocks that have been allocated (0.0 to 100.0).
    /// </summary>
    public double AllocationPercentage => MaxCapacityBlocks > 0
        ? (double)AllocatedBlocks / MaxCapacityBlocks * 100
        : 0;

    /// <summary>
    /// Creates a new streaming append region with 0% initial allocation.
    /// </summary>
    /// <param name="maxCapacityBlocks">Maximum number of blocks this region can grow to.</param>
    /// <param name="growthIncrement">Number of blocks to allocate per growth step (default: 16).</param>
    public StreamingAppendRegion(long maxCapacityBlocks, int growthIncrement = 16)
    {
        if (maxCapacityBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCapacityBlocks),
                "Maximum capacity must be positive.");
        if (growthIncrement <= 0)
            throw new ArgumentOutOfRangeException(nameof(growthIncrement),
                "Growth increment must be positive.");

        MaxCapacityBlocks = maxCapacityBlocks;
        GrowthIncrement = growthIncrement;
        AllocatedBlocks = 0;
        WriteHead = 0;
        ReadTail = 0;
        EntryCount = 0;
    }

    /// <summary>
    /// Writes data to the WriteHead block, growing the buffer if needed.
    /// Advances WriteHead after writing. If the buffer wraps and overwrites
    /// old data, ReadTail advances automatically.
    /// </summary>
    /// <param name="data">Data to append (used for length validation only; actual I/O is external).</param>
    /// <param name="blockSize">Block size in bytes for validation.</param>
    /// <returns>The absolute write position where the data was placed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the buffer is full and cannot grow.</exception>
    public long Append(ReadOnlySpan<byte> data, int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (data.Length > payloadSize)
            throw new ArgumentException(
                $"Data length {data.Length} exceeds block payload size {payloadSize}.", nameof(data));

        // Grow if we've used all allocated blocks
        if (AllocatedBlocks == 0 || EntryCount == AllocatedBlocks)
        {
            if (AllocatedBlocks == MaxCapacityBlocks)
            {
                if (EntryCount == AllocatedBlocks)
                    throw new InvalidOperationException(
                        "Streaming append region is full and cannot grow further.");
            }

            long growth = Math.Min(GrowthIncrement, MaxCapacityBlocks - AllocatedBlocks);
            if (growth <= 0)
                throw new InvalidOperationException(
                    "Streaming append region is full and cannot grow further.");

            AllocatedBlocks += growth;
        }

        long writePosition = WriteHead;

        // If buffer is full (all allocated slots used), overwrite oldest entry
        if (EntryCount == AllocatedBlocks)
        {
            // Wrapping: advance ReadTail to discard oldest
            ReadTail++;
            // EntryCount stays the same (we replace an entry)
        }
        else
        {
            EntryCount++;
        }

        WriteHead++;
        return writePosition;
    }

    /// <summary>
    /// Reads the block at the given absolute position if it is still within
    /// the valid range (between ReadTail and WriteHead).
    /// </summary>
    /// <param name="position">Absolute position to read.</param>
    /// <param name="buffer">Target buffer for block data (unused; actual I/O is external).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>True if the position is valid and readable; false if it has been overwritten.</returns>
    public bool TryRead(long position, Span<byte> buffer, int blockSize)
    {
        // Position must be between ReadTail (inclusive) and WriteHead (exclusive)
        if (position < ReadTail || position >= WriteHead)
            return false;

        // The actual block index in the ring = position % AllocatedBlocks
        // Caller is responsible for reading from that physical location
        return true;
    }

    /// <summary>
    /// Moves ReadTail forward by 1, discarding the oldest entry.
    /// </summary>
    public void AdvanceReadTail()
    {
        if (EntryCount == 0)
            return;

        ReadTail++;
        EntryCount--;
    }

    /// <summary>
    /// Resets WriteHead, ReadTail, and EntryCount to zero.
    /// Does NOT deallocate blocks.
    /// </summary>
    public void Reset()
    {
        WriteHead = 0;
        ReadTail = 0;
        EntryCount = 0;
    }

    /// <summary>
    /// Returns the physical block index for an absolute position within the ring buffer.
    /// </summary>
    /// <param name="absolutePosition">Absolute position counter.</param>
    /// <returns>Physical block index (0-based within the allocated range).</returns>
    public long GetPhysicalBlockIndex(long absolutePosition)
    {
        if (AllocatedBlocks == 0)
            throw new InvalidOperationException("No blocks allocated.");
        return absolutePosition % AllocatedBlocks;
    }

    /// <summary>
    /// Serializes the region header into a single block with UniversalBlockTrailer.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        buffer.Slice(0, blockSize).Clear();

        BinaryPrimitives.WriteInt64LittleEndian(buffer, MaxCapacityBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(8), AllocatedBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(16), WriteHead);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(24), ReadTail);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(32), EntryCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(40), GrowthIncrement);

        UniversalBlockTrailer.Write(buffer, blockSize, BlockTypeTags.STRE, Generation);
    }

    /// <summary>
    /// Deserializes a streaming append region from a single header block.
    /// </summary>
    /// <param name="buffer">Source buffer containing the header block.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>A populated <see cref="StreamingAppendRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if the block trailer verification fails.</exception>
    public static StreamingAppendRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        if (!UniversalBlockTrailer.Verify(buffer, blockSize))
            throw new InvalidDataException("Streaming Append Region block trailer verification failed.");

        var trailer = UniversalBlockTrailer.Read(buffer, blockSize);

        long maxCapacity = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        long allocatedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8));
        long writeHead = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(16));
        long readTail = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(24));
        long entryCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(32));
        int growthIncrement = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(40));

        var region = new StreamingAppendRegion(maxCapacity, growthIncrement)
        {
            Generation = trailer.GenerationNumber
        };

        // Restore internal state
        region.AllocatedBlocks = allocatedBlocks;
        region.WriteHead = writeHead;
        region.ReadTail = readTail;
        region.EntryCount = entryCount;

        return region;
    }
}
