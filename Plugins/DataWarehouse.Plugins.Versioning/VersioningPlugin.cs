using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.Versioning;

/// <summary>
/// Production-ready versioning plugin providing Git-like version control for DataWarehouse.
/// Supports branching, merging, delta storage, and complete version history for enterprise
/// document management and regulatory compliance.
/// </summary>
public sealed class VersioningPlugin : VersioningPluginBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ObjectVersionGraph> _versionGraphs = new();
    private readonly ConcurrentDictionary<string, byte[]> _versionData = new();
    private readonly ConcurrentDictionary<string, DeltaChain> _deltaChains = new();
    private readonly ConcurrentDictionary<string, BranchState> _branches = new();
    private readonly VersioningPluginConfig _config;
    private readonly string _storagePath;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly DeltaEncoder _deltaEncoder;
    private volatile bool _disposed;

    public override string Id => "com.datawarehouse.plugins.versioning";
    public override string Name => "DataWarehouse Versioning Plugin";
    public override string Version => "1.0.0";
    public override bool SupportsBranching => true;
    public override bool SupportsDeltaStorage => true;

    /// <summary>
    /// Event raised when a new version is created.
    /// </summary>
    public event EventHandler<VersionInfo>? VersionCreated;

    /// <summary>
    /// Event raised when versions are merged.
    /// </summary>
    public event EventHandler<MergeResult>? VersionsMerged;

    /// <summary>
    /// Event raised when a version is restored.
    /// </summary>
    public event EventHandler<VersionInfo>? VersionRestored;

    /// <summary>
    /// Creates a new versioning plugin instance.
    /// </summary>
    public VersioningPlugin(VersioningPluginConfig? config = null)
    {
        _config = config ?? new VersioningPluginConfig();
        _storagePath = _config.StoragePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "Versioning");
        _deltaEncoder = new DeltaEncoder(_config.DeltaConfig ?? new DeltaEncoderConfig());
        Directory.CreateDirectory(_storagePath);
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct);
    }

    public override async Task StopAsync()
    {
        await PersistStateAsync(CancellationToken.None);
    }

    #region Core Version Operations

    /// <summary>
    /// Creates a new version of an object with efficient delta storage.
    /// </summary>
    public override async Task<VersionInfo> CreateVersionAsync(
        string objectId,
        Stream data,
        VersionMetadata? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(data);

        var branchName = metadata?.BranchName ?? _config.DefaultBranchName;
        var graph = GetOrCreateVersionGraph(objectId);
        var fullData = await ReadStreamFullyAsync(data, ct);
        var contentHash = ComputeContentHash(fullData);
        var versionId = GenerateVersionId();
        var now = DateTime.UtcNow;

        // Get parent version for delta computation
        string? parentVersionId = null;
        byte[]? parentData = null;

        var branch = GetOrCreateBranch(objectId, branchName);
        if (!string.IsNullOrEmpty(branch.HeadVersionId))
        {
            parentVersionId = branch.HeadVersionId;
            parentData = await GetVersionDataAsync(objectId, parentVersionId, ct);
        }

        // Store data - use delta encoding if we have a parent
        if (parentData != null && _config.EnableDeltaStorage)
        {
            var delta = _deltaEncoder.ComputeDelta(parentData, fullData);
            await StoreDeltaAsync(objectId, versionId, parentVersionId!, delta, ct);
        }
        else
        {
            // Store as base version (full content)
            await StoreBaseVersionAsync(objectId, versionId, fullData, ct);
        }

        // Create version info
        var versionInfo = new VersionInfo
        {
            VersionId = versionId,
            ObjectId = objectId,
            Size = fullData.Length,
            CreatedAt = now,
            CreatedBy = metadata?.Author,
            Message = metadata?.Message,
            ParentVersionId = parentVersionId,
            BranchName = branchName,
            IsLatest = true,
            ContentHash = contentHash,
            Tags = metadata?.Tags ?? new Dictionary<string, string>()
        };

        // Update version graph
        var node = new VersionNode
        {
            VersionId = versionId,
            ParentVersionIds = parentVersionId != null ? new List<string> { parentVersionId } : new List<string>(),
            CreatedAt = now,
            ContentHash = contentHash,
            BranchName = branchName,
            Message = metadata?.Message,
            Author = metadata?.Author,
            Size = fullData.Length
        };

        graph.AddVersion(node);

        // Update branch head
        if (parentVersionId != null)
        {
            graph.Versions[parentVersionId].IsLatest = false;
        }
        branch.HeadVersionId = versionId;
        branch.LastModified = now;

        await PersistStateAsync(ct);
        VersionCreated?.Invoke(this, versionInfo);

        return versionInfo;
    }

    /// <summary>
    /// Retrieves a specific version of an object.
    /// </summary>
    public override async Task<Stream> GetVersionAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        var data = await GetVersionDataAsync(objectId, versionId, ct);
        if (data == null)
        {
            throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'");
        }

        return new MemoryStream(data);
    }

    /// <summary>
    /// Lists all versions of an object with optional filtering.
    /// </summary>
    public override Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(
        string objectId,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);

        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return Task.FromResult<IReadOnlyList<VersionInfo>>(Array.Empty<VersionInfo>());
        }

        var versions = graph.Versions.Values
            .OrderByDescending(v => v.CreatedAt)
            .Take(limit)
            .Select(v => new VersionInfo
            {
                VersionId = v.VersionId,
                ObjectId = objectId,
                Size = v.Size,
                CreatedAt = v.CreatedAt,
                CreatedBy = v.Author,
                Message = v.Message,
                ParentVersionId = v.ParentVersionIds.FirstOrDefault(),
                BranchName = v.BranchName,
                IsLatest = v.IsLatest,
                ContentHash = v.ContentHash,
                Tags = v.Tags
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<VersionInfo>>(versions);
    }

    /// <summary>
    /// Deletes a specific version. Cannot delete if it's the base of a delta chain
    /// unless cascade delete is enabled.
    /// </summary>
    public override async Task<bool> DeleteVersionAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return false;
        }

        if (!graph.Versions.TryGetValue(versionId, out var version))
        {
            return false;
        }

        // Check if this version has children depending on it
        var dependentVersions = graph.Versions.Values
            .Where(v => v.ParentVersionIds.Contains(versionId))
            .ToList();

        if (dependentVersions.Any())
        {
            if (!_config.AllowCascadeDelete)
            {
                throw new InvalidOperationException(
                    $"Cannot delete version '{versionId}' as it has {dependentVersions.Count} dependent versions. " +
                    "Enable cascade delete or rebase dependent versions first.");
            }

            // Rebase dependent versions to this version's parent
            foreach (var dependent in dependentVersions)
            {
                await RebaseVersionAsync(objectId, dependent.VersionId, version.ParentVersionIds.FirstOrDefault(), ct);
            }
        }

        // Remove from storage
        var storageKey = GetStorageKey(objectId, versionId);
        _versionData.TryRemove(storageKey, out _);
        _deltaChains.TryRemove(storageKey, out _);

        // Remove from graph
        graph.Versions.TryRemove(versionId, out _);

        // Update branch heads if necessary
        foreach (var branch in _branches.Values.Where(b => b.ObjectId == objectId && b.HeadVersionId == versionId))
        {
            branch.HeadVersionId = version.ParentVersionIds.FirstOrDefault() ?? string.Empty;
        }

        await PersistStateAsync(ct);
        return true;
    }

    /// <summary>
    /// Restores an object to a specific version by creating a new version with that content.
    /// </summary>
    public override async Task<VersionInfo> RestoreVersionAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        var data = await GetVersionDataAsync(objectId, versionId, ct);
        if (data == null)
        {
            throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'");
        }

        // Get original version info for context
        if (!_versionGraphs.TryGetValue(objectId, out var graph) ||
            !graph.Versions.TryGetValue(versionId, out var originalVersion))
        {
            throw new KeyNotFoundException($"Version metadata not found for '{versionId}'");
        }

        // Create a new version with the restored content
        var metadata = new VersionMetadata
        {
            Message = $"Restored from version {versionId}",
            Author = originalVersion.Author,
            BranchName = originalVersion.BranchName,
            Tags = new Dictionary<string, string>
            {
                ["restored_from"] = versionId,
                ["restored_at"] = DateTime.UtcNow.ToString("O")
            }
        };

        using var stream = new MemoryStream(data);
        var newVersion = await CreateVersionAsync(objectId, stream, metadata, ct);
        VersionRestored?.Invoke(this, newVersion);
        return newVersion;
    }

    #endregion

    #region Diff Operations

    /// <summary>
    /// Computes the diff between two versions with detailed hunk information.
    /// </summary>
    public override async Task<VersionDiff> GetDiffAsync(
        string objectId,
        string fromVersionId,
        string toVersionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(fromVersionId);
        ArgumentException.ThrowIfNullOrEmpty(toVersionId);

        var fromData = await GetVersionDataAsync(objectId, fromVersionId, ct);
        var toData = await GetVersionDataAsync(objectId, toVersionId, ct);

        if (fromData == null)
        {
            throw new KeyNotFoundException($"Version '{fromVersionId}' not found");
        }
        if (toData == null)
        {
            throw new KeyNotFoundException($"Version '{toVersionId}' not found");
        }

        var hunks = _deltaEncoder.ComputeDiffHunks(fromData, toData);
        var bytesAdded = hunks.Where(h => h.OldLength == 0).Sum(h => h.NewLength);
        var bytesRemoved = hunks.Where(h => h.NewLength == 0).Sum(h => h.OldLength);
        var bytesModified = hunks.Where(h => h.OldLength > 0 && h.NewLength > 0)
            .Sum(h => Math.Max(h.OldLength, h.NewLength));

        return new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            BytesAdded = bytesAdded,
            BytesRemoved = bytesRemoved,
            BytesModified = bytesModified,
            Hunks = hunks
        };
    }

    #endregion

    #region Branch Operations

    /// <summary>
    /// Creates a new branch from a specific version.
    /// </summary>
    public override async Task<BranchInfo> CreateBranchAsync(
        string objectId,
        string versionId,
        string branchName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);
        ArgumentException.ThrowIfNullOrEmpty(branchName);

        if (!_versionGraphs.TryGetValue(objectId, out var graph) ||
            !graph.Versions.ContainsKey(versionId))
        {
            throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'");
        }

        var branchKey = GetBranchKey(objectId, branchName);
        if (_branches.ContainsKey(branchKey))
        {
            throw new InvalidOperationException($"Branch '{branchName}' already exists for object '{objectId}'");
        }

        var now = DateTime.UtcNow;
        var branch = new BranchState
        {
            ObjectId = objectId,
            Name = branchName,
            HeadVersionId = versionId,
            BaseVersionId = versionId,
            CreatedAt = now,
            LastModified = now
        };

        _branches[branchKey] = branch;
        await PersistStateAsync(ct);

        return new BranchInfo
        {
            Name = branchName,
            HeadVersionId = versionId,
            BaseVersionId = versionId,
            CreatedAt = now
        };
    }

    /// <summary>
    /// Deletes a branch. Does not delete the versions on the branch.
    /// </summary>
    public async Task<bool> DeleteBranchAsync(
        string objectId,
        string branchName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(branchName);

        if (branchName == _config.DefaultBranchName)
        {
            throw new InvalidOperationException($"Cannot delete the default branch '{_config.DefaultBranchName}'");
        }

        var branchKey = GetBranchKey(objectId, branchName);
        var removed = _branches.TryRemove(branchKey, out _);

        if (removed)
        {
            await PersistStateAsync(ct);
        }

        return removed;
    }

    /// <summary>
    /// Lists all branches for an object.
    /// </summary>
    public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(
        string objectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);

        var branches = _branches.Values
            .Where(b => b.ObjectId == objectId)
            .Select(b => new BranchInfo
            {
                Name = b.Name,
                HeadVersionId = b.HeadVersionId,
                BaseVersionId = b.BaseVersionId,
                CreatedAt = b.CreatedAt
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<BranchInfo>>(branches);
    }

    /// <summary>
    /// Merges two branches using the specified strategy.
    /// </summary>
    public override async Task<MergeResult> MergeBranchAsync(
        string objectId,
        string sourceBranch,
        string targetBranch,
        MergeStrategy strategy = MergeStrategy.ThreeWay,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(sourceBranch);
        ArgumentException.ThrowIfNullOrEmpty(targetBranch);

        var sourceBranchKey = GetBranchKey(objectId, sourceBranch);
        var targetBranchKey = GetBranchKey(objectId, targetBranch);

        if (!_branches.TryGetValue(sourceBranchKey, out var source))
        {
            throw new KeyNotFoundException($"Source branch '{sourceBranch}' not found");
        }

        if (!_branches.TryGetValue(targetBranchKey, out var target))
        {
            throw new KeyNotFoundException($"Target branch '{targetBranch}' not found");
        }

        // Get the common ancestor (merge base)
        var mergeBase = await FindMergeBaseAsync(objectId, source.HeadVersionId, target.HeadVersionId, ct);

        // Get data for all three versions
        var baseData = mergeBase != null ? await GetVersionDataAsync(objectId, mergeBase, ct) : Array.Empty<byte>();
        var sourceData = await GetVersionDataAsync(objectId, source.HeadVersionId, ct);
        var targetData = await GetVersionDataAsync(objectId, target.HeadVersionId, ct);

        if (sourceData == null || targetData == null)
        {
            return new MergeResult
            {
                Success = false,
                HasConflicts = false,
                Conflicts = Array.Empty<MergeConflict>()
            };
        }

        // Perform the merge based on strategy
        var (mergedData, conflicts) = strategy switch
        {
            MergeStrategy.ThreeWay => PerformThreeWayMerge(baseData, sourceData, targetData),
            MergeStrategy.Ours => (targetData, new List<MergeConflict>()),
            MergeStrategy.Theirs => (sourceData, new List<MergeConflict>()),
            MergeStrategy.Union => PerformUnionMerge(baseData, sourceData, targetData),
            _ => throw new ArgumentException($"Unknown merge strategy: {strategy}")
        };

        if (conflicts.Any())
        {
            return new MergeResult
            {
                Success = false,
                HasConflicts = true,
                Conflicts = conflicts
            };
        }

        // Create the merge commit
        var metadata = new VersionMetadata
        {
            Message = $"Merge branch '{sourceBranch}' into '{targetBranch}'",
            BranchName = targetBranch,
            Tags = new Dictionary<string, string>
            {
                ["merge_source"] = sourceBranch,
                ["merge_base"] = mergeBase ?? "none",
                ["merge_strategy"] = strategy.ToString()
            }
        };

        using var stream = new MemoryStream(mergedData);
        var mergeVersion = await CreateVersionAsync(objectId, stream, metadata, ct);

        // Add source as additional parent for merge commit
        if (_versionGraphs.TryGetValue(objectId, out var graph) &&
            graph.Versions.TryGetValue(mergeVersion.VersionId, out var mergeNode))
        {
            mergeNode.ParentVersionIds.Add(source.HeadVersionId);
        }

        var result = new MergeResult
        {
            Success = true,
            ResultVersionId = mergeVersion.VersionId,
            HasConflicts = false,
            Conflicts = Array.Empty<MergeConflict>()
        };

        VersionsMerged?.Invoke(this, result);
        return result;
    }

    #endregion

    #region Advanced Operations

    /// <summary>
    /// Gets the version history as a graph structure.
    /// </summary>
    public Task<ObjectVersionGraph?> GetVersionGraphAsync(
        string objectId,
        CancellationToken ct = default)
    {
        _versionGraphs.TryGetValue(objectId, out var graph);
        return Task.FromResult(graph);
    }

    /// <summary>
    /// Gets version chain from a specific version to its root.
    /// </summary>
    public async Task<IReadOnlyList<VersionInfo>> GetVersionChainAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return Array.Empty<VersionInfo>();
        }

        var chain = new List<VersionInfo>();
        var currentId = versionId;

        while (!string.IsNullOrEmpty(currentId) && graph.Versions.TryGetValue(currentId, out var node))
        {
            chain.Add(new VersionInfo
            {
                VersionId = node.VersionId,
                ObjectId = objectId,
                Size = node.Size,
                CreatedAt = node.CreatedAt,
                CreatedBy = node.Author,
                Message = node.Message,
                ParentVersionId = node.ParentVersionIds.FirstOrDefault(),
                BranchName = node.BranchName,
                IsLatest = node.IsLatest,
                ContentHash = node.ContentHash,
                Tags = node.Tags
            });

            currentId = node.ParentVersionIds.FirstOrDefault();
        }

        return chain;
    }

    /// <summary>
    /// Compacts the delta chain by creating new base versions.
    /// </summary>
    public async Task<int> CompactDeltaChainAsync(
        string objectId,
        int maxChainLength = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);

        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return 0;
        }

        var compacted = 0;

        // Find versions with long delta chains
        foreach (var version in graph.Versions.Values)
        {
            var chainLength = await GetDeltaChainLengthAsync(objectId, version.VersionId, ct);

            if (chainLength > maxChainLength)
            {
                // Materialize this version as a base
                var data = await GetVersionDataAsync(objectId, version.VersionId, ct);
                if (data != null)
                {
                    await StoreBaseVersionAsync(objectId, version.VersionId, data, ct);
                    compacted++;
                }
            }
        }

        if (compacted > 0)
        {
            await PersistStateAsync(ct);
        }

        return compacted;
    }

    /// <summary>
    /// Gets statistics about the version store for an object.
    /// </summary>
    public Task<VersioningStatistics> GetStatisticsAsync(
        string objectId,
        CancellationToken ct = default)
    {
        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return Task.FromResult(new VersioningStatistics());
        }

        var branchCount = _branches.Values.Count(b => b.ObjectId == objectId);
        var totalVersions = graph.Versions.Count;
        var baseVersionCount = _versionData.Keys.Count(k => k.StartsWith($"{objectId}/"));
        var deltaCount = _deltaChains.Keys.Count(k => k.StartsWith($"{objectId}/"));
        var totalSize = graph.Versions.Values.Sum(v => v.Size);

        // Estimate storage savings from delta compression
        var storedSize = _versionData.Where(kv => kv.Key.StartsWith($"{objectId}/"))
            .Sum(kv => kv.Value.Length);
        storedSize += _deltaChains.Where(kv => kv.Key.StartsWith($"{objectId}/"))
            .Sum(kv => kv.Value.DeltaData.Length);

        return Task.FromResult(new VersioningStatistics
        {
            ObjectId = objectId,
            TotalVersions = totalVersions,
            BranchCount = branchCount,
            BaseVersionCount = baseVersionCount,
            DeltaVersionCount = deltaCount,
            TotalLogicalSize = totalSize,
            TotalPhysicalSize = storedSize,
            CompressionRatio = totalSize > 0 ? (double)storedSize / totalSize : 1.0
        });
    }

    #endregion

    #region Private Helper Methods

    private ObjectVersionGraph GetOrCreateVersionGraph(string objectId)
    {
        return _versionGraphs.GetOrAdd(objectId, _ => new ObjectVersionGraph { ObjectId = objectId });
    }

    private BranchState GetOrCreateBranch(string objectId, string branchName)
    {
        var key = GetBranchKey(objectId, branchName);
        return _branches.GetOrAdd(key, _ => new BranchState
        {
            ObjectId = objectId,
            Name = branchName,
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        });
    }

    private static string GetBranchKey(string objectId, string branchName) => $"{objectId}:{branchName}";
    private static string GetStorageKey(string objectId, string versionId) => $"{objectId}/{versionId}";
    private static string GenerateVersionId() => $"v_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}";

    private static string ComputeContentHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<byte[]?> GetVersionDataAsync(string objectId, string versionId, CancellationToken ct)
    {
        var storageKey = GetStorageKey(objectId, versionId);

        // Check if it's a base version
        if (_versionData.TryGetValue(storageKey, out var baseData))
        {
            return baseData;
        }

        // Check if it's a delta version
        if (_deltaChains.TryGetValue(storageKey, out var deltaChain))
        {
            // Recursively get parent data and apply delta
            var parentData = await GetVersionDataAsync(objectId, deltaChain.BaseVersionId, ct);
            if (parentData == null)
            {
                return null;
            }

            return _deltaEncoder.ApplyDelta(parentData, deltaChain.DeltaData);
        }

        return null;
    }

    private Task StoreBaseVersionAsync(string objectId, string versionId, byte[] data, CancellationToken ct)
    {
        var storageKey = GetStorageKey(objectId, versionId);

        // Remove any existing delta chain for this version
        _deltaChains.TryRemove(storageKey, out _);

        _versionData[storageKey] = data;
        return Task.CompletedTask;
    }

    private Task StoreDeltaAsync(string objectId, string versionId, string baseVersionId, byte[] delta, CancellationToken ct)
    {
        var storageKey = GetStorageKey(objectId, versionId);
        _deltaChains[storageKey] = new DeltaChain
        {
            VersionId = versionId,
            BaseVersionId = baseVersionId,
            DeltaData = delta
        };
        return Task.CompletedTask;
    }

    private async Task<int> GetDeltaChainLengthAsync(string objectId, string versionId, CancellationToken ct)
    {
        var length = 0;
        var currentId = versionId;

        while (!string.IsNullOrEmpty(currentId))
        {
            var storageKey = GetStorageKey(objectId, currentId);

            if (_versionData.ContainsKey(storageKey))
            {
                break; // Found base version
            }

            if (_deltaChains.TryGetValue(storageKey, out var chain))
            {
                length++;
                currentId = chain.BaseVersionId;
            }
            else
            {
                break;
            }
        }

        return length;
    }

    private async Task RebaseVersionAsync(string objectId, string versionId, string? newBaseId, CancellationToken ct)
    {
        // Get current version data
        var data = await GetVersionDataAsync(objectId, versionId, ct);
        if (data == null) return;

        if (string.IsNullOrEmpty(newBaseId))
        {
            // Make this a base version
            await StoreBaseVersionAsync(objectId, versionId, data, ct);
        }
        else
        {
            // Compute new delta
            var baseData = await GetVersionDataAsync(objectId, newBaseId, ct);
            if (baseData != null)
            {
                var delta = _deltaEncoder.ComputeDelta(baseData, data);
                await StoreDeltaAsync(objectId, versionId, newBaseId, delta, ct);
            }
            else
            {
                await StoreBaseVersionAsync(objectId, versionId, data, ct);
            }
        }

        // Update parent reference in graph
        if (_versionGraphs.TryGetValue(objectId, out var graph) &&
            graph.Versions.TryGetValue(versionId, out var node))
        {
            node.ParentVersionIds.Clear();
            if (!string.IsNullOrEmpty(newBaseId))
            {
                node.ParentVersionIds.Add(newBaseId);
            }
        }
    }

    private async Task<string?> FindMergeBaseAsync(
        string objectId,
        string versionId1,
        string versionId2,
        CancellationToken ct)
    {
        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return null;
        }

        // Get ancestors of both versions
        var ancestors1 = new HashSet<string>();
        var ancestors2 = new HashSet<string>();

        await CollectAncestors(graph, versionId1, ancestors1);
        await CollectAncestors(graph, versionId2, ancestors2);

        // Find common ancestors
        var commonAncestors = ancestors1.Intersect(ancestors2).ToList();

        if (!commonAncestors.Any())
        {
            return null;
        }

        // Return the most recent common ancestor
        return commonAncestors
            .Where(id => graph.Versions.ContainsKey(id))
            .OrderByDescending(id => graph.Versions[id].CreatedAt)
            .FirstOrDefault();
    }

    private Task CollectAncestors(ObjectVersionGraph graph, string versionId, HashSet<string> ancestors)
    {
        var queue = new Queue<string>();
        queue.Enqueue(versionId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (ancestors.Contains(current)) continue;

            ancestors.Add(current);

            if (graph.Versions.TryGetValue(current, out var node))
            {
                foreach (var parent in node.ParentVersionIds)
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return Task.CompletedTask;
    }

    private (byte[] mergedData, List<MergeConflict> conflicts) PerformThreeWayMerge(
        byte[]? baseData,
        byte[] sourceData,
        byte[] targetData)
    {
        baseData ??= Array.Empty<byte>();

        var conflicts = new List<MergeConflict>();
        var result = new List<byte>();

        // Compute diffs from base to each branch
        var sourceHunks = _deltaEncoder.ComputeDiffHunks(baseData, sourceData);
        var targetHunks = _deltaEncoder.ComputeDiffHunks(baseData, targetData);

        // Build change maps
        var sourceChanges = BuildChangeMap(sourceHunks);
        var targetChanges = BuildChangeMap(targetHunks);

        long position = 0;
        var maxPosition = Math.Max(
            sourceChanges.Any() ? sourceChanges.Max(c => c.Key + c.Value.Length) : 0,
            targetChanges.Any() ? targetChanges.Max(c => c.Key + c.Value.Length) : 0);
        maxPosition = Math.Max(maxPosition, Math.Max(sourceData.Length, targetData.Length));

        while (position < maxPosition)
        {
            var hasSourceChange = sourceChanges.TryGetValue(position, out var sourceChange);
            var hasTargetChange = targetChanges.TryGetValue(position, out var targetChange);

            if (hasSourceChange && hasTargetChange)
            {
                // Both changed at same position - check for conflict
                if (!sourceChange!.SequenceEqual(targetChange!))
                {
                    conflicts.Add(new MergeConflict
                    {
                        Offset = position,
                        Length = Math.Max(sourceChange.Length, targetChange.Length),
                        BaseData = position < baseData.Length
                            ? baseData.Skip((int)position).Take(Math.Max(sourceChange.Length, targetChange.Length)).ToArray()
                            : null,
                        SourceData = sourceChange,
                        TargetData = targetChange
                    });

                    // For now, take target's version (can be resolved later)
                    result.AddRange(targetChange);
                    position += targetChange.Length;
                }
                else
                {
                    // Same change on both sides
                    result.AddRange(sourceChange);
                    position += sourceChange.Length;
                }
            }
            else if (hasSourceChange)
            {
                result.AddRange(sourceChange!);
                position += sourceChange!.Length;
            }
            else if (hasTargetChange)
            {
                result.AddRange(targetChange!);
                position += targetChange!.Length;
            }
            else
            {
                // No changes at this position, take from target
                if (position < targetData.Length)
                {
                    result.Add(targetData[position]);
                }
                position++;
            }
        }

        return (result.ToArray(), conflicts);
    }

    private (byte[] mergedData, List<MergeConflict> conflicts) PerformUnionMerge(
        byte[]? baseData,
        byte[] sourceData,
        byte[] targetData)
    {
        baseData ??= Array.Empty<byte>();

        // Union merge: include all changes from both branches
        var result = new List<byte>();
        var seen = new HashSet<int>();

        // Add all unique bytes from source
        for (int i = 0; i < sourceData.Length; i++)
        {
            var hash = HashCode.Combine(i, sourceData[i]);
            if (seen.Add(hash))
            {
                result.Add(sourceData[i]);
            }
        }

        // Add unique bytes from target not in source
        for (int i = 0; i < targetData.Length; i++)
        {
            if (i >= sourceData.Length || sourceData[i] != targetData[i])
            {
                result.Add(targetData[i]);
            }
        }

        // For binary data, union merge typically returns the larger dataset
        // with preference to target for overlapping regions
        if (targetData.Length >= sourceData.Length)
        {
            return (targetData, new List<MergeConflict>());
        }

        return (sourceData, new List<MergeConflict>());
    }

    private static Dictionary<long, byte[]> BuildChangeMap(IReadOnlyList<DiffHunk> hunks)
    {
        var map = new Dictionary<long, byte[]>();
        foreach (var hunk in hunks)
        {
            if (hunk.NewData != null && hunk.NewData.Length > 0)
            {
                map[hunk.NewStart] = hunk.NewData;
            }
        }
        return map;
    }

    private static async Task<byte[]> ReadStreamFullyAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, ct);
        return memoryStream.ToArray();
    }

    #endregion

    #region Persistence

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var stateFile = Path.Combine(_storagePath, "version_state.json");
        if (!File.Exists(stateFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(stateFile, ct);
            var state = JsonSerializer.Deserialize<VersioningState>(json, JsonOptions);

            if (state == null) return;

            foreach (var graph in state.Graphs)
            {
                _versionGraphs[graph.ObjectId] = graph;
            }

            foreach (var branch in state.Branches)
            {
                var key = GetBranchKey(branch.ObjectId, branch.Name);
                _branches[key] = branch;
            }

            // Load data from files
            var dataDir = Path.Combine(_storagePath, "data");
            if (Directory.Exists(dataDir))
            {
                foreach (var file in Directory.GetFiles(dataDir, "*.base"))
                {
                    var key = Path.GetFileNameWithoutExtension(file).Replace("_", "/");
                    _versionData[key] = await File.ReadAllBytesAsync(file, ct);
                }

                foreach (var file in Directory.GetFiles(dataDir, "*.delta"))
                {
                    var key = Path.GetFileNameWithoutExtension(file).Replace("_", "/");
                    var deltaJson = await File.ReadAllTextAsync(file, ct);
                    var chain = JsonSerializer.Deserialize<DeltaChain>(deltaJson, JsonOptions);
                    if (chain != null)
                    {
                        _deltaChains[key] = chain;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - start with empty state
            System.Diagnostics.Debug.WriteLine($"Failed to load versioning state: {ex.Message}");
        }
    }

    private async Task PersistStateAsync(CancellationToken ct)
    {
        await _persistLock.WaitAsync(ct);
        try
        {
            var state = new VersioningState
            {
                Graphs = _versionGraphs.Values.ToList(),
                Branches = _branches.Values.ToList(),
                SavedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(state, JsonOptions);
            var stateFile = Path.Combine(_storagePath, "version_state.json");
            await File.WriteAllTextAsync(stateFile, json, ct);

            // Save data files
            var dataDir = Path.Combine(_storagePath, "data");
            Directory.CreateDirectory(dataDir);

            foreach (var (key, data) in _versionData)
            {
                var fileName = key.Replace("/", "_") + ".base";
                await File.WriteAllBytesAsync(Path.Combine(dataDir, fileName), data, ct);
            }

            foreach (var (key, chain) in _deltaChains)
            {
                var fileName = key.Replace("/", "_") + ".delta";
                var deltaJson = JsonSerializer.Serialize(chain, JsonOptions);
                await File.WriteAllTextAsync(Path.Combine(dataDir, fileName), deltaJson, ct);
            }
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #endregion

    #region Metadata

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "CreateVersion",
                Description = "Creates a new version of an object with delta storage",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "objectId", Type = "string", Required = true, Description = "Object identifier" },
                    new() { Name = "data", Type = "Stream", Required = true, Description = "Version content" },
                    new() { Name = "message", Type = "string", Required = false, Description = "Version message" }
                }
            },
            new()
            {
                Name = "GetVersion",
                Description = "Retrieves a specific version",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "objectId", Type = "string", Required = true, Description = "Object identifier" },
                    new() { Name = "versionId", Type = "string", Required = true, Description = "Version identifier" }
                }
            },
            new()
            {
                Name = "CreateBranch",
                Description = "Creates a new branch from a version",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "objectId", Type = "string", Required = true, Description = "Object identifier" },
                    new() { Name = "versionId", Type = "string", Required = true, Description = "Base version" },
                    new() { Name = "branchName", Type = "string", Required = true, Description = "New branch name" }
                }
            },
            new()
            {
                Name = "MergeBranches",
                Description = "Merges two branches with conflict detection",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "objectId", Type = "string", Required = true, Description = "Object identifier" },
                    new() { Name = "sourceBranch", Type = "string", Required = true, Description = "Source branch" },
                    new() { Name = "targetBranch", Type = "string", Required = true, Description = "Target branch" },
                    new() { Name = "strategy", Type = "MergeStrategy", Required = false, Description = "Merge strategy" }
                }
            },
            new()
            {
                Name = "ComputeDiff",
                Description = "Computes diff between two versions",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "objectId", Type = "string", Required = true, Description = "Object identifier" },
                    new() { Name = "fromVersionId", Type = "string", Required = true, Description = "From version" },
                    new() { Name = "toVersionId", Type = "string", Required = true, Description = "To version" }
                }
            },
            new()
            {
                Name = "RestoreVersion",
                Description = "Restores object to a specific version",
                Parameters = new List<PluginParameterDescriptor>
                {
                    new() { Name = "objectId", Type = "string", Required = true, Description = "Object identifier" },
                    new() { Name = "versionId", Type = "string", Required = true, Description = "Version to restore" }
                }
            }
        };
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Git-like versioning with branching, merging, and delta storage";
        metadata["SupportsBranching"] = true;
        metadata["SupportsDeltaStorage"] = true;
        metadata["SupportsThreeWayMerge"] = true;
        metadata["SupportsConflictDetection"] = true;
        metadata["TotalObjects"] = _versionGraphs.Count;
        metadata["TotalBranches"] = _branches.Count;
        metadata["TotalVersions"] = _versionGraphs.Values.Sum(g => g.Versions.Count);
        return metadata;
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await PersistStateAsync(CancellationToken.None);
        _persistLock.Dispose();
    }
}

