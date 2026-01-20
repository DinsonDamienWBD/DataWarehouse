namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Public endpoint information discovered via STUN or similar protocols.
/// </summary>
public sealed class PublicEndpoint
{
    /// <summary>Public IP address.</summary>
    public IPAddress Address { get; init; } = IPAddress.None;

    /// <summary>Public port.</summary>
    public int Port { get; init; }

    /// <summary>NAT type detected.</summary>
    public NatType NatType { get; init; } = NatType.Unknown;

    /// <summary>When this endpoint was discovered.</summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Time-to-live for this endpoint info.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether this endpoint is still valid.</summary>
    public bool IsValid => DateTimeOffset.UtcNow < DiscoveredAt + Ttl;

    /// <summary>Creates IPEndPoint from this.</summary>
    public IPEndPoint ToIPEndPoint() => new(Address, Port);

    public override string ToString() => $"{Address}:{Port} ({NatType})";
}

/// <summary>
/// Types of NAT behavior.
/// </summary>
public enum NatType
{
    /// <summary>Unable to determine.</summary>
    Unknown = 0,

    /// <summary>No NAT - direct public IP.</summary>
    Open = 1,

    /// <summary>Full cone NAT - any external can reach mapped port.</summary>
    FullCone = 2,

    /// <summary>Restricted cone - only known IPs can reach.</summary>
    RestrictedCone = 3,

    /// <summary>Port restricted cone - only known IP:port pairs.</summary>
    PortRestrictedCone = 4,

    /// <summary>Symmetric NAT - different mapping per destination.</summary>
    Symmetric = 5,

    /// <summary>UDP blocked.</summary>
    UdpBlocked = 6
}

/// <summary>
/// Result of a NAT traversal attempt.
/// </summary>
public sealed class TraversalResult
{
    /// <summary>Whether traversal succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Established endpoint for communication.</summary>
    public IPEndPoint? Endpoint { get; init; }

    /// <summary>Method used for traversal.</summary>
    public TraversalMethod Method { get; init; }

    /// <summary>Error if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Latency to the endpoint.</summary>
    public TimeSpan? Latency { get; init; }

    public static TraversalResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };

    public static TraversalResult Succeeded(IPEndPoint endpoint, TraversalMethod method, TimeSpan? latency = null) => new()
    {
        Success = true,
        Endpoint = endpoint,
        Method = method,
        Latency = latency
    };
}

/// <summary>
/// Method used for NAT traversal.
/// </summary>
public enum TraversalMethod
{
    /// <summary>Direct connection (no NAT).</summary>
    Direct = 0,

    /// <summary>UDP hole punching.</summary>
    UdpHolePunch = 1,

    /// <summary>TCP hole punching.</summary>
    TcpHolePunch = 2,

    /// <summary>Relayed through gateway.</summary>
    Relayed = 3,

    /// <summary>UPnP port mapping.</summary>
    UPnP = 4,

    /// <summary>NAT-PMP/PCP port mapping.</summary>
    NatPmp = 5
}

/// <summary>
/// Interface for NAT traversal operations.
/// </summary>
public interface INatTraversal
{
    /// <summary>
    /// Discovers this node's public endpoint.
    /// </summary>
    Task<PublicEndpoint?> DiscoverPublicEndpointAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the detected NAT type.
    /// </summary>
    Task<NatType> DetectNatTypeAsync(CancellationToken ct = default);

    /// <summary>
    /// Attempts to establish direct connection to a peer.
    /// </summary>
    Task<TraversalResult> TraverseAsync(
        NodeId targetNode,
        PublicEndpoint targetEndpoint,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a relay gateway for fallback.
    /// </summary>
    void RegisterRelayGateway(NodeId gatewayId, IPEndPoint gatewayEndpoint);

    /// <summary>
    /// Gets available relay gateways.
    /// </summary>
    IReadOnlyList<(NodeId Id, IPEndPoint Endpoint)> GetRelayGateways();
}

/// <summary>
/// STUN client for discovering public endpoints.
/// Implements RFC 5389 (Session Traversal Utilities for NAT).
/// </summary>
public sealed class StunClient : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly List<IPEndPoint> _stunServers;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    /// <summary>Well-known public STUN servers.</summary>
    public static readonly IPEndPoint[] DefaultStunServers = new[]
    {
        new IPEndPoint(IPAddress.Parse("74.125.250.129"), 19302),  // Google STUN
        new IPEndPoint(IPAddress.Parse("64.233.163.127"), 19302),  // Google STUN 2
    };

