using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Delta-based versioning strategy that stores only differences between versions.
/// Uses binary delta encoding (xdelta3-style) with base version + delta chain.
/// Supports periodic full snapshots to limit delta chain length.
/// </summary>
/// <remarks>
/// Features:
/// - Binary delta encoding for efficient storage
/// - Base version + delta chain architecture
/// - Configurable delta chain length limits
/// - Periodic full snapshots for reconstruction efficiency
/// - Rolling checksum algorithm for block matching
/// - Thread-safe concurrent access
/// </remarks>
public sealed class DeltaVersioningStrategy : VersioningStrategyBase
{
    private const int DefaultMaxDeltaChainLength = 10;
    private const int DefaultBlockSize = 64;

    private readonly BoundedDictionary<string, ObjectDeltaStore> _stores = new BoundedDictionary<string, ObjectDeltaStore>(1000);
    private int _maxDeltaChainLength = DefaultMaxDeltaChainLength;
    private int _blockSize = DefaultBlockSize;

    /// <summary>
    /// Represents a stored version entry (either base or delta).
    /// </summary>
    private sealed class DeltaVersionEntry
    {
        /// <summary>
        /// Version metadata.
        /// </summary>
        public required VersionInfo Info { get; init; }

        /// <summary>
        /// True if this is a base (full) snapshot.
        /// </summary>
        public required bool IsBase { get; init; }

        /// <summary>
        /// Full data if this is a base snapshot.
        /// </summary>
        public byte[]? BaseData { get; init; }

        /// <summary>
        /// Delta data if this is not a base snapshot.
        /// </summary>
        public byte[]? DeltaData { get; init; }

        /// <summary>
        /// Version ID of the parent for delta reconstruction.
        /// </summary>
        public string? ParentVersionId { get; init; }

        /// <summary>
        /// Distance from the nearest base snapshot.
        /// </summary>
        public int ChainDepth { get; init; }
    }

    /// <summary>
    /// Store for a single object's delta versions.
    /// </summary>
    private sealed class ObjectDeltaStore
    {
        private readonly object _versionLock = new();
        private readonly Dictionary<string, DeltaVersionEntry> _versions = new();
        private readonly List<string> _versionOrder = new();
        private long _nextVersionNumber = 1;
        private string? _currentVersionId;

        /// <summary>
        /// Creates a new base (full) version.
        /// </summary>
        public VersionInfo CreateBaseVersion(string objectId, byte[] data, VersionMetadata metadata)
        {
            lock (_versionLock)
            {
                var versionId = GenerateVersionId();
                var versionNumber = _nextVersionNumber++;
                var contentHash = ComputeHashStatic(data);

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

                MarkPreviousAsNotCurrent();

                _versions[versionId] = new DeltaVersionEntry
                {
                    Info = info,
                    IsBase = true,
                    BaseData = data,
                    DeltaData = null,
                    ParentVersionId = null,
                    ChainDepth = 0
                };
                _versionOrder.Add(versionId);
                _currentVersionId = versionId;

                return info;
            }
        }

        /// <summary>
        /// Creates a new delta version from the previous version.
        /// </summary>
        public VersionInfo CreateDeltaVersion(
            string objectId,
            byte[] data,
            byte[] deltaData,
            string parentVersionId,
            int chainDepth,
            VersionMetadata metadata)
        {
            lock (_versionLock)
            {
                var versionId = GenerateVersionId();
                var versionNumber = _nextVersionNumber++;
                var contentHash = ComputeHashStatic(data);

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

                MarkPreviousAsNotCurrent();

                _versions[versionId] = new DeltaVersionEntry
                {
                    Info = info,
                    IsBase = false,
                    BaseData = null,
                    DeltaData = deltaData,
                    ParentVersionId = parentVersionId,
                    ChainDepth = chainDepth
                };
                _versionOrder.Add(versionId);
                _currentVersionId = versionId;

                return info;
            }
        }

        private void MarkPreviousAsNotCurrent()
        {
            if (_currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var prev))
            {
                _versions[_currentVersionId] = new DeltaVersionEntry
                {
                    Info = prev.Info with { IsCurrent = false },
                    IsBase = prev.IsBase,
                    BaseData = prev.BaseData,
                    DeltaData = prev.DeltaData,
                    ParentVersionId = prev.ParentVersionId,
                    ChainDepth = prev.ChainDepth
                };
            }
        }

