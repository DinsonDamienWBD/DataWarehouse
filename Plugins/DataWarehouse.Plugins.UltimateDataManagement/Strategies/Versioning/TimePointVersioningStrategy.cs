using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Point-in-time recovery versioning strategy with continuous versioning and millisecond timestamps.
/// Enables querying any point in time with efficient time-range queries and WAL-style change tracking.
/// </summary>
/// <remarks>
/// Features:
/// - Continuous versioning with millisecond precision timestamps
/// - Point-in-time queries for any historical moment
/// - Efficient time-range queries using sorted indices
/// - WAL (Write-Ahead Log) style change tracking
/// - Automatic snapshot intervals for faster recovery
/// - Time-based retention policies
/// - Thread-safe concurrent access
/// </remarks>
public sealed class TimePointVersioningStrategy : VersioningStrategyBase
{
    private const int DefaultSnapshotIntervalMs = 60000; // 1 minute
    private const int DefaultRetentionDays = 30;

    private readonly ConcurrentDictionary<string, ObjectTimePointStore> _stores = new();
    private int _snapshotIntervalMs = DefaultSnapshotIntervalMs;
    private int _retentionDays = DefaultRetentionDays;

    /// <summary>
    /// Represents a WAL (Write-Ahead Log) entry.
    /// </summary>
    private sealed class WalEntry
    {
        /// <summary>
        /// Timestamp of this entry with millisecond precision.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Type of operation.
        /// </summary>
        public required WalOperationType Operation { get; init; }

        /// <summary>
        /// Delta data for incremental changes.
        /// </summary>
        public byte[]? DeltaData { get; init; }

        /// <summary>
        /// Full snapshot data (for snapshot entries).
        /// </summary>
        public byte[]? SnapshotData { get; init; }

        /// <summary>
        /// Content hash at this point.
        /// </summary>
        public required string ContentHash { get; init; }

        /// <summary>
        /// Size at this point.
        /// </summary>
        public required long SizeBytes { get; init; }
    }

    /// <summary>
    /// WAL operation types.
    /// </summary>
    private enum WalOperationType
    {
        /// <summary>Full snapshot.</summary>
        Snapshot,
        /// <summary>Incremental delta.</summary>
        Delta,
        /// <summary>Delete marker.</summary>
        Delete
    }

    /// <summary>
    /// Represents a time-point version entry.
    /// </summary>
    private sealed class TimePointVersionEntry
    {
        /// <summary>
        /// Version metadata.
        /// </summary>
        public required VersionInfo Info { get; init; }

        /// <summary>
        /// Precise timestamp with millisecond precision.
        /// </summary>
        public required DateTime PreciseTimestamp { get; init; }

        /// <summary>
        /// Full data for this version.
        /// </summary>
        public required byte[] Data { get; init; }

        /// <summary>
        /// Whether this is a snapshot point.
        /// </summary>
        public required bool IsSnapshot { get; init; }
    }

    /// <summary>
    /// Store for a single object's time-point versions.
    /// </summary>
    private sealed class ObjectTimePointStore
    {
        private readonly object _storeLock = new();
        private readonly Dictionary<string, TimePointVersionEntry> _versions = new();
        private readonly SortedList<DateTime, string> _timeIndex = new();
        private readonly List<WalEntry> _wal = new();
        private long _nextVersionNumber = 1;
        private string? _currentVersionId;
        private DateTime _lastSnapshotTime = DateTime.MinValue;

