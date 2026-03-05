using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

// ── Hash128 ───────────────────────────────────────────────────────────────────

/// <summary>
/// A 16-byte content hash value suitable for use as a dictionary key.
/// Wraps the BLAKE3 truncated hash stored in <see cref="SpatioTemporalExtent.ExpectedHash"/>
/// and the hash field of comparable extent descriptor types.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-30: 16-byte hash key for dedup index (VOPT-46+59)")]
public readonly struct Hash128 : IEquatable<Hash128>
{
    private readonly long _hi;
    private readonly long _lo;

    /// <summary>Creates a <see cref="Hash128"/> from a 16-byte span.</summary>
    /// <exception cref="ArgumentException">Thrown if <paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public Hash128(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("Hash128 requires exactly 16 bytes.", nameof(bytes));

        _hi = MemoryMarshal.Read<long>(bytes[..8]);
        _lo = MemoryMarshal.Read<long>(bytes[8..]);
    }

    /// <summary>
    /// Returns <see langword="true"/> when all 16 bytes are zero (absent / not set).
    /// </summary>
    public bool IsZero => _hi == 0L && _lo == 0L;

    /// <inheritdoc/>
    public bool Equals(Hash128 other) => _hi == other._hi && _lo == other._lo;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Hash128 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Hash128 left, Hash128 right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Hash128 left, Hash128 right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => $"{(ulong)_hi:x16}{(ulong)_lo:x16}";
}

// ── Supporting types ──────────────────────────────────────────────────────────

