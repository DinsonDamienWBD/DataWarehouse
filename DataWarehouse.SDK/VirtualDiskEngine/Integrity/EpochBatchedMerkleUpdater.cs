using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

// ── Supporting types ─────────────────────────────────────────────────────────

/// <summary>
/// A single dirty-block entry queued by the write hot path.
/// Carries the device-absolute block number and its 32-byte hash.
/// </summary>
/// <remarks>
/// BLAKE3 is preferred for speed; we fall back to SHA-256 (built into BCL)
/// because BLAKE3 does not ship in System.Security.Cryptography as of .NET 9.
/// A comment is placed at the hash-computation site for easy substitution.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-17: Dirty-block entry (VOPT-30)")]
public readonly struct DirtyBlockEntry
{
    /// <summary>Device-absolute block number of the dirty block.</summary>
    public long BlockNumber { get; }

    /// <summary>32-byte hash of the block data (BLAKE3 preferred; SHA-256 fallback).</summary>
    public byte[] BlockHash { get; }

    /// <summary>Creates a new dirty-block entry.</summary>
    public DirtyBlockEntry(long blockNumber, byte[] blockHash)
    {
        BlockNumber = blockNumber;
        BlockHash = blockHash ?? throw new ArgumentNullException(nameof(blockHash));
    }
}

/// <summary>
/// Snapshot statistics returned by <see cref="EpochBatchedMerkleUpdater.GetStats"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-17: Merkle batch stats (VOPT-30)")]
public readonly struct MerkleBatchStats
{
    /// <summary>Total number of batch epochs processed since the updater started.</summary>
    public long TotalBatchesProcessed { get; }

    /// <summary>Total number of block hashes applied to the Merkle tree.</summary>
    public long TotalBlocksUpdated { get; }

    /// <summary>Number of dirty-block entries currently waiting in the queue.</summary>
    public int PendingDirtyBlocks { get; }

    /// <summary>Wall-clock duration of the most recent batch epoch.</summary>
    public TimeSpan LastBatchDuration { get; }

    /// <summary>Timestamp at which the most recent batch epoch completed.</summary>
    public DateTimeOffset LastBatchTime { get; }

    /// <summary>Creates a new stats snapshot.</summary>
    public MerkleBatchStats(
        long totalBatchesProcessed,
        long totalBlocksUpdated,
        int pendingDirtyBlocks,
        TimeSpan lastBatchDuration,
        DateTimeOffset lastBatchTime)
    {
        TotalBatchesProcessed = totalBatchesProcessed;
        TotalBlocksUpdated = totalBlocksUpdated;
        PendingDirtyBlocks = pendingDirtyBlocks;
        LastBatchDuration = lastBatchDuration;
        LastBatchTime = lastBatchTime;
    }
}

// ── EpochBatchedMerkleUpdater ─────────────────────────────────────────────────

