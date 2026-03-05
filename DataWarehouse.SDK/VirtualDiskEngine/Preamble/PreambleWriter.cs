using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Writes <see cref="PreambleHeader"/> instances to streams or byte spans,
/// computing the header checksum automatically during serialization.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WriteHeader"/> serializes the header, computes the checksum over bytes 0-55,
/// and writes the checksum into bytes 56-63 before returning. This ensures the on-disk
/// representation always has a valid integrity checksum.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-61 preamble header writer")]
public static class PreambleWriter
{
    /// <summary>Number of header bytes covered by the checksum (bytes 0-55).</summary>
    private const int ChecksumCoveredBytes = 56;

    /// <summary>Default block size alignment for VdeOffset (4 KiB).</summary>
    private const int DefaultBlockAlignment = FormatConstants.DefaultBlockSize;

    /// <summary>
    /// Serializes a <see cref="PreambleHeader"/> into a byte buffer, computing and writing
    /// the header checksum (bytes 56-63) over bytes 0-55.
    /// </summary>
    /// <param name="header">The header to serialize. The <see cref="PreambleHeader.HeaderChecksum"/> field is ignored;
    /// the correct checksum is computed and written automatically.</param>
    /// <param name="buffer">Destination buffer, must be at least 64 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is too small.</exception>
    public static void WriteHeader(in PreambleHeader header, Span<byte> buffer)
    {
        if (buffer.Length < PreambleHeader.SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {PreambleHeader.SerializedSize} bytes.", nameof(buffer));

        // First serialize with a zeroed checksum to get bytes 0-55
        var tempHeader = new PreambleHeader(
            header.Magic,
            header.PreambleVersion,
            header.Flags,
            header.ContentHashOffset,
            header.PreambleTotalSize,
            header.VdeOffset,
            header.KernelOffset,
            header.KernelSize,
            header.SpdkOffset,
            header.SpdkSize,
            header.RuntimeOffset,
            header.RuntimeSize,
            headerChecksum: 0);

        PreambleHeader.Serialize(tempHeader, buffer);

        // Compute checksum over bytes 0-55
        ulong checksum = PreambleReader.ComputeHeaderChecksum(buffer.Slice(0, ChecksumCoveredBytes));

        // Write the checksum into bytes 56-63
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(56), checksum);
    }

    /// <summary>
    /// Writes a <see cref="PreambleHeader"/> to a stream at position 0.
    /// Computes the header checksum automatically.
    /// </summary>
    /// <param name="stream">A writable stream.</param>
    /// <param name="header">The header to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
    public static async Task WriteHeaderAsync(Stream stream, PreambleHeader header, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("Stream must be writable.", nameof(stream));

        if (stream.CanSeek)
            stream.Position = 0;

        var buffer = new byte[PreambleHeader.SerializedSize];
        WriteHeader(header, buffer);
        await stream.WriteAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new <see cref="PreambleHeader"/> with validated field values.
    /// The returned header has all fields populated except <see cref="PreambleHeader.HeaderChecksum"/>,
    /// which is computed during <see cref="WriteHeader"/>.
    /// </summary>
    /// <param name="vdeOffset">Byte offset of Block 0. Must be 4 KiB-aligned.</param>
    /// <param name="kernelOffset">Byte offset of the micro-kernel payload within the preamble.</param>
    /// <param name="kernelSize">Size of the micro-kernel payload in bytes.</param>
    /// <param name="spdkOffset">Byte offset of the SPDK driver pack within the preamble.</param>
    /// <param name="spdkSize">Size of the SPDK driver pack in bytes.</param>
    /// <param name="runtimeOffset">Byte offset of the lightweight runtime within the preamble.</param>
    /// <param name="runtimeSize">Size of the lightweight runtime in bytes.</param>
    /// <param name="flags">Preamble feature flags.</param>
    /// <param name="arch">Target CPU architecture.</param>
    /// <returns>A new preamble header with computed fields.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="vdeOffset"/> is not 4 KiB-aligned, or is less than the computed preamble total size.
    /// </exception>
    public static PreambleHeader CreateHeader(
        ulong vdeOffset,
        uint kernelOffset,
        uint kernelSize,
        uint spdkOffset,
        uint spdkSize,
        uint runtimeOffset,
        uint runtimeSize,
        PreambleFlags flags = PreambleFlags.None,
        TargetArchitecture arch = TargetArchitecture.X86_64)
    {
        // Validate VdeOffset alignment (4 KiB)
        if (vdeOffset % (ulong)DefaultBlockAlignment != 0)
            throw new ArgumentException(
                $"VdeOffset must be aligned to {DefaultBlockAlignment} bytes (4 KiB). Got 0x{vdeOffset:X}.",
                nameof(vdeOffset));

        // Compute PreambleTotalSize as the maximum extent of any payload section
        ulong preambleTotalSize = Math.Max(
            Math.Max(
                (ulong)kernelOffset + kernelSize,
                (ulong)spdkOffset + spdkSize),
            (ulong)runtimeOffset + runtimeSize);

        // Validate VdeOffset >= PreambleTotalSize
        if (vdeOffset < preambleTotalSize)
            throw new ArgumentException(
                $"VdeOffset (0x{vdeOffset:X}) must be >= PreambleTotalSize (0x{preambleTotalSize:X}). " +
                "The preamble region must fit entirely before Block 0.",
                nameof(vdeOffset));

        // Content hash immediately follows the last runtime byte
        uint contentHashOffset = runtimeOffset + runtimeSize;

        // Combine flags and architecture into the Flags field
        ushort combinedFlags = (ushort)((ushort)flags | ((ushort)arch << 3));

        return new PreambleHeader(
            magic: PreambleHeader.ExpectedMagic,
            preambleVersion: 1,
            flags: combinedFlags,
            contentHashOffset: contentHashOffset,
            preambleTotalSize: preambleTotalSize,
            vdeOffset: vdeOffset,
            kernelOffset: kernelOffset,
            kernelSize: kernelSize,
            spdkOffset: spdkOffset,
            spdkSize: spdkSize,
            runtimeOffset: runtimeOffset,
            runtimeSize: runtimeSize,
            headerChecksum: 0); // Filled in by WriteHeader
    }
}
