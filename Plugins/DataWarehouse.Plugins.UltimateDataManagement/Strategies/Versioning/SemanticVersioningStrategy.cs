using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Semantic versioning strategy implementing SemVer for data versioning.
/// Provides Major.Minor.Patch versioning with breaking change detection,
/// version constraint matching, and automatic version increment based on change type.
/// </summary>
/// <remarks>
/// Features:
/// - Major.Minor.Patch versioning (SemVer 2.0 compliant)
/// - Breaking change detection via schema/structure analysis
/// - Version constraint matching (^1.0.0, ~1.2.0, >=1.0.0)
/// - Automatic version increment based on change type
/// - Compatibility checking between versions
/// - Thread-safe concurrent access
/// </remarks>
public sealed class SemanticVersioningStrategy : VersioningStrategyBase
{
    private readonly ConcurrentDictionary<string, ObjectSemVerStore> _stores = new();

    /// <summary>
    /// Represents a semantic version.
    /// </summary>
    public sealed class SemVer : IComparable<SemVer>
    {
        /// <summary>
        /// Major version (breaking changes).
        /// </summary>
        public int Major { get; }

        /// <summary>
        /// Minor version (new features, backwards compatible).
        /// </summary>
        public int Minor { get; }

        /// <summary>
        /// Patch version (bug fixes, backwards compatible).
        /// </summary>
        public int Patch { get; }

        /// <summary>
        /// Optional prerelease tag (e.g., "alpha", "beta.1").
        /// </summary>
        public string? Prerelease { get; }

        /// <summary>
        /// Optional build metadata.
        /// </summary>
        public string? BuildMetadata { get; }

        /// <summary>
        /// Creates a new semantic version.
        /// </summary>
        public SemVer(int major, int minor, int patch, string? prerelease = null, string? buildMetadata = null)
        {
            Major = major >= 0 ? major : 0;
            Minor = minor >= 0 ? minor : 0;
            Patch = patch >= 0 ? patch : 0;
            Prerelease = prerelease;
            BuildMetadata = buildMetadata;
        }

        /// <summary>
        /// Parses a semantic version string.
        /// </summary>
        public static SemVer Parse(string version)
        {
            var match = Regex.Match(version,
                @"^v?(\d+)\.(\d+)\.(\d+)(?:-([a-zA-Z0-9.-]+))?(?:\+([a-zA-Z0-9.-]+))?$");

            if (!match.Success)
                throw new FormatException($"Invalid semantic version: {version}");

            return new SemVer(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                match.Groups[4].Success ? match.Groups[4].Value : null,
                match.Groups[5].Success ? match.Groups[5].Value : null);
        }

        /// <summary>
        /// Tries to parse a semantic version string.
        /// </summary>
        public static bool TryParse(string version, out SemVer? result)
        {
            try
            {
                result = Parse(version);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        /// Returns the next major version (resets minor and patch).
        /// </summary>
        public SemVer IncrementMajor() => new(Major + 1, 0, 0);

        /// <summary>
        /// Returns the next minor version (resets patch).
        /// </summary>
        public SemVer IncrementMinor() => new(Major, Minor + 1, 0);

        /// <summary>
        /// Returns the next patch version.
        /// </summary>
        public SemVer IncrementPatch() => new(Major, Minor, Patch + 1);

        /// <inheritdoc/>
        public int CompareTo(SemVer? other)
        {
            if (other is null) return 1;

            var majorCmp = Major.CompareTo(other.Major);
            if (majorCmp != 0) return majorCmp;

            var minorCmp = Minor.CompareTo(other.Minor);
            if (minorCmp != 0) return minorCmp;

            var patchCmp = Patch.CompareTo(other.Patch);
            if (patchCmp != 0) return patchCmp;

            // Prerelease versions have lower precedence
            if (Prerelease == null && other.Prerelease != null) return 1;
            if (Prerelease != null && other.Prerelease == null) return -1;
            if (Prerelease != null && other.Prerelease != null)
                return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);

            return 0;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var result = $"{Major}.{Minor}.{Patch}";
            if (Prerelease != null) result += $"-{Prerelease}";
            if (BuildMetadata != null) result += $"+{BuildMetadata}";
            return result;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) =>
            obj is SemVer other && CompareTo(other) == 0;

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

        public static bool operator <(SemVer? left, SemVer? right) =>
            left is null ? right is not null : left.CompareTo(right) < 0;
        public static bool operator >(SemVer? left, SemVer? right) =>
            left is not null && left.CompareTo(right) > 0;
        public static bool operator <=(SemVer? left, SemVer? right) =>
            left is null || left.CompareTo(right) <= 0;
        public static bool operator >=(SemVer? left, SemVer? right) =>
            left is null ? right is null : left.CompareTo(right) >= 0;
        public static bool operator ==(SemVer? left, SemVer? right) =>
            ReferenceEquals(left, right) || (left is not null && left.Equals(right));
        public static bool operator !=(SemVer? left, SemVer? right) => !(left == right);
    }

