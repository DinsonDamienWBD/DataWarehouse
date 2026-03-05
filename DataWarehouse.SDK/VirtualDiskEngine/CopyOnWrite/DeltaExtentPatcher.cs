using System.Buffers.Binary;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

// ── DeltaPatchResult ──────────────────────────────────────────────────────────

/// <summary>
/// The result returned by
/// <see cref="DeltaExtentPatcher.GeneratePatch(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>,
/// describing the binary patch and whether applying the delta path is beneficial
/// compared to storing a full copy-on-write block.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-23: VCDIFF patch generation result (VOPT-36)")]
public readonly struct DeltaPatchResult
{
    /// <summary>
    /// Raw patch bytes encoded in the internal VCDIFF-style format.
    /// Apply with <see cref="DeltaExtentPatcher.ApplyPatch"/>.
    /// </summary>
    public byte[] PatchData { get; init; }

    /// <summary>Total size of <see cref="PatchData"/> in bytes.</summary>
    public int PatchSize { get; init; }

    /// <summary>
    /// <see langword="true"/> when using a delta patch is more efficient than
    /// allocating a new full copy-on-write block (i.e., PatchSize &lt; blockSize * 0.5).
    /// </summary>
    public bool IsDeltaWorthwhile { get; init; }

    /// <summary>
    /// Ratio of original block size to patch size. Values greater than 1.0 indicate
    /// compression; values less than or equal to 1.0 indicate the patch is larger than
    /// or equal to the original block.
    /// </summary>
    public double CompressionRatio { get; init; }
}

// ── DeltaExtentPatcher ────────────────────────────────────────────────────────

/// <summary>
/// VCDIFF-style binary patch engine for sub-block delta extents (DELT, VOPT-36).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Purpose:</strong> When a write modifies less than a configurable threshold
/// fraction of a block (default 10%), the VDE generates a compact binary patch
/// instead of allocating a full 4 KB copy-on-write block. This near-eliminates write
/// amplification for small, random-update workloads.
/// </para>
/// <para>
/// <strong>Internal patch format (simplified VCDIFF):</strong>
/// <code>
///   Header  (14 bytes):
///     [Magic:4]       0x44454C54 ("DELT") — identifies format
///     [OrigSize:4]    little-endian int32  — original block size
///     [PatchedSize:4] little-endian int32  — expected output size
///     [NumOps:2]      little-endian uint16 — number of operations that follow
///
///   Operations (variable):
///     [OpType:1]  0 = Copy (from original), 1 = Insert (literal data)
///     [Offset:4]  little-endian int32  — byte offset in original (Copy) or 0 (Insert)
///     [Length:2]  little-endian uint16 — number of bytes
///     [Data:N]    present only for Insert operations (N = Length bytes)
/// </code>
/// </para>
/// <para>
/// The algorithm scans the modified block looking for runs that differ from the
/// original. Unchanged runs become <em>Copy</em> operations (zero stored bytes);
/// changed runs become <em>Insert</em> operations (literal data). This is semantically
/// equivalent to an RFC 3284 VCDIFF with no secondary compression.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-23: VCDIFF patch engine (VOPT-36, DELT module bit 22)")]
public sealed class DeltaExtentPatcher
{
    // ── Patch format constants ─────────────────────────────────────────────────

    /// <summary>4-byte magic number at the start of every patch header: ASCII "DELT".</summary>
    public const uint PatchHeaderMagic = 0x44454C54u;

    private const int HeaderSize = 14; // magic(4) + origSize(4) + patchedSize(4) + numOps(2)
    private const int OpHeaderSize = 7; // opType(1) + offset(4) + length(2)

    private const byte OpCopy   = 0;
    private const byte OpInsert = 1;

    // ── Instance state ─────────────────────────────────────────────────────────

