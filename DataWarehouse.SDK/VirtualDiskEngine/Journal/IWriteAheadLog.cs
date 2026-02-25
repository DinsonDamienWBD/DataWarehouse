using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Write-ahead log interface for crash recovery and atomic transactions.
/// Ensures data modifications are logged before being applied to actual data blocks.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-03 WAL)")]
public interface IWriteAheadLog
{
    /// <summary>
    /// Begins a new transaction and returns a transaction handle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A transaction handle for grouping atomic operations.</returns>
    Task<WalTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Appends a journal entry to the WAL.
    /// Does not flush automatically; call <see cref="FlushAsync"/> for durability.
    /// </summary>
    /// <param name="entry">Entry to append.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendEntryAsync(JournalEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Flushes all pending WAL entries to persistent storage.
    /// This is the durability guarantee point.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Replays all committed transactions from the WAL for crash recovery.
    /// Returns entries that have been committed but not yet checkpointed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of committed journal entries to be applied.</returns>
    Task<IReadOnlyList<JournalEntry>> ReplayAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a checkpoint: marks all current entries as applied and advances the WAL tail.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task CheckpointAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current sequence number (next entry will have this value + 1).
    /// </summary>
    long CurrentSequenceNumber { get; }

    /// <summary>
    /// Gets the total size of the WAL in blocks.
    /// </summary>
    long WalSizeBlocks { get; }

    /// <summary>
    /// Gets the current WAL utilization as a percentage (0.0 to 1.0).
    /// Indicates how much of the circular buffer is in use.
    /// </summary>
    double WalUtilization { get; }

    /// <summary>
    /// Gets whether the WAL requires recovery (has uncommitted or unapplied entries).
    /// </summary>
    bool NeedsRecovery { get; }
}
