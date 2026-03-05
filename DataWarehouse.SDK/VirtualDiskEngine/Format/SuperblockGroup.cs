using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Coordinates all 4 blocks of the DWVD v2.0 superblock group:
/// Block 0 (Primary Superblock), Block 1 (Region Pointer Table),
/// Block 2 (Extended Metadata), Block 3 (Integrity Anchor).
/// Supports mirror layout at blocks 4-7 for crash resilience.
/// Each block ends with a 16-byte Universal Block Trailer:
/// [BlockTypeTag:4][GenerationNumber:4][XxHash64:8].
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 superblock group coordinator (VDE2-02)")]
public sealed class SuperblockGroup
{
    /// <summary>Number of blocks in one superblock group (primary or mirror).</summary>
    public const int BlockCount = FormatConstants.SuperblockGroupBlocks;

    /// <summary>The primary superblock (Block 0).</summary>
    public SuperblockV2 PrimarySuperblock { get; }

    /// <summary>The region pointer table (Block 1).</summary>
    public RegionPointerTable RegionPointers { get; }

    /// <summary>The extended metadata (Block 2).</summary>
    public ExtendedMetadata Extended { get; }

    /// <summary>The integrity anchor (Block 3).</summary>
    public IntegrityAnchor Integrity { get; }

    /// <summary>Creates a superblock group from its four constituent blocks.</summary>
    public SuperblockGroup(SuperblockV2 primarySuperblock, RegionPointerTable regionPointers,
        ExtendedMetadata extended, IntegrityAnchor integrity)
    {
        PrimarySuperblock = primarySuperblock;
        RegionPointers = regionPointers ?? throw new ArgumentNullException(nameof(regionPointers));
        Extended = extended;
        Integrity = integrity;
    }

    // ── Block type tags for each block position ─────────────────────────

    private static readonly uint[] BlockTags = [BlockTypeTags.SUPB, BlockTypeTags.RMAP, BlockTypeTags.EXMD, BlockTypeTags.IANT];

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes the 4-block superblock group (without mirror).
    /// Returns exactly 4 * <paramref name="blockSize"/> bytes.
    /// Each block has a Universal Block Trailer appended at the last 16 bytes.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>A byte array of 4 * blockSize bytes.</returns>
    public byte[] SerializeToBlocks(int blockSize)
    {
        var totalSize = BlockCount * blockSize;
        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        var generationNumber = (uint)PrimarySuperblock.CheckpointSequence;

        // Block 0: Primary Superblock
        var block0 = span.Slice(0, blockSize);
        SuperblockV2.Serialize(PrimarySuperblock, block0, blockSize);
        WriteTrailer(block0, BlockTags[0], generationNumber);

        // Block 1: Region Pointer Table
        var block1 = span.Slice(blockSize, blockSize);
        RegionPointerTable.Serialize(RegionPointers, block1, blockSize);
        WriteTrailer(block1, BlockTags[1], generationNumber);

        // Block 2: Extended Metadata
        var block2 = span.Slice(2 * blockSize, blockSize);
        ExtendedMetadata.Serialize(Extended, block2, blockSize);
        WriteTrailer(block2, BlockTags[2], generationNumber);

        // Block 3: Integrity Anchor
        var block3 = span.Slice(3 * blockSize, blockSize);
        IntegrityAnchor.Serialize(Integrity, block3, blockSize);
        WriteTrailer(block3, BlockTags[3], generationNumber);

        return buffer;
    }

