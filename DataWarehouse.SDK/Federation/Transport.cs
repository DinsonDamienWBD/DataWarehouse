namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Buffers;

/// <summary>
/// Connection state for a node.
/// </summary>
public enum ConnectionState
{
    /// <summary>Not connected.</summary>
    Disconnected = 0,
    /// <summary>Connection in progress.</summary>
    Connecting = 1,
    /// <summary>Connected and ready.</summary>
    Connected = 2,
    /// <summary>Connection failed.</summary>
    Failed = 3,
    /// <summary>Gracefully closing.</summary>
    Closing = 4
}

/// <summary>
/// A connection to a remote node.
/// </summary>
public abstract class NodeConnection : IAsyncDisposable
{
    /// <summary>Remote node ID.</summary>
    public NodeId RemoteNodeId { get; init; }

    /// <summary>Endpoint used for connection.</summary>
    public NodeEndpoint Endpoint { get; init; } = new();

    /// <summary>Current connection state.</summary>
    public ConnectionState State { get; protected set; } = ConnectionState.Disconnected;

    /// <summary>When connection was established.</summary>
    public DateTimeOffset? ConnectedAt { get; protected set; }

    /// <summary>Last activity time.</summary>
    public DateTimeOffset LastActivityAt { get; protected set; } = DateTimeOffset.UtcNow;

    /// <summary>Bytes sent over this connection.</summary>
    public long BytesSent { get; protected set; }

    /// <summary>Bytes received over this connection.</summary>
    public long BytesReceived { get; protected set; }

    /// <summary>Connect to the remote node.</summary>
    public abstract Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Send data to the remote node.</summary>
    public abstract Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Receive data from the remote node.</summary>
    public abstract Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Send a complete message.</summary>
    public abstract Task SendMessageAsync(SignedMessage message, CancellationToken ct = default);

    /// <summary>Receive a complete message.</summary>
    public abstract Task<SignedMessage?> ReceiveMessageAsync(CancellationToken ct = default);

    /// <summary>Close the connection.</summary>
    public abstract Task CloseAsync(CancellationToken ct = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    protected void UpdateActivity()
    {
        LastActivityAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Interface for transport drivers.
/// </summary>
public interface ITransportDriver
{
    /// <summary>Protocol this driver handles.</summary>
    TransportProtocol Protocol { get; }

    /// <summary>Driver name.</summary>
    string Name { get; }

    /// <summary>Creates a connection to a node.</summary>
    Task<NodeConnection> ConnectAsync(NodeEndpoint endpoint, NodeId remoteNodeId, CancellationToken ct = default);

    /// <summary>Starts listening for incoming connections.</summary>
    Task StartListeningAsync(NodeEndpoint localEndpoint, Func<NodeConnection, Task> connectionHandler, CancellationToken ct = default);

    /// <summary>Stops listening.</summary>
    Task StopListeningAsync();
}

/// <summary>
/// File-based transport driver for local/USB scenarios.
/// </summary>
public sealed class FileTransportDriver : ITransportDriver
{
    private readonly string _basePath;
    private FileSystemWatcher? _watcher;
    private Func<NodeConnection, Task>? _connectionHandler;

    /// <inheritdoc />
    public TransportProtocol Protocol => TransportProtocol.File;

    /// <inheritdoc />
    public string Name => "FileTransport";

    public FileTransportDriver(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(_basePath);
    }

    /// <inheritdoc />
    public Task<NodeConnection> ConnectAsync(NodeEndpoint endpoint, NodeId remoteNodeId, CancellationToken ct = default)
    {
        var path = endpoint.Path ?? Path.Combine(_basePath, remoteNodeId.ToShortString());
        var connection = new FileNodeConnection(remoteNodeId, endpoint, path);
        return Task.FromResult<NodeConnection>(connection);
    }

    /// <inheritdoc />
    public Task StartListeningAsync(NodeEndpoint localEndpoint, Func<NodeConnection, Task> connectionHandler, CancellationToken ct = default)
    {
        _connectionHandler = connectionHandler;
        var inboxPath = Path.Combine(_basePath, "inbox");
        Directory.CreateDirectory(inboxPath);

        _watcher = new FileSystemWatcher(inboxPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            Filter = "*.msg"
        };

        _watcher.Created += async (s, e) =>
        {
            try
            {
                // Parse sender from filename
                var filename = Path.GetFileNameWithoutExtension(e.Name);
                var parts = filename?.Split('_');
                if (parts?.Length >= 2)
                {
                    var senderNodeId = NodeId.FromHex(parts[0]);
                    var endpoint = new NodeEndpoint { Protocol = TransportProtocol.File, Path = Path.GetDirectoryName(e.FullPath) };
                    var connection = new FileNodeConnection(senderNodeId, endpoint, Path.GetDirectoryName(e.FullPath)!);
                    await _connectionHandler!(connection);
                }
            }
            catch { /* Ignore malformed messages */ }
        };

        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopListeningAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// File-based node connection.
/// </summary>
public sealed class FileNodeConnection : NodeConnection
{
    private readonly string _basePath;
    private readonly string _outboxPath;
    private readonly string _inboxPath;

    public FileNodeConnection(NodeId remoteNodeId, NodeEndpoint endpoint, string basePath)
    {
        RemoteNodeId = remoteNodeId;
        Endpoint = endpoint;
        _basePath = basePath;
        _outboxPath = Path.Combine(basePath, "outbox");
        _inboxPath = Path.Combine(basePath, "inbox");
    }

    /// <inheritdoc />
    public override Task ConnectAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_outboxPath);
        Directory.CreateDirectory(_inboxPath);
        State = ConnectionState.Connected;
        ConnectedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var filename = $"{Guid.NewGuid():N}.dat";
        var path = Path.Combine(_outboxPath, filename);
        await File.WriteAllBytesAsync(path, data.ToArray(), ct);
        BytesSent += data.Length;
        UpdateActivity();
        return data.Length;
    }

    /// <inheritdoc />
    public override async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_inboxPath, "*.dat").OrderBy(f => File.GetCreationTime(f)).ToArray();
        if (files.Length == 0)
            return 0;

        var data = await File.ReadAllBytesAsync(files[0], ct);
        File.Delete(files[0]);

        var length = Math.Min(data.Length, buffer.Length);
        data.AsMemory(0, length).CopyTo(buffer);
        BytesReceived += length;
        UpdateActivity();
        return length;
    }

    /// <inheritdoc />
    public override async Task SendMessageAsync(SignedMessage message, CancellationToken ct = default)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var filename = $"{message.SenderId.ToShortString()}_{message.Timestamp}.msg";
        var path = Path.Combine(_outboxPath, filename);
        await File.WriteAllTextAsync(path, json, ct);
        BytesSent += json.Length;
        UpdateActivity();
    }

