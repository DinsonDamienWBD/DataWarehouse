namespace DataWarehouse.Kernel.Federation;

using DataWarehouse.SDK.Federation;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

/// <summary>
/// Configuration for the Federation Hub.
/// </summary>
public sealed class FederationConfig
{
    /// <summary>Human-readable node name.</summary>
    public string NodeName { get; set; } = Environment.MachineName;

    /// <summary>Path to store node identity and keys.</summary>
    public string IdentityStoragePath { get; set; } = "./federation";

    /// <summary>Node capabilities.</summary>
    public NodeCapabilities Capabilities { get; set; } = NodeCapabilities.StandardNode;

    /// <summary>Endpoints to listen on.</summary>
    public List<NodeEndpoint> Endpoints { get; set; } = new()
    {
        new NodeEndpoint { Protocol = TransportProtocol.Tcp, Address = "0.0.0.0", Port = 7700 }
    };

    /// <summary>Bootstrap nodes for initial discovery.</summary>
    public List<string> BootstrapNodes { get; set; } = new();

    /// <summary>Replication configuration.</summary>
    public ReplicationConfig Replication { get; set; } = new();

    /// <summary>Enable federation features.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Interface for the Federation Hub.
/// </summary>
public interface IFederationHub
{
    /// <summary>Gets the local node identity.</summary>
    NodeIdentity LocalNode { get; }

    /// <summary>Gets the node registry.</summary>
    NodeRegistry NodeRegistry { get; }

    /// <summary>Gets the object store.</summary>
    ContentAddressableObjectStore ObjectStore { get; }

    /// <summary>Gets the virtual filesystem.</summary>
    IVirtualFilesystem VirtualFilesystem { get; }

    /// <summary>Gets the object resolver.</summary>
    IObjectResolver ObjectResolver { get; }

    /// <summary>Gets the transport bus.</summary>
    ITransportBus TransportBus { get; }

    /// <summary>Gets the cluster coordinator.</summary>
    IClusterCoordinator Coordinator { get; }

    /// <summary>Starts the federation hub.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops the federation hub.</summary>
    Task StopAsync();

    /// <summary>Stores an object with replication.</summary>
    Task<FederatedStoreResult> StoreAsync(Stream content, string name, string? contentType = null, CancellationToken ct = default);

    /// <summary>Retrieves an object from the federation.</summary>
    Task<FederatedRetrieveResult> RetrieveAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Retrieves an object by virtual path.</summary>
    Task<FederatedRetrieveResult> RetrieveByPathAsync(string virtualPath, CancellationToken ct = default);

