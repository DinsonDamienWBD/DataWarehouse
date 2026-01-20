namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;

/// <summary>
/// Location of an object in the federation.
/// </summary>
public sealed class ObjectLocation
{
    /// <summary>Object identifier.</summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>Node that has this object.</summary>
    public NodeId NodeId { get; init; }

    /// <summary>Confidence score (0.0-1.0) that object exists at this location.</summary>
    public float Confidence { get; set; } = 1.0f;

    /// <summary>Whether this is the primary/authoritative location.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Whether this location is verified reachable.</summary>
    public bool IsVerified { get; set; }

    /// <summary>When this location was last verified.</summary>
    public DateTimeOffset LastVerified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Latency to this location in milliseconds.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Estimated bandwidth in bytes/sec.</summary>
    public long BandwidthBps { get; set; }

    /// <summary>Storage tier at this location.</summary>
    public string StorageTier { get; set; } = "Hot";

    /// <summary>Whether this is a cached copy.</summary>
    public bool IsCached { get; set; }
}

/// <summary>
/// Result of object resolution.
/// </summary>
public sealed class ResolutionResult
{
    /// <summary>Whether resolution succeeded.</summary>
    public bool Found { get; init; }

    /// <summary>Object ID that was resolved.</summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>Located copies of the object.</summary>
    public List<ObjectLocation> Locations { get; init; } = new();

    /// <summary>Best location based on metrics.</summary>
    public ObjectLocation? BestLocation => Locations
        .Where(l => l.IsVerified)
        .OrderByDescending(l => l.IsPrimary)
        .ThenBy(l => l.LatencyMs)
        .ThenByDescending(l => l.BandwidthBps)
        .FirstOrDefault() ?? Locations.FirstOrDefault();

    /// <summary>Resolution time in milliseconds.</summary>
    public long ResolutionTimeMs { get; set; }

    /// <summary>Error message if resolution failed.</summary>
    public string? ErrorMessage { get; init; }

    public static ResolutionResult Success(ObjectId objectId, List<ObjectLocation> locations, long timeMs) =>
        new() { Found = true, ObjectId = objectId, Locations = locations, ResolutionTimeMs = timeMs };

    public static ResolutionResult NotFound(ObjectId objectId, long timeMs) =>
        new() { Found = false, ObjectId = objectId, ResolutionTimeMs = timeMs, ErrorMessage = "Object not found" };

    public static ResolutionResult Error(ObjectId objectId, string error) =>
        new() { Found = false, ObjectId = objectId, ErrorMessage = error };
}

/// <summary>
/// Interface for object resolution.
/// </summary>
public interface IObjectResolver
{
    /// <summary>Resolves an object to its locations.</summary>
    Task<ResolutionResult> ResolveAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Resolves multiple objects.</summary>
    Task<Dictionary<ObjectId, ResolutionResult>> ResolveManyAsync(IEnumerable<ObjectId> objectIds, CancellationToken ct = default);

    /// <summary>Registers a new location for an object.</summary>
    Task RegisterLocationAsync(ObjectLocation location, CancellationToken ct = default);

    /// <summary>Removes a location.</summary>
    Task RemoveLocationAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default);
}

/// <summary>
/// Interface for pluggable resolution providers.
/// </summary>
public interface IResolutionProvider
{
    /// <summary>Provider priority (lower = checked first).</summary>
    int Priority { get; }

    /// <summary>Provider name for debugging.</summary>
    string Name { get; }

    /// <summary>Attempts to resolve an object.</summary>
    Task<IReadOnlyList<ObjectLocation>> ResolveAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Registers a location.</summary>
    Task RegisterAsync(ObjectLocation location, CancellationToken ct = default);

    /// <summary>Removes a location.</summary>
    Task RemoveAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default);
}

/// <summary>
/// Local cache resolution provider.
/// </summary>
public sealed class LocalResolutionProvider : IResolutionProvider
{
    private readonly ConcurrentDictionary<ObjectId, List<ObjectLocation>> _cache = new();
    private readonly TimeSpan _ttl;

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public string Name => "LocalCache";

