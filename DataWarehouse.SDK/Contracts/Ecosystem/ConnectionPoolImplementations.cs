// Phase 89: Ecosystem Compatibility - Connection Pool Implementations (ECOS-18)
// TCP, gRPC, and HTTP/2 pool implementations with shared base class

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

#region Pool Entry and Base

/// <summary>
/// Internal tracking entry for a pooled connection.
/// </summary>
/// <typeparam name="TConnection">The connection type.</typeparam>
internal sealed class PoolEntry<TConnection>
{
    public TConnection Connection { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastUsedAt { get; set; }
    public bool IsHealthy { get; set; } = true;

    public PoolEntry(TConnection connection, DateTimeOffset createdAt)
    {
        Connection = connection;
        CreatedAt = createdAt;
        LastUsedAt = createdAt;
    }
}

/// <summary>
/// Wraps a pool entry as an IPooledConnection, returning to pool on dispose.
/// </summary>
/// <typeparam name="TConnection">The connection type.</typeparam>
internal sealed class PooledConnectionLease<TConnection> : IPooledConnection<TConnection>
{
    private readonly ConnectionPoolBase<TConnection> _pool;
    private readonly PoolEntry<TConnection> _entry;
    private readonly string _nodeId;
    private int _disposed;

    public TConnection Connection => _entry.Connection;
    public DateTimeOffset AcquiredAt { get; }
    public DateTimeOffset CreatedAt => _entry.CreatedAt;
    public bool IsHealthy => _entry.IsHealthy;

    internal PoolEntry<TConnection> Entry => _entry;
    internal string NodeId => _nodeId;