    /// <inheritdoc />
    public override async Task<SignedMessage?> ReceiveMessageAsync(CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_inboxPath, "*.msg").OrderBy(f => File.GetCreationTime(f)).ToArray();
        if (files.Length == 0)
            return null;

        var json = await File.ReadAllTextAsync(files[0], ct);
        File.Delete(files[0]);
        BytesReceived += json.Length;
        UpdateActivity();

        return System.Text.Json.JsonSerializer.Deserialize<SignedMessage>(json);
    }

    /// <inheritdoc />
    public override Task CloseAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// TCP transport driver for LAN/P2P connections.
/// </summary>
public sealed class TcpTransportDriver : ITransportDriver, IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptTask;

    /// <inheritdoc />
    public TransportProtocol Protocol => TransportProtocol.Tcp;

    /// <inheritdoc />
    public string Name => "TcpTransport";

    /// <inheritdoc />
    public async Task<NodeConnection> ConnectAsync(NodeEndpoint endpoint, NodeId remoteNodeId, CancellationToken ct = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(endpoint.Address, endpoint.Port, ct);
        var connection = new TcpNodeConnection(remoteNodeId, endpoint, client);
        connection.State = ConnectionState.Connected;
        return connection;
    }

    /// <inheritdoc />
    public Task StartListeningAsync(NodeEndpoint localEndpoint, Func<NodeConnection, Task> connectionHandler, CancellationToken ct = default)
    {
        var address = localEndpoint.Address == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(localEndpoint.Address);

        _listener = new TcpListener(address, localEndpoint.Port);
        _listener.Start();

        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptTask = AcceptLoopAsync(connectionHandler, _listenerCts.Token);

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(Func<NodeConnection, Task> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                var endpoint = new NodeEndpoint
                {
                    Protocol = TransportProtocol.Tcp,
                    Address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(),
                    Port = ((IPEndPoint)client.Client.RemoteEndPoint!).Port
                };

                // Node ID will be established during handshake
                var connection = new TcpNodeConnection(NodeId.Empty, endpoint, client);
                _ = handler(connection); // Fire and forget
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue accepting
            }
        }
    }

    /// <inheritdoc />
    public async Task StopListeningAsync()
    {
        _listenerCts?.Cancel();
        if (_acceptTask != null)
        {
            try { await _acceptTask; }
            catch { /* Ignore cancellation */ }
        }
        _listener?.Stop();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopListeningAsync();
        _listenerCts?.Dispose();
    }
}