        /// <summary>
        /// Creates a new time-point version.
        /// </summary>
        public (VersionInfo Info, bool CreatedSnapshot) CreateVersion(
            string objectId,
            byte[] data,
            VersionMetadata metadata,
            int snapshotIntervalMs)
        {
            lock (_storeLock)
            {
                var now = DateTime.UtcNow;
                var versionId = GenerateVersionId();
                var versionNumber = _nextVersionNumber++;
                var contentHash = ComputeHashStatic(data);

                // Determine if we should create a snapshot
                var timeSinceSnapshot = (now - _lastSnapshotTime).TotalMilliseconds;
                var isSnapshot = _wal.Count == 0 || timeSinceSnapshot >= snapshotIntervalMs;

                // Add WAL entry
                if (isSnapshot)
                {
                    _wal.Add(new WalEntry
                    {
                        Timestamp = now,
                        Operation = WalOperationType.Snapshot,
                        SnapshotData = data,
                        DeltaData = null,
                        ContentHash = contentHash,
                        SizeBytes = data.Length
                    });
                    _lastSnapshotTime = now;
                }
                else
                {
                    // Compute delta from previous version
                    var prevData = _currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var prev)
                        ? prev.Data
                        : [];
                    var delta = ComputeSimpleDelta(prevData, data);

                    _wal.Add(new WalEntry
                    {
                        Timestamp = now,
                        Operation = WalOperationType.Delta,
                        SnapshotData = null,
                        DeltaData = delta,
                        ContentHash = contentHash,
                        SizeBytes = data.Length
                    });
                }

                var info = new VersionInfo
                {
                    VersionId = versionId,
                    ObjectId = objectId,
                    VersionNumber = versionNumber,
                    ContentHash = contentHash,
                    SizeBytes = data.Length,
                    CreatedAt = now,
                    Metadata = metadata with
                    {
                        Properties = new Dictionary<string, string>(metadata.Properties ?? [])
                        {
                            ["timestamp_ms"] = now.ToString("o"),
                            ["is_snapshot"] = isSnapshot.ToString()
                        }
                    },
                    IsCurrent = true,
                    IsDeleted = false
                };

                // Mark previous current as not current
                if (_currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var previous))
                {
                    _versions[_currentVersionId] = new TimePointVersionEntry
                    {
                        Info = previous.Info with { IsCurrent = false },
                        PreciseTimestamp = previous.PreciseTimestamp,
                        Data = previous.Data,
                        IsSnapshot = previous.IsSnapshot
                    };
                }

                _versions[versionId] = new TimePointVersionEntry
                {
                    Info = info,
                    PreciseTimestamp = now,
                    Data = data,
                    IsSnapshot = isSnapshot
                };

                _timeIndex[now] = versionId;
                _currentVersionId = versionId;

