using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Named snapshot tagging versioning strategy.
/// Creates and manages named tags (release-1.0, backup-2024-01-15) for version snapshots.
/// Thread-safe for concurrent tag operations.
/// </summary>
/// <remarks>
/// Features:
/// - Create named tags for any version
/// - Tag metadata and annotations
/// - Search and filter tags by name/pattern
/// - Tag-based version retrieval
/// - Immutable tags with optional overwrite
/// </remarks>
public sealed class TaggingVersioningStrategy : VersioningStrategyBase
{
    private readonly ConcurrentDictionary<string, TagStore> _stores = new();

    /// <summary>
    /// Metadata for a tag.
    /// </summary>
    public sealed record TagInfo
    {
        /// <summary>Tag name.</summary>
        public required string Name { get; init; }
        /// <summary>Object ID this tag belongs to.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Version ID this tag points to.</summary>
        public required string VersionId { get; init; }
        /// <summary>When the tag was created.</summary>
        public DateTime CreatedAt { get; init; }
        /// <summary>Who created the tag.</summary>
        public string? Author { get; init; }
        /// <summary>Tag message/annotation.</summary>
        public string? Message { get; init; }
        /// <summary>Custom metadata.</summary>
        public Dictionary<string, string>? Metadata { get; init; }
        /// <summary>Whether the tag is deleted.</summary>
        public bool IsDeleted { get; init; }
    }

    /// <summary>
    /// Options for creating a tag.
    /// </summary>
    public sealed class TagCreateOptions
    {
        /// <summary>Tag author.</summary>
        public string? Author { get; init; }
        /// <summary>Tag message/annotation.</summary>
        public string? Message { get; init; }
        /// <summary>Custom metadata.</summary>
        public Dictionary<string, string>? Metadata { get; init; }
        /// <summary>Overwrite existing tag if exists.</summary>
        public bool Overwrite { get; init; }
    }

    /// <summary>
    /// Options for listing tags.
    /// </summary>
    public sealed class TagListOptions
    {
        /// <summary>Pattern to filter tags (supports * wildcard).</summary>
        public string? Pattern { get; init; }
        /// <summary>Maximum results to return.</summary>
        public int MaxResults { get; init; } = 100;
        /// <summary>Include deleted tags.</summary>
        public bool IncludeDeleted { get; init; }
        /// <summary>Sort order (ascending or descending by name).</summary>
        public bool Descending { get; init; }
    }

    /// <summary>
    /// Store for an object's versions and tags.
    /// </summary>
    private sealed class TagStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, VersionEntry> _versions = new();
        private readonly Dictionary<string, TagEntry> _tags = new();
        private long _nextVersionNumber = 1;
        private string? _currentVersionId;

        private sealed class VersionEntry
        {
            public required VersionInfo Info { get; init; }
            public required byte[] Data { get; init; }
        }

        private sealed class TagEntry
        {
            public required TagInfo Info { get; init; }
        }

        /// <summary>
        /// Creates a new version.
        /// </summary>
        public VersionInfo CreateVersion(string objectId, byte[] data, VersionMetadata metadata)
        {
            lock (_lock)
            {
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
                    Metadata = metadata,
                    IsCurrent = true,
                    IsDeleted = false
                };

                if (_currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var prev))
                {
                    _versions[_currentVersionId] = new VersionEntry
                    {
                        Info = prev.Info with { IsCurrent = false },
                        Data = prev.Data
                    };
                }

                _versions[versionId] = new VersionEntry { Info = info, Data = data };
                _currentVersionId = versionId;

                // Auto-create tags from metadata
                if (metadata.Tags != null)
                {
                    foreach (var tag in metadata.Tags)
                    {
                        CreateTag(objectId, tag, versionId, new TagCreateOptions
                        {
                            Author = metadata.Author,
                            Message = metadata.Message,
                            Overwrite = true
                        });
                    }
                }

