using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Retention;

/// <summary>
/// Result of advancing the oldest active epoch, converting millions of individual
/// deletes into a single superblock write.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public readonly struct EpochAdvanceResult
{
    /// <summary>The previous value of <c>OldestActiveEpoch</c> before advancement.</summary>
    public long PreviousOldestEpoch { get; init; }

    /// <summary>The new value of <c>OldestActiveEpoch</c> after advancement.</summary>
    public long NewOldestEpoch { get; init; }

    /// <summary>
    /// Estimated number of blocks eligible for lazy reclamation after this advancement.
    /// Actual reclamation is deferred to Background Vacuum TRLR scan cycles.
    /// </summary>
    public long EstimatedBlocksToReclaim { get; init; }

    /// <summary>
    /// Estimated number of inodes (files/entries) logically deleted by this epoch advancement.
    /// </summary>
    public long EstimatedInodesAffected { get; init; }
}

/// <summary>
/// Result of a single Background Vacuum TRLR scan cycle.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public readonly struct LazyReclaimResult
{
    /// <summary>Number of data blocks examined during this scan cycle.</summary>
    public long BlocksScanned { get; init; }

    /// <summary>Number of blocks reclaimed (freed) during this scan cycle.</summary>
    public long BlocksReclaimed { get; init; }

    /// <summary>Total bytes freed during this scan cycle.</summary>
    public long BytesReclaimed { get; init; }

    /// <summary>
    /// Fraction of the total TRLR scan completed since the last epoch advancement.
    /// Range: [0.0, 1.0]. Reaches 1.0 when the full device has been scanned.
    /// </summary>
    public double ScanProgress { get; init; }

    /// <summary>Wall-clock duration of this scan cycle.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Aggregated statistics for the epoch deletion subsystem.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public readonly struct EpochDeletionStats
{
    /// <summary>Current value of OldestActiveEpoch (the logical deletion fence).</summary>
    public long OldestActiveEpoch { get; init; }

    /// <summary>Current global epoch counter.</summary>
    public long CurrentEpoch { get; init; }

    /// <summary>Cumulative count of blocks reclaimed across all vacuum cycles.</summary>
    public long TotalBlocksReclaimed { get; init; }

    /// <summary>Cumulative count of epoch advancements since this instance was created.</summary>
    public long TotalEpochsAdvanced { get; init; }

    /// <summary>
    /// Fraction of the TRLR range scanned since the last epoch advancement.
    /// Range: [0.0, 1.0].
    /// </summary>
    public double VacuumScanProgress { get; init; }

    /// <summary>UTC timestamp of the most recent epoch advancement, or <see cref="DateTimeOffset.MinValue"/>.</summary>
    public DateTimeOffset LastAdvanceTime { get; init; }

    /// <summary>UTC timestamp of the most recent vacuum scan cycle, or <see cref="DateTimeOffset.MinValue"/>.</summary>
    public DateTimeOffset LastVacuumTime { get; init; }
}

/// <summary>
/// Epoch-gated bulk deletion engine (VOPT-49) that converts millions of individual
/// record deletes into a single superblock write by advancing <c>OldestActiveEpoch</c>.
/// Background Vacuum then lazily reclaims physical blocks via incremental TRLR scans.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design intent:</b> Time-series workloads (IoT telemetry, metrics, logs) routinely
/// need to expire millions of records. Individual per-record deletion causes O(N) I/O.
/// Epoch-gated deletion instead advances a single monotonic counter — the
/// <c>OldestActiveEpoch</c> — to logically invalidate all pre-epoch data in a single
/// superblock write (O(1) I/O). Physical block reclamation is deferred to Background Vacuum.
/// </para>
/// <para>
/// <b>TRLR scan (lazy vacuum):</b> Every physical block carries a
/// <see cref="UniversalBlockTrailer"/> whose <c>GenerationNumber</c> records the epoch at
/// which the block was written. When <c>GenerationNumber &lt; OldestActiveEpoch</c>, the
/// block is logically dead and can be freed. The scan is incremental — it resumes from
/// <see cref="_lastScanPosition"/> across vacuum cycles so that a single cycle processes
/// at most <c>maxBlocksPerCycle</c> blocks.
/// </para>
/// <para>
/// <b>Superblock persistence:</b> <c>OldestActiveEpoch</c> is persisted to a dedicated
/// metadata block on the device (block index 0, reserved layout bytes). On mount, the
/// caller should call <see cref="LoadFromMetadataBlockAsync"/> to restore the fenced epoch.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public sealed class EpochGatedLazyDeletion : IAsyncDisposable
{
    // Metadata block layout (64 bytes total):
    // [0..7]   Magic: "EGLD\0\0\0\0" (8 bytes)
    // [8..15]  OldestActiveEpoch (int64 LE)
    // [16..23] CurrentEpoch (int64 LE)
    // [24..31] LastScanPosition (int64 LE)
    // [32..39] TotalBlocksReclaimed (int64 LE)
    // [40..47] TotalEpochsAdvanced (int64 LE)
    // [48..55] LastAdvanceTimeTicks (int64 LE, UTC)
    // [56..63] LastVacuumTimeTicks (int64 LE, UTC)
    private static readonly byte[] MetadataMagic = [(byte)'E', (byte)'G', (byte)'L', (byte)'D', 0, 0, 0, 0];
    private const int MetadataSize = 64;
    private const long MetadataBlockIndex = 0L; // Block 0 used for epoch metadata

    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly RetentionEpochMapper _retentionMapper;
    private readonly int _blockSize;

    // Epoch state
    private long _oldestActiveEpoch;
    private long _currentEpoch;

    // Vacuum state (scan position persisted across cycles)
    private long _lastScanPosition;

    // Cumulative statistics
    private long _totalBlocksReclaimed;
    private long _totalEpochsAdvanced;
    private DateTimeOffset _lastAdvanceTime = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVacuumTime = DateTimeOffset.MinValue;

    // Disposal
    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;
    private bool _disposed;

    /// <summary>
    /// Gets the current value of <c>OldestActiveEpoch</c>.
    /// All blocks written in epochs strictly less than this value are logically deleted.
    /// </summary>
    public long OldestActiveEpoch => Interlocked.Read(ref _oldestActiveEpoch);

    /// <summary>
    /// Gets the current global epoch counter.
    /// </summary>
    public long CurrentEpoch => Interlocked.Read(ref _currentEpoch);

    /// <summary>
    /// Initializes a new <see cref="EpochGatedLazyDeletion"/> instance.
    /// </summary>
    /// <param name="device">Block device on which metadata and data blocks reside.</param>
    /// <param name="allocator">Block allocator used to physically free reclaimed blocks.</param>
    /// <param name="retentionMapper">Mapper that converts retention policies to epoch boundaries.</param>
    /// <param name="blockSize">Block size in bytes; must be at least <see cref="MetadataSize"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown for null arguments.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="blockSize"/> is too small.</exception>
    public EpochGatedLazyDeletion(
        IBlockDevice device,
        IBlockAllocator allocator,
        RetentionEpochMapper retentionMapper,
        int blockSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(retentionMapper);

        if (blockSize < MetadataSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {MetadataSize} bytes to hold epoch metadata.");

        _device = device;
        _allocator = allocator;
        _retentionMapper = retentionMapper;
        _blockSize = blockSize;
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads <c>OldestActiveEpoch</c> and related state from the metadata block on disk.
    /// Should be called at mount time before any epoch operations.
    /// If no valid epoch metadata block is found, the state is initialized to defaults (epoch 0).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadFromMetadataBlockAsync(CancellationToken ct = default)
    {
        var buffer = new byte[_blockSize];
        await _device.ReadBlockAsync(MetadataBlockIndex, buffer, ct);

        // Validate magic
        for (int i = 0; i < MetadataMagic.Length; i++)
        {
            if (buffer[i] != MetadataMagic[i])
            {
                // No valid metadata found; start fresh.
                return;
            }
        }

        _oldestActiveEpoch = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
        _currentEpoch = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16));
        _lastScanPosition = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
        _totalBlocksReclaimed = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32));
        _totalEpochsAdvanced = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(40));

        long advanceTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(48));
        _lastAdvanceTime = advanceTicks > 0
            ? new DateTimeOffset(advanceTicks, TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        long vacuumTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(56));
        _lastVacuumTime = vacuumTicks > 0
            ? new DateTimeOffset(vacuumTicks, TimeSpan.Zero)
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Persists current epoch state to the metadata block on disk.
    /// This is the single superblock write that makes epoch advancement durable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task PersistMetadataAsync(CancellationToken ct)
    {
        var buffer = new byte[_blockSize];

        // Write magic
        MetadataMagic.CopyTo(buffer.AsSpan(0));

        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), Interlocked.Read(ref _oldestActiveEpoch));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16), Interlocked.Read(ref _currentEpoch));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), Interlocked.Read(ref _lastScanPosition));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), Interlocked.Read(ref _totalBlocksReclaimed));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(40), Interlocked.Read(ref _totalEpochsAdvanced));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(48), _lastAdvanceTime.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(56), _lastVacuumTime.UtcTicks);

        // Single block write — this is the O(1) durable commit of the epoch fence.
        await _device.WriteBlockAsync(MetadataBlockIndex, buffer.AsMemory(), ct);
        await _device.FlushAsync(ct);
    }

    // ── Epoch Advancement (O(1) bulk delete) ────────────────────────────────

    /// <summary>
    /// Advances <c>OldestActiveEpoch</c> to <paramref name="newOldestEpoch"/>, logically
    /// deleting all data blocks written in epochs strictly less than the new value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the core O(1) bulk-delete operation. Instead of issuing N individual block
    /// frees, a single superblock write advances the epoch fence. Physical reclamation is
    /// deferred to Background Vacuum TRLR scan cycles.
    /// </para>
    /// <para>
    /// The estimated reclaim count is calculated from the fraction of total blocks
    /// proportional to the epoch gap advanced.
    /// </para>
    /// </remarks>
    /// <param name="newOldestEpoch">New OldestActiveEpoch value. Must be strictly greater than
    /// the current value and at most equal to <see cref="CurrentEpoch"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Details of the advancement and estimated reclaim scope.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="newOldestEpoch"/> is not strictly
    /// greater than the current OldestActiveEpoch or exceeds CurrentEpoch.</exception>
    public async Task<EpochAdvanceResult> AdvanceOldestEpochAsync(long newOldestEpoch, CancellationToken ct = default)
    {
        long current = Interlocked.Read(ref _oldestActiveEpoch);
        long globalEpoch = Interlocked.Read(ref _currentEpoch);

        if (newOldestEpoch <= current)
            throw new ArgumentException(
                $"newOldestEpoch ({newOldestEpoch}) must be strictly greater than the current OldestActiveEpoch ({current}).",
                nameof(newOldestEpoch));

        if (newOldestEpoch > globalEpoch)
            throw new ArgumentException(
                $"newOldestEpoch ({newOldestEpoch}) cannot exceed CurrentEpoch ({globalEpoch}). " +
                "Cannot logically delete future data.",
                nameof(newOldestEpoch));

        // Estimate blocks to reclaim proportional to epoch gap
        long epochGap = newOldestEpoch - current;
        long epochRange = Math.Max(1L, globalEpoch);
        long totalBlocks = _allocator.TotalBlockCount;
        long estimatedBlocks = (long)((double)epochGap / epochRange * totalBlocks);
        long estimatedInodes = estimatedBlocks / 8; // rough estimate: 1 inode per 8 data blocks

        // --- Single superblock write: this is the O(1) bulk delete operation ---
        Interlocked.Exchange(ref _oldestActiveEpoch, newOldestEpoch);

        // Reset scan position so vacuum re-scans from the beginning after epoch advance
        Interlocked.Exchange(ref _lastScanPosition, 0L);

        // Update statistics
        Interlocked.Increment(ref _totalEpochsAdvanced);
        _lastAdvanceTime = DateTimeOffset.UtcNow;

        // Persist to superblock (one block write — durable O(1) commit)
        await PersistMetadataAsync(ct);

        return new EpochAdvanceResult
        {
            PreviousOldestEpoch = current,
            NewOldestEpoch = newOldestEpoch,
            EstimatedBlocksToReclaim = estimatedBlocks,
            EstimatedInodesAffected = estimatedInodes,
        };
    }

    /// <summary>
    /// Advances the current global epoch counter by 1. Should be called when moving
    /// to a new time window (e.g. once per epoch duration).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new global epoch value.</returns>
    public async Task<long> AdvanceCurrentEpochAsync(CancellationToken ct = default)
    {
        long newEpoch = Interlocked.Increment(ref _currentEpoch);
        await PersistMetadataAsync(ct);
        return newEpoch;
    }

    // ── Background Vacuum TRLR Scan ─────────────────────────────────────────

    /// <summary>
    /// Runs one incremental Background Vacuum TRLR scan cycle. Examines up to
    /// <paramref name="maxBlocksPerCycle"/> data blocks, frees those whose
    /// <see cref="UniversalBlockTrailer.GenerationNumber"/> maps to an epoch strictly
    /// less than <c>OldestActiveEpoch</c>, and updates the scan position for
    /// resumption in the next cycle.
    /// </summary>
    /// <param name="maxBlocksPerCycle">Maximum number of blocks to examine per cycle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reclaim statistics for this cycle.</returns>
    public async Task<LazyReclaimResult> VacuumScanAsync(int maxBlocksPerCycle, CancellationToken ct = default)
    {
        if (maxBlocksPerCycle <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBlocksPerCycle), "Must be positive.");

        long startPos = Interlocked.Read(ref _lastScanPosition);
        long totalBlockCount = _device.BlockCount;

        if (totalBlockCount <= 1)
        {
            // Only the metadata block; nothing to scan.
            return new LazyReclaimResult { ScanProgress = 1.0, Duration = TimeSpan.Zero };
        }

        long scannableBlocks = totalBlockCount - 1; // Exclude block 0 (metadata)
        long startScan = DateTimeOffset.UtcNow.Ticks;

        long blocksScanned = 0;
        long blocksReclaimed = 0;
        long currentOldest = Interlocked.Read(ref _oldestActiveEpoch);

        var buffer = new byte[_blockSize];

        for (int i = 0; i < maxBlocksPerCycle; i++)
        {
            // Wrap around: TRLR scan is circular
            long blockIndex = 1L + ((startPos + i) % scannableBlocks);

            ct.ThrowIfCancellationRequested();

            // Skip blocks that are not allocated (no need to read free blocks)
            if (!_allocator.IsAllocated(blockIndex))
            {
                blocksScanned++;
                continue;
            }

            try
            {
                await _device.ReadBlockAsync(blockIndex, buffer.AsMemory(), ct);
            }
            catch (Exception)
            {
                // Skip unreadable blocks; don't halt the vacuum cycle.
                blocksScanned++;
                continue;
            }

            // Read the Universal Block Trailer from the last 16 bytes of the block
            if (_blockSize >= UniversalBlockTrailer.Size)
            {
                var trailer = UniversalBlockTrailer.Read(buffer.AsSpan(), _blockSize);

                // GenerationNumber in the TRLR encodes the epoch at which this block was written.
                // If that epoch is below OldestActiveEpoch, the block is logically deleted.
                long blockEpoch = trailer.GenerationNumber; // uint -> long widening

                if (blockEpoch < currentOldest)
                {
                    // Block is logically dead — reclaim it
                    _allocator.FreeBlock(blockIndex);
                    Interlocked.Increment(ref _totalBlocksReclaimed);
                    blocksReclaimed++;
                }
            }

            blocksScanned++;
        }

        long newScanPos = startPos + blocksScanned;
        Interlocked.Exchange(ref _lastScanPosition, newScanPos % scannableBlocks);

        double progress = scannableBlocks > 0
            ? Math.Min(1.0, (double)newScanPos / scannableBlocks)
            : 1.0;

        _lastVacuumTime = DateTimeOffset.UtcNow;

        long endScan = DateTimeOffset.UtcNow.Ticks;
        var duration = TimeSpan.FromTicks(endScan - startScan);

        return new LazyReclaimResult
        {
            BlocksScanned = blocksScanned,
            BlocksReclaimed = blocksReclaimed,
            BytesReclaimed = blocksReclaimed * _blockSize,
            ScanProgress = progress,
            Duration = duration,
        };
    }

    // ── Retention-Driven Auto-Advancement ───────────────────────────────────

    /// <summary>
    /// Automatically advances <c>OldestActiveEpoch</c> based on registered retention policies.
    /// Queries <see cref="RetentionEpochMapper.GetOldestRetentionEpoch"/> and advances the
    /// fence if the computed epoch exceeds the current <c>OldestActiveEpoch</c>.
    /// </summary>
    /// <param name="epochDuration">Wall-clock duration of one epoch (e.g. 1 minute).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="EpochAdvanceResult"/> if an advancement occurred, or <c>null</c> if no
    /// advancement was necessary (epoch fence already at or beyond the retention cutoff).
    /// </returns>
    public async Task<EpochAdvanceResult?> AutoAdvanceFromRetentionAsync(TimeSpan epochDuration, CancellationToken ct = default)
    {
        if (epochDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(epochDuration), "Epoch duration must be positive.");

        long globalEpoch = Interlocked.Read(ref _currentEpoch);
        long targetEpoch = _retentionMapper.GetOldestRetentionEpoch(globalEpoch, epochDuration);

        if (targetEpoch < 0)
        {
            // No auto-delete policies registered.
            return null;
        }

        long currentOldest = Interlocked.Read(ref _oldestActiveEpoch);

        if (targetEpoch <= currentOldest)
        {
            // Already at or past the retention cutoff; no advancement needed.
            return null;
        }

        // Clamp to CurrentEpoch to prevent advancing past live data
        long safeTarget = Math.Min(targetEpoch, globalEpoch);

        if (safeTarget <= currentOldest)
            return null;

        return await AdvanceOldestEpochAsync(safeTarget, ct);
    }

    // ── Continuous Background Mode ───────────────────────────────────────────

    /// <summary>
    /// Runs the combined retention-advance + vacuum loop continuously until
    /// <paramref name="ct"/> is cancelled or this instance is disposed.
    /// </summary>
    /// <remarks>
    /// Each cycle:
    /// <list type="number">
    ///   <item>Calls <see cref="AutoAdvanceFromRetentionAsync"/> to enforce retention policies.</item>
    ///   <item>Calls <see cref="VacuumScanAsync"/> to lazily reclaim pre-epoch blocks.</item>
    ///   <item>Waits for <paramref name="vacuumInterval"/> before the next cycle.</item>
    /// </list>
    /// Errors within a cycle are swallowed; the loop retries on the next interval.
    /// </remarks>
    /// <param name="vacuumInterval">Delay between vacuum cycles.</param>
    /// <param name="epochDuration">Wall-clock duration of one epoch.</param>
    /// <param name="ct">External cancellation token; combined with the internal disposal token.</param>
    public async Task RunAsync(TimeSpan vacuumInterval, TimeSpan epochDuration, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var linkedCt = linked.Token;

        int defaultBlocksPerCycle = (int)Math.Max(256, _device.BlockCount / 100);

        while (!linkedCt.IsCancellationRequested)
        {
            try
            {
                // Step 1: Advance epoch fence from retention policies (O(1) bulk delete)
                await AutoAdvanceFromRetentionAsync(epochDuration, linkedCt);

                // Step 2: Lazily reclaim physically freed blocks via TRLR scan
                await VacuumScanAsync(defaultBlocksPerCycle, linkedCt);
            }
            catch (OperationCanceledException) when (linkedCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Swallow per-cycle errors; vacuum will retry next cycle.
            }

            try
            {
                await Task.Delay(vacuumInterval, linkedCt);
            }
            catch (OperationCanceledException) when (linkedCt.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Starts <see cref="RunAsync"/> as a fire-and-forget background task.
    /// </summary>
    /// <param name="vacuumInterval">Delay between vacuum cycles.</param>
    /// <param name="epochDuration">Wall-clock duration of one epoch.</param>
    /// <param name="ct">External cancellation token.</param>
    public void Start(TimeSpan vacuumInterval, TimeSpan epochDuration, CancellationToken ct = default)
    {
        _backgroundTask = Task.Run(() => RunAsync(vacuumInterval, epochDuration, ct), CancellationToken.None);
    }

    // ── Statistics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of epoch deletion statistics.
    /// </summary>
    public EpochDeletionStats GetStats()
    {
        long scanPos = Interlocked.Read(ref _lastScanPosition);
        long totalBlocks = Math.Max(1L, _device.BlockCount - 1);
        double progress = Math.Min(1.0, (double)scanPos / totalBlocks);

        return new EpochDeletionStats
        {
            OldestActiveEpoch = Interlocked.Read(ref _oldestActiveEpoch),
            CurrentEpoch = Interlocked.Read(ref _currentEpoch),
            TotalBlocksReclaimed = Interlocked.Read(ref _totalBlocksReclaimed),
            TotalEpochsAdvanced = Interlocked.Read(ref _totalEpochsAdvanced),
            VacuumScanProgress = progress,
            LastAdvanceTime = _lastAdvanceTime,
            LastVacuumTime = _lastVacuumTime,
        };
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation.
            }
        }

        _cts.Dispose();
    }
}
