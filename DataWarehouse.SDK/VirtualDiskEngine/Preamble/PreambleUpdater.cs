using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Options controlling which preamble payloads to replace during an update.
/// Any <c>null</c> payload property means "keep the existing payload unchanged."
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-67 preamble update options")]
public sealed class PreambleUpdateOptions
{
    /// <summary>New micro-kernel image bytes, or <c>null</c> to keep the existing kernel.</summary>
    public byte[]? NewKernelImage { get; init; }

    /// <summary>New SPDK driver pack bytes, or <c>null</c> to keep the existing SPDK bundle.</summary>
    public byte[]? NewSpdkBundle { get; init; }

    /// <summary>New lightweight runtime binary bytes, or <c>null</c> to keep the existing runtime.</summary>
    public byte[]? NewRuntimeBinary { get; init; }

    /// <summary>
    /// When <c>true</c>, proceed with the update even if the existing preamble header is corrupt.
    /// Used to recover from a previous interrupted update.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>
    /// Optional composition profile override. When provided, the profile's flags and architecture
    /// are used in the updated header. When <c>null</c>, the existing header flags are preserved.
    /// </summary>
    public PreambleCompositionProfile? Profile { get; init; }

    /// <summary>
    /// When <c>true</c> (default), a full integrity verification is performed after the update
    /// completes, and the result is included in <see cref="PreambleUpdateResult.PostUpdateVerification"/>.
    /// </summary>
    public bool ValidateAfterUpdate { get; init; } = true;
}

/// <summary>
/// Result of a preamble update operation, including before/after sizes
/// and an optional post-update verification result.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-67 preamble update result")]
public readonly struct PreambleUpdateResult
{
    /// <summary>Whether the update completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is <c>false</c>; otherwise <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Total preamble size in bytes before the update.</summary>
    public ulong PreviousPreambleSize { get; init; }

    /// <summary>Total preamble size in bytes after the update.</summary>
    public ulong NewPreambleSize { get; init; }

    /// <summary>
    /// Post-update verification result when <see cref="PreambleUpdateOptions.ValidateAfterUpdate"/>
    /// was <c>true</c>; otherwise <c>null</c>.
    /// </summary>
    public PreambleVerificationResult? PostUpdateVerification { get; init; }
}

/// <summary>
/// Updates preamble payloads (kernel, SPDK, runtime) in-place without touching VDE data blocks (VOPT-67).
/// Crash-safe: payloads and content hash are written first, the header is written last.
/// If interrupted before header write, the old header remains intact and VDE opens normally.
/// If interrupted during header write, the header checksum will fail and VDE falls back to hosted mode.
/// </summary>
/// <remarks>
/// <para>The updater rejects any update that would increase the VdeOffset (i.e., require moving
/// all VDE data blocks). Only updates where the new preamble fits within the existing
/// VdeOffset boundary are allowed.</para>
/// <para>
/// CLI integration: The <see cref="UpdateFromFilesAsync"/> method reads payload files from disk
/// and delegates to <see cref="UpdateAsync"/>. This is the method called by <c>dw preamble update</c>.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-67 preamble updater")]
public sealed class PreambleUpdater
{
    /// <summary>Number of header bytes covered by the checksum (bytes 0-55).</summary>
    private const int ChecksumCoveredBytes = 56;

    /// <summary>Size of the content hash in bytes (SHA-256 / BLAKE3).</summary>
    private const int ContentHashSize = 32;

    /// <summary>4 KiB alignment for VDE Block 0.</summary>
    private const ulong AlignmentBytes = 4096;

    private readonly PreambleCompositionEngine _compositionEngine;
    private readonly PreambleIntegrityVerifier _verifier;

    /// <summary>
    /// Creates a new <see cref="PreambleUpdater"/> with default dependencies.
    /// </summary>
    public PreambleUpdater()
    {
        _compositionEngine = new PreambleCompositionEngine();
        _verifier = new PreambleIntegrityVerifier();
    }

