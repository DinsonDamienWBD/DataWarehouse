using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Lakehouse;

/// <summary>
/// Specifies a point-in-time target for time-travel queries: either a specific version
/// or a timestamp. Exactly one of Version or Timestamp must be set.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse time-travel engine")]
public sealed class TimeTravelTarget
{
    /// <summary>
    /// The specific version to query, or null for timestamp-based queries.
    /// </summary>
    public long? Version { get; private init; }

    /// <summary>
    /// The timestamp to query at, or null for version-based queries.
    /// </summary>
    public DateTimeOffset? Timestamp { get; private init; }

    private TimeTravelTarget() { }

    /// <summary>
    /// Creates a version-based time-travel target.
    /// </summary>
    /// <param name="version">The exact version to query.</param>
    /// <returns>A target pointing to the specified version.</returns>
    public static TimeTravelTarget AtVersion(long version) =>
        new() { Version = version };

    /// <summary>
    /// Creates a timestamp-based time-travel target.
    /// </summary>
    /// <param name="timestamp">The point in time to query at.</param>
    /// <returns>A target pointing to the latest version at or before the timestamp.</returns>
    public static TimeTravelTarget AtTimestamp(DateTimeOffset timestamp) =>
        new() { Timestamp = timestamp };
}

/// <summary>
/// A time-travel query specifying which table and at what point to query.
/// </summary>
/// <param name="TableId">The table to query.</param>
/// <param name="Target">The time-travel target (version or timestamp).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse time-travel engine")]
public sealed record TimeTravelQuery(string TableId, TimeTravelTarget Target);

/// <summary>
/// Result of a time-travel query containing the resolved table state.
/// </summary>
/// <param name="TableId">The queried table.</param>
/// <param name="ResolvedVersion">The actual version resolved.</param>
/// <param name="VersionTimestamp">Timestamp of the resolved version.</param>
/// <param name="SchemaAtVersion">The table schema at the resolved version.</param>
/// <param name="ActiveFilesAtVersion">Active data files at the resolved version.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse time-travel engine")]
public sealed record TimeTravelResult(
    string TableId,
    long ResolvedVersion,
    DateTimeOffset VersionTimestamp,
    TableSchema? SchemaAtVersion,
    IReadOnlyList<string> ActiveFilesAtVersion);

/// <summary>
/// Statistics about time-travel engine usage.
/// </summary>
/// <param name="QueriesExecuted">Total number of time-travel queries executed.</param>
/// <param name="VersionLookups">Number of version-based lookups.</param>
/// <param name="TimestampLookups">Number of timestamp-based lookups.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse time-travel engine")]
public sealed record TimeTravelStats(long QueriesExecuted, long VersionLookups, long TimestampLookups);

/// <summary>
/// Time-travel query engine for lakehouse tables using VDE snapshot mechanism.
/// Resolves historical table state by version or timestamp via transaction log replay.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse time-travel engine")]
public sealed class TimeTravelEngine
{
    private readonly DeltaIcebergTransactionLog _transactionLog;
    private readonly LakehouseTableOperations _tableOps;
    private readonly ILogger? _logger;
    private long _queriesExecuted;
    private long _versionLookups;
    private long _timestampLookups;

    /// <summary>
    /// Initializes a new instance of the time-travel engine.
    /// </summary>
    /// <param name="transactionLog">The underlying transaction log.</param>
    /// <param name="tableOps">Table operations for schema retrieval.</param>
    /// <param name="logger">Optional logger.</param>
    public TimeTravelEngine(
        DeltaIcebergTransactionLog transactionLog,
        LakehouseTableOperations tableOps,
        ILogger? logger = null)
    {
        _transactionLog = transactionLog ?? throw new ArgumentNullException(nameof(transactionLog));
        _tableOps = tableOps ?? throw new ArgumentNullException(nameof(tableOps));
        _logger = logger;
    }

    /// <summary>
    /// Executes a time-travel query, resolving the table state at the specified target.
    /// For version-based targets, directly replays the log to that version.
    /// For timestamp-based targets, binary searches for the latest version at or before the timestamp.
    /// </summary>
    /// <param name="query">The time-travel query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved table state.</returns>
    public async ValueTask<TimeTravelResult> QueryAsync(
        TimeTravelQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        Interlocked.Increment(ref _queriesExecuted);

        long resolvedVersion;
        DateTimeOffset versionTimestamp;

        if (query.Target.Version.HasValue)
        {
            // Version-based lookup
            Interlocked.Increment(ref _versionLookups);
            resolvedVersion = query.Target.Version.Value;

            var commit = await _transactionLog.GetVersionAsync(query.TableId, resolvedVersion, ct)
                .ConfigureAwait(false);

            if (commit is null)
            {
                throw new InvalidOperationException(
                    $"Version {resolvedVersion} does not exist for table '{query.TableId}'.");
            }

            versionTimestamp = commit.Timestamp;
        }
        else if (query.Target.Timestamp.HasValue)
        {
            // Timestamp-based lookup: binary search transaction log
            Interlocked.Increment(ref _timestampLookups);
            var targetTimestamp = query.Target.Timestamp.Value;

            resolvedVersion = await ResolveVersionByTimestampAsync(
                query.TableId, targetTimestamp, ct).ConfigureAwait(false);

            var commit = await _transactionLog.GetVersionAsync(query.TableId, resolvedVersion, ct)
                .ConfigureAwait(false);

            versionTimestamp = commit?.Timestamp ?? targetTimestamp;
        }
        else
        {
            throw new InvalidOperationException(
                "TimeTravelTarget must specify either Version or Timestamp.");
        }

        // Replay log to resolved version for effective state
        var snapshot = await _transactionLog.GetSnapshotAtVersionAsync(
            query.TableId, resolvedVersion, ct).ConfigureAwait(false);

        // Extract schema from snapshot
        TableSchema? schemaAtVersion = null;
        var schemaEntry = snapshot.FirstOrDefault(e => e.Action == TransactionAction.SetSchema);
        if (schemaEntry?.SchemaJson is not null)
        {
            schemaAtVersion = JsonSerializer.Deserialize<TableSchema>(
                schemaEntry.SchemaJson, _jsonOptions);
        }

        // Extract active files
        var activeFiles = snapshot
            .Where(e => e.Action == TransactionAction.AddFile && e.FilePath is not null)
            .Select(e => e.FilePath!)
            .ToList();

        _logger?.LogDebug(
            "Time-travel query for table {TableId} resolved to version {Version} ({Files} files)",
            query.TableId, resolvedVersion, activeFiles.Count);

        return new TimeTravelResult(
            query.TableId, resolvedVersion, versionTimestamp,
            schemaAtVersion, activeFiles);
    }