    /// <summary>
    /// Type of change detected between versions.
    /// </summary>
    public enum ChangeType
    {
        /// <summary>No changes detected.</summary>
        None,
        /// <summary>Bug fix or minor correction (patch increment).</summary>
        Patch,
        /// <summary>New feature, backwards compatible (minor increment).</summary>
        Minor,
        /// <summary>Breaking change (major increment).</summary>
        Major
    }

    /// <summary>
    /// Version constraint for matching compatible versions.
    /// </summary>
    public sealed class VersionConstraint
    {
        private readonly Func<SemVer, bool> _matcher;
        private readonly string _original;

        private VersionConstraint(string original, Func<SemVer, bool> matcher)
        {
            _original = original;
            _matcher = matcher;
        }

        /// <summary>
        /// Parses a version constraint string.
        /// Supports: ^1.0.0, ~1.2.0, >=1.0.0, >1.0.0, <=1.0.0, less than 1.0.0, =1.0.0, 1.0.0, 1.x, 1.2.x
        /// </summary>
        public static VersionConstraint Parse(string constraint)
        {
            constraint = constraint.Trim();

            // Caret range: ^1.2.3 allows >=1.2.3 <2.0.0
            if (constraint.StartsWith('^'))
            {
                var version = SemVer.Parse(constraint[1..]);
                return new VersionConstraint(constraint, v =>
                    v >= version && v.Major == version.Major);
            }

            // Tilde range: ~1.2.3 allows >=1.2.3 <1.3.0
            if (constraint.StartsWith('~'))
            {
                var version = SemVer.Parse(constraint[1..]);
                return new VersionConstraint(constraint, v =>
                    v >= version && v.Major == version.Major && v.Minor == version.Minor);
            }

            // Comparison operators
            if (constraint.StartsWith(">="))
            {
                var version = SemVer.Parse(constraint[2..]);
                return new VersionConstraint(constraint, v => v >= version);
            }
            if (constraint.StartsWith('>'))
            {
                var version = SemVer.Parse(constraint[1..]);
                return new VersionConstraint(constraint, v => v > version);
            }
            if (constraint.StartsWith("<="))
            {
                var version = SemVer.Parse(constraint[2..]);
                return new VersionConstraint(constraint, v => v <= version);
            }
            if (constraint.StartsWith('<'))
            {
                var version = SemVer.Parse(constraint[1..]);
                return new VersionConstraint(constraint, v => v < version);
            }
            if (constraint.StartsWith('='))
            {
                var version = SemVer.Parse(constraint[1..]);
                return new VersionConstraint(constraint, v => v == version);
            }

            // Wildcard ranges: 1.x, 1.2.x
            if (constraint.Contains('x') || constraint.Contains('*'))
            {
                var parts = constraint.Split('.');
                if (parts.Length >= 1 && int.TryParse(parts[0], out var major))
                {
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var minor))
                    {
                        // 1.2.x
                        return new VersionConstraint(constraint, v =>
                            v.Major == major && v.Minor == minor);
                    }
                    // 1.x
                    return new VersionConstraint(constraint, v => v.Major == major);
                }
            }

            // Exact match
            var exactVersion = SemVer.Parse(constraint);
            return new VersionConstraint(constraint, v => v == exactVersion);
        }

        /// <summary>
        /// Checks if a version satisfies this constraint.
        /// </summary>
        public bool Satisfies(SemVer version) => _matcher(version);