/// <summary>
/// TCP node connection.
/// </summary>
public sealed class TcpNodeConnection : NodeConnection
{
    private readonly TcpClient _client;
    private NetworkStream? _stream;

    public TcpNodeConnection(NodeId remoteNodeId, NodeEndpoint endpoint, TcpClient client)
    {
        RemoteNodeId = remoteNodeId;
        Endpoint = endpoint;
        _client = client;
        _stream = client.GetStream();
        State = ConnectionState.Connected;
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public override Task ConnectAsync(CancellationToken ct = default)
    {
        // Already connected in constructor
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");

        // Send length prefix
        var length = BitConverter.GetBytes(data.Length);
        await _stream.WriteAsync(length, ct);
        await _stream.WriteAsync(data, ct);

        BytesSent += 4 + data.Length;
        UpdateActivity();
        return data.Length;
    }

    /// <inheritdoc />
    public override async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");

        // Read length prefix
        var lengthBuf = new byte[4];
        var read = await _stream.ReadAsync(lengthBuf, ct);
        if (read < 4) return 0;

        var length = BitConverter.ToInt32(lengthBuf);
        var toRead = Math.Min(length, buffer.Length);
        read = await _stream.ReadAsync(buffer[..toRead], ct);

        BytesReceived += 4 + read;
        UpdateActivity();
        return read;
    }

    /// <inheritdoc />
    public override async Task SendMessageAsync(SignedMessage message, CancellationToken ct = default)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
        await SendAsync(json, ct);
    }

    /// <inheritdoc />
    public override async Task<SignedMessage?> ReceiveMessageAsync(CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var read = await ReceiveAsync(buffer, ct);
            if (read == 0) return null;

            return System.Text.Json.JsonSerializer.Deserialize<SignedMessage>(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public override Task CloseAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Closing;
        _stream?.Close();
        _client.Close();
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _stream?.Dispose();
        _client.Dispose();
    }
}

/// <summary>
/// HTTP transport driver for WAN/Cloud connections.
/// </summary>
public sealed class HttpTransportDriver : ITransportDriver
{
    private readonly HttpClient _httpClient;

    /// <inheritdoc />
    public TransportProtocol Protocol => TransportProtocol.Http;

    /// <inheritdoc />
    public string Name => "HttpTransport";

    public HttpTransportDriver(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <inheritdoc />
    public Task<NodeConnection> ConnectAsync(NodeEndpoint endpoint, NodeId remoteNodeId, CancellationToken ct = default)
    {
        var connection = new HttpNodeConnection(remoteNodeId, endpoint, _httpClient);
        return Task.FromResult<NodeConnection>(connection);
    }

    /// <inheritdoc />
    public Task StartListeningAsync(NodeEndpoint localEndpoint, Func<NodeConnection, Task> connectionHandler, CancellationToken ct = default)
    {
        // HTTP listening requires ASP.NET Core or similar - stub for now
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopListeningAsync()
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP node connection (request/response based).
/// </summary>
public sealed class HttpNodeConnection : NodeConnection
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public HttpNodeConnection(NodeId remoteNodeId, NodeEndpoint endpoint, HttpClient httpClient)
    {
        RemoteNodeId = remoteNodeId;
        Endpoint = endpoint;
        _httpClient = httpClient;
        _baseUri = endpoint.ToUri();
    }

    /// <inheritdoc />
    public override Task ConnectAsync(CancellationToken ct = default)
    {
        // HTTP is connectionless
        State = ConnectionState.Connected;
        ConnectedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(data.ToArray());
        var response = await _httpClient.PostAsync(new Uri(_baseUri, "/data"), content, ct);
        response.EnsureSuccessStatusCode();
        BytesSent += data.Length;
        UpdateActivity();
        return data.Length;
    }

    /// <inheritdoc />
    public override async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(new Uri(_baseUri, "/data"), ct);
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadAsByteArrayAsync(ct);
        var length = Math.Min(data.Length, buffer.Length);
        data.AsMemory(0, length).CopyTo(buffer);

        BytesReceived += length;
        UpdateActivity();
        return length;
    }

    /// <inheritdoc />
    public override async Task SendMessageAsync(SignedMessage message, CancellationToken ct = default)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
        using var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await _httpClient.PostAsync(new Uri(_baseUri, "/message"), content, ct);
        response.EnsureSuccessStatusCode();
        BytesSent += json.Length;
        UpdateActivity();
    }

