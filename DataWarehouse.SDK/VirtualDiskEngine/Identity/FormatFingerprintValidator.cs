using System.Buffers.Binary;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Computes and validates the 32-byte SHA-256 format fingerprint stored in
/// <see cref="IntegrityAnchor.FormatFingerprint"/>. The fingerprint is a deterministic
/// hash of the format specification identity (major version, minor version, spec revision,
/// block size constraints, superblock layout, and module limits), enabling detection of
/// format version mismatches before opening a VDE file.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint input is a fixed 20-byte sequence:
/// <c>[MajorVersion:1][MinorVersion:1][SpecRevision:2 LE][MinBlockSize:4 LE][MaxBlockSize:4 LE][SuperblockGroupBlocks:4 LE][MaxModules:4 LE]</c>
/// </para>
/// <para>
/// All methods are pure (no I/O, no mutable state) and thread-safe.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- format fingerprint (VTMP-02)")]
public static class FormatFingerprintValidator
{
    /// <summary>Size of the fingerprint input data in bytes.</summary>
    private const int FingerprintInputSize = 20;

    /// <summary>Size of the SHA-256 fingerprint output in bytes.</summary>
    public const int FingerprintSize = 32;

    /// <summary>
    /// Computes the format fingerprint for the current specification revision
    /// using values from <see cref="FormatConstants"/>.
    /// </summary>
    /// <returns>A 32-byte SHA-256 hash of the current format specification identity.</returns>
    public static byte[] ComputeFingerprint()
    {
        return ComputeFingerprint(
            FormatConstants.FormatMajorVersion,
            FormatConstants.FormatMinorVersion,
            FormatConstants.SpecRevision);
    }

    /// <summary>
    /// Computes the format fingerprint for an explicit version, using the current
    /// block size and module constants. Useful for cross-version comparison.
    /// </summary>
    /// <param name="majorVersion">Format major version.</param>
    /// <param name="minorVersion">Format minor version.</param>
    /// <param name="specRevision">Specification revision within this major.minor.</param>
    /// <returns>A 32-byte SHA-256 hash of the specified format specification identity.</returns>
    public static byte[] ComputeFingerprint(byte majorVersion, byte minorVersion, ushort specRevision)
    {
        // Build the deterministic 20-byte input
        Span<byte> input = stackalloc byte[FingerprintInputSize];
        int offset = 0;

        // [MajorVersion:1]
        input[offset++] = majorVersion;

        // [MinorVersion:1]
        input[offset++] = minorVersion;

        // [SpecRevision:2 LE]
        BinaryPrimitives.WriteUInt16LittleEndian(input.Slice(offset), specRevision);
        offset += 2;

        // [MinBlockSize:4 LE]
        BinaryPrimitives.WriteInt32LittleEndian(input.Slice(offset), FormatConstants.MinBlockSize);
        offset += 4;

        // [MaxBlockSize:4 LE]
        BinaryPrimitives.WriteInt32LittleEndian(input.Slice(offset), FormatConstants.MaxBlockSize);
        offset += 4;

        // [SuperblockGroupBlocks:4 LE]
        BinaryPrimitives.WriteInt32LittleEndian(input.Slice(offset), FormatConstants.SuperblockGroupBlocks);
        offset += 4;

        // [MaxModules:4 LE]
        BinaryPrimitives.WriteInt32LittleEndian(input.Slice(offset), FormatConstants.MaxModules);

        // Compute SHA-256
        Span<byte> hash = stackalloc byte[FingerprintSize];
        SHA256.HashData(input, hash);
        return hash.ToArray();
    }

    /// <summary>
    /// Validates a stored fingerprint against the current specification's fingerprint.
    /// Uses constant-time comparison to prevent timing side-channels.
    /// </summary>
    /// <param name="storedFingerprint">The fingerprint read from the VDE file's IntegrityAnchor.</param>
    /// <returns>True if the stored fingerprint matches the current specification; false otherwise.</returns>
    public static bool ValidateFingerprint(ReadOnlySpan<byte> storedFingerprint)
    {
        if (storedFingerprint.Length < FingerprintSize)
            return false;

        Span<byte> expected = stackalloc byte[FingerprintSize];
        var computed = ComputeFingerprint();
        computed.AsSpan().CopyTo(expected);

        return CryptographicOperations.FixedTimeEquals(
            expected,
            storedFingerprint.Slice(0, FingerprintSize));
    }

    /// <summary>
    /// Validates a stored fingerprint and provides version context for error reporting.
    /// Since the fingerprint is a one-way hash, the stored version cannot be extracted
    /// from the hash itself -- the out parameters provide the current expected version instead.
    /// </summary>
    /// <param name="storedFingerprint">The fingerprint read from the VDE file's IntegrityAnchor.</param>
    /// <param name="expectedMajor">Set to the current format major version (for error messages).</param>
    /// <param name="expectedMinor">Set to the current format minor version (for error messages).</param>
    /// <returns>True if the stored fingerprint matches the current specification; false otherwise.</returns>
    public static bool ValidateFingerprint(
        ReadOnlySpan<byte> storedFingerprint,
        out byte expectedMajor,
        out byte expectedMinor)
    {
        expectedMajor = FormatConstants.FormatMajorVersion;
        expectedMinor = FormatConstants.FormatMinorVersion;

        return ValidateFingerprint(storedFingerprint);
    }

    /// <summary>
    /// Validates the stored fingerprint and throws <see cref="VdeFingerprintMismatchException"/>
    /// with a descriptive message including hex values if validation fails.
    /// </summary>
    /// <param name="storedFingerprint">The fingerprint read from the VDE file's IntegrityAnchor.</param>
    /// <exception cref="VdeFingerprintMismatchException">
    /// Thrown when the stored fingerprint does not match the expected fingerprint
    /// for the current format specification revision.
    /// </exception>
    public static void ValidateOrThrow(ReadOnlySpan<byte> storedFingerprint)
    {
        if (ValidateFingerprint(storedFingerprint))
            return;

        var expected = ComputeFingerprint();
        var expectedHex = Convert.ToHexString(expected);

        var actualHex = storedFingerprint.Length >= FingerprintSize
            ? Convert.ToHexString(storedFingerprint.Slice(0, FingerprintSize))
            : Convert.ToHexString(storedFingerprint);

        throw new VdeFingerprintMismatchException(
            $"Format fingerprint mismatch: the VDE file was created with a different specification revision. " +
            $"Expected fingerprint for v{FormatConstants.FormatMajorVersion}.{FormatConstants.FormatMinorVersion}r{FormatConstants.SpecRevision}: " +
            $"{expectedHex}, but found: {actualHex}.",
            expectedHex,
            actualHex);
    }
}
