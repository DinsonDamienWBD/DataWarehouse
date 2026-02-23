using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single cross-VDE reference representing a dw:// fabric link between
/// a source VDE/inode and a target VDE/inode.
/// </summary>
/// <remarks>
/// Serialized layout (74 bytes, all LE):
/// [ReferenceId:16][SourceVdeId:16][TargetVdeId:16]
/// [SourceInodeNumber:8][TargetInodeNumber:8][ReferenceType:2][CreatedUtcTicks:8]
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Cross-VDE Reference entry")]
public readonly record struct VdeReference
{
    /// <summary>Serialized size of one reference in bytes.</summary>
    public const int SerializedSize = 74; // 16+16+16+8+8+2+8

    /// <summary>Reference type: Data link between objects.</summary>
    public const ushort TypeDataLink = 0;

    /// <summary>Reference type: Index link (index referencing data in another VDE).</summary>
    public const ushort TypeIndexLink = 1;

    /// <summary>Reference type: Metadata link.</summary>
    public const ushort TypeMetadataLink = 2;

    /// <summary>Reference type: Fabric link (dw:// protocol-level reference).</summary>
    public const ushort TypeFabricLink = 3;

    /// <summary>Unique identifier for this reference.</summary>
    public Guid ReferenceId { get; init; }

    /// <summary>VDE containing the referencing object.</summary>
    public Guid SourceVdeId { get; init; }

    /// <summary>VDE being referenced.</summary>
    public Guid TargetVdeId { get; init; }

    /// <summary>Inode number in the source VDE.</summary>
    public long SourceInodeNumber { get; init; }

    /// <summary>Inode number in the target VDE (0 if VDE-level reference).</summary>
    public long TargetInodeNumber { get; init; }

    /// <summary>
    /// Reference type.
    /// 0=DataLink, 1=IndexLink, 2=MetadataLink, 3=FabricLink.
    /// </summary>
    public ushort ReferenceType { get; init; }

    /// <summary>UTC ticks when the reference was created.</summary>
    public long CreatedUtcTicks { get; init; }

    /// <summary>Writes this reference to the buffer at the specified offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        ReferenceId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        SourceVdeId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        TargetVdeId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), SourceInodeNumber);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), TargetInodeNumber);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), ReferenceType);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), CreatedUtcTicks);
    }

    /// <summary>Reads a reference from the buffer at the specified offset.</summary>
    internal static VdeReference ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var referenceId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        var sourceVdeId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        var targetVdeId = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        long sourceInodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        long targetInodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        ushort referenceType = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        long createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        return new VdeReference
        {
            ReferenceId = referenceId,
            SourceVdeId = sourceVdeId,
            TargetVdeId = targetVdeId,
            SourceInodeNumber = sourceInodeNumber,
            TargetInodeNumber = targetInodeNumber,
            ReferenceType = referenceType,
            CreatedUtcTicks = createdUtcTicks
        };
    }
}

/// <summary>
/// Cross-VDE Reference Table Region: persists dw:// fabric links between VDE
/// volumes, enabling broken-link detection and cross-VDE navigation.
/// Provides O(1) lookup by reference ID via dictionary index.
/// Serialized using <see cref="BlockTypeTags.XREF"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [ReferenceCount:4 LE][Reserved:12][VdeReference entries...]
///     Entries are packed sequentially and overflow to block 1+ if needed.
///   Each block ends with [UniversalBlockTrailer].
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Cross-VDE Reference Table (VREG-11)")]
public sealed class CrossVdeReferenceRegion
{
    /// <summary>Size of the header fields at the start of block 0: ReferenceCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<VdeReference> _references = new();
    private readonly Dictionary<Guid, int> _indexByReferenceId = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of references stored in the table.</summary>
    public int ReferenceCount => _references.Count;

    /// <summary>
    /// Adds a cross-VDE reference. Rejects duplicate ReferenceIds.
    /// </summary>
    /// <param name="reference">The reference to add.</param>
    /// <exception cref="ArgumentException">A reference with the same ReferenceId already exists.</exception>
    public void AddReference(VdeReference reference)
    {
        if (_indexByReferenceId.ContainsKey(reference.ReferenceId))
            throw new ArgumentException(
                $"A reference with ID {reference.ReferenceId} already exists.",
                nameof(reference));

        _indexByReferenceId[reference.ReferenceId] = _references.Count;
        _references.Add(reference);
    }

