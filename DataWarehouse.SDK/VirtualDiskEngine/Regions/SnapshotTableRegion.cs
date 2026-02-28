using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single snapshot entry in the Snapshot Table, recording copy-on-write
/// snapshot metadata with parent-child relationships. No data blocks are
/// copied during snapshot creation; only metadata pointers are recorded.
/// </summary>
/// <remarks>
/// Serialized layout:
/// [SnapshotId:16][ParentSnapshotId:16][CreatedUtcTicks:8 LE]
/// [InodeTableBlockOffset:8 LE][InodeTableBlockCount:8 LE]
/// [DataBlockCount:8 LE][Flags:2 LE][LabelLength:2 LE][Label:variable, max 64]
///
/// Fixed overhead: 68 bytes + variable Label.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Snapshot Table entry")]
public readonly record struct SnapshotEntry
{
    /// <summary>Fixed portion of the serialized size (excluding variable fields): 68 bytes.</summary>
    public const int FixedSize = 68; // 16+16+8+8+8+8+2+2

    /// <summary>Maximum length of the label in bytes.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>Flag bit: snapshot is read-only.</summary>
    public const ushort FlagReadOnly = 0x0001;

    /// <summary>Flag bit: snapshot is deleted (tombstone).</summary>
    public const ushort FlagDeleted = 0x0002;

    /// <summary>Flag bit: this is a base snapshot.</summary>
    public const ushort FlagBaseSnapshot = 0x0004;

    /// <summary>Unique snapshot identifier.</summary>
    public Guid SnapshotId { get; init; }

    /// <summary>Parent snapshot identifier (Guid.Empty for root/first snapshot).</summary>
    public Guid ParentSnapshotId { get; init; }

    /// <summary>UTC ticks when this snapshot was created.</summary>
    public long CreatedUtcTicks { get; init; }

    /// <summary>
    /// Block offset of the inode table snapshot. Under CoW semantics this points
    /// to shared blocks that are only duplicated on modification.
    /// </summary>
    public long InodeTableBlockOffset { get; init; }

    /// <summary>Block count of the inode table at snapshot time.</summary>
    public long InodeTableBlockCount { get; init; }

    /// <summary>
    /// Total data blocks referenced at snapshot time. This is metadata only --
    /// no data blocks are copied when creating a snapshot.
    /// </summary>
    public long DataBlockCount { get; init; }

    /// <summary>
    /// Snapshot flags.
    /// Bit 0: readonly, bit 1: deleted (tombstone), bit 2: base snapshot.
    /// </summary>
    public ushort Flags { get; init; }

    /// <summary>UTF-8 label (max 64 bytes, variable length).</summary>
    public byte[] Label { get; init; }

    /// <summary>
    /// Serialized size of this entry in bytes.
    /// </summary>
    public int SerializedSize => FixedSize + (Label?.Length ?? 0);

    /// <summary>Writes this entry to the buffer at the specified offset, returning bytes written.</summary>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        int start = offset;

        SnapshotId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        ParentSnapshotId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), CreatedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), InodeTableBlockOffset);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), InodeTableBlockCount);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), DataBlockCount);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), Flags);
        offset += 2;

        var label = Label ?? Array.Empty<byte>();
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), (ushort)label.Length);
        offset += 2;
        label.AsSpan().CopyTo(buffer.Slice(offset));
        offset += label.Length;

        return offset - start;
    }

    /// <summary>Reads an entry from the buffer at the specified offset.</summary>
    internal static SnapshotEntry ReadFrom(ReadOnlySpan<byte> buffer, int offset, out int bytesRead)
    {
        int start = offset;

        var snapshotId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        var parentSnapshotId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        long createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long inodeTableBlockOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long inodeTableBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long dataBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        ushort labelLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        if (labelLen > MaxLabelLength || offset + labelLen > buffer.Length)
            throw new InvalidDataException($"SnapshotEntry labelLen {labelLen} exceeds MaxLabelLength {MaxLabelLength} or buffer bounds.");
        byte[] label = buffer.Slice(offset, labelLen).ToArray();
        offset += labelLen;

        bytesRead = offset - start;
        return new SnapshotEntry
        {
            SnapshotId = snapshotId,
            ParentSnapshotId = parentSnapshotId,
            CreatedUtcTicks = createdUtcTicks,
            InodeTableBlockOffset = inodeTableBlockOffset,
            InodeTableBlockCount = inodeTableBlockCount,
            DataBlockCount = dataBlockCount,
            Flags = flags,
            Label = label
        };
    }
}