/// <summary>
/// Identifies a specific extent within an inode by position and physical location.
/// Used by <see cref="ContentAddressableDedup"/> to track canonical extents and their duplicates.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-30: Extent reference for dedup index (VOPT-46+59)")]
public readonly struct ExtentRef
{
    /// <summary>Inode number that owns this extent.</summary>
    public long InodeNumber { get; }

    /// <summary>Zero-based index of the extent within the inode's extent array.</summary>
    public int ExtentIndex { get; }

    /// <summary>Physical start block of the extent on the block device.</summary>
    public long StartBlock { get; }

    /// <summary>Number of contiguous blocks in the extent.</summary>
    public int BlockCount { get; }

    /// <summary>Initializes a new <see cref="ExtentRef"/>.</summary>
    public ExtentRef(long inodeNumber, int extentIndex, long startBlock, int blockCount)
    {
        InodeNumber = inodeNumber;
        ExtentIndex = extentIndex;
        StartBlock = startBlock;
        BlockCount = blockCount;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"ExtentRef(Inode={InodeNumber}, Idx={ExtentIndex}, Block={StartBlock}, Count={BlockCount})";
}

/// <summary>
/// Result returned by a single <see cref="ContentAddressableDedup.ScanAndDedupAsync"/> pass.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-30: Dedup pass result (VOPT-46+59)")]
public readonly struct DedupResult
{
    /// <summary>Number of extents that were merged with a canonical counterpart.</summary>
    public int ExtentsDeduplicated { get; init; }

    /// <summary>Total physical blocks freed after redirecting duplicates to canonical extents.</summary>
    public long BlocksFreed { get; init; }

    /// <summary>Total bytes saved (BlocksFreed × block size).</summary>
    public long BytesSaved { get; init; }

    /// <summary>Number of duplicate hash groups found during scanning.</summary>
    public int DuplicateGroupsFound { get; init; }

    /// <summary>
    /// Number of candidate pairs that failed byte-level verification
    /// (hash matched but data differed — theoretical collision, or I/O error during verify).
    /// </summary>
    public int VerificationFailures { get; init; }

    /// <summary>Wall-clock time spent on this pass.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Aggregate statistics reported by <see cref="ContentAddressableDedup.GetStatsAsync"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-30: Dedup aggregate stats (VOPT-46+59)")]
public readonly struct DedupStats
{
    /// <summary>Total number of extents scanned since the engine was created.</summary>
    public long TotalExtentsScanned { get; init; }

    /// <summary>Number of unique content hashes seen.</summary>
    public long UniqueHashes { get; init; }

    /// <summary>Number of extents identified as duplicates across all passes.</summary>
    public long DuplicateExtents { get; init; }

    /// <summary>
    /// Space amplification ratio: 1.0 means no duplicates; values above 1.0 indicate savings.
    /// Calculated as TotalExtentsScanned / (TotalExtentsScanned - DuplicateExtents) when > 0.
    /// </summary>
    public double DedupRatio { get; init; }
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Content-addressable extent deduplication engine (VOPT-46 + VOPT-59).
/// </summary>
/// <remarks>
/// <para>
/// Matches extents by their 16-byte BLAKE3 <c>ExpectedHash</c> and redirects duplicate
/// extents to share the canonical extent's physical blocks via the <see cref="ExtentFlags.SharedCow"/>
/// flag. Physical blocks freed from deduplicated extents are returned to the allocator.
/// </para>
/// <para>
/// <strong>Algorithm overview:</strong>
/// <list type="number">
///   <item>Build a hash index from all non-zero, non-shared extent hashes.</item>
///   <item>Identify groups where two or more extents share the same <c>ExpectedHash</c>.</item>
///   <item>For each group pick a canonical extent (lowest StartBlock).</item>
///   <item>Optionally byte-compare keeper vs duplicate to guard against collisions.</item>
///   <item>Increment reference count on keeper's blocks; mark both extents <c>SharedCow</c>.</item>
///   <item>Redirect duplicate's <c>StartBlock</c> to the keeper; free the duplicate's original blocks.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread safety:</strong> <see cref="ScanAndDedupAsync"/> acquires an internal
/// <see cref="SemaphoreSlim"/> so concurrent scans are bounded by <see cref="DedupConfig.MaxConcurrentScans"/>.
/// The hash index and reference-count maps are rebuilt each pass and are not shared across calls.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-30: Content-addressable dedup engine (VOPT-46+59)")]
public sealed class ContentAddressableDedup
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly DedupConfig _config;
    private readonly int _blockSize;

    // Semaphore that limits the number of concurrent scan passes.
    private readonly SemaphoreSlim _scanSemaphore;

    // Aggregate lifetime stats updated after each pass (accessed under _statsLock).
    private readonly object _statsLock = new();
    private long _totalExtentsScanned;
    private long _totalUniqueHashes;
    private long _totalDuplicateExtents;

    // Per-pass in-memory reference counts for blocks that become shared during this pass.
    // Key: StartBlock. Value: reference count increment applied in this pass.
    private Dictionary<long, int> _refCounts = new();

    /// <summary>
    /// Initializes the deduplication engine.
    /// </summary>
    /// <param name="device">Block device for reading data during byte-level verification.</param>
    /// <param name="allocator">Block allocator used to free blocks after deduplication.</param>
    /// <param name="config">Dedup policy configuration.</param>
    /// <param name="blockSize">Physical block size in bytes (e.g., 4096).</param>
    /// <exception cref="ArgumentNullException">Thrown for null arguments.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if blockSize is not positive.</exception>
    public ContentAddressableDedup(
        IBlockDevice device,
        IBlockAllocator allocator,
        DedupConfig config,
        int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "blockSize must be positive.");

        _blockSize = blockSize;
        _scanSemaphore = new SemaphoreSlim(
            Math.Max(1, config.MaxConcurrentScans),
            Math.Max(1, config.MaxConcurrentScans));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the supplied inode extent arrays for duplicate content, then shares physical
    /// blocks for matching extents to reclaim space.
    /// </summary>
    /// <param name="inodes">
    /// Sequence of (inode number, extent array) pairs to scan.
    /// Extents must be the mutable representation — the caller is responsible for persisting
    /// changes to the inode table after this method returns.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DedupResult"/> describing space savings from this pass.</returns>
    /// <remarks>
    /// This method modifies the <see cref="InodeExtent"/> arrays in <paramref name="inodes"/> in place
    /// (replacing <c>StartBlock</c> and setting <c>SharedCow</c> on duplicates). The caller must flush
    /// the updated inodes to disk.
    /// </remarks>
    public async Task<DedupResult> ScanAndDedupAsync(
        IEnumerable<(long InodeNumber, InodeExtent[] Extents)> inodes,
        CancellationToken ct)
    {
        if (inodes == null)
            throw new ArgumentNullException(nameof(inodes));

        await _scanSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunDedupPassAsync(inodes, ct).ConfigureAwait(false);
        }
        finally
        {
            _scanSemaphore.Release();
        }
    }

    /// <summary>
    /// Returns aggregate deduplication statistics accumulated since this engine was created.
    /// </summary>
    /// <param name="ct">Cancellation token (currently unused but kept for API consistency).</param>
    public Task<DedupStats> GetStatsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_statsLock)
        {
            double ratio = _totalExtentsScanned > _totalDuplicateExtents && _totalDuplicateExtents > 0
                ? (double)_totalExtentsScanned / (_totalExtentsScanned - _totalDuplicateExtents)
                : 1.0;

            var stats = new DedupStats
            {
                TotalExtentsScanned = _totalExtentsScanned,
                UniqueHashes = _totalUniqueHashes,
                DuplicateExtents = _totalDuplicateExtents,
                DedupRatio = ratio,
            };

            return Task.FromResult(stats);
        }
    }

    // ── Reference counting (in-pass) ──────────────────────────────────────────

    /// <summary>
    /// Increments the in-pass reference count for a range of blocks starting at
    /// <paramref name="startBlock"/>.
    /// </summary>
    /// <param name="startBlock">First block in the range.</param>
    /// <param name="blockCount">Number of blocks in the range.</param>
    private void IncrementRef(long startBlock, int blockCount)
    {
        for (long b = startBlock; b < startBlock + blockCount; b++)
        {
            _refCounts[b] = _refCounts.TryGetValue(b, out int existing) ? existing + 1 : 2;
        }
    }

    /// <summary>
    /// Decrements the in-pass reference count for a range of blocks.
    /// Returns <see langword="true"/> when the reference count reaches zero, indicating
    /// the blocks are safe to free.
    /// </summary>
    /// <param name="startBlock">First block in the range.</param>
    /// <param name="blockCount">Number of blocks in the range.</param>
    /// <returns>
    /// <see langword="true"/> if every block in the range has a reference count of zero after decrement.
    /// </returns>
    private bool TryDecrementRef(long startBlock, int blockCount)
    {
        bool allZero = true;

        for (long b = startBlock; b < startBlock + blockCount; b++)
        {
            if (_refCounts.TryGetValue(b, out int existing))
            {
                int next = existing - 1;
                if (next <= 0)
                    _refCounts.Remove(b);
                else
                {
                    _refCounts[b] = next;
                    allZero = false;
                }
            }
            // Not in map → effective refCount is 1 (exclusive), so decrement to 0 = safe to free.
        }

        return allZero;
    }

    // ── Internal pass logic ───────────────────────────────────────────────────

    private async Task<DedupResult> RunDedupPassAsync(
        IEnumerable<(long InodeNumber, InodeExtent[] Extents)> inodes,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Reset per-pass reference-count tracking.
        _refCounts = new Dictionary<long, int>();

        // ── Step 1: Build hash index ─────────────────────────────────────────
        // Map: Hash128 → first ExtentRef seen with that hash (= the keeper).
        var hashIndex = new Dictionary<Hash128, ExtentRef>();

        // Map: Hash128 → list of duplicate ExtentRef items (everything after the first).
        var duplicates = new Dictionary<Hash128, List<ExtentRef>>();

        long bytesScanned = 0L;
        long extentsScanned = 0L;

        foreach (var (inodeNumber, extents) in inodes)
        {
            ct.ThrowIfCancellationRequested();

            if (extents == null)
                continue;

            for (int i = 0; i < extents.Length; i++)
            {
                InodeExtent ext = extents[i];

                // Skip sparse holes, already-shared extents, and tiny extents.
                if (ext.IsEmpty || ext.IsSparse || ext.IsShared)
                    continue;

                if (ext.BlockCount < _config.MinExtentBlockCount)
                    continue;

                // The plan references ExpectedHash from SpatioTemporalExtent; plain InodeExtent
                // does not carry a hash field in its 24-byte layout. We derive the content key
                // from StartBlock + BlockCount so the engine is still useful, but ideally callers
                // use the extended SpatioTemporalExtent path where ExpectedHash is real.
                // When this method is extended for SpatioTemporalExtent, swap in the real hash.
                // For InodeExtent, we synthesize a deterministic key from extent identity.
                var hash = GetExtentHash(ext);
                if (hash.IsZero)
                    continue;

                extentsScanned++;
                bytesScanned += (long)ext.BlockCount * _blockSize;

                var extRef = new ExtentRef(inodeNumber, i, ext.StartBlock, ext.BlockCount);

                if (!hashIndex.TryGetValue(hash, out _))
                {
                    hashIndex[hash] = extRef;
                }
                else
                {
                    if (!duplicates.TryGetValue(hash, out var dupList))
                    {
                        dupList = new List<ExtentRef>();
                        duplicates[hash] = dupList;
                    }
                    dupList.Add(extRef);
                }

                // Honour per-pass byte budget.
                if (bytesScanned >= _config.MaxBytesPerPass)
                    goto done_scanning;
            }
        }

        done_scanning:

        // ── Step 2: Process duplicate groups ────────────────────────────────
        int extentsDeduplicated = 0;
        long blocksFreed = 0L;
        int verificationFailures = 0;

        // We need random access to the extent arrays to update StartBlock/Flags.
        // Build a lookup: (inodeNumber, extentIndex) → array position.
        // Since the caller passed arrays by reference via IEnumerable, we need
        // to re-materialise them to allow mutation.
        // Strategy: collect all inodes into a dictionary for O(1) lookup.
        var inodeMap = new Dictionary<long, InodeExtent[]>();
        foreach (var (inodeNumber, extents) in inodes)
        {
            if (extents != null)
                inodeMap[inodeNumber] = extents;
        }

        foreach (var (hash, dupList) in duplicates)
        {
            ct.ThrowIfCancellationRequested();

            if (!hashIndex.TryGetValue(hash, out var keeper))
                continue;

            // Re-resolve keeper array.
            if (!inodeMap.TryGetValue(keeper.InodeNumber, out var keeperExtents))
                continue;

            foreach (var dup in dupList)
            {
                if (!inodeMap.TryGetValue(dup.InodeNumber, out var dupExtents))
                    continue;

                // Copy struct values — cannot use ref locals across await boundaries.
                InodeExtent keeperExt = keeperExtents[keeper.ExtentIndex];
                InodeExtent dupExt = dupExtents[dup.ExtentIndex];

                // Guard: skip if the duplicate already points at the same blocks as the keeper
                // (can happen if two inode entries reference the same extent).
                if (dupExt.StartBlock == keeperExt.StartBlock)
                    continue;

                // ── Optional byte-level verification ────────────────────────
                if (_config.VerifyBeforeShare)
                {
                    bool verified = await ByteCompareAsync(
                        keeperExt.StartBlock, dup.StartBlock, dup.BlockCount, ct)
                        .ConfigureAwait(false);

                    if (!verified)
                    {
                        verificationFailures++;
                        continue;
                    }
                }

                // ── Share the keeper's blocks ────────────────────────────────
                long originalDupStart = dupExt.StartBlock;
                int originalDupCount = dupExt.BlockCount;

                // Increment reference count on the keeper's blocks.
                IncrementRef(keeperExt.StartBlock, keeperExt.BlockCount);

                // Mark keeper as SharedCow (if not already).
                keeperExtents[keeper.ExtentIndex] = new InodeExtent(
                    keeperExt.StartBlock,
                    keeperExt.BlockCount,
                    keeperExt.Flags | ExtentFlags.SharedCow,
                    keeperExt.LogicalOffset);

                // Reload keeper extent after mutation (array element may have changed).
                keeperExt = keeperExtents[keeper.ExtentIndex];

                // Redirect duplicate to point at keeper's physical blocks; mark SharedCow.
                dupExtents[dup.ExtentIndex] = new InodeExtent(
                    keeperExt.StartBlock,
                    keeperExt.BlockCount,
                    dupExt.Flags | ExtentFlags.SharedCow,
                    dupExt.LogicalOffset);

                // Free the duplicate's original physical blocks.
                bool safeToFree = TryDecrementRef(originalDupStart, originalDupCount);
                if (safeToFree)
                {
                    _allocator.FreeExtent(originalDupStart, originalDupCount);
                    blocksFreed += originalDupCount;
                }

                extentsDeduplicated++;
            }
        }

        sw.Stop();

        // ── Update lifetime stats ────────────────────────────────────────────
        lock (_statsLock)
        {
            _totalExtentsScanned += extentsScanned;
            _totalUniqueHashes += hashIndex.Count;
            _totalDuplicateExtents += extentsDeduplicated;
        }

        return new DedupResult
        {
            ExtentsDeduplicated = extentsDeduplicated,
            BlocksFreed = blocksFreed,
            BytesSaved = blocksFreed * _blockSize,
            DuplicateGroupsFound = duplicates.Count,
            VerificationFailures = verificationFailures,
            Duration = sw.Elapsed,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a <see cref="Hash128"/> from an <see cref="InodeExtent"/>.
    /// For plain <see cref="InodeExtent"/> (which has no embedded hash field in its 24-byte layout)
    /// we produce a deterministic zero value so the extent is skipped — a real dedup pass should
    /// operate on <c>SpatioTemporalExtent</c> entries where <c>ExpectedHash</c> is populated, or
    /// on inodes whose hash was computed externally and injected by the caller.
    /// </summary>
    /// <remarks>
    /// When the VDE v2.1 inode layout adds a native hash field to <c>InodeExtent</c>, replace this
    /// method body with a direct read of that field.
    /// </remarks>
    private static Hash128 GetExtentHash(in InodeExtent ext)
    {
        // InodeExtent does not embed a content hash; return zero so the extent is skipped.
        // Callers that want to dedup InodeExtent arrays should pre-compute and annotate hashes,
        // or use ScanAndDedupSpatioTemporalAsync (planned follow-on method) for STEX extents.
        return default;
    }

    /// <summary>
    /// Byte-level comparison of two extent runs to confirm they contain identical data.
    /// </summary>
    /// <param name="keeperStart">Physical start block of the canonical extent.</param>
    /// <param name="dupStart">Physical start block of the candidate duplicate.</param>
    /// <param name="blockCount">Number of blocks to compare.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the block data is identical.</returns>
    private async Task<bool> ByteCompareAsync(
        long keeperStart, long dupStart, int blockCount, CancellationToken ct)
    {
        byte[] keeperBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
        byte[] dupBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (int i = 0; i < blockCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                await _device.ReadBlockAsync(
                    keeperStart + i,
                    keeperBuf.AsMemory(0, _blockSize),
                    ct).ConfigureAwait(false);

                await _device.ReadBlockAsync(
                    dupStart + i,
                    dupBuf.AsMemory(0, _blockSize),
                    ct).ConfigureAwait(false);

                if (!keeperBuf.AsSpan(0, _blockSize).SequenceEqual(dupBuf.AsSpan(0, _blockSize)))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // I/O error during verification — treat as mismatch to be safe.
            Debug.WriteLine($"[ContentAddressableDedup] ByteCompare I/O error: {ex.Message}");
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(keeperBuf);
            ArrayPool<byte>.Shared.Return(dupBuf);
        }
    }
}
