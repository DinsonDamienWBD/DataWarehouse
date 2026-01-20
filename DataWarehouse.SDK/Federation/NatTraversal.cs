namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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

// ============================================================================
// SCENARIO 4: P2P DIRECT LINK
// ICE-lite candidate gathering, connectivity checks, relay fallback,
// and link quality monitoring.
// ============================================================================

#region ICE-Lite Candidate Gathering

/// <summary>
/// ICE candidate types per RFC 8445.
/// </summary>
public enum IceCandidateType
{
    /// <summary>Host candidate - local interface address.</summary>
    Host = 0,
    /// <summary>Server reflexive - discovered via STUN.</summary>
    ServerReflexive = 1,
    /// <summary>Peer reflexive - discovered during connectivity checks.</summary>
    PeerReflexive = 2,
    /// <summary>Relayed - via TURN server.</summary>
    Relayed = 3
}

/// <summary>
/// ICE candidate per RFC 8445.
/// </summary>
public sealed class IceCandidate
{
    /// <summary>Unique foundation for this candidate.</summary>
    public string Foundation { get; set; } = string.Empty;

    /// <summary>Component ID (1 = RTP, 2 = RTCP for media).</summary>
    public int ComponentId { get; set; } = 1;

    /// <summary>Transport protocol.</summary>
    public string Transport { get; set; } = "udp";

    /// <summary>Priority (higher = more preferred).</summary>
    public uint Priority { get; set; }

    /// <summary>IP address.</summary>
    public IPAddress Address { get; set; } = IPAddress.None;

    /// <summary>Port number.</summary>
    public int Port { get; set; }

    /// <summary>Candidate type.</summary>
    public IceCandidateType Type { get; set; }

    /// <summary>Related address (for reflexive candidates).</summary>
    public IPAddress? RelatedAddress { get; set; }

    /// <summary>Related port.</summary>
    public int RelatedPort { get; set; }

    /// <summary>When this candidate was gathered.</summary>
    public DateTimeOffset GatheredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Creates IPEndPoint from this candidate.</summary>
    public IPEndPoint ToEndPoint() => new(Address, Port);

    /// <summary>
    /// Calculates priority per RFC 8445 formula.
    /// </summary>
    public static uint CalculatePriority(IceCandidateType type, int localPreference, int componentId)
    {
        var typePreference = type switch
        {
            IceCandidateType.Host => 126,
            IceCandidateType.PeerReflexive => 110,
            IceCandidateType.ServerReflexive => 100,
            IceCandidateType.Relayed => 0,
            _ => 0
        };

        return (uint)((typePreference << 24) + (localPreference << 8) + (256 - componentId));
    }

    public override string ToString() =>
        $"{Type}:{Address}:{Port} (priority={Priority})";
}

/// <summary>
/// Gathers ICE candidates from local interfaces and STUN servers.
/// Implements RFC 8445 ICE-lite subset.
/// </summary>
public sealed class IceLiteCandidateGatherer : IDisposable
{
    private readonly StunClient _stunClient;
    private readonly List<IPEndPoint> _turnServers;
    private readonly TimeSpan _gatherTimeout;
    private bool _disposed;