/// <summary>
/// Snapshot Table Region: stores <see cref="SnapshotEntry"/> records implementing
/// a copy-on-write snapshot registry with parent-child relationships and O(1)
/// lookup by snapshot ID. Serialized using <see cref="BlockTypeTags.SNAP"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [SnapshotCount:4 LE][Reserved:12][SnapshotEntry entries...]
///     Entries are packed sequentially and overflow to block 1+ if needed.
///   Each block ends with [UniversalBlockTrailer].
///
/// Snapshot creation is metadata-only: the inode table block offset and block
/// counts are recorded but no data blocks are duplicated. Copy-on-write semantics
/// mean shared blocks are only copied when modified after the snapshot point.
///
/// Deletion uses tombstone flags (bit 1 in Flags) rather than physical removal
/// to preserve parent-child chain integrity.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Snapshot Table (VREG-13)")]
public sealed class SnapshotTableRegion
{
    /// <summary>Size of the header fields at the start of block 0: SnapshotCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<SnapshotEntry> _snapshots = new();
    private readonly Dictionary<Guid, int> _indexBySnapshotId = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of snapshots stored in this table (including tombstoned).</summary>
    public int SnapshotCount => _snapshots.Count;

    /// <summary>
    /// Creates a new snapshot entry in the table. No data blocks are copied;
    /// this method records metadata pointers only. The inode table offset and
    /// block counts describe which blocks are shared under CoW semantics.
    /// </summary>
    /// <param name="entry">The snapshot entry to create.</param>
    /// <exception cref="ArgumentException">
    /// Thrown if the label exceeds 64 bytes or a snapshot with the same ID already exists.
    /// </exception>
    public void CreateSnapshot(SnapshotEntry entry)
    {
        if (entry.Label is not null && entry.Label.Length > SnapshotEntry.MaxLabelLength)
            throw new ArgumentException(
                $"Label length {entry.Label.Length} exceeds maximum {SnapshotEntry.MaxLabelLength}.",
                nameof(entry));

        if (_indexBySnapshotId.ContainsKey(entry.SnapshotId))
            throw new ArgumentException(
                $"A snapshot with ID {entry.SnapshotId} already exists.",
                nameof(entry));

        _indexBySnapshotId[entry.SnapshotId] = _snapshots.Count;
        _snapshots.Add(entry);
    }

    /// <summary>
    /// Retrieves a snapshot by its unique identifier in O(1) time.
    /// </summary>
    /// <param name="snapshotId">The snapshot ID to look up.</param>
    /// <returns>The matching snapshot entry, or null if not found.</returns>
    public SnapshotEntry? GetSnapshot(Guid snapshotId)
    {
        if (_indexBySnapshotId.TryGetValue(snapshotId, out int index))
            return _snapshots[index];

        return null;
    }

    /// <summary>
    /// Marks a snapshot as deleted by setting the tombstone flag. The entry is
    /// not physically removed to preserve parent-child chain integrity.
    /// </summary>
    /// <param name="snapshotId">The snapshot ID to delete.</param>
    /// <returns>True if the snapshot was found and marked deleted; false otherwise.</returns>
    public bool DeleteSnapshot(Guid snapshotId)
    {
        if (!_indexBySnapshotId.TryGetValue(snapshotId, out int index))
            return false;

        var existing = _snapshots[index];
        if ((existing.Flags & SnapshotEntry.FlagDeleted) != 0)
            return false; // Already tombstoned

        _snapshots[index] = existing with { Flags = (ushort)(existing.Flags | SnapshotEntry.FlagDeleted) };
        return true;
    }

