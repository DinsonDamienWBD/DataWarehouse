using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compatibility;

/// <summary>
/// Detected VDE format version extracted from the first 16 bytes of a DWVD file.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: VDE format version detection (MIGR-01)")]
public readonly record struct DetectedFormatVersion
{
    /// <summary>Format major version (1 = legacy, 2 = current).</summary>
    public byte MajorVersion { get; }

    /// <summary>Format minor version.</summary>
    public byte MinorVersion { get; }

    /// <summary>Specification revision within this major.minor pair.</summary>
    public ushort SpecRevision { get; }

    /// <summary>True if the v2.0 namespace anchor ("dw://") was present and valid.</summary>
    public bool NamespaceValid { get; }

    /// <summary>True if this is a v1.0 format VDE.</summary>
    public bool IsV1 => MajorVersion == 1;

    /// <summary>True if this is a v2.0 format VDE.</summary>
    public bool IsV2 => MajorVersion == 2;

    /// <summary>True if the format version is unrecognized (neither v1 nor v2).</summary>
    public bool IsUnknown => MajorVersion != 1 && MajorVersion != 2;

    /// <summary>Human-readable format description.</summary>
    public string FormatDescription => $"DWVD v{MajorVersion}.{MinorVersion} rev{SpecRevision}";

    /// <summary>
    /// Creates a new detected format version.
    /// </summary>
    public DetectedFormatVersion(byte majorVersion, byte minorVersion, ushort specRevision, bool namespaceValid)
    {
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        SpecRevision = specRevision;
        NamespaceValid = namespaceValid;
    }
}

/// <summary>
/// Detects the VDE format version from raw bytes, streams, or file paths.
/// Recognizes v1.0 (legacy) and v2.0 (current) formats and returns version metadata
/// for routing to the appropriate compatibility layer.
/// </summary>
/// <remarks>
/// <para>
/// Detection reads the first 16 bytes (the magic signature area) and extracts:
/// bytes 0-3 as big-endian uint32 (must be 0x44575644 = "DWVD"),
/// byte 4 as MajorVersion, byte 5 as MinorVersion,
/// bytes 6-7 as SpecRevision (little-endian).
/// </para>
/// <para>
/// For v2.0 files, the namespace anchor at bytes 8-12 ("dw://") is additionally
/// validated via <see cref="MagicSignature.Deserialize"/> and <see cref="MagicSignature.Validate"/>.
/// For v1.0 files, the namespace anchor is not checked (it predates v2.0).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 81: VDE format version detector (MIGR-01)")]
public static class VdeFormatDetector
{
    /// <summary>"DWVD" as big-endian uint32: D=0x44, W=0x57, V=0x56, D=0x44.</summary>
    private const uint DwvdMagic = 0x44575644u;

    /// <summary>Minimum bytes required for format detection (magic signature size).</summary>
    private const int MinDetectionBytes = FormatConstants.MagicSignatureSize;

    /// <summary>
    /// Detects the VDE format version from raw bytes.
    /// </summary>
    /// <param name="data">At least 16 bytes from the start of a VDE file.</param>
    /// <returns>
    /// A <see cref="DetectedFormatVersion"/> if the magic bytes match "DWVD";
    /// null if the data is too short or the magic does not match.
    /// </returns>
    public static DetectedFormatVersion? DetectFromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinDetectionBytes)
            return null;

        // Read first 4 bytes as big-endian uint32; must equal "DWVD"
        uint identifier = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (identifier != DwvdMagic)
            return null;

        // Extract version fields
        byte majorVersion = data[4];
        byte minorVersion = data[5];
        ushort specRevision = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));

        // For v2.0: validate namespace anchor via MagicSignature
        bool namespaceValid = false;
        if (majorVersion >= 2)
        {
            try
            {
                var magic = MagicSignature.Deserialize(data);
                namespaceValid = magic.Validate();
            }
            catch
            {
                // Deserialization failure: namespace invalid but version still detectable
            }
        }
        // For v1.0: namespace anchor may not exist; skip validation (considered valid for v1)

        return new DetectedFormatVersion(majorVersion, minorVersion, specRevision, namespaceValid);
    }

    /// <summary>
    /// Detects the VDE format version from a stream by reading the first 16 bytes.
    /// </summary>
    /// <param name="stream">The stream to read from. Must support reading.</param>
    /// <returns>
    /// A <see cref="DetectedFormatVersion"/> if the stream contains a valid DWVD header;
    /// null if the stream is too short or the magic does not match.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static DetectedFormatVersion? DetectFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> buffer = stackalloc byte[MinDetectionBytes];
        int totalRead = 0;
        while (totalRead < MinDetectionBytes)
        {
            int read = stream.Read(buffer.Slice(totalRead));
            if (read == 0)
                break;
            totalRead += read;
        }

        if (totalRead < MinDetectionBytes)
            return null;

        return DetectFromBytes(buffer);
    }

    /// <summary>
    /// Asynchronously detects the VDE format version from a file path.
    /// </summary>
    /// <param name="filePath">Path to the VDE file to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DetectedFormatVersion"/> if the file contains a valid DWVD header;
    /// null if the file is too short or the magic does not match.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
    public static async Task<DetectedFormatVersion?> DetectAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: MinDetectionBytes,
            useAsync: true);

        var buffer = new byte[MinDetectionBytes];
        int totalRead = 0;
        while (totalRead < MinDetectionBytes)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, MinDetectionBytes - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        if (totalRead < MinDetectionBytes)
            return null;

        return DetectFromBytes(buffer);
    }
}
