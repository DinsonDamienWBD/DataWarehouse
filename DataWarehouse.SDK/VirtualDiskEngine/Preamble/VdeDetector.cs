using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Classification of a DWVD file based on the initial bytes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-62 VDE file type detection")]
public enum VdeFileType
{
    /// <summary>A bootable preamble is present before Block 0.</summary>
    PreamblePresent,

    /// <summary>A v2.x DWVD file without a preamble (Block 0 starts at byte 0).</summary>
    V2PreambleFree,

    /// <summary>A legacy v1.x DWVD file.</summary>
    LegacyV1,

    /// <summary>The file does not contain a valid DWVD signature.</summary>
    NotDwvd,
}

/// <summary>
/// Result of VDE file type detection, providing the file classification,
/// the byte offset of Block 0, and the preamble header when present.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-62 VDE detection result")]
public readonly struct VdeDetectionResult : IEquatable<VdeDetectionResult>
{
    /// <summary>The detected file type.</summary>
    public VdeFileType FileType { get; }

    /// <summary>
    /// Byte offset of VDE Block 0 from the start of the file.
    /// Zero for preamble-free and legacy files; equals PreambleHeader.VdeOffset for preamble-present files.
    /// </summary>
    public long VdeOffset { get; }

    /// <summary>
    /// The deserialized preamble header, or <c>null</c> if the file does not have a preamble.
    /// </summary>
    public PreambleHeader? Preamble { get; }

    /// <summary>Creates a new detection result.</summary>
    public VdeDetectionResult(VdeFileType fileType, long vdeOffset, PreambleHeader? preamble = null)
    {
        FileType = fileType;
        VdeOffset = vdeOffset;
        Preamble = preamble;
    }

    /// <inheritdoc />
    public bool Equals(VdeDetectionResult other) =>
        FileType == other.FileType && VdeOffset == other.VdeOffset && Preamble.Equals(other.Preamble);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VdeDetectionResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FileType, VdeOffset, Preamble);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(VdeDetectionResult left, VdeDetectionResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(VdeDetectionResult left, VdeDetectionResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"VdeDetectionResult(Type={FileType}, VdeOffset={VdeOffset}, HasPreamble={Preamble.HasValue})";
}

/// <summary>
/// Detects the type of a DWVD file by examining the initial bytes.
/// Distinguishes between preamble-present (bootable), v2.x preamble-free, legacy v1.x, and non-DWVD files.
/// </summary>
/// <remarks>
/// <para>Detection algorithm:</para>
/// <list type="number">
/// <item><description>Read bytes 0-7. If they match "DWVD-BOO" magic, the file has a bootable preamble.</description></item>
/// <item><description>If bytes 0-3 match "DWVD" (0x44575644 big-endian), read byte 4 (major version):
/// major >= 2 indicates v2.x preamble-free; otherwise legacy v1.x.</description></item>
/// <item><description>Otherwise the file is not a valid DWVD container.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-62 VDE file type detector")]
public static class VdeDetector
{
    /// <summary>Minimum number of bytes required for detection.</summary>
    private const int MinDetectionBytes = PreambleHeader.SerializedSize;

    /// <summary>"DWVD" as big-endian uint32 (0x44575644).</summary>
    private const uint DwvdIdentifier = 0x44575644u;

    /// <summary>
    /// Detects the type of a DWVD file from a stream.
    /// The stream must be seekable and positioned at the beginning.
    /// </summary>
    /// <param name="stream">A readable, seekable stream containing the DWVD file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A detection result indicating file type, VDE offset, and optional preamble header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.</exception>
    public static async Task<VdeDetectionResult> DetectAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable.", nameof(stream));

        // Ensure we read from position 0
        if (stream.CanSeek)
            stream.Position = 0;

        // Read enough bytes for preamble header detection (64 bytes covers both preamble and superblock magic)
        var buffer = new byte[MinDetectionBytes];
        int totalRead = 0;
        while (totalRead < MinDetectionBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, MinDetectionBytes - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break; // End of stream
            totalRead += read;
        }

        if (totalRead < 8)
            return new VdeDetectionResult(VdeFileType.NotDwvd, 0);

        return Detect(buffer.AsSpan(0, totalRead));
    }

    /// <summary>
    /// Detects the type of a DWVD file from a byte buffer.
    /// The buffer must contain at least 8 bytes from the start of the file.
    /// </summary>
    /// <param name="buffer">Buffer containing the initial bytes of the file.</param>
    /// <returns>A detection result indicating file type, VDE offset, and optional preamble header.</returns>
    public static VdeDetectionResult Detect(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
            return new VdeDetectionResult(VdeFileType.NotDwvd, 0);

        // Check for preamble magic ("DWVD-BOO" as LE uint64)
        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        if (magic == PreambleHeader.ExpectedMagic)
        {
            // Preamble detected - need full 64 bytes to read the header
            if (buffer.Length < PreambleHeader.SerializedSize)
            {
                // We know it's a preamble file but can't fully parse the header
                return new VdeDetectionResult(VdeFileType.PreamblePresent, 0);
            }

            var header = PreambleHeader.Deserialize(buffer);
            return new VdeDetectionResult(VdeFileType.PreamblePresent, (long)header.VdeOffset, header);
        }

        // Check for "DWVD" superblock magic (bytes 0-3 as big-endian uint32)
        uint formatId = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        if (formatId == DwvdIdentifier)
        {
            // Byte 4 is the major version
            byte majorVersion = buffer[4];
            if (majorVersion >= 2)
                return new VdeDetectionResult(VdeFileType.V2PreambleFree, 0);
            else
                return new VdeDetectionResult(VdeFileType.LegacyV1, 0);
        }

        return new VdeDetectionResult(VdeFileType.NotDwvd, 0);
    }
}
