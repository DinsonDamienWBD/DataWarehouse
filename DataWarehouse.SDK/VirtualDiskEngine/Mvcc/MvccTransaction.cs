using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// State of an MVCC transaction through its lifecycle.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC core (VOPT-12)")]
public enum TransactionState : byte
{
    /// <summary>Transaction is actively reading/writing.</summary>
    Active = 0,

    /// <summary>Transaction has been successfully committed.</summary>
    Committed = 1,

    /// <summary>Transaction was explicitly aborted.</summary>
    Aborted = 2,

    /// <summary>Transaction was rolled back due to conflict or error.</summary>
    RolledBack = 3
}

/// <summary>
/// MVCC isolation levels controlling snapshot visibility semantics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC core (VOPT-12)")]
public enum MvccIsolationLevel : byte
{
    /// <summary>
    /// Each statement sees the latest committed data at statement start.
    /// </summary>
    ReadCommitted = 0,

    /// <summary>
    /// All reads within a transaction see the same snapshot taken at transaction begin.
    /// Write-write conflicts detected at commit time.
    /// </summary>
    SnapshotIsolation = 1,

    /// <summary>
    /// Full serializability: reads and writes are validated against concurrent transactions.
    /// Read set is checked for modifications by concurrent committed transactions.
    /// </summary>
    Serializable = 2
}

/// <summary>
/// Represents a single MVCC transaction with snapshot-based visibility.
/// Tracks read/write sets for conflict detection and buffered writes for atomic commit.
/// Writers append WAL records with transaction ID; readers see only versions &lt;= their snapshot.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: MVCC core (VOPT-12)")]
public sealed class MvccTransaction
{
    /// <summary>
    /// Monotonically increasing transaction identifier. Never reused.
    /// </summary>
    public long TransactionId { get; }

    /// <summary>
    /// WAL sequence number captured at transaction begin.
    /// Defines the visibility boundary: only versions with transaction ID &lt;= this value are visible.
    /// </summary>
    public long SnapshotSequence { get; }

    /// <summary>
    /// Isolation level governing snapshot visibility and conflict detection behavior.
    /// </summary>
    public MvccIsolationLevel IsolationLevel { get; }

    /// <summary>
    /// UTC timestamp when the transaction was started.
    /// </summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>
    /// Current lifecycle state of this transaction.
    /// </summary>
    public TransactionState State { get; internal set; }

    /// <summary>
    /// Set of inode numbers read during this transaction.
    /// Used for conflict detection in Serializable isolation.
    /// </summary>
    public HashSet<long> ReadSet { get; } = new();

    /// <summary>
    /// Buffered writes: inode number to modified data.
    /// Applied atomically during commit.
    /// </summary>
    public Dictionary<long, byte[]> WriteSet { get; } = new();

    /// <summary>
    /// Old version chain entries created during commit.
    /// Each entry maps an inode number to the block where its previous version was stored.
    /// </summary>
    public List<(long InodeNumber, long OldVersionBlock)> VersionChainEntries { get; } = new();

    /// <summary>
    /// Creates a new MVCC transaction.
    /// </summary>
    /// <param name="transactionId">Monotonically increasing transaction ID.</param>
    /// <param name="snapshotSequence">WAL sequence number at transaction begin.</param>
    /// <param name="isolationLevel">Isolation level for this transaction.</param>
    internal MvccTransaction(long transactionId, long snapshotSequence, MvccIsolationLevel isolationLevel)
    {
        TransactionId = transactionId;
        SnapshotSequence = snapshotSequence;
        IsolationLevel = isolationLevel;
        StartedUtc = DateTimeOffset.UtcNow;
        State = TransactionState.Active;
    }

    /// <summary>
    /// Marks an inode as read by this transaction. Used for conflict detection.
    /// </summary>
    /// <param name="inodeNumber">The inode number that was read.</param>
    public void MarkRead(long inodeNumber)
    {
        ReadSet.Add(inodeNumber);
    }

    /// <summary>
    /// Buffers a write to an inode's data. The write is applied atomically during commit.
    /// </summary>
    /// <param name="inodeNumber">The inode number being modified.</param>
    /// <param name="data">The new data for this inode.</param>
    public void BufferWrite(long inodeNumber, byte[] data)
    {
        WriteSet[inodeNumber] = data;
    }

    /// <summary>
    /// Determines whether a version created by the given transaction ID is visible
    /// to this transaction based on its snapshot sequence.
    /// </summary>
    /// <param name="versionTransactionId">Transaction ID that created the version.</param>
    /// <returns>True if the version is visible (was committed before this transaction's snapshot).</returns>
    public bool IsVisibleTo(long versionTransactionId)
    {
        // For ReadCommitted and SnapshotIsolation:
        // A version is visible if its creating transaction ID <= our snapshot sequence
        return versionTransactionId <= SnapshotSequence;
    }
}
