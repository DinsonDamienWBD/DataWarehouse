// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Plugins.AccessLog;

/// <summary>
/// Default file-based access logging provider with production-ready features:
/// - Append-only log files stored in date-based hierarchy (logs/YYYY/MM/DD/)
/// - Automatic rotation by date and hour
/// - Thread-safe append operations
/// - Efficient querying with time-range optimization
/// - Tamper attribution analysis
/// - Compliance-ready audit trail
/// </summary>
/// <remarks>
/// This plugin provides a robust, production-ready implementation of access logging
/// suitable for tamper detection, forensic analysis, and compliance requirements.
/// Log files are stored in JSONL format (one JSON object per line) for efficient
/// append operations and streaming reads.
///
/// Storage Layout:
/// logs/
///   2026/
///     01/
///       29/
///         access_20260129_14.log  (entries for hour 14:00-14:59)
///         access_20260129_15.log  (entries for hour 15:00-15:59)
///
/// Thread Safety:
/// All operations are thread-safe and can be called concurrently from multiple threads.
/// Write operations use semaphore-based locking to ensure atomic appends.
///
/// Performance:
/// - Append: O(1) - constant time append with buffered writes
/// - Query: O(n) where n is entries in relevant time range (files outside time range are skipped)
/// - Purge: O(m) where m is number of old files to delete
///
/// Configuration:
/// Set the "LogBasePath" configuration key to customize the log directory location.
/// Default: "logs/access" relative to application directory.
/// </remarks>
public sealed class DefaultAccessLogPlugin : AccessLogProviderPluginBase
{
    private LogFileManager? _logManager;
    private string _logBasePath = "logs/access";

    /// <summary>
    /// Gets the unique identifier for this plugin.
    /// </summary>
    public override string Id => "com.datawarehouse.accesslog.default";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "Default Access Log Provider";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Initializes the plugin with the provided configuration.
    /// </summary>
    /// <param name="config">Configuration dictionary containing optional settings.</param>
    private void Initialize(Dictionary<string, object>? config = null)
    {
        // Extract configuration
        if (config != null)
        {
            if (config.TryGetValue("LogBasePath", out var pathObj) && pathObj is string path)
            {
                _logBasePath = path;
            }
        }

        // Initialize log manager
        _logManager = new LogFileManager(_logBasePath);
    }

    /// <summary>
    /// Starts the access logging feature.
    /// Initializes the log manager and ensures directory structure is ready.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when start is finished.</returns>
    public override Task StartAsync(CancellationToken ct)
    {
        // Initialize if not already done
        if (_logManager == null)
        {
            Initialize();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the access logging feature.
    /// Flushes all pending writes and closes file handles.
    /// </summary>
    /// <returns>A task that completes when stop is finished.</returns>
    public override Task StopAsync()
    {
        _logManager?.Dispose();
        _logManager = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Persists an access log entry to the file-based storage.
    /// Automatically creates directory structure and handles rotation.
    /// </summary>
    /// <param name="entry">The validated access log entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is persisted to disk.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the plugin is not initialized.</exception>
    protected override async Task PersistLogEntryAsync(AccessLogEntry entry, CancellationToken ct)
    {
        EnsureInitialized();

        await _logManager!.AppendLogEntryAsync(entry, ct);
    }

    /// <summary>
    /// Queries access logs based on the specified criteria.
    /// Efficiently scans only relevant log files within the query time range.
    /// </summary>
    /// <param name="query">The validated query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of access log entries matching the query.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the plugin is not initialized.</exception>
    protected override async Task<IReadOnlyList<AccessLogEntry>> QueryLogsAsync(
        AccessLogQuery query,
        CancellationToken ct)
    {
        EnsureInitialized();

        return await _logManager!.QueryLogsAsync(query, ct);
    }

    /// <summary>
    /// Purges access log entries older than the specified timestamp.
    /// Deletes entire log files if all entries are older than the threshold.
    /// Automatically cleans up empty directories after purge.
    /// </summary>
    /// <param name="olderThan">Delete all log entries with timestamps before this value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the purge operation finishes.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the plugin is not initialized.</exception>
    /// <remarks>
    /// This implementation is optimized for bulk deletion by removing entire log files
    /// rather than processing individual entries. For compliance purposes, consider
    /// archiving logs to long-term storage before purging.
    /// </remarks>
    public override async Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        EnsureInitialized();

        var deletedCount = await _logManager!.PurgeOldLogsAsync(olderThan, ct);

        // Log the purge operation itself (meta-logging)
        if (deletedCount > 0)
        {
            // Create a log entry documenting the purge
            var purgeEntry = AccessLogEntry.CreateAdminOperation(
                objectId: Guid.Empty, // System operation, not specific to an object
                principal: "system:access-log-plugin",
                operationDetails: $"Purged {deletedCount} log entries older than {olderThan:yyyy-MM-dd HH:mm:ss UTC}");

            await PersistLogEntryAsync(purgeEntry, ct);
        }
    }

    /// <summary>
    /// Gets additional plugin metadata including storage statistics.
    /// </summary>
    /// <returns>A dictionary of metadata key-value pairs.</returns>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "File-based access logging with date rotation, thread-safe operations, and tamper attribution analysis";
        metadata["StorageType"] = "File-based";
        metadata["RotationStrategy"] = "Hourly";
        metadata["Format"] = "JSONL";
        metadata["ThreadSafe"] = true;
        metadata["SupportsRangeQueries"] = true;
        metadata["SupportsPurge"] = true;

        if (_logManager != null)
        {
            metadata["LogPath"] = _logBasePath;
            metadata["LogFileCount"] = _logManager.GetLogFileCount();
            metadata["TotalLogSizeBytes"] = _logManager.GetTotalLogSize();
        }

        return metadata;
    }

    /// <summary>
    /// Ensures the plugin is initialized before performing operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the plugin is not initialized.</exception>
    private void EnsureInitialized()
    {
        if (_logManager == null)
        {
            throw new InvalidOperationException(
                "Plugin is not initialized. Call InitializeAsync before using the plugin.");
        }
    }
}