    /// <summary>
    /// Creates a new <see cref="PreambleUpdater"/> with specified dependencies.
    /// </summary>
    /// <param name="compositionEngine">The composition engine for layout computation.</param>
    /// <param name="verifier">The integrity verifier for pre/post-update checks.</param>
    public PreambleUpdater(PreambleCompositionEngine compositionEngine, PreambleIntegrityVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(compositionEngine);
        ArgumentNullException.ThrowIfNull(verifier);
        _compositionEngine = compositionEngine;
        _verifier = verifier;
    }

    /// <summary>
    /// Updates preamble payloads in-place on a VDE stream. Payloads and content hash are written
    /// before the header (crash-safe ordering). Rejects updates that would require moving VDE blocks.
    /// </summary>
    /// <param name="vdeStream">
    /// A readable, writable, seekable stream containing the full DWVD file. The stream must
    /// be positioned at or before the preamble header (position 0).
    /// </param>
    /// <param name="options">Update options specifying which payloads to replace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PreambleUpdateResult"/> describing the outcome.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="vdeStream"/> or <paramref name="options"/> is <c>null</c>.
    /// </exception>
    public async Task<PreambleUpdateResult> UpdateAsync(
        Stream vdeStream,
        PreambleUpdateOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vdeStream);
        ArgumentNullException.ThrowIfNull(options);

