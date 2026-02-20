using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Version-based retention strategy that keeps N versions and prunes older ones.
/// Supports configurable version limits with optional time-based fallback.
/// </summary>
/// <remarks>
/// Features:
/// - Keep N most recent versions
/// - Per-object version tracking
/// - Time-based fallback for old versions
/// - Configurable minimum versions to keep
/// - Version history management
/// </remarks>
public sealed class VersionRetentionStrategy : RetentionStrategyBase
{
    private readonly BoundedDictionary<string, ObjectVersionHistory> _versionHistories = new BoundedDictionary<string, ObjectVersionHistory>(1000);
    private readonly int _maxVersions;
    private readonly int _minVersions;
    private readonly TimeSpan? _maxVersionAge;

    /// <summary>
    /// Initializes with default 10 versions maximum, 1 minimum.
    /// </summary>
    public VersionRetentionStrategy() : this(10, 1, null) { }

    /// <summary>
    /// Initializes with specified version limits.
    /// </summary>
    /// <param name="maxVersions">Maximum versions to keep.</param>
    /// <param name="minVersions">Minimum versions to always keep.</param>
    /// <param name="maxVersionAge">Optional maximum age for versions beyond minimum.</param>
    public VersionRetentionStrategy(int maxVersions, int minVersions, TimeSpan? maxVersionAge)
    {
        if (maxVersions < 1)
            throw new ArgumentOutOfRangeException(nameof(maxVersions), "Must keep at least 1 version");
        if (minVersions < 0 || minVersions > maxVersions)
            throw new ArgumentOutOfRangeException(nameof(minVersions), "Min versions must be between 0 and max");

        _maxVersions = maxVersions;
        _minVersions = minVersions;
        _maxVersionAge = maxVersionAge;
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.version";

    /// <inheritdoc/>
    public override string DisplayName => "Version Retention";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Version-based retention keeping {_maxVersions} versions (minimum {_minVersions}). " +
        "Automatically prunes older versions while preserving version history. " +
        "Ideal for document management and version control scenarios.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "versioning", "prune", "history", "document-management"];

    /// <summary>
    /// Gets the maximum versions to keep.
    /// </summary>
    public int MaxVersions => _maxVersions;

    /// <summary>
    /// Gets the minimum versions to always keep.
    /// </summary>
    public int MinVersions => _minVersions;

    /// <summary>
    /// Registers a new version of an object.
    /// </summary>
    /// <param name="baseObjectId">Base object identifier (without version suffix).</param>
    /// <param name="versionId">Version identifier.</param>
    /// <param name="version">Version number.</param>
    /// <param name="createdAt">When this version was created.</param>
    /// <param name="size">Size of this version.</param>
    public void RegisterVersion(string baseObjectId, string versionId, int version, DateTime createdAt, long size)
    {
        var history = _versionHistories.GetOrAdd(baseObjectId, _ => new ObjectVersionHistory
        {
            BaseObjectId = baseObjectId,
            Versions = new List<VersionInfo>()
        });

        lock (history)
        {
            history.Versions.Add(new VersionInfo
            {
                VersionId = versionId,
                VersionNumber = version,
                CreatedAt = createdAt,
                Size = size
            });

            // Sort by version number descending (newest first)
            history.Versions = history.Versions.OrderByDescending(v => v.VersionNumber).ToList();
        }
    }

    /// <summary>
    /// Gets the version history for an object.
    /// </summary>
    /// <param name="baseObjectId">Base object identifier.</param>
    /// <returns>Version history or null.</returns>
    public IReadOnlyList<VersionInfo>? GetVersionHistory(string baseObjectId)
    {
        if (_versionHistories.TryGetValue(baseObjectId, out var history))
        {
            lock (history)
            {
                return history.Versions.ToList().AsReadOnly();
            }
        }
        return null;
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Determine base object ID
        var baseObjectId = GetBaseObjectId(data);

        if (!_versionHistories.TryGetValue(baseObjectId, out var history))
        {
            // No version history - keep by default
            return Task.FromResult(RetentionDecision.Retain(
                "No version history found - keeping as single version",
                DateTime.UtcNow.AddDays(30)));
        }

        lock (history)
        {
            var versions = history.Versions;
            var versionIndex = versions.FindIndex(v => v.VersionId == data.ObjectId);

            if (versionIndex == -1)
            {
                // Not in version history, treat as newest
                return Task.FromResult(RetentionDecision.Retain(
                    "Version not in history - keeping",
                    DateTime.UtcNow.AddDays(1)));
            }

            // Always keep latest version
            if (data.IsLatestVersion || versionIndex == 0)
            {
                return Task.FromResult(RetentionDecision.Retain(
                    "Latest version - always keep",
                    DateTime.UtcNow.AddDays(1)));
            }

            // Keep minimum versions
            if (versionIndex < _minVersions)
            {
                return Task.FromResult(RetentionDecision.Retain(
                    $"Within minimum versions ({_minVersions}) - keeping",
                    DateTime.UtcNow.AddDays(7)));
            }

            // Check max versions
            if (versionIndex >= _maxVersions)
            {
                return Task.FromResult(RetentionDecision.Delete(
                    $"Exceeds maximum versions ({_maxVersions}) - version {data.Version} eligible for deletion"));
            }

            // Check version age if configured
            if (_maxVersionAge.HasValue && versionIndex >= _minVersions)
            {
                var versionInfo = versions[versionIndex];
                var age = DateTime.UtcNow - versionInfo.CreatedAt;

                if (age > _maxVersionAge.Value)
                {
                    return Task.FromResult(RetentionDecision.Delete(
                        $"Version age ({age.TotalDays:F0} days) exceeds maximum ({_maxVersionAge.Value.TotalDays:F0} days)"));
                }
            }

            // Within limits - keep
            var nextEval = _maxVersionAge.HasValue
                ? versions[versionIndex].CreatedAt + _maxVersionAge.Value
                : DateTime.UtcNow.AddDays(30);

            return Task.FromResult(RetentionDecision.Retain(
                $"Version {data.Version} within retention limits ({versionIndex + 1} of {_maxVersions})",
                nextEval));
        }
    }

    /// <inheritdoc/>
    protected override async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct)
    {
        var affected = 0;

        foreach (var history in _versionHistories.Values)
        {
            ct.ThrowIfCancellationRequested();

            lock (history)
            {
                var versionsToRemove = new List<VersionInfo>();

                for (int i = 0; i < history.Versions.Count; i++)
                {
                    // Skip if within minimum
                    if (i < _minVersions)
                        continue;

                    var version = history.Versions[i];

                    // Check max versions
                    if (i >= _maxVersions)
                    {
                        versionsToRemove.Add(version);
                        continue;
                    }

                    // Check age
                    if (_maxVersionAge.HasValue)
                    {
                        var age = DateTime.UtcNow - version.CreatedAt;
                        if (age > _maxVersionAge.Value)
                        {
                            versionsToRemove.Add(version);
                        }
                    }
                }

                if (!scope.DryRun)
                {
                    foreach (var version in versionsToRemove)
                    {
                        history.Versions.Remove(version);
                        TrackedObjects.TryRemove(version.VersionId, out _);
                    }
                }

                affected += versionsToRemove.Count;
            }
        }

        return affected;
    }