    public StunClient(IEnumerable<IPEndPoint>? stunServers = null, TimeSpan? timeout = null)
    {
        _udpClient = new UdpClient(0); // Bind to any available port
        _stunServers = stunServers?.ToList() ?? DefaultStunServers.ToList();
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Discovers public endpoint using STUN.
    /// </summary>
    public async Task<PublicEndpoint?> DiscoverAsync(CancellationToken ct = default)
    {
        foreach (var server in _stunServers)
        {
            try
            {
                var endpoint = await QueryStunServerAsync(server, ct);
                if (endpoint != null)
                    return endpoint;
            }
            catch
            {
                // Try next server
            }
        }

        return null;
    }

    /// <summary>
    /// Detects NAT type using RFC 3489 algorithm.
    /// </summary>
    public async Task<NatType> DetectNatTypeAsync(CancellationToken ct = default)
    {
        if (_stunServers.Count < 2)
            return NatType.Unknown;

        // Test 1: Basic binding request
        var endpoint1 = await QueryStunServerAsync(_stunServers[0], ct);
        if (endpoint1 == null)
            return NatType.UdpBlocked;

        // Check if we have a public IP (no NAT)
        var localEndpoint = (IPEndPoint?)_udpClient.Client.LocalEndPoint;
        if (localEndpoint != null && endpoint1.Address.Equals(localEndpoint.Address))
            return NatType.Open;

        // Test 2: Query second server from same socket
        var endpoint2 = await QueryStunServerAsync(_stunServers[1], ct);
        if (endpoint2 == null)
            return NatType.Unknown;

        // If mapping differs, it's symmetric NAT
        if (!endpoint1.Address.Equals(endpoint2.Address) || endpoint1.Port != endpoint2.Port)
            return NatType.Symmetric;

        // Further tests would require STUN server with CHANGE-REQUEST support
        // For now, assume port-restricted cone (common case)
        return NatType.PortRestrictedCone;
    }

    private async Task<PublicEndpoint?> QueryStunServerAsync(IPEndPoint server, CancellationToken ct)
    {
        // Build STUN Binding Request (RFC 5389)
        var transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);

        var request = new byte[20];
        // Message Type: Binding Request (0x0001)
        request[0] = 0x00;
        request[1] = 0x01;
        // Message Length: 0 (no attributes)
        request[2] = 0x00;
        request[3] = 0x00;
        // Magic Cookie (0x2112A442)
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;
        // Transaction ID
        Array.Copy(transactionId, 0, request, 8, 12);

        await _udpClient.SendAsync(request, request.Length, server);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        try
        {
            var receiveTask = _udpClient.ReceiveAsync(cts.Token);
            var result = await receiveTask;

            return ParseStunResponse(result.Buffer, transactionId);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static PublicEndpoint? ParseStunResponse(byte[] response, byte[] expectedTransactionId)
    {
        if (response.Length < 20)
            return null;

        // Check message type: Binding Response (0x0101)
        if (response[0] != 0x01 || response[1] != 0x01)
            return null;

        // Verify magic cookie
        if (response[4] != 0x21 || response[5] != 0x12 || response[6] != 0xA4 || response[7] != 0x42)
            return null;

        // Verify transaction ID
        for (int i = 0; i < 12; i++)
        {
            if (response[8 + i] != expectedTransactionId[i])
                return null;
        }

        // Parse attributes
        int messageLength = (response[2] << 8) | response[3];
        int offset = 20;

        while (offset < 20 + messageLength && offset + 4 <= response.Length)
        {
            int attrType = (response[offset] << 8) | response[offset + 1];
            int attrLength = (response[offset + 2] << 8) | response[offset + 3];
            offset += 4;

            if (offset + attrLength > response.Length)
                break;

            // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
            if (attrType == 0x0020 || attrType == 0x0001)
            {
                if (attrLength >= 8)
                {
                    int family = response[offset + 1];
                    int port;
                    IPAddress address;

                    if (attrType == 0x0020)
                    {
                        // XOR with magic cookie
                        port = ((response[offset + 2] ^ 0x21) << 8) | (response[offset + 3] ^ 0x12);
                        if (family == 0x01) // IPv4
                        {
                            var addrBytes = new byte[4];
                            addrBytes[0] = (byte)(response[offset + 4] ^ 0x21);
                            addrBytes[1] = (byte)(response[offset + 5] ^ 0x12);
                            addrBytes[2] = (byte)(response[offset + 6] ^ 0xA4);
                            addrBytes[3] = (byte)(response[offset + 7] ^ 0x42);
                            address = new IPAddress(addrBytes);
                        }
                        else
                        {
                            continue; // Skip IPv6 for now
                        }
                    }
                    else
                    {
                        port = (response[offset + 2] << 8) | response[offset + 3];
                        if (family == 0x01) // IPv4
                        {
                            var addrBytes = new byte[4];
                            Array.Copy(response, offset + 4, addrBytes, 0, 4);
                            address = new IPAddress(addrBytes);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    return new PublicEndpoint
                    {
                        Address = address,
                        Port = port,
                        NatType = NatType.Unknown // Determined separately
                    };
                }
            }

            // Align to 4-byte boundary
            offset += (attrLength + 3) & ~3;
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _udpClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// UDP hole punching driver for establishing direct P2P connections.
/// </summary>
public sealed class HolePunchingDriver
{
    private readonly TimeSpan _punchInterval;
    private readonly int _maxAttempts;
    private readonly TimeSpan _timeout;

    public HolePunchingDriver(
        TimeSpan? punchInterval = null,
        int maxAttempts = 10,
        TimeSpan? timeout = null)
    {
        _punchInterval = punchInterval ?? TimeSpan.FromMilliseconds(100);
        _maxAttempts = maxAttempts;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Attempts UDP hole punching to establish connection.
    /// Both peers must call this simultaneously.
    /// </summary>
    public async Task<TraversalResult> PunchAsync(
        UdpClient localClient,
        IPEndPoint targetEndpoint,
        byte[] handshakeToken,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        var punchPacket = BuildPunchPacket(handshakeToken);
        var startTime = DateTimeOffset.UtcNow;

        // Start receiving task
        var receiveTask = ReceiveHandshakeAsync(localClient, handshakeToken, cts.Token);

        // Send punch packets
        for (int i = 0; i < _maxAttempts && !cts.IsCancellationRequested; i++)
        {
            try
            {
                await localClient.SendAsync(punchPacket, punchPacket.Length, targetEndpoint);

                // Check if we received response
                if (receiveTask.IsCompleted)
                {
                    var result = await receiveTask;
                    if (result != null)
                    {
                        return TraversalResult.Succeeded(
                            result.Value,
                            TraversalMethod.UdpHolePunch,
                            DateTimeOffset.UtcNow - startTime);
                    }
                }

                await Task.Delay(_punchInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue punching
            }
        }

        // Final check for received handshake
        try
        {
            var result = await receiveTask;
            if (result != null)
            {
                return TraversalResult.Succeeded(
                    result.Value,
                    TraversalMethod.UdpHolePunch,
                    DateTimeOffset.UtcNow - startTime);
            }
        }
        catch
        {
            // Timeout or cancelled
        }

        return TraversalResult.Failed("Hole punching failed - no response received");
    }

    private static byte[] BuildPunchPacket(byte[] handshakeToken)
    {
        // Simple packet: magic + token
        var packet = new byte[4 + handshakeToken.Length];
        packet[0] = 0x44; // 'D'
        packet[1] = 0x57; // 'W'
        packet[2] = 0x48; // 'H'
        packet[3] = 0x50; // 'P' (DataWarehouse Hole Punch)
        Array.Copy(handshakeToken, 0, packet, 4, handshakeToken.Length);
        return packet;
    }

    private static async Task<IPEndPoint?> ReceiveHandshakeAsync(
        UdpClient client,
        byte[] expectedToken,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct);

                // Validate packet
                if (result.Buffer.Length >= 4 + expectedToken.Length &&
                    result.Buffer[0] == 0x44 &&
                    result.Buffer[1] == 0x57 &&
                    result.Buffer[2] == 0x48 &&
                    result.Buffer[3] == 0x50)
                {
                    // Token validation (in real impl, would verify cryptographic handshake)
                    return result.RemoteEndPoint;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue listening
            }
        }

        return null;
    }
}

/// <summary>
/// Relay connection through a gateway node.
/// Used as fallback when direct P2P fails.
/// </summary>
public sealed class RelayConnection : IDisposable
{
    private readonly TcpClient _gatewayClient;
    private readonly NetworkStream _stream;
    private readonly NodeId _gatewayId;
    private readonly NodeId _targetId;
    private bool _disposed;

    public NodeId GatewayId => _gatewayId;
    public NodeId TargetId => _targetId;
    public bool IsConnected => _gatewayClient.Connected;

    internal RelayConnection(TcpClient client, NodeId gatewayId, NodeId targetId)
    {
        _gatewayClient = client;
        _stream = client.GetStream();
        _gatewayId = gatewayId;
        _targetId = targetId;
    }

    /// <summary>
    /// Sends data through the relay.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        // Framing: length prefix (4 bytes) + data
        var frame = new byte[4 + data.Length];
        frame[0] = (byte)(data.Length >> 24);
        frame[1] = (byte)(data.Length >> 16);
        frame[2] = (byte)(data.Length >> 8);
        frame[3] = (byte)data.Length;
        Array.Copy(data, 0, frame, 4, data.Length);

        await _stream.WriteAsync(frame, 0, frame.Length, ct);
    }

    /// <summary>
    /// Receives data from the relay.
    /// </summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        var lengthBuffer = new byte[4];
        var read = await _stream.ReadAsync(lengthBuffer, 0, 4, ct);
        if (read < 4)
            return null;

        var length = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) |
                     (lengthBuffer[2] << 8) | lengthBuffer[3];

        if (length > 10 * 1024 * 1024) // 10MB max
            throw new InvalidOperationException("Message too large");

        var data = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            read = await _stream.ReadAsync(data, totalRead, length - totalRead, ct);
            if (read == 0)
                return null;
            totalRead += read;
        }

        return data;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _gatewayClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Gateway node that relays traffic between peers that cannot establish direct connections.
/// </summary>
public sealed class RelayGateway : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private bool _disposed;

    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;
    public int ActiveSessions => _sessions.Count;

    public RelayGateway(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>
    /// Starts the relay gateway.
    /// </summary>
    public void Start()
    {
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();

            // Read session request: target node ID
            var headerBuffer = new byte[64];
            var read = await stream.ReadAsync(headerBuffer, 0, 64, ct);
            if (read < 32)
            {
                client.Dispose();
                return;
            }

            var sessionId = Encoding.UTF8.GetString(headerBuffer, 0, 32).TrimEnd('\0');
            var targetId = Encoding.UTF8.GetString(headerBuffer, 32, Math.Min(32, read - 32)).TrimEnd('\0');

            // Find or create session
            var session = _sessions.GetOrAdd(sessionId, _ => new RelaySession(sessionId));

            if (session.Client1 == null)
            {
                session.Client1 = client;
                session.Client1Target = targetId;
            }
            else if (session.Client2 == null)
            {
                session.Client2 = client;
                session.Client2Target = targetId;

                // Both peers connected - start relaying
                _ = RelayBetweenAsync(session, ct);
            }
            else
            {
                // Session full
                client.Dispose();
            }
        }
        catch
        {
            client.Dispose();
        }
    }

    private async Task RelayBetweenAsync(RelaySession session, CancellationToken ct)
    {
        try
        {
            var stream1 = session.Client1!.GetStream();
            var stream2 = session.Client2!.GetStream();

            var task1 = CopyStreamAsync(stream1, stream2, ct);
            var task2 = CopyStreamAsync(stream2, stream1, ct);

            await Task.WhenAny(task1, task2);
        }
        finally
        {
            _sessions.TryRemove(session.SessionId, out _);
            session.Client1?.Dispose();
            session.Client2?.Dispose();
        }
    }

    private static async Task CopyStreamAsync(NetworkStream source, NetworkStream dest, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read == 0)
                break;
            await dest.WriteAsync(buffer, 0, read, ct);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var session in _sessions.Values)
            {
                session.Client1?.Dispose();
                session.Client2?.Dispose();
            }
            _sessions.Clear();
            _disposed = true;
        }
    }

    private sealed class RelaySession
    {
        public string SessionId { get; }
        public TcpClient? Client1 { get; set; }
        public TcpClient? Client2 { get; set; }
        public string? Client1Target { get; set; }
        public string? Client2Target { get; set; }

        public RelaySession(string sessionId)
        {
            SessionId = sessionId;
        }
    }
}

/// <summary>
/// Complete NAT traversal implementation combining STUN, hole punching, and relay fallback.
/// </summary>
public sealed class NatTraversalService : INatTraversal, IDisposable
{
    private readonly StunClient _stunClient;
    private readonly HolePunchingDriver _holePunchDriver;
    private readonly ConcurrentDictionary<NodeId, IPEndPoint> _relayGateways = new();
    private readonly ConcurrentDictionary<NodeId, PublicEndpoint> _peerEndpoints = new();
    private PublicEndpoint? _cachedPublicEndpoint;
    private NatType _cachedNatType = NatType.Unknown;
    private UdpClient? _mainUdpClient;
    private bool _disposed;

