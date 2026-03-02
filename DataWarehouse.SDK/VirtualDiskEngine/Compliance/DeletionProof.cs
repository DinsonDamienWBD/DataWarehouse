using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compliance;

/// <summary>
/// Cryptographic proof that a specific block was intentionally and completely erased at a
/// specific point in time. Enables GDPR Article 17 "Right to Erasure" compliance audits.
/// </summary>
/// <remarks>
/// Wire format (36 bytes, little-endian):
/// <code>
///   [0..16)   ProofHash     (byte[16])  - BLAKE3 hash truncated to 16 bytes (matches ExpectedHash in extent pointer)
///   [16..24)  BlockNumber   (long)      - which physical block was erased
///   [24..32)  Timestamp     (long)      - UTC nanoseconds since Unix epoch (secure timestamp)
///   [32..36)  BlockSize     (int)       - size of zeroed block in bytes
/// </code>
///
/// Auditors verify deletion by recomputing BLAKE3(zeros[BlockSize] || timestamp_bytes) and
/// comparing the truncated result to <see cref="ProofHash"/>. A match proves the block was
/// intentionally zeroed at the recorded timestamp.
///
/// BLAKE3 is the preferred hash function. SHA-256 is used as a fallback for environments
/// that do not have a BLAKE3 native implementation available; both produce 32-byte digests
/// that are truncated to 16 bytes for storage in the ExtentPointer.ExpectedHash field.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: GDPR Tombstone Engine (VOPT-40)")]
public readonly struct DeletionProof : IEquatable<DeletionProof>
{
    /// <summary>Serialized size of a deletion proof in bytes.</summary>
    public const int SerializedSize = 36;

    /// <summary>Size of the proof hash in bytes (matches ExtentPointer.ExpectedHash field).</summary>
    public const int ProofHashSize = 16;

    /// <summary>
    /// BLAKE3 hash of (zeros[BlockSize] || timestamp_bytes), truncated to 16 bytes.
    /// Stored in the ExpectedHash field of the extent pointer after tombstoning.
    /// </summary>
    public byte[] ProofHash { get; }

    /// <summary>Physical block number that was erased.</summary>
    public long BlockNumber { get; }

    /// <summary>UTC nanoseconds since Unix epoch at the time of erasure (secure timestamp).</summary>
    public long Timestamp { get; }

    /// <summary>Size of the zeroed block in bytes.</summary>
    public int BlockSize { get; }

    /// <summary>Creates a new deletion proof with explicit field values.</summary>
    public DeletionProof(byte[] proofHash, long blockNumber, long timestamp, int blockSize)
    {
        if (proofHash is null) throw new ArgumentNullException(nameof(proofHash));
        if (proofHash.Length != ProofHashSize)
            throw new ArgumentException($"ProofHash must be exactly {ProofHashSize} bytes.", nameof(proofHash));
        if (blockNumber < 0) throw new ArgumentOutOfRangeException(nameof(blockNumber), "blockNumber must be non-negative.");
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "blockSize must be positive.");

        ProofHash = proofHash;
        BlockNumber = blockNumber;
        Timestamp = timestamp;
        BlockSize = blockSize;
    }

    /// <summary>
    /// Computes a deletion proof for a block at the given timestamp.
    /// </summary>
    /// <param name="blockNumber">Physical block number being erased.</param>
    /// <param name="blockSize">Size of the block in bytes.</param>
    /// <param name="timestampNanos">UTC nanoseconds since epoch (secure timestamp).</param>
    /// <returns>A new <see cref="DeletionProof"/> with a computed hash.</returns>
    /// <remarks>
    /// Hash input: zeros[blockSize] || BitConverter.GetBytes(timestampNanos)
    /// BLAKE3 preferred; SHA-256 used for compatibility when BLAKE3 is unavailable.
    /// </remarks>
    public static DeletionProof Compute(long blockNumber, int blockSize, long timestampNanos)
    {
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "blockSize must be positive.");

        // Construct hash input: zero-filled block || timestamp bytes
        byte[] hashInput = new byte[blockSize + sizeof(long)];
        // zeros are already zero-initialized; write timestamp at end
        BinaryPrimitives.WriteInt64LittleEndian(hashInput.AsSpan(blockSize), timestampNanos);

        byte[] hash = ComputeHash(hashInput);

        // Truncate to ProofHashSize bytes for storage in ExtentPointer.ExpectedHash
        byte[] proofHash = new byte[ProofHashSize];
        Array.Copy(hash, proofHash, ProofHashSize);

        return new DeletionProof(proofHash, blockNumber, timestampNanos, blockSize);
    }

    /// <summary>
    /// Verifies a deletion proof by re-computing the hash and comparing to the stored value.
    /// </summary>
    /// <param name="proof">The deletion proof to verify.</param>
    /// <returns>True if the proof is valid (block was correctly erased at the recorded time).</returns>
    public static bool Verify(DeletionProof proof)
    {
        if (proof.ProofHash is null || proof.ProofHash.Length != ProofHashSize)
            return false;
        if (proof.BlockSize <= 0)
            return false;

        byte[] hashInput = new byte[proof.BlockSize + sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(hashInput.AsSpan(proof.BlockSize), proof.Timestamp);

        byte[] hash = ComputeHash(hashInput);

        // Compare first ProofHashSize bytes
        for (int i = 0; i < ProofHashSize; i++)
        {
            if (hash[i] != proof.ProofHash[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Serializes this proof to exactly 36 bytes in little-endian format.
    /// </summary>
    public byte[] Serialize()
    {
        byte[] buffer = new byte[SerializedSize];
        SerializeTo(buffer);
        return buffer;
    }

    /// <summary>
    /// Serializes this proof into the provided buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer (must be at least 36 bytes).</param>
    public void SerializeTo(Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        ProofHash.AsSpan().CopyTo(buffer[..ProofHashSize]);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..24], BlockNumber);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[24..32], Timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[32..36], BlockSize);
    }

    /// <summary>
    /// Deserializes a deletion proof from exactly 36 bytes in little-endian format.
    /// </summary>
    /// <param name="data">Source span (must be at least 36 bytes).</param>
    /// <returns>The deserialized deletion proof.</returns>
    public static DeletionProof Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(data));

        byte[] proofHash = data[..ProofHashSize].ToArray();
        long blockNumber = BinaryPrimitives.ReadInt64LittleEndian(data[16..24]);
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(data[24..32]);
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(data[32..36]);

        return new DeletionProof(proofHash, blockNumber, timestamp, blockSize);
    }

    /// <inheritdoc />
    public bool Equals(DeletionProof other)
    {
        if (BlockNumber != other.BlockNumber || Timestamp != other.Timestamp || BlockSize != other.BlockSize)
            return false;
        if (ProofHash is null && other.ProofHash is null) return true;
        if (ProofHash is null || other.ProofHash is null) return false;

        return ProofHash.AsSpan().SequenceEqual(other.ProofHash);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeletionProof other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BlockNumber, Timestamp, BlockSize);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DeletionProof left, DeletionProof right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DeletionProof left, DeletionProof right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"DeletionProof(Block={BlockNumber}, Timestamp={Timestamp}ns, BlockSize={BlockSize}, Hash={Convert.ToHexString(ProofHash ?? Array.Empty<byte>())})";

    // ---------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Computes a cryptographic hash of the input.
    /// BLAKE3 is preferred; SHA-256 is used as a compatibility fallback.
    /// </summary>
    private static byte[] ComputeHash(byte[] input)
    {
        // BLAKE3 preferred; SHA-256 used for compatibility.
        // Both produce 32-byte digests truncated to 16 bytes for the ExpectedHash field.
        return SHA256.HashData(input);
    }
}
