using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Git-like branching versioning strategy.
/// Provides branch creation, merging, deletion, and isolation with conflict detection.
/// Thread-safe for concurrent branch operations.
/// </summary>
/// <remarks>
/// Features:
/// - Create, merge, and delete branches
/// - Branch isolation with independent version histories
/// - Merge conflict detection via hash comparison
/// - Branch metadata and lineage tracking
/// - Efficient branch switching
/// </remarks>
public sealed class BranchingVersioningStrategy : VersioningStrategyBase
{
    private readonly ConcurrentDictionary<string, BranchStore> _stores = new();
    private const string DefaultBranch = "main";

    /// <summary>
    /// Represents a branch with its version history.
    /// </summary>
    private sealed class Branch
    {
        public required string Name { get; init; }
        public required string ObjectId { get; init; }
        public string? ParentBranch { get; init; }
        public string? BranchPoint { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool IsDeleted { get; set; }
        public List<VersionEntry> Versions { get; } = new();
        public string? CurrentVersionId { get; set; }
    }

    /// <summary>
    /// A version entry within a branch.
    /// </summary>
    private sealed class VersionEntry
    {
        public required VersionInfo Info { get; init; }
        public required byte[] Data { get; init; }
    }

    /// <summary>
    /// Store for a single object's branches.
    /// </summary>
    private sealed class BranchStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Branch> _branches = new();
        private readonly Dictionary<string, VersionEntry> _allVersions = new();
        private long _nextVersionNumber = 1;