    private readonly int _blockSize;

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="DeltaExtentPatcher"/> for blocks of the specified size.
    /// </summary>
    /// <param name="blockSize">
    /// The block size in bytes used by the VDE (e.g., 4096). Patch worthwhileness
    /// thresholds are calculated relative to this value.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="blockSize"/> is not positive.
    /// </exception>
    public DeltaExtentPatcher(int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        _blockSize = blockSize;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether it is worthwhile to store a delta patch rather than a full
    /// copy-on-write block, based on the fraction of bytes that differ.
    /// </summary>
    /// <param name="originalBlock">Original block bytes (before modification).</param>
    /// <param name="modifiedBlock">Modified block bytes (after modification).</param>
    /// <param name="threshold">
    /// Maximum fraction of changed bytes for which delta patching is considered
    /// beneficial. Default is 0.1 (10%). Must be in (0, 1].
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the fraction of differing bytes is strictly less
    /// than <paramref name="threshold"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Block spans have different lengths, or <paramref name="threshold"/> is out of range.
    /// </exception>
    public bool ShouldUseDelta(
        ReadOnlySpan<byte> originalBlock,
        ReadOnlySpan<byte> modifiedBlock,
        double threshold = 0.1)
    {
        if (originalBlock.Length != modifiedBlock.Length)
            throw new ArgumentException(
                "originalBlock and modifiedBlock must have the same length.");
        if (threshold <= 0.0 || threshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(threshold),
                "threshold must be in the range (0, 1].");

        if (originalBlock.IsEmpty)
            return false;

        int diffBytes = 0;
        for (int i = 0; i < originalBlock.Length; i++)
        {
            if (originalBlock[i] != modifiedBlock[i])
                diffBytes++;
        }

        return (double)diffBytes / originalBlock.Length < threshold;
    }

    /// <summary>
    /// Generates a VCDIFF-style binary patch that transforms
    /// <paramref name="originalBlock"/> into <paramref name="modifiedBlock"/>.
    /// </summary>
    /// <param name="originalBlock">The original block (before modification).</param>
    /// <param name="modifiedBlock">The modified block (after modification).</param>
    /// <returns>
    /// A <see cref="DeltaPatchResult"/> containing the encoded patch and metadata
    /// indicating whether applying the delta is more efficient than full CoW.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Block spans have different lengths.
    /// </exception>
    public DeltaPatchResult GeneratePatch(
        ReadOnlySpan<byte> originalBlock,
        ReadOnlySpan<byte> modifiedBlock)
    {
        if (originalBlock.Length != modifiedBlock.Length)
            throw new ArgumentException(
                "originalBlock and modifiedBlock must have the same length.");

        int blockLen = originalBlock.Length;

        // Build the list of operations by scanning for changed byte runs.
        var ops = BuildOperations(originalBlock, modifiedBlock);

        // Encode into byte array.
        byte[] patchData = EncodeOps(ops, blockLen, blockLen);
        int patchSize = patchData.Length;

        double ratio = blockLen == 0 ? 1.0 : (double)blockLen / patchSize;
        bool worthwhile = patchSize < _blockSize * 0.5;

        return new DeltaPatchResult
        {
            PatchData       = patchData,
            PatchSize       = patchSize,
            IsDeltaWorthwhile = worthwhile,
            CompressionRatio = ratio,
        };
    }

    /// <summary>
    /// Applies a binary patch generated by <see cref="GeneratePatch"/> to
    /// <paramref name="baseBlock"/>, returning the reconstructed block.
    /// </summary>
    /// <param name="baseBlock">The original (base) block bytes.</param>
    /// <param name="patchData">Patch bytes as produced by <see cref="GeneratePatch"/>.</param>
    /// <returns>The patched block bytes.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="patchData"/> has an invalid header or is too short.
    /// </exception>
    public byte[] ApplyPatch(ReadOnlySpan<byte> baseBlock, ReadOnlySpan<byte> patchData)
    {
        return ApplyPatchInternal(baseBlock, patchData);
    }

    /// <summary>
    /// Applies a sequential chain of patches to <paramref name="baseBlock"/>,
    /// returning the final reconstructed block after all patches have been applied.
    /// </summary>
    /// <param name="baseBlock">The original (base) block.</param>
    /// <param name="patches">
    /// Ordered list of patch byte arrays. Each patch is applied to the output of the
    /// previous step.
    /// </param>
    /// <returns>The block resulting from applying all patches in order.</returns>
    public byte[] ApplyDeltaChain(ReadOnlySpan<byte> baseBlock, IReadOnlyList<byte[]> patches)
    {
        if (patches.Count == 0)
        {
            byte[] copy = new byte[baseBlock.Length];
            baseBlock.CopyTo(copy);
            return copy;
        }

        byte[] current = ApplyPatch(baseBlock, patches[0]);
        for (int i = 1; i < patches.Count; i++)
            current = ApplyPatch(current, patches[i]);

        return current;
    }

    /// <summary>
    /// Flattens a delta chain into a single fresh base block by applying all patches
    /// sequentially. Semantically identical to <see cref="ApplyDeltaChain"/>; the
    /// separate method name signals intent to the compaction path.
    /// </summary>
    /// <param name="baseBlock">The original base block.</param>
    /// <param name="patches">Ordered list of patches to apply.</param>
    /// <returns>The compacted (flattened) block.</returns>
    public byte[] FlattenChain(ReadOnlySpan<byte> baseBlock, IReadOnlyList<byte[]> patches)
        => ApplyDeltaChain(baseBlock, patches);

    // ── Private helpers ────────────────────────────────────────────────────────

    private static byte[] ApplyPatchInternal(ReadOnlySpan<byte> baseBlock, ReadOnlySpan<byte> patch)
    {
        if (patch.Length < HeaderSize)
            throw new ArgumentException("Patch data is too short to contain a valid header.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(patch[0..4]);
        if (magic != PatchHeaderMagic)
            throw new ArgumentException(
                $"Invalid patch magic: 0x{magic:X8}. Expected 0x{PatchHeaderMagic:X8}.");

        int origSize    = BinaryPrimitives.ReadInt32LittleEndian(patch[4..8]);
        int patchedSize = BinaryPrimitives.ReadInt32LittleEndian(patch[8..12]);
        ushort numOps   = BinaryPrimitives.ReadUInt16LittleEndian(patch[12..14]);

        if (origSize < 0 || patchedSize < 0)
            throw new ArgumentException("Patch header contains negative block sizes.");

        byte[] output = new byte[patchedSize];
        int writePos  = 0;
        int readPos   = HeaderSize;

        for (int op = 0; op < numOps; op++)
        {
            if (readPos + OpHeaderSize > patch.Length)
                throw new ArgumentException(
                    $"Patch data truncated at operation {op}.");

            byte opType  = patch[readPos];
            int offset   = BinaryPrimitives.ReadInt32LittleEndian(patch.Slice(readPos + 1, 4));
            int length   = BinaryPrimitives.ReadUInt16LittleEndian(patch.Slice(readPos + 5, 2));
            readPos += OpHeaderSize;

            if (opType == OpCopy)
            {
                // Copy 'length' bytes from baseBlock at 'offset'.
                if (offset < 0 || offset + length > baseBlock.Length)
                    throw new ArgumentException(
                        $"Copy op at index {op} out of range: offset={offset}, length={length}.");
                if (writePos + length > output.Length)
                    throw new ArgumentException(
                        $"Copy op at index {op} would overflow output buffer.");

                baseBlock.Slice(offset, length).CopyTo(output.AsSpan(writePos, length));
                writePos += length;
            }
            else if (opType == OpInsert)
            {
                // Insert 'length' literal bytes that follow immediately in the patch.
                if (readPos + length > patch.Length)
                    throw new ArgumentException(
                        $"Insert op at index {op} extends past end of patch data.");
                if (writePos + length > output.Length)
                    throw new ArgumentException(
                        $"Insert op at index {op} would overflow output buffer.");

                patch.Slice(readPos, length).CopyTo(output.AsSpan(writePos, length));
                writePos += length;
                readPos  += length;
            }
            else
            {
                throw new ArgumentException($"Unknown op type 0x{opType:X2} at operation {op}.");
            }
        }

        if (writePos != patchedSize)
            throw new ArgumentException(
                $"Patch application produced {writePos} bytes but header declared {patchedSize}.");

        return output;
    }

    /// <summary>
    /// Builds an ordered list of operations (Copy/Insert) by scanning two blocks
    /// for differing byte runs.
    /// </summary>
    private static List<(byte OpType, int Offset, ushort Length, byte[]? Data)> BuildOperations(
        ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> modified)
    {
        var ops = new List<(byte, int, ushort, byte[]?)>();
        int len = original.Length;
        int i = 0;

        while (i < len)
        {
            if (original[i] == modified[i])
            {
                // Find the end of the matching run.
                int start = i;
                while (i < len && original[i] == modified[i])
                    i++;

                int runLen = i - start;
                // Emit as one or more Copy ops (ushort max per op).
                int remaining = runLen;
                int cursor    = start;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, ushort.MaxValue);
                    ops.Add((OpCopy, cursor, (ushort)chunk, null));
                    cursor    += chunk;
                    remaining -= chunk;
                }
            }
            else
            {
                // Find the end of the differing run.
                int start = i;
                while (i < len && (i >= original.Length || original[i] != modified[i]))
                    i++;

                int runLen   = i - start;
                int remaining = runLen;
                int cursor    = start;
                while (remaining > 0)
                {
                    int chunk   = Math.Min(remaining, ushort.MaxValue);
                    byte[] data = modified.Slice(cursor, chunk).ToArray();
                    ops.Add((OpInsert, 0, (ushort)chunk, data));
                    cursor    += chunk;
                    remaining -= chunk;
                }
            }
        }

        return ops;
    }

