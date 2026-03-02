using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Recovery;

/// <summary>
/// Recovery Footer: the very last block of a VDE file, at offset (fileSize - blockSize).
/// Mirrors Superblock Block 0 (the primary superblock) to provide spatial redundancy.
/// When Superblock Block 0 is corrupted or overwritten, the Recovery Footer enables
/// VDE identification and reconstruction without needing the first 32 KB.
///
/// Update Protocol:
///   - On clean unmount: footer is written to match current Superblock Block 0.
///   - On epoch boundary: footer is updated asynchronously.
///   - On VDE grow: write new footer at new EOF, mark old footer block RESERVED,
///     update Superblock with new ExpectedFileSize, then free old block.
///   - On VDE shrink: footer moves first (written to new EOF), then free space released.
///   - On normal write: footer is NOT updated (use <see cref="ShouldUpdate"/> to check).
///
/// The footer carries the same SUPB block type tag as the primary superblock so that
/// recovery tools recognize it as a superblock copy without needing additional tags.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-77: Recovery Footer at EOF (Superblock Block 0 mirror)")]
public sealed class RecoveryFooter
{
    // ── Layout Constants ─────────────────────────────────────────────────

    /// <summary>Size of the footer in blocks (always 1).</summary>
    public const int FooterSizeBlocks = 1;

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>Raw bytes of Superblock Block 0, up to blockSize.</summary>
    public byte[] SuperblockMirror { get; set; } = Array.Empty<byte>();

    /// <summary>Generation number incremented on each footer update.</summary>
    public uint Generation { get; set; }

    // ── Offset Calculation ───────────────────────────────────────────────

    /// <summary>
    /// Returns the byte offset of the Recovery Footer within the VDE file.
    /// The footer is always the last block: <c>fileSizeBytes - blockSize</c>.
    /// </summary>
    /// <param name="fileSizeBytes">Total VDE file size in bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Byte offset of the footer block.</returns>
    public static long GetFooterOffset(long fileSizeBytes, int blockSize)
    {
        if (fileSizeBytes < blockSize)
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes),
                "File size must be at least one block.");
        return fileSizeBytes - blockSize;
    }

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the Recovery Footer into <paramref name="buffer"/>.
    /// Copies <see cref="SuperblockMirror"/> into the buffer and writes a SUPB
    /// universal block trailer so recovery tools identify it as a superblock copy.
    /// </summary>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        buffer.Slice(0, blockSize).Clear();

        var mirror = SuperblockMirror ?? Array.Empty<byte>();
        var copyLen = Math.Min(mirror.Length, blockSize - UniversalBlockTrailer.Size);
        mirror.AsSpan(0, copyLen).CopyTo(buffer);

        // Write SUPB trailer (same tag as primary superblock) so recovery tools identify it
        UniversalBlockTrailer.Write(buffer, blockSize, BlockTypeTags.SUPB, Generation);
    }

    /// <summary>
    /// Deserializes a Recovery Footer from <paramref name="buffer"/>.
    /// Validates the SUPB universal block trailer.
    /// </summary>
    /// <exception cref="InvalidDataException">Trailer verification fails.</exception>
    public static RecoveryFooter Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        if (!UniversalBlockTrailer.Verify(buffer, blockSize, out var trailer))
            throw new InvalidDataException("Recovery Footer trailer checksum verification failed.");

        if (trailer.BlockTypeTag != BlockTypeTags.SUPB)
            throw new InvalidDataException(
                $"Recovery Footer tag mismatch: expected SUPB (0x{BlockTypeTags.SUPB:X8}), " +
                $"got 0x{trailer.BlockTypeTag:X8}.");

        // Copy payload (bytes before trailer) as the superblock mirror
        int payloadSize = blockSize - UniversalBlockTrailer.Size;
        var mirror = new byte[payloadSize];
        buffer.Slice(0, payloadSize).CopyTo(mirror);

        return new RecoveryFooter
        {
            SuperblockMirror = mirror,
            Generation = trailer.GenerationNumber,
        };
    }

    // ── Update Decision ──────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the footer should be updated for the given reason.
    /// The footer is only written on clean unmount, epoch boundaries, and volume resize
    /// operations — NOT on every normal write.
    /// </summary>
    public static bool ShouldUpdate(RecoveryFooterUpdateReason reason) =>
        reason switch
        {
            RecoveryFooterUpdateReason.CleanUnmount   => true,
            RecoveryFooterUpdateReason.EpochBoundary  => true,
            RecoveryFooterUpdateReason.VdeGrow        => true,
            RecoveryFooterUpdateReason.VdeShrink      => true,
            RecoveryFooterUpdateReason.NormalWrite     => false,
            _ => false,
        };
}

/// <summary>
/// Reasons that may trigger a Recovery Footer update.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-77: Recovery Footer update triggers")]
public enum RecoveryFooterUpdateReason
{
    /// <summary>
    /// Volume is being unmounted cleanly. Footer is updated to match the final
    /// state of Superblock Block 0.
    /// </summary>
    CleanUnmount,

    /// <summary>
    /// An epoch boundary has been crossed (e.g. replication epoch increment).
    /// Footer is updated asynchronously.
    /// </summary>
    EpochBoundary,

    /// <summary>
    /// Volume size has grown. New footer is written at the new EOF before freeing
    /// the old footer block.
    ///
    /// Grow protocol:
    ///   1. Write new footer block at (newFileSize - blockSize).
    ///   2. Mark old footer block as RESERVED in the block allocation bitmap.
    ///   3. Update Superblock with new TotalBlocks and ExpectedFileSize.
    ///   4. Free old footer block.
    /// </summary>
    VdeGrow,

    /// <summary>
    /// Volume size has shrunk. Footer must move to the new EOF before releasing space.
    ///
    /// Shrink protocol:
    ///   1. Write new footer at (newFileSize - blockSize).
    ///   2. Release freed space from allocation bitmap.
    /// </summary>
    VdeShrink,

    /// <summary>
    /// A normal data or metadata write. Footer is NOT updated.
    /// </summary>
    NormalWrite,
}
