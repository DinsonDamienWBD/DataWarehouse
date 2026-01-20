namespace DataWarehouse.SDK.Federation;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Node state in the federation.
/// </summary>
public enum NodeState
{
    /// <summary>Active and participating in federation.</summary>
    Active = 0,
    /// <summary>Dormant - reachable but not actively participating.</summary>
    Dormant = 1,
    /// <summary>Offline - unreachable or shut down.</summary>
    Offline = 2,
    /// <summary>Joining - in process of joining the federation.</summary>
    Joining = 3,
    /// <summary>Leaving - gracefully departing the federation.</summary>
    Leaving = 4,
    /// <summary>Suspended - temporarily excluded from federation.</summary>
    Suspended = 5
}

/// <summary>
/// Capabilities a node can provide in the federation.
/// </summary>
[Flags]
public enum NodeCapabilities
{
    /// <summary>No capabilities.</summary>
    None = 0,
    /// <summary>Can store object data.</summary>
    Storage = 1 << 0,
    /// <summary>Can perform compute operations.</summary>
    Compute = 1 << 1,
    /// <summary>Can act as a gateway to external networks.</summary>
    Gateway = 1 << 2,
    /// <summary>Can route requests to other nodes.</summary>
    Router = 1 << 3,
    /// <summary>Can participate in consensus.</summary>
    Consensus = 1 << 4,
    /// <summary>Can serve as metadata coordinator.</summary>
    Coordinator = 1 << 5,
    /// <summary>Can cache objects for faster access.</summary>
    Cache = 1 << 6,
    /// <summary>Can perform erasure coding.</summary>
    ErasureCoding = 1 << 7,
    /// <summary>Standard storage node.</summary>
    StandardNode = Storage | Router | Consensus,
    /// <summary>Full-featured node.</summary>
    FullNode = Storage | Compute | Router | Consensus | Cache | ErasureCoding
}

/// <summary>
/// Transport protocol for node communication.
/// </summary>
public enum TransportProtocol
{
    /// <summary>Local file system (same machine).</summary>
    File = 0,
    /// <summary>TCP socket connection.</summary>
    Tcp = 1,
    /// <summary>HTTP/HTTPS REST API.</summary>
    Http = 2,
    /// <summary>gRPC over HTTP/2.</summary>
    Grpc = 3,
    /// <summary>WebSocket for bidirectional streaming.</summary>
    WebSocket = 4,
    /// <summary>QUIC for low-latency connections.</summary>
    Quic = 5,
    /// <summary>Custom protocol via plugin.</summary>
    Custom = 99
}

/// <summary>
/// Unique identifier for a node in the federation.
/// Derived from the node's public key.
/// </summary>
public readonly struct NodeId : IEquatable<NodeId>, IComparable<NodeId>
{
    private readonly byte[] _id;

    /// <summary>Gets an empty NodeId.</summary>
    public static NodeId Empty { get; } = new(Array.Empty<byte>());

    /// <summary>Gets the raw ID bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _id ?? Array.Empty<byte>();

    /// <summary>Gets whether this NodeId is empty.</summary>
    public bool IsEmpty => _id == null || _id.Length == 0;

    /// <summary>
    /// Creates a NodeId from raw bytes.
    /// </summary>
    public NodeId(byte[] id)
    {
        _id = id ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Creates a NodeId from a public key.
    /// </summary>
    public static NodeId FromPublicKey(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        return new NodeId(hash);
    }

    /// <summary>
    /// Creates a NodeId from a hex string.
    /// </summary>
    public static NodeId FromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Empty;
        var bytes = Convert.FromHexString(hex);
        return new NodeId(bytes);
    }

    /// <summary>
    /// Generates a random NodeId (for testing).
    /// </summary>
    public static NodeId NewRandom()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return new NodeId(bytes);
    }

    /// <summary>
    /// Returns hex string representation.
    /// </summary>
    public string ToHex() => _id == null ? string.Empty : Convert.ToHexString(_id).ToLowerInvariant();

    /// <summary>
    /// Returns short form (first 8 chars) for display.
    /// </summary>
    public string ToShortString() => ToHex().Length >= 8 ? ToHex()[..8] : ToHex();

    public override string ToString() => ToHex();
    public override int GetHashCode() => _id == null ? 0 : BitConverter.ToInt32(_id.AsSpan()[..4]);
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    public bool Equals(NodeId other)
    {
        if (_id == null && other._id == null) return true;
        if (_id == null || other._id == null) return false;
        return _id.AsSpan().SequenceEqual(other._id);
    }

    public int CompareTo(NodeId other)
    {
        if (_id == null && other._id == null) return 0;
        if (_id == null) return -1;
        if (other._id == null) return 1;
        return _id.AsSpan().SequenceCompareTo(other._id);
    }

    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
    public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;
    public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;
}

