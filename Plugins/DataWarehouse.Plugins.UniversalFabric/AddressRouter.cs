using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Storage.Fabric;
using System;
using DataWarehouse.SDK.Utilities;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;

namespace DataWarehouse.Plugins.UniversalFabric;

/// <summary>
/// Resolves <see cref="StorageAddress"/> instances to the correct <see cref="IStorageStrategy"/>
/// by maintaining mappings from buckets, nodes, and clusters to backend IDs and
/// performing pattern-based routing for all address types.
/// </summary>
public sealed class AddressRouter
{
    private readonly BoundedDictionary<string, string> _bucketMappings = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, string> _nodeMappings = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, string> _clusterMappings = new BoundedDictionary<string, string>(1000);
    private volatile string? _defaultBackendId;

    /// <summary>
    /// Resolves a <see cref="StorageAddress"/> to the appropriate <see cref="IStorageStrategy"/>
    /// by pattern matching on the address type and consulting the backend registry.
    /// </summary>
    /// <param name="address">The storage address to resolve.</param>
    /// <param name="registry">The backend registry to look up strategies.</param>
    /// <returns>The resolved storage strategy, or null if no backend matches.</returns>
    public IStorageStrategy? Resolve(StorageAddress address, IBackendRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(registry);

        var backendId = ResolveBackendId(address, registry);
        if (backendId is null) return null;

        return registry.GetStrategy(backendId);
    }

    /// <summary>
    /// Registers a bucket-to-backend mapping so that <see cref="DwBucketAddress"/> instances
    /// for the specified bucket are routed to the given backend.
    /// </summary>
    /// <param name="bucket">The bucket name to map.</param>
    /// <param name="backendId">The backend ID to route to.</param>
    public void MapBucket(string bucket, string backendId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNullOrEmpty(backendId);
        _bucketMappings[bucket] = backendId;
    }

    /// <summary>
    /// Registers a node-to-backend mapping so that <see cref="DwNodeAddress"/> instances
    /// for the specified node are routed to the given backend.
    /// </summary>
    /// <param name="nodeId">The node ID to map.</param>
    /// <param name="backendId">The backend ID to route to.</param>
    public void MapNode(string nodeId, string backendId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNullOrEmpty(backendId);
        _nodeMappings[nodeId] = backendId;
    }

    /// <summary>
    /// Registers a cluster-to-backend mapping so that <see cref="DwClusterAddress"/> instances
    /// for the specified cluster are routed to the given backend.
    /// </summary>
    /// <param name="clusterName">The cluster name to map.</param>
    /// <param name="backendId">The backend ID to route to.</param>
    public void MapCluster(string clusterName, string backendId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(clusterName);
        ArgumentNullException.ThrowIfNullOrEmpty(backendId);
        _clusterMappings[clusterName] = backendId;
    }

    /// <summary>
    /// Sets the default backend used when no explicit mapping exists for the address.
    /// </summary>
    /// <param name="backendId">The backend ID to use as the fallback default.</param>
    public void SetDefaultBackend(string backendId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(backendId);
        _defaultBackendId = backendId;
    }

    /// <summary>
    /// Gets the current default backend ID, if set.
    /// </summary>
    public string? DefaultBackendId => _defaultBackendId;

    /// <summary>
    /// Extracts the routing key (backend ID + object key) from a storage address.
    /// The backend ID identifies which backend handles the address, and the object key
    /// is the key to pass to that backend's storage operations.
    /// </summary>
    /// <param name="address">The storage address to decompose.</param>
    /// <param name="registry">The backend registry for endpoint matching.</param>
    /// <returns>A tuple of (backendId, objectKey). backendId may be null if no backend matches.</returns>
    public (string? backendId, string objectKey) ExtractRoutingKey(StorageAddress address, IBackendRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(registry);

        var backendId = ResolveBackendId(address, registry);
        var objectKey = ExtractObjectKey(address);

        return (backendId, objectKey);
    }

    /// <summary>
    /// Resolves the backend ID for a given address by pattern matching on the address type.
    /// </summary>
    private string? ResolveBackendId(StorageAddress address, IBackendRegistry registry)
    {
        return address switch
        {
            DwBucketAddress bucket =>
                _bucketMappings.TryGetValue(bucket.Bucket, out var bid) ? bid : _defaultBackendId,

            DwNodeAddress node =>
                _nodeMappings.TryGetValue(node.NodeId, out var nid) ? nid : _defaultBackendId,

            DwClusterAddress cluster =>
                _clusterMappings.TryGetValue(cluster.ClusterName, out var cid) ? cid : _defaultBackendId,

            ObjectKeyAddress =>
                _defaultBackendId,

            FilePathAddress =>
                FindLocalFilesystemBackend(registry) ?? _defaultBackendId,

            NetworkEndpointAddress net =>
                FindBackendByEndpoint(net, registry) ?? _defaultBackendId,

            _ => _defaultBackendId
        };
    }

    /// <summary>
    /// Extracts the object key portion from a storage address. This is the key
    /// that will be passed to the backend's StoreAsync/RetrieveAsync methods.
    /// </summary>
    private static string ExtractObjectKey(StorageAddress address)
    {
        return address switch
        {
            DwBucketAddress bucket => bucket.ObjectPath,
            DwNodeAddress node => node.ObjectPath,
            DwClusterAddress cluster => cluster.Key,
            _ => address.ToKey()
        };
    }

    /// <summary>
    /// Finds a backend tagged as "local" or "filesystem" for local file path addresses.
    /// </summary>
    private static string? FindLocalFilesystemBackend(IBackendRegistry registry)
    {
        var locals = registry.FindByTag("local");
        if (locals.Count > 0) return locals[0].BackendId;

        var fs = registry.FindByTag("filesystem");
        if (fs.Count > 0) return fs[0].BackendId;

        return null;
    }

    /// <summary>
    /// Finds a backend whose endpoint matches the network endpoint address.
    /// </summary>
    private static string? FindBackendByEndpoint(NetworkEndpointAddress net, IBackendRegistry registry)
    {
        var endpoint = $"{net.Host}:{net.Port}";
        foreach (var backend in registry.All)
        {
            if (backend.Endpoint is not null &&
                backend.Endpoint.Contains(endpoint, StringComparison.OrdinalIgnoreCase))
            {
                return backend.BackendId;
            }
        }
        return null;
    }

    /// <summary>
    /// Removes a bucket mapping.
    /// </summary>
    public bool UnmapBucket(string bucket) => _bucketMappings.TryRemove(bucket, out _);

    /// <summary>
    /// Removes a node mapping.
    /// </summary>
    public bool UnmapNode(string nodeId) => _nodeMappings.TryRemove(nodeId, out _);

    /// <summary>
    /// Removes a cluster mapping.
    /// </summary>
    public bool UnmapCluster(string clusterName) => _clusterMappings.TryRemove(clusterName, out _);

    /// <summary>
    /// Gets the total number of active route mappings (buckets + nodes + clusters).
    /// </summary>
    public int TotalMappings => _bucketMappings.Count + _nodeMappings.Count + _clusterMappings.Count;
}
