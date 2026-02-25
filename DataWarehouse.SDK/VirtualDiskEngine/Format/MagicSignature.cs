using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Represents the 16-byte magic signature at offset 0 of every DWVD v2.0 file.
/// Layout: bytes 0-3 "DWVD", byte 4 major version, byte 5 minor version,
/// bytes 6-7 spec revision (LE), bytes 8-12 "dw://", bytes 13-15 zero padding.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE Format v2.0 magic signature (VDE2-01)")]
public readonly struct MagicSignature : IEquatable<MagicSignature>
{
    /// <summary>"DWVD" encoded as big-endian uint32: D=0x44, W=0x57, V=0x56, D=0x44.</summary>
    public uint FormatIdentifier { get; }

    /// <summary>Format major version number.</summary>
    public byte MajorVersion { get; }

    /// <summary>Format minor version number.</summary>
    public byte MinorVersion { get; }

    /// <summary>Specification revision within this major.minor pair (little-endian on disk).</summary>
    public ushort SpecRevision { get; }

    /// <summary>
    /// Bytes 8-15: namespace anchor "dw://" (5 bytes) + 3 bytes zero padding,
    /// packed into a ulong for zero-allocation storage.
    /// </summary>
    public ulong NamespaceAnchor { get; }

    /// <summary>Expected FormatIdentifier value for "DWVD" in big-endian byte order.</summary>
    private const uint ExpectedIdentifier = 0x44575644u;

    /// <summary>
    /// Expected NamespaceAnchor value: bytes "dw://" (0x64,0x77,0x3A,0x2F,0x2F)
    /// followed by three zero bytes, stored as little-endian ulong.
    /// </summary>
    private static readonly ulong ExpectedNamespaceAnchor = BuildExpectedNamespaceAnchor();

    private MagicSignature(uint formatIdentifier, byte majorVersion, byte minorVersion,
        ushort specRevision, ulong namespaceAnchor)
    {
        FormatIdentifier = formatIdentifier;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        SpecRevision = specRevision;
        NamespaceAnchor = namespaceAnchor;
    }

    /// <summary>
    /// Creates the default v2.0 magic signature with current spec revision.
    /// </summary>
    public static MagicSignature CreateDefault()
    {
        return new MagicSignature(
            ExpectedIdentifier,
            FormatConstants.FormatMajorVersion,
            FormatConstants.FormatMinorVersion,
            FormatConstants.SpecRevision,
            ExpectedNamespaceAnchor);
    }

    /// <summary>
    /// Serializes this signature into exactly 16 bytes.
    /// </summary>
    /// <param name="sig">The signature to serialize.</param>
    /// <param name="buffer">Destination buffer, must be at least 16 bytes.</param>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static void Serialize(in MagicSignature sig, Span<byte> buffer)
    {
        if (buffer.Length < FormatConstants.MagicSignatureSize)
            throw new ArgumentException(
                $"Buffer must be at least {FormatConstants.MagicSignatureSize} bytes.",
                nameof(buffer));

        // Bytes 0-3: "DWVD" as big-endian uint32
        BinaryPrimitives.WriteUInt32BigEndian(buffer, sig.FormatIdentifier);

        // Byte 4: major version
        buffer[4] = sig.MajorVersion;

        // Byte 5: minor version
        buffer[5] = sig.MinorVersion;

        // Bytes 6-7: spec revision (little-endian)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6), sig.SpecRevision);

        // Bytes 8-15: namespace anchor as little-endian ulong
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(8), sig.NamespaceAnchor);
    }

    /// <summary>
    /// Deserializes a 16-byte magic signature from a buffer.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least 16 bytes.</param>
    /// <returns>The deserialized magic signature.</returns>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static MagicSignature Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < FormatConstants.MagicSignatureSize)
            throw new ArgumentException(
                $"Buffer must be at least {FormatConstants.MagicSignatureSize} bytes.",
                nameof(buffer));

        var formatId = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        var major = buffer[4];
        var minor = buffer[5];
        var specRev = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6));
        var nsAnchor = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(8));

        return new MagicSignature(formatId, major, minor, specRev, nsAnchor);
    }

    /// <summary>
    /// Validates that this signature is a well-formed DWVD v2.x signature.
    /// Checks: "DWVD" identifier, "dw://" namespace anchor, and zero padding.
    /// </summary>
    /// <returns>True if the signature is valid.</returns>
    public bool Validate()
    {
        if (FormatIdentifier != ExpectedIdentifier)
            return false;

        if (NamespaceAnchor != ExpectedNamespaceAnchor)
            return false;

        return true;
    }

    /// <summary>
    /// Checks if this signature is compatible with the specified minimum version.
    /// </summary>
    /// <param name="minMajor">Minimum required major version.</param>
    /// <param name="minMinor">Minimum required minor version.</param>
    /// <returns>True if this signature's version meets or exceeds the minimum.</returns>
    public bool IsCompatible(byte minMajor, byte minMinor)
    {
        if (MajorVersion > minMajor)
            return true;
        if (MajorVersion < minMajor)
            return false;
        return MinorVersion >= minMinor;
    }

    /// <inheritdoc />
    public bool Equals(MagicSignature other)
    {
        return FormatIdentifier == other.FormatIdentifier
            && MajorVersion == other.MajorVersion
            && MinorVersion == other.MinorVersion
            && SpecRevision == other.SpecRevision
            && NamespaceAnchor == other.NamespaceAnchor;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MagicSignature other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FormatIdentifier, MajorVersion, MinorVersion, SpecRevision, NamespaceAnchor);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(MagicSignature left, MagicSignature right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(MagicSignature left, MagicSignature right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"DWVD v{MajorVersion}.{MinorVersion} rev{SpecRevision}";

    private static ulong BuildExpectedNamespaceAnchor()
    {
        Span<byte> temp = stackalloc byte[8];
        // "dw://" = 0x64, 0x77, 0x3A, 0x2F, 0x2F
        temp[0] = 0x64;
        temp[1] = 0x77;
        temp[2] = 0x3A;
        temp[3] = 0x2F;
        temp[4] = 0x2F;
        // bytes 5-7 remain zero (padding)
        return BinaryPrimitives.ReadUInt64LittleEndian(temp);
    }
}
