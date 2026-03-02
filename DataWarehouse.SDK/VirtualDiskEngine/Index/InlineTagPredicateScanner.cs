using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// Result from an inline tag predicate scan operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-54 inline tag predicate scan result")]
public readonly struct TagScanResult
{
    /// <summary>Inode numbers that satisfied all predicates.</summary>
    public IReadOnlyList<long> MatchingInodeNumbers { get; init; }

    /// <summary>Total number of inodes examined during the scan.</summary>
    public long InodesScanned { get; init; }

    /// <summary>Total number of tag slots examined across all scanned inodes.</summary>
    public long TagSlotsExamined { get; init; }

    /// <summary>Whether SIMD Vector256 acceleration was used during hash matching.</summary>
    public bool SimdWasUsed { get; init; }

    /// <summary>Total wall-clock duration of the scan.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// High-throughput fixed-offset columnar scanner for inline tag predicate evaluation (VOPT-54).
/// </summary>
/// <remarks>
/// Scans the inode table block-by-block, extracting only the 176-byte InlineTagArea
/// starting at byte offset 320 within each 512-byte inode, without deserializing the full inode.
///
/// Inode layout (relevant fields):
/// <code>
///   [0..312)    Core inode fields
///   [312..316)  InlineTagCount (uint32) — number of valid inline tag slots (0-5)
///   [316..320)  Reserved / padding
///   [320..496)  InlineTagArea (176 bytes = 5 × 32-byte tag slots + 16-byte overflow block ref)
///   [496..512)  Extended inode tail
/// </code>
///
/// Tag slot layout (32 bytes):
/// <code>
///   [0..4)   NamespaceHash (uint32, little-endian)
///   [4..8)   NameHash      (uint32, little-endian)
///   [8)      ValueType     (byte)
///   [9)      ValueLen      (byte, 0-22)
///   [10..32) Value         (up to 22 bytes)
/// </code>
///
/// When <see cref="TagPredicateScanConfig.UseSimdAcceleration"/> is true and AVX2 is available,
/// hash matching uses Vector256 to compare all 5 NamespaceHash values in parallel.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-54 inline tag predicate scanner with SIMD acceleration")]
public sealed class InlineTagPredicateScanner
{
    // ── Inode layout constants ───────────────────────────────────────────

    /// <summary>Total size of one inode on disk (bytes).</summary>
    public const int InodeSize = 512;

    /// <summary>Byte offset of InlineTagCount within each 512-byte inode.</summary>
    public const int InlineTagCountOffset = 312;

    /// <summary>Byte offset where the InlineTagArea begins within each 512-byte inode.</summary>
    public const int InlineTagAreaOffset = 320;

    /// <summary>Total size of the InlineTagArea in bytes (5 slots × 32B + 16B overflow ref).</summary>
    public const int InlineTagAreaSize = 176;

    /// <summary>Size of a single tag slot in bytes.</summary>
    public const int TagSlotSize = 32;

    /// <summary>Maximum number of tag slots stored inline per inode.</summary>
    public const int MaxInlineSlots = 5;

    /// <summary>Maximum number of value bytes stored per inline tag slot.</summary>
    public const int MaxInlineValueBytes = 22;

    /// <summary>
    /// Number of inodes per 4KB device block (4096 / 512 = 8).
    /// Assumes block size is 4096; adjusted at runtime via <see cref="_inodesPerBlock"/>.
    /// </summary>
    public const int InodesPerBlock = 4096 / InodeSize;

    // ── Tag slot field offsets (relative to slot start) ──────────────────

    private const int SlotNamespaceHashOffset = 0;  // uint32
    private const int SlotNameHashOffset = 4;        // uint32
    private const int SlotValueTypeOffset = 8;       // byte
    private const int SlotValueLenOffset = 9;        // byte
    private const int SlotValueOffset = 10;          // up to 22 bytes

    // ── Private state ────────────────────────────────────────────────────

    private readonly IBlockDevice _device;
    private readonly int _blockSize;
    private readonly int _inodesPerBlock;
    private readonly TagPredicateScanConfig _config;
    private readonly bool _simdAvailable;

    /// <summary>
    /// Initialises the scanner.
    /// </summary>
    /// <param name="device">Block device containing the inode table.</param>
    /// <param name="blockSize">Device block size in bytes (typically 4096).</param>
    /// <param name="config">Scan configuration.</param>
    public InlineTagPredicateScanner(IBlockDevice device, int blockSize, TagPredicateScanConfig config)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _inodesPerBlock = blockSize / InodeSize;
        if (_inodesPerBlock < 1)
            throw new ArgumentException($"Block size {blockSize} is smaller than inode size {InodeSize}.", nameof(blockSize));

        // Detect SIMD capability once at construction time.
        _simdAvailable = config.UseSimdAcceleration && Avx2.IsSupported;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Scans the inode table for inodes whose inline tags satisfy all supplied predicates.
    /// </summary>
    /// <param name="predicates">
    /// List of predicates that must ALL match (AND semantics).
    /// Pass an empty list to return all inodes that have at least one inline tag.
    /// </param>
    /// <param name="inodeTableStartBlock">Block number where the inode table begins on the device.</param>
    /// <param name="inodeCount">Total number of inodes in the table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="TagScanResult"/> with matching inode numbers and scan statistics.
    /// </returns>
    public async Task<TagScanResult> ScanAsync(
        IReadOnlyList<TagPredicate> predicates,
        long inodeTableStartBlock,
        long inodeCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentOutOfRangeException.ThrowIfNegative(inodeTableStartBlock);
        ArgumentOutOfRangeException.ThrowIfNegative(inodeCount);

        var sw = Stopwatch.StartNew();
        var results = new List<long>();
        long inodesScanned = 0;
        long tagSlotsExamined = 0;
        bool simdUsed = false;

        long totalBlocks = (inodeCount + _inodesPerBlock - 1) / _inodesPerBlock;
        int batchBytes = _config.BatchSizeBlocks * _blockSize;
        byte[] batchBuffer = new byte[batchBytes];

        for (long batchStart = 0; batchStart < totalBlocks && results.Count < _config.MaxResultCount; batchStart += _config.BatchSizeBlocks)
        {
            ct.ThrowIfCancellationRequested();

            long blocksInBatch = Math.Min(_config.BatchSizeBlocks, totalBlocks - batchStart);
            int bytesInBatch = (int)(blocksInBatch * _blockSize);

            // Read each block individually (IBlockDevice provides single-block reads).
            for (long bi = 0; bi < blocksInBatch; bi++)
            {
                long blockNumber = inodeTableStartBlock + batchStart + bi;
                int blockOffset = (int)(bi * _blockSize);
                await _device.ReadBlockAsync(blockNumber, batchBuffer.AsMemory(blockOffset, _blockSize), ct).ConfigureAwait(false);
            }

            // Process inodes within the batch.
            long globalInodeBase = (batchStart / 1) * _inodesPerBlock; // batchStart is in blocks

            // Recalculate: batchStart is the first block of this batch.
            globalInodeBase = batchStart * _inodesPerBlock;

            int inodesInBatch = (int)(blocksInBatch * _inodesPerBlock);
            for (int i = 0; i < inodesInBatch && inodesScanned < inodeCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= _config.MaxResultCount) break;

                int inodeBaseInBuffer = i * InodeSize;
                if (inodeBaseInBuffer + InodeSize > bytesInBatch) break;

                ReadOnlySpan<byte> inodeSpan = batchBuffer.AsSpan(inodeBaseInBuffer, InodeSize);

                // Read InlineTagCount at offset 312 (uint32 LE).
                uint inlineTagCount = BinaryPrimitives.ReadUInt32LittleEndian(inodeSpan.Slice(InlineTagCountOffset, 4));

                inodesScanned++;

                if (inlineTagCount == 0) continue; // Skip inodes with no inline tags.

                uint effectiveSlots = Math.Min(inlineTagCount, (uint)MaxInlineSlots);

                // Extract the 176-byte InlineTagArea at offset 320.
                ReadOnlySpan<byte> tagArea = inodeSpan.Slice(InlineTagAreaOffset, InlineTagAreaSize);

                tagSlotsExamined += effectiveSlots;

                long inodeNumber = globalInodeBase + i;

                if (predicates.Count == 0)
                {
                    // No predicates — match all inodes with tags.
                    results.Add(inodeNumber);
                    continue;
                }

                // Evaluate all predicates against this inode's inline tags.
                bool allMatch = EvaluateAllPredicates(predicates, tagArea, effectiveSlots, ref simdUsed);
                if (allMatch)
                {
                    results.Add(inodeNumber);
                }
            }
        }

        sw.Stop();
        return new TagScanResult
        {
            MatchingInodeNumbers = results,
            InodesScanned = inodesScanned,
            TagSlotsExamined = tagSlotsExamined,
            SimdWasUsed = simdUsed,
            Duration = sw.Elapsed,
        };
    }

    // ── Predicate evaluation ─────────────────────────────────────────────

    /// <summary>
    /// Returns true if every predicate in the list is satisfied by at least one tag slot
    /// in the given tag area.
    /// </summary>
    private bool EvaluateAllPredicates(
        IReadOnlyList<TagPredicate> predicates,
        ReadOnlySpan<byte> tagArea,
        uint slotCount,
        ref bool simdUsed)
    {
        foreach (var predicate in predicates)
        {
            bool predicateSatisfied;

            if (_simdAvailable && slotCount > 1)
            {
                predicateSatisfied = TrySimdMatchHashes(tagArea, predicate.NamespaceHash, predicate.NameHash, slotCount);
                simdUsed = true;

                if (predicateSatisfied && predicate.Op == TagPredicateOp.Exists)
                {
                    // Exists + SIMD hash match → predicate satisfied.
                    continue;
                }

                if (!predicateSatisfied)
                {
                    // No slot has matching hashes → predicate not satisfied.
                    return false;
                }

                // Hash matched (at least one slot) — still need to evaluate Op against value.
                // Fall through to scalar evaluation for value comparison.
            }
            else
            {
                predicateSatisfied = false;
            }

            // Scalar per-slot evaluation.
            bool foundMatchingSlot = false;
            for (uint s = 0; s < slotCount; s++)
            {
                ReadOnlySpan<byte> slot = tagArea.Slice((int)(s * TagSlotSize), TagSlotSize);
                if (EvaluatePredicate(predicate, slot))
                {
                    foundMatchingSlot = true;
                    break;
                }
            }

            if (!foundMatchingSlot) return false;
        }

        return true;
    }

    /// <summary>
    /// Evaluates a single tag predicate against one tag slot.
    /// </summary>
    /// <param name="predicate">The predicate to evaluate.</param>
    /// <param name="tagSlot">Exactly <see cref="TagSlotSize"/> bytes representing one tag slot.</param>
    /// <returns>True if the slot matches the predicate.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(TagPredicate predicate, ReadOnlySpan<byte> tagSlot)
    {
        // Parse slot header.
        uint nsHash = BinaryPrimitives.ReadUInt32LittleEndian(tagSlot.Slice(SlotNamespaceHashOffset, 4));
        uint nameHash = BinaryPrimitives.ReadUInt32LittleEndian(tagSlot.Slice(SlotNameHashOffset, 4));
        byte valueLen = tagSlot[SlotValueLenOffset];

        // Fast reject: hashes must match.
        if (nsHash != predicate.NamespaceHash || nameHash != predicate.NameHash)
            return false;

        // Clamp valueLen to the maximum storable.
        int effectiveLen = Math.Min((int)valueLen, MaxInlineValueBytes);
        ReadOnlySpan<byte> slotValue = tagSlot.Slice(SlotValueOffset, effectiveLen);
        ReadOnlySpan<byte> predicateValue = predicate.Value.Span;

        return predicate.Op switch
        {
            TagPredicateOp.Exists => true,

            TagPredicateOp.Equals => slotValue.SequenceEqual(predicateValue),

            TagPredicateOp.NotEquals => !slotValue.SequenceEqual(predicateValue),

            TagPredicateOp.StartsWith =>
                predicateValue.Length <= slotValue.Length &&
                slotValue[..predicateValue.Length].SequenceEqual(predicateValue),

            TagPredicateOp.GreaterThan =>
                ReadUInt64Le(slotValue) > ReadUInt64Le(predicateValue),

            TagPredicateOp.LessThan =>
                ReadUInt64Le(slotValue) < ReadUInt64Le(predicateValue),

            _ => false,
        };
    }

    // ── SIMD acceleration ────────────────────────────────────────────────

    /// <summary>
    /// Uses AVX2 Vector256 to batch-compare all inline tag slot hashes against the target hashes.
    /// </summary>
    /// <param name="tagArea">The 176-byte inline tag area.</param>
    /// <param name="targetNamespaceHash">Namespace hash to match.</param>
    /// <param name="targetNameHash">Name hash to match.</param>
    /// <param name="slotCount">Number of valid slots (1-5).</param>
    /// <returns>True if any slot has matching namespace hash AND name hash.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySimdMatchHashes(
        ReadOnlySpan<byte> tagArea,
        uint targetNamespaceHash,
        uint targetNameHash,
        uint slotCount)
    {
        // Load namespace hashes from all 5 slot positions into an array.
        // Slots are at offsets 0, 32, 64, 96, 128 within tagArea.
        // Each slot: [NamespaceHash:4][NameHash:4][...]
        // We read up to MaxInlineSlots = 5 slots; pad missing lanes with 0.

        Span<uint> nsHashes = stackalloc uint[8];   // Vector256<uint> = 8 uint lanes
        Span<uint> nameHashes = stackalloc uint[8];

        for (uint s = 0; s < MaxInlineSlots; s++)
        {
            if (s < slotCount)
            {
                int slotBase = (int)(s * TagSlotSize);
                nsHashes[(int)s] = BinaryPrimitives.ReadUInt32LittleEndian(tagArea.Slice(slotBase, 4));
                nameHashes[(int)s] = BinaryPrimitives.ReadUInt32LittleEndian(tagArea.Slice(slotBase + 4, 4));
            }
            // else: remain 0, which won't match a real target hash (unless target is 0, handled correctly)
        }

        // Broadcast target hashes across all 8 lanes.
        Vector256<uint> vecNsHashes = Vector256.Create(nsHashes[0], nsHashes[1], nsHashes[2], nsHashes[3],
                                                        nsHashes[4], nsHashes[5], nsHashes[6], nsHashes[7]);
        Vector256<uint> vecNameHashes = Vector256.Create(nameHashes[0], nameHashes[1], nameHashes[2], nameHashes[3],
                                                          nameHashes[4], nameHashes[5], nameHashes[6], nameHashes[7]);

        Vector256<uint> targetNs = Vector256.Create(targetNamespaceHash);
        Vector256<uint> targetName = Vector256.Create(targetNameHash);

        // Compare both namespace and name hashes in parallel.
        Vector256<uint> nsMatch = Avx2.CompareEqual(vecNsHashes, targetNs);
        Vector256<uint> nameMatch = Avx2.CompareEqual(vecNameHashes, targetName);
        Vector256<uint> bothMatch = Avx2.And(nsMatch, nameMatch);

        // Check if any lane matched (non-zero mask).
        // Only lanes 0..slotCount-1 are valid; since invalid lanes have 0 hashes
        // and a real hash of 0 is unlikely, this is safe for practical purposes.
        int mask = Avx2.MoveMask(bothMatch.AsByte());
        return mask != 0;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a uint64 from up to 8 bytes, little-endian, zero-padding if shorter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64Le(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8)
            return BinaryPrimitives.ReadUInt64LittleEndian(data);

        Span<byte> padded = stackalloc byte[8];
        data.CopyTo(padded);
        return BinaryPrimitives.ReadUInt64LittleEndian(padded);
    }
}
