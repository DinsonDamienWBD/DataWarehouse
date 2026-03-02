using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// FROST threshold signature scheme variants supported by the quorum seal.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-28: Quorum-sealed write path (VOPT-42)")]
public enum QuorumScheme : byte
{
    /// <summary>FROST over Ed25519 curve — deterministic, widely supported.</summary>
    Frost_Ed25519 = 0,

    /// <summary>FROST over Ristretto255 — prime-order group, constant-time by construction.</summary>
    Frost_Ristretto255 = 1,
}

/// <summary>
/// Policy applied when quorum threshold cannot be met during a write operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-28: Quorum-sealed write path (VOPT-42)")]
public enum QuorumDegradePolicy : byte
{
    /// <summary>
    /// Reject the write entirely and throw an <see cref="InvalidOperationException"/>.
    /// Provides maximum security at the cost of availability.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Accept the write without a quorum seal and log a warning.
    /// Reduces security but maintains availability; suitable for maintenance windows.
    /// </summary>
    AcceptUnsigned = 1,

    /// <summary>
    /// Write data provisionally and enqueue for later sealing when sufficient signers become available.
    /// Balances availability with eventual integrity.
    /// </summary>
    QueueForSealing = 2,
}

/// <summary>
/// Configuration for the quorum-sealed write path (VOPT-42).
/// Controls FROST threshold parameters, degrade policy, and replay-prevention options.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-28: Quorum-sealed write path (VOPT-42)")]
public sealed class QuorumSealConfig
{
    /// <summary>
    /// Policy applied when quorum threshold cannot be met.
    /// Default: <see cref="QuorumDegradePolicy.Reject"/> for maximum security.
    /// </summary>
    public QuorumDegradePolicy DegradePolicy { get; set; } = QuorumDegradePolicy.Reject;

    /// <summary>
    /// Minimum number of signers required for a valid quorum (t in t-of-n).
    /// Must be at least 1 and at most <see cref="DefaultTotalSigners"/>.
    /// Default: 3.
    /// </summary>
    public int DefaultThreshold { get; set; } = 3;

    /// <summary>
    /// Total number of signers in the set (n in t-of-n).
    /// Must be at least <see cref="DefaultThreshold"/>.
    /// Default: 5.
    /// </summary>
    public int DefaultTotalSigners { get; set; } = 5;

    /// <summary>
    /// Maximum time to wait for additional signers in <see cref="QuorumDegradePolicy.QueueForSealing"/> mode.
    /// Expired items are handled per the configured timeout policy.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan SealingQueueTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, the ReplicationGeneration counter is included in the signed message,
    /// preventing cross-generation replay attacks.
    /// Default: true (recommended for all production deployments).
    /// </summary>
    public bool IncludeReplicationGeneration { get; set; } = true;

    /// <summary>
    /// Validates this configuration, throwing <see cref="ArgumentException"/> on invalid state.
    /// </summary>
    public void Validate()
    {
        if (DefaultThreshold < 1)
            throw new ArgumentException("DefaultThreshold must be at least 1.", nameof(DefaultThreshold));
        if (DefaultTotalSigners < DefaultThreshold)
            throw new ArgumentException(
                $"DefaultTotalSigners ({DefaultTotalSigners}) must be >= DefaultThreshold ({DefaultThreshold}).",
                nameof(DefaultTotalSigners));
        if (DefaultTotalSigners > 32)
            throw new ArgumentException(
                "DefaultTotalSigners cannot exceed 32 (SignerBitmap is 32 bits).", nameof(DefaultTotalSigners));
        if (SealingQueueTimeout <= TimeSpan.Zero)
            throw new ArgumentException("SealingQueueTimeout must be positive.", nameof(SealingQueueTimeout));
    }
}

