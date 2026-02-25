using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single compression dictionary entry describing a trained dictionary stored
/// in the VDE. DictId 0-255 addresses user dictionaries; 256+ are reserved.
/// </summary>
/// <remarks>
/// Serialized layout (68 bytes, all LE except ContentHash):
/// [DictId:2][AlgorithmId:2][BlockOffset:8][BlockCount:4][DictionarySizeBytes:4]
/// [TrainedUtcTicks:8][SampleCount:8][ContentHash:32]
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct CompressionDictEntry
{
    /// <summary>Serialized size of one entry in bytes.</summary>
    public const int SerializedSize = 68; // 2+2+8+4+4+8+8+32

    /// <summary>Size of the SHA-256 content hash in bytes.</summary>
    public const int HashSize = 32;

    /// <summary>2-byte dictionary identifier (0-255 for user dictionaries, 256+ reserved).</summary>
    public ushort DictId { get; init; }

    /// <summary>
    /// Compression algorithm identifier.
    /// 0=Zstd, 1=Brotli, 2=LZ4, 3=Deflate, 4=Custom.
    /// </summary>
    public ushort AlgorithmId { get; init; }

    /// <summary>Starting block where dictionary data is stored in the VDE.</summary>
    public long BlockOffset { get; init; }

    /// <summary>Number of blocks occupied by the dictionary data.</summary>
    public int BlockCount { get; init; }

    /// <summary>Exact byte size of the dictionary.</summary>
    public int DictionarySizeBytes { get; init; }

    /// <summary>UTC ticks when the dictionary was trained.</summary>
    public long TrainedUtcTicks { get; init; }

    /// <summary>Number of samples used to train this dictionary.</summary>
    public long SampleCount { get; init; }

    /// <summary>32-byte SHA-256 hash of the dictionary data for integrity verification.</summary>
    public byte[] ContentHash { get; init; }

    /// <summary>Writes this entry to the buffer at the specified offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), DictId);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), AlgorithmId);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), BlockOffset);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), BlockCount);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), DictionarySizeBytes);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), TrainedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), SampleCount);
        offset += 8;

        var hash = ContentHash ?? new byte[HashSize];
        hash.AsSpan(0, Math.Min(hash.Length, HashSize)).CopyTo(buffer.Slice(offset, HashSize));
    }

    /// <summary>Reads an entry from the buffer at the specified offset.</summary>
    internal static CompressionDictEntry ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        ushort dictId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        ushort algorithmId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        long blockOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        int dictionarySizeBytes = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        long trainedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long sampleCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        byte[] contentHash = buffer.Slice(offset, HashSize).ToArray();

        return new CompressionDictEntry
        {
            DictId = dictId,
            AlgorithmId = algorithmId,
            BlockOffset = blockOffset,
            BlockCount = blockCount,
            DictionarySizeBytes = dictionarySizeBytes,
            TrainedUtcTicks = trainedUtcTicks,
            SampleCount = sampleCount,
            ContentHash = contentHash
        };
    }
}

/// <summary>
/// Compression Dictionary Region: manages up to 256 trained compression dictionaries
/// (Zstd-style) addressable by 2-byte DictId with O(1) lookup via flat array indexing.
/// Serialized using <see cref="BlockTypeTags.DICT"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [DictionaryCount:4 LE][Reserved:12][entries...]
///   Only non-null entries are serialized, with DictId prefix for sparse encoding.
///   Entries overflow to block 1+ if needed. Each block ends with [UniversalBlockTrailer].
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Compression Dictionary (VREG-16)")]
public sealed class CompressionDictionaryRegion
{
    /// <summary>Maximum number of dictionaries (DictId 0-255).</summary>
    public const int MaxDictionaries = 256;

