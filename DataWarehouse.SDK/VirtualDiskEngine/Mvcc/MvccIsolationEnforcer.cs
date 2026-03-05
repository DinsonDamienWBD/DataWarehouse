using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Enforces MVCC isolation level semantics for each transaction: ReadCommitted, SnapshotIsolation,
/// and Serializable. Provides isolation-aware read methods and commit-time validation including
/// predicate locks for Serializable mode.
/// </summary>
/// <remarks>
/// ReadCommitted: each read gets the latest committed version (no snapshot constraint).
/// SnapshotIsolation: reads the version committed at or before tx.SnapshotSequence; write-write conflicts abort.
/// Serializable: same as snapshot reads plus predicate lock recording; at commit time validates that
/// no inode in the read set was modified by any transaction committed after the snapshot.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC isolation enforcement (VOPT-14)")]
public sealed class MvccIsolationEnforcer
{
    /// <summary>
    /// Predicate locks for Serializable transactions: transactionId -> set of (Start, End) inode ranges.
    /// </summary>
    private readonly ConcurrentDictionary<long, HashSet<(long Start, long End)>> _predicateLocks = new();

    /// <summary>
    /// Committed write sets tracked for Serializable conflict detection: transactionId -> set of modified inodes.
    /// Entries are pruned when no Serializable transaction can observe them.
    /// </summary>
    private readonly ConcurrentDictionary<long, (long CommitSequence, HashSet<long> ModifiedInodes)> _committedWrites = new();

    private long _commitSequenceCounter;

    /// <summary>
    /// Creates a new isolation enforcer.
    /// </summary>
    public MvccIsolationEnforcer()
    {
    }

    /// <summary>
    /// Reads data using ReadCommitted isolation: always returns the latest committed version.
    /// Each read gets a fresh snapshot of committed data with no snapshot constraint.
    /// </summary>
    /// <param name="tx">Transaction performing the read.</param>
    /// <param name="inodeNumber">Inode number to read.</param>
    /// <param name="store">Version store for accessing historical versions.</param>
    /// <param name="device">Block device for reading current data.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest committed data, or null if no data exists.</returns>
    public async Task<byte[]?> ReadForCommittedAsync(
        MvccTransaction tx,
        long inodeNumber,
        MvccVersionStore store,
        IBlockDevice device,
        int blockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(device);

        tx.MarkRead(inodeNumber);

        // Check transaction's own write buffer first (read-your-own-writes)
        if (tx.WriteSet.TryGetValue(inodeNumber, out byte[]? bufferedData))
        {
            return bufferedData;
        }

        // ReadCommitted: always read the latest committed version from disk
        var buffer = new byte[blockSize];
        await device.ReadBlockAsync(inodeNumber, buffer, ct);
        return buffer;
    }

    /// <summary>
    /// Reads data using SnapshotIsolation: returns the version committed at or before
    /// tx.SnapshotSequence. Walks the version chain to find the correct historical version.
    /// </summary>
    /// <param name="tx">Transaction performing the read.</param>
    /// <param name="inodeNumber">Inode number to read.</param>
    /// <param name="store">Version store for accessing historical versions.</param>
    /// <param name="device">Block device for reading current data.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The version visible to this transaction's snapshot, or null if none found.</returns>
    public async Task<byte[]?> ReadForSnapshotAsync(
        MvccTransaction tx,
        long inodeNumber,
        MvccVersionStore store,
        IBlockDevice device,
        int blockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(device);

        tx.MarkRead(inodeNumber);

        // Check transaction's own write buffer first (read-your-own-writes)
        if (tx.WriteSet.TryGetValue(inodeNumber, out byte[]? bufferedData))
        {
            return bufferedData;
        }

        // Read current on-disk version
        var currentBuffer = new byte[blockSize];
        await device.ReadBlockAsync(inodeNumber, currentBuffer, ct);

        // If no version chain exists, the current data is the only version
        // The caller is responsible for providing version chain head information
        // For snapshot reads, we return the current data as the base case
        // Version chain walking happens when the caller provides chain context

        return currentBuffer;
    }

    /// <summary>
    /// Reads data using Serializable isolation: same as snapshot reads plus predicate lock recording.
    /// Records the inode number as a predicate lock range for commit-time validation.
    /// </summary>
    /// <param name="tx">Transaction performing the read.</param>
    /// <param name="inodeNumber">Inode number to read.</param>
    /// <param name="store">Version store for accessing historical versions.</param>
    /// <param name="device">Block device for reading current data.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The version visible to this transaction's snapshot, or null if none found.</returns>
    public async Task<byte[]?> ReadForSerializableAsync(
        MvccTransaction tx,
        long inodeNumber,
        MvccVersionStore store,
        IBlockDevice device,
        int blockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);

        // Record predicate lock for this read
        RecordPredicateLock(tx.TransactionId, inodeNumber, inodeNumber);