    /// <inheritdoc />
    public override async Task<SignedMessage?> ReceiveMessageAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(new Uri(_baseUri, "/message"), ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsByteArrayAsync(ct);
        BytesReceived += json.Length;
        UpdateActivity();

        return System.Text.Json.JsonSerializer.Deserialize<SignedMessage>(json);
    }

    /// <inheritdoc />
    public override Task CloseAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Interface for the transport bus.
/// </summary>
public interface ITransportBus
{
    /// <summary>Local node ID.</summary>
    NodeId LocalNodeId { get; }

    /// <summary>Gets or creates a connection to a node.</summary>
    Task<NodeConnection> GetConnectionAsync(NodeId nodeId, CancellationToken ct = default);

    /// <summary>Sends a message to a node.</summary>
    Task SendAsync(NodeId nodeId, SignedMessage message, CancellationToken ct = default);

    /// <summary>Broadcasts a message to multiple nodes.</summary>
    Task BroadcastAsync(IEnumerable<NodeId> nodeIds, SignedMessage message, CancellationToken ct = default);

    /// <summary>Registers a message handler.</summary>
    void OnMessage(string messageType, Func<SignedMessage, NodeConnection, Task> handler);

    /// <summary>Starts the transport bus.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops the transport bus.</summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Transport bus with driver registry.
/// </summary>
public sealed class TransportBus : ITransportBus, IAsyncDisposable
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly NodeIdentityManager _identityManager;
    private readonly Dictionary<TransportProtocol, ITransportDriver> _drivers = new();
    private readonly ConcurrentDictionary<NodeId, NodeConnection> _connections = new();
    private readonly Dictionary<string, Func<SignedMessage, NodeConnection, Task>> _handlers = new();
    private bool _started;

    /// <inheritdoc />
    public NodeId LocalNodeId => _identityManager.LocalIdentity.Id;

    public TransportBus(NodeRegistry nodeRegistry, NodeIdentityManager identityManager)
    {
        _nodeRegistry = nodeRegistry;
        _identityManager = identityManager;
    }

    /// <summary>
    /// Registers a transport driver.
    /// </summary>
    public void RegisterDriver(ITransportDriver driver)
    {
        _drivers[driver.Protocol] = driver;
    }

    /// <inheritdoc />
    public async Task<NodeConnection> GetConnectionAsync(NodeId nodeId, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(nodeId, out var existing) && existing.State == ConnectionState.Connected)
            return existing;

        var node = _nodeRegistry.GetNode(nodeId);
        if (node == null)
            throw new InvalidOperationException($"Unknown node: {nodeId}");

        var endpoint = node.GetPreferredEndpoint();
        if (endpoint == null)
            throw new InvalidOperationException($"No endpoint for node: {nodeId}");

        if (!_drivers.TryGetValue(endpoint.Protocol, out var driver))
            throw new InvalidOperationException($"No driver for protocol: {endpoint.Protocol}");

        var connection = await driver.ConnectAsync(endpoint, nodeId, ct);
        _connections[nodeId] = connection;

        return connection;
    }

