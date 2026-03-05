using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single anonymization mapping associating a PII identifier with an anonymized
/// token for GDPR compliance. Raw PII is never stored; only SHA-256 hashes are kept.
/// </summary>
/// <remarks>
/// Serialized layout (108 bytes, all LE except hash/token fields):
/// [MappingId:16][SubjectId:16][PiiType:2][PiiHash:32][AnonymizedToken:32]
/// [CreatedUtcTicks:8][Flags:2]
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct AnonymizationMapping
{
    /// <summary>Serialized size of one mapping in bytes.</summary>
    public const int SerializedSize = 108; // 16+16+2+32+32+8+2

    /// <summary>Size of the PII hash and anonymized token fields in bytes (SHA-256).</summary>
    public const int HashSize = 32;

    /// <summary>Flag bit: mapping is active.</summary>
    public const ushort FlagActive = 0x0001;

    /// <summary>Flag bit: mapping has been erased (GDPR right-to-be-forgotten applied).</summary>
    public const ushort FlagErased = 0x0002;

    /// <summary>Unique mapping identifier.</summary>
    public Guid MappingId { get; init; }

    /// <summary>The data subject (person) this PII belongs to.</summary>
    public Guid SubjectId { get; init; }

    /// <summary>
    /// PII type identifier.
    /// 0=Name, 1=Email, 2=Phone, 3=SSN, 4=Address, 5=DOB, 6=IP, 7=Custom.
    /// </summary>
    public ushort PiiType { get; init; }

    /// <summary>32-byte SHA-256 hash of the original PII (raw PII is never stored).</summary>
    public byte[] PiiHash { get; init; }

    /// <summary>32-byte anonymized replacement token.</summary>
    public byte[] AnonymizedToken { get; init; }

    /// <summary>UTC ticks when the mapping was created.</summary>
    public long CreatedUtcTicks { get; init; }

    /// <summary>
    /// Flags: bit 0 = active, bit 1 = erased (right-to-be-forgotten applied).
    /// </summary>
    public ushort Flags { get; init; }

    /// <summary>Whether this mapping is active (not erased).</summary>
    public bool IsActive => (Flags & FlagActive) != 0 && (Flags & FlagErased) == 0;

    /// <summary>Whether this mapping has been erased via GDPR right-to-be-forgotten.</summary>
    public bool IsErased => (Flags & FlagErased) != 0;

    /// <summary>Writes this mapping to the buffer at the specified offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        MappingId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        SubjectId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), PiiType);
        offset += 2;

        var piiHash = PiiHash ?? new byte[HashSize];
        piiHash.AsSpan(0, Math.Min(piiHash.Length, HashSize)).CopyTo(buffer.Slice(offset, HashSize));
        offset += HashSize;

        var token = AnonymizedToken ?? new byte[HashSize];
        token.AsSpan(0, Math.Min(token.Length, HashSize)).CopyTo(buffer.Slice(offset, HashSize));
        offset += HashSize;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), CreatedUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), Flags);
    }

    /// <summary>Reads a mapping from the buffer at the specified offset.</summary>
    internal static AnonymizationMapping ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var mappingId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        var subjectId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        ushort piiType = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        byte[] piiHash = buffer.Slice(offset, HashSize).ToArray();
        offset += HashSize;
        byte[] anonymizedToken = buffer.Slice(offset, HashSize).ToArray();
        offset += HashSize;
        long createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));

        return new AnonymizationMapping
        {
            MappingId = mappingId,
            SubjectId = subjectId,
            PiiType = piiType,
            PiiHash = piiHash,
            AnonymizedToken = anonymizedToken,
            CreatedUtcTicks = createdUtcTicks,
            Flags = flags
        };
    }
}

/// <summary>
/// Anonymization Table Region: maps PII identifiers to anonymized tokens for GDPR
/// compliance. Supports O(1) lookup by MappingId, indexed retrieval by SubjectId,
/// and bulk erasure for GDPR Article 17 right-to-be-forgotten.
/// Serialized using <see cref="BlockTypeTags.ANON"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [MappingCount:4 LE][Reserved:12][entries...]
///   Entries overflow to block 1+ if needed. Each block ends with [UniversalBlockTrailer].
///
/// On Deserialize, both the MappingId index and SubjectId index are rebuilt.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Anonymization Table (VREG-18)")]
public sealed class AnonymizationTableRegion
{
    /// <summary>Size of the header fields at the start of block 0: MappingCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<AnonymizationMapping> _mappings = new();
    private readonly Dictionary<Guid, int> _mappingIdIndex = new();
    private readonly Dictionary<Guid, List<int>> _subjectIdIndex = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of mappings stored in this table.</summary>
    public int MappingCount => _mappings.Count;