    private static string GetBaseObjectId(DataObject data)
    {
        // Try to extract base object ID from metadata
        if (data.Metadata?.TryGetValue("baseObjectId", out var baseId) == true)
        {
            return baseId?.ToString() ?? data.ObjectId;
        }

        // Try to remove version suffix (e.g., "doc123_v2" -> "doc123")
        var objectId = data.ObjectId;
        var lastUnderscore = objectId.LastIndexOf('_');
        if (lastUnderscore > 0 && objectId.Length > lastUnderscore + 1)
        {
            var suffix = objectId[(lastUnderscore + 1)..];
            if (suffix.StartsWith('v') && int.TryParse(suffix[1..], out _))
            {
                return objectId[..lastUnderscore];
            }
        }

        return objectId;
    }

    private sealed class ObjectVersionHistory
    {
        public required string BaseObjectId { get; init; }
        public required List<VersionInfo> Versions { get; set; }
    }
}

/// <summary>
/// Information about a specific version.
/// </summary>
public sealed class VersionInfo
{
    /// <summary>
    /// Unique version identifier.
    /// </summary>
    public required string VersionId { get; init; }

    /// <summary>
    /// Version number.
    /// </summary>
    public required int VersionNumber { get; init; }

    /// <summary>
    /// When this version was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Size of this version in bytes.
    /// </summary>
    public required long Size { get; init; }
}
