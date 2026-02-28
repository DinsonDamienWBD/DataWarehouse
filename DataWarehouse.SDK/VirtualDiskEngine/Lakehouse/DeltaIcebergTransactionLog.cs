using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Lakehouse;

/// <summary>
/// Lakehouse table format: Delta Lake or Apache Iceberg.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public enum LakehouseFormat
{
    /// <summary>Delta Lake table format.</summary>
    DeltaLake,

    /// <summary>Apache Iceberg table format.</summary>
    Iceberg
}

/// <summary>
/// Actions that can be recorded in a transaction log entry.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public enum TransactionAction
{
    /// <summary>A data file was added to the table.</summary>
    AddFile,

    /// <summary>A data file was logically removed from the table.</summary>
    RemoveFile,

    /// <summary>A partition was added.</summary>
    AddPartition,

    /// <summary>A partition was removed.</summary>
    RemovePartition,

    /// <summary>Table schema was updated.</summary>
    SetSchema,

    /// <summary>Table properties were updated.</summary>
    SetProperties,

    /// <summary>Commit metadata information.</summary>
    CommitInfo
}

/// <summary>
/// A single entry in the transaction log describing one atomic change.
/// </summary>
/// <param name="Version">Monotonically increasing version per table.</param>
/// <param name="TableId">Identifier of the table this entry belongs to.</param>
/// <param name="Action">The type of change recorded.</param>
/// <param name="Timestamp">When this entry was created.</param>
/// <param name="FilePath">File path for AddFile/RemoveFile actions.</param>
/// <param name="Properties">Key-value properties for SetProperties/CommitInfo.</param>
/// <param name="SchemaJson">JSON schema definition for SetSchema actions.</param>
/// <param name="CommitId">Unique identifier for the commit this entry belongs to.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public sealed record TransactionEntry(
    long Version,
    string TableId,
    TransactionAction Action,
    DateTimeOffset Timestamp,
    string? FilePath,
    Dictionary<string, string>? Properties,
    string? SchemaJson,
    string CommitId);

/// <summary>
/// An atomic commit containing one or more transaction entries for a single table version.
/// </summary>
/// <param name="Version">The version number of this commit.</param>
/// <param name="TableId">The table this commit applies to.</param>
/// <param name="Entries">All entries in this atomic commit.</param>
/// <param name="CommitId">Unique commit identifier (GUID).</param>
/// <param name="Timestamp">When the commit was created.</param>
/// <param name="IsAtomic">Whether this commit is part of an atomic multi-table transaction.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public sealed record TransactionCommit(
    long Version,
    string TableId,
    IReadOnlyList<TransactionEntry> Entries,
    string CommitId,
    DateTimeOffset Timestamp,
    bool IsAtomic);

/// <summary>
/// Groups commits across multiple tables for atomic multi-table transactions.
/// All tables succeed or all rollback.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public sealed class MultiTableTransaction
{
    private readonly List<(string TableId, IReadOnlyList<TransactionEntry> Entries)> _tableCommits = new();

    /// <summary>
    /// Shared identifier for this multi-table transaction group.
    /// </summary>
    public string TransactionGroupId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether all expected table commits have been added.
    /// At least one table commit is required.
    /// </summary>
    public bool IsComplete => _tableCommits.Count > 0;

    /// <summary>
    /// Gets the table commits in this transaction.
    /// </summary>
    public IReadOnlyList<(string TableId, IReadOnlyList<TransactionEntry> Entries)> TableCommits => _tableCommits;

    /// <summary>
    /// Adds a table commit to this multi-table transaction.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="entries">The transaction entries for this table.</param>
    public void AddTableCommit(string tableId, IReadOnlyList<TransactionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(tableId);
        ArgumentNullException.ThrowIfNull(entries);
        _tableCommits.Add((tableId, entries));
    }
}

/// <summary>
/// Statistics about the transaction log.
/// </summary>
/// <param name="TableCount">Number of distinct tables.</param>
/// <param name="TotalVersions">Total number of committed versions across all tables.</param>
/// <param name="TotalEntries">Total number of transaction entries across all versions.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public sealed record TransactionLogStats(int TableCount, long TotalVersions, long TotalEntries);

/// <summary>
/// VDE-native transaction log supporting both Delta Lake and Iceberg table formats.
/// Stores versioned transaction entries per table with atomic multi-table commits
/// and log replay for snapshot reconstruction. Thread-safe via per-table SemaphoreSlim.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse transaction log")]
public sealed class DeltaIcebergTransactionLog : IDisposable
{
    private readonly LakehouseFormat _format;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, SortedList<long, TransactionCommit>> _tables = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tableLocks = new();
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the transaction log.
    /// </summary>
    /// <param name="format">The lakehouse format (Delta Lake or Iceberg).</param>
    /// <param name="logger">Optional logger.</param>
    public DeltaIcebergTransactionLog(LakehouseFormat format, ILogger? logger = null)
    {
        _format = format;
        _logger = logger;
    }