    /// <summary>
    /// Adds a new anonymization mapping. Validates that PiiHash and AnonymizedToken
    /// are exactly 32 bytes.
    /// </summary>
    /// <param name="mapping">The mapping to add.</param>
    /// <exception cref="ArgumentException">Hash fields are not 32 bytes, or MappingId already exists.</exception>
    public void AddMapping(AnonymizationMapping mapping)
    {
        if (mapping.PiiHash is null || mapping.PiiHash.Length != AnonymizationMapping.HashSize)
            throw new ArgumentException(
                $"PiiHash must be exactly {AnonymizationMapping.HashSize} bytes.", nameof(mapping));
        if (mapping.AnonymizedToken is null || mapping.AnonymizedToken.Length != AnonymizationMapping.HashSize)
            throw new ArgumentException(
                $"AnonymizedToken must be exactly {AnonymizationMapping.HashSize} bytes.", nameof(mapping));
        if (_mappingIdIndex.ContainsKey(mapping.MappingId))
            throw new ArgumentException(
                $"MappingId {mapping.MappingId} already exists.", nameof(mapping));

        int index = _mappings.Count;
        _mappings.Add(mapping);
        _mappingIdIndex[mapping.MappingId] = index;

        if (!_subjectIdIndex.TryGetValue(mapping.SubjectId, out var list))
        {
            list = new List<int>();
            _subjectIdIndex[mapping.SubjectId] = list;
        }
        list.Add(index);
    }

    /// <summary>
    /// Retrieves a mapping by its unique identifier via O(1) lookup.
    /// </summary>
    /// <param name="mappingId">The mapping ID to find.</param>
    /// <returns>The matching mapping, or null if not found.</returns>
    public AnonymizationMapping? GetMapping(Guid mappingId)
    {
        if (_mappingIdIndex.TryGetValue(mappingId, out int index))
            return _mappings[index];
        return null;
    }

    /// <summary>
    /// Returns all mappings for a specific data subject.
    /// </summary>
    /// <param name="subjectId">The subject ID to search for.</param>
    /// <returns>All mappings belonging to this subject.</returns>
    public IReadOnlyList<AnonymizationMapping> GetMappingsBySubject(Guid subjectId)
    {
        if (!_subjectIdIndex.TryGetValue(subjectId, out var indices))
            return Array.Empty<AnonymizationMapping>();

        var results = new List<AnonymizationMapping>(indices.Count);
        for (int i = 0; i < indices.Count; i++)
            results.Add(_mappings[indices[i]]);
        return results;
    }

    /// <summary>
    /// Implements GDPR Article 17 right to erasure. After calling this method, the
    /// original PII cannot be recovered from this region. Sets the erased flag on ALL
    /// mappings for the specified subject and zeros out PiiHash and AnonymizedToken bytes.
    /// </summary>
    /// <param name="subjectId">The subject whose PII data should be erased.</param>
    /// <returns>The number of mappings that were erased.</returns>
    public int EraseSubject(Guid subjectId)
    {
        if (!_subjectIdIndex.TryGetValue(subjectId, out var indices))
            return 0;

        int erasedCount = 0;
        for (int i = 0; i < indices.Count; i++)
        {
            int idx = indices[i];
            var mapping = _mappings[idx];

            if (mapping.IsErased)
                continue;

            // Zero out PII data and set erased flag
            var zeroHash = new byte[AnonymizationMapping.HashSize];
            var zeroToken = new byte[AnonymizationMapping.HashSize];

            _mappings[idx] = mapping with
            {
                PiiHash = zeroHash,
                AnonymizedToken = zeroToken,
                Flags = (ushort)((mapping.Flags | AnonymizationMapping.FlagErased) & ~AnonymizationMapping.FlagActive)
            };

            erasedCount++;
        }

        return erasedCount;
    }