        public BranchStore(string objectId)
        {
            _branches[DefaultBranch] = new Branch
            {
                Name = DefaultBranch,
                ObjectId = objectId,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a new version on the specified branch.
        /// </summary>
        public VersionInfo CreateVersion(string objectId, byte[] data, VersionMetadata metadata)
        {
            lock (_lock)
            {
                var branchName = metadata.Branch ?? DefaultBranch;

                if (!_branches.TryGetValue(branchName, out var branch) || branch.IsDeleted)
                    throw new InvalidOperationException($"Branch '{branchName}' does not exist.");

                var versionId = GenerateVersionId();
                var contentHash = ComputeHash(data);

                var info = new VersionInfo
                {
                    VersionId = versionId,
                    ObjectId = objectId,
                    VersionNumber = _nextVersionNumber++,
                    ContentHash = contentHash,
                    SizeBytes = data.Length,
                    CreatedAt = DateTime.UtcNow,
                    Metadata = metadata with { Branch = branchName, ParentVersionId = branch.CurrentVersionId },
                    IsCurrent = true,
                    IsDeleted = false
                };

                var entry = new VersionEntry { Info = info, Data = data };

                // Update previous current version
                if (branch.CurrentVersionId != null && _allVersions.TryGetValue(branch.CurrentVersionId, out var prev))
                {
                    var updatedPrev = new VersionEntry
                    {
                        Info = prev.Info with { IsCurrent = false },
                        Data = prev.Data
                    };
                    _allVersions[branch.CurrentVersionId] = updatedPrev;
                    var idx = branch.Versions.FindIndex(v => v.Info.VersionId == branch.CurrentVersionId);
                    if (idx >= 0) branch.Versions[idx] = updatedPrev;
                }

                branch.Versions.Add(entry);
                branch.CurrentVersionId = versionId;
                _allVersions[versionId] = entry;

                return info;
            }
        }

        /// <summary>
        /// Creates a new branch from a version point.
        /// </summary>
        public Branch CreateBranch(string branchName, string? fromBranch = null, string? fromVersion = null)
        {
            lock (_lock)
            {
                if (_branches.ContainsKey(branchName))
                    throw new InvalidOperationException($"Branch '{branchName}' already exists.");

                var sourceBranch = fromBranch ?? DefaultBranch;
                if (!_branches.TryGetValue(sourceBranch, out var source) || source.IsDeleted)
                    throw new InvalidOperationException($"Source branch '{sourceBranch}' does not exist.");

                var branchPoint = fromVersion ?? source.CurrentVersionId;

                var newBranch = new Branch
                {
                    Name = branchName,
                    ObjectId = source.ObjectId,
                    ParentBranch = sourceBranch,
                    BranchPoint = branchPoint,
                    CreatedAt = DateTime.UtcNow,
                    CurrentVersionId = branchPoint
                };

                // Copy versions up to branch point
                foreach (var v in source.Versions)
                {
                    newBranch.Versions.Add(v);
                    if (v.Info.VersionId == branchPoint)
                        break;
                }

                _branches[branchName] = newBranch;
                return newBranch;
            }
        }

        /// <summary>
        /// Merges one branch into another.
        /// </summary>
        public MergeResult MergeBranch(string sourceBranch, string targetBranch)
        {
            lock (_lock)
            {
                if (!_branches.TryGetValue(sourceBranch, out var source) || source.IsDeleted)
                    throw new InvalidOperationException($"Source branch '{sourceBranch}' does not exist.");

                if (!_branches.TryGetValue(targetBranch, out var target) || target.IsDeleted)
                    throw new InvalidOperationException($"Target branch '{targetBranch}' does not exist.");

                if (source.CurrentVersionId == null)
                    throw new InvalidOperationException($"Source branch '{sourceBranch}' has no versions.");

                // Check for conflicts by comparing content hashes
                var sourceContent = _allVersions[source.CurrentVersionId];
                var hasConflict = false;

                if (target.CurrentVersionId != null)
                {
                    var targetContent = _allVersions[target.CurrentVersionId];
                    // Conflict if both have diverged from common ancestor
                    var commonAncestor = FindCommonAncestor(source, target);
                    if (commonAncestor != null)
                    {
                        hasConflict = source.Versions.Any(v =>
                            v.Info.VersionNumber > commonAncestor.Info.VersionNumber) &&
                            target.Versions.Any(v =>
                            v.Info.VersionNumber > commonAncestor.Info.VersionNumber &&
                            v.Info.ContentHash != sourceContent.Info.ContentHash);
                    }
                }

                if (hasConflict)
                {
                    return new MergeResult
                    {
                        Success = false,
                        HasConflicts = true,
                        Message = $"Merge conflict detected between '{sourceBranch}' and '{targetBranch}'."
                    };
                }

                // Fast-forward or create merge commit
                var mergeVersionId = GenerateVersionId();
                var mergeInfo = new VersionInfo
                {
                    VersionId = mergeVersionId,
                    ObjectId = target.ObjectId,
                    VersionNumber = _nextVersionNumber++,
                    ContentHash = sourceContent.Info.ContentHash,
                    SizeBytes = sourceContent.Data.Length,
                    CreatedAt = DateTime.UtcNow,
                    Metadata = new VersionMetadata
                    {
                        Message = $"Merge '{sourceBranch}' into '{targetBranch}'",
                        Branch = targetBranch,
                        ParentVersionId = target.CurrentVersionId,
                        Properties = new Dictionary<string, string>
                        {
                            ["MergeSource"] = sourceBranch,
                            ["MergeSourceVersion"] = source.CurrentVersionId!
                        }
                    },
                    IsCurrent = true,
                    IsDeleted = false
                };

                var mergeEntry = new VersionEntry { Info = mergeInfo, Data = sourceContent.Data };

                if (target.CurrentVersionId != null && _allVersions.TryGetValue(target.CurrentVersionId, out var prev))
                {
                    _allVersions[target.CurrentVersionId] = new VersionEntry
                    {
                        Info = prev.Info with { IsCurrent = false },
                        Data = prev.Data
                    };
                }

                target.Versions.Add(mergeEntry);
                target.CurrentVersionId = mergeVersionId;
                _allVersions[mergeVersionId] = mergeEntry;

                return new MergeResult
                {
                    Success = true,
                    HasConflicts = false,
                    MergeVersionId = mergeVersionId,
                    Message = $"Successfully merged '{sourceBranch}' into '{targetBranch}'."
                };
            }
        }

        /// <summary>
        /// Deletes a branch (soft delete).
        /// </summary>
        public bool DeleteBranch(string branchName)
        {
            lock (_lock)
            {
                if (branchName == DefaultBranch)
                    throw new InvalidOperationException("Cannot delete the default branch.");

                if (!_branches.TryGetValue(branchName, out var branch) || branch.IsDeleted)
                    return false;

                branch.IsDeleted = true;
                return true;
            }
        }

        /// <summary>
        /// Gets all branches.
        /// </summary>
        public IEnumerable<BranchInfo> ListBranches(bool includeDeleted = false)
        {
            lock (_lock)
            {
                return _branches.Values
                    .Where(b => includeDeleted || !b.IsDeleted)
                    .Select(b => new BranchInfo
                    {
                        Name = b.Name,
                        ObjectId = b.ObjectId,
                        ParentBranch = b.ParentBranch,
                        BranchPoint = b.BranchPoint,
                        CreatedAt = b.CreatedAt,
                        CurrentVersionId = b.CurrentVersionId,
                        VersionCount = b.Versions.Count,
                        IsDeleted = b.IsDeleted
                    })
                    .ToList();
            }
        }

        /// <summary>
        /// Gets version data.
        /// </summary>
        public byte[]? GetVersionData(string versionId)
        {
            lock (_lock)
            {
                return _allVersions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry.Data
                    : null;
            }
        }

        /// <summary>
        /// Lists versions, optionally filtered by branch.
        /// </summary>
        public IEnumerable<VersionInfo> ListVersions(VersionListOptions options)
        {
            lock (_lock)
            {
                IEnumerable<VersionEntry> query;

                if (!string.IsNullOrEmpty(options.Branch))
                {
                    if (!_branches.TryGetValue(options.Branch, out var branch))
                        return [];
                    query = branch.Versions;
                }
                else
                {
                    query = _allVersions.Values;
                }

                var filtered = query
                    .Where(v => options.IncludeDeleted || !v.Info.IsDeleted)
                    .Select(v => v.Info);

                if (options.FromDate.HasValue)
                    filtered = filtered.Where(v => v.CreatedAt >= options.FromDate.Value);

                if (options.ToDate.HasValue)
                    filtered = filtered.Where(v => v.CreatedAt <= options.ToDate.Value);

                return filtered
                    .OrderByDescending(v => v.VersionNumber)
                    .Take(options.MaxResults)
                    .ToList();
            }
        }

        /// <summary>
        /// Gets current version for a branch.
        /// </summary>
        public VersionInfo? GetCurrentVersion(string? branchName = null)
        {
            lock (_lock)
            {
                var branch = branchName ?? DefaultBranch;
                if (!_branches.TryGetValue(branch, out var b) || b.IsDeleted || b.CurrentVersionId == null)
                    return null;

                return _allVersions.TryGetValue(b.CurrentVersionId, out var entry) ? entry.Info : null;
            }
        }

        /// <summary>
        /// Deletes a version (soft delete).
        /// </summary>
        public bool DeleteVersion(string versionId)
        {
            lock (_lock)
            {
                if (!_allVersions.TryGetValue(versionId, out var entry) || entry.Info.IsDeleted)
                    return false;

                var updated = new VersionEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    Data = entry.Data
                };
                _allVersions[versionId] = updated;

                // Update in branch if it's current
                foreach (var branch in _branches.Values)
                {
                    if (branch.CurrentVersionId == versionId)
                    {
                        branch.CurrentVersionId = branch.Versions
                            .Where(v => !v.Info.IsDeleted && v.Info.VersionId != versionId)
                            .OrderByDescending(v => v.Info.VersionNumber)
                            .FirstOrDefault()?.Info.VersionId;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Restores a version as a new version.
        /// </summary>
        public VersionInfo? RestoreVersion(string objectId, string versionId, string? toBranch = null)
        {
            lock (_lock)
            {
                if (!_allVersions.TryGetValue(versionId, out var entry))
                    return null;

                var targetBranch = toBranch ?? entry.Info.Metadata?.Branch ?? DefaultBranch;

                var metadata = new VersionMetadata
                {
                    Author = entry.Info.Metadata?.Author,
                    Message = $"Restored from version {entry.Info.VersionNumber}",
                    Branch = targetBranch,
                    ParentVersionId = versionId
                };

                // Temporarily set branch in metadata for CreateVersion
                return CreateVersion(objectId, entry.Data, metadata);
            }
        }

        /// <summary>
        /// Gets version info by ID.
        /// </summary>
        public VersionInfo? GetVersionInfo(string versionId)
        {
            lock (_lock)
            {
                return _allVersions.TryGetValue(versionId, out var entry) ? entry.Info : null;
            }
        }

        /// <summary>
        /// Gets version count.
        /// </summary>
        public long GetVersionCount(string? branchName = null)
        {
            lock (_lock)
            {
                if (branchName != null)
                {
                    return _branches.TryGetValue(branchName, out var branch)
                        ? branch.Versions.Count(v => !v.Info.IsDeleted)
                        : 0;
                }
                return _allVersions.Values.Count(v => !v.Info.IsDeleted);
            }
        }

        private VersionEntry? FindCommonAncestor(Branch a, Branch b)
        {
            var aVersions = new HashSet<string>(a.Versions.Select(v => v.Info.VersionId));
            return b.Versions
                .OrderByDescending(v => v.Info.VersionNumber)
                .FirstOrDefault(v => aVersions.Contains(v.Info.VersionId));
        }

        private static string GenerateVersionId() => Guid.NewGuid().ToString("N")[..12];

        private static string ComputeHash(byte[] data)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Information about a branch.
    /// </summary>
    public sealed class BranchInfo
    {
        /// <summary>Branch name.</summary>
        public required string Name { get; init; }
        /// <summary>Object ID this branch belongs to.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Parent branch name.</summary>
        public string? ParentBranch { get; init; }
        /// <summary>Version ID where branch was created.</summary>
        public string? BranchPoint { get; init; }
        /// <summary>When the branch was created.</summary>
        public DateTime CreatedAt { get; init; }
        /// <summary>Current version ID on this branch.</summary>
        public string? CurrentVersionId { get; init; }
        /// <summary>Number of versions on this branch.</summary>
        public int VersionCount { get; init; }
        /// <summary>Whether the branch is deleted.</summary>
        public bool IsDeleted { get; init; }
    }

    /// <summary>
    /// Result of a merge operation.
    /// </summary>
    public sealed class MergeResult
    {
        /// <summary>Whether the merge succeeded.</summary>
        public bool Success { get; init; }
        /// <summary>Whether there were conflicts.</summary>
        public bool HasConflicts { get; init; }
        /// <summary>The merge commit version ID.</summary>
        public string? MergeVersionId { get; init; }
        /// <summary>Status message.</summary>
        public string? Message { get; init; }
    }

    /// <inheritdoc/>
    public override string StrategyId => "versioning.branching";

    /// <inheritdoc/>
    public override string DisplayName => "Branching Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Git-like branching versioning strategy with branch creation, merging, and isolation. " +
        "Supports independent version histories per branch with conflict detection on merge. " +
        "Ideal for collaborative editing and feature-based development workflows.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "branching", "git", "merge", "isolation"];

    /// <summary>
    /// Creates a new branch from an existing branch.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="branchName">New branch name.</param>
    /// <param name="fromBranch">Source branch (defaults to main).</param>
    /// <param name="fromVersion">Version to branch from (defaults to current).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Branch information.</returns>
    public Task<BranchInfo> CreateBranchAsync(string objectId, string branchName, string? fromBranch = null, string? fromVersion = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ct.ThrowIfCancellationRequested();

        var store = _stores.GetOrAdd(objectId, id => new BranchStore(id));
        var branch = store.CreateBranch(branchName, fromBranch, fromVersion);

        return Task.FromResult(new BranchInfo
        {
            Name = branch.Name,
            ObjectId = branch.ObjectId,
            ParentBranch = branch.ParentBranch,
            BranchPoint = branch.BranchPoint,
            CreatedAt = branch.CreatedAt,
            CurrentVersionId = branch.CurrentVersionId,
            VersionCount = branch.Versions.Count,
            IsDeleted = branch.IsDeleted
        });
    }

    /// <summary>
    /// Merges one branch into another.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="sourceBranch">Branch to merge from.</param>
    /// <param name="targetBranch">Branch to merge into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Merge result with conflict information.</returns>
    public Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        return Task.FromResult(store.MergeBranch(sourceBranch, targetBranch));
    }

    /// <summary>
    /// Deletes a branch.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="branchName">Branch to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if branch was deleted.</returns>
    public Task<bool> DeleteBranchAsync(string objectId, string branchName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.DeleteBranch(branchName));
    }

    /// <summary>
    /// Lists all branches for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="includeDeleted">Whether to include deleted branches.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of branch information.</returns>
    public Task<IEnumerable<BranchInfo>> ListBranchesAsync(string objectId, bool includeDeleted = false, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IEnumerable<BranchInfo>>([]);

        return Task.FromResult(store.ListBranches(includeDeleted));
    }

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, id => new BranchStore(id));
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
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found.");

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
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found.");

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
            Summary = isIdentical
                ? "Versions are identical"
                : $"Content differs. Size: {fromData.Length} -> {toData.Length} bytes"
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
}
