// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Plugins.AccessLog;

/// <summary>
/// Manages file-based log storage with date-based rotation and efficient querying.
/// Provides thread-safe append operations and optimized log retrieval.
/// </summary>
/// <remarks>
/// Log files are organized in a hierarchical directory structure:
/// logs/
///   YYYY/
///     MM/
///       DD/
///         access_YYYYMMDD_HH.log
///
/// Each log file contains JSON entries, one per line (JSONL format).
/// Files are automatically rotated hourly to balance file size and query performance.
/// </remarks>
public sealed class LogFileManager : IDisposable
{
    private readonly string _basePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, StreamWriter> _openWriters = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the LogFileManager.
    /// </summary>
    /// <param name="basePath">The base directory path for log storage.</param>
    /// <exception cref="ArgumentNullException">Thrown if basePath is null or empty.</exception>
    public LogFileManager(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new ArgumentNullException(nameof(basePath));

        _basePath = basePath;

        // Ensure base directory exists
        Directory.CreateDirectory(_basePath);
    }

    /// <summary>
    /// Appends an access log entry to the appropriate log file based on timestamp.
    /// Thread-safe operation that automatically creates directories and rotates files.
    /// </summary>
    /// <param name="entry">The access log entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is written to disk.</returns>
    public async Task AppendLogEntryAsync(AccessLogEntry entry, CancellationToken ct = default)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));

        var timestamp = entry.Timestamp.UtcDateTime;
        var logFilePath = GetLogFilePath(timestamp);
        var logLine = SerializeEntry(entry);

        await _writeLock.WaitAsync(ct);
        try
        {
            // Get or create writer for this log file
            var writer = GetOrCreateWriter(logFilePath);

            // Write entry as single line (JSONL format)
            await writer.WriteLineAsync(logLine);
            await writer.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Queries log entries matching the specified criteria.
    /// Efficiently scans only relevant log files based on the time range.
    /// </summary>
    /// <param name="query">The query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of matching access log entries.</returns>
    public async Task<IReadOnlyList<AccessLogEntry>> QueryLogsAsync(
        AccessLogQuery query,
        CancellationToken ct = default)
    {
        var results = new List<AccessLogEntry>();

        // Determine which log files to scan based on time range
        var filesToScan = GetRelevantLogFiles(query.StartTime, query.EndTime);

        foreach (var filePath in filesToScan)
        {
            if (!File.Exists(filePath))
                continue;

            var entries = await ReadLogFileAsync(filePath, ct);

            // Filter entries based on query criteria
            var matchingEntries = entries.Where(e => MatchesQuery(e, query));

            results.AddRange(matchingEntries);

            // Check cancellation periodically
            ct.ThrowIfCancellationRequested();
        }

        // Apply sorting
        results = ApplySorting(results, query.SortOrder);

        // Apply pagination
        if (query.Offset.HasValue)
            results = results.Skip(query.Offset.Value).ToList();

        if (query.Limit.HasValue)
            results = results.Take(query.Limit.Value).ToList();

        return results;
    }

    /// <summary>
    /// Deletes log entries older than the specified timestamp.
    /// Removes entire log files if all entries are older than the threshold.
    /// </summary>
    /// <param name="olderThan">The cutoff timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries deleted.</returns>
    public async Task<int> PurgeOldLogsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var cutoffDate = olderThan.UtcDateTime;
        var deletedCount = 0;

        await _writeLock.WaitAsync(ct);
        try
        {
            // Close all open writers to allow file deletion
            await CloseAllWritersAsync();

            // Find all log files older than cutoff
            var allFiles = Directory.GetFiles(_basePath, "*.log", SearchOption.AllDirectories);

            foreach (var filePath in allFiles)
            {
                var fileDate = GetDateFromLogFilePath(filePath);
                if (fileDate < cutoffDate)
                {
                    // Count entries before deletion
                    var entries = await ReadLogFileAsync(filePath, ct);
                    deletedCount += entries.Count;

                    // Delete the file
                    File.Delete(filePath);
                }
            }

            // Clean up empty directories
            CleanupEmptyDirectories(_basePath);
        }
        finally
        {
            _writeLock.Release();
        }

        return deletedCount;
    }

    /// <summary>
    /// Computes the total size of all log files in bytes.
    /// </summary>
    /// <returns>The total size in bytes.</returns>
    public long GetTotalLogSize()
    {
        var allFiles = Directory.GetFiles(_basePath, "*.log", SearchOption.AllDirectories);
        return allFiles.Sum(f => new FileInfo(f).Length);
    }

    /// <summary>
    /// Gets the number of log files currently stored.
    /// </summary>
    /// <returns>The count of log files.</returns>
    public int GetLogFileCount()
    {
        return Directory.GetFiles(_basePath, "*.log", SearchOption.AllDirectories).Length;
    }

    /// <summary>
    /// Disposes resources and closes all open file writers.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _writeLock.Wait();
        try
        {
            CloseAllWritersAsync().GetAwaiter().GetResult();
            _disposed = true;
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }

    // Private helper methods

    private string GetLogFilePath(DateTime timestamp)
    {
        var year = timestamp.Year.ToString("D4");
        var month = timestamp.Month.ToString("D2");
        var day = timestamp.Day.ToString("D2");
        var hour = timestamp.Hour.ToString("D2");

        var directory = Path.Combine(_basePath, year, month, day);
        Directory.CreateDirectory(directory);

        var fileName = $"access_{year}{month}{day}_{hour}.log";
        return Path.Combine(directory, fileName);
    }

    private DateTime GetDateFromLogFilePath(string filePath)
    {
        // Extract date from filename like access_20260129_14.log
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var parts = fileName.Split('_');

        if (parts.Length >= 2 && parts[1].Length == 8)
        {
            var dateStr = parts[1];
            if (DateTime.TryParseExact(dateStr, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            {
                return date;
            }
        }

        // Fallback: use directory structure
        var dirParts = Path.GetDirectoryName(filePath)?.Split(Path.DirectorySeparatorChar) ?? Array.Empty<string>();
        if (dirParts.Length >= 3)
        {
            var year = int.Parse(dirParts[^3]);
            var month = int.Parse(dirParts[^2]);
            var day = int.Parse(dirParts[^1]);
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }

        return DateTime.MinValue;
    }

    private IEnumerable<string> GetRelevantLogFiles(DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        var allFiles = Directory.GetFiles(_basePath, "*.log", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (!startTime.HasValue && !endTime.HasValue)
            return allFiles;

        var filteredFiles = new List<string>();

        foreach (var filePath in allFiles)
        {
            var fileDate = GetDateFromLogFilePath(filePath);

            // Check if file could contain relevant entries
            if (startTime.HasValue && fileDate.AddDays(1) < startTime.Value.UtcDateTime)
                continue;

            if (endTime.HasValue && fileDate > endTime.Value.UtcDateTime.AddDays(1))
                continue;

            filteredFiles.Add(filePath);
        }

        return filteredFiles;
    }

    private StreamWriter GetOrCreateWriter(string logFilePath)
    {
        return _openWriters.GetOrAdd(logFilePath, path =>
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
        });
    }

    private async Task CloseAllWritersAsync()
    {
        foreach (var kvp in _openWriters)
        {
            try
            {
                await kvp.Value.FlushAsync();
                kvp.Value.Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }

        _openWriters.Clear();
    }

    private static string SerializeEntry(AccessLogEntry entry)
    {
        return JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    private async Task<List<AccessLogEntry>> ReadLogFileAsync(string filePath, CancellationToken ct)
    {
        var entries = new List<AccessLogEntry>();

        using var reader = new StreamReader(filePath, Encoding.UTF8);
        string? line;

        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<AccessLogEntry>(line);
                if (entry != null)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // Skip malformed entries
                continue;
            }
        }

        return entries;
    }

    private static bool MatchesQuery(AccessLogEntry entry, AccessLogQuery query)
    {
        // Object ID filter
        if (query.ObjectId.HasValue && entry.ObjectId != query.ObjectId.Value)
            return false;

        // Principal filter (with wildcard support)
        if (!string.IsNullOrWhiteSpace(query.Principal))
        {
            if (query.Principal.EndsWith('*'))
            {
                var prefix = query.Principal.TrimEnd('*');
                if (!entry.Principal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else if (!entry.Principal.Equals(query.Principal, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Access type filter
        if (query.AccessTypes != null && query.AccessTypes.Count > 0)
        {
            if (!query.AccessTypes.Contains(entry.AccessType))
                return false;
        }

        // Time range filter
        if (query.StartTime.HasValue && entry.Timestamp < query.StartTime.Value)
            return false;

        if (query.EndTime.HasValue && entry.Timestamp > query.EndTime.Value)
            return false;

        // Session ID filter
        if (!string.IsNullOrWhiteSpace(query.SessionId) &&
            !string.Equals(entry.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Client IP filter
        if (!string.IsNullOrWhiteSpace(query.ClientIp) &&
            !string.Equals(entry.ClientIp, query.ClientIp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Success filter
        if (query.SucceededOnly.HasValue && entry.Succeeded != query.SucceededOnly.Value)
            return false;

        // Hash filter
        if (query.WithHashOnly.HasValue && query.WithHashOnly.Value &&
            string.IsNullOrWhiteSpace(entry.ComputedHash))
        {
            return false;
        }

        return true;
    }

    private static List<AccessLogEntry> ApplySorting(List<AccessLogEntry> entries, AccessLogSortOrder sortOrder)
    {
        return sortOrder switch
        {
            AccessLogSortOrder.TimestampDescending => entries.OrderByDescending(e => e.Timestamp).ToList(),
            AccessLogSortOrder.TimestampAscending => entries.OrderBy(e => e.Timestamp).ToList(),
            AccessLogSortOrder.PrincipalAscending => entries.OrderBy(e => e.Principal).ToList(),
            AccessLogSortOrder.AccessTypeAscending => entries.OrderBy(e => e.AccessType).ToList(),
            AccessLogSortOrder.DurationDescending => entries.OrderByDescending(e => e.DurationMs ?? 0).ToList(),
            _ => entries
        };
    }

    private static void CleanupEmptyDirectories(string basePath)
    {
        var directories = Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length); // Process deepest first

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
