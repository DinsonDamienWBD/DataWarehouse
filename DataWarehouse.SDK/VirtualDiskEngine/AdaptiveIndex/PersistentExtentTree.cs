using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Extends the in-memory <see cref="ExtentTree"/> with durable persistence to VDE blocks.
/// Supports full and incremental checkpointing, WAL protection, and crash recovery
/// by serializing extent tree state to dedicated VDE blocks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-15 Persistent extent tree")]
public sealed class PersistentExtentTree : IAsyncDisposable
{
    // ── Block format magic numbers ───────────────────────────────────────

    /// <summary>Magic bytes for full checkpoint blocks: "EXTR" (0x45585452).</summary>
    private const uint MagicFullCheckpoint = 0x45585452;

    /// <summary>Magic bytes for delta (incremental) checkpoint blocks: "EXTD" (0x45585444).</summary>
    private const uint MagicDeltaCheckpoint = 0x45585444;

    /// <summary>Current serialization format version.</summary>
    private const ushort FormatVersion = 1;

    /// <summary>Size of per-extent entry: StartBlock(8) + Count(4) = 12 bytes.</summary>
    private const int ExtentEntrySize = 12;

    /// <summary>Full block header: Magic(4) + Version(2) + ExtentCount(4) + NextBlock(8) = 18 bytes.</summary>
    private const int FullBlockHeaderSize = 18;

    /// <summary>Delta block header: Magic(4) + DeltaCount(4) = 8 bytes.</summary>
    private const int DeltaBlockHeaderSize = 8;

    /// <summary>XxHash64 checksum size in bytes.</summary>
    private const int ChecksumSize = 8;

    // ── Dependencies ─────────────────────────────────────────────────────

    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly long _regionStartBlock;
    private readonly int _blockSize;
    private readonly ILogger _logger;

    // ── Inner tree + synchronization ─────────────────────────────────────

