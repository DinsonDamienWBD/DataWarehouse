using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Manages MVCC transaction lifecycle including begin, commit, abort, and snapshot-based reads.
/// Integrates with the WAL for durability and the version store for old version persistence.
/// </summary>
/// <remarks>
/// Writers append WAL records with transaction ID. Readers acquire a snapshot (WAL sequence number)
/// and see only versions &lt;= that snapshot. Version chains link old versions through the
/// inode VersionChainHead pointer into the dedicated MVCC region.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC core (VOPT-12)")]
public sealed class MvccManager
{
    private readonly IWriteAheadLog _wal;
    private readonly MvccVersionStore _versionStore;
    private readonly IBlockDevice _device;
    private readonly int _blockSize;

    private long _nextTransactionId;

    /// <summary>
    /// Active transactions indexed by transaction ID.
    /// </summary>
    private readonly ConcurrentDictionary<long, MvccTransaction> _activeTransactions = new();

    /// <summary>
    /// Tracks which inodes were modified by which committed transactions (WAL-commit-sequence -> inode numbers).
    /// Used for Serializable conflict detection. Entries are pruned when no active transaction can observe them.
    /// </summary>
    private readonly ConcurrentDictionary<long, long[]> _committedWriteSets = new();

    /// <summary>
    /// Tracks version chain entries for snapshot-visible reads.
    /// Maps inodeNumber -> list of (WAL-commit-sequence, version-block-number) entries.
    /// </summary>
    private readonly ConcurrentDictionary<long, System.Collections.Generic.List<(long CommitSeq, long VersionBlock)>> _committedVersionChains = new();

    /// <summary>
    /// Default isolation level for new transactions when none is specified.
    /// </summary>
    public MvccIsolationLevel DefaultIsolationLevel { get; set; } = MvccIsolationLevel.ReadCommitted;

    /// <summary>
    /// Gets the minimum snapshot sequence across all active transactions.
    /// Used by garbage collection to determine which old versions can be reclaimed.
    /// Returns <see cref="long.MaxValue"/> when no transactions are active.
    /// </summary>
    public long OldestActiveSnapshot
    {
        get
        {
            long oldest = long.MaxValue;
            foreach (var kvp in _activeTransactions)
            {
                long snap = kvp.Value.SnapshotSequence;
                if (snap < oldest)
                {
                    oldest = snap;
                }
            }
            return oldest;
        }
    }

    /// <summary>
    /// Gets the number of currently active transactions.
    /// </summary>
    public int ActiveTransactionCount => _activeTransactions.Count;