    /// <summary>
    /// Gets the lakehouse format this log operates in.
    /// </summary>
    public LakehouseFormat Format => _format;

    /// <summary>
    /// Commits a new version for a single table. Entries are appended atomically with a
    /// monotonically increasing version number. Returns the new version number.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="entries">The transaction entries to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly assigned version number.</returns>
    public async ValueTask<long> CommitAsync(
        string tableId,
        IReadOnlyList<TransactionEntry> entries,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tableId);
        ArgumentNullException.ThrowIfNull(entries);

        var tableLock = GetOrCreateTableLock(tableId);
        await tableLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var table = _tables.GetOrAdd(tableId, _ => new SortedList<long, TransactionCommit>());
            long newVersion = table.Count > 0 ? table.Keys[^1] + 1 : 0;
            string commitId = Guid.NewGuid().ToString("N");
            var timestamp = DateTimeOffset.UtcNow;

            var versionedEntries = entries.Select(e => e with
            {
                Version = newVersion,
                TableId = tableId,
                CommitId = commitId,
                Timestamp = timestamp
            }).ToList();

            var commit = new TransactionCommit(
                newVersion, tableId, versionedEntries, commitId, timestamp, IsAtomic: false);

            table.Add(newVersion, commit);

            _logger?.LogDebug(
                "Committed version {Version} for table {TableId} ({Format}, {EntryCount} entries)",
                newVersion, tableId, _format, entries.Count);