    public IceLiteCandidateGatherer(
        StunClient? stunClient = null,
        IEnumerable<IPEndPoint>? turnServers = null,
        TimeSpan? gatherTimeout = null)
    {
        _stunClient = stunClient ?? new StunClient();
        _turnServers = turnServers?.ToList() ?? new();
        _gatherTimeout = gatherTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Gathers all available ICE candidates.
    /// </summary>
    public async Task<IceGatheringResult> GatherCandidatesAsync(CancellationToken ct = default)
    {
        var result = new IceGatheringResult();
        var candidates = new List<IceCandidate>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_gatherTimeout);

        // 1. Gather host candidates from local interfaces
        var hostCandidates = GatherHostCandidates();
        candidates.AddRange(hostCandidates);
        result.HostCandidatesGathered = hostCandidates.Count;

        // 2. Gather server reflexive candidates via STUN
        try
        {
            var publicEndpoint = await _stunClient.DiscoverAsync(cts.Token);
            if (publicEndpoint != null)
            {
                var srflxCandidate = new IceCandidate
                {
                    Type = IceCandidateType.ServerReflexive,
                    Address = publicEndpoint.Address,
                    Port = publicEndpoint.Port,
                    Priority = IceCandidate.CalculatePriority(IceCandidateType.ServerReflexive, 65535, 1),
                    Foundation = $"srflx-{publicEndpoint.Address}",
                    RelatedAddress = hostCandidates.FirstOrDefault()?.Address,
                    RelatedPort = hostCandidates.FirstOrDefault()?.Port ?? 0
                };
                candidates.Add(srflxCandidate);
                result.ServerReflexiveCandidatesGathered = 1;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout - continue with host candidates
        }

        // 3. Gather relay candidates from TURN servers
        foreach (var turnServer in _turnServers)
        {
            try
            {
                var relayCandidate = await AllocateTurnRelayAsync(turnServer, cts.Token);
                if (relayCandidate != null)
                {
                    candidates.Add(relayCandidate);
                    result.RelayedCandidatesGathered++;
                }
            }
            catch
            {
                // TURN allocation failed - continue
            }
        }

        // Sort by priority
        result.Candidates = candidates.OrderByDescending(c => c.Priority).ToList();
        result.Success = result.Candidates.Count > 0;

        return result;
    }

    private List<IceCandidate> GatherHostCandidates()
    {
        var candidates = new List<IceCandidate>();
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        int localPref = 65535;
        foreach (var iface in interfaces)
        {
            var props = iface.GetIPProperties();
            foreach (var unicast in props.UnicastAddresses)
            {
                // Skip link-local and loopback
                if (unicast.Address.IsIPv6LinkLocal) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;

                var candidate = new IceCandidate
                {
                    Type = IceCandidateType.Host,
                    Address = unicast.Address,
                    Port = 0, // Will be assigned when socket is bound
                    Priority = IceCandidate.CalculatePriority(IceCandidateType.Host, localPref--, 1),
                    Foundation = $"host-{iface.Id}-{unicast.Address.AddressFamily}"
                };
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private async Task<IceCandidate?> AllocateTurnRelayAsync(IPEndPoint turnServer, CancellationToken ct)
    {
        // Simplified TURN allocation - in production use full TURN protocol
        using var udpClient = new UdpClient();
        udpClient.Connect(turnServer);

        // Send TURN Allocate request (simplified)
        var allocateRequest = new byte[20];
        allocateRequest[0] = 0x00; // TURN Allocate
        allocateRequest[1] = 0x03;
        await udpClient.SendAsync(allocateRequest, 20);

        var response = await udpClient.ReceiveAsync(ct);

        // Parse response (simplified - assume success with relay address in response)
        if (response.Buffer.Length >= 12)
        {
            return new IceCandidate
            {
                Type = IceCandidateType.Relayed,
                Address = turnServer.Address,
                Port = turnServer.Port + 1, // Simplified
                Priority = IceCandidate.CalculatePriority(IceCandidateType.Relayed, 65535, 1),
                Foundation = $"relay-{turnServer.Address}",
                RelatedAddress = ((IPEndPoint?)udpClient.Client.LocalEndPoint)?.Address,
                RelatedPort = ((IPEndPoint?)udpClient.Client.LocalEndPoint)?.Port ?? 0
            };
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stunClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Result of ICE candidate gathering.
/// </summary>
public sealed class IceGatheringResult
{
    public bool Success { get; set; }
    public List<IceCandidate> Candidates { get; set; } = new();
    public int HostCandidatesGathered { get; set; }
    public int ServerReflexiveCandidatesGathered { get; set; }
    public int RelayedCandidatesGathered { get; set; }
    public int TotalCandidates => Candidates.Count;
}

#endregion

#region Connectivity Checker

/// <summary>
/// ICE candidate pair for connectivity checking.
/// </summary>
public sealed class CandidatePair
{
    public IceCandidate LocalCandidate { get; set; } = new();
    public IceCandidate RemoteCandidate { get; set; } = new();
    public CandidatePairState State { get; set; } = CandidatePairState.Frozen;
    public ulong Priority { get; set; }
    public TimeSpan? Rtt { get; set; }
    public DateTimeOffset? SucceededAt { get; set; }
    public int FailCount { get; set; }

    /// <summary>
    /// Calculates pair priority per RFC 8445.
    /// </summary>
    public static ulong CalculatePriority(uint localPriority, uint remotePriority, bool controlling)
    {
        var g = controlling ? localPriority : remotePriority;
        var d = controlling ? remotePriority : localPriority;
        return ((ulong)Math.Min(g, d) << 32) + (2 * (ulong)Math.Max(g, d)) + (g > d ? 1ul : 0ul);
    }
}

public enum CandidatePairState
{
    Frozen,
    Waiting,
    InProgress,
    Succeeded,
    Failed
}

/// <summary>
/// Performs connectivity checks on ICE candidate pairs.
/// </summary>
public sealed class ConnectivityChecker
{
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _checkTimeout;
    private readonly int _maxRetries;

    public ConnectivityChecker(
        TimeSpan? checkInterval = null,
        TimeSpan? checkTimeout = null,
        int maxRetries = 3)
    {
        _checkInterval = checkInterval ?? TimeSpan.FromMilliseconds(50);
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(2);
        _maxRetries = maxRetries;
    }

    /// <summary>
    /// Performs connectivity checks on candidate pairs in priority order.
    /// </summary>
    public async Task<ConnectivityCheckResult> CheckConnectivityAsync(
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        bool controlling,
        CancellationToken ct = default)
    {
        var result = new ConnectivityCheckResult();

        // Form candidate pairs
        var pairs = FormCandidatePairs(localCandidates, remoteCandidates, controlling);
        result.TotalPairsChecked = pairs.Count;

        // Check pairs in priority order
        foreach (var pair in pairs.OrderByDescending(p => p.Priority))
        {
            if (ct.IsCancellationRequested) break;

            pair.State = CandidatePairState.InProgress;

            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                var checkResult = await PerformConnectivityCheckAsync(pair, ct);

                if (checkResult.Success)
                {
                    pair.State = CandidatePairState.Succeeded;
                    pair.Rtt = checkResult.Rtt;
                    pair.SucceededAt = DateTimeOffset.UtcNow;

                    result.SuccessfulPairs.Add(pair);

                    // First successful pair becomes nominated
                    if (result.NominatedPair == null)
                    {
                        result.NominatedPair = pair;
                    }

                    break;
                }
                else
                {
                    pair.FailCount++;
                    await Task.Delay(_checkInterval, ct);
                }
            }

            if (pair.State != CandidatePairState.Succeeded)
            {
                pair.State = CandidatePairState.Failed;
                result.FailedPairs.Add(pair);
            }
        }

        result.Success = result.NominatedPair != null;
        return result;
    }

    private List<CandidatePair> FormCandidatePairs(
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        bool controlling)
    {
        var pairs = new List<CandidatePair>();

        foreach (var local in localCandidates)
        {
            foreach (var remote in remoteCandidates)
            {
                // Only pair same address family
                if (local.Address.AddressFamily != remote.Address.AddressFamily)
                    continue;

                var pair = new CandidatePair
                {
                    LocalCandidate = local,
                    RemoteCandidate = remote,
                    Priority = CandidatePair.CalculatePriority(local.Priority, remote.Priority, controlling)
                };
                pairs.Add(pair);
            }
        }

        return pairs;
    }

    private async Task<SingleCheckResult> PerformConnectivityCheckAsync(
        CandidatePair pair,
        CancellationToken ct)
    {
        var result = new SingleCheckResult();
        var sw = Stopwatch.StartNew();

        try
        {
            using var udpClient = new UdpClient();

            // Bind to local candidate's address if specified
            if (pair.LocalCandidate.Port > 0)
            {
                udpClient.Client.Bind(pair.LocalCandidate.ToEndPoint());
            }

            // Build STUN Binding Request with appropriate attributes
            var transactionId = new byte[12];
            Random.Shared.NextBytes(transactionId);

            var request = BuildBindingRequest(transactionId);

            // Send to remote candidate
            await udpClient.SendAsync(request, request.Length, pair.RemoteCandidate.ToEndPoint());

            // Wait for response
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_checkTimeout);

            var response = await udpClient.ReceiveAsync(cts.Token);
            sw.Stop();

            // Validate response
            if (ValidateBindingResponse(response.Buffer, transactionId))
            {
                result.Success = true;
                result.Rtt = sw.Elapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout
        }
        catch (SocketException)
        {
            // Network error
        }

        return result;
    }

    private static byte[] BuildBindingRequest(byte[] transactionId)
    {
        var request = new byte[20];
        // Message Type: Binding Request (0x0001)
        request[0] = 0x00;
        request[1] = 0x01;
        // Message Length: 0
        request[2] = 0x00;
        request[3] = 0x00;
        // Magic Cookie
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;
        // Transaction ID
        Array.Copy(transactionId, 0, request, 8, 12);
        return request;
    }

    private static bool ValidateBindingResponse(byte[] response, byte[] expectedTransactionId)
    {
        if (response.Length < 20) return false;

        // Check message type (Binding Response = 0x0101)
        if (response[0] != 0x01 || response[1] != 0x01) return false;

        // Check magic cookie
        if (response[4] != 0x21 || response[5] != 0x12 ||
            response[6] != 0xA4 || response[7] != 0x42) return false;

        // Check transaction ID
        for (int i = 0; i < 12; i++)
        {
            if (response[8 + i] != expectedTransactionId[i]) return false;
        }

        return true;
    }

    private sealed class SingleCheckResult
    {
        public bool Success { get; set; }
        public TimeSpan? Rtt { get; set; }
    }
}

/// <summary>
/// Result of connectivity checks.
/// </summary>
public sealed class ConnectivityCheckResult
{
    public bool Success { get; set; }
    public CandidatePair? NominatedPair { get; set; }
    public List<CandidatePair> SuccessfulPairs { get; } = new();
    public List<CandidatePair> FailedPairs { get; } = new();
    public int TotalPairsChecked { get; set; }
}

#endregion

#region Relay Fallback Chain

/// <summary>
/// Relay server with metrics.
/// </summary>
public sealed class RelayServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public IPEndPoint Endpoint { get; set; } = new(IPAddress.Any, 0);
    public TimeSpan Latency { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int CurrentLoad { get; set; }
    public int MaxCapacity { get; set; } = 1000;
    public string Region { get; set; } = "unknown";
    public DateTimeOffset LastChecked { get; set; }
    public int FailureCount { get; set; }

    public float LoadPercent => MaxCapacity > 0 ? (float)CurrentLoad / MaxCapacity * 100 : 100;
}

/// <summary>
/// Manages relay fallback with latency-based selection.
/// </summary>
public sealed class RelayFallbackChain
{
    private readonly ConcurrentDictionary<string, RelayServer> _relays = new();
    private readonly TimeSpan _healthCheckInterval;
    private readonly int _maxFailuresBeforeRemoval;
    private Timer? _healthCheckTimer;

    public event EventHandler<RelayEventArgs>? RelaySelected;
    public event EventHandler<RelayEventArgs>? RelayFailed;

    public RelayFallbackChain(
        TimeSpan? healthCheckInterval = null,
        int maxFailuresBeforeRemoval = 3)
    {
        _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromMinutes(1);
        _maxFailuresBeforeRemoval = maxFailuresBeforeRemoval;
    }

    /// <summary>
    /// Starts health checking of relay servers.
    /// </summary>
    public void StartHealthChecks()
    {
        _healthCheckTimer = new Timer(_ => CheckRelayHealthAsync(), null,
            TimeSpan.Zero, _healthCheckInterval);
    }

    /// <summary>
    /// Adds a relay server to the chain.
    /// </summary>
    public void AddRelay(RelayServer relay)
    {
        _relays[relay.Id] = relay;
    }

    /// <summary>
    /// Removes a relay server from the chain.
    /// </summary>
    public void RemoveRelay(string relayId)
    {
        _relays.TryRemove(relayId, out _);
    }

    /// <summary>
    /// Selects the best available relay based on latency and load.
    /// </summary>
    public RelayServer? SelectBestRelay(string? preferredRegion = null)
    {
        var available = _relays.Values
            .Where(r => r.IsAvailable && r.LoadPercent < 90)
            .ToList();

        if (available.Count == 0) return null;

        // Score = 1/latency * (1 - load%) * region_bonus
        var scored = available.Select(r => new
        {
            Relay = r,
            Score = (1.0 / Math.Max(1, r.Latency.TotalMilliseconds)) *
                    (1 - r.LoadPercent / 100) *
                    (r.Region == preferredRegion ? 1.5 : 1.0)
        });

        var best = scored.OrderByDescending(s => s.Score).First().Relay;
        RelaySelected?.Invoke(this, new RelayEventArgs { Relay = best });
        return best;
    }

    /// <summary>
    /// Gets all relays sorted by preference.
    /// </summary>
    public IReadOnlyList<RelayServer> GetRelaysByPreference(string? preferredRegion = null)
    {
        return _relays.Values
            .Where(r => r.IsAvailable)
            .OrderBy(r => r.Region != preferredRegion)
            .ThenBy(r => r.Latency)
            .ThenBy(r => r.LoadPercent)
            .ToList();
    }

    /// <summary>
    /// Attempts connection through relay chain until success.
    /// </summary>
    public async Task<RelayConnectionResult> ConnectWithFallbackAsync(
        NodeId targetNode,
        string? preferredRegion = null,
        CancellationToken ct = default)
    {
        var relays = GetRelaysByPreference(preferredRegion);

        foreach (var relay in relays)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(relay.Endpoint.Address, relay.Endpoint.Port, ct);

                return new RelayConnectionResult
                {
                    Success = true,
                    Relay = relay,
                    Connection = new RelayConnection(client, NodeId.FromHex(relay.Id), targetNode)
                };
            }
            catch
            {
                relay.FailureCount++;
                relay.IsAvailable = relay.FailureCount < _maxFailuresBeforeRemoval;
                RelayFailed?.Invoke(this, new RelayEventArgs { Relay = relay });
            }
        }

        return new RelayConnectionResult
        {
            Success = false,
            ErrorMessage = "All relay servers failed"
        };
    }

    private async void CheckRelayHealthAsync()
    {
        foreach (var relay in _relays.Values)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(relay.Endpoint.Address, relay.Endpoint.Port, cts.Token);

                sw.Stop();
                relay.Latency = sw.Elapsed;
                relay.IsAvailable = true;
                relay.FailureCount = 0;
                relay.LastChecked = DateTimeOffset.UtcNow;
            }
            catch
            {
                relay.FailureCount++;
                relay.IsAvailable = relay.FailureCount < _maxFailuresBeforeRemoval;
                relay.LastChecked = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Gets statistics about the relay chain.
    /// </summary>
    public RelayChainStats GetStats()
    {
        var relays = _relays.Values.ToList();
        return new RelayChainStats
        {
            TotalRelays = relays.Count,
            AvailableRelays = relays.Count(r => r.IsAvailable),
            AverageLatencyMs = relays.Where(r => r.IsAvailable).Select(r => r.Latency.TotalMilliseconds).DefaultIfEmpty(0).Average(),
            TotalLoad = relays.Sum(r => r.CurrentLoad),
            TotalCapacity = relays.Sum(r => r.MaxCapacity)
        };
    }
}

public sealed class RelayConnectionResult
{
    public bool Success { get; set; }
    public RelayServer? Relay { get; set; }
    public RelayConnection? Connection { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class RelayEventArgs : EventArgs
{
    public RelayServer? Relay { get; set; }
}

public sealed class RelayChainStats
{
    public int TotalRelays { get; set; }
    public int AvailableRelays { get; set; }
    public double AverageLatencyMs { get; set; }
    public int TotalLoad { get; set; }
    public int TotalCapacity { get; set; }
}

#endregion

#region Link Quality Monitor

/// <summary>
/// Quality metrics for a P2P link.
/// </summary>
public sealed class LinkQualityMetrics
{
    public NodeId RemoteNodeId { get; set; }
    public IPEndPoint Endpoint { get; set; } = new(IPAddress.Any, 0);
    public TraversalMethod ConnectionMethod { get; set; }

    // Latency metrics
    public TimeSpan CurrentRtt { get; set; }
    public TimeSpan AverageRtt { get; set; }
    public TimeSpan MinRtt { get; set; } = TimeSpan.MaxValue;
    public TimeSpan MaxRtt { get; set; }

    // Jitter metrics
    public TimeSpan Jitter { get; set; }
    public TimeSpan AverageJitter { get; set; }

    // Packet loss
    public int PacketsSent { get; set; }
    public int PacketsReceived { get; set; }
    public int PacketsLost { get; set; }
    public float PacketLossPercent => PacketsSent > 0 ? (float)PacketsLost / PacketsSent * 100 : 0;

    // Bandwidth estimation
    public long EstimatedBandwidthBps { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    // Timestamps
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset LastMeasurement { get; set; }

    /// <summary>
    /// Overall quality score (0-100).
    /// </summary>
    public int QualityScore
    {
        get
        {
            // Score based on latency, jitter, and packet loss
            var latencyScore = Math.Max(0, 100 - (int)AverageRtt.TotalMilliseconds / 2);
            var jitterScore = Math.Max(0, 100 - (int)AverageJitter.TotalMilliseconds * 5);
            var lossScore = Math.Max(0, 100 - (int)PacketLossPercent * 10);

            return (latencyScore + jitterScore + lossScore) / 3;
        }
    }

    /// <summary>
    /// Quality tier based on score.
    /// </summary>
    public LinkQualityTier QualityTier => QualityScore switch
    {
        >= 80 => LinkQualityTier.Excellent,
        >= 60 => LinkQualityTier.Good,
        >= 40 => LinkQualityTier.Fair,
        >= 20 => LinkQualityTier.Poor,
        _ => LinkQualityTier.Critical
    };
}

public enum LinkQualityTier
{
    Critical = 0,
    Poor = 1,
    Fair = 2,
    Good = 3,
    Excellent = 4
}

/// <summary>
/// Monitors quality metrics for P2P connections.
/// </summary>
public sealed class LinkQualityMonitor : IDisposable
{
    private readonly ConcurrentDictionary<NodeId, LinkQualityMetrics> _metrics = new();
    private readonly ConcurrentDictionary<NodeId, UdpClient> _probeClients = new();
    private readonly TimeSpan _probeInterval;
    private readonly int _rttWindowSize;
    private readonly ConcurrentDictionary<NodeId, Queue<TimeSpan>> _rttHistory = new();
    private Timer? _probeTimer;
    private bool _disposed;

    public event EventHandler<LinkQualityEventArgs>? QualityChanged;
    public event EventHandler<LinkQualityEventArgs>? LinkDegraded;

    public LinkQualityMonitor(
        TimeSpan? probeInterval = null,
        int rttWindowSize = 20)
    {
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(1);
        _rttWindowSize = rttWindowSize;
    }

    /// <summary>
    /// Starts monitoring a connection.
    /// </summary>
    public void StartMonitoring(NodeId remoteNodeId, IPEndPoint endpoint, TraversalMethod method)
    {
        var metrics = new LinkQualityMetrics
        {
            RemoteNodeId = remoteNodeId,
            Endpoint = endpoint,
            ConnectionMethod = method,
            ConnectedAt = DateTimeOffset.UtcNow
        };

        _metrics[remoteNodeId] = metrics;
        _rttHistory[remoteNodeId] = new Queue<TimeSpan>();

        var probeClient = new UdpClient();
        _probeClients[remoteNodeId] = probeClient;

        // Start probing if not already running
        _probeTimer ??= new Timer(_ => ProbeAllConnections(), null, TimeSpan.Zero, _probeInterval);
    }

    /// <summary>
    /// Stops monitoring a connection.
    /// </summary>
    public void StopMonitoring(NodeId remoteNodeId)
    {
        _metrics.TryRemove(remoteNodeId, out _);
        _rttHistory.TryRemove(remoteNodeId, out _);

        if (_probeClients.TryRemove(remoteNodeId, out var client))
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Gets current metrics for a connection.
    /// </summary>
    public LinkQualityMetrics? GetMetrics(NodeId remoteNodeId)
    {
        return _metrics.GetValueOrDefault(remoteNodeId);
    }

    /// <summary>
    /// Gets all monitored connections.
    /// </summary>
    public IReadOnlyList<LinkQualityMetrics> GetAllMetrics()
    {
        return _metrics.Values.ToList();
    }

    /// <summary>
    /// Records a successful data transfer.
    /// </summary>
    public void RecordTransfer(NodeId remoteNodeId, long bytesSent, long bytesReceived, TimeSpan rtt)
    {
        if (!_metrics.TryGetValue(remoteNodeId, out var metrics)) return;

        metrics.BytesSent += bytesSent;
        metrics.BytesReceived += bytesReceived;
        metrics.PacketsSent++;
        metrics.PacketsReceived++;

        UpdateRtt(remoteNodeId, metrics, rtt);
    }

    /// <summary>
    /// Records a failed/timed-out transmission.
    /// </summary>
    public void RecordLoss(NodeId remoteNodeId)
    {
        if (!_metrics.TryGetValue(remoteNodeId, out var metrics)) return;

        metrics.PacketsSent++;
        metrics.PacketsLost++;
    }

    private void ProbeAllConnections()
    {
        foreach (var (nodeId, client) in _probeClients)
        {
            if (!_metrics.TryGetValue(nodeId, out var metrics)) continue;

            _ = ProbeConnectionAsync(nodeId, client, metrics);
        }
    }

    private async Task ProbeConnectionAsync(NodeId nodeId, UdpClient client, LinkQualityMetrics metrics)
    {
        try
        {
            // Build probe packet
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var probe = new byte[16];
            probe[0] = 0x44; // 'D'
            probe[1] = 0x57; // 'W'
            probe[2] = 0x51; // 'Q' (Quality probe)
            probe[3] = 0x50; // 'P'
            BitConverter.TryWriteBytes(probe.AsSpan(4), timestamp);

            var sw = Stopwatch.StartNew();
            await client.SendAsync(probe, probe.Length, metrics.Endpoint);

            using var cts = new CancellationTokenSource(_probeInterval);
            var response = await client.ReceiveAsync(cts.Token);
            sw.Stop();

            metrics.PacketsSent++;
            metrics.PacketsReceived++;
            UpdateRtt(nodeId, metrics, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            metrics.PacketsSent++;
            metrics.PacketsLost++;
        }
        catch (SocketException)
        {
            metrics.PacketsSent++;
            metrics.PacketsLost++;
        }

        metrics.LastMeasurement = DateTimeOffset.UtcNow;

        // Check for quality degradation
        var previousTier = metrics.QualityTier;
        if (metrics.QualityTier < previousTier)
        {
            LinkDegraded?.Invoke(this, new LinkQualityEventArgs { Metrics = metrics });
        }
    }

    private void UpdateRtt(NodeId nodeId, LinkQualityMetrics metrics, TimeSpan rtt)
    {
        var previousRtt = metrics.CurrentRtt;
        metrics.CurrentRtt = rtt;

        if (rtt < metrics.MinRtt) metrics.MinRtt = rtt;
        if (rtt > metrics.MaxRtt) metrics.MaxRtt = rtt;

        // Update jitter (variation in RTT)
        if (previousRtt > TimeSpan.Zero)
        {
            var jitterSample = TimeSpan.FromTicks(Math.Abs(rtt.Ticks - previousRtt.Ticks));
            metrics.Jitter = jitterSample;
        }

        // Maintain RTT history for averages
        if (_rttHistory.TryGetValue(nodeId, out var history))
        {
            history.Enqueue(rtt);
            while (history.Count > _rttWindowSize)
            {
                history.Dequeue();
            }

            metrics.AverageRtt = TimeSpan.FromTicks((long)history.Average(t => t.Ticks));

            // Calculate average jitter
            if (history.Count > 1)
            {
                var rtts = history.ToList();
                var jitters = new List<long>();
                for (int i = 1; i < rtts.Count; i++)
                {
                    jitters.Add(Math.Abs(rtts[i].Ticks - rtts[i - 1].Ticks));
                }
                metrics.AverageJitter = TimeSpan.FromTicks((long)jitters.Average());
            }
        }

        // Estimate bandwidth (simplified - based on RTT)
        // In production, would use actual throughput measurement
        var bandwidthBps = rtt.TotalMilliseconds > 0
            ? (long)(1_000_000 / rtt.TotalMilliseconds * 1024)
            : 0;
        metrics.EstimatedBandwidthBps = (metrics.EstimatedBandwidthBps + bandwidthBps) / 2;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _probeTimer?.Dispose();
            foreach (var client in _probeClients.Values)
            {
                client.Dispose();
            }
            _probeClients.Clear();
            _disposed = true;
        }
    }
}

public sealed class LinkQualityEventArgs : EventArgs
{
    public LinkQualityMetrics? Metrics { get; set; }
}

#endregion
