using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Block 3 of the superblock group: Integrity Anchor.
/// Contains Merkle root hash, policy vault hash, inode table hash, tag index hash,
/// hash chain state, blockchain anchor, SBOM digest, format fingerprint, and
/// metadata chain hash with generation tracking.
/// All hash fields are fixed-size byte arrays (BLAKE3 = 32 bytes, extended = 64 bytes).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 integrity anchor (VDE2-02)")]
public readonly struct IntegrityAnchor : IEquatable<IntegrityAnchor>
{
    // ── Size constants ──────────────────────────────────────────────────

    /// <summary>Size of a BLAKE3 hash (32 bytes).</summary>
    public const int HashSize = 32;

    /// <summary>Size of an extended hash (64 bytes / 512-bit).</summary>
    public const int ExtendedHashSize = 64;

    /// <summary>Total size of all explicit fields before reserved area.</summary>
    public const int FieldsSize =
        HashSize          // MerkleRootHash
        + HashSize        // PolicyVaultHash
        + HashSize        // InodeTableHash
        + HashSize        // TagIndexHash
        + 8               // HashChainCounter
        + ExtendedHashSize // HashChainHead
        + ExtendedHashSize // BlockchainAnchorTxId
        + HashSize        // SbomDigest
        + HashSize        // FormatFingerprint
        + HashSize        // MetadataChainHash
        + 8               // ChainGeneration
        + 8;              // ChainTimestamp

    // ── Fields ──────────────────────────────────────────────────────────

    /// <summary>BLAKE3 Merkle root hash of the entire block tree (32 bytes).</summary>
    public byte[] MerkleRootHash { get; }

    /// <summary>BLAKE3 hash of the policy vault contents (32 bytes).</summary>
    public byte[] PolicyVaultHash { get; }

    /// <summary>BLAKE3 hash of the inode table (32 bytes).</summary>
    public byte[] InodeTableHash { get; }

    /// <summary>BLAKE3 hash of the tag index (32 bytes).</summary>
    public byte[] TagIndexHash { get; }

    /// <summary>Monotonically increasing hash chain counter.</summary>
    public long HashChainCounter { get; }

    /// <summary>Current head of the 512-bit hash chain (64 bytes).</summary>
    public byte[] HashChainHead { get; }

    /// <summary>512-bit blockchain anchor transaction ID (64 bytes).</summary>
    public byte[] BlockchainAnchorTxId { get; }

    /// <summary>BLAKE3 hash of the SBOM (Software Bill of Materials) (32 bytes).</summary>
    public byte[] SbomDigest { get; }

    /// <summary>BLAKE3 fingerprint of the format specification document (32 bytes).</summary>
    public byte[] FormatFingerprint { get; }

    /// <summary>Rolling metadata chain hash (32 bytes).</summary>
    public byte[] MetadataChainHash { get; }

    /// <summary>Current generation number of the metadata chain.</summary>
    public long ChainGeneration { get; }

    /// <summary>Timestamp of the last chain update (UTC nanoseconds).</summary>
    public long ChainTimestamp { get; }

    // ── Constructor ─────────────────────────────────────────────────────

    /// <summary>Creates a new integrity anchor with all fields specified.</summary>
    public IntegrityAnchor(
        byte[] merkleRootHash,
        byte[] policyVaultHash,
        byte[] inodeTableHash,
        byte[] tagIndexHash,
        long hashChainCounter,
        byte[] hashChainHead,
        byte[] blockchainAnchorTxId,
        byte[] sbomDigest,
        byte[] formatFingerprint,
        byte[] metadataChainHash,
        long chainGeneration,
        long chainTimestamp)
    {
        MerkleRootHash = merkleRootHash ?? new byte[HashSize];
        PolicyVaultHash = policyVaultHash ?? new byte[HashSize];
        InodeTableHash = inodeTableHash ?? new byte[HashSize];
        TagIndexHash = tagIndexHash ?? new byte[HashSize];
        HashChainCounter = hashChainCounter;
        HashChainHead = hashChainHead ?? new byte[ExtendedHashSize];
        BlockchainAnchorTxId = blockchainAnchorTxId ?? new byte[ExtendedHashSize];
        SbomDigest = sbomDigest ?? new byte[HashSize];
        FormatFingerprint = formatFingerprint ?? new byte[HashSize];
        MetadataChainHash = metadataChainHash ?? new byte[HashSize];
        ChainGeneration = chainGeneration;
        ChainTimestamp = chainTimestamp;
    }

    /// <summary>Creates a default (zeroed) integrity anchor.</summary>
    public static IntegrityAnchor CreateDefault() => new(
        new byte[HashSize], new byte[HashSize], new byte[HashSize], new byte[HashSize],
        0,
        new byte[ExtendedHashSize], new byte[ExtendedHashSize],
        new byte[HashSize], new byte[HashSize], new byte[HashSize],
        0, 0);

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes this integrity anchor into a block-sized buffer.
    /// Remaining bytes after all fields are zero-filled.
    /// </summary>
    public static void Serialize(in IntegrityAnchor ia, Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        buffer.Slice(0, blockSize).Clear();

        int offset = 0;

        CopyHash(ia.MerkleRootHash, buffer.Slice(offset), HashSize);
        offset += HashSize;

        CopyHash(ia.PolicyVaultHash, buffer.Slice(offset), HashSize);
        offset += HashSize;

        CopyHash(ia.InodeTableHash, buffer.Slice(offset), HashSize);
        offset += HashSize;

        CopyHash(ia.TagIndexHash, buffer.Slice(offset), HashSize);
        offset += HashSize;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), ia.HashChainCounter);
        offset += 8;

        CopyHash(ia.HashChainHead, buffer.Slice(offset), ExtendedHashSize);
        offset += ExtendedHashSize;

        CopyHash(ia.BlockchainAnchorTxId, buffer.Slice(offset), ExtendedHashSize);
        offset += ExtendedHashSize;

        CopyHash(ia.SbomDigest, buffer.Slice(offset), HashSize);
        offset += HashSize;

        CopyHash(ia.FormatFingerprint, buffer.Slice(offset), HashSize);
        offset += HashSize;

        CopyHash(ia.MetadataChainHash, buffer.Slice(offset), HashSize);
        offset += HashSize;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), ia.ChainGeneration);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), ia.ChainTimestamp);
        // Remaining bytes already zeroed
    }

    /// <summary>
    /// Deserializes an integrity anchor from a block-sized buffer.
    /// </summary>
    public static IntegrityAnchor Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        int offset = 0;

        var merkleRoot = ReadHash(buffer, ref offset, HashSize);
        var policyVault = ReadHash(buffer, ref offset, HashSize);
        var inodeTable = ReadHash(buffer, ref offset, HashSize);
        var tagIndex = ReadHash(buffer, ref offset, HashSize);

        var hashChainCounter = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var hashChainHead = ReadHash(buffer, ref offset, ExtendedHashSize);
        var blockchainAnchor = ReadHash(buffer, ref offset, ExtendedHashSize);
        var sbomDigest = ReadHash(buffer, ref offset, HashSize);
        var formatFingerprint = ReadHash(buffer, ref offset, HashSize);
        var metadataChainHash = ReadHash(buffer, ref offset, HashSize);

        var chainGeneration = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var chainTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        return new IntegrityAnchor(
            merkleRoot, policyVault, inodeTable, tagIndex,
            hashChainCounter, hashChainHead, blockchainAnchor,
            sbomDigest, formatFingerprint, metadataChainHash,
            chainGeneration, chainTimestamp);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void CopyHash(byte[]? source, Span<byte> dest, int size)
    {
        if (source is not null)
        {
            var len = Math.Min(source.Length, size);
            source.AsSpan(0, len).CopyTo(dest);
        }
    }

    private static byte[] ReadHash(ReadOnlySpan<byte> buffer, ref int offset, int size)
    {
        var result = new byte[size];
        buffer.Slice(offset, size).CopyTo(result);
        offset += size;
        return result;
    }

    // ── Equality ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(IntegrityAnchor other) =>
        HashChainCounter == other.HashChainCounter
        && ChainGeneration == other.ChainGeneration
        && ChainTimestamp == other.ChainTimestamp
        && MerkleRootHash.AsSpan().SequenceEqual(other.MerkleRootHash)
        && PolicyVaultHash.AsSpan().SequenceEqual(other.PolicyVaultHash)
        && InodeTableHash.AsSpan().SequenceEqual(other.InodeTableHash)
        && TagIndexHash.AsSpan().SequenceEqual(other.TagIndexHash)
        && HashChainHead.AsSpan().SequenceEqual(other.HashChainHead)
        && MetadataChainHash.AsSpan().SequenceEqual(other.MetadataChainHash);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IntegrityAnchor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(HashChainCounter, ChainGeneration, ChainTimestamp);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(IntegrityAnchor left, IntegrityAnchor right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(IntegrityAnchor left, IntegrityAnchor right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"IntegrityAnchor(ChainCounter={HashChainCounter}, Gen={ChainGeneration})";
}