            return newVersion;
        }
        finally
        {
            tableLock.Release();
        }
    }

    /// <summary>
    /// Commits an atomic multi-table transaction. Acquires all table locks in sorted order
    /// (deadlock prevention), commits all tables or rolls back on failure.
    /// </summary>
    /// <param name="transaction">The multi-table transaction to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The version assigned to the first table in the transaction (representative).</returns>
    public async ValueTask<long> CommitMultiTableAsync(
        MultiTableTransaction transaction,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!transaction.IsComplete)
        {
            throw new InvalidOperationException("Multi-table transaction has no table commits.");
        }

        // Sort table IDs for deadlock-free lock acquisition
        var sortedTableIds = transaction.TableCommits
            .Select(tc => tc.TableId)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var acquiredLocks = new List<SemaphoreSlim>();
        var committedVersions = new List<(string TableId, long Version)>();
        try
        {
            // Acquire locks in sorted order
            foreach (var tableId in sortedTableIds)
            {
                var tableLock = GetOrCreateTableLock(tableId);
                await tableLock.WaitAsync(ct).ConfigureAwait(false);
                acquiredLocks.Add(tableLock);
            }

            var timestamp = DateTimeOffset.UtcNow;
            long representativeVersion = -1;

            // Commit all tables atomically
            foreach (var (tableId, entries) in transaction.TableCommits)
            {
                var table = _tables.GetOrAdd(tableId, _ => new SortedList<long, TransactionCommit>());
                long newVersion = table.Count > 0 ? table.Keys[^1] + 1 : 0;

                var versionedEntries = entries.Select(e => e with
                {
                    Version = newVersion,
                    TableId = tableId,
                    CommitId = transaction.TransactionGroupId,
                    Timestamp = timestamp
                }).ToList();

                var commit = new TransactionCommit(
                    newVersion, tableId, versionedEntries,
                    transaction.TransactionGroupId, timestamp, IsAtomic: true);

                table.Add(newVersion, commit);
                committedVersions.Add((tableId, newVersion));

                if (representativeVersion < 0)
                {
                    representativeVersion = newVersion;
                }
            }

            _logger?.LogDebug(
                "Multi-table commit {GroupId}: {TableCount} tables committed atomically ({Format})",
                transaction.TransactionGroupId, committedVersions.Count, _format);

            return representativeVersion;
        }
        catch (Exception ex) when (acquiredLocks.Count > 0)
        {
            // Rollback: remove partially committed versions to restore atomicity
            foreach (var committed in committedVersions)
            {
                if (_tables.TryGetValue(committed.TableId, out var table))
                {
                    lock (table)
                    {
                        table.Remove(committed.Version);
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine(
                $"[DeltaIcebergTransactionLog] Multi-table commit rolled back {committedVersions.Count} versions: {ex.Message}");
            throw;
        }
        finally
        {
            // Release all locks in reverse order
            for (int i = acquiredLocks.Count - 1; i >= 0; i--)
            {
                acquiredLocks[i].Release();
            }
        }
    }

    /// <summary>
    /// Gets a specific version commit for a table.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="version">The version number to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The commit at the specified version, or null if not found.</returns>
    public ValueTask<TransactionCommit?> GetVersionAsync(
        string tableId,
        long version,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_tables.TryGetValue(tableId, out var table) &&
            table.TryGetValue(version, out var commit))
        {
            return new ValueTask<TransactionCommit?>(commit);
        }

        return new ValueTask<TransactionCommit?>((TransactionCommit?)null);
    }

    /// <summary>
    /// Gets the commit history for a table within a version range (inclusive).
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="fromVersion">Start version (inclusive).</param>
    /// <param name="toVersion">End version (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of commits in the specified range.</returns>
    public ValueTask<IReadOnlyList<TransactionCommit>> GetHistoryAsync(
        string tableId,
        long fromVersion,
        long toVersion,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tables.TryGetValue(tableId, out var table))
        {
            return new ValueTask<IReadOnlyList<TransactionCommit>>(
                Array.Empty<TransactionCommit>());
        }

        var results = new List<TransactionCommit>();
        foreach (var kvp in table)
        {
            if (kvp.Key >= fromVersion && kvp.Key <= toVersion)
            {
                results.Add(kvp.Value);
            }
            else if (kvp.Key > toVersion)
            {
                break;
            }
        }

        return new ValueTask<IReadOnlyList<TransactionCommit>>(results);
    }

    /// <summary>
    /// Gets the latest version number for a table.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest version, or -1 if the table has no commits.</returns>
    public ValueTask<long> GetLatestVersionAsync(string tableId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_tables.TryGetValue(tableId, out var table) && table.Count > 0)
        {
            return new ValueTask<long>(table.Keys[^1]);
        }

        return new ValueTask<long>(-1L);
    }

    /// <summary>
    /// Replays the transaction log from version 0 to the target version, returning the
    /// effective state (AddFile entries minus RemoveFile entries) at that point.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="version">The target version to reconstruct state at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The effective transaction entries at the target version.</returns>
    public ValueTask<IReadOnlyList<TransactionEntry>> GetSnapshotAtVersionAsync(
        string tableId,
        long version,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tables.TryGetValue(tableId, out var table))
        {
            return new ValueTask<IReadOnlyList<TransactionEntry>>(
                Array.Empty<TransactionEntry>());
        }

        // Replay log: track active files and latest schema/properties
        var activeFiles = new HashSet<string>(StringComparer.Ordinal);
        TransactionEntry? latestSchema = null;
        TransactionEntry? latestProperties = null;
        var partitions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var kvp in table)
        {
            if (kvp.Key > version) break;

            foreach (var entry in kvp.Value.Entries)
            {
                switch (entry.Action)
                {
                    case TransactionAction.AddFile when entry.FilePath is not null:
                        activeFiles.Add(entry.FilePath);
                        break;
                    case TransactionAction.RemoveFile when entry.FilePath is not null:
                        activeFiles.Remove(entry.FilePath);
                        break;
                    case TransactionAction.SetSchema:
                        latestSchema = entry;
                        break;
                    case TransactionAction.SetProperties:
                        latestProperties = entry;
                        break;
                    case TransactionAction.AddPartition when entry.FilePath is not null:
                        partitions.Add(entry.FilePath);
                        break;
                    case TransactionAction.RemovePartition when entry.FilePath is not null:
                        partitions.Remove(entry.FilePath);
                        break;
                }
            }
        }

        var result = new List<TransactionEntry>();

        if (latestSchema is not null) result.Add(latestSchema);
        if (latestProperties is not null) result.Add(latestProperties);

        foreach (var filePath in activeFiles)
        {
            result.Add(new TransactionEntry(
                version, tableId, TransactionAction.AddFile,
                DateTimeOffset.UtcNow, filePath, null, null, string.Empty));
        }

        foreach (var partition in partitions)
        {
            result.Add(new TransactionEntry(
                version, tableId, TransactionAction.AddPartition,
                DateTimeOffset.UtcNow, partition, null, null, string.Empty));
        }

        return new ValueTask<IReadOnlyList<TransactionEntry>>(result);
    }

    /// <summary>
    /// Gets statistics about the transaction log.
    /// </summary>
    /// <returns>Current log statistics.</returns>
    public TransactionLogStats GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int tableCount = _tables.Count;
        long totalVersions = 0;
        long totalEntries = 0;

        foreach (var table in _tables.Values)
        {
            totalVersions += table.Count;
            foreach (var commit in table.Values)
            {
                totalEntries += commit.Entries.Count;
            }
        }

        return new TransactionLogStats(tableCount, totalVersions, totalEntries);
    }

    /// <summary>
    /// Disposes per-table semaphores.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var semaphore in _tableLocks.Values)
        {
            semaphore.Dispose();
        }
    }

    private SemaphoreSlim GetOrCreateTableLock(string tableId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _tableLocks.GetOrAdd(tableId, _ =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new SemaphoreSlim(1, 1);
        });
    }
}
