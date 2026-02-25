using System.Buffers.Binary;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Block 0 of the DWVD v2.0 superblock group: primary superblock.
/// Contains format identity, volume metadata, module manifest, algorithm defaults,
/// timestamps, last-writer identity, and a trailing HMAC-BLAKE3 integrity seal.
/// Serializes to exactly one block (minus <see cref="FormatConstants.UniversalBlockTrailerSize"/>
/// bytes reserved for the universal block trailer).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 primary superblock (VDE2-02)")]
public readonly struct SuperblockV2 : IEquatable<SuperblockV2>
{
    // ── Layout constants ────────────────────────────────────────────────

    /// <summary>Size in bytes of the volume label UTF-8 buffer.</summary>
    public const int VolumeLabelSize = 64;

    /// <summary>Size in bytes of the header integrity seal (HMAC-BLAKE3).</summary>
    public const int IntegritySealSize = 32;

    // ── Fields (matching spec "Block 0 -- Primary Superblock") ──────────

    /// <summary>16-byte magic signature at offset 0x00.</summary>
    public MagicSignature Magic { get; }

    /// <summary>16-byte version info (feature flags + min versions) at offset 0x10.</summary>
    public FormatVersionInfo VersionInfo { get; }

    /// <summary>32-bit module bitfield at offset 0x20. Each bit enables one module slot.</summary>
    public uint ModuleManifest { get; }

    /// <summary>64-bit nibble-encoded per-module config at offset 0x24.</summary>
    public ulong ModuleConfig { get; }

    /// <summary>64-bit extended per-module config at offset 0x2C.</summary>
    public ulong ModuleConfigExt { get; }

    /// <summary>Block size in bytes at offset 0x34.</summary>
    public int BlockSize { get; }

    /// <summary>Total number of blocks in the volume.</summary>
    public long TotalBlocks { get; }

    /// <summary>Number of free (unallocated) blocks.</summary>
    public long FreeBlocks { get; }

    /// <summary>Expected file size sentinel for truncation detection at offset 0x48.</summary>
    public long ExpectedFileSize { get; }

    /// <summary>Total allocated blocks (thin provisioning) at offset 0x50.</summary>
    public long TotalAllocatedBlocks { get; }

    /// <summary>128-bit UUID v7 identifying this volume.</summary>
    public Guid VolumeUuid { get; }

    /// <summary>128-bit node ID for cluster membership.</summary>
    public Guid ClusterNodeId { get; }

    /// <summary>Default compression algorithm identifier.</summary>
    public byte DefaultCompressionAlgo { get; }

    /// <summary>Default encryption algorithm identifier.</summary>
    public byte DefaultEncryptionAlgo { get; }

    /// <summary>Default checksum algorithm identifier.</summary>
    public byte DefaultChecksumAlgo { get; }

    /// <summary>Inode size in bytes (calculated from enabled modules).</summary>
    public ushort InodeSize { get; }

    /// <summary>Policy engine version for compatibility gating.</summary>
    public uint PolicyVersion { get; }

    /// <summary>Replication epoch counter.</summary>
    public uint ReplicationEpoch { get; }

    /// <summary>WORM high water mark block number.</summary>
    public long WormHighWaterMark { get; }

    /// <summary>Fingerprint of the active encryption key.</summary>
    public ulong EncryptionKeyFingerprint { get; }

    /// <summary>Sovereignty zone identifier.</summary>
    public ushort SovereigntyZoneId { get; }

    /// <summary>Volume label as a 64-byte UTF-8 buffer (null-padded).</summary>
    public byte[] VolumeLabel { get; }

    /// <summary>Volume creation timestamp (UTC ticks).</summary>
    public long CreatedTimestampUtc { get; }

    /// <summary>Last modification timestamp (UTC ticks).</summary>
    public long ModifiedTimestampUtc { get; }

    /// <summary>Last integrity scrub timestamp (UTC ticks).</summary>
    public long LastScrubTimestamp { get; }

    /// <summary>Monotonically increasing checkpoint sequence number.</summary>
    public long CheckpointSequence { get; }

    /// <summary>Number of blocks in the error map.</summary>
    public long ErrorMapBlockCount { get; }

    /// <summary>Session ID of the last writer.</summary>
    public Guid LastWriterSessionId { get; }

    /// <summary>Timestamp of the last write operation (UTC ticks).</summary>
    public long LastWriterTimestamp { get; }

    /// <summary>Node ID of the last writer.</summary>
    public Guid LastWriterNodeId { get; }

    /// <summary>Physically allocated blocks (thin provisioning accounting).</summary>
    public long PhysicalAllocatedBlocks { get; }

    /// <summary>HMAC-BLAKE3 seal of the header content (last 32 bytes before trailer).</summary>
    public byte[] HeaderIntegritySeal { get; }

    // ── Constructor ─────────────────────────────────────────────────────

    /// <summary>Creates a new SuperblockV2 with all fields specified.</summary>
    public SuperblockV2(
        MagicSignature magic,
        FormatVersionInfo versionInfo,
        uint moduleManifest,
        ulong moduleConfig,
        ulong moduleConfigExt,
        int blockSize,
        long totalBlocks,
        long freeBlocks,
        long expectedFileSize,
        long totalAllocatedBlocks,
        Guid volumeUuid,
        Guid clusterNodeId,
        byte defaultCompressionAlgo,
        byte defaultEncryptionAlgo,
        byte defaultChecksumAlgo,
        ushort inodeSize,
        uint policyVersion,
        uint replicationEpoch,
        long wormHighWaterMark,
        ulong encryptionKeyFingerprint,
        ushort sovereigntyZoneId,
        byte[] volumeLabel,
        long createdTimestampUtc,
        long modifiedTimestampUtc,
        long lastScrubTimestamp,
        long checkpointSequence,
        long errorMapBlockCount,
        Guid lastWriterSessionId,
        long lastWriterTimestamp,
        Guid lastWriterNodeId,
        long physicalAllocatedBlocks,
        byte[] headerIntegritySeal)
    {
        Magic = magic;
        VersionInfo = versionInfo;
        ModuleManifest = moduleManifest;
        ModuleConfig = moduleConfig;
        ModuleConfigExt = moduleConfigExt;
        BlockSize = blockSize;
        TotalBlocks = totalBlocks;
        FreeBlocks = freeBlocks;
        ExpectedFileSize = expectedFileSize;
        TotalAllocatedBlocks = totalAllocatedBlocks;
        VolumeUuid = volumeUuid;
        ClusterNodeId = clusterNodeId;
        DefaultCompressionAlgo = defaultCompressionAlgo;
        DefaultEncryptionAlgo = defaultEncryptionAlgo;
        DefaultChecksumAlgo = defaultChecksumAlgo;
        InodeSize = inodeSize;
        PolicyVersion = policyVersion;
        ReplicationEpoch = replicationEpoch;
        WormHighWaterMark = wormHighWaterMark;
        EncryptionKeyFingerprint = encryptionKeyFingerprint;
        SovereigntyZoneId = sovereigntyZoneId;
        VolumeLabel = volumeLabel ?? new byte[VolumeLabelSize];
        CreatedTimestampUtc = createdTimestampUtc;
        ModifiedTimestampUtc = modifiedTimestampUtc;
        LastScrubTimestamp = lastScrubTimestamp;
        CheckpointSequence = checkpointSequence;
        ErrorMapBlockCount = errorMapBlockCount;
        LastWriterSessionId = lastWriterSessionId;
        LastWriterTimestamp = lastWriterTimestamp;
        LastWriterNodeId = lastWriterNodeId;
        PhysicalAllocatedBlocks = physicalAllocatedBlocks;
        HeaderIntegritySeal = headerIntegritySeal ?? new byte[IntegritySealSize];
    }

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes this superblock into a block-sized buffer.
    /// The last <see cref="FormatConstants.UniversalBlockTrailerSize"/> bytes are
    /// reserved for the universal block trailer (not written here).
    /// </summary>
    /// <param name="sb">The superblock to serialize.</param>
    /// <param name="buffer">Destination buffer, must be at least <paramref name="blockSize"/> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public static void Serialize(in SuperblockV2 sb, Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        // Clear the entire block first
        buffer.Slice(0, blockSize).Clear();

        int offset = 0;

        // 0x00: Magic signature (16 bytes)
        MagicSignature.Serialize(sb.Magic, buffer.Slice(offset));
        offset += FormatConstants.MagicSignatureSize;

        // 0x10: Version info (16 bytes)
        FormatVersionInfo.Serialize(sb.VersionInfo, buffer.Slice(offset));
        offset += FormatVersionInfo.SerializedSize;

        // 0x20: Module manifest (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), sb.ModuleManifest);
        offset += 4;

        // 0x24: Module config (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), sb.ModuleConfig);
        offset += 8;

        // 0x2C: Module config extended (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), sb.ModuleConfigExt);
        offset += 8;

        // 0x34: Block size (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), sb.BlockSize);
        offset += 4;

        // 0x38: Total blocks (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.TotalBlocks);
        offset += 8;

        // 0x40: Free blocks (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.FreeBlocks);
        offset += 8;

        // 0x48: Expected file size (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.ExpectedFileSize);
        offset += 8;

        // 0x50: Total allocated blocks (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.TotalAllocatedBlocks);
        offset += 8;

        // 0x58: Volume UUID (16 bytes)
        sb.VolumeUuid.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // 0x68: Cluster node ID (16 bytes)
        sb.ClusterNodeId.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // 0x78: Default compression algo (1 byte)
        buffer[offset++] = sb.DefaultCompressionAlgo;

        // 0x79: Default encryption algo (1 byte)
        buffer[offset++] = sb.DefaultEncryptionAlgo;

        // 0x7A: Default checksum algo (1 byte)
        buffer[offset++] = sb.DefaultChecksumAlgo;

        // 0x7B: padding (1 byte)
        offset += 1;

        // 0x7C: Inode size (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), sb.InodeSize);
        offset += 2;

        // 0x7E: Sovereignty zone ID (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), sb.SovereigntyZoneId);
        offset += 2;

        // 0x80: Policy version (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), sb.PolicyVersion);
        offset += 4;

        // 0x84: Replication epoch (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), sb.ReplicationEpoch);
        offset += 4;

        // 0x88: WORM high water mark (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.WormHighWaterMark);
        offset += 8;

        // 0x90: Encryption key fingerprint (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), sb.EncryptionKeyFingerprint);
        offset += 8;

        // 0x98: Volume label (64 bytes)
        var label = sb.VolumeLabel ?? Array.Empty<byte>();
        var labelLen = Math.Min(label.Length, VolumeLabelSize);
        label.AsSpan(0, labelLen).CopyTo(buffer.Slice(offset));
        offset += VolumeLabelSize;

        // 0xD8: Created timestamp (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.CreatedTimestampUtc);
        offset += 8;

        // 0xE0: Modified timestamp (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.ModifiedTimestampUtc);
        offset += 8;

        // 0xE8: Last scrub timestamp (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.LastScrubTimestamp);
        offset += 8;

        // 0xF0: Checkpoint sequence (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.CheckpointSequence);
        offset += 8;

        // 0xF8: Error map block count (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.ErrorMapBlockCount);
        offset += 8;

        // 0x100: Last writer session ID (16 bytes)
        sb.LastWriterSessionId.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // 0x110: Last writer timestamp (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.LastWriterTimestamp);
        offset += 8;

        // 0x118: Last writer node ID (16 bytes)
        sb.LastWriterNodeId.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // 0x128: Physical allocated blocks (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), sb.PhysicalAllocatedBlocks);
        // offset += 8; // not needed: remaining bytes already zeroed

        // Header integrity seal: last 32 bytes before the universal block trailer
        int sealOffset = blockSize - FormatConstants.UniversalBlockTrailerSize - IntegritySealSize;
        var seal = sb.HeaderIntegritySeal ?? Array.Empty<byte>();
        var sealLen = Math.Min(seal.Length, IntegritySealSize);
        seal.AsSpan(0, sealLen).CopyTo(buffer.Slice(sealOffset));
    }

    /// <summary>
    /// Deserializes a primary superblock from a block-sized buffer.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least <paramref name="blockSize"/> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>The deserialized superblock.</returns>
    public static SuperblockV2 Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        int offset = 0;

        var magic = MagicSignature.Deserialize(buffer.Slice(offset));
        offset += FormatConstants.MagicSignatureSize;

        var versionInfo = FormatVersionInfo.Deserialize(buffer.Slice(offset));
        offset += FormatVersionInfo.SerializedSize;

        var moduleManifest = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var moduleConfig = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var moduleConfigExt = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var bs = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var totalBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var freeBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var expectedFileSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var totalAllocatedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var volumeUuid = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var clusterNodeId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var defaultCompressionAlgo = buffer[offset++];
        var defaultEncryptionAlgo = buffer[offset++];
        var defaultChecksumAlgo = buffer[offset++];
        offset += 1; // padding

        var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        var sovereigntyZoneId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        var policyVersion = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var replicationEpoch = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var wormHighWaterMark = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var encryptionKeyFingerprint = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var volumeLabel = new byte[VolumeLabelSize];
        buffer.Slice(offset, VolumeLabelSize).CopyTo(volumeLabel);
        offset += VolumeLabelSize;

        var createdTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var modifiedTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var lastScrubTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var checkpointSequence = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var errorMapBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var lastWriterSessionId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var lastWriterTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var lastWriterNodeId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var physicalAllocatedBlocks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        // Header integrity seal: last 32 bytes before trailer
        int sealOffset = blockSize - FormatConstants.UniversalBlockTrailerSize - IntegritySealSize;
        var headerIntegritySeal = new byte[IntegritySealSize];
        buffer.Slice(sealOffset, IntegritySealSize).CopyTo(headerIntegritySeal);

        return new SuperblockV2(
            magic, versionInfo, moduleManifest, moduleConfig, moduleConfigExt,
            bs, totalBlocks, freeBlocks, expectedFileSize, totalAllocatedBlocks,
            volumeUuid, clusterNodeId,
            defaultCompressionAlgo, defaultEncryptionAlgo, defaultChecksumAlgo,
            inodeSize, policyVersion, replicationEpoch,
            wormHighWaterMark, encryptionKeyFingerprint, sovereigntyZoneId,
            volumeLabel, createdTimestamp, modifiedTimestamp, lastScrubTimestamp,
            checkpointSequence, errorMapBlockCount,
            lastWriterSessionId, lastWriterTimestamp, lastWriterNodeId,
            physicalAllocatedBlocks, headerIntegritySeal);
    }

    /// <summary>
    /// Creates a superblock with sensible defaults for a new volume.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="totalBlocks">Total number of blocks in the volume.</param>
    /// <param name="volumeUuid">Unique volume identifier (UUID v7).</param>
    /// <returns>A new SuperblockV2 with default values.</returns>
    public static SuperblockV2 CreateDefault(int blockSize, long totalBlocks, Guid volumeUuid)
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        return new SuperblockV2(
            magic: MagicSignature.CreateDefault(),
            versionInfo: new FormatVersionInfo(
                minReaderVersion: FormatConstants.FormatMajorVersion,
                minWriterVersion: FormatConstants.FormatMajorVersion,
                incompatibleFeatures: IncompatibleFeatureFlags.None,
                readOnlyCompatibleFeatures: ReadOnlyCompatibleFeatureFlags.None,
                compatibleFeatures: CompatibleFeatureFlags.None),
            moduleManifest: 0,
            moduleConfig: 0,
            moduleConfigExt: 0,
            blockSize: blockSize,
            totalBlocks: totalBlocks,
            freeBlocks: totalBlocks - (FormatConstants.SuperblockGroupBlocks * 2) - FormatConstants.RegionDirectoryBlocks,
            expectedFileSize: totalBlocks * blockSize,
            totalAllocatedBlocks: (FormatConstants.SuperblockGroupBlocks * 2) + FormatConstants.RegionDirectoryBlocks,
            volumeUuid: volumeUuid,
            clusterNodeId: Guid.Empty,
            defaultCompressionAlgo: 0,
            defaultEncryptionAlgo: 0,
            defaultChecksumAlgo: 1, // XxHash64
            inodeSize: (ushort)FormatConstants.InodeCoreSize,
            policyVersion: 1,
            replicationEpoch: 0,
            wormHighWaterMark: 0,
            encryptionKeyFingerprint: 0,
            sovereigntyZoneId: 0,
            volumeLabel: new byte[VolumeLabelSize],
            createdTimestampUtc: now,
            modifiedTimestampUtc: now,
            lastScrubTimestamp: 0,
            checkpointSequence: 1,
            errorMapBlockCount: 0,
            lastWriterSessionId: Guid.Empty,
            lastWriterTimestamp: now,
            lastWriterNodeId: Guid.Empty,
            physicalAllocatedBlocks: 0,
            headerIntegritySeal: new byte[IntegritySealSize]);
    }

    /// <summary>
    /// Gets the volume label as a trimmed UTF-8 string.
    /// </summary>
    public string GetVolumeLabelString()
    {
        if (VolumeLabel is null) return string.Empty;
        var end = Array.IndexOf(VolumeLabel, (byte)0);
        if (end < 0) end = VolumeLabel.Length;
        return Encoding.UTF8.GetString(VolumeLabel, 0, end);
    }

    /// <summary>
    /// Creates a volume label byte array from a UTF-8 string, padded to 64 bytes.
    /// </summary>
    /// <param name="label">The label string (max 64 UTF-8 bytes).</param>
    /// <returns>A 64-byte null-padded array.</returns>
    public static byte[] CreateVolumeLabel(string label)
    {
        var result = new byte[VolumeLabelSize];
        if (!string.IsNullOrEmpty(label))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            var len = Math.Min(bytes.Length, VolumeLabelSize);
            Array.Copy(bytes, result, len);
        }
        return result;
    }

    // ── Equality ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(SuperblockV2 other) =>
        Magic == other.Magic
        && VersionInfo == other.VersionInfo
        && ModuleManifest == other.ModuleManifest
        && ModuleConfig == other.ModuleConfig
        && ModuleConfigExt == other.ModuleConfigExt
        && BlockSize == other.BlockSize
        && TotalBlocks == other.TotalBlocks
        && FreeBlocks == other.FreeBlocks
        && ExpectedFileSize == other.ExpectedFileSize
        && TotalAllocatedBlocks == other.TotalAllocatedBlocks
        && VolumeUuid == other.VolumeUuid
        && ClusterNodeId == other.ClusterNodeId
        && DefaultCompressionAlgo == other.DefaultCompressionAlgo
        && DefaultEncryptionAlgo == other.DefaultEncryptionAlgo
        && DefaultChecksumAlgo == other.DefaultChecksumAlgo
        && InodeSize == other.InodeSize
        && PolicyVersion == other.PolicyVersion
        && ReplicationEpoch == other.ReplicationEpoch
        && WormHighWaterMark == other.WormHighWaterMark
        && EncryptionKeyFingerprint == other.EncryptionKeyFingerprint
        && SovereigntyZoneId == other.SovereigntyZoneId
        && CheckpointSequence == other.CheckpointSequence
        && PhysicalAllocatedBlocks == other.PhysicalAllocatedBlocks;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SuperblockV2 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(VolumeUuid, TotalBlocks, CheckpointSequence);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SuperblockV2 left, SuperblockV2 right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SuperblockV2 left, SuperblockV2 right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"SuperblockV2(Volume={VolumeUuid}, Blocks={TotalBlocks}, Free={FreeBlocks}, Seq={CheckpointSequence})";
}
