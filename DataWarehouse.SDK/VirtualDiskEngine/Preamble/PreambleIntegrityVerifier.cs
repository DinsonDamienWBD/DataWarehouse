using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Status codes for preamble integrity verification.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-67 preamble integrity verification")]
public enum PreambleVerificationStatus : byte
{
    /// <summary>Both header checksum and content hash are valid.</summary>
    Valid = 0,

    /// <summary>The magic bytes do not match "DWVD-BOO" — the stream does not contain a preamble.</summary>
    NotAPreamble = 1,

    /// <summary>The 8-byte header checksum (bytes 56-63) does not match BLAKE3(bytes[0..55]).</summary>
    HeaderCorrupted = 2,

    /// <summary>The 32-byte content hash does not match the computed hash over all payloads.</summary>
    ContentCorrupted = 3,

    /// <summary>The preamble version is higher than supported (future format).</summary>
    UnsupportedVersion = 4,

    /// <summary>An I/O error prevented verification from completing.</summary>
    IoError = 5,
}

/// <summary>
/// Result of preamble integrity verification, containing the status code,
/// the parsed header (when available), and diagnostic hash values on failure.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-67 preamble verification result")]
public readonly struct PreambleVerificationResult
{
    /// <summary>The verification outcome.</summary>
    public PreambleVerificationStatus Status { get; init; }

    /// <summary>Human-readable description of the failure (null when valid).</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>
    /// The parsed preamble header, or <c>null</c> when the header could not be parsed
    /// (e.g., <see cref="PreambleVerificationStatus.NotAPreamble"/>).
    /// </summary>
    public PreambleHeader? Header { get; init; }

    /// <summary>The stored content hash from the preamble (for diagnostics on content mismatch).</summary>
    public byte[]? ExpectedContentHash { get; init; }

    /// <summary>The computed content hash over all payloads (for diagnostics on content mismatch).</summary>
    public byte[]? ActualContentHash { get; init; }

    /// <summary>Returns <c>true</c> when the preamble passed all integrity checks.</summary>
    public bool IsValid => Status == PreambleVerificationStatus.Valid;
}

/// <summary>
/// Verifies the integrity of a DWVD bootable preamble region (VOPT-67).
/// Provides both a fast header-only check and a thorough full-content verification
/// that covers the 8-byte header checksum and the 32-byte content hash over all payloads.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VerifyAsync"/> performs full verification: header magic, header checksum,
/// version check, and BLAKE3 content hash over kernel + SPDK + runtime payloads.
/// </para>
/// <para>
/// <see cref="QuickVerifyHeaderOnlyAsync"/> is a fast path that only checks the header
/// checksum. Use it during VDE open; full content verification is expensive and only
/// needed before boot or via an explicit <c>dw preamble verify</c> command.
/// </para>
/// <para>
/// Uses SHA-256 as a BLAKE3 fallback until BLAKE3 ships in the .NET BCL.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-67 preamble integrity verifier")]
public sealed class PreambleIntegrityVerifier
{
    /// <summary>Number of header bytes covered by the checksum (bytes 0-55).</summary>
    private const int ChecksumCoveredBytes = 56;

    /// <summary>Size of the content hash in bytes (SHA-256 / BLAKE3).</summary>
    private const int ContentHashSize = 32;

    /// <summary>Maximum supported preamble version.</summary>
    private const ushort MaxSupportedVersion = 1;

    /// <summary>Buffer size for incremental content hashing.</summary>
    private const int HashBufferSize = 8192;

    /// <summary>
    /// Performs full preamble integrity verification: header magic, header checksum,
    /// version check, and content hash over all payload sections.
    /// </summary>
    /// <param name="stream">
    /// A readable, seekable stream positioned at or before the preamble header.
    /// The stream must contain the full preamble region.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PreambleVerificationResult"/> describing the verification outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public async Task<PreambleVerificationResult> VerifyAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            // Step 1: Read 64-byte header from position 0
            if (stream.CanSeek)
                stream.Position = 0;