                return info;
            }
        }

        /// <summary>
        /// Creates a tag pointing to a version.
        /// </summary>
        public TagInfo CreateTag(string objectId, string tagName, string versionId, TagCreateOptions options)
        {
            lock (_lock)
            {
                if (!_versions.ContainsKey(versionId))
                    throw new KeyNotFoundException($"Version '{versionId}' not found.");

                if (_tags.TryGetValue(tagName, out var existing))
                {
                    if (!options.Overwrite)
                        throw new InvalidOperationException($"Tag '{tagName}' already exists. Use Overwrite option to replace.");

                    if (existing.Info.IsDeleted)
                        throw new InvalidOperationException($"Tag '{tagName}' was deleted. Create a new tag instead.");
                }

                var tag = new TagInfo
                {
                    Name = tagName,
                    ObjectId = objectId,
                    VersionId = versionId,
                    CreatedAt = DateTime.UtcNow,
                    Author = options.Author,
                    Message = options.Message,
                    Metadata = options.Metadata,
                    IsDeleted = false
                };

                _tags[tagName] = new TagEntry { Info = tag };
                return tag;
            }
        }

        /// <summary>
        /// Gets a tag by name.
        /// </summary>
        public TagInfo? GetTag(string tagName)
        {
            lock (_lock)
            {
                return _tags.TryGetValue(tagName, out var entry) && !entry.Info.IsDeleted
                    ? entry.Info
                    : null;
            }
        }

        /// <summary>
        /// Gets version data by tag name.
        /// </summary>
        public (byte[]? data, VersionInfo? info) GetVersionByTag(string tagName)
        {
            lock (_lock)
            {
                if (!_tags.TryGetValue(tagName, out var tag) || tag.Info.IsDeleted)
                    return (null, null);

                if (!_versions.TryGetValue(tag.Info.VersionId, out var version))
                    return (null, null);

                return (version.Data, version.Info);
            }
        }

        /// <summary>
        /// Deletes a tag.
        /// </summary>
        public bool DeleteTag(string tagName)
        {
            lock (_lock)
            {
                if (!_tags.TryGetValue(tagName, out var entry) || entry.Info.IsDeleted)
                    return false;

                _tags[tagName] = new TagEntry { Info = entry.Info with { IsDeleted = true } };
                return true;
            }
        }

        /// <summary>
        /// Lists tags with optional filtering.
        /// </summary>
        public IEnumerable<TagInfo> ListTags(TagListOptions options)
        {
            lock (_lock)
            {
                var query = _tags.Values
                    .Select(t => t.Info)
                    .Where(t => options.IncludeDeleted || !t.IsDeleted);

                if (!string.IsNullOrEmpty(options.Pattern))
                {
                    var pattern = options.Pattern.Replace("*", ".*");
                    var regex = new System.Text.RegularExpressions.Regex(
                        $"^{pattern}$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    query = query.Where(t => regex.IsMatch(t.Name));
                }

                query = options.Descending
                    ? query.OrderByDescending(t => t.Name)
                    : query.OrderBy(t => t.Name);

                return query.Take(options.MaxResults).ToList();
            }
        }

        /// <summary>
        /// Gets tags for a specific version.
        /// </summary>
        public IEnumerable<TagInfo> GetTagsForVersion(string versionId)
        {
            lock (_lock)
            {
                return _tags.Values
                    .Where(t => t.Info.VersionId == versionId && !t.Info.IsDeleted)
                    .Select(t => t.Info)
                    .ToList();
            }
        }

        /// <summary>
        /// Gets version data by ID.
        /// </summary>
        public byte[]? GetVersionData(string versionId)
        {
            lock (_lock)
            {
                return _versions.TryGetValue(versionId, out var entry) && !entry.Info.IsDeleted
                    ? entry.Data
                    : null;
            }
        }

        /// <summary>
        /// Lists versions.
        /// </summary>
        public IEnumerable<VersionInfo> ListVersions(VersionListOptions options)
        {
            lock (_lock)
            {
                var query = _versions.Values
                    .Select(v => v.Info)
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
        /// Gets current version.
        /// </summary>
        public VersionInfo? GetCurrentVersion()
        {
            lock (_lock)
            {
                return _currentVersionId != null && _versions.TryGetValue(_currentVersionId, out var entry)
                    ? entry.Info
                    : null;
            }
        }

        /// <summary>
        /// Deletes a version (soft delete).
        /// </summary>
        public bool DeleteVersion(string versionId)
        {
            lock (_lock)
            {
                if (!_versions.TryGetValue(versionId, out var entry) || entry.Info.IsDeleted)
                    return false;

                _versions[versionId] = new VersionEntry
                {
                    Info = entry.Info with { IsDeleted = true, IsCurrent = false },
                    Data = entry.Data
                };

                if (_currentVersionId == versionId)
                {
                    _currentVersionId = _versions.Values
                        .Where(v => !v.Info.IsDeleted)
                        .OrderByDescending(v => v.Info.VersionNumber)
                        .FirstOrDefault()?.Info.VersionId;
                }

                return true;
            }
        }

        /// <summary>
        /// Restores a version.
        /// </summary>
        public VersionInfo? RestoreVersion(string objectId, string versionId)
        {
            lock (_lock)
            {
                if (!_versions.TryGetValue(versionId, out var entry))
                    return null;

                var metadata = new VersionMetadata
                {
                    Author = entry.Info.Metadata?.Author,
                    Message = $"Restored from version {entry.Info.VersionNumber}",
                    ParentVersionId = versionId
                };

                return CreateVersion(objectId, entry.Data, metadata);
            }
        }

        /// <summary>
        /// Gets version info.
        /// </summary>
        public VersionInfo? GetVersionInfo(string versionId)
        {
            lock (_lock)
            {
                return _versions.TryGetValue(versionId, out var entry) ? entry.Info : null;
            }
        }

        /// <summary>
        /// Gets version count.
        /// </summary>
        public long GetVersionCount()
        {
            lock (_lock)
            {
                return _versions.Values.Count(v => !v.Info.IsDeleted);
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
    public override string StrategyId => "versioning.tagging";

    /// <inheritdoc/>
    public override string DisplayName => "Tagging Versioning";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Named snapshot tagging strategy for version management. " +
        "Create named tags like 'release-1.0' or 'backup-2024-01-15' pointing to specific versions. " +
        "Ideal for release management, snapshots, and named checkpoints.";

    /// <inheritdoc/>
    public override string[] Tags => ["versioning", "tagging", "snapshot", "release", "checkpoint"];

    /// <summary>
    /// Creates a tag pointing to a specific version.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="tagName">Name of the tag.</param>
    /// <param name="versionId">Version to tag.</param>
    /// <param name="options">Tag creation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created tag information.</returns>
    public Task<TagInfo> CreateTagAsync(string objectId, string tagName, string versionId, TagCreateOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        return Task.FromResult(store.CreateTag(objectId, tagName, versionId, options ?? new TagCreateOptions()));
    }

    /// <summary>
    /// Gets a tag by name.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="tagName">Tag name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tag information, or null if not found.</returns>
    public Task<TagInfo?> GetTagAsync(string objectId, string tagName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<TagInfo?>(null);

        return Task.FromResult(store.GetTag(tagName));
    }

    /// <summary>
    /// Gets version data by tag name.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="tagName">Tag name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream of version data.</returns>
    public Task<Stream> GetVersionByTagAsync(string objectId, string tagName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            throw new KeyNotFoundException($"No versions exist for object '{objectId}'.");

        var (data, _) = store.GetVersionByTag(tagName);
        if (data == null)
            throw new KeyNotFoundException($"Tag '{tagName}' not found.");

        return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }

    /// <summary>
    /// Deletes a tag.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="tagName">Tag name to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if tag was deleted.</returns>
    public Task<bool> DeleteTagAsync(string objectId, string tagName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.DeleteTag(tagName));
    }

    /// <summary>
    /// Lists tags with optional filtering.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="options">Listing options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of tag information.</returns>
    public Task<IEnumerable<TagInfo>> ListTagsAsync(string objectId, TagListOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IEnumerable<TagInfo>>([]);

        return Task.FromResult(store.ListTags(options ?? new TagListOptions()));
    }

    /// <summary>
    /// Gets all tags for a specific version.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of tag information.</returns>
    public Task<IEnumerable<TagInfo>> GetTagsForVersionAsync(string objectId, string versionId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ct.ThrowIfCancellationRequested();

        if (!_stores.TryGetValue(objectId, out var store))
            return Task.FromResult<IEnumerable<TagInfo>>([]);

        return Task.FromResult(store.GetTagsForVersion(versionId));
    }

    /// <inheritdoc/>
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        data.CopyTo(ms);
        var bytes = ms.ToArray();

        var store = _stores.GetOrAdd(objectId, _ => new TagStore());
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

        return Task.FromResult(new VersionDiff
        {
            FromVersionId = fromVersionId,
            ToVersionId = toVersionId,
            SizeDifference = toData.Length - fromData.Length,
            IsIdentical = fromInfo.ContentHash == toInfo.ContentHash,
            Summary = fromInfo.ContentHash == toInfo.ContentHash
                ? "Versions are identical"
                : $"Size: {fromData.Length} -> {toData.Length} bytes"
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
