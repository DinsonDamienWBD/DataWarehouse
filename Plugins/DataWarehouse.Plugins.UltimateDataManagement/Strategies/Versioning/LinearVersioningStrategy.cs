using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Sequential version numbering strategy (v1, v2, v3...).
/// Provides simple, linear version history with auto-increment and rollback support.
/// Thread-safe for concurrent access across multiple objects.
/// </summary>
/// <remarks>
/// Features:
/// - Auto-increment version numbers on each save
/// - Full version history tracking
/// - Rollback/restore to any previous version
/// - Efficient diff computation between versions
/// - Atomic version creation with hash verification
/// </remarks>
public sealed class LinearVersioningStrategy : VersioningStrategyBase
{
    private readonly BoundedDictionary<string, ObjectVersionStore> _stores = new BoundedDictionary<string, ObjectVersionStore>(1000);
    private readonly object _lockObject = new();

    /// <summary>
    /// Internal store for a single object's versions.
    /// </summary>
    private sealed class ObjectVersionStore
    {
        private readonly object _versionLock = new();
        private readonly Dictionary<string, VersionEntry> _versions = new();
        private readonly List<string> _versionOrder = new();
        private long _nextVersionNumber = 1;
        private string? _currentVersionId;

        public sealed class VersionEntry
        {
            public required VersionInfo Info { get; init; }
            public required byte[] Data { get; init; }
        }

        /// <summary>
        /// Creates a new version with the next sequential number.
        /// </summary>
        public VersionInfo CreateVersion(string objectId, byte[] data, VersionMetadata metadata)
        {
            lock (_versionLock)
            {
                var versionId = GenerateVersionId();
                var versionNumber = _nextVersionNumber++;
                var contentHash = ComputeHash(data);

                var info = new VersionInfo
                {
                    VersionId = versionId,
                    ObjectId = objectId,
                    VersionNumber = versionNumber,
                    ContentHash = contentHash,
                    SizeBytes = data.Length,
                    CreatedAt = DateTime.UtcNow,
                    Metadata = metadata,
                    IsCurrent = true,
                    IsDeleted = false
                };

                // Mark previous current as not current
                if (_currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var prev))
                {
                    _versions[_currentVersionId] = new VersionEntry
                    {
                        Info = prev.Info with { IsCurrent = false },
                        Data = prev.Data
                    };
                }

                _versions[versionId] = new VersionEntry { Info = info, Data = data };
                _versionOrder.Add(versionId);
                _currentVersionId = versionId;

                return info;
            }
        }

        /// <summary>
        /// Gets version data by ID.
        /// </summary>
        public byte[]? GetVersionData(string versionId)
        {
            lock (_versionLock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry.Data
                    : null;
            }
        }

        /// <summary>
        /// Lists all versions with optional filtering.
        /// </summary>
        public IEnumerable<VersionInfo> ListVersions(VersionListOptions options)
        {
            lock (_versionLock)
            {
                var query = _versionOrder
                    .Select(id => _versions[id].Info)
                    .Where(v => options.IncludeDeleted || !v.IsDeleted);

                if (options.FromDate.HasValue)
                    query = query.Where(v => v.CreatedAt >= options.FromDate.Value);

                if (options.ToDate.HasValue)
                    query = query.Where(v => v.CreatedAt <= options.ToDate.Value);

                return query
                    .OrderByDescending(v => v.VersionNumber)
                    .Take(options.MaxResults)
                    .ToList();
            }
        }

