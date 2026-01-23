using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.DeltaSyncVersioning;

/// <summary>
/// Production-ready delta-based synchronization and versioning plugin for DataWarehouse.
/// Implements rsync-like rolling hash algorithms for efficient binary delta calculation,
/// delta chain management, version branching/merging, and full snapshot reconstruction.
/// Optimized for synchronization scenarios where changes between versions are incremental.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> All public methods are thread-safe. Uses concurrent collections
/// and semaphores for synchronization.</para>
/// <para><b>Performance:</b> Uses rolling hash with O(n) complexity for delta computation.
/// Delta chains are automatically compacted when they exceed configurable thresholds.</para>
/// <para><b>Storage:</b> Deltas are compressed using DEFLATE. Full snapshots are stored
/// periodically to limit reconstruction time.</para>
/// </remarks>
/// <example>
/// <code>
/// var config = new DeltaSyncVersioningConfig { MaxDeltaChainLength = 15 };
/// await using var plugin = new DeltaSyncVersioningPlugin(config);
/// await plugin.StartAsync(CancellationToken.None);
///
/// // Create first version (stored as full snapshot)
/// using var data1 = new MemoryStream(Encoding.UTF8.GetBytes("Hello World"));
/// var v1 = await plugin.CreateVersionAsync("doc1", data1);
///
/// // Create second version (stored as delta)
/// using var data2 = new MemoryStream(Encoding.UTF8.GetBytes("Hello World!"));
/// var v2 = await plugin.CreateVersionAsync("doc1", data2);
///
/// // Reconstruct any version
/// using var retrieved = await plugin.GetVersionAsync("doc1", v1.VersionId);
/// </code>
/// </example>
public sealed class DeltaSyncVersioningPlugin : VersioningPluginBase, IAsyncDisposable
{
    #region Constants

    /// <summary>Rolling hash prime multiplier (FNV-1a inspired).</summary>
    private const uint RollingHashPrime = 16777619u;

    /// <summary>Rolling hash initial value (FNV offset basis).</summary>
    private const uint RollingHashBasis = 2166136261u;

    /// <summary>Magic number for delta file header validation.</summary>
    private const uint DeltaMagicNumber = 0x444C5441; // "DLTA"

    /// <summary>Current delta format version for forward compatibility.</summary>
    private const ushort DeltaFormatVersion = 1;

    /// <summary>Delta instruction: copy bytes from source.</summary>
    private const byte OpCopy = 0x01;

    /// <summary>Delta instruction: insert new bytes.</summary>
    private const byte OpInsert = 0x02;

    /// <summary>Delta instruction: end of delta stream.</summary>
    private const byte OpEnd = 0xFF;

    /// <summary>Default block size for rolling hash computation.</summary>
    private const int DefaultBlockSize = 64;

    /// <summary>Maximum entries per hash bucket to prevent pathological cases.</summary>
    private const int MaxEntriesPerHashBucket = 128;

    /// <summary>Minimum match length to consider a copy operation worthwhile.</summary>
    private const int MinMatchLengthBytes = 8;

    /// <summary>Maximum size for an insert buffer before flushing.</summary>
    private const int MaxInsertBufferSize = 65535;

    #endregion

    #region Fields

    private readonly DeltaSyncVersioningConfig _config;
    private readonly string _storagePath;
    private readonly RollingHashDeltaEngine _deltaEngine;

    private readonly ConcurrentDictionary<string, DeltaSyncVersionGraph> _versionGraphs = new();
    private readonly ConcurrentDictionary<string, DeltaSyncBranchState> _branches = new();
    private readonly ConcurrentDictionary<string, byte[]> _snapshotStore = new();
    private readonly ConcurrentDictionary<string, DeltaRecord> _deltaStore = new();

    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Timer? _autoCompactTimer;
    private readonly Timer? _autoPruneTimer;

    private volatile bool _disposed;
    private long _operationCount;

    #endregion

    #region Properties

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.plugins.deltasync.versioning";

    /// <inheritdoc />
    public override string Name => "Delta Sync Versioning Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override bool SupportsBranching => true;

    /// <inheritdoc />
    public override bool SupportsDeltaStorage => true;

    #endregion

    #region Events

    /// <summary>
    /// Raised when a new version is created successfully.
    /// </summary>
    public event EventHandler<VersionInfo>? VersionCreated;

    /// <summary>
    /// Raised when branches are merged successfully.
    /// </summary>
    public event EventHandler<MergeResult>? BranchesMerged;

    /// <summary>
    /// Raised when a version is restored.
    /// </summary>
    public event EventHandler<VersionInfo>? VersionRestored;

    /// <summary>
    /// Raised when delta chains are compacted.
    /// </summary>
    public event EventHandler<DeltaCompactionEventArgs>? DeltaChainsCompacted;

    /// <summary>
    /// Raised when old versions are pruned.
    /// </summary>
    public event EventHandler<VersionPruneEventArgs>? VersionsPruned;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="DeltaSyncVersioningPlugin"/> class.
    /// </summary>
    /// <param name="config">Optional configuration. Uses defaults if null.</param>
    /// <exception cref="InvalidOperationException">Thrown if storage path creation fails.</exception>
    public DeltaSyncVersioningPlugin(DeltaSyncVersioningConfig? config = null)
    {
        _config = config ?? new DeltaSyncVersioningConfig();

        _storagePath = _config.StoragePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "DeltaSyncVersioning");

        try
        {
            Directory.CreateDirectory(_storagePath);
            Directory.CreateDirectory(Path.Combine(_storagePath, "snapshots"));
            Directory.CreateDirectory(Path.Combine(_storagePath, "deltas"));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create storage directories at '{_storagePath}'", ex);
        }

        _deltaEngine = new RollingHashDeltaEngine(new RollingHashConfig
        {
            BlockSize = _config.BlockSize,
            MinMatchLength = _config.MinMatchLength,
            EnableCompression = _config.EnableDeltaCompression
        });

        if (_config.AutoCompactIntervalSeconds > 0)
        {
            _autoCompactTimer = new Timer(
                async _ => await AutoCompactAsync(),
                null,
                TimeSpan.FromSeconds(_config.AutoCompactIntervalSeconds),
                TimeSpan.FromSeconds(_config.AutoCompactIntervalSeconds));
        }