    /// <summary>
    /// Encodes the operation list into a byte array using the internal VCDIFF-style format.
    /// </summary>
    private static byte[] EncodeOps(
        List<(byte OpType, int Offset, ushort Length, byte[]? Data)> ops,
        int origSize,
        int patchedSize)
    {
        // Calculate total size.
        int totalSize = HeaderSize;
        foreach (var (opType, _, length, data) in ops)
        {
            totalSize += OpHeaderSize;
            if (opType == OpInsert && data != null)
                totalSize += data.Length;
        }

        byte[] patch = new byte[totalSize];
        int pos = 0;

        // Write header.
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(pos, 4), PatchHeaderMagic);
        pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(pos, 4), origSize);
        pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(pos, 4), patchedSize);
        pos += 4;

        // Clamp to ushort.MaxValue; callers should not exceed this for well-formed blocks.
        ushort numOps = (ushort)Math.Min(ops.Count, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(patch.AsSpan(pos, 2), numOps);
        pos += 2;

        // Write operations.
        foreach (var (opType, offset, length, data) in ops)
        {
            patch[pos] = opType;
            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(pos + 1, 4), offset);
            BinaryPrimitives.WriteUInt16LittleEndian(patch.AsSpan(pos + 5, 2), length);
            pos += OpHeaderSize;

            if (opType == OpInsert && data != null)
            {
                data.CopyTo(patch, pos);
                pos += data.Length;
            }
        }

        return patch;
    }
}
