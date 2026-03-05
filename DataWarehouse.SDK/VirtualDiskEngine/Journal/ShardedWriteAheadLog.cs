using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Per-shard utilization statistics for monitoring and observability.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-29: Thread-affinity sharded WAL")]
public readonly struct WalShardStats
{
    /// <summary>Shard index.</summary>
    public int ShardId { get; init; }

    /// <summary>Current LSN in this shard.</summary>
    public long CurrentLsn { get; init; }

    /// <summary>Number of entries buffered in the ring but not yet flushed.</summary>
    public int PendingEntries { get; init; }
}

/// <summary>
/// Write-ahead log that distributes journal entries across N shards by thread affinity
/// (one shard per CPU core, up to 32 shards).  Each thread is permanently pinned to a shard
/// on its first call, eliminating lock contention between threads.
/// Shard 0 acts as the commit barrier shard, writing barrier entries that reference all
/// shard LSNs for crash recovery.
/// </summary>
/// <remarks>
/// Drop-in replacement for <see cref="WriteAheadLog"/> implementing <see cref="IWriteAheadLog"/>.
/// Thread affinity is per-process-lifetime: once a thread is assigned a shard it never
/// migrates.  This is intentional — it maximises cache locality and eliminates false sharing.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-29: Thread-affinity sharded WAL")]
public sealed class ShardedWriteAheadLog : IWriteAheadLog, IAsyncDisposable
{
    /// <summary>Gets the device for diagnostic access.</summary>
    internal IBlockDevice Device { get; }
    private readonly int _shardCount;
    private readonly long _walTotalBlocks;
    private readonly WalShard[] _shards;
    private readonly ShardCommitBarrier _barrier;

    private long _nextTransactionId;
    private bool _disposed;

    // -------------------------------------------------------------------------
    // Thread-affinity: assign each thread to a shard permanently on first use.
    // ConcurrentDictionary keyed by ManagedThreadId avoids the S2696 Sonar error
    // that arises from writing to [ThreadStatic] fields in instance methods.
    // -------------------------------------------------------------------------

    // Counter used to round-robin shard assignment across threads.
    private int _nextShardAssignment;

    // Maps ManagedThreadId -> assigned shard index (permanent for session lifetime).
    private readonly ConcurrentDictionary<int, int> _threadShardMap = new();

    // -------------------------------------------------------------------------
    // IWriteAheadLog properties
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public long CurrentSequenceNumber => _shards.Max(s => s.CurrentLsn);

    /// <inheritdoc/>
    public long WalSizeBlocks => _walTotalBlocks;

    /// <inheritdoc/>
    public double WalUtilization
    {
        get
        {
            if (_shards.Length == 0) return 0.0;
            double sum = _shards.Sum(s => (double)s.PendingEntryCount);
            // Normalise: total pending entries / (shardCount * ringCapacity).
            // Ring capacity is not directly exposed; use pending count as a proxy.
            return sum / (_shards.Length * 4096.0);
        }
    }

    /// <inheritdoc/>
    public bool NeedsRecovery
    {
        get { return _shards.Any(s => s.PendingEntryCount > 0 || s.CurrentLsn > 0); }
    }

    /// <summary>
    /// Gets the number of active shards.
    /// </summary>
    public int ShardCount => _shardCount;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a new sharded WAL.
    /// </summary>
    /// <param name="device">Underlying block device that owns the WAL region.</param>
    /// <param name="walStartBlock">First block of the WAL region (absolute).</param>
    /// <param name="walTotalBlocks">Total blocks allocated to the WAL region.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="shardCount">
    ///   Number of shards to create.  Pass 0 to auto-detect:
    ///   <c>Math.Min(Environment.ProcessorCount, 32)</c>.
    ///   Shard 0 receives extra blocks for barrier entries.
    /// </param>
    public ShardedWriteAheadLog(IBlockDevice device, long walStartBlock, long walTotalBlocks,
                                int blockSize, int shardCount = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(walTotalBlocks, 2L);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(shardCount);

        Device = device;
        _walTotalBlocks = walTotalBlocks;

        // Auto-detect shard count
        _shardCount = shardCount == 0
            ? Math.Min(Environment.ProcessorCount, 32)
            : shardCount;

        if (_shardCount < 1) _shardCount = 1;

        // Divide walTotalBlocks evenly. Shard 0 gets the remainder blocks (for barriers).
        long baseBlocksPerShard = walTotalBlocks / _shardCount;
        long extraBlocksForShard0 = walTotalBlocks - (baseBlocksPerShard * _shardCount);

        _shards = new WalShard[_shardCount];
        long currentStart = walStartBlock;

        for (int i = 0; i < _shardCount; i++)
        {
            long blocks = (i == 0)
                ? baseBlocksPerShard + extraBlocksForShard0
                : baseBlocksPerShard;

            if (blocks < 1) blocks = 1;

            _shards[i] = new WalShard(i, device, currentStart, blocks, blockSize);
            currentStart += blocks;
        }

        _barrier = new ShardCommitBarrier(_shards);
        _nextTransactionId = 1;
        _nextShardAssignment = 0;
    }