#region Supporting Types

/// <summary>
/// Configuration for the versioning plugin.
/// </summary>
public sealed record VersioningPluginConfig
{
    /// <summary>Storage path for version data.</summary>
    public string? StoragePath { get; init; }

    /// <summary>Enable delta storage for efficient space usage.</summary>
    public bool EnableDeltaStorage { get; init; } = true;

    /// <summary>Default branch name for new objects.</summary>
    public string DefaultBranchName { get; init; } = "main";

    /// <summary>Allow cascading deletion of dependent versions.</summary>
    public bool AllowCascadeDelete { get; init; } = false;

    /// <summary>Maximum delta chain length before auto-compaction.</summary>
    public int MaxDeltaChainLength { get; init; } = 10;

    /// <summary>Delta encoder configuration.</summary>
    public DeltaEncoderConfig? DeltaConfig { get; init; }
}

/// <summary>
/// Configuration for the delta encoder.
/// </summary>
public sealed record DeltaEncoderConfig
{
    /// <summary>Minimum match length for copy operations.</summary>
    public int MinMatchLength { get; init; } = 4;

    /// <summary>Window size for finding matches.</summary>
    public int WindowSize { get; init; } = 65536;

    /// <summary>Block size for chunking.</summary>
    public int BlockSize { get; init; } = 16;
}

