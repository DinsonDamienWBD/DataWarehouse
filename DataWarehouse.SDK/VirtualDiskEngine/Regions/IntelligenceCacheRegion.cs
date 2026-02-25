using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single intelligence cache entry storing AI classification results for a
/// data object, including confidence score, access heat score, and storage tier
/// assignment.
/// </summary>
/// <remarks>
/// Serialized layout (43 bytes, all LE):
/// [ObjectId:16][ClassificationId:2][ConfidenceScore:4][HeatScore:4]
/// [TierAssignment:1][LastClassifiedUtcTicks:8][LastAccessedUtcTicks:8]
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Intelligence Cache entry")]
public readonly record struct IntelligenceCacheEntry
{
    /// <summary>Serialized size of one entry in bytes.</summary>
    public const int SerializedSize = 43; // 16+2+4+4+1+8+8

    /// <summary>Data object this classification covers.</summary>
    public Guid ObjectId { get; init; }

    /// <summary>AI classification category (user-defined enum range).</summary>
    public ushort ClassificationId { get; init; }

    /// <summary>Confidence in the classification (0.0 to 1.0).</summary>
    public float ConfidenceScore { get; init; }

    /// <summary>Access heat score, recency-weighted frequency (0.0 to 1.0).</summary>
    public float HeatScore { get; init; }

    /// <summary>
    /// Storage tier assignment.
    /// 0=Hot, 1=Warm, 2=Cold, 3=Frozen, 4=Archive.
    /// </summary>
    public byte TierAssignment { get; init; }

    /// <summary>UTC ticks when the AI last classified this object.</summary>
    public long LastClassifiedUtcTicks { get; init; }

    /// <summary>UTC ticks when the object was last accessed (for heat calculation).</summary>
    public long LastAccessedUtcTicks { get; init; }

    /// <summary>Writes this entry to the buffer at the specified offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        ObjectId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), ClassificationId);
        offset += 2;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset), ConfidenceScore);
        offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset), HeatScore);
        offset += 4;
        buffer[offset] = TierAssignment;
        offset += 1;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), LastClassifiedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), LastAccessedUtcTicks);
    }

    /// <summary>Reads an entry from the buffer at the specified offset.</summary>
    internal static IntelligenceCacheEntry ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var objectId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        ushort classificationId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        float confidenceScore = BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(offset));
        offset += 4;
        float heatScore = BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(offset));
        offset += 4;
        byte tierAssignment = buffer[offset];
        offset += 1;
        long lastClassifiedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long lastAccessedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        return new IntelligenceCacheEntry
        {
            ObjectId = objectId,
            ClassificationId = classificationId,
            ConfidenceScore = confidenceScore,
            HeatScore = heatScore,
            TierAssignment = tierAssignment,
            LastClassifiedUtcTicks = lastClassifiedUtcTicks,
            LastAccessedUtcTicks = lastAccessedUtcTicks
        };
    }
}