    /// <summary>
    /// Deserializes a 4-block superblock group (without mirror).
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least 4 * <paramref name="blockSize"/> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>The deserialized superblock group.</returns>
    public static SuperblockGroup DeserializeFromBlocks(ReadOnlySpan<byte> buffer, int blockSize)
    {
        var totalSize = BlockCount * blockSize;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));

        var sb = SuperblockV2.Deserialize(buffer.Slice(0, blockSize), blockSize);
        var rpt = RegionPointerTable.Deserialize(buffer.Slice(blockSize, blockSize), blockSize);
        var ext = ExtendedMetadata.Deserialize(buffer.Slice(2 * blockSize, blockSize), blockSize);
        var ia = IntegrityAnchor.Deserialize(buffer.Slice(3 * blockSize, blockSize), blockSize);

        return new SuperblockGroup(sb, rpt, ext, ia);
    }

    /// <summary>
    /// Serializes the superblock group with its mirror copy.
    /// Returns exactly 8 * <paramref name="blockSize"/> bytes:
    /// primary group at blocks 0-3, identical mirror at blocks 4-7.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>A byte array of 8 * blockSize bytes.</returns>
    public byte[] SerializeWithMirror(int blockSize)
    {
        var primary = SerializeToBlocks(blockSize);
        var totalSize = primary.Length * 2;
        var buffer = new byte[totalSize];

        // Write primary group (blocks 0-3)
        Array.Copy(primary, 0, buffer, 0, primary.Length);

        // Write identical mirror (blocks 4-7)
        Array.Copy(primary, 0, buffer, primary.Length, primary.Length);

        return buffer;
    }

    /// <summary>
    /// Deserializes a superblock group with mirror fallback.
    /// Reads the primary group first; if any block trailer validation fails,
    /// falls back to the mirror copy at blocks 4-7.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least 8 * <paramref name="blockSize"/> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>The deserialized superblock group.</returns>
    /// <exception cref="InvalidDataException">Both primary and mirror groups have corrupted trailers.</exception>
    public static SuperblockGroup DeserializeWithMirror(ReadOnlySpan<byte> buffer, int blockSize)
    {
        var groupSize = BlockCount * blockSize;
        var totalSize = groupSize * 2;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));

        // Try primary group first
        var primaryData = buffer.Slice(0, groupSize);
        if (ValidateTrailers(primaryData, blockSize))
        {
            return DeserializeFromBlocks(primaryData, blockSize);
        }

        // Primary corrupted, try mirror
        var mirrorData = buffer.Slice(groupSize, groupSize);
        if (ValidateTrailers(mirrorData, blockSize))
        {
            return DeserializeFromBlocks(mirrorData, blockSize);
        }

        throw new InvalidDataException(
            "Both primary and mirror superblock groups have corrupted block trailers. " +
            "The VDE file may be damaged beyond recovery.");
    }

    /// <summary>
    /// Validates the Universal Block Trailers on all 4 blocks in a superblock group.
    /// Each trailer's XxHash64 is verified against the block content.
    /// </summary>
    /// <param name="blockData">Buffer containing 4 blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>True if all 4 block trailers are valid.</returns>
    public static bool ValidateTrailers(ReadOnlySpan<byte> blockData, int blockSize)
    {
        var groupSize = BlockCount * blockSize;
        if (blockData.Length < groupSize)
            return false;

        for (int i = 0; i < BlockCount; i++)
        {
            var block = blockData.Slice(i * blockSize, blockSize);
            if (!ValidateSingleTrailer(block, blockSize))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Creates a complete default superblock group for a new volume.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="totalBlocks">Total number of blocks in the volume.</param>
    /// <param name="volumeUuid">Unique volume identifier (UUID v7).</param>
    /// <param name="moduleManifest">Module bitfield indicating enabled modules.</param>
    /// <returns>A new SuperblockGroup with default values.</returns>
    public static SuperblockGroup CreateDefault(int blockSize, long totalBlocks, Guid volumeUuid, uint moduleManifest)
    {
        var sb = SuperblockV2.CreateDefault(blockSize, totalBlocks, volumeUuid);
        // Update the module manifest via re-creating with the correct value
        sb = new SuperblockV2(
            sb.Magic, sb.VersionInfo, moduleManifest, sb.ModuleConfig, sb.ModuleConfigExt,
            sb.BlockSize, sb.TotalBlocks, sb.FreeBlocks, sb.ExpectedFileSize, sb.TotalAllocatedBlocks,
            sb.VolumeUuid, sb.ClusterNodeId,
            sb.DefaultCompressionAlgo, sb.DefaultEncryptionAlgo, sb.DefaultChecksumAlgo,
            sb.InodeSize, sb.PolicyVersion, sb.ReplicationEpoch,
            sb.WormHighWaterMark, sb.EncryptionKeyFingerprint, sb.SovereigntyZoneId,
            sb.VolumeLabel, sb.CreatedTimestampUtc, sb.ModifiedTimestampUtc, sb.LastScrubTimestamp,
            sb.CheckpointSequence, sb.ErrorMapBlockCount,
            sb.LastWriterSessionId, sb.LastWriterTimestamp, sb.LastWriterNodeId,
            sb.PhysicalAllocatedBlocks, sb.HeaderIntegritySeal);

        var rpt = new RegionPointerTable();
        var ext = ExtendedMetadata.CreateDefault();
        var ia = IntegrityAnchor.CreateDefault();

        return new SuperblockGroup(sb, rpt, ext, ia);
    }

    // ── Universal Block Trailer ─────────────────────────────────────────

    /// <summary>
    /// Writes the Universal Block Trailer at the last 16 bytes of a block.
    /// Layout: [BlockTypeTag:4 LE][GenerationNumber:4 LE][XxHash64:8 LE]
    /// The XxHash64 is computed over bytes [0..blockSize-16).
    /// </summary>
    private static void WriteTrailer(Span<byte> block, uint blockTypeTag, uint generationNumber)
    {
        int blockSize = block.Length;
        int trailerOffset = blockSize - FormatConstants.UniversalBlockTrailerSize;

        // Compute XxHash64 over all bytes except the trailer
        var contentSlice = block.Slice(0, trailerOffset);
        var hash = XxHash64.HashToUInt64(contentSlice);

        // Write trailer
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(trailerOffset), blockTypeTag);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(trailerOffset + 4), generationNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(trailerOffset + 8), hash);
    }

    /// <summary>
    /// Validates a single block's Universal Block Trailer.
    /// </summary>
    private static bool ValidateSingleTrailer(ReadOnlySpan<byte> block, int blockSize)
    {
        if (block.Length < blockSize) return false;

        int trailerOffset = blockSize - FormatConstants.UniversalBlockTrailerSize;

        // Read stored hash
        var storedHash = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(trailerOffset + 8));

        // Compute expected hash over content bytes
        var contentSlice = block.Slice(0, trailerOffset);
        var computedHash = XxHash64.HashToUInt64(contentSlice);

        return storedHash == computedHash;
    }
}