            var headerBuffer = new byte[PreambleHeader.SerializedSize];
            int totalRead = 0;
            while (totalRead < PreambleHeader.SerializedSize)
            {
                int read = await stream.ReadAsync(
                    headerBuffer.AsMemory(totalRead, PreambleHeader.SerializedSize - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return new PreambleVerificationResult
                    {
                        Status = PreambleVerificationStatus.NotAPreamble,
                        ErrorDetail = $"Stream ended after {totalRead} bytes; need {PreambleHeader.SerializedSize} bytes for preamble header.",
                    };
                }

                totalRead += read;
            }

            // Step 2: Validate magic bytes == "DWVD-BOO"
            var header = PreambleHeader.Deserialize(headerBuffer);
            if (!header.ValidateMagic())
            {
                return new PreambleVerificationResult
                {
                    Status = PreambleVerificationStatus.NotAPreamble,
                    ErrorDetail = "Magic signature does not match 'DWVD-BOO'. This stream does not contain a DWVD bootable preamble.",
                };
            }

            // Step 3: Compute header checksum and compare to stored value
            ulong computedChecksum = PreambleReader.ComputeHeaderChecksum(
                headerBuffer.AsSpan(0, ChecksumCoveredBytes));
            if (header.HeaderChecksum != computedChecksum)
            {
                return new PreambleVerificationResult
                {
                    Status = PreambleVerificationStatus.HeaderCorrupted,
                    ErrorDetail = $"Header checksum mismatch. Expected 0x{computedChecksum:X16}, stored 0x{header.HeaderChecksum:X16}. The header may have been partially overwritten.",
                    Header = header,
                };
            }

            // Step 4: Check preamble version
            if (header.PreambleVersion > MaxSupportedVersion)
            {
                return new PreambleVerificationResult
                {
                    Status = PreambleVerificationStatus.UnsupportedVersion,
                    ErrorDetail = $"Preamble version {header.PreambleVersion} is not supported. Maximum supported version is {MaxSupportedVersion}.",
                    Header = header,
                };
            }

            // Step 5: Read the stored 32-byte content hash
            if (!stream.CanSeek)
            {
                return new PreambleVerificationResult
                {
                    Status = PreambleVerificationStatus.IoError,
                    ErrorDetail = "Stream is not seekable; cannot verify content hash.",
                    Header = header,
                };
            }

            stream.Position = header.ContentHashOffset;
            var storedHash = new byte[ContentHashSize];
            int hashRead = 0;
            while (hashRead < ContentHashSize)
            {
                int read = await stream.ReadAsync(
                    storedHash.AsMemory(hashRead, ContentHashSize - hashRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return new PreambleVerificationResult
                    {
                        Status = PreambleVerificationStatus.ContentCorrupted,
                        ErrorDetail = $"Could not read stored content hash at offset {header.ContentHashOffset}. Stream ended after {hashRead} bytes.",
                        Header = header,
                    };
                }

                hashRead += read;
            }

            // Step 6: Compute hash over kernel + SPDK + runtime payloads incrementally
            // TODO: Replace SHA-256 with BLAKE3 when it ships in the .NET BCL.
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await HashPayloadAsync(stream, incrementalHash, header.KernelOffset, header.KernelSize, ct)
                .ConfigureAwait(false);
            await HashPayloadAsync(stream, incrementalHash, header.SpdkOffset, header.SpdkSize, ct)
                .ConfigureAwait(false);
            await HashPayloadAsync(stream, incrementalHash, header.RuntimeOffset, header.RuntimeSize, ct)
                .ConfigureAwait(false);

            var computedHash = incrementalHash.GetHashAndReset();

            // Step 7: Compare computed hash to stored hash
            if (!computedHash.AsSpan().SequenceEqual(storedHash))
            {
                return new PreambleVerificationResult
                {
                    Status = PreambleVerificationStatus.ContentCorrupted,
                    ErrorDetail = "Content hash mismatch. The payload data (kernel, SPDK, or runtime) has been modified or corrupted since the preamble was written.",
                    Header = header,
                    ExpectedContentHash = storedHash,
                    ActualContentHash = computedHash,
                };
            }

            // Step 8: All checks passed
            return new PreambleVerificationResult
            {
                Status = PreambleVerificationStatus.Valid,
                Header = header,
                ExpectedContentHash = storedHash,
                ActualContentHash = computedHash,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreambleVerificationResult
            {
                Status = PreambleVerificationStatus.IoError,
                ErrorDetail = $"I/O error during preamble verification: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Fast-path header-only verification: checks the magic and header checksum only,
    /// without reading or hashing payload content. Use during VDE open where full content
    /// verification is too expensive.
    /// </summary>
    /// <param name="stream">A readable stream positioned at or before the preamble header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the header magic and checksum are valid; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public async Task<bool> QuickVerifyHeaderOnlyAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            if (stream.CanSeek)
                stream.Position = 0;

            var headerBuffer = new byte[PreambleHeader.SerializedSize];
            int totalRead = 0;
            while (totalRead < PreambleHeader.SerializedSize)
            {
                int read = await stream.ReadAsync(
                    headerBuffer.AsMemory(totalRead, PreambleHeader.SerializedSize - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    return false;
                totalRead += read;
            }

            var header = PreambleHeader.Deserialize(headerBuffer);

            // Check magic
            if (!header.ValidateMagic())
                return false;

            // Check header checksum
            ulong computedChecksum = PreambleReader.ComputeHeaderChecksum(
                headerBuffer.AsSpan(0, ChecksumCoveredBytes));

            return header.HeaderChecksum == computedChecksum;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a payload section from the stream and feeds it into the incremental hash.
    /// </summary>
    private static async Task HashPayloadAsync(
        Stream stream,
        IncrementalHash hash,
        uint offset,
        uint size,
        CancellationToken ct)
    {
        if (size == 0)
            return;

        stream.Position = offset;
        var buffer = new byte[HashBufferSize];
        uint remaining = size;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, (uint)HashBufferSize);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            remaining -= (uint)read;
        }
    }
}