    /// <summary>
    /// Checks if all mappings for a subject have the erased flag set.
    /// Returns false if the subject has no mappings.
    /// </summary>
    /// <param name="subjectId">The subject to check.</param>
    /// <returns>True if all mappings for this subject are erased; false otherwise.</returns>
    public bool IsSubjectErased(Guid subjectId)
    {
        if (!_subjectIdIndex.TryGetValue(subjectId, out var indices))
            return false;

        for (int i = 0; i < indices.Count; i++)
        {
            if (!_mappings[indices[i]].IsErased)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns all mappings including erased ones (snapshot).
    /// </summary>
    public IReadOnlyList<AnonymizationMapping> GetAllMappings() => _mappings.AsReadOnly();

    /// <summary>
    /// Returns only active (non-erased) mappings.
    /// </summary>
    public IReadOnlyList<AnonymizationMapping> GetActiveMappings()
    {
        var results = new List<AnonymizationMapping>();
        for (int i = 0; i < _mappings.Count; i++)
        {
            if (!_mappings[i].IsErased)
                results.Add(_mappings[i]);
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
        if (_mappings.Count == 0)
            return 1;

        int totalEntryBytes = _mappings.Count * AnonymizationMapping.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalEntryBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalEntryBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the anonymization table into blocks with UniversalBlockTrailer on each block.
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

        // Block 0 header
        BinaryPrimitives.WriteInt32LittleEndian(buffer, _mappings.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int mappingIndex = 0;

        while (mappingIndex < _mappings.Count)
        {
            // offset is relative to current block start
            // payloadSize accounts for trailer but not for the block-0 header
            if (offset + AnonymizationMapping.SerializedSize > payloadSize)
            {
                UniversalBlockTrailer.Write(
                    buffer.Slice(currentBlock * blockSize, blockSize),
                    blockSize, BlockTypeTags.ANON, Generation);
                currentBlock++;
                offset = 0;
            }

            _mappings[mappingIndex].WriteTo(buffer.Slice(currentBlock * blockSize), offset);
            offset += AnonymizationMapping.SerializedSize;
            mappingIndex++;
        }

        // Write trailer for the last block containing data
        UniversalBlockTrailer.Write(
            buffer.Slice(currentBlock * blockSize, blockSize),
            blockSize, BlockTypeTags.ANON, Generation);

        // Write trailers for any remaining empty blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            UniversalBlockTrailer.Write(
                buffer.Slice(blk * blockSize, blockSize),
                blockSize, BlockTypeTags.ANON, Generation);
        }
    }

    /// <summary>
    /// Deserializes an anonymization table from blocks, verifying block trailers.
    /// Rebuilds both the MappingId index and SubjectId index.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="AnonymizationTableRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static AnonymizationTableRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"Anonymization Table block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int mappingCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        // Cat 14 (finding 895): upper-bound mappingCount before entering the deserialization loop.
        // An arbitrarily large value from a corrupt block device would otherwise trigger
        // ArgumentOutOfRangeException deep in the loop rather than a clean InvalidDataException here.
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int maxPossibleMappingsPerBlock = payloadSize / AnonymizationMapping.SerializedSize;
        int maxBlocks = buffer.Length / blockSize;
        int absoluteMax = maxPossibleMappingsPerBlock * maxBlocks;
        if (mappingCount < 0 || mappingCount > absoluteMax)
            throw new InvalidDataException(
                $"AnonymizationTableRegion: mappingCount {mappingCount} is out of range [0, {absoluteMax}].");

        var region = new AnonymizationTableRegion
        {
            Generation = trailer.GenerationNumber
        };

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int mappingsRead = 0;

        while (mappingsRead < mappingCount)
        {
            int blockPayloadEnd = payloadSize;

            if (offset + AnonymizationMapping.SerializedSize > blockPayloadEnd)
            {
                currentBlock++;
                offset = 0;
            }

            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            var mapping = AnonymizationMapping.ReadFrom(block, offset);
            offset += AnonymizationMapping.SerializedSize;

            // Add to list and rebuild indexes
            int index = region._mappings.Count;
            region._mappings.Add(mapping);
            region._mappingIdIndex[mapping.MappingId] = index;

            if (!region._subjectIdIndex.TryGetValue(mapping.SubjectId, out var list))
            {
                list = new List<int>();
                region._subjectIdIndex[mapping.SubjectId] = list;
            }
            list.Add(index);

            mappingsRead++;
        }

        return region;
    }
}