/// <summary>
/// Network endpoint for reaching a node.
/// </summary>
public sealed class NodeEndpoint
{
    /// <summary>Transport protocol.</summary>
    public TransportProtocol Protocol { get; set; } = TransportProtocol.Tcp;

    /// <summary>Host address (IP or hostname).</summary>
    public string Address { get; set; } = "localhost";

    /// <summary>Port number.</summary>
    public int Port { get; set; } = 7700;

    /// <summary>Optional path (for HTTP/file protocols).</summary>
    public string? Path { get; set; }

    /// <summary>Whether TLS/encryption is required.</summary>
    public bool RequireTls { get; set; } = true;

    /// <summary>Custom protocol identifier (for Protocol.Custom).</summary>
    public string? CustomProtocol { get; set; }

    /// <summary>Priority for endpoint selection (lower = preferred).</summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Constructs a URI for this endpoint.
    /// </summary>
    public Uri ToUri()
    {
        var scheme = Protocol switch
        {
            TransportProtocol.File => "file",
            TransportProtocol.Tcp => RequireTls ? "tls" : "tcp",
            TransportProtocol.Http => RequireTls ? "https" : "http",
            TransportProtocol.Grpc => RequireTls ? "grpcs" : "grpc",
            TransportProtocol.WebSocket => RequireTls ? "wss" : "ws",
            TransportProtocol.Quic => "quic",
            TransportProtocol.Custom => CustomProtocol ?? "custom",
            _ => "unknown"
        };

        var uriBuilder = new UriBuilder(scheme, Address, Port);
        if (!string.IsNullOrEmpty(Path))
            uriBuilder.Path = Path;

        return uriBuilder.Uri;
    }

    /// <summary>
    /// Parses an endpoint from a URI.
    /// </summary>
    public static NodeEndpoint FromUri(Uri uri)
    {
        var endpoint = new NodeEndpoint
        {
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 7700,
            Path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ? null : uri.AbsolutePath
        };

        endpoint.Protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "file" => TransportProtocol.File,
            "tcp" => TransportProtocol.Tcp,
            "tls" => TransportProtocol.Tcp,
            "http" => TransportProtocol.Http,
            "https" => TransportProtocol.Http,
            "grpc" => TransportProtocol.Grpc,
            "grpcs" => TransportProtocol.Grpc,
            "ws" => TransportProtocol.WebSocket,
            "wss" => TransportProtocol.WebSocket,
            "quic" => TransportProtocol.Quic,
            _ => TransportProtocol.Custom
        };

        endpoint.RequireTls = uri.Scheme.ToLowerInvariant() is "tls" or "https" or "grpcs" or "wss";

        if (endpoint.Protocol == TransportProtocol.Custom)
            endpoint.CustomProtocol = uri.Scheme;

        return endpoint;
    }

    public override string ToString() => ToUri().ToString();
}

/// <summary>
/// Cryptographic key pair for node identity.
/// </summary>
public sealed class NodeKeyPair : IDisposable
{
    private readonly ECDsa _ecdsa;
    private bool _disposed;

    /// <summary>Public key bytes.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Whether this key pair has a private key.</summary>
    public bool HasPrivateKey { get; }

    private NodeKeyPair(ECDsa ecdsa, bool hasPrivateKey)
    {
        _ecdsa = ecdsa;
        PublicKey = ecdsa.ExportSubjectPublicKeyInfo();
        HasPrivateKey = hasPrivateKey;
    }

