using System.Buffers.Binary;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// A 176-byte namespace registration entry stored in the extended metadata block.
/// Contains a namespace prefix, UUID, authority string, and Ed25519 signature.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 namespace registration (VDE2-02)")]
public readonly struct NamespaceRegistration : IEquatable<NamespaceRegistration>
{
    /// <summary>Serialized size of a namespace registration in bytes.</summary>
    public const int SerializedSize = 176;

    /// <summary>Size of the namespace prefix buffer (UTF-8, null-padded).</summary>
    public const int PrefixSize = 32;

    /// <summary>Size of the namespace authority buffer (UTF-8, null-padded).</summary>
    public const int AuthoritySize = 64;

    /// <summary>Size of the Ed25519 signature buffer.</summary>
    public const int SignatureSize = 64;

    /// <summary>UTF-8 namespace prefix (32 bytes, null-padded).</summary>
    public byte[] NamespacePrefix { get; }

    /// <summary>UUID v7 identifying this namespace.</summary>
    public Guid NamespaceUuid { get; }

    /// <summary>UTF-8 authority string (64 bytes, null-padded).</summary>
    public byte[] NamespaceAuthority { get; }

    /// <summary>Ed25519 signature over the namespace registration (64 bytes).</summary>
    public byte[] NamespaceSignature { get; }

    /// <summary>Creates a new namespace registration with all fields specified.</summary>
    public NamespaceRegistration(byte[] namespacePrefix, Guid namespaceUuid, byte[] namespaceAuthority, byte[] namespaceSignature)
    {
        NamespacePrefix = namespacePrefix ?? new byte[PrefixSize];
        NamespaceUuid = namespaceUuid;
        NamespaceAuthority = namespaceAuthority ?? new byte[AuthoritySize];
        NamespaceSignature = namespaceSignature ?? new byte[SignatureSize];
    }

    /// <summary>Gets the namespace prefix as a trimmed UTF-8 string.</summary>
    public string GetPrefixString()
    {
        if (NamespacePrefix is null) return string.Empty;
        var end = Array.IndexOf(NamespacePrefix, (byte)0);
        if (end < 0) end = NamespacePrefix.Length;
        return Encoding.UTF8.GetString(NamespacePrefix, 0, end);
    }

    /// <summary>Gets the namespace authority as a trimmed UTF-8 string.</summary>
    public string GetAuthorityString()
    {
        if (NamespaceAuthority is null) return string.Empty;
        var end = Array.IndexOf(NamespaceAuthority, (byte)0);
        if (end < 0) end = NamespaceAuthority.Length;
        return Encoding.UTF8.GetString(NamespaceAuthority, 0, end);
    }

    /// <summary>
    /// Serializes this namespace registration into exactly 176 bytes.
    /// </summary>
    public static void Serialize(in NamespaceRegistration ns, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        buffer.Slice(0, SerializedSize).Clear();

        int offset = 0;

        // Prefix (32 bytes)
        var prefix = ns.NamespacePrefix ?? Array.Empty<byte>();
        prefix.AsSpan(0, Math.Min(prefix.Length, PrefixSize)).CopyTo(buffer.Slice(offset));
        offset += PrefixSize;

        // UUID (16 bytes)
        ns.NamespaceUuid.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // Authority (64 bytes)
        var authority = ns.NamespaceAuthority ?? Array.Empty<byte>();
        authority.AsSpan(0, Math.Min(authority.Length, AuthoritySize)).CopyTo(buffer.Slice(offset));
        offset += AuthoritySize;

        // Signature (64 bytes)
        var sig = ns.NamespaceSignature ?? Array.Empty<byte>();
        sig.AsSpan(0, Math.Min(sig.Length, SignatureSize)).CopyTo(buffer.Slice(offset));
    }

    /// <summary>
    /// Deserializes a namespace registration from a 176-byte buffer.
    /// </summary>
    public static NamespaceRegistration Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        int offset = 0;

        var prefix = new byte[PrefixSize];
        buffer.Slice(offset, PrefixSize).CopyTo(prefix);
        offset += PrefixSize;

        var uuid = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var authority = new byte[AuthoritySize];
        buffer.Slice(offset, AuthoritySize).CopyTo(authority);
        offset += AuthoritySize;

        var signature = new byte[SignatureSize];
        buffer.Slice(offset, SignatureSize).CopyTo(signature);

        return new NamespaceRegistration(prefix, uuid, authority, signature);
    }

    /// <inheritdoc />
    public bool Equals(NamespaceRegistration other) => NamespaceUuid == other.NamespaceUuid;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NamespaceRegistration other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => NamespaceUuid.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(NamespaceRegistration left, NamespaceRegistration right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(NamespaceRegistration left, NamespaceRegistration right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"Namespace({GetPrefixString()}, {NamespaceUuid})";
}

/// <summary>
/// Block 2 of the superblock group: Extended Metadata.
/// Contains namespace registration, dotted version vector snapshot, sovereignty
/// zone config, RAID layout summary, streaming config, fabric namespace root,
/// tier policy digest, AI metadata summary, and billing meter snapshot.
/// All fields are fixed-size byte buffers serialized into a single block.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 extended metadata (VDE2-02)")]
public readonly struct ExtendedMetadata : IEquatable<ExtendedMetadata>
{
    // ── Buffer sizes ────────────────────────────────────────────────────

    /// <summary>Size of the DVV snapshot buffer.</summary>
    public const int DvvSize = 256;

    /// <summary>Size of the sovereignty zone config buffer.</summary>
    public const int SovereigntyZoneConfigSize = 128;

    /// <summary>Size of the RAID layout summary buffer.</summary>
    public const int RaidLayoutSummarySize = 128;

    /// <summary>Size of the streaming config buffer.</summary>
    public const int StreamingConfigSize = 128;

    /// <summary>Size of the fabric namespace root buffer.</summary>
    public const int FabricNamespaceRootSize = 256;

    /// <summary>Size of the tier policy digest buffer.</summary>
    public const int TierPolicyDigestSize = 128;

    /// <summary>Size of the AI metadata summary buffer.</summary>
    public const int AiMetadataSummarySize = 128;

    /// <summary>Size of the billing meter snapshot buffer.</summary>
    public const int BillingMeterSnapshotSize = 128;

    /// <summary>Total serialized size of all fields (excluding reserved/padding).</summary>
    public const int FieldsSize = NamespaceRegistration.SerializedSize
        + DvvSize + SovereigntyZoneConfigSize + RaidLayoutSummarySize
        + StreamingConfigSize + FabricNamespaceRootSize + TierPolicyDigestSize
        + AiMetadataSummarySize + BillingMeterSnapshotSize;

    // ── Fields ──────────────────────────────────────────────────────────

    /// <summary>Namespace registration (176 bytes).</summary>
    public NamespaceRegistration Namespace { get; }

    /// <summary>Dotted Version Vector snapshot (256 bytes).</summary>
    public byte[] DottedVersionVector { get; }

    /// <summary>Sovereignty zone configuration (128 bytes).</summary>
    public byte[] SovereigntyZoneConfig { get; }

    /// <summary>RAID layout summary (128 bytes).</summary>
    public byte[] RaidLayoutSummary { get; }

    /// <summary>Streaming configuration (128 bytes).</summary>
    public byte[] StreamingConfig { get; }

    /// <summary>Fabric namespace root (256 bytes).</summary>
    public byte[] FabricNamespaceRoot { get; }

    /// <summary>Tier policy digest (128 bytes).</summary>
    public byte[] TierPolicyDigest { get; }

    /// <summary>AI metadata summary (128 bytes).</summary>
    public byte[] AiMetadataSummary { get; }

    /// <summary>Billing meter snapshot (128 bytes).</summary>
    public byte[] BillingMeterSnapshot { get; }

    // ── Constructor ─────────────────────────────────────────────────────

    /// <summary>Creates a new extended metadata block with all fields specified.</summary>
    public ExtendedMetadata(
        NamespaceRegistration ns,
        byte[] dottedVersionVector,
        byte[] sovereigntyZoneConfig,
        byte[] raidLayoutSummary,
        byte[] streamingConfig,
        byte[] fabricNamespaceRoot,
        byte[] tierPolicyDigest,
        byte[] aiMetadataSummary,
        byte[] billingMeterSnapshot)
    {
        Namespace = ns;
        DottedVersionVector = dottedVersionVector ?? new byte[DvvSize];
        SovereigntyZoneConfig = sovereigntyZoneConfig ?? new byte[SovereigntyZoneConfigSize];
        RaidLayoutSummary = raidLayoutSummary ?? new byte[RaidLayoutSummarySize];
        StreamingConfig = streamingConfig ?? new byte[StreamingConfigSize];
        FabricNamespaceRoot = fabricNamespaceRoot ?? new byte[FabricNamespaceRootSize];
        TierPolicyDigest = tierPolicyDigest ?? new byte[TierPolicyDigestSize];
        AiMetadataSummary = aiMetadataSummary ?? new byte[AiMetadataSummarySize];
        BillingMeterSnapshot = billingMeterSnapshot ?? new byte[BillingMeterSnapshotSize];
    }

    /// <summary>Creates a default (empty) extended metadata block.</summary>
    public static ExtendedMetadata CreateDefault() => new(
        default,
        new byte[DvvSize],
        new byte[SovereigntyZoneConfigSize],
        new byte[RaidLayoutSummarySize],
        new byte[StreamingConfigSize],
        new byte[FabricNamespaceRootSize],
        new byte[TierPolicyDigestSize],
        new byte[AiMetadataSummarySize],
        new byte[BillingMeterSnapshotSize]);

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes this extended metadata into a block-sized buffer.
    /// Remaining bytes after all fields are zero-filled.
    /// </summary>
    public static void Serialize(in ExtendedMetadata em, Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        buffer.Slice(0, blockSize).Clear();

        int offset = 0;

        // Namespace registration (176 bytes)
        NamespaceRegistration.Serialize(em.Namespace, buffer.Slice(offset));
        offset += NamespaceRegistration.SerializedSize;

        // DVV (256 bytes)
        CopyField(em.DottedVersionVector, buffer.Slice(offset), DvvSize);
        offset += DvvSize;

        // Sovereignty zone config (128 bytes)
        CopyField(em.SovereigntyZoneConfig, buffer.Slice(offset), SovereigntyZoneConfigSize);
        offset += SovereigntyZoneConfigSize;

        // RAID layout summary (128 bytes)
        CopyField(em.RaidLayoutSummary, buffer.Slice(offset), RaidLayoutSummarySize);
        offset += RaidLayoutSummarySize;

        // Streaming config (128 bytes)
        CopyField(em.StreamingConfig, buffer.Slice(offset), StreamingConfigSize);
        offset += StreamingConfigSize;

        // Fabric namespace root (256 bytes)
        CopyField(em.FabricNamespaceRoot, buffer.Slice(offset), FabricNamespaceRootSize);
        offset += FabricNamespaceRootSize;

        // Tier policy digest (128 bytes)
        CopyField(em.TierPolicyDigest, buffer.Slice(offset), TierPolicyDigestSize);
        offset += TierPolicyDigestSize;

        // AI metadata summary (128 bytes)
        CopyField(em.AiMetadataSummary, buffer.Slice(offset), AiMetadataSummarySize);
        offset += AiMetadataSummarySize;

        // Billing meter snapshot (128 bytes)
        CopyField(em.BillingMeterSnapshot, buffer.Slice(offset), BillingMeterSnapshotSize);
        // Remaining bytes already zeroed
    }

    /// <summary>
    /// Deserializes extended metadata from a block-sized buffer.
    /// </summary>
    public static ExtendedMetadata Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        int offset = 0;

        var ns = NamespaceRegistration.Deserialize(buffer.Slice(offset));
        offset += NamespaceRegistration.SerializedSize;

        var dvv = ReadField(buffer, ref offset, DvvSize);
        var sovereignty = ReadField(buffer, ref offset, SovereigntyZoneConfigSize);
        var raid = ReadField(buffer, ref offset, RaidLayoutSummarySize);
        var streaming = ReadField(buffer, ref offset, StreamingConfigSize);
        var fabric = ReadField(buffer, ref offset, FabricNamespaceRootSize);
        var tierPolicy = ReadField(buffer, ref offset, TierPolicyDigestSize);
        var aiMetadata = ReadField(buffer, ref offset, AiMetadataSummarySize);
        var billing = ReadField(buffer, ref offset, BillingMeterSnapshotSize);

        return new ExtendedMetadata(ns, dvv, sovereignty, raid, streaming, fabric, tierPolicy, aiMetadata, billing);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void CopyField(byte[]? source, Span<byte> dest, int fieldSize)
    {
        if (source is not null)
        {
            var len = Math.Min(source.Length, fieldSize);
            source.AsSpan(0, len).CopyTo(dest);
        }
    }

    private static byte[] ReadField(ReadOnlySpan<byte> buffer, ref int offset, int size)
    {
        var result = new byte[size];
        buffer.Slice(offset, size).CopyTo(result);
        offset += size;
        return result;
    }

    // ── Equality ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(ExtendedMetadata other) =>
        Namespace == other.Namespace &&
        DottedVersionVector.AsSpan().SequenceEqual(other.DottedVersionVector) &&
        SovereigntyZoneConfig.AsSpan().SequenceEqual(other.SovereigntyZoneConfig) &&
        RaidLayoutSummary.AsSpan().SequenceEqual(other.RaidLayoutSummary) &&
        StreamingConfig.AsSpan().SequenceEqual(other.StreamingConfig) &&
        FabricNamespaceRoot.AsSpan().SequenceEqual(other.FabricNamespaceRoot) &&
        TierPolicyDigest.AsSpan().SequenceEqual(other.TierPolicyDigest) &&
        AiMetadataSummary.AsSpan().SequenceEqual(other.AiMetadataSummary) &&
        BillingMeterSnapshot.AsSpan().SequenceEqual(other.BillingMeterSnapshot);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ExtendedMetadata other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Namespace);
        // Include a representative sample from each byte array for hash stability
        foreach (var b in DottedVersionVector.AsSpan(0, Math.Min(8, DottedVersionVector.Length)))
            hc.Add(b);
        foreach (var b in SovereigntyZoneConfig.AsSpan(0, Math.Min(8, SovereigntyZoneConfig.Length)))
            hc.Add(b);
        return hc.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ExtendedMetadata left, ExtendedMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ExtendedMetadata left, ExtendedMetadata right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"ExtendedMetadata(Namespace={Namespace})";
}