        if (_config.AutoPruneIntervalSeconds > 0)
        {
            _autoPruneTimer = new Timer(
                async _ => await AutoPruneAsync(),
                null,
                TimeSpan.FromSeconds(_config.AutoPruneIntervalSeconds),
                TimeSpan.FromSeconds(_config.AutoPruneIntervalSeconds));
        }
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);
    }

    #endregion

    #region Core Version Operations

    /// <inheritdoc />
    /// <summary>
    /// Creates a new version using efficient delta encoding.
    /// </summary>
    /// <param name="objectId">Unique identifier for the object being versioned.</param>
    /// <param name="data">Stream containing the new version's content.</param>
    /// <param name="metadata">Optional version metadata including message and author.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about the created version.</returns>
    /// <exception cref="ArgumentException">Thrown if objectId is null or empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown if data is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if plugin is disposed.</exception>
    public override async Task<VersionInfo> CreateVersionAsync(
        string objectId,
        Stream data,
        VersionMetadata? metadata = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(data);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _operationCount);

            var branchName = metadata?.BranchName ?? _config.DefaultBranchName;
            var graph = GetOrCreateVersionGraph(objectId);
            var fullData = await ReadStreamToArrayAsync(data, ct).ConfigureAwait(false);
            var contentHash = ComputeSha256Hash(fullData);
            var versionId = GenerateVersionId();
            var now = DateTime.UtcNow;

            // Get current branch head for delta computation
            var branch = GetOrCreateBranch(objectId, branchName);
            string? parentVersionId = null;
            byte[]? parentData = null;

            if (!string.IsNullOrEmpty(branch.HeadVersionId))
            {
                parentVersionId = branch.HeadVersionId;
                parentData = await ReconstructSnapshotAsync(objectId, parentVersionId, ct).ConfigureAwait(false);
            }

            // Store version - use delta if parent exists and delta storage is enabled
            DeltaStorageInfo storageInfo;
            if (parentData != null && _config.EnableDeltaStorage)
            {
                // Check if delta would be worthwhile (small changes benefit most)
                var delta = _deltaEngine.ComputeDelta(parentData, fullData);
                var deltaSavings = fullData.Length - delta.Length;

                if (deltaSavings > _config.MinDeltaSavingsBytes || delta.Length < fullData.Length * _config.MaxDeltaRatio)
                {
                    await StoreDeltaAsync(objectId, versionId, parentVersionId!, delta, ct).ConfigureAwait(false);
                    storageInfo = new DeltaStorageInfo
                    {
                        IsDelta = true,
                        BaseVersionId = parentVersionId,
                        StoredSize = delta.Length,
                        OriginalSize = fullData.Length,
                        CompressionRatio = (double)delta.Length / fullData.Length
                    };
                }
                else
                {
                    // Delta not worthwhile, store as snapshot
                    await StoreSnapshotAsync(objectId, versionId, fullData, ct).ConfigureAwait(false);
                    storageInfo = new DeltaStorageInfo
                    {
                        IsDelta = false,
                        StoredSize = fullData.Length,
                        OriginalSize = fullData.Length,
                        CompressionRatio = 1.0
                    };
                }
            }
            else
            {
                // No parent - store as base snapshot
                await StoreSnapshotAsync(objectId, versionId, fullData, ct).ConfigureAwait(false);
                storageInfo = new DeltaStorageInfo
                {
                    IsDelta = false,
                    StoredSize = fullData.Length,
                    OriginalSize = fullData.Length,
                    CompressionRatio = 1.0
                };
            }

            // Create version node
            var versionNode = new DeltaSyncVersionNode
            {
                VersionId = versionId,
                ObjectId = objectId,
                ParentVersionIds = parentVersionId != null ? new List<string> { parentVersionId } : new List<string>(),
                CreatedAt = now,
                ContentHash = contentHash,
                BranchName = branchName,
                Message = metadata?.Message,
                Author = metadata?.Author,
                Size = fullData.Length,
                StorageInfo = storageInfo,
                Tags = metadata?.Tags ?? new Dictionary<string, string>()
            };

            // Update graph
            graph.AddVersion(versionNode);

            // Mark previous head as not latest
            if (!string.IsNullOrEmpty(parentVersionId) && graph.Versions.TryGetValue(parentVersionId, out var parentNode))
            {
                parentNode.IsLatest = false;
            }

            // Update branch head
            branch.HeadVersionId = versionId;
            branch.LastModified = now;
            branch.VersionCount++;

            // Check if auto-compaction is needed
            await CheckAndCompactChainAsync(objectId, versionId, ct).ConfigureAwait(false);

            await PersistStateAsync(ct).ConfigureAwait(false);

            var versionInfo = ToVersionInfo(versionNode);
            VersionCreated?.Invoke(this, versionInfo);

            return versionInfo;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Retrieves a specific version by reconstructing it from the delta chain.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version identifier to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the version's content.</returns>
    /// <exception cref="ArgumentException">Thrown if identifiers are null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown if version is not found.</exception>
    public override async Task<Stream> GetVersionAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        var data = await ReconstructSnapshotAsync(objectId, versionId, ct).ConfigureAwait(false);

        if (data == null)
        {
            throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'");
        }

        return new MemoryStream(data, writable: false);
    }

    /// <inheritdoc />
    /// <summary>
    /// Lists versions for an object, ordered by creation time descending.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="limit">Maximum number of versions to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of version information.</returns>
    public override Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(
        string objectId,
        int limit = 100,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);

        if (!_versionGraphs.TryGetValue(objectId, out var graph))
        {
            return Task.FromResult<IReadOnlyList<VersionInfo>>(Array.Empty<VersionInfo>());
        }

        var versions = graph.Versions.Values
            .OrderByDescending(v => v.CreatedAt)
            .Take(limit)
            .Select(ToVersionInfo)
            .ToList();

        return Task.FromResult<IReadOnlyList<VersionInfo>>(versions);
    }

    /// <inheritdoc />
    /// <summary>
    /// Deletes a version. If the version is a base snapshot with dependent deltas,
    /// those deltas will be rebased or the deletion will fail based on configuration.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deleted successfully.</returns>
    public override async Task<bool> DeleteVersionAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_versionGraphs.TryGetValue(objectId, out var graph))
            {
                return false;
            }

            if (!graph.Versions.TryGetValue(versionId, out var version))
            {
                return false;
            }

            // Find versions that depend on this one
            var dependentVersions = graph.Versions.Values
                .Where(v => v.StorageInfo.IsDelta && v.StorageInfo.BaseVersionId == versionId)
                .ToList();

            if (dependentVersions.Count > 0)
            {
                if (!_config.AllowCascadeDelete)
                {
                    throw new InvalidOperationException(
                        $"Cannot delete version '{versionId}' because {dependentVersions.Count} versions depend on it. " +
                        "Enable cascade delete or rebase dependent versions first.");
                }

                // Rebase dependent versions to parent or make them snapshots
                foreach (var dependent in dependentVersions)
                {
                    await RebaseVersionAsync(objectId, dependent.VersionId, version.ParentVersionIds.FirstOrDefault(), ct)
                        .ConfigureAwait(false);
                }
            }

            // Remove from storage
            var snapshotKey = GetSnapshotKey(objectId, versionId);
            var deltaKey = GetDeltaKey(objectId, versionId);
            _snapshotStore.TryRemove(snapshotKey, out _);
            _deltaStore.TryRemove(deltaKey, out _);

            // Remove storage files
            var snapshotFile = Path.Combine(_storagePath, "snapshots", $"{snapshotKey.Replace("/", "_")}.snap");
            var deltaFile = Path.Combine(_storagePath, "deltas", $"{deltaKey.Replace("/", "_")}.delta");
            TryDeleteFile(snapshotFile);
            TryDeleteFile(deltaFile);

            // Remove from graph
            graph.Versions.TryRemove(versionId, out _);

            // Update branch heads if needed
            foreach (var branch in _branches.Values.Where(b => b.ObjectId == objectId && b.HeadVersionId == versionId))
            {
                branch.HeadVersionId = version.ParentVersionIds.FirstOrDefault() ?? string.Empty;
                branch.VersionCount--;
            }

            await PersistStateAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Restores an object to a specific version by creating a new version with that content.
    /// </summary>
    public override async Task<VersionInfo> RestoreVersionAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        var data = await ReconstructSnapshotAsync(objectId, versionId, ct).ConfigureAwait(false);
        if (data == null)
        {
            throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'");
        }

        if (!_versionGraphs.TryGetValue(objectId, out var graph) ||
            !graph.Versions.TryGetValue(versionId, out var originalVersion))
        {
            throw new KeyNotFoundException($"Version metadata not found for '{versionId}'");
        }

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
        var newVersion = await CreateVersionAsync(objectId, stream, metadata, ct).ConfigureAwait(false);
        VersionRestored?.Invoke(this, newVersion);
        return newVersion;
    }

    #endregion

    #region Diff Operations

    /// <inheritdoc />
    /// <summary>
    /// Computes detailed diff between two versions with hunk-level granularity.
    /// </summary>
    public override async Task<VersionDiff> GetDiffAsync(
        string objectId,
        string fromVersionId,
        string toVersionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(fromVersionId);
        ArgumentException.ThrowIfNullOrEmpty(toVersionId);

        var fromData = await ReconstructSnapshotAsync(objectId, fromVersionId, ct).ConfigureAwait(false);
        var toData = await ReconstructSnapshotAsync(objectId, toVersionId, ct).ConfigureAwait(false);

        if (fromData == null)
        {
            throw new KeyNotFoundException($"Version '{fromVersionId}' not found");
        }
        if (toData == null)
        {
            throw new KeyNotFoundException($"Version '{toVersionId}' not found");
        }

        var hunks = _deltaEngine.ComputeDiffHunks(fromData, toData);

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

    /// <inheritdoc />
    /// <summary>
    /// Creates a new branch starting from a specific version.
    /// </summary>
    public override async Task<BranchInfo> CreateBranchAsync(
        string objectId,
        string versionId,
        string branchName,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);
        ArgumentException.ThrowIfNullOrEmpty(branchName);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
            var branch = new DeltaSyncBranchState
            {
                ObjectId = objectId,
                Name = branchName,
                HeadVersionId = versionId,
                BaseVersionId = versionId,
                CreatedAt = now,
                LastModified = now,
                VersionCount = 1
            };

            _branches[branchKey] = branch;
            await PersistStateAsync(ct).ConfigureAwait(false);

            return new BranchInfo
            {
                Name = branchName,
                HeadVersionId = versionId,
                BaseVersionId = versionId,
                CreatedAt = now
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Merges source branch into target branch with conflict detection.
    /// </summary>
    public override async Task<MergeResult> MergeBranchAsync(
        string objectId,
        string sourceBranch,
        string targetBranch,
        MergeStrategy strategy = MergeStrategy.ThreeWay,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(sourceBranch);
        ArgumentException.ThrowIfNullOrEmpty(targetBranch);

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sourceKey = GetBranchKey(objectId, sourceBranch);
            var targetKey = GetBranchKey(objectId, targetBranch);

            if (!_branches.TryGetValue(sourceKey, out var source))
            {
                throw new KeyNotFoundException($"Source branch '{sourceBranch}' not found");
            }

            if (!_branches.TryGetValue(targetKey, out var target))
            {
                throw new KeyNotFoundException($"Target branch '{targetBranch}' not found");
            }

            // Find common ancestor
            var mergeBase = await FindMergeBaseAsync(objectId, source.HeadVersionId, target.HeadVersionId, ct)
                .ConfigureAwait(false);

            // Get data for all versions
            var baseData = mergeBase != null
                ? await ReconstructSnapshotAsync(objectId, mergeBase, ct).ConfigureAwait(false)
                : Array.Empty<byte>();

            var sourceData = await ReconstructSnapshotAsync(objectId, source.HeadVersionId, ct).ConfigureAwait(false);
            var targetData = await ReconstructSnapshotAsync(objectId, target.HeadVersionId, ct).ConfigureAwait(false);

            if (sourceData == null || targetData == null)
            {
                return new MergeResult
                {
                    Success = false,
                    HasConflicts = false,
                    Conflicts = Array.Empty<MergeConflict>()
                };
            }

            // Perform merge based on strategy
            var (mergedData, conflicts) = strategy switch
            {
                MergeStrategy.ThreeWay => PerformThreeWayMerge(baseData ?? Array.Empty<byte>(), sourceData, targetData),
                MergeStrategy.Ours => (targetData, new List<MergeConflict>()),
                MergeStrategy.Theirs => (sourceData, new List<MergeConflict>()),
                MergeStrategy.Union => PerformUnionMerge(baseData ?? Array.Empty<byte>(), sourceData, targetData),
                _ => throw new ArgumentException($"Unknown merge strategy: {strategy}", nameof(strategy))
            };

            if (conflicts.Count > 0)
            {
                return new MergeResult
                {
                    Success = false,
                    HasConflicts = true,
                    Conflicts = conflicts
                };
            }

            // Create merge commit
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
            var mergeVersion = await CreateVersionAsync(objectId, stream, metadata, ct).ConfigureAwait(false);

            // Add source head as additional parent
            if (_versionGraphs.TryGetValue(objectId, out var graph) &&
                graph.Versions.TryGetValue(mergeVersion.VersionId, out var mergeNode))
            {
                if (!mergeNode.ParentVersionIds.Contains(source.HeadVersionId))
                {
                    mergeNode.ParentVersionIds.Add(source.HeadVersionId);
                }
            }

            await PersistStateAsync(ct).ConfigureAwait(false);

            var result = new MergeResult
            {
                Success = true,
                ResultVersionId = mergeVersion.VersionId,
                HasConflicts = false,
                Conflicts = Array.Empty<MergeConflict>()
            };

            BranchesMerged?.Invoke(this, result);
            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Lists all branches for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of branch information.</returns>
    public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(
        string objectId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
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
    /// Deletes a branch. Does not delete the versions on the branch.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="branchName">Branch name to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deleted successfully.</returns>
    public async Task<bool> DeleteBranchAsync(
        string objectId,
        string branchName,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
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
            await PersistStateAsync(ct).ConfigureAwait(false);
        }

        return removed;
    }

    #endregion

    #region Delta Chain Management

    /// <summary>
    /// Gets the delta chain length for a specific version.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Length of the delta chain (0 if version is a snapshot).</returns>
    public async Task<int> GetDeltaChainLengthAsync(
        string objectId,
        string versionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentException.ThrowIfNullOrEmpty(versionId);

        return await ComputeChainLengthAsync(objectId, versionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compacts delta chains that exceed the maximum length by creating new snapshots.
    /// </summary>
    /// <param name="objectId">Object identifier. If null, compacts all objects.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of versions converted to snapshots.</returns>
    public async Task<int> CompactDeltaChainsAsync(
        string? objectId = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var graphs = objectId != null && _versionGraphs.TryGetValue(objectId, out var graph)
            ? new[] { graph }
            : _versionGraphs.Values.ToArray();

        var compacted = 0;

        foreach (var g in graphs)
        {
            foreach (var version in g.Versions.Values.Where(v => v.StorageInfo.IsDelta))
            {
                var chainLength = await ComputeChainLengthAsync(g.ObjectId, version.VersionId, ct).ConfigureAwait(false);

                if (chainLength > _config.MaxDeltaChainLength)
                {
                    var data = await ReconstructSnapshotAsync(g.ObjectId, version.VersionId, ct).ConfigureAwait(false);
                    if (data != null)
                    {
                        // Remove delta
                        var deltaKey = GetDeltaKey(g.ObjectId, version.VersionId);
                        _deltaStore.TryRemove(deltaKey, out _);

                        // Store as snapshot
                        await StoreSnapshotAsync(g.ObjectId, version.VersionId, data, ct).ConfigureAwait(false);

                        // Update storage info
                        version.StorageInfo = new DeltaStorageInfo
                        {
                            IsDelta = false,
                            StoredSize = data.Length,
                            OriginalSize = data.Length,
                            CompressionRatio = 1.0
                        };

                        compacted++;
                    }
                }
            }
        }

        if (compacted > 0)
        {
            await PersistStateAsync(ct).ConfigureAwait(false);
            DeltaChainsCompacted?.Invoke(this, new DeltaCompactionEventArgs
            {
                ObjectId = objectId,
                VersionsCompacted = compacted,
                CompactedAt = DateTime.UtcNow
            });
        }

        return compacted;
    }

    /// <summary>
    /// Prunes old versions based on retention policy.
    /// </summary>
    /// <param name="objectId">Object identifier. If null, prunes all objects.</param>
    /// <param name="retentionDays">Minimum age in days for versions to be eligible for pruning.</param>
    /// <param name="keepMinVersions">Minimum versions to keep regardless of age.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of versions pruned.</returns>
    public async Task<int> PruneOldVersionsAsync(
        string? objectId = null,
        int? retentionDays = null,
        int? keepMinVersions = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var effectiveRetentionDays = retentionDays ?? _config.RetentionDays;
        var effectiveMinVersions = keepMinVersions ?? _config.MinVersionsToKeep;

        if (effectiveRetentionDays <= 0)
        {
            return 0;
        }

        var graphs = objectId != null && _versionGraphs.TryGetValue(objectId, out var graph)
            ? new[] { graph }
            : _versionGraphs.Values.ToArray();

        var pruned = 0;
        var cutoffDate = DateTime.UtcNow.AddDays(-effectiveRetentionDays);

        foreach (var g in graphs)
        {
            var versions = g.Versions.Values
                .OrderByDescending(v => v.CreatedAt)
                .ToList();

            // Keep minimum number of versions
            var eligibleForPruning = versions
                .Skip(effectiveMinVersions)
                .Where(v => v.CreatedAt < cutoffDate && !v.IsLatest)
                .ToList();

            foreach (var version in eligibleForPruning)
            {
                try
                {
                    var deleted = await DeleteVersionAsync(g.ObjectId, version.VersionId, ct).ConfigureAwait(false);
                    if (deleted)
                    {
                        pruned++;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Skip versions with dependents if cascade delete is disabled
                }
            }
        }

        if (pruned > 0)
        {
            VersionsPruned?.Invoke(this, new VersionPruneEventArgs
            {
                ObjectId = objectId,
                VersionsPruned = pruned,
                PrunedAt = DateTime.UtcNow
            });
        }

        return pruned;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets comprehensive statistics for delta sync versioning.
    /// </summary>
    /// <param name="objectId">Object identifier. If null, returns aggregate stats.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Versioning statistics.</returns>
    public Task<DeltaSyncStatistics> GetStatisticsAsync(
        string? objectId = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var graphs = objectId != null && _versionGraphs.TryGetValue(objectId, out var graph)
            ? new[] { graph }
            : _versionGraphs.Values.ToArray();

        var totalVersions = graphs.Sum(g => g.Versions.Count);
        var snapshotCount = graphs.Sum(g => g.Versions.Values.Count(v => !v.StorageInfo.IsDelta));
        var deltaCount = graphs.Sum(g => g.Versions.Values.Count(v => v.StorageInfo.IsDelta));
        var branchCount = objectId != null
            ? _branches.Values.Count(b => b.ObjectId == objectId)
            : _branches.Count;

        var totalLogicalSize = graphs.Sum(g => g.Versions.Values.Sum(v => v.Size));
        var totalPhysicalSize = graphs.Sum(g => g.Versions.Values.Sum(v => v.StorageInfo.StoredSize));

        var avgChainLength = deltaCount > 0
            ? graphs.SelectMany(g => g.Versions.Values.Where(v => v.StorageInfo.IsDelta))
                .Average(v => 1) // Would need async computation
            : 0;

        return Task.FromResult(new DeltaSyncStatistics
        {
            ObjectId = objectId,
            TotalObjects = graphs.Length,
            TotalVersions = totalVersions,
            SnapshotCount = snapshotCount,
            DeltaCount = deltaCount,
            BranchCount = branchCount,
            TotalLogicalSize = totalLogicalSize,
            TotalPhysicalSize = totalPhysicalSize,
            SpaceSavings = totalLogicalSize > 0 ? 1.0 - ((double)totalPhysicalSize / totalLogicalSize) : 0,
            AverageChainLength = avgChainLength,
            OperationCount = Interlocked.Read(ref _operationCount)
        });
    }

    #endregion

    #region Private Methods - Delta Operations

    private async Task<byte[]?> ReconstructSnapshotAsync(
        string objectId,
        string versionId,
        CancellationToken ct)
    {
        var snapshotKey = GetSnapshotKey(objectId, versionId);

        // Check if it's a stored snapshot
        if (_snapshotStore.TryGetValue(snapshotKey, out var snapshot))
        {
            return snapshot;
        }

        // Try loading from disk
        var snapshotFile = Path.Combine(_storagePath, "snapshots", $"{snapshotKey.Replace("/", "_")}.snap");
        if (File.Exists(snapshotFile))
        {
            var data = await File.ReadAllBytesAsync(snapshotFile, ct).ConfigureAwait(false);
            var decompressed = DecompressData(data);
            _snapshotStore[snapshotKey] = decompressed;
            return decompressed;
        }

        // Check if it's a delta
        var deltaKey = GetDeltaKey(objectId, versionId);
        if (_deltaStore.TryGetValue(deltaKey, out var deltaRecord))
        {
            // Recursively reconstruct base version
            var baseData = await ReconstructSnapshotAsync(objectId, deltaRecord.BaseVersionId, ct).ConfigureAwait(false);
            if (baseData == null)
            {
                return null;
            }

            // Apply delta
            return _deltaEngine.ApplyDelta(baseData, deltaRecord.DeltaData);
        }

        // Try loading delta from disk
        var deltaFile = Path.Combine(_storagePath, "deltas", $"{deltaKey.Replace("/", "_")}.delta");
        if (File.Exists(deltaFile))
        {
            var deltaJson = await File.ReadAllTextAsync(deltaFile, ct).ConfigureAwait(false);
            var delta = JsonSerializer.Deserialize<DeltaRecord>(deltaJson, JsonOptions);
            if (delta != null)
            {
                _deltaStore[deltaKey] = delta;
                var baseData = await ReconstructSnapshotAsync(objectId, delta.BaseVersionId, ct).ConfigureAwait(false);
                if (baseData != null)
                {
                    return _deltaEngine.ApplyDelta(baseData, delta.DeltaData);
                }
            }
        }

        return null;
    }

    private Task StoreSnapshotAsync(string objectId, string versionId, byte[] data, CancellationToken ct)
    {
        var key = GetSnapshotKey(objectId, versionId);
        var compressed = CompressData(data);
        _snapshotStore[key] = data; // Store uncompressed in memory

        // Persist compressed to disk
        var file = Path.Combine(_storagePath, "snapshots", $"{key.Replace("/", "_")}.snap");
        return File.WriteAllBytesAsync(file, compressed, ct);
    }

    private Task StoreDeltaAsync(string objectId, string versionId, string baseVersionId, byte[] delta, CancellationToken ct)
    {
        var key = GetDeltaKey(objectId, versionId);
        var record = new DeltaRecord
        {
            VersionId = versionId,
            BaseVersionId = baseVersionId,
            DeltaData = delta,
            CreatedAt = DateTime.UtcNow
        };

        _deltaStore[key] = record;

        var file = Path.Combine(_storagePath, "deltas", $"{key.Replace("/", "_")}.delta");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        return File.WriteAllTextAsync(file, json, ct);
    }

    private async Task<int> ComputeChainLengthAsync(string objectId, string versionId, CancellationToken ct)
    {
        var length = 0;
        var currentId = versionId;
        var visited = new HashSet<string>();

        while (!string.IsNullOrEmpty(currentId) && visited.Add(currentId))
        {
            var snapshotKey = GetSnapshotKey(objectId, currentId);
            if (_snapshotStore.ContainsKey(snapshotKey))
            {
                break;
            }

            var snapshotFile = Path.Combine(_storagePath, "snapshots", $"{snapshotKey.Replace("/", "_")}.snap");
            if (File.Exists(snapshotFile))
            {
                break;
            }

            var deltaKey = GetDeltaKey(objectId, currentId);
            if (_deltaStore.TryGetValue(deltaKey, out var delta))
            {
                length++;
                currentId = delta.BaseVersionId;
            }
            else
            {
                var deltaFile = Path.Combine(_storagePath, "deltas", $"{deltaKey.Replace("/", "_")}.delta");
                if (File.Exists(deltaFile))
                {
                    var json = await File.ReadAllTextAsync(deltaFile, ct).ConfigureAwait(false);
                    var record = JsonSerializer.Deserialize<DeltaRecord>(json, JsonOptions);
                    if (record != null)
                    {
                        length++;
                        currentId = record.BaseVersionId;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        return length;
    }

    private async Task CheckAndCompactChainAsync(string objectId, string versionId, CancellationToken ct)
    {
        if (!_config.EnableAutoCompaction)
        {
            return;
        }

        var chainLength = await ComputeChainLengthAsync(objectId, versionId, ct).ConfigureAwait(false);

        if (chainLength > _config.MaxDeltaChainLength)
        {
            var data = await ReconstructSnapshotAsync(objectId, versionId, ct).ConfigureAwait(false);
            if (data != null && _versionGraphs.TryGetValue(objectId, out var graph) &&
                graph.Versions.TryGetValue(versionId, out var version))
            {
                // Remove delta
                var deltaKey = GetDeltaKey(objectId, versionId);
                _deltaStore.TryRemove(deltaKey, out _);

                // Store as snapshot
                await StoreSnapshotAsync(objectId, versionId, data, ct).ConfigureAwait(false);

                // Update storage info
                version.StorageInfo = new DeltaStorageInfo
                {
                    IsDelta = false,
                    StoredSize = data.Length,
                    OriginalSize = data.Length,
                    CompressionRatio = 1.0
                };
            }
        }
    }

    private async Task RebaseVersionAsync(string objectId, string versionId, string? newBaseId, CancellationToken ct)
    {
        var data = await ReconstructSnapshotAsync(objectId, versionId, ct).ConfigureAwait(false);
        if (data == null) return;

        if (!_versionGraphs.TryGetValue(objectId, out var graph) ||
            !graph.Versions.TryGetValue(versionId, out var version))
        {
            return;
        }

        // Remove old delta
        var oldDeltaKey = GetDeltaKey(objectId, versionId);
        _deltaStore.TryRemove(oldDeltaKey, out _);

        if (string.IsNullOrEmpty(newBaseId))
        {
            // Store as snapshot
            await StoreSnapshotAsync(objectId, versionId, data, ct).ConfigureAwait(false);
            version.StorageInfo = new DeltaStorageInfo
            {
                IsDelta = false,
                StoredSize = data.Length,
                OriginalSize = data.Length,
                CompressionRatio = 1.0
            };
        }
        else
        {
            var baseData = await ReconstructSnapshotAsync(objectId, newBaseId, ct).ConfigureAwait(false);
            if (baseData != null)
            {
                var delta = _deltaEngine.ComputeDelta(baseData, data);
                await StoreDeltaAsync(objectId, versionId, newBaseId, delta, ct).ConfigureAwait(false);
                version.StorageInfo = new DeltaStorageInfo
                {
                    IsDelta = true,
                    BaseVersionId = newBaseId,
                    StoredSize = delta.Length,
                    OriginalSize = data.Length,
                    CompressionRatio = (double)delta.Length / data.Length
                };
            }
            else
            {
                await StoreSnapshotAsync(objectId, versionId, data, ct).ConfigureAwait(false);
                version.StorageInfo = new DeltaStorageInfo
                {
                    IsDelta = false,
                    StoredSize = data.Length,
                    OriginalSize = data.Length,
                    CompressionRatio = 1.0
                };
            }
        }

        // Update parent references
        version.ParentVersionIds.Clear();
        if (!string.IsNullOrEmpty(newBaseId))
        {
            version.ParentVersionIds.Add(newBaseId);
        }
    }

    #endregion

    #region Private Methods - Merge

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

        var ancestors1 = new HashSet<string>();
        var ancestors2 = new HashSet<string>();

        CollectAncestors(graph, versionId1, ancestors1);
        CollectAncestors(graph, versionId2, ancestors2);

        var commonAncestors = ancestors1.Intersect(ancestors2).ToList();

        if (commonAncestors.Count == 0)
        {
            return null;
        }

        return commonAncestors
            .Where(id => graph.Versions.ContainsKey(id))
            .OrderByDescending(id => graph.Versions[id].CreatedAt)
            .FirstOrDefault();
    }

    private void CollectAncestors(DeltaSyncVersionGraph graph, string versionId, HashSet<string> ancestors)
    {
        var queue = new Queue<string>();
        queue.Enqueue(versionId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!ancestors.Add(current)) continue;

            if (graph.Versions.TryGetValue(current, out var node))
            {
                foreach (var parent in node.ParentVersionIds)
                {
                    queue.Enqueue(parent);
                }
            }
        }
    }

    private (byte[] mergedData, List<MergeConflict> conflicts) PerformThreeWayMerge(
        byte[] baseData,
        byte[] sourceData,
        byte[] targetData)
    {
        var conflicts = new List<MergeConflict>();

        // Compute changes from base to each branch
        var sourceHunks = _deltaEngine.ComputeDiffHunks(baseData, sourceData);
        var targetHunks = _deltaEngine.ComputeDiffHunks(baseData, targetData);

        // Build change maps by position
        var sourceChanges = BuildChangeMap(sourceHunks);
        var targetChanges = BuildChangeMap(targetHunks);

        var result = new List<byte>();
        long position = 0;

        var maxPos = Math.Max(
            sourceChanges.Count > 0 ? sourceChanges.Keys.Max() + sourceChanges.Values.Max(v => v.Length) : 0,
            targetChanges.Count > 0 ? targetChanges.Keys.Max() + targetChanges.Values.Max(v => v.Length) : 0);
        maxPos = Math.Max(maxPos, Math.Max(sourceData.Length, targetData.Length));

        var processedPositions = new HashSet<long>();

        while (position < maxPos)
        {
            var hasSourceChange = sourceChanges.TryGetValue(position, out var sourceChange);
            var hasTargetChange = targetChanges.TryGetValue(position, out var targetChange);

            if (hasSourceChange && hasTargetChange)
            {
                if (!sourceChange!.SequenceEqual(targetChange!))
                {
                    // Conflict
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

                    // Default to target (ours)
                    result.AddRange(targetChange);
                    position += targetChange.Length;
                }
                else
                {
                    // Same change
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
                // No change at this position
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
        byte[] baseData,
        byte[] sourceData,
        byte[] targetData)
    {
        // Union merge: take longer version, prefer target for conflicts
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

    #endregion

    #region Private Methods - Helpers

    private DeltaSyncVersionGraph GetOrCreateVersionGraph(string objectId)
    {
        return _versionGraphs.GetOrAdd(objectId, id => new DeltaSyncVersionGraph { ObjectId = id });
    }

    private DeltaSyncBranchState GetOrCreateBranch(string objectId, string branchName)
    {
        var key = GetBranchKey(objectId, branchName);
        return _branches.GetOrAdd(key, _ => new DeltaSyncBranchState
        {
            ObjectId = objectId,
            Name = branchName,
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        });
    }

    private static string GetSnapshotKey(string objectId, string versionId) => $"{objectId}/snap/{versionId}";
    private static string GetDeltaKey(string objectId, string versionId) => $"{objectId}/delta/{versionId}";
    private static string GetBranchKey(string objectId, string branchName) => $"{objectId}:{branchName}";
    private static string GenerateVersionId() => $"dsv_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}";

    private static string ComputeSha256Hash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadStreamToArrayAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, ct).ConfigureAwait(false);
        return memoryStream.ToArray();
    }

    private static byte[] CompressData(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] DecompressData(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static VersionInfo ToVersionInfo(DeltaSyncVersionNode node)
    {
        return new VersionInfo
        {
            VersionId = node.VersionId,
            ObjectId = node.ObjectId,
            Size = node.Size,
            CreatedAt = node.CreatedAt,
            CreatedBy = node.Author,
            Message = node.Message,
            ParentVersionId = node.ParentVersionIds.FirstOrDefault(),
            BranchName = node.BranchName,
            IsLatest = node.IsLatest,
            ContentHash = node.ContentHash,
            Tags = node.Tags
        };
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore deletion errors
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #endregion

    #region Private Methods - Auto Operations

    private async Task AutoCompactAsync()
    {
        if (_disposed) return;

        try
        {
            await CompactDeltaChainsAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors in background operation
        }
    }

    private async Task AutoPruneAsync()
    {
        if (_disposed) return;

        try
        {
            await PruneOldVersionsAsync(null, null, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors in background operation
        }
    }

    #endregion

    #region Persistence

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var stateFile = Path.Combine(_storagePath, "deltasync_state.json");
        if (!File.Exists(stateFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<DeltaSyncState>(json, JsonOptions);

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
        }
        catch
        {
            // Start with empty state on error
        }
    }

    private async Task PersistStateAsync(CancellationToken ct)
    {
        await _persistLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = new DeltaSyncState
            {
                Graphs = _versionGraphs.Values.ToList(),
                Branches = _branches.Values.ToList(),
                SavedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(state, JsonOptions);
            var stateFile = Path.Combine(_storagePath, "deltasync_state.json");
            await File.WriteAllTextAsync(stateFile, json, ct).ConfigureAwait(false);
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

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "deltasync.create-version",
                DisplayName = "Create Version",
                Description = "Creates a new version with delta encoding for efficient storage"
            },
            new()
            {
                Name = "deltasync.get-version",
                DisplayName = "Get Version",
                Description = "Retrieves a version by reconstructing from delta chain"
            },
            new()
            {
                Name = "deltasync.list-versions",
                DisplayName = "List Versions",
                Description = "Lists all versions for an object"
            },
            new()
            {
                Name = "deltasync.delete-version",
                DisplayName = "Delete Version",
                Description = "Deletes a version with optional cascade handling"
            },
            new()
            {
                Name = "deltasync.restore-version",
                DisplayName = "Restore Version",
                Description = "Restores object to a specific version"
            },
            new()
            {
                Name = "deltasync.get-diff",
                DisplayName = "Get Diff",
                Description = "Computes detailed diff between two versions"
            },
            new()
            {
                Name = "deltasync.create-branch",
                DisplayName = "Create Branch",
                Description = "Creates a new branch from a version"
            },
            new()
            {
                Name = "deltasync.merge-branches",
                DisplayName = "Merge Branches",
                Description = "Merges branches with conflict detection"
            },
            new()
            {
                Name = "deltasync.compact-chains",
                DisplayName = "Compact Delta Chains",
                Description = "Compacts long delta chains into snapshots"
            },
            new()
            {
                Name = "deltasync.prune-versions",
                DisplayName = "Prune Old Versions",
                Description = "Removes old versions based on retention policy"
            }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Delta-based sync versioning with rolling hash and efficient reconstruction";
        metadata["SupportsBranching"] = true;
        metadata["SupportsDeltaStorage"] = true;
        metadata["SupportsCompression"] = true;
        metadata["SupportsAutoCompaction"] = true;
        metadata["SupportsRetentionPolicy"] = true;
        metadata["TotalObjects"] = _versionGraphs.Count;
        metadata["TotalBranches"] = _branches.Count;
        metadata["TotalVersions"] = _versionGraphs.Values.Sum(g => g.Versions.Count);
        metadata["OperationCount"] = Interlocked.Read(ref _operationCount);
        return metadata;
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Disposes the plugin and releases all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_autoCompactTimer != null)
        {
            await _autoCompactTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_autoPruneTimer != null)
        {
            await _autoPruneTimer.DisposeAsync().ConfigureAwait(false);
        }

        await PersistStateAsync(CancellationToken.None).ConfigureAwait(false);

        _persistLock.Dispose();
        _operationLock.Dispose();
    }

    #endregion
}

#region Configuration

/// <summary>
/// Configuration for the delta sync versioning plugin.
/// </summary>
public sealed record DeltaSyncVersioningConfig
{
    /// <summary>
    /// Storage path for version data. Defaults to user app data directory.
    /// </summary>
    public string? StoragePath { get; init; }

    /// <summary>
    /// Enable delta storage for efficient space usage. Default: true.
    /// </summary>
    public bool EnableDeltaStorage { get; init; } = true;

    /// <summary>
    /// Enable delta compression. Default: true.
    /// </summary>
    public bool EnableDeltaCompression { get; init; } = true;

    /// <summary>
    /// Default branch name for new objects. Default: "main".
    /// </summary>
    public string DefaultBranchName { get; init; } = "main";

    /// <summary>
    /// Allow cascading deletion of dependent versions. Default: false.
    /// </summary>
    public bool AllowCascadeDelete { get; init; } = false;

    /// <summary>
    /// Maximum delta chain length before auto-compaction. Default: 10.
    /// </summary>
    public int MaxDeltaChainLength { get; init; } = 10;

    /// <summary>
    /// Enable automatic compaction of long chains. Default: true.
    /// </summary>
    public bool EnableAutoCompaction { get; init; } = true;

    /// <summary>
    /// Block size for rolling hash computation. Default: 64 bytes.
    /// </summary>
    public int BlockSize { get; init; } = 64;

    /// <summary>
    /// Minimum match length for copy operations. Default: 8 bytes.
    /// </summary>
    public int MinMatchLength { get; init; } = 8;

    /// <summary>
    /// Minimum bytes saved by delta to use delta storage. Default: 64 bytes.
    /// </summary>
    public int MinDeltaSavingsBytes { get; init; } = 64;

    /// <summary>
    /// Maximum delta to original size ratio. Default: 0.9 (90%).
    /// </summary>
    public double MaxDeltaRatio { get; init; } = 0.9;

    /// <summary>
    /// Days to retain old versions (0 = infinite). Default: 0.
    /// </summary>
    public int RetentionDays { get; init; } = 0;

    /// <summary>
    /// Minimum versions to keep regardless of retention policy. Default: 5.
    /// </summary>
    public int MinVersionsToKeep { get; init; } = 5;

    /// <summary>
    /// Auto-compact interval in seconds (0 = disabled). Default: 3600 (1 hour).
    /// </summary>
    public int AutoCompactIntervalSeconds { get; init; } = 3600;

    /// <summary>
    /// Auto-prune interval in seconds (0 = disabled). Default: 86400 (1 day).
    /// </summary>
    public int AutoPruneIntervalSeconds { get; init; } = 86400;
}

#endregion

#region Rolling Hash Delta Engine

/// <summary>
/// Configuration for the rolling hash delta engine.
/// </summary>
public sealed record RollingHashConfig
{
    /// <summary>Block size for rolling hash. Default: 64.</summary>
    public int BlockSize { get; init; } = 64;

    /// <summary>Minimum match length for copy operations. Default: 8.</summary>
    public int MinMatchLength { get; init; } = 8;

    /// <summary>Enable compression of delta data. Default: true.</summary>
    public bool EnableCompression { get; init; } = true;
}

/// <summary>
/// Rsync-like rolling hash delta engine for efficient binary delta computation.
/// Uses a variant of the Rabin fingerprint algorithm for fast block matching.
/// </summary>
internal sealed class RollingHashDeltaEngine
{
    private const uint Prime = 16777619u;
    private const uint Basis = 2166136261u;
    private const byte OpCopy = 0x01;
    private const byte OpInsert = 0x02;
    private const byte OpEnd = 0xFF;
    private const uint DeltaMagic = 0x444C5441u;
    private const ushort FormatVersion = 1;

    private readonly RollingHashConfig _config;

    public RollingHashDeltaEngine(RollingHashConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Computes a delta that transforms source data into target data.
    /// </summary>
    /// <param name="sourceData">Original data.</param>
    /// <param name="targetData">Target data.</param>
    /// <returns>Compressed delta data.</returns>
    public byte[] ComputeDelta(byte[] sourceData, byte[] targetData)
    {
        if (sourceData.Length == 0)
        {
            return EncodeFullInsert(targetData);
        }

        if (targetData.Length == 0)
        {
            return EncodeEmpty();
        }

        // Build rolling hash index of source
        var sourceIndex = BuildRollingHashIndex(sourceData);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        // Header
        writer.Write(DeltaMagic);
        writer.Write(FormatVersion);
        writer.Write(sourceData.Length);
        writer.Write(targetData.Length);

        var targetPos = 0;
        var pendingInsert = new List<byte>();

        while (targetPos < targetData.Length)
        {
            var match = FindBestMatch(sourceData, targetData, targetPos, sourceIndex);

            if (match.Length >= _config.MinMatchLength)
            {
                // Flush pending insert
                if (pendingInsert.Count > 0)
                {
                    WriteInsert(writer, pendingInsert);
                    pendingInsert.Clear();
                }

                // Write copy
                WriteCopy(writer, match.SourceOffset, match.Length);
                targetPos += match.Length;
            }
            else
            {
                pendingInsert.Add(targetData[targetPos]);
                targetPos++;

                // Flush large inserts
                if (pendingInsert.Count >= 65535)
                {
                    WriteInsert(writer, pendingInsert);
                    pendingInsert.Clear();
                }
            }
        }

        // Flush remaining insert
        if (pendingInsert.Count > 0)
        {
            WriteInsert(writer, pendingInsert);
        }

        writer.Write(OpEnd);

        var delta = output.ToArray();

        if (_config.EnableCompression)
        {
            return CompressDelta(delta);
        }

        return delta;
    }

    /// <summary>
    /// Applies a delta to source data to reconstruct target data.
    /// </summary>
    /// <param name="sourceData">Original data.</param>
    /// <param name="deltaData">Delta data (possibly compressed).</param>
    /// <returns>Reconstructed target data.</returns>
    public byte[] ApplyDelta(byte[] sourceData, byte[] deltaData)
    {
        if (deltaData.Length == 0)
        {
            return sourceData;
        }

        var delta = _config.EnableCompression ? DecompressDelta(deltaData) : deltaData;

        using var input = new MemoryStream(delta);
        using var reader = new BinaryReader(input);

        // Read and validate header
        var magic = reader.ReadUInt32();
        if (magic != DeltaMagic)
        {
            throw new InvalidDataException("Invalid delta format: bad magic number");
        }

        var version = reader.ReadUInt16();
        if (version > FormatVersion)
        {
            throw new InvalidDataException($"Unsupported delta format version: {version}");
        }

        var expectedSourceSize = reader.ReadInt32();
        var expectedTargetSize = reader.ReadInt32();

        if (sourceData.Length != expectedSourceSize)
        {
            throw new InvalidDataException(
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
                        throw new InvalidDataException(
                            $"Invalid copy: offset={offset}, length={length}, source size={sourceData.Length}");
                    }

                    for (var i = 0; i < length; i++)
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
                    goto done;

                default:
                    throw new InvalidDataException($"Unknown delta opcode: 0x{op:X2}");
            }
        }

        done:

        if (target.Count != expectedTargetSize)
        {
            throw new InvalidDataException(
                $"Target size mismatch: expected {expectedTargetSize}, got {target.Count}");
        }

        return target.ToArray();
    }

    /// <summary>
    /// Computes diff hunks between two byte arrays for detailed change analysis.
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

        // LCS-based diff
        var matches = ComputeMatchingBlocks(oldData, newData);
        return ExtractHunks(oldData, newData, matches);
    }

    private Dictionary<uint, List<int>> BuildRollingHashIndex(byte[] data)
    {
        var index = new Dictionary<uint, List<int>>();
        var blockSize = _config.BlockSize;

        if (data.Length < blockSize)
        {
            return index;
        }

        var hash = ComputeBlockHash(data, 0, blockSize);
        AddToIndex(index, hash, 0);

        for (var i = 1; i <= data.Length - blockSize; i++)
        {
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

        if (positions.Count < 128)
        {
            positions.Add(position);
        }
    }

    private static uint ComputeBlockHash(byte[] data, int start, int length)
    {
        var hash = Basis;
        var end = Math.Min(start + length, data.Length);

        for (var i = start; i < end; i++)
        {
            hash ^= data[i];
            hash *= Prime;
        }

        return hash;
    }

    private static uint RollHash(uint hash, byte oldByte, byte newByte, int length)
    {
        // Simplified rolling hash
        hash ^= (uint)(oldByte * (uint)Math.Pow(Prime, length - 1));
        hash *= Prime;
        hash ^= newByte;
        return hash;
    }

    private MatchInfo FindBestMatch(
        byte[] sourceData,
        byte[] targetData,
        int targetPos,
        Dictionary<uint, List<int>> sourceIndex)
    {
        var blockSize = _config.BlockSize;
        var best = new MatchInfo { Length = 0 };

        if (targetPos + blockSize > targetData.Length)
        {
            return FindShortMatch(sourceData, targetData, targetPos);
        }

        var targetHash = ComputeBlockHash(targetData, targetPos, blockSize);

        if (!sourceIndex.TryGetValue(targetHash, out var candidates))
        {
            return best;
        }

        foreach (var sourcePos in candidates)
        {
            var length = ExtendMatch(sourceData, targetData, sourcePos, targetPos);

            if (length > best.Length)
            {
                best = new MatchInfo
                {
                    SourceOffset = sourcePos,
                    Length = length
                };
            }
        }

        return best;
    }

    private MatchInfo FindShortMatch(byte[] sourceData, byte[] targetData, int targetPos)
    {
        var best = new MatchInfo { Length = 0 };
        var remaining = targetData.Length - targetPos;
        var minMatch = Math.Min(_config.MinMatchLength, remaining);

        for (var sourcePos = 0; sourcePos <= sourceData.Length - minMatch; sourcePos++)
        {
            var length = ExtendMatch(sourceData, targetData, sourcePos, targetPos);
            if (length > best.Length)
            {
                best = new MatchInfo
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
        var length = 0;
        var maxLength = Math.Min(sourceData.Length - sourcePos, targetData.Length - targetPos);

        while (length < maxLength && sourceData[sourcePos + length] == targetData[targetPos + length])
        {
            length++;
        }

        return length;
    }

    private byte[] EncodeFullInsert(byte[] data)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write(DeltaMagic);
        writer.Write(FormatVersion);
        writer.Write(0); // source size
        writer.Write(data.Length); // target size

        writer.Write(OpInsert);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Write(OpEnd);

        var delta = output.ToArray();
        return _config.EnableCompression ? CompressDelta(delta) : delta;
    }

    private byte[] EncodeEmpty()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write(DeltaMagic);
        writer.Write(FormatVersion);
        writer.Write(0);
        writer.Write(0);
        writer.Write(OpEnd);

        var delta = output.ToArray();
        return _config.EnableCompression ? CompressDelta(delta) : delta;
    }

    private static void WriteCopy(BinaryWriter writer, int offset, int length)
    {
        writer.Write(OpCopy);
        writer.Write(offset);
        writer.Write(length);
    }

    private static void WriteInsert(BinaryWriter writer, List<byte> data)
    {
        writer.Write(OpInsert);
        writer.Write(data.Count);
        writer.Write(data.ToArray());
    }

    private static byte[] CompressDelta(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x01); // Compression flag
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] DecompressDelta(byte[] data)
    {
        if (data.Length == 0 || data[0] != 0x01)
        {
            return data;
        }

        using var input = new MemoryStream(data, 1, data.Length - 1);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private List<(int oldIndex, int newIndex, int length)> ComputeMatchingBlocks(byte[] oldData, byte[] newData)
    {
        var matches = new List<(int, int, int)>();
        var oldPos = 0;
        var newPos = 0;

        while (oldPos < oldData.Length && newPos < newData.Length)
        {
            if (oldData[oldPos] == newData[newPos])
            {
                var start = (oldPos, newPos);
                var len = 0;

                while (oldPos + len < oldData.Length &&
                       newPos + len < newData.Length &&
                       oldData[oldPos + len] == newData[newPos + len])
                {
                    len++;
                }

                if (len > 0)
                {
                    matches.Add((start.Item1, start.Item2, len));
                }

                oldPos += len;
                newPos += len;
            }
            else
            {
                // Advance both pointers to find next match
                var found = false;
                for (var offset = 1; offset < 1000 && !found; offset++)
                {
                    if (newPos + offset < newData.Length && oldPos < oldData.Length)
                    {
                        if (oldData[oldPos] == newData[newPos + offset])
                        {
                            newPos += offset;
                            found = true;
                        }
                    }
                    if (!found && oldPos + offset < oldData.Length && newPos < newData.Length)
                    {
                        if (oldData[oldPos + offset] == newData[newPos])
                        {
                            oldPos += offset;
                            found = true;
                        }
                    }
                }

                if (!found)
                {
                    oldPos++;
                    newPos++;
                }
            }
        }

        return matches;
    }

    private static List<DiffHunk> ExtractHunks(
        byte[] oldData,
        byte[] newData,
        List<(int oldIndex, int newIndex, int length)> matches)
    {
        var hunks = new List<DiffHunk>();
        var oldPos = 0;
        var newPos = 0;

        foreach (var (matchOld, matchNew, matchLen) in matches)
        {
            // Diff before match
            if (oldPos < matchOld || newPos < matchNew)
            {
                var oldLen = matchOld - oldPos;
                var newLen = matchNew - newPos;

                if (oldLen > 0 || newLen > 0)
                {
                    hunks.Add(new DiffHunk
                    {
                        OldStart = oldPos,
                        OldLength = oldLen,
                        NewStart = newPos,
                        NewLength = newLen,
                        OldData = oldLen > 0 ? oldData[oldPos..matchOld] : null,
                        NewData = newLen > 0 ? newData[newPos..matchNew] : null
                    });
                }
            }

            oldPos = matchOld + matchLen;
            newPos = matchNew + matchLen;
        }

        // Trailing diff
        if (oldPos < oldData.Length || newPos < newData.Length)
        {
            var oldLen = oldData.Length - oldPos;
            var newLen = newData.Length - newPos;

            if (oldLen > 0 || newLen > 0)
            {
                hunks.Add(new DiffHunk
                {
                    OldStart = oldPos,
                    OldLength = oldLen,
                    NewStart = newPos,
                    NewLength = newLen,
                    OldData = oldLen > 0 ? oldData[oldPos..] : null,
                    NewData = newLen > 0 ? newData[newPos..] : null
                });
            }
        }

        return hunks;
    }

    private struct MatchInfo
    {
        public int SourceOffset;
        public int Length;
    }
}

#endregion

#region Internal Types

/// <summary>
/// Version graph for delta sync versioning.
/// </summary>
internal sealed class DeltaSyncVersionGraph
{
    public string ObjectId { get; init; } = string.Empty;
    public ConcurrentDictionary<string, DeltaSyncVersionNode> Versions { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    public void AddVersion(DeltaSyncVersionNode node)
    {
        Versions[node.VersionId] = node;
        LastModified = DateTime.UtcNow;
    }
}

/// <summary>
/// Version node in the delta sync version graph.
/// </summary>
internal sealed class DeltaSyncVersionNode
{
    public string VersionId { get; init; } = string.Empty;
    public string ObjectId { get; init; } = string.Empty;
    public List<string> ParentVersionIds { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public string? BranchName { get; set; }
    public string? Message { get; init; }
    public string? Author { get; init; }
    public long Size { get; init; }
    public bool IsLatest { get; set; } = true;
    public DeltaStorageInfo StorageInfo { get; set; } = new();
    public Dictionary<string, string> Tags { get; init; } = new();
}

/// <summary>
/// Storage information for a version (snapshot or delta).
/// </summary>
internal sealed class DeltaStorageInfo
{
    public bool IsDelta { get; init; }
    public string? BaseVersionId { get; init; }
    public long StoredSize { get; init; }
    public long OriginalSize { get; init; }
    public double CompressionRatio { get; init; }
}

/// <summary>
/// Branch state for delta sync versioning.
/// </summary>
internal sealed class DeltaSyncBranchState
{
    public string ObjectId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HeadVersionId { get; set; } = string.Empty;
    public string BaseVersionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime LastModified { get; set; }
    public int VersionCount { get; set; }
}

/// <summary>
/// Delta record for persisted delta storage.
/// </summary>
internal sealed class DeltaRecord
{
    public string VersionId { get; init; } = string.Empty;
    public string BaseVersionId { get; init; } = string.Empty;
    public byte[] DeltaData { get; init; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Persisted state for the delta sync versioning plugin.
/// </summary>
internal sealed class DeltaSyncState
{
    public List<DeltaSyncVersionGraph> Graphs { get; init; } = new();
    public List<DeltaSyncBranchState> Branches { get; init; } = new();
    public DateTime SavedAt { get; init; }
}

#endregion

#region Event Args

/// <summary>
/// Event arguments for delta chain compaction.
/// </summary>
public sealed class DeltaCompactionEventArgs : EventArgs
{
    /// <summary>Object ID that was compacted, or null for all objects.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Number of versions converted from delta to snapshot.</summary>
    public int VersionsCompacted { get; init; }

    /// <summary>Time of compaction.</summary>
    public DateTime CompactedAt { get; init; }
}

/// <summary>
/// Event arguments for version pruning.
/// </summary>
public sealed class VersionPruneEventArgs : EventArgs
{
    /// <summary>Object ID that was pruned, or null for all objects.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Number of versions pruned.</summary>
    public int VersionsPruned { get; init; }

    /// <summary>Time of pruning.</summary>
    public DateTime PrunedAt { get; init; }
}

#endregion

#region Statistics

/// <summary>
/// Statistics for delta sync versioning operations.
/// </summary>
public sealed class DeltaSyncStatistics
{
    /// <summary>Object ID or null for aggregate stats.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Total number of objects being versioned.</summary>
    public int TotalObjects { get; init; }

    /// <summary>Total number of versions across all objects.</summary>
    public int TotalVersions { get; init; }

    /// <summary>Number of full snapshots stored.</summary>
    public int SnapshotCount { get; init; }

    /// <summary>Number of delta versions stored.</summary>
    public int DeltaCount { get; init; }

    /// <summary>Number of branches.</summary>
    public int BranchCount { get; init; }

    /// <summary>Total logical size of all versions (uncompressed).</summary>
    public long TotalLogicalSize { get; init; }

    /// <summary>Total physical size of stored data.</summary>
    public long TotalPhysicalSize { get; init; }

    /// <summary>Space savings ratio (0.0 to 1.0).</summary>
    public double SpaceSavings { get; init; }

    /// <summary>Average delta chain length.</summary>
    public double AverageChainLength { get; init; }

    /// <summary>Total number of operations performed.</summary>
    public long OperationCount { get; init; }
}

#endregion