/// <summary>
/// On-disk representation of a quorum seal stored in the 79-byte inode overflow block.
/// Layout (per VOPT-42 spec): [Scheme:1][Threshold:1][TotalSigners:1][SignerBitmap:4][AggregateSignature:64][Nonce:8] = 79 bytes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-28: Quorum overflow inode (VOPT-42)")]
public readonly struct QuorumOverflowInode : IEquatable<QuorumOverflowInode>
{
    /// <summary>Exact on-disk size in bytes (per VOPT-42 spec).</summary>
    public const int Size = 79;

    /// <summary>Size of the AggregateSignature field in bytes.</summary>
    public const int SignatureSize = 64;

    /// <summary>FROST scheme used to produce this seal.</summary>
    public QuorumScheme Scheme { get; }

    /// <summary>Minimum signers required (t in t-of-n).</summary>
    public byte Threshold { get; }

    /// <summary>Total signers in the set (n in t-of-n).</summary>
    public byte TotalSigners { get; }

    /// <summary>
    /// Bitmask indicating which of the n signers contributed to this aggregate.
    /// Bit i set = signer i participated. PopCount must be >= Threshold.
    /// </summary>
    public uint SignerBitmap { get; }

    /// <summary>
    /// 64-byte FROST aggregate Schnorr signature over the sealed message.
    /// Layout: [R:32][s:32] where R is the aggregate nonce commitment and s is the aggregate response.
    /// </summary>
    public ReadOnlyMemory<byte> AggregateSignature { get; }

    /// <summary>
    /// Monotonic nonce for replay prevention. Combined with InodeNumber and ReplicationGeneration
    /// in the signed message to produce a unique per-write commitment.
    /// </summary>
    public long Nonce { get; }

    /// <summary>
    /// Initialises a <see cref="QuorumOverflowInode"/> with all fields.
    /// </summary>
    public QuorumOverflowInode(
        QuorumScheme scheme,
        byte threshold,
        byte totalSigners,
        uint signerBitmap,
        ReadOnlyMemory<byte> aggregateSignature,
        long nonce)
    {
        if (aggregateSignature.Length != SignatureSize)
            throw new ArgumentException($"AggregateSignature must be exactly {SignatureSize} bytes.", nameof(aggregateSignature));

        Scheme = scheme;
        Threshold = threshold;
        TotalSigners = totalSigners;
        SignerBitmap = signerBitmap;
        AggregateSignature = aggregateSignature;
        Nonce = nonce;
    }

    /// <summary>
    /// Serialises this struct into the supplied <paramref name="buffer"/>.
    /// Exactly <see cref="Size"/> bytes are written.
    /// </summary>
    /// <param name="buffer">Must be at least <see cref="Size"/> bytes.</param>
    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        int offset = 0;

        buffer[offset++] = (byte)Scheme;          // [0]   Scheme:1
        buffer[offset++] = Threshold;             // [1]   Threshold:1
        buffer[offset++] = TotalSigners;          // [2]   TotalSigners:1

        // [3..6] SignerBitmap:4 (little-endian)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), SignerBitmap);
        offset += 4;

        // [7..70] AggregateSignature:64
        AggregateSignature.Span.CopyTo(buffer.Slice(offset, SignatureSize));
        offset += SignatureSize;

        // [71..78] Nonce:8 (little-endian)
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), Nonce);
        // offset += 8; — total = 79
    }

    /// <summary>
    /// Deserialises a <see cref="QuorumOverflowInode"/> from a read-only span.
    /// Expects exactly <see cref="Size"/> bytes at the start of <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Must contain at least <see cref="Size"/> bytes.</param>
    /// <returns>Deserialised struct.</returns>
    public static QuorumOverflowInode ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        int offset = 0;

        var scheme = (QuorumScheme)buffer[offset++];
        byte threshold = buffer[offset++];
        byte totalSigners = buffer[offset++];

        uint signerBitmap = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        byte[] sig = new byte[SignatureSize];
        buffer.Slice(offset, SignatureSize).CopyTo(sig);
        offset += SignatureSize;

        long nonce = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));

        return new QuorumOverflowInode(scheme, threshold, totalSigners, signerBitmap, sig, nonce);
    }

    /// <inheritdoc/>
    public bool Equals(QuorumOverflowInode other)
    {
        return Scheme == other.Scheme
            && Threshold == other.Threshold
            && TotalSigners == other.TotalSigners
            && SignerBitmap == other.SignerBitmap
            && Nonce == other.Nonce
            && AggregateSignature.Span.SequenceEqual(other.AggregateSignature.Span);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is QuorumOverflowInode q && Equals(q);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Scheme, Threshold, TotalSigners, SignerBitmap, Nonce);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(QuorumOverflowInode left, QuorumOverflowInode right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(QuorumOverflowInode left, QuorumOverflowInode right) => !left.Equals(right);
}