    /// <summary>
    /// Generates a new key pair.
    /// </summary>
    public static NodeKeyPair Generate()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new NodeKeyPair(ecdsa, true);
    }

    /// <summary>
    /// Imports a key pair from PKCS#8 format.
    /// </summary>
    public static NodeKeyPair ImportPrivateKey(byte[] pkcs8PrivateKey)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return new NodeKeyPair(ecdsa, true);
    }

    /// <summary>
    /// Imports only the public key.
    /// </summary>
    public static NodeKeyPair ImportPublicKey(byte[] publicKey)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return new NodeKeyPair(ecdsa, false);
    }

    /// <summary>
    /// Exports the private key in PKCS#8 format.
    /// </summary>
    public byte[] ExportPrivateKey()
    {
        if (!HasPrivateKey)
            throw new InvalidOperationException("This key pair does not have a private key.");
        return _ecdsa.ExportPkcs8PrivateKey();
    }

    /// <summary>
    /// Signs data with the private key.
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        if (!HasPrivateKey)
            throw new InvalidOperationException("Cannot sign without private key.");
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Verifies a signature against data.
    /// </summary>
    public bool Verify(byte[] data, byte[] signature)
    {
        return _ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ecdsa.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Complete identity for a node in the federation.
/// </summary>
public sealed class NodeIdentity
{
    /// <summary>Unique node identifier (derived from public key).</summary>
    public NodeId Id { get; init; }

    /// <summary>Human-readable node name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Current node state.</summary>
    public NodeState State { get; set; } = NodeState.Offline;

    /// <summary>Node capabilities.</summary>
    public NodeCapabilities Capabilities { get; set; } = NodeCapabilities.StandardNode;

    /// <summary>Endpoints for reaching this node.</summary>
    public List<NodeEndpoint> Endpoints { get; set; } = new();

    /// <summary>Public key bytes.</summary>
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();

    /// <summary>When this node identity was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time this node was seen active.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Version of the node software.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Region/datacenter identifier.</summary>
    public string? Region { get; set; }

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Storage capacity in bytes (if Storage capability).</summary>
    public long StorageCapacityBytes { get; set; }

    /// <summary>Available storage in bytes.</summary>
    public long StorageAvailableBytes { get; set; }

    /// <summary>
    /// Gets the preferred endpoint for communication.
    /// </summary>
    public NodeEndpoint? GetPreferredEndpoint(TransportProtocol? preferredProtocol = null)
    {
        if (Endpoints.Count == 0) return null;

        var candidates = preferredProtocol.HasValue
            ? Endpoints.Where(e => e.Protocol == preferredProtocol.Value)
            : Endpoints;

        return candidates.OrderBy(e => e.Priority).FirstOrDefault() ?? Endpoints[0];
    }

    /// <summary>
    /// Checks if this node has a specific capability.
    /// </summary>
    public bool HasCapability(NodeCapabilities capability) =>
        (Capabilities & capability) == capability;

    /// <summary>
    /// Serializes identity to JSON (excludes private key).
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, NodeIdentityJsonContext.Default.NodeIdentity);

    /// <summary>
    /// Deserializes identity from JSON.
    /// </summary>
    public static NodeIdentity? FromJson(string json) =>
        JsonSerializer.Deserialize(json, NodeIdentityJsonContext.Default.NodeIdentity);
}

/// <summary>
/// Manages node identity including key storage and signing.
/// </summary>
public sealed class NodeIdentityManager : IDisposable
{
    private readonly string _storagePath;
    private NodeKeyPair? _keyPair;
    private NodeIdentity? _localIdentity;
    private bool _disposed;

    /// <summary>
    /// Gets the local node's identity.
    /// </summary>
    public NodeIdentity LocalIdentity =>
        _localIdentity ?? throw new InvalidOperationException("Identity not initialized.");

    /// <summary>
    /// Creates a new NodeIdentityManager.
    /// </summary>
    /// <param name="storagePath">Path to store identity files.</param>
    public NodeIdentityManager(string storagePath)
    {
        _storagePath = storagePath;
    }

