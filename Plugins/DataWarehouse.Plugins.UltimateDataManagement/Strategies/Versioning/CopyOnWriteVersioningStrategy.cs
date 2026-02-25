using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Copy-on-Write versioning strategy with efficient storage using shared data blocks.
/// Only stores changed blocks/segments, with reference counting for deduplication.
/// Provides instant snapshot creation with space-efficient versioning.
/// </summary>
/// <remarks>
/// Features:
/// - Copy-on-write semantics for efficient storage
/// - Block-level deduplication with reference counting
/// - Instant snapshot creation (O(1) time complexity)
/// - Garbage collection for orphaned blocks
/// - Content-addressable storage using block hashes
/// - Thread-safe concurrent access
/// </remarks>
public sealed class CopyOnWriteVersioningStrategy : VersioningStrategyBase
{
    private const int DefaultBlockSize = 4096; // 4KB blocks

    private readonly BoundedDictionary<string, ObjectCoWStore> _stores = new BoundedDictionary<string, ObjectCoWStore>(1000);
    private readonly BoundedDictionary<string, SharedBlock> _blockPool = new BoundedDictionary<string, SharedBlock>(1000);
    private readonly object _gcLock = new();
    private int _blockSize = DefaultBlockSize;

    /// <summary>
    /// Represents a shared data block with reference counting.
    /// </summary>
    private sealed class SharedBlock
    {
        private int _referenceCount;
        private readonly object _refLock = new();

        /// <summary>
        /// Content-addressable hash of the block data.
        /// </summary>
        public required string BlockHash { get; init; }

        /// <summary>
        /// The actual block data.
        /// </summary>
        public required byte[] Data { get; init; }

        /// <summary>
        /// Current reference count.
        /// </summary>
        public int ReferenceCount
        {
            get { lock (_refLock) return _referenceCount; }
        }

        /// <summary>
        /// Increments the reference count.
        /// </summary>
        public void AddReference()
        {
            lock (_refLock) _referenceCount++;
        }

        /// <summary>
        /// Decrements the reference count.
        /// </summary>
        /// <returns>True if the block is now orphaned (count = 0).</returns>
        public bool RemoveReference()
        {
            lock (_refLock)
            {
                _referenceCount--;
                return _referenceCount <= 0;
            }
        }
    }

    /// <summary>
    /// Represents a version as a list of block references.
    /// </summary>
    private sealed class CoWVersionEntry
    {
        /// <summary>
        /// Version metadata.
        /// </summary>
        public required VersionInfo Info { get; init; }

        /// <summary>
        /// Ordered list of block hashes that compose this version.
        /// </summary>
        public required List<string> BlockHashes { get; init; }

        /// <summary>
        /// Total size of the version data.
        /// </summary>
        public required long TotalSize { get; init; }
    }

    /// <summary>
    /// Store for a single object's CoW versions.
    /// </summary>
    private sealed class ObjectCoWStore
    {
        private readonly object _versionLock = new();
        private readonly Dictionary<string, CoWVersionEntry> _versions = new();
        private readonly List<string> _versionOrder = new();
        private long _nextVersionNumber = 1;
        private string? _currentVersionId;

        /// <summary>
        /// Creates a new version from block hashes.
        /// </summary>
        public (VersionInfo Info, List<string> OldBlockHashes) CreateVersion(
            string objectId,
            List<string> blockHashes,
            long totalSize,
            string contentHash,
            VersionMetadata metadata)
        {
            lock (_versionLock)
            {
                var versionId = GenerateVersionId();
                var versionNumber = _nextVersionNumber++;

                var info = new VersionInfo
                {
                    VersionId = versionId,
                    ObjectId = objectId,
                    VersionNumber = versionNumber,
                    ContentHash = contentHash,
                    SizeBytes = totalSize,
                    CreatedAt = DateTime.UtcNow,
                    Metadata = metadata,
                    IsCurrent = true,
                    IsDeleted = false
                };

                List<string> oldBlockHashes = [];

                // Mark previous current as not current
                if (_currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var prev))
                {
                    _versions[_currentVersionId] = new CoWVersionEntry
                    {
                        Info = prev.Info with { IsCurrent = false },
                        BlockHashes = prev.BlockHashes,
                        TotalSize = prev.TotalSize
                    };
                }

                _versions[versionId] = new CoWVersionEntry
                {
                    Info = info,
                    BlockHashes = blockHashes,
                    TotalSize = totalSize
                };
                _versionOrder.Add(versionId);
                _currentVersionId = versionId;

                return (info, oldBlockHashes);
            }
        }