/// <summary>
/// Intelligence Cache Region: stores per-object AI classification results with
/// confidence scores, access heat scores, and storage tier assignments.
/// Provides O(1) lookup by object ID via dictionary index.
/// Serialized using <see cref="BlockTypeTags.INTE"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [EntryCount:4 LE][Reserved:12][IntelligenceCacheEntry entries...]
///     Entries are packed sequentially and overflow to block 1+ if needed.
///   Each block ends with [UniversalBlockTrailer].
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Intelligence Cache (VREG-10)")]
public sealed class IntelligenceCacheRegion
{
    /// <summary>Size of the header fields at the start of block 0: EntryCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<IntelligenceCacheEntry> _entries = new();
    private readonly Dictionary<Guid, int> _indexByObjectId = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of entries stored in the cache.</summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Adds or updates an intelligence cache entry by ObjectId.
    /// If an entry with the same ObjectId exists, it is replaced.
    /// </summary>
    /// <param name="entry">The entry to add or update.</param>
    public void AddOrUpdate(IntelligenceCacheEntry entry)
    {
        if (_indexByObjectId.TryGetValue(entry.ObjectId, out int existingIndex))
        {
            _entries[existingIndex] = entry;
        }
        else
        {
            _indexByObjectId[entry.ObjectId] = _entries.Count;
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Looks up an entry by object ID in O(1) time.
    /// </summary>
    /// <param name="objectId">The object ID to find.</param>
    /// <returns>The matching entry, or null if not found.</returns>
    public IntelligenceCacheEntry? GetByObjectId(Guid objectId)
    {
        if (_indexByObjectId.TryGetValue(objectId, out int index))
            return _entries[index];
        return null;
    }

    /// <summary>
    /// Removes an entry by object ID.
    /// </summary>
    /// <param name="objectId">The object ID to remove.</param>
    /// <returns>True if the entry was found and removed; false otherwise.</returns>
    public bool Remove(Guid objectId)
    {
        if (!_indexByObjectId.TryGetValue(objectId, out int index))
            return false;

        int lastIndex = _entries.Count - 1;
        if (index < lastIndex)
        {
            // Swap with last element to maintain contiguous storage
            var lastEntry = _entries[lastIndex];
            _entries[index] = lastEntry;
            _indexByObjectId[lastEntry.ObjectId] = index;
        }

        _entries.RemoveAt(lastIndex);
        _indexByObjectId.Remove(objectId);
        return true;
    }

    /// <summary>
    /// Returns all entries assigned to the specified storage tier.
    /// </summary>
    /// <param name="tier">The tier to filter by (0=Hot, 1=Warm, 2=Cold, 3=Frozen, 4=Archive).</param>
    /// <returns>All entries in the specified tier.</returns>
    public IReadOnlyList<IntelligenceCacheEntry> GetByTier(byte tier)
    {
        var results = new List<IntelligenceCacheEntry>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].TierAssignment == tier)
                results.Add(_entries[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all entries with a heat score above the specified threshold.
    /// </summary>
    /// <param name="heatThreshold">Minimum heat score (exclusive).</param>
    /// <returns>All entries above the heat threshold.</returns>
    public IReadOnlyList<IntelligenceCacheEntry> GetHotEntries(float heatThreshold)
    {
        var results = new List<IntelligenceCacheEntry>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].HeatScore > heatThreshold)
                results.Add(_entries[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns a snapshot of all entries.
    /// </summary>
    public IReadOnlyList<IntelligenceCacheEntry> GetAllEntries() => _entries.AsReadOnly();

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required (minimum 1).</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_entries.Count == 0)
            return 1;

        int totalEntryBytes = _entries.Count * IntelligenceCacheEntry.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalEntryBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalEntryBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the intelligence cache into blocks with UniversalBlockTrailer on each block.
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

        // Block 0: Header + entries
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _entries.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int entryIndex = 0;

        while (entryIndex < _entries.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            if (currentBlock > 0)
                offset = 0;

            while (entryIndex < _entries.Count)
            {
                if (offset + IntelligenceCacheEntry.SerializedSize > blockPayloadEnd)
                    break;

                _entries[entryIndex].WriteTo(block, offset);
                offset += IntelligenceCacheEntry.SerializedSize;
                entryIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.INTE, Generation);

            if (entryIndex < _entries.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no entries
        if (_entries.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.INTE, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.INTE, Generation);
        }
    }

    /// <summary>
    /// Deserializes an intelligence cache region from blocks, verifying block trailers
    /// and rebuilding the dictionary index.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="IntelligenceCacheRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static IntelligenceCacheRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"Intelligence Cache block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new IntelligenceCacheRegion
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int entriesRead = 0;

        while (entriesRead < entryCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (entriesRead < entryCount && offset + IntelligenceCacheEntry.SerializedSize <= blockPayloadEnd)
            {
                var entry = IntelligenceCacheEntry.ReadFrom(block, offset);
                region._indexByObjectId[entry.ObjectId] = region._entries.Count;
                region._entries.Add(entry);
                offset += IntelligenceCacheEntry.SerializedSize;
                entriesRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }
}