                return (info, isSnapshot);
            }
        }

        /// <summary>
        /// Gets the version at a specific point in time.
        /// </summary>
        public TimePointVersionEntry? GetVersionAtTime(DateTime pointInTime)
        {
            lock (_storeLock)
            {
                // Find the latest version at or before the given time
                string? versionId = null;
                foreach (var kvp in _timeIndex)
                {
                    if (kvp.Key <= pointInTime)
                        versionId = kvp.Value;
                    else
                        break;
                }

                if (versionId == null)
                    return null;

                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry
                    : null;
            }
        }

        /// <summary>
        /// Gets versions within a time range.
        /// </summary>
        public IEnumerable<TimePointVersionEntry> GetVersionsInRange(DateTime from, DateTime to)
        {
            lock (_storeLock)
            {
                var results = new List<TimePointVersionEntry>();

                foreach (var kvp in _timeIndex)
                {
                    if (kvp.Key < from)
                        continue;
                    if (kvp.Key > to)
                        break;

                    if (_versions.TryGetValue(kvp.Value, out var entry) && !entry.Info.IsDeleted)
                        results.Add(entry);
                }

                return results;
            }
        }

        /// <summary>
        /// Gets version data by ID.
        /// </summary>
        public byte[]? GetVersionData(string versionId)
        {
            lock (_storeLock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry.Data
                    : null;
            }
        }

        /// <summary>
        /// Gets entry by version ID.
        /// </summary>
        public TimePointVersionEntry? GetEntry(string versionId)
        {
            lock (_storeLock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry
                    : null;
            }
        }

        /// <summary>
        /// Lists all versions with optional filtering.
        /// </summary>
        public IEnumerable<VersionInfo> ListVersions(VersionListOptions options)
        {
            lock (_storeLock)
            {
                var query = _timeIndex.Values
                    .Select(id => _versions[id].Info)
                    .Where(v => options.IncludeDeleted || !v.IsDeleted);

                if (options.FromDate.HasValue)
                    query = query.Where(v => v.CreatedAt >= options.FromDate.Value);

                if (options.ToDate.HasValue)
                    query = query.Where(v => v.CreatedAt <= options.ToDate.Value);

                return query
                    .OrderByDescending(v => v.CreatedAt)
                    .Take(options.MaxResults)
                    .ToList();
            }
        }

        /// <summary>
        /// Soft-deletes a version.
        /// </summary>
        public bool DeleteVersion(string versionId)
        {
            lock (_storeLock)
            {
                if (!_versions.TryGetValue(versionId, out var entry) || entry.Info.IsDeleted)
                    return false;

                _versions[versionId] = new TimePointVersionEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    PreciseTimestamp = entry.PreciseTimestamp,
                    Data = entry.Data,
                    IsSnapshot = entry.IsSnapshot
                };

                // Add delete marker to WAL
                _wal.Add(new WalEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Operation = WalOperationType.Delete,
                    ContentHash = entry.Info.ContentHash,
                    SizeBytes = 0
                });

                if (_currentVersionId == versionId)
                {
                    _currentVersionId = null;
                    // Find previous non-deleted version
                    foreach (var kvp in _timeIndex.Reverse())
                    {
                        if (_versions.TryGetValue(kvp.Value, out var v) && !v.Info.IsDeleted)
                        {
                            _currentVersionId = kvp.Value;
                            _versions[kvp.Value] = new TimePointVersionEntry
                            {
                                Info = v.Info with { IsCurrent = true },
                                PreciseTimestamp = v.PreciseTimestamp,
                                Data = v.Data,
                                IsSnapshot = v.IsSnapshot
                            };
                            break;
                        }
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Applies retention policy, removing versions older than the retention period.
        /// </summary>
        /// <returns>Number of versions removed.</returns>
        public int ApplyRetentionPolicy(int retentionDays)
        {
            lock (_storeLock)
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var toRemove = new List<DateTime>();
                var removedCount = 0;

                foreach (var kvp in _timeIndex)
                {
                    if (kvp.Key >= cutoff)
                        break;

                    // Keep at least the last snapshot before cutoff for recovery
                    if (_versions.TryGetValue(kvp.Value, out var entry) && !entry.IsSnapshot)
                    {
                        toRemove.Add(kvp.Key);
                        _versions.Remove(kvp.Value);
                        removedCount++;
                    }
                }

                foreach (var time in toRemove)
                    _timeIndex.Remove(time);

                // Also trim WAL
                _wal.RemoveAll(w => w.Timestamp < cutoff && w.Operation != WalOperationType.Snapshot);

                return removedCount;
            }
        }

        /// <summary>
        /// Gets the current version info.
        /// </summary>
        public VersionInfo? GetCurrentVersion()
        {
            lock (_storeLock)
            {
                return _currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var entry)
                    ? entry.Info
                    : null;
            }
        }

        /// <summary>
        /// Gets version info by ID.
        /// </summary>
        public VersionInfo? GetVersionInfo(string versionId)
        {
            lock (_storeLock)
            {
                return _versions.TryGetValue(versionId, out var entry) ? entry.Info : null;
            }
        }

        /// <summary>
        /// Gets total version count.
        /// </summary>
        public long GetVersionCount(bool includeDeleted = false)
        {
            lock (_storeLock)
            {
                return includeDeleted
                    ? _versions.Count
                    : _versions.Values.Count(v => !v.Info.IsDeleted);
            }
        }

        /// <summary>
        /// Gets WAL statistics.
        /// </summary>
        public (int TotalEntries, int Snapshots, int Deltas) GetWalStats()
        {
            lock (_storeLock)
            {
                return (
                    _wal.Count,
                    _wal.Count(w => w.Operation == WalOperationType.Snapshot),
                    _wal.Count(w => w.Operation == WalOperationType.Delta)
                );
            }
        }

        private static string GenerateVersionId() => Guid.NewGuid().ToString("N")[..12];

        private static string ComputeHashStatic(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static byte[] ComputeSimpleDelta(byte[] from, byte[] to)
        {
            using var ms = new MemoryStream(65536);
            using var writer = new BinaryWriter(ms);

            writer.Write(from.Length);
            writer.Write(to.Length);

            var minLen = Math.Min(from.Length, to.Length);
            for (int i = 0; i < minLen; i++)
            {
                writer.Write((byte)(from[i] ^ to[i]));
            }

            if (to.Length > from.Length)
            {
                writer.Write(to, minLen, to.Length - minLen);
            }

            return ms.ToArray();
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "versioning.timepoint";

    /// <inheritdoc/>
    public override string DisplayName => "Time-Point Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 80_000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Point-in-time recovery versioning with continuous versioning and millisecond timestamps. " +
        "Enables querying any historical point in time with efficient time-range queries. " +
        "Uses WAL-style change tracking with automatic snapshot intervals. " +
        "Ideal for audit logs, temporal queries, and time-travel debugging.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "temporal", "timepoint", "wal", "point-in-time", "recovery"];

    /// <summary>
    /// Gets or sets the snapshot interval in milliseconds.
    /// </summary>
    public int SnapshotIntervalMs
    {
        get => _snapshotIntervalMs;
        set => _snapshotIntervalMs = value > 0 ? value : DefaultSnapshotIntervalMs;
    }

    /// <summary>
    /// Gets or sets the retention period in days.
    /// </summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => _retentionDays = value > 0 ? value : DefaultRetentionDays;
    }

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream(65536);
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, _ => new ObjectTimePointStore());
        var (info, _) = store.CreateVersion(objectId, bytes, metadata, _snapshotIntervalMs);

        return Task.FromResult(info);
    }

    /// <inheritdoc/>
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var data = store.GetVersionData(versionId)
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'.");

        return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }

    /// <inheritdoc/>
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IEnumerable<VersionInfo>>([]);

        return Task.FromResult(store.ListVersions(options));
    }

    /// <inheritdoc/>
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.DeleteVersion(versionId));
    }

    /// <inheritdoc/>
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<VersionInfo?>(null);

        return Task.FromResult(store.GetCurrentVersion());
    }

    /// <inheritdoc/>
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var entry = store.GetEntry(versionId)
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'.");

        var restoredMetadata = new VersionMetadata
        {
            Author = entry.Info.Metadata?.Author,
            Message = $"Restored from point-in-time {entry.PreciseTimestamp:o}",
            ParentVersionId = versionId,
            Properties = new Dictionary<string, string>(entry.Info.Metadata?.Properties ?? [])
            {
                ["restored_from_time"] = entry.PreciseTimestamp.ToString("o")
            }
        };

        var (info, _) = store.CreateVersion(objectId, entry.Data, restoredMetadata, _snapshotIntervalMs);
        return Task.FromResult(info);
    }

    /// <inheritdoc/>
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var fromEntry = store.GetEntry(fromVersionId)
            ?? throw new KeyNotFoundException($"Version '{fromVersionId}' not found.");
        var toEntry = store.GetEntry(toVersionId)
            ?? throw new KeyNotFoundException($"Version '{toVersionId}' not found.");

        var isIdentical = fromEntry.Info.ContentHash == toEntry.Info.ContentHash;
        var timeDiff = toEntry.PreciseTimestamp - fromEntry.PreciseTimestamp;

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toEntry.Info.SizeBytes - fromEntry.Info.SizeBytes,
            IsIdentical = isIdentical,
            DeltaBytes = null,
            Summary = isIdentical
                ? $"Versions are identical (time span: {timeDiff})"
                : $"Changed over {timeDiff.TotalMilliseconds:F0}ms ({fromEntry.PreciseTimestamp:o} -> {toEntry.PreciseTimestamp:o})"
        });
    }

    /// <inheritdoc/>
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult(0L);

        return Task.FromResult(store.GetVersionCount());
    }

    /// <summary>
    /// Gets the version state at a specific point in time.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="pointInTime">The point in time to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Version info at that point in time, or null if no version existed.</returns>
    public Task<VersionInfo?> GetVersionAtTimeAsync(string objectId, DateTime pointInTime, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<VersionInfo?>(null);

        var entry = store.GetVersionAtTime(pointInTime);
        return Task.FromResult(entry?.Info);
    }

    /// <summary>
    /// Gets the data at a specific point in time.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="pointInTime">The point in time to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data stream at that point in time.</returns>
    public Task<Stream?> GetDataAtTimeAsync(string objectId, DateTime pointInTime, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<Stream?>(null);

        var entry = store.GetVersionAtTime(pointInTime);
        if (entry == null)
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new MemoryStream(entry.Data, writable: false));
    }

    /// <summary>
    /// Gets all versions within a time range.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="from">Start of time range.</param>
    /// <param name="to">End of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of versions in the time range.</returns>
    public Task<IEnumerable<VersionInfo>> GetVersionsInRangeAsync(string objectId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IEnumerable<VersionInfo>>([]);

        var entries = store.GetVersionsInRange(from, to);
        return Task.FromResult(entries.Select(e => e.Info));
    }

    /// <summary>
    /// Applies retention policy to an object, removing old versions.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="retentionDays">Optional override for retention days.</param>
    /// <returns>Number of versions removed.</returns>
    public int ApplyRetentionPolicy(string objectId, int? retentionDays = null)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            return 0;

        return store.ApplyRetentionPolicy(retentionDays ?? _retentionDays);
    }

    /// <summary>
    /// Applies retention policy to all objects.
    /// </summary>
    /// <returns>Total number of versions removed.</returns>
    public int ApplyRetentionPolicyAll()
    {
        var total = 0;
        foreach (var store in _stores.Values)
        {
            total += store.ApplyRetentionPolicy(_retentionDays);
        }
        return total;
    }

    /// <summary>
    /// Gets WAL statistics for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>Tuple of (total entries, snapshots, deltas).</returns>
    public (int TotalEntries, int Snapshots, int Deltas) GetWalStats(string objectId)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            return (0, 0, 0);

        return store.GetWalStats();
    }
}