        /// <summary>
        /// Gets block hashes for a version.
        /// </summary>
        public List<string>? GetVersionBlockHashes(string versionId)
        {
            lock (_versionLock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry.BlockHashes.ToList()
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
        /// <returns>Block hashes that may need garbage collection.</returns>
        public (bool Success, List<string> BlockHashes) DeleteVersion(string versionId)
        {
            lock (_versionLock)
            {
                if (!_versions.TryGetValue(versionId, out var entry) || entry.Info.IsDeleted)
                    return (false, []);

                var blockHashes = entry.BlockHashes.ToList();

                _versions[versionId] = new CoWVersionEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    BlockHashes = entry.BlockHashes,
                    TotalSize = entry.TotalSize
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
                            _versions[id] = new CoWVersionEntry
                            {
                                Info = _versions[id].Info with { IsCurrent = true },
                                BlockHashes = _versions[id].BlockHashes,
                                TotalSize = _versions[id].TotalSize
                            };
                            break;
                        }
                    }
                }

                return (true, blockHashes);
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
        /// Gets version info and block hashes for restoration.
        /// </summary>
        public (VersionInfo? Info, List<string>? BlockHashes) GetVersionForRestore(string versionId)
        {
            lock (_versionLock)
            {
                if (!_versions.TryGetValue(versionId, out var entry))
                    return (null, null);

                return (entry.Info, entry.BlockHashes.ToList());
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

        /// <summary>
        /// Gets all active block hashes across all non-deleted versions.
        /// </summary>
        public HashSet<string> GetAllActiveBlockHashes()
        {
            lock (_versionLock)
            {
                var hashes = new HashSet<string>();
                foreach (var version in _versions.Values)
                {
                    if (!version.Info.IsDeleted)
                    {
                        foreach (var hash in version.BlockHashes)
                            hashes.Add(hash);
                    }
                }
                return hashes;
            }
        }

        private static string GenerateVersionId() => Guid.NewGuid().ToString("N")[..12];
    }

    /// <inheritdoc/>
    public override string StrategyId => "versioning.copy-on-write";

    /// <inheritdoc/>
    public override string DisplayName => "Copy-on-Write Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Copy-on-Write versioning strategy with efficient block-level storage. " +
        "Only changed blocks are stored, with reference counting for shared data. " +
        "Provides instant snapshot creation with minimal storage overhead. " +
        "Ideal for large files with localized changes.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "cow", "copy-on-write", "deduplication", "snapshots", "blocks"];

    /// <summary>
    /// Gets or sets the block size for chunking data. Default is 4KB.
    /// </summary>
    public int BlockSize
    {
        get => _blockSize;
        set => _blockSize = value > 0 ? value : DefaultBlockSize;
    }

    /// <summary>
    /// Gets the number of unique blocks in the block pool.
    /// </summary>
    public int UniqueBlockCount => _blockPool.Count;

    /// <summary>
    /// Gets the total storage used by unique blocks.
    /// </summary>
    public long TotalBlockPoolBytes => _blockPool.Values.Sum(b => (long)b.Data.Length);

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream(65536);
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        // Split into blocks
        var blockHashes = new List<string>();
        var position = 0;

        while (position < bytes.Length)
        {
            var blockLength = Math.Min(_blockSize, bytes.Length - position);
            var blockData = new byte[blockLength];
            Array.Copy(bytes, position, blockData, 0, blockLength);

            var blockHash = ComputeBlockHash(blockData);

            // Add to block pool with reference counting (CoW deduplication)
            var block = _blockPool.GetOrAdd(blockHash, hash => new SharedBlock
            {
                BlockHash = hash,
                Data = blockData
            });
            block.AddReference();

            blockHashes.Add(blockHash);
            position += blockLength;
        }

        var contentHash = ComputeHash(bytes);
        var store = _stores.GetOrAdd(objectId, _ => new ObjectCoWStore());
        var (info, _) = store.CreateVersion(objectId, blockHashes, bytes.Length, contentHash, metadata);

        return Task.FromResult(info);
    }

    /// <inheritdoc/>
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var blockHashes = store.GetVersionBlockHashes(versionId)
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'.");

        // Reconstruct data from blocks
        using var ms = new MemoryStream(65536);
        foreach (var hash in blockHashes)
        {
            if (!_blockPool.TryGetValue(hash, out var block))
                throw new InvalidOperationException($"Block '{hash}' not found in pool.");

            ms.Write(block.Data, 0, block.Data.Length);
        }

        return Task.FromResult<Stream>(new MemoryStream(ms.ToArray(), writable: false));
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

        var (success, blockHashes) = store.DeleteVersion(versionId);

        if (success && blockHashes.Count > 0)
        {
            // Decrement reference counts
            foreach (var hash in blockHashes)
            {
                if (_blockPool.TryGetValue(hash, out var block))
                {
                    block.RemoveReference();
                }
            }
        }

        return Task.FromResult(success);
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

        var (info, blockHashes) = store.GetVersionForRestore(versionId);

        if (info == null || blockHashes == null)
            throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'.");

        // Increment references for restored blocks
        foreach (var hash in blockHashes)
        {
            if (_blockPool.TryGetValue(hash, out var block))
                block.AddReference();
        }

        var restoredMetadata = new VersionMetadata
        {
            Author = info.Metadata?.Author,
            Message = $"Restored from version {info.VersionNumber} (CoW snapshot)",
            ParentVersionId = versionId,
            Properties = info.Metadata?.Properties
        };

        var (restoredInfo, _) = store.CreateVersion(
            objectId,
            blockHashes,
            info.SizeBytes,
            info.ContentHash,
            restoredMetadata);

        return Task.FromResult(restoredInfo);
    }