    /// <summary>
    /// Initializes or loads the local node identity.
    /// </summary>
    public async Task InitializeAsync(string nodeName, NodeCapabilities capabilities, CancellationToken ct = default)
    {
        var keyPath = Path.Combine(_storagePath, "node.key");
        var identityPath = Path.Combine(_storagePath, "node.identity.json");

        Directory.CreateDirectory(_storagePath);

        if (File.Exists(keyPath) && File.Exists(identityPath))
        {
            // Load existing identity
            var keyBytes = await File.ReadAllBytesAsync(keyPath, ct);
            _keyPair = NodeKeyPair.ImportPrivateKey(keyBytes);

            var identityJson = await File.ReadAllTextAsync(identityPath, ct);
            _localIdentity = NodeIdentity.FromJson(identityJson);

            if (_localIdentity != null)
            {
                _localIdentity.State = NodeState.Active;
                _localIdentity.LastSeenAt = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            // Generate new identity
            _keyPair = NodeKeyPair.Generate();
            var nodeId = NodeId.FromPublicKey(_keyPair.PublicKey);

            _localIdentity = new NodeIdentity
            {
                Id = nodeId,
                Name = nodeName,
                State = NodeState.Active,
                Capabilities = capabilities,
                PublicKey = _keyPair.PublicKey,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            };

            // Save to disk
            await File.WriteAllBytesAsync(keyPath, _keyPair.ExportPrivateKey(), ct);
            await File.WriteAllTextAsync(identityPath, _localIdentity.ToJson(), ct);
        }
    }

    /// <summary>
    /// Adds an endpoint to the local identity.
    /// </summary>
    public void AddEndpoint(NodeEndpoint endpoint)
    {
        if (_localIdentity == null)
            throw new InvalidOperationException("Identity not initialized.");

        _localIdentity.Endpoints.Add(endpoint);
    }

    /// <summary>
    /// Signs data using the local node's private key.
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        if (_keyPair == null)
            throw new InvalidOperationException("Identity not initialized.");
        return _keyPair.Sign(data);
    }

    /// <summary>
    /// Verifies a signature from a remote node.
    /// </summary>
    public bool VerifySignature(NodeIdentity remoteNode, byte[] data, byte[] signature)
    {
        using var keyPair = NodeKeyPair.ImportPublicKey(remoteNode.PublicKey);
        return keyPair.Verify(data, signature);
    }

    /// <summary>
    /// Creates a signed message envelope.
    /// </summary>
    public SignedMessage CreateSignedMessage(byte[] payload, string messageType)
    {
        if (_localIdentity == null || _keyPair == null)
            throw new InvalidOperationException("Identity not initialized.");

        var message = new SignedMessage
        {
            SenderId = _localIdentity.Id,
            MessageType = messageType,
            Payload = payload,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = RandomNumberGenerator.GetBytes(16)
        };

        message.Signature = _keyPair.Sign(message.GetSignableBytes());
        return message;
    }

    /// <summary>
    /// Verifies a signed message from a remote node.
    /// </summary>
    public bool VerifySignedMessage(SignedMessage message, NodeIdentity sender)
    {
        using var keyPair = NodeKeyPair.ImportPublicKey(sender.PublicKey);
        return keyPair.Verify(message.GetSignableBytes(), message.Signature);
    }

    /// <summary>
    /// Exports the local identity for sharing with other nodes.
    /// </summary>
    public NodeIdentity ExportPublicIdentity()
    {
        if (_localIdentity == null)
            throw new InvalidOperationException("Identity not initialized.");

        return new NodeIdentity
        {
            Id = _localIdentity.Id,
            Name = _localIdentity.Name,
            State = _localIdentity.State,
            Capabilities = _localIdentity.Capabilities,
            Endpoints = new List<NodeEndpoint>(_localIdentity.Endpoints),
            PublicKey = _localIdentity.PublicKey,
            CreatedAt = _localIdentity.CreatedAt,
            LastSeenAt = _localIdentity.LastSeenAt,
            Version = _localIdentity.Version,
            Region = _localIdentity.Region,
            Metadata = new Dictionary<string, string>(_localIdentity.Metadata),
            StorageCapacityBytes = _localIdentity.StorageCapacityBytes,
            StorageAvailableBytes = _localIdentity.StorageAvailableBytes
        };
    }