    public LocalResolutionProvider(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ObjectLocation>> ResolveAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(objectId, out var locations))
            return Task.FromResult<IReadOnlyList<ObjectLocation>>(Array.Empty<ObjectLocation>());

        var cutoff = DateTimeOffset.UtcNow - _ttl;
        var valid = locations.Where(l => l.LastVerified > cutoff).ToList();

        return Task.FromResult<IReadOnlyList<ObjectLocation>>(valid);
    }

    /// <inheritdoc />
    public Task RegisterAsync(ObjectLocation location, CancellationToken ct = default)
    {
        _cache.AddOrUpdate(
            location.ObjectId,
            _ => new List<ObjectLocation> { location },
            (_, list) =>
            {
                var existing = list.FindIndex(l => l.NodeId == location.NodeId);
                if (existing >= 0)
                    list[existing] = location;
                else
                    list.Add(location);
                return list;
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(objectId, out var locations))
        {
            locations.RemoveAll(l => l.NodeId == nodeId);
            if (locations.Count == 0)
                _cache.TryRemove(objectId, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears expired entries.
    /// </summary>
    public int Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl;
        var removed = 0;

        foreach (var kvp in _cache)
        {
            kvp.Value.RemoveAll(l => l.LastVerified < cutoff);
            if (kvp.Value.Count == 0)
            {
                _cache.TryRemove(kvp.Key, out _);
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>
/// DHT-based resolution provider using consistent hashing.
/// </summary>
public sealed class DhtResolutionProvider : IResolutionProvider
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly Func<NodeId, IReadOnlyList<ObjectLocation>, Task<IReadOnlyList<ObjectLocation>>>? _queryNode;
    private readonly int _replicationFactor;

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public string Name => "DHT";

    public DhtResolutionProvider(
        NodeRegistry nodeRegistry,
        Func<NodeId, IReadOnlyList<ObjectLocation>, Task<IReadOnlyList<ObjectLocation>>>? queryNode = null,
        int replicationFactor = 3)
    {
        _nodeRegistry = nodeRegistry;
        _queryNode = queryNode;
        _replicationFactor = replicationFactor;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObjectLocation>> ResolveAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (_queryNode == null)
            return Array.Empty<ObjectLocation>();

        // Get responsible nodes using consistent hashing
        var responsibleNodes = GetResponsibleNodes(objectId);

        var results = new List<ObjectLocation>();
        foreach (var nodeId in responsibleNodes)
        {
            try
            {
                var locations = await _queryNode(nodeId, results);
                results.AddRange(locations);
            }
            catch
            {
                // Node unreachable, continue with others
            }
        }

        return results.DistinctBy(l => l.NodeId).ToList();
    }

    private IEnumerable<NodeId> GetResponsibleNodes(ObjectId objectId)
    {
        var activeNodes = _nodeRegistry.GetNodesWithCapability(NodeCapabilities.Storage);
        if (activeNodes.Count == 0)
            return Enumerable.Empty<NodeId>();

        // Simple consistent hashing based on object ID
        var hash = objectId.GetHashCode();
        var sorted = activeNodes.OrderBy(n => n.Id.GetHashCode() ^ hash).ToList();

        return sorted.Take(Math.Min(_replicationFactor, sorted.Count)).Select(n => n.Id);
    }

    /// <inheritdoc />
    public Task RegisterAsync(ObjectLocation location, CancellationToken ct = default)
    {
        // DHT registration would propagate to responsible nodes
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Object resolver with provider chain.
/// </summary>
public sealed class ObjectResolver : IObjectResolver
{
    private readonly List<IResolutionProvider> _providers;
    private readonly NodeId _localNodeId;

    public ObjectResolver(NodeId localNodeId, IEnumerable<IResolutionProvider>? providers = null)
    {
        _localNodeId = localNodeId;
        _providers = providers?.OrderBy(p => p.Priority).ToList() ?? new List<IResolutionProvider>();
    }

    /// <summary>
    /// Adds a resolution provider.
    /// </summary>
    public void AddProvider(IResolutionProvider provider)
    {
        _providers.Add(provider);
        _providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <inheritdoc />
    public async Task<ResolutionResult> ResolveAsync(ObjectId objectId, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allLocations = new List<ObjectLocation>();

        foreach (var provider in _providers)
        {
            try
            {
                var locations = await provider.ResolveAsync(objectId, ct);
                allLocations.AddRange(locations);

                // If we found verified primary location, can stop early
                if (allLocations.Any(l => l.IsPrimary && l.IsVerified))
                    break;
            }
            catch (Exception)
            {
                // Provider failed, continue with next
            }
        }

        sw.Stop();

        // Deduplicate by node
        var deduplicated = allLocations
            .GroupBy(l => l.NodeId)
            .Select(g => g.OrderByDescending(l => l.Confidence).First())
            .ToList();

        if (deduplicated.Count == 0)
            return ResolutionResult.NotFound(objectId, sw.ElapsedMilliseconds);

        return ResolutionResult.Success(objectId, deduplicated, sw.ElapsedMilliseconds);
    }

    /// <inheritdoc />
    public async Task<Dictionary<ObjectId, ResolutionResult>> ResolveManyAsync(
        IEnumerable<ObjectId> objectIds,
        CancellationToken ct = default)
    {
        var results = new Dictionary<ObjectId, ResolutionResult>();
        var tasks = objectIds.Select(async id =>
        {
            var result = await ResolveAsync(id, ct);
            return (id, result);
        });

        foreach (var task in await Task.WhenAll(tasks))
        {
            results[task.id] = task.result;
        }

        return results;
    }

    /// <inheritdoc />
    public async Task RegisterLocationAsync(ObjectLocation location, CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            await provider.RegisterAsync(location, ct);
        }
    }

    /// <inheritdoc />
    public async Task RemoveLocationAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            await provider.RemoveAsync(objectId, nodeId, ct);
        }
    }
}

/// <summary>
/// Path-based resolver that combines VFS and object resolution.
/// </summary>
public sealed class PathResolver
{
    private readonly IVirtualFilesystem _vfs;
    private readonly IObjectResolver _objectResolver;

    public PathResolver(IVirtualFilesystem vfs, IObjectResolver objectResolver)
    {
        _vfs = vfs;
        _objectResolver = objectResolver;
    }

    /// <summary>
    /// Resolves a virtual path to object locations.
    /// </summary>
    public async Task<PathResolutionResult> ResolvePathAsync(VfsPath path, CancellationToken ct = default)
    {
        // Resolve symlinks
        var resolvedPath = await _vfs.ResolveAsync(path, ct: ct);

        // Get the VFS node
        var node = await _vfs.GetNodeAsync(resolvedPath, ct);
        if (node == null)
            return PathResolutionResult.NotFound(path);

        if (!node.ObjectId.HasValue)
            return PathResolutionResult.IsDirectory(path, node);

        // Resolve object locations
        var objectResult = await _objectResolver.ResolveAsync(node.ObjectId.Value, ct);

        return PathResolutionResult.Success(path, node, objectResult);
    }
}

/// <summary>
/// Result of path resolution.
/// </summary>
public sealed class PathResolutionResult
{
    /// <summary>Whether the path was found.</summary>
    public bool Found { get; init; }

    /// <summary>Original requested path.</summary>
    public VfsPath Path { get; init; }

    /// <summary>VFS node at the path.</summary>
    public VfsNode? Node { get; init; }

    /// <summary>Object resolution result (for files).</summary>
    public ResolutionResult? ObjectResult { get; init; }

    /// <summary>Whether this is a directory.</summary>
    public bool IsDirectory => Node?.IsDirectory ?? false;

    /// <summary>Error message if not found.</summary>
    public string? ErrorMessage { get; init; }

    public static PathResolutionResult Success(VfsPath path, VfsNode node, ResolutionResult objectResult) =>
        new() { Found = true, Path = path, Node = node, ObjectResult = objectResult };

    public static PathResolutionResult IsDirectory(VfsPath path, VfsNode node) =>
        new() { Found = true, Path = path, Node = node };

    public static PathResolutionResult NotFound(VfsPath path) =>
        new() { Found = false, Path = path, ErrorMessage = "Path not found" };
}
