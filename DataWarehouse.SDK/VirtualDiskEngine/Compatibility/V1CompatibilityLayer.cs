using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compatibility;

/// <summary>
/// Summary of a v1.0 VDE superblock, extracted for migration tooling and diagnostics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: v1.0 superblock summary (MIGR-01)")]
public readonly record struct V1SuperblockSummary
{
    /// <summary>Unique identifier for the v1.0 volume.</summary>
    public Guid VolumeUuid { get; init; }

    /// <summary>Human-readable volume label (up to 64 bytes, UTF-8).</summary>
    public string VolumeLabel { get; init; }

    /// <summary>Total number of blocks in the v1.0 VDE.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Block size in bytes.</summary>
    public int BlockSize { get; init; }

    /// <summary>Format major version (expected: 1).</summary>
    public byte MajorVersion { get; init; }

    /// <summary>Format minor version.</summary>
    public byte MinorVersion { get; init; }
}

/// <summary>
/// Provides read-only interpretation of v1.0 VDE files. Parses the v1.0 superblock layout
/// and exposes volume metadata for migration tooling without modifying the source file.
/// </summary>
/// <remarks>
/// <para>
/// The v1.0 superblock layout (plausible reconstruction):
/// <list type="bullet">
/// <item>Offset 0x00: Magic "DWVD" (4 bytes)</item>
/// <item>Offset 0x04: MajorVersion (1 byte), MinorVersion (1 byte)</item>
/// <item>Offset 0x06: Reserved (2 bytes)</item>
/// <item>Offset 0x08: BlockSize (4 bytes, little-endian)</item>
/// <item>Offset 0x0C: TotalBlocks (8 bytes, little-endian)</item>
/// <item>Offset 0x14: VolumeUuid (16 bytes)</item>
/// <item>Offset 0x24: VolumeLabel (64 bytes, UTF-8, null-terminated)</item>
/// </list>
/// </para>
/// <para>
/// All reads use <see cref="BinaryPrimitives"/> and <see cref="ArrayPool{T}"/> for
/// zero-allocation buffer management on the hot path.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 81: v1.0 compatibility layer (MIGR-01)")]
public sealed class V1CompatibilityLayer
{
    /// <summary>Minimum block size for the source stream (same constraint as VDE v2.0).</summary>
    private const int MinBlockSize = FormatConstants.MinBlockSize;

    /// <summary>Maximum block size for the source stream (same constraint as VDE v2.0).</summary>
    private const int MaxBlockSize = FormatConstants.MaxBlockSize;

    /// <summary>Minimum superblock size required for v1.0 parsing (0x24 + 64 = 100 bytes).</summary>
    private const int MinV1SuperblockSize = 0x24 + 64;

    // v1.0 superblock field offsets
    private const int OffsetMajorVersion = 0x04;
    private const int OffsetMinorVersion = 0x05;
    private const int OffsetBlockSize = 0x08;
    private const int OffsetTotalBlocks = 0x0C;
    private const int OffsetVolumeUuid = 0x14;
    private const int OffsetVolumeLabel = 0x24;
    private const int VolumeLabelLength = 64;

    private readonly Stream _sourceStream;
    private readonly int _blockSize;
    private V1SuperblockSummary? _cachedSummary;

    /// <summary>
    /// Creates a new v1.0 compatibility layer for the specified source stream.
    /// </summary>
    /// <param name="sourceStream">The v1.0 VDE source stream. Must support reading and seeking.</param>
    /// <param name="blockSize">Block size in bytes (must be between 512 and 65536).</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceStream"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is outside valid range.</exception>
    public V1CompatibilityLayer(Stream sourceStream, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);

        if (blockSize < MinBlockSize || blockSize > MaxBlockSize)
            throw new ArgumentOutOfRangeException(
                nameof(blockSize),
                blockSize,
                $"Block size must be between {MinBlockSize} and {MaxBlockSize} bytes.");

        _sourceStream = sourceStream;
        _blockSize = blockSize;
    }

    /// <summary>
    /// Reads and parses the v1.0 superblock from the source stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="V1SuperblockSummary"/> with the parsed volume metadata.</returns>
    /// <exception cref="VdeFormatException">
    /// Thrown if the stream is too short or the superblock cannot be parsed.
    /// </exception>
    public async Task<V1SuperblockSummary> ReadV1SuperblockAsync(CancellationToken ct = default)
    {
        if (_cachedSummary.HasValue)
            return _cachedSummary.Value;

        // Read the first block
        int readSize = Math.Max(_blockSize, MinV1SuperblockSize);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(readSize);
        try
        {
            // Seek to start if the stream supports it
            if (_sourceStream.CanSeek)
                _sourceStream.Position = 0;

            int totalRead = 0;
            while (totalRead < readSize)
            {
                int read = await _sourceStream.ReadAsync(
                    buffer.AsMemory(totalRead, readSize - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                totalRead += read;
            }

            if (totalRead < MinV1SuperblockSize)
                throw new VdeFormatException(
                    $"Stream too short for v1.0 superblock parsing: expected at least {MinV1SuperblockSize} bytes, got {totalRead}.");

            var data = buffer.AsSpan(0, totalRead);

            // Verify DWVD magic
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (magic != 0x44575644u)
                throw new VdeFormatException(
                    "Invalid magic bytes: expected 'DWVD' (0x44575644) at offset 0.");

            byte majorVersion = data[OffsetMajorVersion];
            byte minorVersion = data[OffsetMinorVersion];
            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(OffsetBlockSize));
            long totalBlocks = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(OffsetTotalBlocks));

            // Parse UUID (16 bytes at offset 0x14)
            var uuidBytes = data.Slice(OffsetVolumeUuid, 16);
            var volumeUuid = new Guid(uuidBytes);

            // Parse volume label (64 bytes UTF-8, null-terminated)
            var labelSpan = data.Slice(OffsetVolumeLabel, VolumeLabelLength);
            int labelEnd = labelSpan.IndexOf((byte)0);
            if (labelEnd < 0)
                labelEnd = VolumeLabelLength;
            string volumeLabel = Encoding.UTF8.GetString(labelSpan.Slice(0, labelEnd));

            var summary = new V1SuperblockSummary
            {
                VolumeUuid = volumeUuid,
                VolumeLabel = volumeLabel,
                TotalBlocks = totalBlocks,
                BlockSize = blockSize,
                MajorVersion = majorVersion,
                MinorVersion = minorVersion
            };

            _cachedSummary = summary;
            return summary;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Creates a <see cref="CompatibilityModeContext"/> for this v1.0 VDE.
    /// </summary>
    /// <returns>A context configured for v1.0 read-only compatibility mode.</returns>
    public CompatibilityModeContext GetCompatibilityContext()
    {
        var version = _cachedSummary.HasValue
            ? new DetectedFormatVersion(
                _cachedSummary.Value.MajorVersion,
                _cachedSummary.Value.MinorVersion,
                specRevision: 0,
                namespaceValid: false)
            : new DetectedFormatVersion(1, 0, 0, false);

        return CompatibilityModeContext.ForV1(version);
    }

    /// <summary>
    /// Estimates the total data size in bytes for migration planning.
    /// </summary>
    /// <returns>Estimated data size as totalBlocks * blockSize, or 0 if the superblock has not been read.</returns>
    public long EstimateDataSizeBytes()
    {
        if (!_cachedSummary.HasValue)
            return 0;

        return _cachedSummary.Value.TotalBlocks * _cachedSummary.Value.BlockSize;
    }
}