    public PooledConnectionLease(
        ConnectionPoolBase<TConnection> pool,
        PoolEntry<TConnection> entry,
        string nodeId)
    {
        _pool = pool;
        _entry = entry;
        _nodeId = nodeId;
        AcquiredAt = DateTimeOffset.UtcNow;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            await _pool.ReleaseAsync(this).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Abstract base class implementing core connection pooling logic with per-node limits,
/// background health checking, and idle eviction.
/// </summary>
/// <typeparam name="TConnection">The connection type managed by this pool.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public abstract class ConnectionPoolBase<TConnection> : IConnectionPool<TConnection>
{
    private readonly IConnectionFactory<TConnection> _factory;
    private readonly ConnectionPoolOptions _options;
    private readonly ConcurrentDictionary<string, ConcurrentBag<PoolEntry<TConnection>>> _idlePools = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeGates = new();
    private readonly SemaphoreSlim _globalGate;
    private readonly Timer _healthCheckTimer;
    private readonly Timer _idleEvictionTimer;
    private int _connectionsCreated;
    private int _connectionsDestroyed;
    private int _failedHealthChecks;
    private DateTimeOffset _lastHealthCheck;
    private int _disposed;

    /// <summary>
    /// Initializes the connection pool base with the given factory and options.
    /// </summary>
    /// <param name="factory">Factory for creating/validating/destroying connections.</param>
    /// <param name="options">Pool configuration options.</param>
    protected ConnectionPoolBase(IConnectionFactory<TConnection> factory, ConnectionPoolOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new ConnectionPoolOptions();
        _globalGate = new SemaphoreSlim(_options.MaxTotalConnections, _options.MaxTotalConnections);

        _healthCheckTimer = new Timer(
            _ => _ = RunHealthChecksAsync(),
            null,
            _options.HealthCheckInterval,
            _options.HealthCheckInterval);

        _idleEvictionTimer = new Timer(
            _ => _ = RunIdleEvictionAsync(),
            null,
            _options.IdleTimeout,
            TimeSpan.FromSeconds(Math.Max(30, _options.IdleTimeout.TotalSeconds / 2)));
    }

    /// <inheritdoc />
    public async ValueTask<IPooledConnection<TConnection>> AcquireAsync(string nodeId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var nodeGate = _nodeGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections));
        var idlePool = _idlePools.GetOrAdd(nodeId, _ => new ConcurrentBag<PoolEntry<TConnection>>());

        // Try to get an idle connection first
        while (idlePool.TryTake(out var entry))
        {
            // Check max lifetime
            if (DateTimeOffset.UtcNow - entry.CreatedAt > _options.MaxLifetime)
            {
                await DestroyEntryAsync(entry).ConfigureAwait(false);
                continue;
            }

            // Optionally validate on acquire
            if (_options.ValidateOnAcquire)
            {
                var healthy = false;
                try
                {
                    healthy = await _factory.ValidateAsync(entry.Connection, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Validation threw - treat as unhealthy
                }

                if (!healthy)
                {
                    entry.IsHealthy = false;
                    await DestroyEntryAsync(entry).ConfigureAwait(false);
                    continue;
                }
            }

            entry.IsHealthy = true;
            entry.LastUsedAt = DateTimeOffset.UtcNow;
            return new PooledConnectionLease<TConnection>(this, entry, nodeId);
        }

        // No idle connection available - create a new one
        // Wait for both per-node and global capacity
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.AcquireTimeout);

        bool nodeAcquired = false;
        bool globalAcquired = false;

        try
        {
            nodeAcquired = await nodeGate.WaitAsync(_options.AcquireTimeout, timeoutCts.Token).ConfigureAwait(false);
            if (!nodeAcquired)
            {
                throw new TimeoutException($"Timed out waiting for per-node connection capacity for node '{nodeId}'.");
            }

            globalAcquired = await _globalGate.WaitAsync(_options.AcquireTimeout, timeoutCts.Token).ConfigureAwait(false);
            if (!globalAcquired)
            {
                throw new TimeoutException("Timed out waiting for global connection pool capacity.");
            }

            var connection = await _factory.CreateAsync(nodeId, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _connectionsCreated);
            var newEntry = new PoolEntry<TConnection>(connection, DateTimeOffset.UtcNow);

            // Transfer semaphore ownership to the lease (released on return)
            nodeAcquired = false;
            globalAcquired = false;

            return new PooledConnectionLease<TConnection>(this, newEntry, nodeId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out acquiring connection for node '{nodeId}'.");
        }
        finally
        {
            if (nodeAcquired) nodeGate.Release();
            if (globalAcquired) _globalGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(IPooledConnection<TConnection> connection, CancellationToken ct = default)
    {
        if (connection is not PooledConnectionLease<TConnection> lease)
        {
            return;
        }

        var entry = lease.Entry;
        var nodeId = lease.NodeId;

        // Check if connection should be destroyed (max lifetime exceeded or unhealthy)
        bool shouldDestroy = DateTimeOffset.UtcNow - entry.CreatedAt > _options.MaxLifetime;

        if (!shouldDestroy && _options.ValidateOnRelease)
        {
            try
            {
                shouldDestroy = !await _factory.ValidateAsync(entry.Connection, ct).ConfigureAwait(false);
            }
            catch
            {
                shouldDestroy = true;
            }
        }

        if (shouldDestroy || _disposed != 0)
        {
            await DestroyEntryAsync(entry).ConfigureAwait(false);
            ReleaseCapacity(nodeId);
            return;
        }

        // Return to idle pool
        entry.LastUsedAt = DateTimeOffset.UtcNow;
        entry.IsHealthy = true;
        var idlePool = _idlePools.GetOrAdd(nodeId, _ => new ConcurrentBag<PoolEntry<TConnection>>());
        idlePool.Add(entry);

        // Release semaphore capacity
        ReleaseCapacity(nodeId);
    }

    /// <inheritdoc />
    public ValueTask<PoolHealthReport> GetHealthAsync(CancellationToken ct = default)
    {
        var perNode = new Dictionary<string, NodePoolStatus>();
        int totalIdle = 0;
        int totalActive = 0;

        foreach (var kvp in _idlePools)
        {
            var idle = kvp.Value.Count;
            totalIdle += idle;

            var nodeGate = _nodeGates.GetOrAdd(kvp.Key, _ => new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections));
            var active = _options.MaxConnections - nodeGate.CurrentCount;
            totalActive += active;

            perNode[kvp.Key] = new NodePoolStatus
            {
                NodeId = kvp.Key,
                Active = active,
                Idle = idle,
                Pending = 0,
                Healthy = true
            };
        }

        var report = new PoolHealthReport
        {
            TotalConnections = totalActive + totalIdle,
            ActiveConnections = totalActive,
            IdleConnections = totalIdle,
            FailedHealthChecks = Volatile.Read(ref _failedHealthChecks),
            ConnectionsCreated = Volatile.Read(ref _connectionsCreated),
            ConnectionsDestroyed = Volatile.Read(ref _connectionsDestroyed),
            PerNodeStatus = perNode,
            LastHealthCheck = _lastHealthCheck
        };

        return new ValueTask<PoolHealthReport>(report);
    }

    /// <inheritdoc />
    public async ValueTask DrainAsync(CancellationToken ct = default)
    {
        foreach (var kvp in _idlePools)
        {
            while (kvp.Value.TryTake(out var entry))
            {
                await DestroyEntryAsync(entry).ConfigureAwait(false);
            }
        }

        _idlePools.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DrainNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (_idlePools.TryRemove(nodeId, out var bag))
        {
            while (bag.TryTake(out var entry))
            {
                await DestroyEntryAsync(entry).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        await _healthCheckTimer.DisposeAsync().ConfigureAwait(false);
        await _idleEvictionTimer.DisposeAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        _globalGate.Dispose();

        foreach (var gate in _nodeGates.Values)
        {
            gate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void ReleaseCapacity(string nodeId)
    {
        if (_nodeGates.TryGetValue(nodeId, out var nodeGate))
        {
            try { nodeGate.Release(); } catch (SemaphoreFullException) { }
        }

        try { _globalGate.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task DestroyEntryAsync(PoolEntry<TConnection> entry)
    {
        try
        {
            await _factory.DestroyAsync(entry.Connection, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best effort destruction
        }

        Interlocked.Increment(ref _connectionsDestroyed);
    }

    private async Task RunHealthChecksAsync()
    {
        foreach (var kvp in _idlePools)
        {
            var healthy = new ConcurrentBag<PoolEntry<TConnection>>();

            while (kvp.Value.TryTake(out var entry))
            {
                try
                {
                    var isHealthy = await _factory.ValidateAsync(entry.Connection, CancellationToken.None).ConfigureAwait(false);
                    if (isHealthy && DateTimeOffset.UtcNow - entry.CreatedAt <= _options.MaxLifetime)
                    {
                        entry.IsHealthy = true;
                        healthy.Add(entry);
                    }
                    else
                    {
                        entry.IsHealthy = false;
                        Interlocked.Increment(ref _failedHealthChecks);
                        await DestroyEntryAsync(entry).ConfigureAwait(false);
                    }
                }
                catch
                {
                    entry.IsHealthy = false;
                    Interlocked.Increment(ref _failedHealthChecks);
                    await DestroyEntryAsync(entry).ConfigureAwait(false);
                }
            }

            // Re-add healthy entries
            foreach (var entry in healthy)
            {
                kvp.Value.Add(entry);
            }
        }

        _lastHealthCheck = DateTimeOffset.UtcNow;
    }

    private async Task RunIdleEvictionAsync()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _idlePools)
        {
            var keep = new ConcurrentBag<PoolEntry<TConnection>>();
            int keptCount = 0;

            while (kvp.Value.TryTake(out var entry))
            {
                bool isExpired = now - entry.LastUsedAt > _options.IdleTimeout;
                bool isOverLifetime = now - entry.CreatedAt > _options.MaxLifetime;
                bool aboveMin = keptCount >= _options.MinConnections;

                if ((isExpired || isOverLifetime) && aboveMin)
                {
                    await DestroyEntryAsync(entry).ConfigureAwait(false);
                }
                else
                {
                    keep.Add(entry);
                    keptCount++;
                }
            }

            // Re-add kept entries
            foreach (var entry in keep)
            {
                kvp.Value.Add(entry);
            }
        }
    }
}

#endregion

#region TCP Connection Pool

/// <summary>
/// Wraps a TCP Socket and NetworkStream as a poolable connection.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed class TcpPooledStream
{
    /// <summary>
    /// Gets the underlying socket.
    /// </summary>
    public Socket Socket { get; }

    /// <summary>
    /// Gets the network stream for read/write operations.
    /// </summary>
    public NetworkStream Stream { get; }

    /// <summary>
    /// Gets the remote endpoint string (host:port).
    /// </summary>
    public string Endpoint { get; }

    internal TcpPooledStream(Socket socket, NetworkStream stream, string endpoint)
    {
        Socket = socket;
        Stream = stream;
        Endpoint = endpoint;
    }
}

/// <summary>
/// Connection pool for TCP socket connections. Used for Raft, replication, and fabric inter-node communication.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed class TcpConnectionPool : ConnectionPoolBase<TcpPooledStream>
{
    /// <summary>
    /// Creates a new TCP connection pool with the specified options.
    /// </summary>
    /// <param name="options">Pool configuration options.</param>
    public TcpConnectionPool(ConnectionPoolOptions? options = null)
        : base(new TcpConnectionFactory(), options)
    {
    }

    private sealed class TcpConnectionFactory : IConnectionFactory<TcpPooledStream>
    {
        public async ValueTask<TcpPooledStream> CreateAsync(string endpoint, CancellationToken ct = default)
        {
            var parts = endpoint.Split(':', 2);
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 9100;

            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            socket.DualMode = true;
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            try
            {
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: false);
                return new TcpPooledStream(socket, stream, endpoint);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async ValueTask<bool> ValidateAsync(TcpPooledStream connection, CancellationToken ct = default)
        {
            try
            {
                if (!connection.Socket.Connected) return false;

                // Poll for readability with zero timeout to check if socket is still alive
                // If Poll returns true with zero bytes available, the connection is closed
                bool readable = connection.Socket.Poll(0, SelectMode.SelectRead);
                if (readable && connection.Socket.Available == 0) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public ValueTask DestroyAsync(TcpPooledStream connection, CancellationToken ct = default)
        {
            try
            {
                connection.Stream.Dispose();
                connection.Socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Best effort
            }
            finally
            {
                connection.Socket.Dispose();
            }

            return default;
        }
    }
}

#endregion

#region gRPC Channel Pool

/// <summary>
/// Represents a logical gRPC channel for pooling. Wraps endpoint metadata and a channel identifier
/// since actual Grpc.Net.Client is an external dependency.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed class GrpcPooledChannel
{
    /// <summary>
    /// Gets the target endpoint for this gRPC channel.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// Gets the unique channel identifier.
    /// </summary>
    public Guid ChannelId { get; }

    /// <summary>
    /// Gets or sets the channel state. True indicates the channel is considered ready.
    /// </summary>
    public bool IsReady { get; set; } = true;

    /// <summary>
    /// Gets when this channel was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    internal GrpcPooledChannel(string endpoint)
    {
        Endpoint = endpoint;
        ChannelId = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Connection pool for logical gRPC channels. Used by client SDKs for streaming and unary RPC connections.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed class GrpcChannelPool : ConnectionPoolBase<GrpcPooledChannel>
{
    /// <summary>
    /// Creates a new gRPC channel pool with the specified options.
    /// </summary>
    /// <param name="options">Pool configuration options.</param>
    public GrpcChannelPool(ConnectionPoolOptions? options = null)
        : base(new GrpcChannelFactory(), options)
    {
    }

    private sealed class GrpcChannelFactory : IConnectionFactory<GrpcPooledChannel>
    {
        public ValueTask<GrpcPooledChannel> CreateAsync(string endpoint, CancellationToken ct = default)
        {
            var channel = new GrpcPooledChannel(endpoint);
            return new ValueTask<GrpcPooledChannel>(channel);
        }

        public ValueTask<bool> ValidateAsync(GrpcPooledChannel connection, CancellationToken ct = default)
        {
            // Channel is healthy if it's still marked ready and not expired
            return new ValueTask<bool>(connection.IsReady);
        }

        public ValueTask DestroyAsync(GrpcPooledChannel connection, CancellationToken ct = default)
        {
            connection.IsReady = false;
            return default;
        }
    }
}

#endregion

#region HTTP/2 Connection Pool

/// <summary>
/// Wraps an HttpClient configured for HTTP/2 as a poolable connection.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed class Http2PooledConnection
{
    /// <summary>
    /// Gets the underlying HttpClient configured for HTTP/2.
    /// </summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Gets the base endpoint URI for this connection.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// Gets when this connection was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    internal Http2PooledConnection(HttpClient client, string endpoint)
    {
        Client = client;
        Endpoint = endpoint;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Connection pool for HTTP/2 connections. Used for REST APIs and Arrow Flight connections.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed class Http2ConnectionPool : ConnectionPoolBase<Http2PooledConnection>
{
    /// <summary>
    /// Creates a new HTTP/2 connection pool with the specified options.
    /// </summary>
    /// <param name="options">Pool configuration options.</param>
    public Http2ConnectionPool(ConnectionPoolOptions? options = null)
        : base(new Http2ConnectionFactory(), options)
    {
    }

    private sealed class Http2ConnectionFactory : IConnectionFactory<Http2PooledConnection>
    {
        public ValueTask<Http2PooledConnection> CreateAsync(string endpoint, CancellationToken ct = default)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromHours(1),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                EnableMultipleHttp2Connections = true
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? endpoint
                    : $"https://{endpoint}"),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            return new ValueTask<Http2PooledConnection>(new Http2PooledConnection(client, endpoint));
        }

        public async ValueTask<bool> ValidateAsync(Http2PooledConnection connection, CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "/");
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

                using var response = await connection.Client.SendAsync(request, ct).ConfigureAwait(false);
                return true; // Any response means the connection is alive
            }
            catch
            {
                return false;
            }
        }

        public ValueTask DestroyAsync(Http2PooledConnection connection, CancellationToken ct = default)
        {
            connection.Client.Dispose();
            return default;
        }
    }
}

#endregion