    /// <summary>Size of the header fields at the start of block 0: DictionaryCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    /// <summary>
    /// Flat array of size 256 for O(1) lookup by DictId index.
    /// Index = DictId, value = entry or null if slot is empty.
    /// </summary>
    private readonly CompressionDictEntry?[] _dictionaries = new CompressionDictEntry?[MaxDictionaries];

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of non-null dictionary entries.</summary>
    public int DictionaryCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < MaxDictionaries; i++)
            {
                if (_dictionaries[i].HasValue)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Registers a new compression dictionary. Validates that DictId is within range,
    /// ContentHash is 32 bytes, and the slot is not already occupied.
    /// </summary>
    /// <param name="entry">The dictionary entry to register.</param>
    /// <exception cref="ArgumentOutOfRangeException">DictId is >= MaxDictionaries.</exception>
    /// <exception cref="ArgumentException">ContentHash is not 32 bytes or slot is already occupied.</exception>
    public void RegisterDictionary(CompressionDictEntry entry)
    {
        if (entry.DictId >= MaxDictionaries)
            throw new ArgumentOutOfRangeException(nameof(entry),
                $"DictId {entry.DictId} must be less than {MaxDictionaries}.");

        if (entry.ContentHash is null || entry.ContentHash.Length != CompressionDictEntry.HashSize)
            throw new ArgumentException(
                $"ContentHash must be exactly {CompressionDictEntry.HashSize} bytes.", nameof(entry));

        if (_dictionaries[entry.DictId].HasValue)
            throw new ArgumentException(
                $"Slot {entry.DictId} is already occupied. Use ReplaceDictionary to overwrite.", nameof(entry));

        _dictionaries[entry.DictId] = entry;
    }

    /// <summary>
    /// Retrieves a dictionary by its DictId via O(1) array index lookup.
    /// </summary>
    /// <param name="dictId">The dictionary identifier (0-255).</param>
    /// <returns>The dictionary entry, or null if the slot is empty.</returns>
    public CompressionDictEntry? GetDictionary(ushort dictId)
    {
        if (dictId >= MaxDictionaries)
            return null;
        return _dictionaries[dictId];
    }

    /// <summary>
    /// Removes a dictionary by setting its slot to null.
    /// </summary>
    /// <param name="dictId">The dictionary identifier to remove.</param>
    /// <returns>True if the slot was occupied and is now removed; false otherwise.</returns>
    public bool RemoveDictionary(ushort dictId)
    {
        if (dictId >= MaxDictionaries)
            return false;
        if (!_dictionaries[dictId].HasValue)
            return false;
        _dictionaries[dictId] = null;
        return true;
    }

    /// <summary>
    /// Replaces an existing dictionary entry (e.g., after retraining).
    /// The slot must already be occupied.
    /// </summary>
    /// <param name="entry">The new dictionary entry.</param>
    /// <returns>True if the slot was occupied and replaced; false if slot was empty.</returns>
    /// <exception cref="ArgumentOutOfRangeException">DictId is >= MaxDictionaries.</exception>
    /// <exception cref="ArgumentException">ContentHash is not 32 bytes.</exception>
    public bool ReplaceDictionary(CompressionDictEntry entry)
    {
        if (entry.DictId >= MaxDictionaries)
            throw new ArgumentOutOfRangeException(nameof(entry),
                $"DictId {entry.DictId} must be less than {MaxDictionaries}.");

        if (entry.ContentHash is null || entry.ContentHash.Length != CompressionDictEntry.HashSize)
            throw new ArgumentException(
                $"ContentHash must be exactly {CompressionDictEntry.HashSize} bytes.", nameof(entry));

        if (!_dictionaries[entry.DictId].HasValue)
            return false;

        _dictionaries[entry.DictId] = entry;
        return true;
    }

    /// <summary>
    /// Returns all dictionaries matching the given algorithm identifier.
    /// </summary>
    /// <param name="algorithmId">Algorithm to filter by (0=Zstd, 1=Brotli, 2=LZ4, 3=Deflate, 4=Custom).</param>
    /// <returns>All matching dictionary entries.</returns>
    public IReadOnlyList<CompressionDictEntry> GetDictionariesByAlgorithm(ushort algorithmId)
    {
        var results = new List<CompressionDictEntry>();
        for (int i = 0; i < MaxDictionaries; i++)
        {
            if (_dictionaries[i].HasValue && _dictionaries[i]!.Value.AlgorithmId == algorithmId)
                results.Add(_dictionaries[i]!.Value);
        }
        return results;
    }

    /// <summary>
    /// Returns all non-null dictionary entries.
    /// </summary>
    public IReadOnlyList<CompressionDictEntry> GetAllDictionaries()
    {
        var results = new List<CompressionDictEntry>();
        for (int i = 0; i < MaxDictionaries; i++)
        {
            if (_dictionaries[i].HasValue)
                results.Add(_dictionaries[i]!.Value);
        }
        return results;
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int dictCount = DictionaryCount;
        if (dictCount == 0)
            return 1;

        int totalEntryBytes = dictCount * CompressionDictEntry.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalEntryBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalEntryBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the compression dictionary region into blocks with UniversalBlockTrailer on each block.
    /// Only non-null entries are serialized sequentially (sparse encoding; DictId in the entry identifies the slot).
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
        int dictCount = DictionaryCount;

        // Block 0 header
        BinaryPrimitives.WriteInt32LittleEndian(buffer, dictCount);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;

        for (int i = 0; i < MaxDictionaries; i++)
        {
            if (!_dictionaries[i].HasValue)
                continue;

            int blockPayloadEnd = payloadSize;

            // Check if we need to overflow to the next block
            if (offset + CompressionDictEntry.SerializedSize > blockPayloadEnd)
            {
                UniversalBlockTrailer.Write(
                    buffer.Slice(currentBlock * blockSize, blockSize),
                    blockSize, BlockTypeTags.DICT, Generation);
                currentBlock++;
                offset = 0;
            }

            _dictionaries[i]!.Value.WriteTo(buffer.Slice(currentBlock * blockSize), offset);
            offset += CompressionDictEntry.SerializedSize;
        }

        // Write trailer for the last block containing data
        UniversalBlockTrailer.Write(
            buffer.Slice(currentBlock * blockSize, blockSize),
            blockSize, BlockTypeTags.DICT, Generation);

        // Write trailers for any remaining empty blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            UniversalBlockTrailer.Write(
                buffer.Slice(blk * blockSize, blockSize),
                blockSize, BlockTypeTags.DICT, Generation);
        }
    }

    /// <summary>
    /// Deserializes a compression dictionary region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="CompressionDictionaryRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static CompressionDictionaryRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"Compression Dictionary block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int dictCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new CompressionDictionaryRegion
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int entriesRead = 0;

        while (entriesRead < dictCount)
        {
            int blockPayloadEnd = payloadSize;

            if (offset + CompressionDictEntry.SerializedSize > blockPayloadEnd)
            {
                currentBlock++;
                offset = 0;
            }

            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            var entry = CompressionDictEntry.ReadFrom(block, offset);
            offset += CompressionDictEntry.SerializedSize;

            if (entry.DictId < MaxDictionaries)
                region._dictionaries[entry.DictId] = entry;

            entriesRead++;
        }

        return region;
    }
}