    /// <summary>
    /// Creates a new MVCC manager.
    /// </summary>
    /// <param name="wal">Write-ahead log for durability.</param>
    /// <param name="versionStore">Store for old version data in the MVCC region.</param>
    /// <param name="device">Block device for reading/writing inode data.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public MvccManager(IWriteAheadLog wal, MvccVersionStore versionStore, IBlockDevice device, int blockSize)
    {
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _versionStore = versionStore ?? throw new ArgumentNullException(nameof(versionStore));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Begins a new MVCC transaction. Takes a snapshot of the current WAL sequence number
    /// and registers the transaction in the active set.
    /// </summary>
    /// <param name="level">Isolation level (defaults to <see cref="DefaultIsolationLevel"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new <see cref="MvccTransaction"/> handle.</returns>
    public Task<MvccTransaction> BeginAsync(MvccIsolationLevel? level = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        long txId = Interlocked.Increment(ref _nextTransactionId);
        long snapshot = _wal.CurrentSequenceNumber;
        var isolationLevel = level ?? DefaultIsolationLevel;

        var tx = new MvccTransaction(txId, snapshot, isolationLevel);
        _activeTransactions.TryAdd(txId, tx);

        return Task.FromResult(tx);
    }

    /// <summary>
    /// Commits an MVCC transaction. For each buffered write:
    /// 1. Stores the current (old) data as a version in the MVCC version store
    /// 2. Updates the inode's version chain head to point to the old version block
    /// 3. Writes the new data to the inode's data blocks
    /// 4. Appends a WAL commit record
    /// </summary>
    /// <param name="tx">Transaction to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction is not active or conflict detected.</exception>
    public async Task CommitAsync(MvccTransaction tx, CancellationToken ct = default)
    {
        ValidateActive(tx);

        // Serializable conflict detection: check if any inode in ReadSet was modified
        // by a concurrent committed transaction after our snapshot
        if (tx.IsolationLevel == MvccIsolationLevel.Serializable && tx.ReadSet.Count > 0)
        {
            foreach (var kvp in _committedWriteSets)
            {
                long commitWalSequence = kvp.Key; // WAL sequence number at commit time
                if (commitWalSequence > tx.SnapshotSequence)
                {
                    // This transaction committed after our snapshot (both are WAL sequence numbers)
                    long[] modifiedInodes = kvp.Value;
                    foreach (long modifiedInode in modifiedInodes)
                    {
                        if (tx.ReadSet.Contains(modifiedInode))
                        {
                            tx.State = TransactionState.Aborted;
                            _activeTransactions.TryRemove(tx.TransactionId, out _);
                            throw new InvalidOperationException(
                                $"Serializable conflict: inode {modifiedInode} was modified by a transaction " +
                                $"that committed at WAL sequence {commitWalSequence} after our snapshot {tx.SnapshotSequence}.");
                        }
                    }
                }
            }
        }

        // Process each buffered write
        foreach (var (inodeNumber, newData) in tx.WriteSet)
        {
            // 1. Read current data (old version) from the device
            var oldDataBuffer = new byte[_blockSize];
            await _device.ReadBlockAsync(inodeNumber, oldDataBuffer, ct);

            // 2. Store old version in the MVCC version store
            long oldVersionBlock = await _versionStore.StoreOldVersionAsync(
                inodeNumber, tx.TransactionId, oldDataBuffer, ct);

            tx.VersionChainEntries.Add((inodeNumber, oldVersionBlock));

            // 3. Write new data to the device
            var writeData = new byte[_blockSize];
            var copyLength = Math.Min(newData.Length, _blockSize);
            newData.AsSpan(0, copyLength).CopyTo(writeData);
            await _device.WriteBlockAsync(inodeNumber, writeData, ct);
        }

        // 4. Append WAL commit record
        var commitEntry = new JournalEntry
        {
            SequenceNumber = -1, // Assigned by WAL
            TransactionId = tx.TransactionId,
            Type = JournalEntryType.CommitTransaction,
            TargetBlockNumber = -1,
            BeforeImage = null,
            AfterImage = null
        };

        await _wal.AppendEntryAsync(commitEntry, ct);
        await _wal.FlushAsync(ct);

        // Record committed write set for Serializable conflict detection.
        // Key is the WAL commit sequence number (same unit as SnapshotSequence),
        // enabling correct ordering comparison in Serializable conflict detection.
        if (tx.WriteSet.Count > 0)
        {
            long commitSequence = _wal.CurrentSequenceNumber;
            _committedWriteSets.TryAdd(commitSequence, tx.WriteSet.Keys.ToArray());

            // Record version chain entries so snapshot reads can find historical versions
            foreach (var (inodeNum, versionBlock) in tx.VersionChainEntries)
            {
                _committedVersionChains.AddOrUpdate(
                    inodeNum,
                    _ => new System.Collections.Generic.List<(long, long)> { (commitSequence, versionBlock) },
                    (_, list) => { lock (list) { list.Add((commitSequence, versionBlock)); } return list; });
            }
        }

        // Remove from active set and mark committed
        _activeTransactions.TryRemove(tx.TransactionId, out _);
        tx.State = TransactionState.Committed;

        // Prune old committed write sets that no active transaction can observe
        PruneCommittedWriteSets();
    }

    /// <summary>
    /// Aborts an MVCC transaction. Discards all buffered writes and removes from active set.
    /// </summary>
    /// <param name="tx">Transaction to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AbortAsync(MvccTransaction tx, CancellationToken ct = default)
    {
        ValidateActive(tx);

        tx.WriteSet.Clear();
        tx.ReadSet.Clear();
        _activeTransactions.TryRemove(tx.TransactionId, out _);
        tx.State = TransactionState.Aborted;

        PruneCommittedWriteSets();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads data visible to the given transaction's snapshot from the specified inode block.
    /// For ReadCommitted: reads the latest committed version.
    /// For SnapshotIsolation: reads the version &lt;= tx.SnapshotSequence.
    /// Marks the inode in the transaction's read set for conflict detection.
    /// </summary>
    /// <param name="tx">Transaction performing the read.</param>
    /// <param name="inodeNumber">Inode (block) number to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The visible data, or null if no visible version exists.</returns>
    public async Task<byte[]?> ReadAsync(MvccTransaction tx, long inodeNumber, CancellationToken ct = default)
    {
        ValidateActive(tx);

        tx.MarkRead(inodeNumber);

        // Check transaction's own write buffer first (read-your-own-writes)
        if (tx.WriteSet.TryGetValue(inodeNumber, out byte[]? bufferedData))
        {
            return bufferedData;
        }

        // For ReadCommitted, the current on-disk data IS the latest committed version
        if (tx.IsolationLevel == MvccIsolationLevel.ReadCommitted)
        {
            var buffer = new byte[_blockSize];
            await _device.ReadBlockAsync(inodeNumber, buffer, ct);
            return buffer;
        }

        // For SnapshotIsolation and Serializable:
        // Check if any transaction committed after our snapshot has written to this inode.
        // If so, find the most-recent old-version block saved before our snapshot.
        var currentBuffer = new byte[_blockSize];
        await _device.ReadBlockAsync(inodeNumber, currentBuffer, ct);

        // Find the most-recent version block recorded for this inode by a transaction
        // that committed AFTER our snapshot (meaning current data is too new for us).
        // We search through all active transactions' VersionChainEntries for an older version.
        long bestVersionBlock = -1;
        long bestCommitSeq = -1;
        bool newerVersionExists = false;

        foreach (var kvp in _committedWriteSets)
        {
            long commitSeq = kvp.Key;
            if (commitSeq > tx.SnapshotSequence && kvp.Value.Contains(inodeNumber))
            {
                newerVersionExists = true;
                break;
            }
        }

        if (!newerVersionExists)
            return currentBuffer;

        // Search version chain entries from recently committed transactions to find
        // the correct historical snapshot. Iterate _committedVersionChains (if tracked)
        // or fall back to returning currentBuffer when version chain lookup is unavailable.
        // The version store read path uses inodeNumber -> version block recorded at commit.
        if (_committedVersionChains.TryGetValue(inodeNumber, out var chainEntries))
        {
            foreach (var (commitSeq, versionBlock) in chainEntries)
            {
                if (commitSeq <= tx.SnapshotSequence && commitSeq > bestCommitSeq)
                {
                    bestCommitSeq = commitSeq;
                    bestVersionBlock = versionBlock;
                }
            }
        }

        if (bestVersionBlock > 0)
        {
            var versionRecord = await _versionStore.ReadVersionAsync(bestVersionBlock, ct);
            if (versionRecord.HasValue)
                return versionRecord.Value.Data;
        }

        // Fallback: current data (best effort when version chain unavailable)
        return currentBuffer;
    }

    /// <summary>
    /// Returns the set of inode numbers that were modified by committed transactions
    /// whose transaction IDs fall in the range (sinceTransactionId, untilTransactionId].
    /// Used by <see cref="DataWarehouse.SDK.VirtualDiskEngine.Replication.ExtentDeltaReplicator"/>
    /// to compute replication deltas from the MVCC change log.
    /// </summary>
    /// <param name="sinceTransactionId">Exclusive lower bound (changes strictly after this ID).</param>
    /// <param name="untilTransactionId">Inclusive upper bound.</param>
    /// <returns>A deduplicated set of inode numbers modified in the specified range.</returns>
    public IReadOnlyCollection<long> GetModifiedInodesBetween(long sinceTransactionId, long untilTransactionId)
    {
        var result = new HashSet<long>();
        foreach (var kvp in _committedWriteSets)
        {
            long txId = kvp.Key;
            if (txId > sinceTransactionId && txId <= untilTransactionId)
            {
                foreach (long inodeNumber in kvp.Value)
                    result.Add(inodeNumber);
            }
        }
        return result;
    }

    /// <summary>
    /// Validates that the transaction is in the Active state.
    /// </summary>
    private static void ValidateActive(MvccTransaction tx)
    {
        if (tx.State != TransactionState.Active)
        {
            throw new InvalidOperationException(
                $"Transaction {tx.TransactionId} is not active (state: {tx.State}).");
        }
    }

    /// <summary>
    /// Removes committed write set records that are no longer needed for conflict detection.
    /// A write set can be pruned when its transaction ID is &lt;= the oldest active snapshot.
    /// </summary>
    private void PruneCommittedWriteSets()
    {
        long oldest = OldestActiveSnapshot;
        if (oldest == long.MaxValue)
        {
            // No active transactions; all committed write sets can be pruned
            _committedWriteSets.Clear();
            return;
        }

        foreach (long txId in _committedWriteSets.Keys)
        {
            if (txId <= oldest)
            {
                _committedWriteSets.TryRemove(txId, out _);
            }
        }
    }
}