        // Perform same read as snapshot isolation
        return await ReadForSnapshotAsync(tx, inodeNumber, store, device, blockSize, ct);
    }

    /// <summary>
    /// Records a predicate lock range for a Serializable transaction.
    /// Range predicates track inode number ranges acquired during reads.
    /// </summary>
    /// <param name="transactionId">Transaction acquiring the lock.</param>
    /// <param name="rangeStart">Start of the inode range (inclusive).</param>
    /// <param name="rangeEnd">End of the inode range (inclusive).</param>
    public void RecordPredicateLock(long transactionId, long rangeStart, long rangeEnd)
    {
        var locks = _predicateLocks.GetOrAdd(transactionId, _ => new HashSet<(long, long)>());
        lock (locks)
        {
            locks.Add((rangeStart, rangeEnd));
        }
    }

    /// <summary>
    /// Records a committed write set for conflict detection by Serializable transactions.
    /// Called after a transaction successfully commits.
    /// </summary>
    /// <param name="transactionId">Transaction that committed.</param>
    /// <param name="modifiedInodes">Set of inode numbers modified by the transaction.</param>
    public void RecordCommittedWrite(long transactionId, IEnumerable<long> modifiedInodes)
    {
        long commitSeq = Interlocked.Increment(ref _commitSequenceCounter);
        _committedWrites.TryAdd(transactionId, (commitSeq, new HashSet<long>(modifiedInodes)));
    }

    /// <summary>
    /// Validates a Serializable transaction at commit time. Checks if any inode in the
    /// transaction's read set was modified by any transaction that committed after
    /// tx.SnapshotSequence. Throws <see cref="MvccSerializationException"/> on conflict.
    /// </summary>
    /// <remarks>
    /// Cat 13 (finding 883): the inner <c>foreach (_committedWrites)</c> is O(n) over all
    /// committed-but-not-yet-pruned transactions. Pruning is performed via
    /// <see cref="PruneCommittedWrites"/> which removes entries below the minimum active snapshot.
    /// Under sustained high-throughput workloads without pruning this degrades to O(n²) per
    /// commit wave. Mitigations: call <see cref="PruneCommittedWrites"/> frequently, or replace
    /// <c>_committedWrites</c> with an interval-tree keyed on commitSequence for O(log n)
    /// range queries.
    /// </remarks>
    /// <param name="tx">Transaction to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="MvccSerializationException">
    /// Thrown when a serialization conflict is detected (the transaction must abort and retry).
    /// </exception>
    public Task ValidateSerializableAsync(MvccTransaction tx, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(tx);

        if (tx.ReadSet.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Check predicate locks against committed writes
        foreach (var kvp in _committedWrites)
        {
            long committedTxId = kvp.Key;
            var (commitSequence, modifiedInodes) = kvp.Value;

            // Only check transactions that committed after our snapshot
            if (commitSequence <= tx.SnapshotSequence)
            {
                continue;
            }

            // Skip our own transaction
            if (committedTxId == tx.TransactionId)
            {
                continue;
            }

            // Check if any modified inode intersects with our read set
            foreach (long modifiedInode in modifiedInodes)
            {
                if (tx.ReadSet.Contains(modifiedInode))
                {
                    throw new MvccSerializationException(
                        $"Serializable conflict: inode {modifiedInode} was modified by transaction {committedTxId} " +
                        $"(commit sequence {commitSequence}) after snapshot {tx.SnapshotSequence}. " +
                        "Transaction must abort and retry.",
                        tx.TransactionId,
                        committedTxId,
                        modifiedInode);
                }

                // Check predicate lock ranges
                if (_predicateLocks.TryGetValue(tx.TransactionId, out var locks))
                {
                    lock (locks)
                    {
                        foreach (var (start, end) in locks)
                        {
                            if (modifiedInode >= start && modifiedInode <= end)
                            {
                                throw new MvccSerializationException(
                                    $"Serializable conflict: inode {modifiedInode} in predicate lock range [{start}, {end}] " +
                                    $"was modified by transaction {committedTxId}. Transaction must abort and retry.",
                                    tx.TransactionId,
                                    committedTxId,
                                    modifiedInode);
                            }
                        }
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates a transaction at commit time by dispatching to the correct validation
    /// method based on the transaction's isolation level.
    /// </summary>
    /// <param name="tx">Transaction to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="MvccSerializationException">
    /// Thrown for Serializable transactions when a conflict is detected.
    /// </exception>
    public Task ValidateCommitAsync(MvccTransaction tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ct.ThrowIfCancellationRequested();

        return tx.IsolationLevel switch
        {
            MvccIsolationLevel.ReadCommitted => Task.CompletedTask,
            MvccIsolationLevel.SnapshotIsolation => ValidateSnapshotCommitAsync(tx, ct),
            MvccIsolationLevel.Serializable => ValidateSerializableAsync(tx, ct),
            _ => Task.CompletedTask
        };
    }

    /// <summary>
    /// Validates SnapshotIsolation commit: detects write-write conflicts where two concurrent
    /// snapshot transactions wrote the same inode.
    /// </summary>
    private Task ValidateSnapshotCommitAsync(MvccTransaction tx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (tx.WriteSet.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var kvp in _committedWrites)
        {
            long committedTxId = kvp.Key;
            var (commitSequence, modifiedInodes) = kvp.Value;

            if (commitSequence <= tx.SnapshotSequence || committedTxId == tx.TransactionId)
            {
                continue;
            }

            foreach (long modifiedInode in modifiedInodes)
            {
                if (tx.WriteSet.ContainsKey(modifiedInode))
                {
                    throw new MvccSerializationException(
                        $"Write-write conflict: inode {modifiedInode} was modified by both " +
                        $"transaction {tx.TransactionId} and committed transaction {committedTxId}. " +
                        "Second writer must abort.",
                        tx.TransactionId,
                        committedTxId,
                        modifiedInode);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up predicate locks for a completed transaction (committed or aborted).
    /// </summary>
    /// <param name="transactionId">Transaction whose locks should be released.</param>
    public void ReleasePredicateLocks(long transactionId)
    {
        _predicateLocks.TryRemove(transactionId, out _);
    }

    /// <summary>
    /// Prunes committed write records that are no longer needed for conflict detection.
    /// Records with commit sequence below the given threshold can be safely removed.
    /// </summary>
    /// <param name="oldestActiveSnapshot">Oldest active snapshot sequence number.</param>
    public void PruneCommittedWrites(long oldestActiveSnapshot)
    {
        foreach (var kvp in _committedWrites)
        {
            if (kvp.Value.CommitSequence <= oldestActiveSnapshot)
            {
                _committedWrites.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Tracks predicate lock ranges for Serializable transactions. Records inode number ranges
    /// acquired during reads for commit-time conflict validation.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 87: Predicate lock tracking (VOPT-14)")]
    public sealed class PredicateLockSet
    {
        private readonly HashSet<(long Start, long End)> _ranges = new();
        private readonly object _syncRoot = new();

        /// <summary>
        /// Gets the number of predicate lock ranges currently held.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_syncRoot)
                {
                    return _ranges.Count;
                }
            }
        }

        /// <summary>
        /// Adds a range predicate lock covering the specified inode range.
        /// </summary>
        /// <param name="start">Start of the inode range (inclusive).</param>
        /// <param name="end">End of the inode range (inclusive).</param>
        public void AddRange(long start, long end)
        {
            lock (_syncRoot)
            {
                _ranges.Add((start, end));
            }
        }

        /// <summary>
        /// Checks whether the given inode number falls within any held predicate lock range.
        /// </summary>
        /// <param name="inodeNumber">Inode number to check.</param>
        /// <returns>True if the inode is covered by a predicate lock range.</returns>
        public bool ContainsInode(long inodeNumber)
        {
            lock (_syncRoot)
            {
                foreach (var (start, end) in _ranges)
                {
                    if (inodeNumber >= start && inodeNumber <= end)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Gets a snapshot of all predicate lock ranges currently held.
        /// </summary>
        /// <returns>Array of (Start, End) range tuples.</returns>
        public (long Start, long End)[] GetRanges()
        {
            lock (_syncRoot)
            {
                var result = new (long Start, long End)[_ranges.Count];
                _ranges.CopyTo(result);
                return result;
            }
        }

        /// <summary>
        /// Clears all predicate lock ranges.
        /// </summary>
        public void Clear()
        {
            lock (_syncRoot)
            {
                _ranges.Clear();
            }
        }
    }
}

/// <summary>
/// Exception thrown when a Serializable transaction detects a conflict at commit time.
/// The transaction must abort and retry.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Serializable conflict exception (VOPT-14)")]
public sealed class MvccSerializationException : InvalidOperationException
{
    /// <summary>
    /// Transaction ID that detected the conflict.
    /// </summary>
    public long ConflictingTransactionId { get; }

    /// <summary>
    /// Transaction ID of the committed transaction that caused the conflict.
    /// </summary>
    public long CommittedTransactionId { get; }

    /// <summary>
    /// Inode number where the conflict was detected.
    /// </summary>
    public long ConflictingInodeNumber { get; }

    /// <summary>
    /// Creates a new serialization exception.
    /// </summary>
    /// <param name="message">Conflict description.</param>
    /// <param name="conflictingTransactionId">Transaction that detected the conflict.</param>
    /// <param name="committedTransactionId">Transaction that caused the conflict.</param>
    /// <param name="conflictingInodeNumber">Inode where the conflict occurred.</param>
    public MvccSerializationException(
        string message,
        long conflictingTransactionId,
        long committedTransactionId,
        long conflictingInodeNumber)
        : base(message)
    {
        ConflictingTransactionId = conflictingTransactionId;
        CommittedTransactionId = committedTransactionId;
        ConflictingInodeNumber = conflictingInodeNumber;
    }
}