    /// <summary>
    /// Lists all version numbers for a table, optionally filtered by time range.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="from">Optional start timestamp (inclusive).</param>
    /// <param name="to">Optional end timestamp (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of version numbers in the specified range.</returns>
    public async ValueTask<IReadOnlyList<long>> ListVersionsAsync(
        string tableId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);

        long latestVersion = await _transactionLog.GetLatestVersionAsync(tableId, ct)
            .ConfigureAwait(false);

        if (latestVersion < 0) return Array.Empty<long>();

        var history = await _transactionLog.GetHistoryAsync(tableId, 0, latestVersion, ct)
            .ConfigureAwait(false);

        var versions = new List<long>();
        foreach (var commit in history)
        {
            if (from.HasValue && commit.Timestamp < from.Value) continue;
            if (to.HasValue && commit.Timestamp > to.Value) continue;
            versions.Add(commit.Version);
        }

        return versions;
    }

    /// <summary>
    /// Computes the difference between two versions: files added and removed.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="fromVersion">The starting version.</param>
    /// <param name="toVersion">The ending version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary with "added" and "removed" file lists.</returns>
    public async ValueTask<IReadOnlyDictionary<string, object>> DiffVersionsAsync(
        string tableId,
        long fromVersion,
        long toVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);

        var fromSnapshot = await _transactionLog.GetSnapshotAtVersionAsync(tableId, fromVersion, ct)
            .ConfigureAwait(false);
        var toSnapshot = await _transactionLog.GetSnapshotAtVersionAsync(tableId, toVersion, ct)
            .ConfigureAwait(false);

        var fromFiles = fromSnapshot
            .Where(e => e.Action == TransactionAction.AddFile && e.FilePath is not null)
            .Select(e => e.FilePath!)
            .ToHashSet(StringComparer.Ordinal);

        var toFiles = toSnapshot
            .Where(e => e.Action == TransactionAction.AddFile && e.FilePath is not null)
            .Select(e => e.FilePath!)
            .ToHashSet(StringComparer.Ordinal);

        var added = toFiles.Except(fromFiles).ToList();
        var removed = fromFiles.Except(toFiles).ToList();

        return new Dictionary<string, object>
        {
            ["added"] = added,
            ["removed"] = removed,
            ["addedCount"] = added.Count,
            ["removedCount"] = removed.Count
        };
    }

    /// <summary>
    /// Gets current statistics about the time-travel engine.
    /// </summary>
    /// <returns>Engine usage statistics.</returns>
    public TimeTravelStats GetStats()
    {
        return new TimeTravelStats(
            Interlocked.Read(ref _queriesExecuted),
            Interlocked.Read(ref _versionLookups),
            Interlocked.Read(ref _timestampLookups));
    }

    /// <summary>
    /// Binary searches the transaction log for the latest version at or before the target timestamp.
    /// </summary>
    private async ValueTask<long> ResolveVersionByTimestampAsync(
        string tableId,
        DateTimeOffset targetTimestamp,
        CancellationToken ct)
    {
        long latestVersion = await _transactionLog.GetLatestVersionAsync(tableId, ct)
            .ConfigureAwait(false);

        if (latestVersion < 0)
        {
            throw new InvalidOperationException(
                $"Table '{tableId}' has no committed versions.");
        }

        // Binary search for the latest version at or before the target timestamp
        long low = 0;
        long high = latestVersion;
        long result = -1;

        while (low <= high)
        {
            long mid = low + (high - low) / 2;
            var commit = await _transactionLog.GetVersionAsync(tableId, mid, ct)
                .ConfigureAwait(false);

            if (commit is null)
            {
                // Version gap: move forward
                low = mid + 1;
                continue;
            }

            if (commit.Timestamp <= targetTimestamp)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (result < 0)
        {
            throw new InvalidOperationException(
                $"No version exists at or before {targetTimestamp} for table '{tableId}'.");
        }

        return result;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