    /// <summary>Shares access to an object with another node.</summary>
    Task<CapabilityToken?> ShareAsync(ObjectId objectId, string targetNodeId, CapabilityPermissions permissions, TimeSpan? expiry = null, CancellationToken ct = default);
}

/// <summary>
/// Result of storing an object in the federation.
/// </summary>
public sealed class FederatedStoreResult
{
    public bool Success { get; init; }
    public ObjectId ObjectId { get; init; }
    public int ReplicaCount { get; init; }
    public List<NodeId> ReplicaNodes { get; init; } = new();
    public string? VirtualPath { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of retrieving an object from the federation.
/// </summary>
public sealed class FederatedRetrieveResult
{
    public bool Success { get; init; }
    public ObjectId ObjectId { get; init; }
    public Stream? Data { get; init; }
    public ObjectManifest? Manifest { get; init; }
    public NodeId SourceNode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Federation Hub - orchestrates all federation components.
/// </summary>
public sealed class FederationHub : IFederationHub, IAsyncDisposable
{
    private readonly FederationConfig _config;
    private readonly NodeIdentityManager _identityManager;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ContentAddressableObjectStore _objectStore;
    private readonly VirtualFilesystem _vfs;
    private readonly ObjectResolver _objectResolver;
    private readonly LocalResolutionProvider _localResolver;
    private readonly TransportBus _transportBus;
    private readonly CapabilityStore _capabilityStore;
    private readonly CapabilityIssuer _capabilityIssuer;
    private readonly CapabilityVerifier _capabilityVerifier;
    private readonly RoutingTable _routingTable;
    private readonly QuorumReplicator _replicator;
    private readonly CrdtMetadataSync _metadataSync;
    private readonly GossipDiscovery _discovery;
    private readonly BullyCoordinator _coordinator;
    private readonly ConcurrentDictionary<string, Func<SignedMessage, NodeConnection, Task>> _messageHandlers = new();
    private bool _started;

    /// <inheritdoc />
    public NodeIdentity LocalNode => _identityManager.LocalIdentity;

    /// <inheritdoc />
    public NodeRegistry NodeRegistry => _nodeRegistry;

    /// <inheritdoc />
    public ContentAddressableObjectStore ObjectStore => _objectStore;

    /// <inheritdoc />
    public IVirtualFilesystem VirtualFilesystem => _vfs;

    /// <inheritdoc />
    public IObjectResolver ObjectResolver => _objectResolver;

    /// <inheritdoc />
    public ITransportBus TransportBus => _transportBus;

    /// <inheritdoc />
    public IClusterCoordinator Coordinator => _coordinator;

    public FederationHub(FederationConfig config, string? storagePath = null)
    {
        _config = config;

        // Initialize core components
        _identityManager = new NodeIdentityManager(config.IdentityStoragePath);
        _nodeRegistry = new NodeRegistry();
        _objectStore = new ContentAddressableObjectStore(storagePath ?? "./objects");

        // These will be properly initialized in StartAsync after identity is ready
        _vfs = null!;
        _objectResolver = null!;
        _localResolver = new LocalResolutionProvider();
        _transportBus = null!;
        _capabilityStore = new CapabilityStore();
        _capabilityIssuer = null!;
        _capabilityVerifier = null!;
        _routingTable = null!;
        _replicator = null!;
        _metadataSync = null!;
        _discovery = null!;
        _coordinator = null!;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;

        // Initialize identity
        await _identityManager.InitializeAsync(_config.NodeName, _config.Capabilities, ct);

        // Add endpoints
        foreach (var endpoint in _config.Endpoints)
        {
            _identityManager.AddEndpoint(endpoint);
        }

        // Register self in registry
        _nodeRegistry.Register(_identityManager.LocalIdentity);

        // Initialize remaining components that need identity
        InitializeComponents();

        // Register message handlers
        RegisterMessageHandlers();

        // Start transport
        await _transportBus.StartAsync(ct);

        // Start discovery
        await _discovery.StartAsync(ct);
        await _discovery.AnnounceAsync(ct);

        // Start coordinator
        await _coordinator.StartAsync(ct);

        // Connect to bootstrap nodes
        await ConnectToBootstrapNodesAsync(ct);

        _started = true;
    }

    private void InitializeComponents()
    {
        var localNodeId = _identityManager.LocalIdentity.Id;

        // VFS
        var vfs = new VirtualFilesystem(localNodeId.ToHex());

        // Transport with drivers
        var transportBus = new TransportBus(_nodeRegistry, _identityManager);
        transportBus.RegisterDriver(new FileTransportDriver(Path.Combine(_config.IdentityStoragePath, "transport")));
        transportBus.RegisterDriver(new TcpTransportDriver());
        transportBus.RegisterDriver(new HttpTransportDriver());

        // Resolution
        var objectResolver = new ObjectResolver(localNodeId);
        objectResolver.AddProvider(_localResolver);
        objectResolver.AddProvider(new DhtResolutionProvider(_nodeRegistry));

        // Capabilities
        var capabilityIssuer = new CapabilityIssuer(_identityManager);
        var capabilityVerifier = new CapabilityVerifier(_nodeRegistry, _capabilityStore);

        // Routing
        var routingTable = new RoutingTable(_nodeRegistry, localNodeId);

        // Replication
        var replicator = new QuorumReplicator(_objectStore, transportBus, _nodeRegistry, _config.Replication);

        // Metadata sync
        var metadataSync = new CrdtMetadataSync(_identityManager, transportBus);

        // Discovery
        var discovery = new GossipDiscovery(_identityManager, _nodeRegistry, transportBus);

        // Coordinator
        var coordinator = new BullyCoordinator(_identityManager, _nodeRegistry, transportBus);

        // Assign to fields using reflection or direct assignment
        SetField(ref _vfs, vfs);
        SetField(ref _transportBus, transportBus);
        SetField(ref _objectResolver, objectResolver);
        SetField(ref _capabilityIssuer, capabilityIssuer);
        SetField(ref _capabilityVerifier, capabilityVerifier);
        SetField(ref _routingTable, routingTable);
        SetField(ref _replicator, replicator);
        SetField(ref _metadataSync, metadataSync);
        SetField(ref _discovery, discovery);
        SetField(ref _coordinator, coordinator);
    }

    private static void SetField<T>(ref T field, T value) where T : class
    {
        field = value;
    }

    private void RegisterMessageHandlers()
    {
        // Object operations
        _transportBus.OnMessage("object.store", HandleObjectStore);
        _transportBus.OnMessage("object.retrieve", HandleObjectRetrieve);
        _transportBus.OnMessage("object.exists", HandleObjectExists);

        // Capability operations
        _transportBus.OnMessage("capability.verify", HandleCapabilityVerify);
        _transportBus.OnMessage("capability.share", HandleCapabilityShare);

        // VFS operations
        _transportBus.OnMessage("vfs.list", HandleVfsList);
        _transportBus.OnMessage("vfs.resolve", HandleVfsResolve);
    }

    private async Task HandleObjectStore(SignedMessage message, NodeConnection connection)
    {
        // Handle incoming object storage request
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ObjectStoreRequest>(message.Payload);
            if (request == null) return;

            using var ms = new MemoryStream(request.Data);
            var result = await _objectStore.StoreAsync(ms, request.Name, request.ContentType);

            var response = new ObjectStoreResponse
            {
                Success = result.Success,
                ObjectId = result.ObjectId.ToHex()
            };

            var responsePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
            await connection.SendAsync(responsePayload);
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleObjectRetrieve(SignedMessage message, NodeConnection connection)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ObjectRetrieveRequest>(message.Payload);
            if (request == null) return;

            var objectId = ObjectId.FromHex(request.ObjectId);
            var result = await _objectStore.RetrieveAsync(objectId);

            if (result.Success && result.Data != null)
            {
                await connection.SendAsync(result.Data);
            }
        }
        catch
        {
            // Log error
        }
    }

    private async Task HandleObjectExists(SignedMessage message, NodeConnection connection)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ObjectExistsRequest>(message.Payload);
            if (request == null) return;

            var objectId = ObjectId.FromHex(request.ObjectId);
            var manifest = await _objectStore.GetManifestAsync(objectId);

            var response = new ObjectExistsResponse { Exists = manifest != null };
            var responsePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
            await connection.SendAsync(responsePayload);
        }
        catch
        {
            // Log error
        }
    }