        /// <summary>
        /// Soft-deletes a version.
        /// </summary>
        public bool DeleteVersion(string versionId)
        {
            lock (_versionLock)
            {
                if (!_versions.TryGetValue(versionId, out var entry) || entry.Info.IsDeleted)
                    return false;

                _versions[versionId] = new VersionEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    Data = entry.Data
                };

                // If this was current, find the previous non-deleted version
                if (_currentVersionId == versionId)
                {
                    _currentVersionId = null;
                    for (int i = _versionOrder.Count - 1; i >= 0; i--)
                    {
                        var id = _versionOrder[i];
                        if (!_versions[id].Info.IsDeleted)
                        {
                            _currentVersionId = id;
                            _versions[id] = new VersionEntry
                            {
                                Info = _versions[id].Info with { IsCurrent = true },
                                Data = _versions[id].Data
                            };
                            break;
                        }
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Gets the current version info.
        /// </summary>
        public VersionInfo? GetCurrentVersion()
        {
            lock (_versionLock)
            {
                return _currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var entry)
                    ? entry.Info
                    : null;
            }
        }

        /// <summary>
        /// Restores a version by creating a new version with its content.
        /// </summary>
        public VersionInfo? RestoreVersion(string objectId, string versionId)
        {
            lock (_versionLock)
            {
                if (!_versions.TryGetValue(versionId, out var entry))
                    return null;

                var metadata = new VersionMetadata
                {
                    Author = entry.Info.Metadata?.Author,
                    Message = $"Restored from version {entry.Info.VersionNumber}",
                    ParentVersionId = versionId,
                    Properties = entry.Info.Metadata?.Properties
                };

                return CreateVersion(objectId, entry.Data, metadata);
            }
        }

        /// <summary>
        /// Gets version info by ID.
        /// </summary>
        public VersionInfo? GetVersionInfo(string versionId)
        {
            lock (_versionLock)
            {
                return _versions.TryGetValue(versionId, out var entry) ? entry.Info : null;
            }
        }

        /// <summary>
        /// Gets total version count (excluding deleted if not specified).
        /// </summary>
        public long GetVersionCount(bool includeDeleted = false)
        {
            lock (_versionLock)
            {
                return includeDeleted
                    ? _versions.Count
                    : _versions.Values.Count(v => !v.Info.IsDeleted);
            }
        }

        private static string GenerateVersionId() => Guid.NewGuid().ToString("N")[..12];

        private static string ComputeHash(byte[] data)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "versioning.linear";

    /// <inheritdoc/>
    public override string DisplayName => "Linear Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Sequential version numbering strategy with auto-increment on each save. " +
        "Provides simple linear version history (v1, v2, v3...) with full rollback support. " +
        "Ideal for straightforward version tracking without branching complexity.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "linear", "sequential", "history", "rollback"];

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream(65536);
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, _ => new ObjectVersionStore());
        var info = store.CreateVersion(objectId, bytes, metadata);

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

        var restored = store.RestoreVersion(objectId, versionId)
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'.");

        return Task.FromResult(restored);
    }

    /// <inheritdoc/>
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var fromData = store.GetVersionData(fromVersionId)
            ?? throw new KeyNotFoundException($"Version '{fromVersionId}' not found.");
        var toData = store.GetVersionData(toVersionId)
            ?? throw new KeyNotFoundException($"Version '{toVersionId}' not found.");

        var fromInfo = store.GetVersionInfo(fromVersionId)!;
        var toInfo = store.GetVersionInfo(toVersionId)!;

        var isIdentical = fromInfo.ContentHash == toInfo.ContentHash;

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toData.Length - fromData.Length,
            IsIdentical = isIdentical,
            DeltaBytes = isIdentical ? null : ComputeSimpleDelta(fromData, toData),
            Summary = isIdentical
                ? "Versions are identical"
                : $"Size changed from {fromData.Length} to {toData.Length} bytes"
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
    /// Computes a simple XOR-based delta between two byte arrays.
    /// </summary>
    private static byte[] ComputeSimpleDelta(byte[] from, byte[] to)
    {
        // Simple delta: store length prefix + XOR for overlapping bytes + additional bytes
        using var ms = new MemoryStream(65536);
        using var writer = new BinaryWriter(ms);

        writer.Write(from.Length);
        writer.Write(to.Length);

        var minLen = Math.Min(from.Length, to.Length);
        for (int i = 0; i < minLen; i++)
        {
            writer.Write((byte)(from[i] ^ to[i]));
        }

        // Additional bytes if 'to' is longer
        if (to.Length > from.Length)
        {
            writer.Write(to, minLen, to.Length - minLen);
        }

        return ms.ToArray();
    }
}