        if (!vdeStream.CanRead || !vdeStream.CanWrite || !vdeStream.CanSeek)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = "Stream must be readable, writable, and seekable for preamble update.",
            };
        }

        // Step 1: Read existing preamble header
        PreambleHeader existingHeader;
        try
        {
            vdeStream.Position = 0;
            var headerBuffer = new byte[PreambleHeader.SerializedSize];
            await ReadExactlyAsync(vdeStream, headerBuffer, ct).ConfigureAwait(false);
            existingHeader = PreambleHeader.Deserialize(headerBuffer);
        }
        catch (Exception ex)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = $"Failed to read existing preamble header: {ex.Message}",
            };
        }

        // Step 2: Verify header integrity (quick verify) unless Force
        if (!options.Force)
        {
            vdeStream.Position = 0;
            bool headerValid = await _verifier.QuickVerifyHeaderOnlyAsync(vdeStream, ct)
                .ConfigureAwait(false);
            if (!headerValid)
            {
                return new PreambleUpdateResult
                {
                    Success = false,
                    ErrorMessage = "Existing preamble header is corrupt. Use Force=true to override and attempt recovery.",
                    PreviousPreambleSize = existingHeader.PreambleTotalSize,
                };
            }
        }

        ulong previousPreambleSize = existingHeader.PreambleTotalSize;
        ulong existingVdeOffset = existingHeader.VdeOffset;

        // Step 3: Determine what's changing — read existing payloads for anything not being replaced
        byte[] kernelData;
        byte[] spdkData;
        byte[] runtimeData;

        try
        {
            kernelData = options.NewKernelImage
                ?? await ReadPayloadAsync(vdeStream, existingHeader.KernelOffset, existingHeader.KernelSize, ct)
                    .ConfigureAwait(false);

            spdkData = options.NewSpdkBundle
                ?? await ReadPayloadAsync(vdeStream, existingHeader.SpdkOffset, existingHeader.SpdkSize, ct)
                    .ConfigureAwait(false);

            runtimeData = options.NewRuntimeBinary
                ?? await ReadPayloadAsync(vdeStream, existingHeader.RuntimeOffset, existingHeader.RuntimeSize, ct)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = $"Failed to read existing payload for unchanged section: {ex.Message}",
                PreviousPreambleSize = previousPreambleSize,
            };
        }

        // Step 4: Compute new layout
        uint newKernelSize = (uint)kernelData.Length;
        uint newSpdkSize = (uint)spdkData.Length;
        uint newRuntimeSize = (uint)runtimeData.Length;

        // Compute offsets: kernel starts at byte 64 (after header), SPDK follows kernel, runtime follows SPDK
        uint kernelOffset = (uint)PreambleHeader.SerializedSize;
        uint spdkOffset = newSpdkSize > 0 ? kernelOffset + newKernelSize : kernelOffset;
        uint runtimeOffset = spdkOffset + newSpdkSize;
        uint contentHashOffset = runtimeOffset + newRuntimeSize;

        ulong newPreambleTotalSize = (ulong)contentHashOffset + ContentHashSize;

        // Compute aligned VDE offset
        ulong newVdeOffset = AlignUp(newPreambleTotalSize, AlignmentBytes);

        // Step 4b: Reject if new VdeOffset > existing VdeOffset (would require moving all VDE blocks)
        if (newVdeOffset > existingVdeOffset)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = $"Updated preamble requires VdeOffset 0x{newVdeOffset:X} but existing VdeOffset is 0x{existingVdeOffset:X}. " +
                    "The new payloads are too large to fit in the existing preamble space. " +
                    "Reducing payload sizes or recreating the VDE file with a larger preamble allocation is required.",
                PreviousPreambleSize = previousPreambleSize,
                NewPreambleSize = newPreambleTotalSize,
            };
        }

        // Step 5: Write new payloads at computed offsets (BEFORE header — crash-safe ordering)
        try
        {
            vdeStream.Position = kernelOffset;
            await vdeStream.WriteAsync(kernelData.AsMemory(), ct).ConfigureAwait(false);

            if (newSpdkSize > 0)
            {
                vdeStream.Position = spdkOffset;
                await vdeStream.WriteAsync(spdkData.AsMemory(), ct).ConfigureAwait(false);
            }

            vdeStream.Position = runtimeOffset;
            await vdeStream.WriteAsync(runtimeData.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = $"Failed to write payload data: {ex.Message}",
                PreviousPreambleSize = previousPreambleSize,
            };
        }

        // Step 6: Compute new content hash over all payloads and write at ContentHashOffset
        // TODO: Replace SHA-256 with BLAKE3 when it ships in the .NET BCL.
        byte[] contentHash;
        using (var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            incrementalHash.AppendData(kernelData);
            if (newSpdkSize > 0)
                incrementalHash.AppendData(spdkData);
            incrementalHash.AppendData(runtimeData);
            contentHash = incrementalHash.GetHashAndReset();
        }

        try
        {
            vdeStream.Position = contentHashOffset;
            await vdeStream.WriteAsync(contentHash.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = $"Failed to write content hash: {ex.Message}",
                PreviousPreambleSize = previousPreambleSize,
            };
        }

        // Step 4d: Zero-fill gap between new preamble end and VDE start if new preamble is smaller
        if (newVdeOffset < existingVdeOffset)
        {
            // Zero the gap between content hash end and the existing VDE start
            ulong gapStart = (ulong)contentHashOffset + ContentHashSize;
            ulong gapLength = existingVdeOffset - gapStart;
            if (gapLength > 0 && gapLength <= existingVdeOffset)
            {
                try
                {
                    vdeStream.Position = (long)gapStart;
                    var zeros = new byte[Math.Min(gapLength, 8192UL)];
                    ulong remaining = gapLength;
                    while (remaining > 0)
                    {
                        int toWrite = (int)Math.Min(remaining, (ulong)zeros.Length);
                        await vdeStream.WriteAsync(zeros.AsMemory(0, toWrite), ct).ConfigureAwait(false);
                        remaining -= (ulong)toWrite;
                    }
                }
                catch (Exception ex)
                {
                    return new PreambleUpdateResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to zero-fill gap between preamble and VDE data: {ex.Message}",
                        PreviousPreambleSize = previousPreambleSize,
                    };
                }
            }
        }

        // Step 7: Build new PreambleHeader with updated sizes/offsets
        ushort newFlags = options.Profile is not null
            ? PreambleCompositionEngine.ComputeFlags(options.Profile)
            : existingHeader.Flags;

        var newHeader = new PreambleHeader(
            magic: PreambleHeader.ExpectedMagic,
            preambleVersion: existingHeader.PreambleVersion,
            flags: newFlags,
            contentHashOffset: contentHashOffset,
            preambleTotalSize: newPreambleTotalSize,
            vdeOffset: existingVdeOffset, // Preserve existing VDE offset (never move VDE blocks)
            kernelOffset: kernelOffset,
            kernelSize: newKernelSize,
            spdkOffset: spdkOffset,
            spdkSize: newSpdkSize,
            runtimeOffset: runtimeOffset,
            runtimeSize: newRuntimeSize,
            headerChecksum: 0); // PreambleWriter computes this

        // Step 8: Flush stream before writing header (ensures all payloads are persisted)
        await vdeStream.FlushAsync(ct).ConfigureAwait(false);

        // Step 9: Write header LAST (crash-safe: old header remains if crash occurs before this)
        try
        {
            await PreambleWriter.WriteHeaderAsync(vdeStream, newHeader, ct).ConfigureAwait(false);
            await vdeStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PreambleUpdateResult
            {
                Success = false,
                ErrorMessage = $"Failed to write updated header. The preamble may be in an inconsistent state; use Force=true to recover. Error: {ex.Message}",
                PreviousPreambleSize = previousPreambleSize,
                NewPreambleSize = newPreambleTotalSize,
            };
        }

        // Step 10: Optional post-update verification
        PreambleVerificationResult? postVerification = null;
        if (options.ValidateAfterUpdate)
        {
            postVerification = await _verifier.VerifyAsync(vdeStream, ct).ConfigureAwait(false);
        }

        return new PreambleUpdateResult
        {
            Success = true,
            PreviousPreambleSize = previousPreambleSize,
            NewPreambleSize = newPreambleTotalSize,
            PostUpdateVerification = postVerification,
        };
    }

    /// <summary>
    /// Convenience method that reads payload files from disk and delegates to <see cref="UpdateAsync"/>.
    /// This is the method the CLI <c>dw preamble update</c> command calls.
    /// </summary>
    /// <param name="vdeStream">A readable, writable, seekable stream containing the full DWVD file.</param>
    /// <param name="kernelPath">Path to the new kernel image file, or <c>null</c> to keep existing.</param>
    /// <param name="spdkPath">Path to the new SPDK bundle file, or <c>null</c> to keep existing.</param>
    /// <param name="runtimePath">Path to the new runtime binary file, or <c>null</c> to keep existing.</param>
    /// <param name="force">When <c>true</c>, proceed even if the existing preamble is corrupt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PreambleUpdateResult"/> describing the outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vdeStream"/> is <c>null</c>.</exception>
    /// <exception cref="FileNotFoundException">A specified payload file does not exist.</exception>
    public async Task<PreambleUpdateResult> UpdateFromFilesAsync(
        Stream vdeStream,
        string? kernelPath,
        string? spdkPath,
        string? runtimePath,
        bool force,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vdeStream);

        byte[]? kernelData = null;
        byte[]? spdkData = null;
        byte[]? runtimeData = null;

        if (kernelPath is not null)
        {
            if (!File.Exists(kernelPath))
                throw new FileNotFoundException($"Kernel image file not found: {kernelPath}", kernelPath);
            kernelData = await File.ReadAllBytesAsync(kernelPath, ct).ConfigureAwait(false);
        }

        if (spdkPath is not null)
        {
            if (!File.Exists(spdkPath))
                throw new FileNotFoundException($"SPDK bundle file not found: {spdkPath}", spdkPath);
            spdkData = await File.ReadAllBytesAsync(spdkPath, ct).ConfigureAwait(false);
        }

        if (runtimePath is not null)
        {
            if (!File.Exists(runtimePath))
                throw new FileNotFoundException($"Runtime binary file not found: {runtimePath}", runtimePath);
            runtimeData = await File.ReadAllBytesAsync(runtimePath, ct).ConfigureAwait(false);
        }

        var options = new PreambleUpdateOptions
        {
            NewKernelImage = kernelData,
            NewSpdkBundle = spdkData,
            NewRuntimeBinary = runtimeData,
            Force = force,
            ValidateAfterUpdate = true,
        };

        return await UpdateAsync(vdeStream, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// </summary>
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Stream ended after {totalRead} bytes; expected {buffer.Length} bytes.");
            totalRead += read;
        }
    }

    /// <summary>
    /// Reads a payload section from the stream.
    /// </summary>
    private static async Task<byte[]> ReadPayloadAsync(
        Stream stream, uint offset, uint size, CancellationToken ct)
    {
        if (size == 0)
            return Array.Empty<byte>();

        stream.Position = offset;
        var data = new byte[size];
        await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to the nearest multiple of <paramref name="alignment"/>.
    /// </summary>
    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }
}