        /// <summary>
        /// Gets the current version's data and chain info.
        /// </summary>
        public DeltaVersionEntry? GetCurrentEntry()
        {
            lock (_versionLock)
            {
                return _currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var entry)
                    ? entry
                    : null;
            }
        }

        /// <summary>
        /// Gets a version entry by ID.
        /// </summary>
        public DeltaVersionEntry? GetEntry(string versionId)
        {
            lock (_versionLock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry
                    : null;
            }
        }

        /// <summary>
        /// Gets all entries in the delta chain leading to a version.
        /// </summary>
        public List<DeltaVersionEntry> GetDeltaChain(string versionId)
        {
            lock (_versionLock)
            {
                var chain = new List<DeltaVersionEntry>();
                var currentId = versionId;

                while (currentId != null && _versions.TryGetValue(currentId, out var entry))
                {
                    chain.Add(entry);
                    if (entry.IsBase)
                        break;
                    currentId = entry.ParentVersionId;
                }

                chain.Reverse(); // Base first, then deltas in order
                return chain;
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

                _versions[versionId] = new DeltaVersionEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    IsBase = entry.IsBase,
                    BaseData = entry.BaseData,
                    DeltaData = entry.DeltaData,
                    ParentVersionId = entry.ParentVersionId,
                    ChainDepth = entry.ChainDepth
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
                            _versions[id] = new DeltaVersionEntry
                            {
                                Info = v.Info with { IsCurrent = true },
                                IsBase = v.IsBase,
                                BaseData = v.BaseData,
                                DeltaData = v.DeltaData,
                                ParentVersionId = v.ParentVersionId,
                                ChainDepth = v.ChainDepth
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
    public override string StrategyId => "versioning.delta";

    /// <inheritdoc/>
    public override string DisplayName => "Delta Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 30_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Delta-based versioning strategy that stores only differences between versions. " +
        "Uses binary delta encoding with base version + delta chain architecture. " +
        "Periodic full snapshots limit chain length for efficient reconstruction. " +
        "Ideal for incremental changes with minimal storage overhead.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "delta", "diff", "incremental", "xdelta", "storage-efficient"];

    /// <summary>
    /// Gets or sets the maximum delta chain length before creating a new base snapshot.
    /// </summary>
    public int MaxDeltaChainLength
    {
        get => _maxDeltaChainLength;
        set => _maxDeltaChainLength = value > 0 ? value : DefaultMaxDeltaChainLength;
    }

    /// <summary>
    /// Gets or sets the block size for rolling hash matching.
    /// </summary>
    public int BlockSize
    {
        get => _blockSize;
        set => _blockSize = value > 0 ? value : DefaultBlockSize;
    }

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream(65536);
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, _ => new ObjectDeltaStore());
        var currentEntry = store.GetCurrentEntry();

        // If no current version or chain too long, create base
        if (currentEntry == null || currentEntry.ChainDepth >= _maxDeltaChainLength)
        {
            var info = store.CreateBaseVersion(objectId, bytes, metadata);
            return Task.FromResult(info);
        }

        // Reconstruct current data to compute delta
        var currentData = ReconstructVersion(store, currentEntry.Info.VersionId);
        var delta = ComputeDelta(currentData, bytes);

        // If delta is larger than the data itself, create a base instead
        if (delta.Length >= bytes.Length * 0.8)
        {
            var info = store.CreateBaseVersion(objectId, bytes, metadata);
            return Task.FromResult(info);
        }

        var deltaInfo = store.CreateDeltaVersion(
            objectId,
            bytes,
            delta,
            currentEntry.Info.VersionId,
            currentEntry.ChainDepth + 1,
            metadata);

        return Task.FromResult(deltaInfo);
    }

    /// <inheritdoc/>
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var entry = store.GetEntry(versionId)
            ?? throw new KeyNotFoundException($"Version '{versionId}' not found for object '{objectId}'.");

        var data = ReconstructVersion(store, versionId);
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

        var data = ReconstructVersion(store, versionId);

        var restoredMetadata = new VersionMetadata
        {
            Author = entry.Info.Metadata?.Author,
            Message = $"Restored from version {entry.Info.VersionNumber} (delta chain depth: {entry.ChainDepth})",
            ParentVersionId = versionId,
            Properties = entry.Info.Metadata?.Properties
        };

        // Restoration creates a new base to reset the delta chain
        var info = store.CreateBaseVersion(objectId, data, restoredMetadata);
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

        byte[]? deltaBytes = null;
        if (!isIdentical)
        {
            var fromData = ReconstructVersion(store, fromVersionId);
            var toData = ReconstructVersion(store, toVersionId);
            deltaBytes = ComputeDelta(fromData, toData);
        }

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toEntry.Info.SizeBytes - fromEntry.Info.SizeBytes,
            IsIdentical = isIdentical,
            DeltaBytes = deltaBytes,
            Summary = isIdentical
                ? "Versions are identical"
                : $"Delta size: {deltaBytes?.Length ?? 0} bytes (from {fromEntry.ChainDepth} to {toEntry.ChainDepth} chain depth)"
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
    /// Reconstructs a version by applying deltas to the base.
    /// </summary>
    private byte[] ReconstructVersion(ObjectDeltaStore store, string versionId)
    {
        var chain = store.GetDeltaChain(versionId);

        if (chain.Count == 0)
            throw new InvalidOperationException("Empty delta chain.");

        // Start with base
        var baseEntry = chain[0];
        if (!baseEntry.IsBase || baseEntry.BaseData == null)
            throw new InvalidOperationException("First entry in chain must be a base.");

        var currentData = baseEntry.BaseData;

        // Apply deltas in order
        for (int i = 1; i < chain.Count; i++)
        {
            var deltaEntry = chain[i];
            if (deltaEntry.DeltaData == null)
                throw new InvalidOperationException("Delta entry missing delta data.");

            currentData = ApplyDelta(currentData, deltaEntry.DeltaData);
        }

        return currentData;
    }

    /// <summary>
    /// Computes a delta between source and target data using xdelta3-style algorithm.
    /// </summary>
    private byte[] ComputeDelta(byte[] source, byte[] target)
    {
        using var ms = new MemoryStream(65536);
        using var writer = new BinaryWriter(ms);

        // Build index of source blocks using rolling hash
        var sourceIndex = new Dictionary<uint, List<int>>();
        for (int i = 0; i <= source.Length - _blockSize; i++)
        {
            var hash = ComputeRollingHash(source, i, _blockSize);
            if (!sourceIndex.ContainsKey(hash))
                sourceIndex[hash] = [];
            sourceIndex[hash].Add(i);
        }

        // Write header: source length, target length
        writer.Write(source.Length);
        writer.Write(target.Length);

        int targetPos = 0;
        var literalBuffer = new List<byte>();

        while (targetPos < target.Length)
        {
            bool matched = false;

            if (targetPos <= target.Length - _blockSize)
            {
                var hash = ComputeRollingHash(target, targetPos, _blockSize);

                if (sourceIndex.TryGetValue(hash, out var positions))
                {
                    foreach (var sourcePos in positions)
                    {
                        // Verify match and extend
                        int matchLen = 0;
                        while (targetPos + matchLen < target.Length &&
                               sourcePos + matchLen < source.Length &&
                               target[targetPos + matchLen] == source[sourcePos + matchLen])
                        {
                            matchLen++;
                        }

                        if (matchLen >= _blockSize)
                        {
                            // Flush literal buffer first
                            if (literalBuffer.Count > 0)
                            {
                                writer.Write((byte)0); // Literal marker
                                writer.Write(literalBuffer.Count);
                                writer.Write(literalBuffer.ToArray());
                                literalBuffer.Clear();
                            }

                            // Write copy instruction
                            writer.Write((byte)1); // Copy marker
                            writer.Write(sourcePos);
                            writer.Write(matchLen);

                            targetPos += matchLen;
                            matched = true;
                            break;
                        }
                    }
                }
            }

            if (!matched)
            {
                literalBuffer.Add(target[targetPos]);
                targetPos++;
            }
        }

        // Flush remaining literals
        if (literalBuffer.Count > 0)
        {
            writer.Write((byte)0); // Literal marker
            writer.Write(literalBuffer.Count);
            writer.Write(literalBuffer.ToArray());
        }

        // End marker
        writer.Write((byte)255);

        return ms.ToArray();
    }

    /// <summary>
    /// Applies a delta to source data to produce target data.
    /// </summary>
    private byte[] ApplyDelta(byte[] source, byte[] delta)
    {
        using var ms = new MemoryStream(delta);
        using var reader = new BinaryReader(ms);

        var expectedSourceLength = reader.ReadInt32();
        var targetLength = reader.ReadInt32();

        if (source.Length != expectedSourceLength)
            throw new InvalidOperationException("Source length mismatch.");

        using var output = new MemoryStream(targetLength);

        while (true)
        {
            var marker = reader.ReadByte();

            if (marker == 255) // End marker
                break;

            if (marker == 0) // Literal
            {
                var length = reader.ReadInt32();
                var data = reader.ReadBytes(length);
                output.Write(data, 0, data.Length);
            }
            else if (marker == 1) // Copy
            {
                var sourcePos = reader.ReadInt32();
                var copyLen = reader.ReadInt32();
                output.Write(source, sourcePos, copyLen);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Computes a rolling hash for a block of data.
    /// Uses Rabin-Karp style polynomial hashing.
    /// </summary>
    private static uint ComputeRollingHash(byte[] data, int offset, int length)
    {
        const uint prime = 31;
        uint hash = 0;
        uint primePower = 1;

        for (int i = 0; i < length && offset + i < data.Length; i++)
        {
            hash += data[offset + i] * primePower;
            primePower *= prime;
        }

        return hash;
    }
}