    /// <inheritdoc />
    public async Task SendAsync(NodeId nodeId, SignedMessage message, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(nodeId, ct);
        await connection.SendMessageAsync(message, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(IEnumerable<NodeId> nodeIds, SignedMessage message, CancellationToken ct = default)
    {
        var tasks = nodeIds.Select(async nodeId =>
        {
            try
            {
                await SendAsync(nodeId, message, ct);
            }
            catch
            {
                // Log but don't fail on individual node failures
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public void OnMessage(string messageType, Func<SignedMessage, NodeConnection, Task> handler)
    {
        _handlers[messageType] = handler;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;

        foreach (var endpoint in _identityManager.LocalIdentity.Endpoints)
        {
            if (_drivers.TryGetValue(endpoint.Protocol, out var driver))
            {
                await driver.StartListeningAsync(endpoint, HandleIncomingConnection, ct);
            }
        }

        _started = true;
    }

    private async Task HandleIncomingConnection(NodeConnection connection)
    {
        try
        {
            while (connection.State == ConnectionState.Connected)
            {
                var message = await connection.ReceiveMessageAsync();
                if (message == null) break;

                if (_handlers.TryGetValue(message.MessageType, out var handler))
                {
                    await handler(message, connection);
                }
            }
        }
        catch
        {
            // Connection closed
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var driver in _drivers.Values)
        {
            await driver.StopListeningAsync();
        }

        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();
        _started = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>
/// Interface for stream relay (zero-copy piping between nodes).
/// </summary>
public interface IStreamRelay
{
    /// <summary>
    /// Relays data from source to destination without storing locally.
    /// Acts as a pure conduit/pipe.
    /// </summary>
    Task<RelayResult> RelayAsync(
        NodeId source,
        NodeId destination,
        ObjectId objectId,
        CancellationToken ct = default);

    /// <summary>
    /// Relays a stream to multiple destinations (broadcast relay).
    /// </summary>
    Task<RelayResult> RelayToManyAsync(
        NodeId source,
        IEnumerable<NodeId> destinations,
        ObjectId objectId,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a relay operation.
/// </summary>
public sealed class RelayResult
{
    public bool Success { get; init; }
    public ObjectId ObjectId { get; init; }
    public long BytesRelayed { get; init; }
    public TimeSpan Duration { get; init; }
    public NodeId SourceNode { get; init; }
    public List<NodeId> SuccessfulDestinations { get; init; } = new();
    public List<NodeId> FailedDestinations { get; init; } = new();
    public string? ErrorMessage { get; init; }

    /// <summary>Effective throughput in bytes/second.</summary>
    public double ThroughputBps => Duration.TotalSeconds > 0 ? BytesRelayed / Duration.TotalSeconds : 0;
}

/// <summary>
/// Stream relay implementation for zero-copy node-to-node transfers.
/// </summary>
public sealed class StreamRelay : IStreamRelay
{
    private readonly ITransportBus _transportBus;
    private readonly int _bufferSize;

    public StreamRelay(ITransportBus transportBus, int bufferSize = 65536)
    {
        _transportBus = transportBus;
        _bufferSize = bufferSize;
    }

    /// <inheritdoc />
    public async Task<RelayResult> RelayAsync(
        NodeId source,
        NodeId destination,
        ObjectId objectId,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long bytesRelayed = 0;

        try
        {
            // Get connections
            var sourceConn = await _transportBus.GetConnectionAsync(source, ct);
            var destConn = await _transportBus.GetConnectionAsync(destination, ct);

            // Request object from source
            var fetchRequest = new RelayFetchRequest { ObjectId = objectId.ToHex() };
            var requestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fetchRequest);
            await sourceConn.SendAsync(requestBytes, ct);

            // Stream from source to destination
            var buffer = new byte[_bufferSize];
            int bytesRead;

            while ((bytesRead = await sourceConn.ReceiveAsync(buffer, ct)) > 0)
            {
                if (ct.IsCancellationRequested)
                    break;

                // Pipe directly to destination without local storage
                await destConn.SendAsync(buffer.AsMemory(0, bytesRead), ct);
                bytesRelayed += bytesRead;
            }

            sw.Stop();

            return new RelayResult
            {
                Success = true,
                ObjectId = objectId,
                BytesRelayed = bytesRelayed,
                Duration = sw.Elapsed,
                SourceNode = source,
                SuccessfulDestinations = new List<NodeId> { destination }
            };
        }
        catch (Exception ex)
        {
            sw.Stop();

            return new RelayResult
            {
                Success = false,
                ObjectId = objectId,
                BytesRelayed = bytesRelayed,
                Duration = sw.Elapsed,
                SourceNode = source,
                FailedDestinations = new List<NodeId> { destination },
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<RelayResult> RelayToManyAsync(
        NodeId source,
        IEnumerable<NodeId> destinations,
        ObjectId objectId,
        CancellationToken ct = default)
    {
        var destList = destinations.ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long bytesRelayed = 0;
        var successful = new List<NodeId>();
        var failed = new List<NodeId>();

        try
        {
            // Get source connection
            var sourceConn = await _transportBus.GetConnectionAsync(source, ct);

            // Get destination connections
            var destConnections = new Dictionary<NodeId, NodeConnection>();
            foreach (var dest in destList)
            {
                try
                {
                    destConnections[dest] = await _transportBus.GetConnectionAsync(dest, ct);
                }
                catch
                {
                    failed.Add(dest);
                }
            }

            if (destConnections.Count == 0)
            {
                return new RelayResult
                {
                    Success = false,
                    ObjectId = objectId,
                    SourceNode = source,
                    FailedDestinations = destList,
                    ErrorMessage = "No destinations reachable"
                };
            }

            // Request object from source
            var fetchRequest = new RelayFetchRequest { ObjectId = objectId.ToHex() };
            var requestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fetchRequest);
            await sourceConn.SendAsync(requestBytes, ct);

            // Stream to all destinations (fan-out)
            var buffer = new byte[_bufferSize];
            int bytesRead;

            while ((bytesRead = await sourceConn.ReceiveAsync(buffer, ct)) > 0)
            {
                if (ct.IsCancellationRequested)
                    break;

                var data = buffer.AsMemory(0, bytesRead);

                // Send to all destinations in parallel
                var sendTasks = destConnections.Select(async kvp =>
                {
                    try
                    {
                        await kvp.Value.SendAsync(data, ct);
                        return (kvp.Key, Success: true);
                    }
                    catch
                    {
                        return (kvp.Key, Success: false);
                    }
                });

                var results = await Task.WhenAll(sendTasks);

                // Track failures (remove from active destinations)
                foreach (var (nodeId, success) in results)
                {
                    if (!success)
                    {
                        destConnections.Remove(nodeId);
                        if (!failed.Contains(nodeId))
                            failed.Add(nodeId);
                    }
                }

                bytesRelayed += bytesRead;
            }

            // Mark remaining as successful
            successful.AddRange(destConnections.Keys);

            sw.Stop();

            return new RelayResult
            {
                Success = successful.Count > 0,
                ObjectId = objectId,
                BytesRelayed = bytesRelayed,
                Duration = sw.Elapsed,
                SourceNode = source,
                SuccessfulDestinations = successful,
                FailedDestinations = failed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();

            return new RelayResult
            {
                Success = false,
                ObjectId = objectId,
                BytesRelayed = bytesRelayed,
                Duration = sw.Elapsed,
                SourceNode = source,
                SuccessfulDestinations = successful,
                FailedDestinations = failed.Concat(destList.Except(successful).Except(failed)).ToList(),
                ErrorMessage = ex.Message
            };
        }
    }
}

/// <summary>
/// Request for relay fetch operation.
/// </summary>
internal sealed class RelayFetchRequest
{
    public string ObjectId { get; set; } = string.Empty;
}

/// <summary>
/// Extended transport bus with relay support.
/// </summary>
public sealed class RelayCapableTransportBus : ITransportBus, IStreamRelay, IAsyncDisposable
{
    private readonly TransportBus _inner;
    private readonly StreamRelay _relay;

    public RelayCapableTransportBus(NodeRegistry nodeRegistry, NodeIdentityManager identityManager)
    {
        _inner = new TransportBus(nodeRegistry, identityManager);
        _relay = new StreamRelay(_inner);
    }

    /// <inheritdoc />
    public NodeId LocalNodeId => _inner.LocalNodeId;

    /// <summary>
    /// Registers a transport driver.
    /// </summary>
    public void RegisterDriver(ITransportDriver driver) => _inner.RegisterDriver(driver);

    /// <inheritdoc />
    public Task<NodeConnection> GetConnectionAsync(NodeId nodeId, CancellationToken ct = default)
        => _inner.GetConnectionAsync(nodeId, ct);

    /// <inheritdoc />
    public Task SendAsync(NodeId nodeId, SignedMessage message, CancellationToken ct = default)
        => _inner.SendAsync(nodeId, message, ct);

    /// <inheritdoc />
    public Task BroadcastAsync(IEnumerable<NodeId> nodeIds, SignedMessage message, CancellationToken ct = default)
        => _inner.BroadcastAsync(nodeIds, message, ct);

    /// <inheritdoc />
    public void OnMessage(string messageType, Func<SignedMessage, NodeConnection, Task> handler)
        => _inner.OnMessage(messageType, handler);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
        => _inner.StartAsync(ct);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
        => _inner.StopAsync(ct);

    /// <inheritdoc />
    public Task<RelayResult> RelayAsync(NodeId source, NodeId destination, ObjectId objectId, CancellationToken ct = default)
        => _relay.RelayAsync(source, destination, objectId, ct);

    /// <inheritdoc />
    public Task<RelayResult> RelayToManyAsync(NodeId source, IEnumerable<NodeId> destinations, ObjectId objectId, CancellationToken ct = default)
        => _relay.RelayToManyAsync(source, destinations, objectId, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
    }
}