        /// <inheritdoc/>
        public override string ToString() => _original;
    }

    /// <summary>
    /// Semantic version entry.
    /// </summary>
    private sealed class SemVerEntry
    {
        public required VersionInfo Info { get; init; }
        public required SemVer SemanticVersion { get; init; }
        public required byte[] Data { get; init; }
        public required string[] DataStructureKeys { get; init; }
    }

    /// <summary>
    /// Store for a single object's semantic versions.
    /// </summary>
    private sealed class ObjectSemVerStore
    {
        private readonly object _versionLock = new();
        private readonly Dictionary<string, SemVerEntry> _versions = new();
        private readonly List<string> _versionOrder = new();
        private SemVer _currentSemVer = new(0, 0, 0);
        private string? _currentVersionId;

        /// <summary>
        /// Creates a new semantic version.
        /// </summary>
        public VersionInfo CreateVersion(
            string objectId,
            byte[] data,
            SemVer semVer,
            string[] structureKeys,
            VersionMetadata metadata)
        {
            lock (_versionLock)
            {
                var versionId = GenerateVersionId();
                var contentHash = ComputeHashStatic(data);

                var info = new VersionInfo
                {
                    VersionId = versionId,
                    ObjectId = objectId,
                    VersionNumber = semVer.Major * 10000 + semVer.Minor * 100 + semVer.Patch,
                    ContentHash = contentHash,
                    SizeBytes = data.Length,
                    CreatedAt = DateTime.UtcNow,
                    Metadata = metadata with
                    {
                        Properties = new Dictionary<string, string>(metadata.Properties ?? [])
                        {
                            ["semver"] = semVer.ToString()
                        }
                    },
                    IsCurrent = true,
                    IsDeleted = false
                };

                // Mark previous current as not current
                if (_currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var prev))
                {
                    _versions[_currentVersionId] = new SemVerEntry
                    {
                        Info = prev.Info with { IsCurrent = false },
                        SemanticVersion = prev.SemanticVersion,
                        Data = prev.Data,
                        DataStructureKeys = prev.DataStructureKeys
                    };
                }

                _versions[versionId] = new SemVerEntry
                {
                    Info = info,
                    SemanticVersion = semVer,
                    Data = data,
                    DataStructureKeys = structureKeys
                };
                _versionOrder.Add(versionId);
                _currentVersionId = versionId;
                _currentSemVer = semVer;

                return info;
            }
        }

        /// <summary>
        /// Gets the current semantic version.
        /// </summary>
        public SemVer GetCurrentSemVer()
        {
            lock (_versionLock) return _currentSemVer;
        }

        /// <summary>
        /// Gets entry by version ID.
        /// </summary>
        public SemVerEntry? GetEntry(string versionId)
        {
            lock (_versionLock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry
                    : null;
            }
        }

        /// <summary>
        /// Gets the current entry.
        /// </summary>
        public SemVerEntry? GetCurrentEntry()
        {
            lock (_versionLock)
            {
                return _currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var entry)
                    ? entry
                    : null;
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
        /// Finds versions matching a constraint.
        /// </summary>
        public IEnumerable<(VersionInfo Info, SemVer Version)> FindMatchingVersions(VersionConstraint constraint)
        {
            lock (_versionLock)
            {
                return _versions.Values
                    .Where(e => !e.Info.IsDeleted && constraint.Satisfies(e.SemanticVersion))
                    .Select(e => (e.Info, e.SemanticVersion))
                    .OrderByDescending(x => x.SemanticVersion)
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

                _versions[versionId] = new SemVerEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    SemanticVersion = entry.SemanticVersion,
                    Data = entry.Data,
                    DataStructureKeys = entry.DataStructureKeys
                };

                if (_currentVersionId == versionId)
                {
                    _currentVersionId = null;
                    for (int i = _versionOrder.Count - 1; i >= 0; i--)
                    {
                        var id = _versionOrder[i];
                        if (!_versions[id].Info.IsDeleted)
                        {
                            _currentVersionId = id;
                            var v = _versions[id];
                            _versions[id] = new SemVerEntry
                            {
                                Info = v.Info with { IsCurrent = true },
                                SemanticVersion = v.SemanticVersion,
                                Data = v.Data,
                                DataStructureKeys = v.DataStructureKeys
                            };
                            _currentSemVer = v.SemanticVersion;
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
        /// Gets total version count.
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

        private static string ComputeHashStatic(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "versioning.semantic";

    /// <inheritdoc/>
    public override string DisplayName => "Semantic Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.3
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Semantic versioning strategy implementing SemVer 2.0 for data versioning. " +
        "Provides Major.Minor.Patch versioning with breaking change detection. " +
        "Supports version constraint matching (^1.0.0, ~1.2.0, >=1.0.0) and " +
        "automatic version increment based on change type analysis.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "semantic", "semver", "compatibility", "constraints"];

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, _ => new ObjectSemVerStore());
        var currentEntry = store.GetCurrentEntry();

        // Analyze structure for change detection
        var structureKeys = AnalyzeDataStructure(bytes);

        // Determine change type and new version
        SemVer newVersion;
        if (currentEntry == null)
        {
            // First version: 1.0.0
            newVersion = new SemVer(1, 0, 0);
        }
        else
        {
            // Check for explicit version in metadata
            if (metadata.Properties?.TryGetValue("semver", out var explicitVersion) == true &&
                SemVer.TryParse(explicitVersion, out var parsed) && parsed != null)
            {
                newVersion = parsed;
            }
            else
            {
                // Auto-detect change type
                var changeType = DetectChangeType(currentEntry.DataStructureKeys, structureKeys, currentEntry.Data, bytes);
                var currentSemVer = store.GetCurrentSemVer();

                newVersion = changeType switch
                {
                    ChangeType.Major => currentSemVer.IncrementMajor(),
                    ChangeType.Minor => currentSemVer.IncrementMinor(),
                    ChangeType.Patch => currentSemVer.IncrementPatch(),
                    _ => currentSemVer.IncrementPatch() // Default to patch
                };
            }
        }

        var info = store.CreateVersion(objectId, bytes, newVersion, structureKeys, metadata);
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
            Message = $"Restored from version {entry.SemanticVersion}",
            ParentVersionId = versionId,
            Properties = new Dictionary<string, string>(entry.Info.Metadata?.Properties ?? [])
            {
                ["restored_from"] = entry.SemanticVersion.ToString()
            }
        };

        // Restoration creates a new patch version
        var currentSemVer = store.GetCurrentSemVer();
        var restoredVersion = currentSemVer.IncrementPatch();

        var info = store.CreateVersion(objectId, entry.Data, restoredVersion, entry.DataStructureKeys, restoredMetadata);
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
        var changeType = DetectChangeType(fromEntry.DataStructureKeys, toEntry.DataStructureKeys, fromEntry.Data, toEntry.Data);

        var versionDiff = $"{fromEntry.SemanticVersion} -> {toEntry.SemanticVersion}";
        var summary = isIdentical
            ? $"Versions are identical ({versionDiff})"
            : $"{changeType} change: {versionDiff}";

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toEntry.Info.SizeBytes - fromEntry.Info.SizeBytes,
            IsIdentical = isIdentical,
            DeltaBytes = null,
            Summary = summary
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
    /// Finds versions matching a constraint.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="constraint">Version constraint (e.g., "^1.0.0", "~1.2.0", ">=1.0.0").</param>
    /// <returns>Matching versions ordered by version descending.</returns>
    public IEnumerable<(VersionInfo Info, SemVer Version)> FindMatchingVersions(string objectId, string constraint)
    {
        if (!_stores.TryGetValue(objectId, out var store))
            return [];

        var parsed = VersionConstraint.Parse(constraint);
        return store.FindMatchingVersions(parsed);
    }

    /// <summary>
    /// Checks if two versions are compatible (same major version).
    /// </summary>
    /// <param name="version1">First version string.</param>
    /// <param name="version2">Second version string.</param>
    /// <returns>True if versions are compatible.</returns>
    public static bool AreCompatible(string version1, string version2)
    {
        var v1 = SemVer.Parse(version1);
        var v2 = SemVer.Parse(version2);
        return v1.Major == v2.Major;
    }

    /// <summary>
    /// Analyzes data structure to extract keys for change detection.
    /// </summary>
    private static string[] AnalyzeDataStructure(byte[] data)
    {
        var keys = new List<string>();

        // Simple heuristic: look for JSON-like keys or delimiters
        var text = System.Text.Encoding.UTF8.GetString(data);

        // Extract quoted strings that look like keys
        var matches = Regex.Matches(text, @"""([a-zA-Z_][a-zA-Z0-9_]*)""\s*:");
        foreach (Match match in matches)
        {
            keys.Add(match.Groups[1].Value);
        }

        // Also extract XML-like tags
        var xmlMatches = Regex.Matches(text, @"<([a-zA-Z_][a-zA-Z0-9_]*)[\s>]");
        foreach (Match match in xmlMatches)
        {
            keys.Add($"xml:{match.Groups[1].Value}");
        }

        // Add size-based signature
        keys.Add($"size:{data.Length}");

        return keys.Distinct().OrderBy(k => k).ToArray();
    }

    /// <summary>
    /// Detects the type of change between two versions.
    /// </summary>
    private static ChangeType DetectChangeType(string[] oldKeys, string[] newKeys, byte[] oldData, byte[] newData)
    {
        var oldSet = new HashSet<string>(oldKeys);
        var newSet = new HashSet<string>(newKeys);

        // Keys removed = breaking change (major)
        var removed = oldSet.Except(newSet).Where(k => !k.StartsWith("size:")).ToList();
        if (removed.Count > 0)
            return ChangeType.Major;

        // Keys added = new feature (minor)
        var added = newSet.Except(oldSet).Where(k => !k.StartsWith("size:")).ToList();
        if (added.Count > 0)
            return ChangeType.Minor;

        // Same keys, different content = patch
        if (!oldData.SequenceEqual(newData))
            return ChangeType.Patch;

        return ChangeType.None;
    }
}
