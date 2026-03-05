using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Reads and validates <see cref="PreambleHeader"/> instances from streams or byte spans.
/// Performs magic validation and header checksum verification to ensure structural integrity.
/// </summary>
/// <remarks>
/// <para>
/// The header checksum covers bytes 0-55 of the 64-byte header. The checksum is computed
/// using SHA-256 (truncated to 8 bytes) as a fallback until BLAKE3 is available in the .NET BCL.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-61 preamble header reader")]
public static class PreambleReader
{
    /// <summary>Number of header bytes covered by the checksum (bytes 0-55).</summary>
    private const int ChecksumCoveredBytes = 56;

    /// <summary>
    /// Reads and validates a <see cref="PreambleHeader"/> from a byte buffer.
    /// </summary>
    /// <param name="buffer">Buffer containing at least 64 bytes of preamble header data.</param>
    /// <returns>The validated preamble header.</returns>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is too small.</exception>
    /// <exception cref="InvalidOperationException">
    /// The magic signature is invalid or the header checksum does not match.
    /// </exception>
    public static PreambleHeader ReadHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < PreambleHeader.SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {PreambleHeader.SerializedSize} bytes.", nameof(buffer));

        var header = PreambleHeader.Deserialize(buffer);

        // Validate magic
        if (!header.ValidateMagic())
            throw new InvalidOperationException(
                "Invalid preamble magic signature. Expected 'DWVD-BOO'.");

        // Validate header checksum: SHA-256(bytes[0..55]) truncated to 8 bytes
        // TODO: Replace SHA-256 with BLAKE3 when it ships in the .NET BCL.
        ulong expectedChecksum = ComputeHeaderChecksum(buffer.Slice(0, ChecksumCoveredBytes));
        if (header.HeaderChecksum != expectedChecksum)
            throw new InvalidOperationException(
                $"Preamble header checksum mismatch. Expected 0x{expectedChecksum:X16}, " +
                $"got 0x{header.HeaderChecksum:X16}. The header may be corrupted.");

        return header;
    }

    /// <summary>
    /// Reads and validates a <see cref="PreambleHeader"/> from a stream.
    /// Reads 64 bytes starting from stream position 0.
    /// </summary>
    /// <param name="stream">A readable stream containing the preamble header at position 0.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validated preamble header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The magic signature is invalid, the header checksum does not match,
    /// or the stream does not contain enough data.
    /// </exception>
    public static async Task<PreambleHeader> ReadHeaderAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
            stream.Position = 0;

        var buffer = new byte[PreambleHeader.SerializedSize];
        int totalRead = 0;
        while (totalRead < PreambleHeader.SerializedSize)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, PreambleHeader.SerializedSize - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                throw new InvalidOperationException(
                    $"Stream ended after {totalRead} bytes; need {PreambleHeader.SerializedSize} bytes for preamble header.");
            totalRead += read;
        }

        return ReadHeader(buffer);
    }

    /// <summary>
    /// Validates the content integrity hash stored in the preamble region.
    /// Reads the kernel, SPDK, and runtime payload bytes and computes a hash,
    /// comparing it to the 32-byte hash stored at <see cref="PreambleHeader.ContentHashOffset"/>.
    /// </summary>
    /// <param name="stream">A readable, seekable stream containing the full DWVD file.</param>
    /// <param name="header">The validated preamble header.</param>
    /// <returns><c>true</c> if the computed content hash matches the stored hash; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not seekable.</exception>
    /// <remarks>
    /// Uses SHA-256 as a BLAKE3 fallback. The stored hash is 32 bytes at ContentHashOffset.
    /// TODO: Replace SHA-256 with BLAKE3 when it ships in the .NET BCL.
    /// </remarks>
    public static bool TryValidateContentHash(Stream stream, PreambleHeader header)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable for content hash validation.", nameof(stream));

        const int hashSize = 32; // SHA-256 / BLAKE3 output size

        // Read the stored content hash
        stream.Position = header.ContentHashOffset;
        Span<byte> storedHash = stackalloc byte[hashSize];
        int hashRead = 0;
        while (hashRead < hashSize)
        {
            int read = stream.Read(storedHash.Slice(hashRead));
            if (read == 0)
                return false; // Cannot read stored hash
            hashRead += read;
        }

        // Compute hash over all payload sections (kernel + SPDK + runtime)
        // TODO: Replace SHA-256 with BLAKE3 when it ships in the .NET BCL.
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        ReadPayloadIntoHash(stream, sha256, header.KernelOffset, header.KernelSize);
        ReadPayloadIntoHash(stream, sha256, header.SpdkOffset, header.SpdkSize);
        ReadPayloadIntoHash(stream, sha256, header.RuntimeOffset, header.RuntimeSize);

        Span<byte> computedHash = stackalloc byte[hashSize];
        if (!sha256.TryGetHashAndReset(computedHash, out int bytesWritten) || bytesWritten != hashSize)
            return false;

        return computedHash.SequenceEqual(storedHash);
    }

    /// <summary>
    /// Computes the header checksum over the first 56 bytes of the header.
    /// Uses SHA-256 truncated to 8 bytes as a BLAKE3 fallback.
    /// </summary>
    internal static ulong ComputeHeaderChecksum(ReadOnlySpan<byte> headerBytes)
    {
        // TODO: Replace SHA-256 with BLAKE3 when it ships in the .NET BCL.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(headerBytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    private static void ReadPayloadIntoHash(Stream stream, IncrementalHash hash, uint offset, uint size)
    {
        if (size == 0)
            return;

        stream.Position = offset;
        const int chunkSize = 8192;
        Span<byte> chunk = stackalloc byte[chunkSize];
        uint remaining = size;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, (uint)chunkSize);
            int read = stream.Read(chunk.Slice(0, toRead));
            if (read == 0)
                break;
            hash.AppendData(chunk.Slice(0, read));
            remaining -= (uint)read;
        }
    }
}