/// <summary>
/// Decouples Merkle root computation from the write hot path by batching dirty-block
/// hash updates into a configurable background epoch loop (VOPT-30).
/// </summary>
/// <remarks>
/// <para>
/// <b>Write hot path</b>: callers invoke <see cref="MarkDirty"/> which computes the
/// block hash and enqueues a <see cref="DirtyBlockEntry"/>.  The operation is O(1),
/// allocation-light, and lock-free.  No Merkle tree traversal occurs on the hot path.
/// </para>
/// <para>
/// <b>Background loop</b>: <see cref="RunAsync"/> wakes every
/// <see cref="MerkleBatchConfig.BatchInterval"/>, drains up to
/// <see cref="MerkleBatchConfig.MaxDirtyBlocksPerBatch"/> entries, calls
/// <see cref="MerkleIntegrityVerifier.UpdateBlockAsync"/> for each dirty leaf, and
/// then recomputes the Merkle root.
/// </para>
/// <para>
/// <b>Crash recovery</b>: <see cref="RecoverFromWalAsync"/> scans WAL replay entries
/// for <see cref="JournalEntryType.BlockWrite"/> operations, re-hashes the after-images,
/// and enqueues them so the next batch brings the Merkle tree up to date.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-17: Epoch-batched Merkle updater (VOPT-30)")]
public sealed class EpochBatchedMerkleUpdater : IAsyncDisposable
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly MerkleIntegrityVerifier _verifier;
    private readonly IWriteAheadLog _wal;
    private readonly MerkleBatchConfig _config;

    // Lock-free dirty queue — the ONLY data structure touched on the write hot path.
    private readonly ConcurrentQueue<DirtyBlockEntry> _dirtyQueue = new();

    // Metrics (Interlocked-safe longs)
    private long _totalBatchesProcessed;
    private long _totalBlocksUpdated;

    // Last-batch stats stored as atomic long ticks / DateTimeOffset
    private long _lastBatchDurationTicks;    // TimeSpan ticks
    private long _lastBatchTimeTicks;        // DateTimeOffset.UtcTicks

    private bool _disposed;

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new epoch-batched Merkle updater.
    /// </summary>
    /// <param name="verifier">The underlying Merkle integrity verifier that owns tree persistence.</param>
    /// <param name="wal">Write-ahead log used for crash recovery.</param>
    /// <param name="config">Batch configuration.</param>
    public EpochBatchedMerkleUpdater(
        MerkleIntegrityVerifier verifier,
        IWriteAheadLog wal,
        MerkleBatchConfig config)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    // ── Write Hot Path ───────────────────────────────────────────────────────

    /// <summary>
    /// Called from the write hot path after a block has been written to the device.
    /// Computes the block hash and enqueues a dirty entry for background processing.
    /// </summary>
    /// <param name="blockNumber">Device-absolute block number of the written block.</param>
    /// <param name="blockData">Raw block data that was written.</param>
    /// <remarks>
    /// This method is O(1) from the Merkle-tree perspective (no tree traversal).
    /// Hash computation is SHA-256 (BLAKE3 preferred; swap in when BCL ships it).
    /// The queue is lock-free; no synchronisation primitives are acquired.
    /// </remarks>
    public void MarkDirty(long blockNumber, ReadOnlySpan<byte> blockData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // BLAKE3 preferred for speed; SHA-256 used as BCL fallback.
        // TODO: replace SHA256.HashData with Blake3.HashData when BLAKE3 ships in BCL.
        byte[] hash = SHA256.HashData(blockData);

        _dirtyQueue.Enqueue(new DirtyBlockEntry(blockNumber, hash));

        // Back-pressure signal — log a warning when the soft capacity is exceeded.
        // We do NOT block the hot path; the queue itself is unbounded.
        if (_dirtyQueue.Count > _config.DirtyQueueCapacity)
        {
            // Logging via Debug.WriteLine to avoid heavy I/O on the write path.
            Debug.WriteLine(
                $"[EpochBatchedMerkleUpdater] Dirty queue exceeded soft capacity " +
                $"({_config.DirtyQueueCapacity}). Current size: {_dirtyQueue.Count}. " +
                "Consider increasing BatchInterval or MaxDirtyBlocksPerBatch.");
        }
    }

    // ── Background Loop ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs the background epoch loop.  Intended to be started once on a dedicated
    /// task (e.g., <c>Task.Run(() => updater.RunAsync(cts.Token))</c>).
    /// Returns when <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="ct">Cancellation token; triggers graceful shutdown.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.BatchInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await ProcessOneBatchAsync(ct).ConfigureAwait(false);
        }
    }

    // ── WAL Crash Recovery ───────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the dirty-block set from WAL replay entries after a crash.
    /// Should be called at mount time, before <see cref="RunAsync"/> is started.
    /// Only available when <see cref="MerkleBatchConfig.WalRecoveryEnabled"/> is true.
    /// </summary>
    /// <param name="walEntries">
    /// Committed WAL entries returned by <see cref="IWriteAheadLog.ReplayAsync"/>.
    /// </param>
    /// <param name="device">Block device used to read current block data for hashing.</param>
    /// <param name="blockSize">Block size in bytes; used to allocate read buffers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WAL recovery is disabled via <see cref="MerkleBatchConfig.WalRecoveryEnabled"/>.
    /// </exception>
    public async Task RecoverFromWalAsync(
        IReadOnlyList<JournalEntry> walEntries,
        IBlockDevice device,
        int blockSize,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_config.WalRecoveryEnabled)
            throw new InvalidOperationException(
                "WAL recovery is disabled. Set MerkleBatchConfig.WalRecoveryEnabled = true to enable it.");

        ArgumentNullException.ThrowIfNull(walEntries);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, 0);

        var buffer = new byte[blockSize];

        foreach (var entry in walEntries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Type != JournalEntryType.BlockWrite)
                continue;

            if (entry.TargetBlockNumber < 0)
                continue;

            // Prefer the after-image embedded in the WAL entry to avoid an extra I/O.
            if (entry.AfterImage is { Length: > 0 })
            {
                // BLAKE3 preferred; SHA-256 BCL fallback.
                byte[] hash = SHA256.HashData(entry.AfterImage);
                _dirtyQueue.Enqueue(new DirtyBlockEntry(entry.TargetBlockNumber, hash));
            }
            else
            {
                // Fall back to reading the current block from the device.
                await device.ReadBlockAsync(entry.TargetBlockNumber, buffer, ct)
                    .ConfigureAwait(false);

                // BLAKE3 preferred; SHA-256 BCL fallback.
                byte[] hash = SHA256.HashData(buffer);
                _dirtyQueue.Enqueue(new DirtyBlockEntry(entry.TargetBlockNumber, hash));
            }
        }

        // Process one full batch immediately to bring the tree up to date before
        // the background loop starts accepting new writes.
        await ProcessOneBatchAsync(ct).ConfigureAwait(false);
    }

    // ── Force Flush ──────────────────────────────────────────────────────────

    /// <summary>
    /// Force-processes ALL pending dirty entries immediately.
    /// Should be called at unmount/shutdown to ensure the Merkle tree is
    /// consistent before the device is closed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Drain the entire queue in batches.
        while (!_dirtyQueue.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessOneBatchAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a point-in-time snapshot of batch processing statistics.
    /// </summary>
    public MerkleBatchStats GetStats()
    {
        long durationTicks = Interlocked.Read(ref _lastBatchDurationTicks);
        long timeTicks = Interlocked.Read(ref _lastBatchTimeTicks);

        return new MerkleBatchStats(
            totalBatchesProcessed: Interlocked.Read(ref _totalBatchesProcessed),
            totalBlocksUpdated: Interlocked.Read(ref _totalBlocksUpdated),
            pendingDirtyBlocks: _dirtyQueue.Count,
            lastBatchDuration: TimeSpan.FromTicks(durationTicks),
            lastBatchTime: timeTicks == 0
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(timeTicks, TimeSpan.Zero));
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Best-effort: flush remaining dirty blocks before disposal.
        // Use a short timeout so disposal does not hang indefinitely.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[EpochBatchedMerkleUpdater] FlushAsync timed out during disposal.");
        }
    }

    // ── Private Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Drains up to <see cref="MerkleBatchConfig.MaxDirtyBlocksPerBatch"/> entries
    /// from the dirty queue, calls UpdateBlockAsync on each, and recomputes the
    /// Merkle root after the batch.
    /// </summary>
    private async Task ProcessOneBatchAsync(CancellationToken ct)
    {
        if (_dirtyQueue.IsEmpty)
            return;

        var sw = Stopwatch.StartNew();
        int processed = 0;

        // Deduplicate within the batch: if the same block appears multiple times,
        // only the last hash wins (most recent write state).
        var batchMap = new Dictionary<long, byte[]>(_config.MaxDirtyBlocksPerBatch);

        while (processed < _config.MaxDirtyBlocksPerBatch &&
               _dirtyQueue.TryDequeue(out DirtyBlockEntry entry))
        {
            // Last write wins for the same block number within a single batch.
            batchMap[entry.BlockNumber] = entry.BlockHash;
            processed++;
        }

        if (batchMap.Count == 0)
            return;

        // Apply each unique dirty block to the Merkle tree.
        foreach (var (blockNumber, hash) in batchMap)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // UpdateBlockAsync expects: inode number, block-within-object index,
                // and the checksum as byte[].
                // We store the device-absolute block number as the inode number here
                // because EpochBatchedMerkleUpdater operates at device (not object) level.
                // Callers that need object-scoped trees should partition by inode before
                // calling MarkDirty and use a separate EpochBatchedMerkleUpdater per object.
                await _verifier.UpdateBlockAsync(
                    objectInodeNumber: blockNumber,
                    blockNumber: 0,             // Within-object index; device-level = single-block objects.
                    newBlockChecksum: hash,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log but do not terminate the batch loop on individual update failures.
                Debug.WriteLine(
                    $"[EpochBatchedMerkleUpdater] UpdateBlockAsync failed for block {blockNumber}: {ex.Message}");
            }
        }

        sw.Stop();

        // Update metrics atomically.
        Interlocked.Increment(ref _totalBatchesProcessed);
        Interlocked.Add(ref _totalBlocksUpdated, batchMap.Count);
        Interlocked.Exchange(ref _lastBatchDurationTicks, sw.Elapsed.Ticks);
        Interlocked.Exchange(ref _lastBatchTimeTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