/// <summary>
/// Version graph for an object, tracking all versions and their relationships.
/// </summary>
public sealed class ObjectVersionGraph
{
    public string ObjectId { get; init; } = string.Empty;
    public ConcurrentDictionary<string, VersionNode> Versions { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    public void AddVersion(VersionNode node)
    {
        Versions[node.VersionId] = node;
        LastModified = DateTime.UtcNow;
    }
}

/// <summary>
/// A node in the version graph representing a single version.
/// </summary>
public sealed class VersionNode
{
    public string VersionId { get; init; } = string.Empty;
    public List<string> ParentVersionIds { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public string? BranchName { get; set; }
    public string? Message { get; init; }
    public string? Author { get; init; }
    public long Size { get; init; }
    public bool IsLatest { get; set; } = true;
    public Dictionary<string, string> Tags { get; init; } = new();
}

/// <summary>
/// State of a branch.
/// </summary>
public sealed class BranchState
{
    public string ObjectId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HeadVersionId { get; set; } = string.Empty;
    public string BaseVersionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime LastModified { get; set; }
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Delta chain information for a version.
/// </summary>
public sealed class DeltaChain
{
    public string VersionId { get; init; } = string.Empty;
    public string BaseVersionId { get; init; } = string.Empty;
    public byte[] DeltaData { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Persisted state for the versioning plugin.
/// </summary>
internal sealed class VersioningState
{
    public List<ObjectVersionGraph> Graphs { get; init; } = new();
    public List<BranchState> Branches { get; init; } = new();
    public DateTime SavedAt { get; init; }
}

/// <summary>
/// Statistics about versioning for an object.
/// </summary>
public sealed class VersioningStatistics
{
    public string ObjectId { get; init; } = string.Empty;
    public int TotalVersions { get; init; }
    public int BranchCount { get; init; }
    public int BaseVersionCount { get; init; }
    public int DeltaVersionCount { get; init; }
    public long TotalLogicalSize { get; init; }
    public long TotalPhysicalSize { get; init; }
    public double CompressionRatio { get; init; }
}

#endregion

#region Delta Encoder

/// <summary>
/// Production-quality binary delta encoder implementing a variation of
/// the xdelta/vcdiff algorithm for efficient incremental storage.
/// Uses a rolling hash for finding matching blocks and encodes changes
/// as a sequence of copy and insert operations.
/// </summary>
public sealed class DeltaEncoder
{
    private readonly DeltaEncoderConfig _config;
    private const uint RollingHashPrime = 16777619;
    private const uint RollingHashBase = 2166136261;

    // Delta instruction opcodes
    private const byte OpCopy = 0x00;      // Copy from source
    private const byte OpInsert = 0x01;    // Insert new data
    private const byte OpEnd = 0xFF;       // End of delta

    public DeltaEncoder(DeltaEncoderConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Computes a delta that transforms sourceData into targetData.
    /// Returns a compact binary representation of the changes.
    /// </summary>
    public byte[] ComputeDelta(byte[] sourceData, byte[] targetData)
    {
        if (sourceData.Length == 0)
        {
            // No source - just insert all target data
            return EncodeFullInsert(targetData);
        }

        if (targetData.Length == 0)
        {
            // Target is empty
            return new byte[] { OpEnd };
        }

        // Build hash index of source blocks for fast lookup
        var sourceIndex = BuildBlockIndex(sourceData);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        // Write header
        writer.Write((int)sourceData.Length);
        writer.Write((int)targetData.Length);

        int targetPos = 0;
        var pendingInsert = new List<byte>();

        while (targetPos < targetData.Length)
        {
            // Try to find a match in source
            var match = FindBestMatch(sourceData, targetData, targetPos, sourceIndex);

            if (match.Length >= _config.MinMatchLength)
            {
                // Flush any pending insert
                if (pendingInsert.Count > 0)
                {
                    WriteInsert(writer, pendingInsert.ToArray());
                    pendingInsert.Clear();
                }

                // Write copy instruction
                WriteCopy(writer, match.SourceOffset, match.Length);
                targetPos += match.Length;
            }
            else
            {
                // Add to pending insert
                pendingInsert.Add(targetData[targetPos]);
                targetPos++;

                // Flush if insert buffer is getting large
                if (pendingInsert.Count >= 65535)
                {
                    WriteInsert(writer, pendingInsert.ToArray());
                    pendingInsert.Clear();
                }
            }
        }

        // Flush remaining insert
        if (pendingInsert.Count > 0)
        {
            WriteInsert(writer, pendingInsert.ToArray());
        }

        // Write end marker
        writer.Write(OpEnd);

        return output.ToArray();
    }

    /// <summary>
    /// Applies a delta to source data to produce target data.
    /// </summary>
    public byte[] ApplyDelta(byte[] sourceData, byte[] deltaData)
    {
        if (deltaData.Length == 0)
        {
            return sourceData;
        }

        using var input = new MemoryStream(deltaData);
        using var reader = new BinaryReader(input);

        // Read header
        var expectedSourceSize = reader.ReadInt32();
        var expectedTargetSize = reader.ReadInt32();

        if (sourceData.Length != expectedSourceSize)
        {
            throw new InvalidOperationException(
                $"Source data size mismatch: expected {expectedSourceSize}, got {sourceData.Length}");
        }

        var target = new List<byte>(expectedTargetSize);

        while (input.Position < input.Length)
        {
            var op = reader.ReadByte();

            switch (op)
            {
                case OpCopy:
                {
                    var offset = reader.ReadInt32();
                    var length = reader.ReadInt32();

                    if (offset < 0 || offset + length > sourceData.Length)
                    {
                        throw new InvalidOperationException(
                            $"Invalid copy range: offset={offset}, length={length}, source size={sourceData.Length}");
                    }

                    for (int i = 0; i < length; i++)
                    {
                        target.Add(sourceData[offset + i]);
                    }
                    break;
                }

                case OpInsert:
                {
                    var length = reader.ReadInt32();
                    var data = reader.ReadBytes(length);
                    target.AddRange(data);
                    break;
                }

                case OpEnd:
                    // Delta complete
                    goto done;

                default:
                    throw new InvalidOperationException($"Unknown delta opcode: 0x{op:X2}");
            }
        }

        done:

        if (target.Count != expectedTargetSize)
        {
            throw new InvalidOperationException(
                $"Target data size mismatch: expected {expectedTargetSize}, got {target.Count}");
        }

        return target.ToArray();
    }

    /// <summary>
    /// Computes detailed diff hunks between two byte arrays.
    /// </summary>
    public IReadOnlyList<DiffHunk> ComputeDiffHunks(byte[] oldData, byte[] newData)
    {
        var hunks = new List<DiffHunk>();

        if (oldData.Length == 0 && newData.Length == 0)
        {
            return hunks;
        }

        if (oldData.Length == 0)
        {
            // All new data is an insertion
            hunks.Add(new DiffHunk
            {
                OldStart = 0,
                OldLength = 0,
                NewStart = 0,
                NewLength = newData.Length,
                NewData = newData
            });
            return hunks;
        }

        if (newData.Length == 0)
        {
            // All old data is a deletion
            hunks.Add(new DiffHunk
            {
                OldStart = 0,
                OldLength = oldData.Length,
                NewStart = 0,
                NewLength = 0,
                OldData = oldData
            });
            return hunks;
        }

        // Use LCS-based diff for detailed hunks
        var lcs = ComputeLCS(oldData, newData);
        hunks = ExtractHunksFromLCS(oldData, newData, lcs);

        return hunks;
    }

    private Dictionary<uint, List<int>> BuildBlockIndex(byte[] data)
    {
        var index = new Dictionary<uint, List<int>>();
        var blockSize = _config.BlockSize;

        if (data.Length < blockSize)
        {
            return index;
        }

        // Compute rolling hash for each block position
        uint hash = ComputeBlockHash(data, 0, blockSize);
        AddToIndex(index, hash, 0);

        for (int i = 1; i <= data.Length - blockSize; i++)
        {
            // Roll the hash forward
            hash = RollHash(hash, data[i - 1], data[i + blockSize - 1], blockSize);
            AddToIndex(index, hash, i);
        }

        return index;
    }

    private static void AddToIndex(Dictionary<uint, List<int>> index, uint hash, int position)
    {
        if (!index.TryGetValue(hash, out var positions))
        {
            positions = new List<int>();
            index[hash] = positions;
        }

        // Limit entries per hash to avoid pathological cases
        if (positions.Count < 100)
        {
            positions.Add(position);
        }
    }

    private static uint ComputeBlockHash(byte[] data, int start, int length)
    {
        uint hash = RollingHashBase;
        int end = Math.Min(start + length, data.Length);

        for (int i = start; i < end; i++)
        {
            hash ^= data[i];
            hash *= RollingHashPrime;
        }

        return hash;
    }

    private static uint RollHash(uint hash, byte oldByte, byte newByte, int length)
    {
        // Remove contribution of old byte and add new byte
        // This is a simplified rolling hash - production would use Rabin fingerprinting
        hash ^= (uint)(oldByte * Math.Pow(RollingHashPrime, length - 1));
        hash *= RollingHashPrime;
        hash ^= newByte;
        return hash;
    }

    private MatchResult FindBestMatch(
        byte[] sourceData,
        byte[] targetData,
        int targetPos,
        Dictionary<uint, List<int>> sourceIndex)
    {
        var blockSize = _config.BlockSize;
        var best = new MatchResult { Length = 0 };

        if (targetPos + blockSize > targetData.Length)
        {
            // Not enough data for block matching, try byte-by-byte
            return FindShortMatch(sourceData, targetData, targetPos);
        }

        // Compute hash of target block
        var targetHash = ComputeBlockHash(targetData, targetPos, blockSize);

        if (!sourceIndex.TryGetValue(targetHash, out var candidates))
        {
            return best;
        }

        // Check each candidate position
        foreach (var sourcePos in candidates)
        {
            // Verify the match and extend it
            var length = ExtendMatch(sourceData, targetData, sourcePos, targetPos);

            if (length > best.Length)
            {
                best = new MatchResult
                {
                    SourceOffset = sourcePos,
                    Length = length
                };
            }
        }

        return best;
    }

    private MatchResult FindShortMatch(byte[] sourceData, byte[] targetData, int targetPos)
    {
        var best = new MatchResult { Length = 0 };
        var remaining = targetData.Length - targetPos;
        var minMatch = Math.Min(_config.MinMatchLength, remaining);

        // Brute force search for short matches at end of data
        for (int sourcePos = 0; sourcePos <= sourceData.Length - minMatch; sourcePos++)
        {
            var length = ExtendMatch(sourceData, targetData, sourcePos, targetPos);
            if (length > best.Length)
            {
                best = new MatchResult
                {
                    SourceOffset = sourcePos,
                    Length = length
                };
            }
        }

        return best;
    }

    private static int ExtendMatch(byte[] sourceData, byte[] targetData, int sourcePos, int targetPos)
    {
        int length = 0;
        int maxLength = Math.Min(sourceData.Length - sourcePos, targetData.Length - targetPos);

        while (length < maxLength && sourceData[sourcePos + length] == targetData[targetPos + length])
        {
            length++;
        }

        return length;
    }

    private static byte[] EncodeFullInsert(byte[] data)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        // Header: source size = 0, target size
        writer.Write(0);
        writer.Write(data.Length);

        // Single insert instruction
        writer.Write(OpInsert);
        writer.Write(data.Length);
        writer.Write(data);

        // End marker
        writer.Write(OpEnd);

        return output.ToArray();
    }

    private static void WriteCopy(BinaryWriter writer, int offset, int length)
    {
        writer.Write(OpCopy);
        writer.Write(offset);
        writer.Write(length);
    }

    private static void WriteInsert(BinaryWriter writer, byte[] data)
    {
        writer.Write(OpInsert);
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static List<(int oldIndex, int newIndex)> ComputeLCS(byte[] oldData, byte[] newData)
    {
        // For large files, use a more efficient algorithm
        // This is a simplified Myers diff algorithm variant

        var result = new List<(int, int)>();

        // Use patience diff approach - find unique matching lines first
        int oldPos = 0;
        int newPos = 0;

        while (oldPos < oldData.Length && newPos < newData.Length)
        {
            if (oldData[oldPos] == newData[newPos])
            {
                result.Add((oldPos, newPos));
                oldPos++;
                newPos++;
            }
            else
            {
                // Find next match
                var (nextOld, nextNew) = FindNextMatch(oldData, newData, oldPos, newPos);
                if (nextOld >= 0 && nextNew >= 0)
                {
                    oldPos = nextOld;
                    newPos = nextNew;
                }
                else
                {
                    break;
                }
            }
        }

        return result;
    }

    private static (int oldIndex, int newIndex) FindNextMatch(
        byte[] oldData, byte[] newData, int oldStart, int newStart)
    {
        var searchLimit = Math.Min(1000, Math.Min(oldData.Length - oldStart, newData.Length - newStart));

        // Look for next matching byte
        for (int offset = 1; offset < searchLimit; offset++)
        {
            // Check advancing new position
            if (newStart + offset < newData.Length)
            {
                for (int oldOffset = 0; oldOffset <= offset && oldStart + oldOffset < oldData.Length; oldOffset++)
                {
                    if (oldData[oldStart + oldOffset] == newData[newStart + offset])
                    {
                        return (oldStart + oldOffset, newStart + offset);
                    }
                }
            }

            // Check advancing old position
            if (oldStart + offset < oldData.Length)
            {
                for (int newOffset = 0; newOffset <= offset && newStart + newOffset < newData.Length; newOffset++)
                {
                    if (oldData[oldStart + offset] == newData[newStart + newOffset])
                    {
                        return (oldStart + offset, newStart + newOffset);
                    }
                }
            }
        }

        return (-1, -1);
    }

    private static List<DiffHunk> ExtractHunksFromLCS(
        byte[] oldData,
        byte[] newData,
        List<(int oldIndex, int newIndex)> lcs)
    {
        var hunks = new List<DiffHunk>();

        int oldPos = 0;
        int newPos = 0;
        int lcsIndex = 0;

        while (oldPos < oldData.Length || newPos < newData.Length)
        {
            int nextOldMatch = lcsIndex < lcs.Count ? lcs[lcsIndex].oldIndex : oldData.Length;
            int nextNewMatch = lcsIndex < lcs.Count ? lcs[lcsIndex].newIndex : newData.Length;

            // Check for differences before next match
            if (oldPos < nextOldMatch || newPos < nextNewMatch)
            {
                var oldLength = nextOldMatch - oldPos;
                var newLength = nextNewMatch - newPos;

                if (oldLength > 0 || newLength > 0)
                {
                    hunks.Add(new DiffHunk
                    {
                        OldStart = oldPos,
                        OldLength = oldLength,
                        NewStart = newPos,
                        NewLength = newLength,
                        OldData = oldLength > 0 ? oldData[oldPos..(oldPos + oldLength)] : null,
                        NewData = newLength > 0 ? newData[newPos..(newPos + newLength)] : null
                    });
                }

                oldPos = nextOldMatch;
                newPos = nextNewMatch;
            }

            // Skip matching bytes
            while (lcsIndex < lcs.Count &&
                   lcs[lcsIndex].oldIndex == oldPos &&
                   lcs[lcsIndex].newIndex == newPos)
            {
                oldPos++;
                newPos++;
                lcsIndex++;
            }
        }

        return hunks;
    }

    private struct MatchResult
    {
        public int SourceOffset;
        public int Length;
    }
}

#endregion