    /// <summary>
    /// Returns all direct child snapshots of the specified parent.
    /// </summary>
    /// <param name="parentId">The parent snapshot ID.</param>
    /// <returns>All snapshots whose ParentSnapshotId matches.</returns>
    public IReadOnlyList<SnapshotEntry> GetChildSnapshots(Guid parentId)
    {
        var results = new List<SnapshotEntry>();
        for (int i = 0; i < _snapshots.Count; i++)
        {
            if (_snapshots[i].ParentSnapshotId == parentId)
                results.Add(_snapshots[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all snapshots excluding those marked as deleted (tombstoned).
    /// </summary>
    public IReadOnlyList<SnapshotEntry> GetAllSnapshots()
    {
        var results = new List<SnapshotEntry>();
        for (int i = 0; i < _snapshots.Count; i++)
        {
            if ((_snapshots[i].Flags & SnapshotEntry.FlagDeleted) == 0)
                results.Add(_snapshots[i]);
        }
        return results;
    }

    /// <summary>
    /// Walks the ParentSnapshotId chain from the given snapshot to the root,
    /// returning the full ancestor chain (from the specified snapshot to root).
    /// </summary>
    /// <param name="snapshotId">The snapshot ID to start the chain walk from.</param>
    /// <returns>
    /// The chain from the specified snapshot to the root (first element is the
    /// specified snapshot, last element is the root with ParentSnapshotId = Guid.Empty).
    /// Returns empty if the snapshot is not found.
    /// </returns>
    public IReadOnlyList<SnapshotEntry> GetSnapshotChain(Guid snapshotId)
    {
        var chain = new List<SnapshotEntry>();
        var visited = new HashSet<Guid>();
        var currentId = snapshotId;

        while (currentId != Guid.Empty && visited.Add(currentId))
        {
            if (!_indexBySnapshotId.TryGetValue(currentId, out int index))
                break;

            var entry = _snapshots[index];
            chain.Add(entry);
            currentId = entry.ParentSnapshotId;
        }

        return chain;
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this table.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_snapshots.Count == 0)
            return 1;

        int totalSnapshotBytes = 0;
        for (int i = 0; i < _snapshots.Count; i++)
            totalSnapshotBytes += _snapshots[i].SerializedSize;

        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalSnapshotBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalSnapshotBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the snapshot table into blocks with UniversalBlockTrailer on each block.
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

        // Block 0: Header + snapshot entries
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _snapshots.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int snapshotIndex = 0;

        while (snapshotIndex < _snapshots.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            if (currentBlock > 0)
                offset = 0;

            while (snapshotIndex < _snapshots.Count)
            {
                int entrySize = _snapshots[snapshotIndex].SerializedSize;
                if (offset + entrySize > blockPayloadEnd)
                    break;

                _snapshots[snapshotIndex].WriteTo(block, offset);
                offset += entrySize;
                snapshotIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.SNAP, Generation);

            if (snapshotIndex < _snapshots.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no snapshots
        if (_snapshots.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.SNAP, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.SNAP, Generation);
        }
    }

    /// <summary>
    /// Deserializes a snapshot table region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="SnapshotTableRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static SnapshotTableRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"Snapshot Table block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int snapshotCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new SnapshotTableRegion
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int snapshotsRead = 0;

        while (snapshotsRead < snapshotCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (snapshotsRead < snapshotCount && offset < blockPayloadEnd)
            {
                // Check if there's enough room for at least the fixed portion
                if (offset + SnapshotEntry.FixedSize > blockPayloadEnd)
                    break;

                var entry = SnapshotEntry.ReadFrom(block, offset, out int bytesRead);
                region._indexBySnapshotId[entry.SnapshotId] = region._snapshots.Count;
                region._snapshots.Add(entry);
                offset += bytesRead;
                snapshotsRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }
}