    private readonly ExtentTree _inner = new();
    private readonly ReaderWriterLockSlim _treeLock = new();
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);

    // ── Dirty tracking + incremental changes ─────────────────────────────

    private volatile bool _dirty;
    private readonly ConcurrentQueue<ExtentDelta> _changeLog = new();
    private long _lastFullCheckpointBlock;
    private long _lastDeltaBlock;
    private DateTime _lastCheckpointTime = DateTime.UtcNow;

    // ── Auto-checkpoint policy ───────────────────────────────────────────

    private Timer? _autoCheckpointTimer;
    private volatile bool _disposed;

    /// <summary>
    /// Configuration for automatic checkpoint triggers.
    /// </summary>
    public CheckpointPolicy Policy { get; set; } = new();

    /// <summary>
    /// Gets the number of free extents in the tree.
    /// </summary>
    public int ExtentCount
    {
        get
        {
            _treeLock.EnterReadLock();
            try { return _inner.ExtentCount; }
            finally { _treeLock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Gets whether the tree has unsaved mutations since the last checkpoint.
    /// </summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// Gets the number of pending delta changes since the last checkpoint.
    /// </summary>
    public int PendingDeltaCount => _changeLog.Count;

    /// <summary>
    /// Creates a new persistent extent tree backed by VDE blocks.
    /// </summary>
    /// <param name="device">Block device for reading/writing checkpoint blocks.</param>
    /// <param name="allocator">Block allocator for obtaining new checkpoint blocks.</param>
    /// <param name="wal">Write-ahead log for crash-safe checkpoint writes.</param>
    /// <param name="regionStartBlock">First block in the region dedicated to extent tree persistence.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="logger">Optional logger.</param>
    public PersistentExtentTree(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        long regionStartBlock,
        int blockSize,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentOutOfRangeException.ThrowIfNegative(regionStartBlock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, 0);

        _device = device;
        _allocator = allocator;
        _wal = wal;
        _regionStartBlock = regionStartBlock;
        _blockSize = blockSize;
        _logger = logger ?? NullLogger.Instance;
        _lastFullCheckpointBlock = regionStartBlock;
        _lastDeltaBlock = -1;
    }

    // ── Tree operations (delegated to inner ExtentTree) ──────────────────

    /// <summary>
    /// Adds a free extent, merging with adjacent extents if possible.
    /// </summary>
    public void AddFreeExtent(long start, int count)
    {
        _treeLock.EnterWriteLock();
        try
        {
            _inner.AddFreeExtent(start, count);
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }

        _changeLog.Enqueue(new ExtentDelta(DeltaOperation.AddFree, start, count));
        _dirty = true;
    }

    /// <summary>
    /// Finds the smallest extent that satisfies the allocation request (best-fit).
    /// </summary>
    public FreeExtent? FindExtent(int minBlocks)
    {
        _treeLock.EnterReadLock();
        try
        {
            return _inner.FindExtent(minBlocks);
        }
        finally
        {
            _treeLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes an extent from the tree (marks as allocated).
    /// </summary>
    public void RemoveExtent(FreeExtent extent)
    {
        _treeLock.EnterWriteLock();
        try
        {
            _inner.RemoveExtent(extent);
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }

        _changeLog.Enqueue(new ExtentDelta(DeltaOperation.RemoveFree, extent.StartBlock, extent.BlockCount));
        _dirty = true;
    }

    /// <summary>
    /// Splits an extent: allocates from the beginning, returns the remainder.
    /// </summary>
    public FreeExtent? SplitExtent(FreeExtent extent, int allocatedBlocks)
    {
        FreeExtent? remainder;
        _treeLock.EnterWriteLock();
        try
        {
            remainder = _inner.SplitExtent(extent, allocatedBlocks);
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }

        _changeLog.Enqueue(new ExtentDelta(DeltaOperation.RemoveFree, extent.StartBlock, allocatedBlocks));
        if (remainder != null)
        {
            _changeLog.Enqueue(new ExtentDelta(DeltaOperation.AddFree, remainder.StartBlock, remainder.BlockCount));
        }
        _dirty = true;

        return remainder;
    }

    /// <summary>
    /// Builds the extent tree from a bitmap.
    /// </summary>
    public void BuildFromBitmap(byte[] bitmap, long totalBlocks)
    {
        _treeLock.EnterWriteLock();
        try
        {
            _inner.BuildFromBitmap(bitmap, totalBlocks);
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }

        _dirty = true;
    }

    // ── Full checkpoint ──────────────────────────────────────────────────

    /// <summary>
    /// Performs a full checkpoint: serializes the entire extent tree state to VDE blocks.
    /// WAL-protected for crash safety. Clears the delta chain on success.
    /// </summary>
    /// <remarks>
    /// Block format: [MagicEXTR:4][Version:2][ExtentCount:4][Extents:(StartBlock:8, Count:4)*N][XxHash64:8]
    /// Multi-block chaining via next-block pointer in header when extents exceed one block capacity.
    /// </remarks>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        await _checkpointLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Snapshot current state under read lock
            (long start, int count)[] snapshot;
            _treeLock.EnterReadLock();
            try
            {
                snapshot = SnapshotExtents();
            }
            finally
            {
                _treeLock.ExitReadLock();
            }

            // WAL-protect the checkpoint write
            await using var tx = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Serialize to blocks
            await WriteFullCheckpointBlocks(snapshot, ct).ConfigureAwait(false);

            // Flush WAL for durability
            await _wal.FlushAsync(ct).ConfigureAwait(false);

            // Clear delta chain and dirty flag
            while (_changeLog.TryDequeue(out _)) { }
            _lastDeltaBlock = -1;
            _dirty = false;
            _lastCheckpointTime = DateTime.UtcNow;

            _logger.LogDebug("Full checkpoint completed: {ExtentCount} extents at block {Block}",
                snapshot.Length, _lastFullCheckpointBlock);
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    private async Task WriteFullCheckpointBlocks((long start, int count)[] extents, CancellationToken ct)
    {
        int payloadPerBlock = _blockSize - FullBlockHeaderSize - ChecksumSize;
        int extentsPerBlock = payloadPerBlock / ExtentEntrySize;
        int totalBlocks = extents.Length == 0 ? 1 : (extents.Length + extentsPerBlock - 1) / extentsPerBlock;

        long currentBlock = _regionStartBlock;
        int extentOffset = 0;

        for (int blockIdx = 0; blockIdx < totalBlocks; blockIdx++)
        {
            byte[] buffer = new byte[_blockSize];
            int pos = 0;

            // Header
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), MagicFullCheckpoint);
            pos += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), FormatVersion);
            pos += 2;

            int extentsInThisBlock = Math.Min(extents.Length - extentOffset, extentsPerBlock);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos), extentsInThisBlock);
            pos += 4;

            // Next block pointer (0 = last block in chain)
            long nextBlock = blockIdx < totalBlocks - 1 ? currentBlock + 1 : 0;
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos), nextBlock);
            pos += 8;

            // Extent entries
            for (int i = 0; i < extentsInThisBlock; i++)
            {
                var (start, count) = extents[extentOffset + i];
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos), start);
                pos += 8;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos), count);
                pos += 4;
            }

            // XxHash64 checksum over everything before the checksum
            int checksumOffset = _blockSize - ChecksumSize;
            ulong checksum = SimdOperations.XxHash64Simd(buffer.AsSpan(0, checksumOffset), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(checksumOffset), checksum);

            // Write to device
            await _device.WriteBlockAsync(currentBlock, buffer, ct).ConfigureAwait(false);

            extentOffset += extentsInThisBlock;
            currentBlock++;
        }

        _lastFullCheckpointBlock = _regionStartBlock;
    }

    // ── Load from VDE blocks ─────────────────────────────────────────────

    /// <summary>
    /// Loads the extent tree from persisted VDE blocks. Validates XxHash64 checksum per block.
    /// On corruption, logs a warning and returns an empty tree (caller must rebuild from allocation bitmap).
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _treeLock.EnterWriteLock();
        try
        {
            // Clear existing state
            _inner.BuildFromBitmap(Array.Empty<byte>(), 0);

            long currentBlock = _regionStartBlock;
            bool hasMore = true;

            while (hasMore)
            {
                byte[] buffer = new byte[_blockSize];
                await _device.ReadBlockAsync(currentBlock, buffer, ct).ConfigureAwait(false);

                // Validate checksum
                int checksumOffset = _blockSize - ChecksumSize;
                ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(checksumOffset));
                ulong computedChecksum = SimdOperations.XxHash64Simd(buffer.AsSpan(0, checksumOffset), 0);

                if (storedChecksum != computedChecksum)
                {
                    _logger.LogWarning(
                        "Extent tree block {Block} checksum mismatch (stored: 0x{Stored:X16}, computed: 0x{Computed:X16}). Returning empty tree.",
                        currentBlock, storedChecksum, computedChecksum);
                    _inner.BuildFromBitmap(Array.Empty<byte>(), 0);
                    return;
                }

                // Validate magic
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0));
                if (magic != MagicFullCheckpoint)
                {
                    _logger.LogWarning(
                        "Extent tree block {Block} invalid magic 0x{Magic:X8}. Returning empty tree.",
                        currentBlock, magic);
                    _inner.BuildFromBitmap(Array.Empty<byte>(), 0);
                    return;
                }

                // Parse header
                int pos = 4;
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos));
                pos += 2;

                if (version != FormatVersion)
                {
                    _logger.LogWarning("Extent tree block {Block} unsupported version {Version}.", currentBlock, version);
                    _inner.BuildFromBitmap(Array.Empty<byte>(), 0);
                    return;
                }

                int extentCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos));
                pos += 4;

                long nextBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(pos));
                pos += 8;

                // Read extents
                for (int i = 0; i < extentCount; i++)
                {
                    long start = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(pos));
                    pos += 8;
                    int count = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos));
                    pos += 4;

                    _inner.AddFreeExtent(start, count);
                }

                hasMore = nextBlock != 0;
                currentBlock = nextBlock;
            }

            _lastFullCheckpointBlock = _regionStartBlock;
            _dirty = false;

            _logger.LogDebug("Extent tree loaded: {ExtentCount} extents from block {Block}",
                _inner.ExtentCount, _regionStartBlock);
        }
        finally
        {
            _treeLock.ExitWriteLock();
        }
    }

    // ── Incremental checkpoint ───────────────────────────────────────────

    /// <summary>
    /// Performs an incremental checkpoint: writes only the mutations (deltas) since the last checkpoint.
    /// Delta format: [MagicEXTD:4][DeltaCount:4][(Operation:1)(StartBlock:8)(Count:4)*N][XxHash64:8]
    /// Operation: 0 = AddFree, 1 = RemoveFree (allocated).
    /// </summary>
    public async Task IncrementalCheckpointAsync(CancellationToken ct = default)
    {
        await _checkpointLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drain all pending deltas
            var deltas = new System.Collections.Generic.List<ExtentDelta>();
            while (_changeLog.TryDequeue(out var delta))
            {
                deltas.Add(delta);
            }

            if (deltas.Count == 0)
            {
                _dirty = false;
                return;
            }

            // WAL-protect
            await using var tx = await _wal.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Allocate a block for the delta — validate the result before use.
            long deltaBlock = _allocator.AllocateBlock(ct);
            if (deltaBlock <= 0)
                throw new InvalidOperationException($"PersistentExtentTree: block allocator returned invalid block {deltaBlock} — out of space or corrupt allocator.");


            byte[] buffer = new byte[_blockSize];
            int pos = 0;

            // Header
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), MagicDeltaCheckpoint);
            pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos), deltas.Count);
            pos += 4;

            // Delta entries: Operation(1) + StartBlock(8) + Count(4) = 13 bytes each
            int maxDeltas = (_blockSize - DeltaBlockHeaderSize - ChecksumSize) / 13;
            int writtenCount = Math.Min(deltas.Count, maxDeltas);

            for (int i = 0; i < writtenCount; i++)
            {
                var d = deltas[i];
                buffer[pos] = (byte)d.Operation;
                pos += 1;
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos), d.StartBlock);
                pos += 8;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos), d.BlockCount);
                pos += 4;
            }

            // Update delta count if truncated
            if (writtenCount < deltas.Count)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), writtenCount);
            }

            // XxHash64 checksum
            int checksumOffset = _blockSize - ChecksumSize;
            ulong checksum = SimdOperations.XxHash64Simd(buffer.AsSpan(0, checksumOffset), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(checksumOffset), checksum);

            // Write to device
            await _device.WriteBlockAsync(deltaBlock, buffer, ct).ConfigureAwait(false);
            await _wal.FlushAsync(ct).ConfigureAwait(false);

            _lastDeltaBlock = deltaBlock;
            _dirty = false;
            _lastCheckpointTime = DateTime.UtcNow;

            _logger.LogDebug("Incremental checkpoint: {DeltaCount} deltas at block {Block}",
                writtenCount, deltaBlock);

            // If too many deltas were truncated, trigger a full checkpoint
            if (writtenCount < deltas.Count)
            {
                _logger.LogInformation("Delta overflow ({Total} deltas, {Written} written). Scheduling full checkpoint.",
                    deltas.Count, writtenCount);
                _dirty = true;
            }
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    // ── Crash recovery ───────────────────────────────────────────────────

    /// <summary>
    /// Performs crash recovery: loads the last full checkpoint, then replays any delta blocks.
    /// If delta blocks are corrupt (partial write during crash), discards deltas and uses the
    /// last full checkpoint only.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        // Step 1: Load full checkpoint
        await LoadAsync(ct).ConfigureAwait(false);

        // Step 2: Replay deltas if any exist
        if (_lastDeltaBlock > 0)
        {
            await ReplayDeltasAsync(_lastDeltaBlock, ct).ConfigureAwait(false);
        }

        _dirty = false;
        _logger.LogInformation("Extent tree recovery complete: {ExtentCount} extents", ExtentCount);
    }

    private async Task ReplayDeltasAsync(long deltaBlock, CancellationToken ct)
    {
        byte[] buffer = new byte[_blockSize];

        try
        {
            await _device.ReadBlockAsync(deltaBlock, buffer, ct).ConfigureAwait(false);

            // Validate checksum
            int checksumOffset = _blockSize - ChecksumSize;
            ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(checksumOffset));
            ulong computedChecksum = SimdOperations.XxHash64Simd(buffer.AsSpan(0, checksumOffset), 0);

            if (storedChecksum != computedChecksum)
            {
                _logger.LogWarning(
                    "Delta block {Block} checksum mismatch. Discarding deltas, using last full checkpoint.",
                    deltaBlock);
                return;
            }

            // Validate magic
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0));
            if (magic != MagicDeltaCheckpoint)
            {
                _logger.LogWarning(
                    "Delta block {Block} invalid magic 0x{Magic:X8}. Discarding deltas.",
                    deltaBlock, magic);
                return;
            }

            int pos = 4;
            int deltaCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos));
            pos += 4;

            _treeLock.EnterWriteLock();
            try
            {
                for (int i = 0; i < deltaCount; i++)
                {
                    var operation = (DeltaOperation)buffer[pos];
                    pos += 1;
                    long start = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(pos));
                    pos += 8;
                    int count = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos));
                    pos += 4;

                    switch (operation)
                    {
                        case DeltaOperation.AddFree:
                            _inner.AddFreeExtent(start, count);
                            break;
                        case DeltaOperation.RemoveFree:
                            var extent = _inner.FindExtent(count);
                            if (extent != null && extent.StartBlock == start && extent.BlockCount == count)
                            {
                                _inner.RemoveExtent(extent);
                            }
                            break;
                    }
                }
            }
            finally
            {
                _treeLock.ExitWriteLock();
            }

            _logger.LogDebug("Replayed {DeltaCount} deltas from block {Block}", deltaCount, deltaBlock);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replay delta block {Block}. Using last full checkpoint.", deltaBlock);
        }
    }

    // ── Auto-checkpoint timer ────────────────────────────────────────────

    /// <summary>
    /// Starts the automatic checkpoint background timer.
    /// Triggers a checkpoint when either the change threshold or time threshold is exceeded.
    /// </summary>
    public void StartAutoCheckpoint()
    {
        _autoCheckpointTimer?.Dispose();
        _autoCheckpointTimer = new Timer(
            AutoCheckpointCallback,
            null,
            Policy.TimeThreshold,
            Policy.TimeThreshold);
    }

    /// <summary>
    /// Stops the automatic checkpoint background timer.
    /// </summary>
    public void StopAutoCheckpoint()
    {
        _autoCheckpointTimer?.Dispose();
        _autoCheckpointTimer = null;
    }

    private async void AutoCheckpointCallback(object? state)
    {
        if (_disposed) return;

        bool shouldCheckpoint = _dirty &&
            (_changeLog.Count >= Policy.ChangeThreshold ||
             DateTime.UtcNow - _lastCheckpointTime >= Policy.TimeThreshold);

        if (!shouldCheckpoint) return;

        try
        {
            if (_changeLog.Count > Policy.ChangeThreshold * 2)
            {
                // Too many deltas — do a full checkpoint
                await CheckpointAsync().ConfigureAwait(false);
            }
            else
            {
                await IncrementalCheckpointAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-checkpoint failed");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private (long start, int count)[] SnapshotExtents()
    {
        // Walk the inner tree by attempting to find extents of all sizes
        // We rebuild from the tree by scanning through known extents
        var result = new System.Collections.Generic.List<(long, int)>();
        int extentCount = _inner.ExtentCount;

        if (extentCount == 0) return Array.Empty<(long, int)>();

        // Use FindExtent with minBlocks=1 to get the smallest, then remove and re-add
        // This is a snapshot approach: we temporarily extract all, then put them back
        var extracted = new System.Collections.Generic.List<FreeExtent>();

        while (true)
        {
            var extent = _inner.FindExtent(1);
            if (extent == null) break;
            extracted.Add(extent);
            _inner.RemoveExtent(extent);
        }

        foreach (var ext in extracted)
        {
            result.Add((ext.StartBlock, ext.BlockCount));
            _inner.AddFreeExtent(ext.StartBlock, ext.BlockCount);
        }

        return result.ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopAutoCheckpoint();

        if (_dirty)
        {
            try
            {
                await CheckpointAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to checkpoint on dispose");
            }
        }

        _treeLock.Dispose();
        _checkpointLock.Dispose();
    }
}

/// <summary>
/// Represents a mutation delta for incremental checkpointing.
/// </summary>
internal readonly record struct ExtentDelta(DeltaOperation Operation, long StartBlock, int BlockCount);

/// <summary>
/// Types of extent mutations tracked for incremental checkpoints.
/// </summary>
internal enum DeltaOperation : byte
{
    /// <summary>A free extent was added to the tree.</summary>
    AddFree = 0,

    /// <summary>A free extent was removed (allocated) from the tree.</summary>
    RemoveFree = 1
}

/// <summary>
/// Configuration for automatic checkpoint triggering thresholds.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-15 Checkpoint policy")]
public sealed class CheckpointPolicy
{
    /// <summary>
    /// Number of mutations before triggering an automatic checkpoint.
    /// Default: 1000.
    /// </summary>
    public int ChangeThreshold { get; set; } = 1000;

    /// <summary>
    /// Maximum time between checkpoints before triggering an automatic checkpoint.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan TimeThreshold { get; set; } = TimeSpan.FromSeconds(60);
}