    private Task HandleCapabilityVerify(SignedMessage message, NodeConnection connection)
    {
        // Handle capability verification request
        return Task.CompletedTask;
    }

    private Task HandleCapabilityShare(SignedMessage message, NodeConnection connection)
    {
        // Handle capability sharing request
        return Task.CompletedTask;
    }

    private async Task HandleVfsList(SignedMessage message, NodeConnection connection)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<VfsListRequest>(message.Payload);
            if (request == null) return;

            var path = VfsPath.Parse(request.Path);
            var nodes = await _vfs.ListAsync(path);

            var response = new VfsListResponse
            {
                Items = nodes.Select(n => new VfsListItem
                {
                    Name = n.Name,
                    IsDirectory = n.IsDirectory,
                    Size = n.SizeBytes,
                    ObjectId = n.ObjectId?.ToHex()
                }).ToList()
            };

            var responsePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
            await connection.SendAsync(responsePayload);
        }
        catch
        {
            // Log error
        }
    }

    private Task HandleVfsResolve(SignedMessage message, NodeConnection connection)
    {
        // Handle VFS path resolution
        return Task.CompletedTask;
    }

    private async Task ConnectToBootstrapNodesAsync(CancellationToken ct)
    {
        foreach (var nodeUri in _config.BootstrapNodes)
        {
            try
            {
                var uri = new Uri(nodeUri);
                var endpoint = NodeEndpoint.FromUri(uri);

                // Create a temporary node identity for the bootstrap
                var tempId = NodeId.NewRandom();
                var connection = await _transportBus.GetConnectionAsync(tempId, ct);

                // Request node list
                var message = _identityManager.CreateSignedMessage(Array.Empty<byte>(), "discovery.request");
                await connection.SendMessageAsync(message, ct);
            }
            catch
            {
                // Bootstrap node unreachable, continue
            }
        }
    }

    /// <inheritdoc />
    public async Task<FederatedStoreResult> StoreAsync(
        Stream content,
        string name,
        string? contentType = null,
        CancellationToken ct = default)
    {
        // Store locally first
        var localResult = await _objectStore.StoreAsync(content, name, contentType, ct);
        if (!localResult.Success)
        {
            return new FederatedStoreResult
            {
                Success = false,
                ErrorMessage = "Failed to store locally"
            };
        }

        var objectId = localResult.ObjectId;

        // Register in VFS
        var vfsPath = new VfsPath($"/{name}");
        await _vfs.CreateFileAsync(vfsPath, objectId, localResult.Manifest?.Size ?? 0, contentType ?? "application/octet-stream", ct);

        // Register location
        await _objectResolver.RegisterLocationAsync(new ObjectLocation
        {
            ObjectId = objectId,
            NodeId = _identityManager.LocalIdentity.Id,
            IsPrimary = true,
            IsVerified = true
        }, ct);

        // Replicate to other nodes
        var storageNodes = _nodeRegistry.GetNodesWithCapability(NodeCapabilities.Storage)
            .Where(n => n.Id != _identityManager.LocalIdentity.Id)
            .Take(_config.Replication.ReplicationFactor - 1)
            .Select(n => n.Id)
            .ToList();

        var replicationResult = await _replicator.ReplicateAsync(objectId, storageNodes, ct);

        // Update metadata
        _metadataSync.UpdateLocal(objectId, manifest =>
        {
            manifest.Name = name;
            manifest.Size = localResult.Manifest?.Size ?? 0;
            manifest.ContentType = contentType ?? "application/octet-stream";
            manifest.OwnerNodeId = _identityManager.LocalIdentity.Id.ToHex();
            manifest.ReplicaNodes = new HashSet<string>(replicationResult.SuccessNodes.Select(n => n.ToHex()))
            {
                _identityManager.LocalIdentity.Id.ToHex()
            };
        });

        return new FederatedStoreResult
        {
            Success = true,
            ObjectId = objectId,
            ReplicaCount = replicationResult.SuccessfulReplicas + 1,
            ReplicaNodes = new List<NodeId>(replicationResult.SuccessNodes) { _identityManager.LocalIdentity.Id },
            VirtualPath = vfsPath.ToString()
        };
    }

    /// <inheritdoc />
    public async Task<FederatedRetrieveResult> RetrieveAsync(ObjectId objectId, CancellationToken ct = default)
    {
        // Try local first
        var localResult = await _objectStore.RetrieveAsync(objectId, ct);
        if (localResult.Success && localResult.Data != null)
        {
            return new FederatedRetrieveResult
            {
                Success = true,
                ObjectId = objectId,
                Data = new MemoryStream(localResult.Data),
                Manifest = localResult.Manifest,
                SourceNode = _identityManager.LocalIdentity.Id
            };
        }

        // Resolve object location
        var resolution = await _objectResolver.ResolveAsync(objectId, ct);
        if (!resolution.Found)
        {
            return new FederatedRetrieveResult
            {
                Success = false,
                ObjectId = objectId,
                ErrorMessage = "Object not found"
            };
        }

        // Fetch from replicas
        var fetchResult = await _replicator.FetchFromReplicasAsync(objectId, ct);
        if (fetchResult.Success && fetchResult.Data != null)
        {
            // Cache locally
            using var ms = new MemoryStream(fetchResult.Data);
            await _objectStore.StoreAsync(ms, "", cancellationToken: ct);

            return new FederatedRetrieveResult
            {
                Success = true,
                ObjectId = objectId,
                Data = new MemoryStream(fetchResult.Data),
                SourceNode = fetchResult.SourceNode
            };
        }

        return new FederatedRetrieveResult
        {
            Success = false,
            ObjectId = objectId,
            ErrorMessage = fetchResult.ErrorMessage ?? "Failed to retrieve from federation"
        };
    }

    /// <inheritdoc />
    public async Task<FederatedRetrieveResult> RetrieveByPathAsync(string virtualPath, CancellationToken ct = default)
    {
        var path = VfsPath.Parse(virtualPath);
        var node = await _vfs.GetNodeAsync(path, ct);

        if (node == null || !node.ObjectId.HasValue)
        {
            return new FederatedRetrieveResult
            {
                Success = false,
                ErrorMessage = "Path not found or not a file"
            };
        }

        return await RetrieveAsync(node.ObjectId.Value, ct);
    }

    /// <inheritdoc />
    public async Task<CapabilityToken?> ShareAsync(
        ObjectId objectId,
        string targetNodeId,
        CapabilityPermissions permissions,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
    {
        var constraints = new CapabilityConstraints
        {
            AllowedNodes = new HashSet<string> { targetNodeId },
            ExpiresAt = expiry.HasValue ? DateTimeOffset.UtcNow + expiry.Value : null
        };

        var token = _capabilityIssuer.Issue(
            CapabilityType.Object,
            objectId.ToHex(),
            permissions,
            targetNodeId,
            constraints);

        // Store token
        await _capabilityStore.StoreTokenAsync(token, ct);

        // Send to target node
        var targetId = NodeId.FromHex(targetNodeId);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(token);
        var message = _identityManager.CreateSignedMessage(payload, "capability.share");

        try
        {
            await _transportBus.SendAsync(targetId, message, ct);
        }
        catch
        {
            // Target unreachable, token still valid when they connect
        }

        return token;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (!_started) return;

        await _coordinator.StopAsync();
        await _discovery.StopAsync();
        await _transportBus.StopAsync();

        await _identityManager.SaveAsync();

        _started = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_coordinator is IAsyncDisposable coordDisposable)
            await coordDisposable.DisposeAsync();

        if (_discovery is IAsyncDisposable discDisposable)
            await discDisposable.DisposeAsync();

        if (_transportBus is IAsyncDisposable transDisposable)
            await transDisposable.DisposeAsync();

        if (_objectStore is IAsyncDisposable storeDisposable)
            await storeDisposable.DisposeAsync();

        _identityManager.Dispose();
    }
}

#region Message DTOs

internal sealed class ObjectStoreRequest
{
    public string Name { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

internal sealed class ObjectStoreResponse
{
    public bool Success { get; set; }
    public string ObjectId { get; set; } = string.Empty;
}

internal sealed class ObjectRetrieveRequest
{
    public string ObjectId { get; set; } = string.Empty;
}

internal sealed class ObjectExistsRequest
{
    public string ObjectId { get; set; } = string.Empty;
}

internal sealed class ObjectExistsResponse
{
    public bool Exists { get; set; }
}

internal sealed class VfsListRequest
{
    public string Path { get; set; } = "/";
}

internal sealed class VfsListResponse
{
    public List<VfsListItem> Items { get; set; } = new();
}

internal sealed class VfsListItem
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string? ObjectId { get; set; }
}

#endregion