    // -------------------------------------------------------------------------
    // IWriteAheadLog implementation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<WalTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long txId = Interlocked.Increment(ref _nextTransactionId) - 1;
        return Task.FromResult(new WalTransaction(txId, this));
    }

    /// <inheritdoc/>
    public Task AppendEntryAsync(JournalEntry entry, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);

        int shardIdx = GetShardForCurrentThread();
        _shards[shardIdx].Append(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Flush ALL shards in parallel
        await Task.WhenAll(_shards.Select(s => s.FlushAsync(ct)));

        // Write commit barrier on Shard 0 once all shards are flushed
        await _barrier.CommitBarrierAsync(Interlocked.Read(ref _nextTransactionId), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JournalEntry>> ReplayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Find last barrier on Shard 0
        ShardCommitBarrier.BarrierRecord? barrierRecord = await _barrier.FindLastBarrierAsync(ct);

        // 2. Reconcile all shards to barrier point (discard entries past barrier)
        if (barrierRecord.HasValue)
        {
            await _barrier.ReconcileShardsAsync(barrierRecord.Value, _shards, ct);
        }

        // 3. Replay all shards
        Task<IReadOnlyList<JournalEntry>>[] replayTasks =
            _shards.Select(s => s.ReplayAsync(ct)).ToArray();

        await Task.WhenAll(replayTasks);

        // 4. Merge all entries by sequence number
        var merged = new List<JournalEntry>();
        foreach (var task in replayTasks)
        {
            merged.AddRange(task.Result);
        }

        merged.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));

        // Filter to entries at or below barrier LSN (if barrier exists)
        if (barrierRecord.HasValue)
        {
            long maxBarrierLsn = barrierRecord.Value.ShardLsns.Max();
            merged.RemoveAll(e => e.SequenceNumber > maxBarrierLsn);
        }

        return merged;
    }

    /// <inheritdoc/>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Flush all shards
        await Task.WhenAll(_shards.Select(s => s.FlushAsync(ct)));

        // Write barrier as checkpoint anchor
        await _barrier.CommitBarrierAsync(Interlocked.Read(ref _nextTransactionId), ct);
    }

    // -------------------------------------------------------------------------
    // Extended API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns per-shard utilization statistics for monitoring.
    /// </summary>
    public WalShardStats[] GetShardStats()
    {
        var stats = new WalShardStats[_shards.Length];
        for (int i = 0; i < _shards.Length; i++)
        {
            stats[i] = new WalShardStats
            {
                ShardId = _shards[i].ShardId,
                CurrentLsn = _shards[i].CurrentLsn,
                PendingEntries = _shards[i].PendingEntryCount
            };
        }
        return stats;
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Flush pending entries before disposal
            await Task.WhenAll(_shards.Select(s => s.FlushAsync()));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ShardedWriteAheadLog.DisposeAsync] Flush failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the shard index pinned to the current thread.
    /// On first call by a thread, permanently assigns it a shard via round-robin.
    /// </summary>
    private int GetShardForCurrentThread()
    {
        int threadId = Environment.CurrentManagedThreadId;
        return _threadShardMap.GetOrAdd(threadId, _ =>
        {
            int raw = Interlocked.Increment(ref _nextShardAssignment) % _shardCount;
            return raw < 0 ? raw + _shardCount : raw;
        });
    }
}