    /// <summary>
    /// Removes a reference by its unique identifier.
    /// </summary>
    /// <param name="referenceId">The reference ID to remove.</param>
    /// <returns>True if the reference was found and removed; false otherwise.</returns>
    public bool RemoveReference(Guid referenceId)
    {
        if (!_indexByReferenceId.TryGetValue(referenceId, out int index))
            return false;

        int lastIndex = _references.Count - 1;
        if (index < lastIndex)
        {
            // Swap with last element to maintain contiguous storage
            var lastRef = _references[lastIndex];
            _references[index] = lastRef;
            _indexByReferenceId[lastRef.ReferenceId] = index;
        }

        _references.RemoveAt(lastIndex);
        _indexByReferenceId.Remove(referenceId);
        return true;
    }

    /// <summary>
    /// Looks up a reference by its unique identifier in O(1) time.
    /// </summary>
    /// <param name="referenceId">The reference ID to find.</param>
    /// <returns>The matching reference, or null if not found.</returns>
    public VdeReference? GetReference(Guid referenceId)
    {
        if (_indexByReferenceId.TryGetValue(referenceId, out int index))
            return _references[index];
        return null;
    }

    /// <summary>
    /// Returns all outgoing references from the specified source VDE.
    /// </summary>
    /// <param name="sourceVdeId">The source VDE ID to filter by.</param>
    /// <returns>All references originating from this VDE.</returns>
    public IReadOnlyList<VdeReference> GetReferencesBySource(Guid sourceVdeId)
    {
        var results = new List<VdeReference>();
        for (int i = 0; i < _references.Count; i++)
        {
            if (_references[i].SourceVdeId == sourceVdeId)
                results.Add(_references[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all incoming references targeting the specified VDE.
    /// </summary>
    /// <param name="targetVdeId">The target VDE ID to filter by.</param>
    /// <returns>All references pointing to this VDE.</returns>
    public IReadOnlyList<VdeReference> GetReferencesByTarget(Guid targetVdeId)
    {
        var results = new List<VdeReference>();
        for (int i = 0; i < _references.Count; i++)
        {
            if (_references[i].TargetVdeId == targetVdeId)
                results.Add(_references[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all references of the specified type.
    /// </summary>
    /// <param name="referenceType">The reference type to filter by (0=DataLink, 1=IndexLink, 2=MetadataLink, 3=FabricLink).</param>
    /// <returns>All references of this type.</returns>
    public IReadOnlyList<VdeReference> GetReferencesByType(ushort referenceType)
    {
        var results = new List<VdeReference>();
        for (int i = 0; i < _references.Count; i++)
        {
            if (_references[i].ReferenceType == referenceType)
                results.Add(_references[i]);
        }
        return results;
    }

    /// <summary>
    /// Detects broken dw:// links by scanning references for target VDEs
    /// that are not in the known set of reachable VDE IDs.
    /// </summary>
    /// <param name="knownVdeIds">The set of known/reachable VDE volume IDs.</param>
    /// <returns>ReferenceIds whose TargetVdeId is NOT in the known set.</returns>
    public IReadOnlyList<Guid> DetectBrokenLinks(IReadOnlySet<Guid> knownVdeIds)
    {
        if (knownVdeIds is null)
            throw new ArgumentNullException(nameof(knownVdeIds));

        var brokenLinks = new List<Guid>();
        for (int i = 0; i < _references.Count; i++)
        {
            if (!knownVdeIds.Contains(_references[i].TargetVdeId))
                brokenLinks.Add(_references[i].ReferenceId);
        }
        return brokenLinks;
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required (minimum 1).</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_references.Count == 0)
            return 1;

        int totalRefBytes = _references.Count * VdeReference.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalRefBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalRefBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the cross-VDE reference table into blocks with UniversalBlockTrailer on each block.
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

        // Block 0: Header + reference entries
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _references.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int refIndex = 0;

        while (refIndex < _references.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            if (currentBlock > 0)
                offset = 0;

            while (refIndex < _references.Count)
            {
                if (offset + VdeReference.SerializedSize > blockPayloadEnd)
                    break;

                _references[refIndex].WriteTo(block, offset);
                offset += VdeReference.SerializedSize;
                refIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.XREF, Generation);

            if (refIndex < _references.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no references
        if (_references.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.XREF, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.XREF, Generation);
        }
    }

    /// <summary>
    /// Deserializes a cross-VDE reference table from blocks, verifying block trailers
    /// and rebuilding the dictionary index.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="CrossVdeReferenceRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static CrossVdeReferenceRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"Cross-VDE Reference block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int referenceCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new CrossVdeReferenceRegion
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int refsRead = 0;

        while (refsRead < referenceCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (refsRead < referenceCount && offset + VdeReference.SerializedSize <= blockPayloadEnd)
            {
                var reference = VdeReference.ReadFrom(block, offset);
                region._indexByReferenceId[reference.ReferenceId] = region._references.Count;
                region._references.Add(reference);
                offset += VdeReference.SerializedSize;
                refsRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }
}