    public NatTraversalService(IEnumerable<IPEndPoint>? stunServers = null)
    {
        _stunClient = new StunClient(stunServers);
        _holePunchDriver = new HolePunchingDriver();
    }

    /// <inheritdoc />
    public async Task<PublicEndpoint?> DiscoverPublicEndpointAsync(CancellationToken ct = default)
    {
        if (_cachedPublicEndpoint?.IsValid == true)
            return _cachedPublicEndpoint;

        _cachedPublicEndpoint = await _stunClient.DiscoverAsync(ct);
        return _cachedPublicEndpoint;
    }

    /// <inheritdoc />
    public async Task<NatType> DetectNatTypeAsync(CancellationToken ct = default)
    {
        if (_cachedNatType != NatType.Unknown)
            return _cachedNatType;

        _cachedNatType = await _stunClient.DetectNatTypeAsync(ct);
        return _cachedNatType;
    }

    /// <inheritdoc />
    public async Task<TraversalResult> TraverseAsync(
        NodeId targetNode,
        PublicEndpoint targetEndpoint,
        CancellationToken ct = default)
    {
        // Strategy 1: If either side is Open or FullCone, direct connection works
        var myNatType = await DetectNatTypeAsync(ct);
        if (myNatType == NatType.Open || targetEndpoint.NatType == NatType.Open)
        {
            return TraversalResult.Succeeded(
                targetEndpoint.ToIPEndPoint(),
                TraversalMethod.Direct);
        }

        // Strategy 2: Try UDP hole punching
        if (myNatType != NatType.Symmetric && targetEndpoint.NatType != NatType.Symmetric)
        {
            _mainUdpClient ??= new UdpClient(0);

            // Generate handshake token from node IDs
            var handshakeToken = GenerateHandshakeToken(targetNode);

            var punchResult = await _holePunchDriver.PunchAsync(
                _mainUdpClient,
                targetEndpoint.ToIPEndPoint(),
                handshakeToken,
                ct);

            if (punchResult.Success)
            {
                _peerEndpoints[targetNode] = punchResult.Endpoint!;
                return punchResult;
            }
        }

        // Strategy 3: Fall back to relay
        var gateways = GetRelayGateways();
        if (gateways.Count > 0)
        {
            var gateway = gateways[0];
            return await ConnectViaRelayAsync(targetNode, gateway.Endpoint, ct);
        }

        return TraversalResult.Failed("All traversal methods failed");
    }