    /// <summary>
    /// Saves the current identity state.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_localIdentity == null) return;

        var identityPath = Path.Combine(_storagePath, "node.identity.json");
        await File.WriteAllTextAsync(identityPath, _localIdentity.ToJson(), ct);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _keyPair?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// A signed message envelope for secure node-to-node communication.
/// </summary>
public sealed class SignedMessage
{
    /// <summary>Sender node ID.</summary>
    public NodeId SenderId { get; set; }

    /// <summary>Message type identifier.</summary>
    public string MessageType { get; set; } = string.Empty;

    /// <summary>Message payload.</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>Unix timestamp in milliseconds.</summary>
    public long Timestamp { get; set; }

    /// <summary>Random nonce for replay protection.</summary>
    public byte[] Nonce { get; set; } = Array.Empty<byte>();

    /// <summary>Signature over the message.</summary>
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets the bytes that should be signed.
    /// </summary>
    public byte[] GetSignableBytes()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(SenderId.Bytes.ToArray());
        writer.Write(MessageType);
        writer.Write(Payload.Length);
        writer.Write(Payload);
        writer.Write(Timestamp);
        writer.Write(Nonce);

        return ms.ToArray();
    }

    /// <summary>
    /// Checks if the message timestamp is within acceptable range.
    /// </summary>
    public bool IsTimestampValid(TimeSpan maxAge)
    {
        var messageTime = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);
        var age = DateTimeOffset.UtcNow - messageTime;
        return age >= TimeSpan.Zero && age <= maxAge;
    }
}

/// <summary>
/// Registry of known nodes in the federation.
/// </summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<NodeId, NodeIdentity> _nodes = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Gets the count of known nodes.
    /// </summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _nodes.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Registers or updates a node.
    /// </summary>
    public void Register(NodeIdentity node)
    {
        _lock.EnterWriteLock();
        try
        {
            _nodes[node.Id] = node;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Gets a node by ID.
    /// </summary>
    public NodeIdentity? GetNode(NodeId id)
    {
        _lock.EnterReadLock();
        try
        {
            return _nodes.TryGetValue(id, out var node) ? node : null;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Removes a node from the registry.
    /// </summary>
    public bool Remove(NodeId id)
    {
        _lock.EnterWriteLock();
        try
        {
            return _nodes.Remove(id);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Gets all nodes matching the given predicate.
    /// </summary>
    public IReadOnlyList<NodeIdentity> GetNodes(Func<NodeIdentity, bool>? predicate = null)
    {
        _lock.EnterReadLock();
        try
        {
            var nodes = predicate == null ? _nodes.Values : _nodes.Values.Where(predicate);
            return nodes.ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Gets all active nodes with a specific capability.
    /// </summary>
    public IReadOnlyList<NodeIdentity> GetNodesWithCapability(NodeCapabilities capability)
    {
        return GetNodes(n => n.State == NodeState.Active && n.HasCapability(capability));
    }

    /// <summary>
    /// Updates a node's state.
    /// </summary>
    public bool UpdateState(NodeId id, NodeState state)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_nodes.TryGetValue(id, out var node))
            {
                node.State = state;
                node.LastSeenAt = DateTimeOffset.UtcNow;
                return true;
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Marks nodes as offline if not seen within the timeout.
    /// </summary>
    public int MarkStaleNodesOffline(TimeSpan timeout)
    {
        var cutoff = DateTimeOffset.UtcNow - timeout;
        var count = 0;

        _lock.EnterWriteLock();
        try
        {
            foreach (var node in _nodes.Values)
            {
                if (node.State == NodeState.Active && node.LastSeenAt < cutoff)
                {
                    node.State = NodeState.Offline;
                    count++;
                }
            }
        }
        finally { _lock.ExitWriteLock(); }

        return count;
    }
}

/// <summary>
/// JSON serialization context for NodeIdentity.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NodeIdentity))]
[JsonSerializable(typeof(List<NodeEndpoint>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class NodeIdentityJsonContext : JsonSerializerContext
{
}