    /// <inheritdoc/>
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var fromHashes = store.GetVersionBlockHashes(fromVersionId)
            ?? throw new KeyNotFoundException($"Version '{fromVersionId}' not found.");
        var toHashes = store.GetVersionBlockHashes(toVersionId)
            ?? throw new KeyNotFoundException($"Version '{toVersionId}' not found.");

        var fromInfo = store.GetVersionInfo(fromVersionId)!;
        var toInfo = store.GetVersionInfo(toVersionId)!;

        var isIdentical = fromInfo.ContentHash == toInfo.ContentHash;

        // Calculate block-level changes
        var fromSet = new HashSet<string>(fromHashes);
        var toSet = new HashSet<string>(toHashes);
        var addedBlocks = toSet.Except(fromSet).Count();
        var removedBlocks = fromSet.Except(toSet).Count();
        var sharedBlocks = fromSet.Intersect(toSet).Count();

        var summary = isIdentical
            ? "Versions are identical"
            : $"Block changes: +{addedBlocks} -{removedBlocks}, {sharedBlocks} shared blocks";

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toInfo.SizeBytes - fromInfo.SizeBytes,
            IsIdentical = isIdentical,
            DeltaBytes = null, // CoW uses block references, not byte deltas
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
    /// Runs garbage collection to remove orphaned blocks with zero references.
    /// </summary>
    /// <returns>Number of blocks removed.</returns>
    public int RunGarbageCollection()
    {
        lock (_gcLock)
        {
            // Collect all active block hashes across all stores
            var activeHashes = new HashSet<string>();
            foreach (var store in _stores.Values)
            {
                foreach (var hash in store.GetAllActiveBlockHashes())
                    activeHashes.Add(hash);
            }

            // Remove orphaned blocks
            var toRemove = _blockPool.Keys
                .Where(hash => !activeHashes.Contains(hash) || _blockPool[hash].ReferenceCount <= 0)
                .ToList();

            foreach (var hash in toRemove)
                _blockPool.TryRemove(hash, out _);

            return toRemove.Count;
        }
    }

    /// <summary>
    /// Computes a SHA-256 hash for a data block.
    /// </summary>
    private static string ComputeBlockHash(byte[] blockData)
    {
        var hash = SHA256.HashData(blockData);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