    private async Task<TraversalResult> ConnectViaRelayAsync(
        NodeId targetNode,
        IPEndPoint gatewayEndpoint,
        CancellationToken ct)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(gatewayEndpoint.Address, gatewayEndpoint.Port, ct);

            // Send session request
            var sessionId = Guid.NewGuid().ToString("N");
            var header = new byte[64];
            Encoding.UTF8.GetBytes(sessionId.PadRight(32, '\0'), header);
            Encoding.UTF8.GetBytes(targetNode.Value.PadRight(32, '\0'), header.AsSpan(32));

            var stream = client.GetStream();
            await stream.WriteAsync(header, 0, header.Length, ct);

            return TraversalResult.Succeeded(
                gatewayEndpoint,
                TraversalMethod.Relayed);
        }
        catch (Exception ex)
        {
            return TraversalResult.Failed($"Relay connection failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void RegisterRelayGateway(NodeId gatewayId, IPEndPoint gatewayEndpoint)
    {
        _relayGateways[gatewayId] = gatewayEndpoint;
    }

    /// <inheritdoc />
    public IReadOnlyList<(NodeId Id, IPEndPoint Endpoint)> GetRelayGateways()
    {
        return _relayGateways.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Registers a peer's public endpoint for future connections.
    /// </summary>
    public void RegisterPeerEndpoint(NodeId peerId, PublicEndpoint endpoint)
    {
        _peerEndpoints[peerId] = endpoint;
    }

    /// <summary>
    /// Gets a known peer endpoint.
    /// </summary>
    public PublicEndpoint? GetPeerEndpoint(NodeId peerId)
    {
        return _peerEndpoints.TryGetValue(peerId, out var endpoint) ? endpoint : null;
    }

    private static byte[] GenerateHandshakeToken(NodeId targetNode)
    {
        // Simple token based on node ID (in production, would use cryptographic handshake)
        return Encoding.UTF8.GetBytes(targetNode.Value.PadRight(16, '\0'));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stunClient.Dispose();
            _mainUdpClient?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// P2P connection manager that integrates NAT traversal with federation transport.
/// </summary>
public sealed class P2PConnectionManager : IDisposable
{
    private readonly INatTraversal _natTraversal;
    private readonly ITransportBus _transportBus;
    private readonly ConcurrentDictionary<NodeId, P2PConnection> _connections = new();
    private bool _disposed;

    public P2PConnectionManager(INatTraversal natTraversal, ITransportBus transportBus)
    {
        _natTraversal = natTraversal;
        _transportBus = transportBus;
    }

    /// <summary>
    /// Establishes or retrieves a P2P connection to a node.
    /// </summary>
    public async Task<P2PConnection?> GetOrCreateConnectionAsync(
        NodeId targetNode,
        PublicEndpoint? knownEndpoint,
        CancellationToken ct = default)
    {
        // Return existing connection if active
        if (_connections.TryGetValue(targetNode, out var existing) && existing.IsActive)
            return existing;

        // Need endpoint info to connect
        if (knownEndpoint == null)
            return null;

        // Attempt traversal
        var result = await _natTraversal.TraverseAsync(targetNode, knownEndpoint, ct);
        if (!result.Success)
            return null;

        var connection = new P2PConnection(targetNode, result.Endpoint!, result.Method);
        _connections[targetNode] = connection;

        return connection;
    }

    /// <summary>
    /// Gets statistics about P2P connections.
    /// </summary>
    public P2PStats GetStats()
    {
        var connections = _connections.Values.ToList();
        return new P2PStats
        {
            TotalConnections = connections.Count,
            ActiveConnections = connections.Count(c => c.IsActive),
            DirectConnections = connections.Count(c => c.Method == TraversalMethod.Direct),
            HolePunchedConnections = connections.Count(c => c.Method == TraversalMethod.UdpHolePunch),
            RelayedConnections = connections.Count(c => c.Method == TraversalMethod.Relayed)
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var conn in _connections.Values)
            {
                conn.Dispose();
            }
            _connections.Clear();
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents an active P2P connection.
/// </summary>
public sealed class P2PConnection : IDisposable
{
    public NodeId TargetNode { get; }
    public IPEndPoint Endpoint { get; }
    public TraversalMethod Method { get; }
    public DateTimeOffset EstablishedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; private set; } = true;

    private UdpClient? _udpClient;
    private bool _disposed;

    public P2PConnection(NodeId targetNode, IPEndPoint endpoint, TraversalMethod method)
    {
        TargetNode = targetNode;
        Endpoint = endpoint;
        Method = method;
    }

    /// <summary>
    /// Sends data over the P2P connection.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (!IsActive)
            throw new InvalidOperationException("Connection is not active");

        _udpClient ??= new UdpClient();
        await _udpClient.SendAsync(data, data.Length, Endpoint);
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    public void Close()
    {
        IsActive = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            IsActive = false;
            _udpClient?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Statistics about P2P connections.
/// </summary>
public sealed class P2PStats
{
    public int TotalConnections { get; init; }
    public int ActiveConnections { get; init; }
    public int DirectConnections { get; init; }
    public int HolePunchedConnections { get; init; }
    public int RelayedConnections { get; init; }
}

/// <summary>
/// Extension methods for integrating NAT traversal with federation.
/// </summary>
public static class NatTraversalExtensions
{
    /// <summary>
    /// Adds NAT traversal capability to a federation hub.
    /// </summary>
    public static NatTraversalService EnableNatTraversal(
        this IFederationHub hub,
        IEnumerable<IPEndPoint>? stunServers = null)
    {
        var service = new NatTraversalService(stunServers);

        // Register this node's public endpoint when discovered
        _ = Task.Run(async () =>
        {
            var endpoint = await service.DiscoverPublicEndpointAsync();
            if (endpoint != null)
            {
                // Could broadcast to peers via TransportBus
            }
        });

        return service;
    }

    /// <summary>
    /// Creates a relay gateway on this node.
    /// </summary>
    public static RelayGateway CreateRelayGateway(this IFederationHub hub, int port = 0)
    {
        var gateway = new RelayGateway(port);
        gateway.Start();
        return gateway;
    }
}
