using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.LoadBalancer
{
    #region Enums

    /// <summary>
    /// Supported load balancing algorithms for request distribution.
    /// </summary>
    public enum LoadBalancingAlgorithm
    {
        /// <summary>
        /// Distributes requests sequentially across backends in order.
        /// Simple and effective for uniform request processing times.
        /// </summary>
        RoundRobin,

        /// <summary>
        /// Routes requests to the backend with fewest active connections.
        /// Best for varying request processing times.
        /// </summary>
        LeastConnections,

        /// <summary>
        /// Distributes requests based on assigned backend weights.
        /// Useful when backends have different capacities.
        /// </summary>
        Weighted,

        /// <summary>
        /// Uses consistent hashing for session affinity without explicit sticky sessions.
        /// Minimizes redistribution when backends are added/removed.
        /// </summary>
        ConsistentHash,

        /// <summary>
        /// Combines least connections with backend weights.
        /// Weighted(connections) = connections / weight.
        /// </summary>
        WeightedLeastConnections,

        /// <summary>
        /// Randomly selects a backend for each request.
        /// Simple but may not distribute load evenly.
        /// </summary>
        Random,

        /// <summary>
        /// Routes based on IP hash for client affinity.
        /// Same client IP always goes to the same backend.
        /// </summary>
        IpHash,

        /// <summary>
        /// Routes to backend with lowest response time.
        /// Requires response time tracking.
        /// </summary>
        LeastResponseTime
    }

    /// <summary>
    /// Health status of a backend server.
    /// </summary>
    public enum BackendHealthStatus
    {
        /// <summary>
        /// Backend is healthy and accepting requests.
        /// </summary>
        Healthy,

        /// <summary>
        /// Backend is experiencing issues but still operational.
        /// </summary>
        Degraded,

        /// <summary>
        /// Backend has failed health checks and is not receiving traffic.
        /// </summary>
        Unhealthy,

        /// <summary>
        /// Backend health status is unknown (not yet checked).
        /// </summary>
        Unknown,

        /// <summary>
        /// Backend is intentionally taken offline for maintenance.
        /// </summary>
        Maintenance
    }

    /// <summary>
    /// Type of health check to perform.
    /// </summary>
    public enum HealthCheckType
    {
        /// <summary>
        /// TCP connection check only.
        /// </summary>
        Tcp,

        /// <summary>
        /// HTTP/HTTPS request with status code validation.
        /// </summary>
        Http,

        /// <summary>
        /// HTTP/HTTPS request with response body validation.
        /// </summary>
        HttpContent,

        /// <summary>
        /// gRPC health check protocol.
        /// </summary>
        Grpc,

        /// <summary>
        /// Custom health check via callback.
        /// </summary>
        Custom
    }

    /// <summary>
    /// Connection state in the pool.
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>
        /// Connection is available for use.
        /// </summary>
        Available,

        /// <summary>
        /// Connection is currently in use.
        /// </summary>
        InUse,

        /// <summary>
        /// Connection is being validated.
        /// </summary>
        Validating,

        /// <summary>
        /// Connection is closed.
        /// </summary>
        Closed
    }

    #endregion

    #region Supporting Types

    /// <summary>
    /// Represents a backend server in the load balancer pool.
    /// </summary>
    public class Backend
    {
        /// <summary>
        /// Unique identifier for this backend.
        /// </summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Display name for the backend.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Host address (IP or hostname).
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// Port number for the backend service.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Weight for weighted load balancing (higher = more traffic).
        /// Default is 100.
        /// </summary>
        public int Weight { get; set; } = 100;

        /// <summary>
        /// Maximum number of concurrent connections allowed.
        /// </summary>
        public int MaxConnections { get; set; } = 1000;

        /// <summary>
        /// Current health status of the backend.
        /// </summary>
        public BackendHealthStatus HealthStatus { get; set; } = BackendHealthStatus.Unknown;

        /// <summary>
        /// Whether this backend is enabled for receiving traffic.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Custom metadata for the backend.
        /// </summary>
        public Dictionary<string, string> Metadata { get; init; } = new();

        /// <summary>
        /// When this backend was added to the pool.
        /// </summary>
        public DateTime AddedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// When the last health check was performed.
        /// </summary>
        public DateTime? LastHealthCheck { get; set; }

        /// <summary>
        /// When the backend last transitioned to healthy state.
        /// </summary>
        public DateTime? LastHealthyAt { get; set; }

        /// <summary>
        /// Number of consecutive failed health checks.
        /// </summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>
        /// Number of consecutive successful health checks.
        /// </summary>
        public int ConsecutiveSuccesses { get; set; }

        /// <summary>
        /// Gets the full address of the backend.
        /// </summary>
        public string Address => $"{Host}:{Port}";

        /// <summary>
        /// Whether the backend can receive traffic.
        /// </summary>
        public bool CanReceiveTraffic => IsEnabled &&
            (HealthStatus == BackendHealthStatus.Healthy || HealthStatus == BackendHealthStatus.Degraded);
    }

    /// <summary>
    /// Configuration for health checks.
    /// </summary>
    public class HealthCheckConfig
    {
        /// <summary>
        /// Type of health check to perform.
        /// </summary>
        public HealthCheckType Type { get; set; } = HealthCheckType.Tcp;

        /// <summary>
        /// Interval between health checks.
        /// </summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Timeout for individual health checks.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Number of consecutive failures before marking unhealthy.
        /// </summary>
        public int UnhealthyThreshold { get; set; } = 3;

        /// <summary>
        /// Number of consecutive successes before marking healthy.
        /// </summary>
        public int HealthyThreshold { get; set; } = 2;

        /// <summary>
        /// Path for HTTP health checks.
        /// </summary>
        public string HttpPath { get; set; } = "/health";

        /// <summary>
        /// HTTP method for health checks.
        /// </summary>
        public string HttpMethod { get; set; } = "GET";

        /// <summary>
        /// Expected HTTP status codes (comma-separated, e.g., "200,204").
        /// </summary>
        public string ExpectedStatusCodes { get; set; } = "200";

        /// <summary>
        /// Expected content in HTTP response body (for HttpContent type).
        /// </summary>
        public string? ExpectedContent { get; set; }

        /// <summary>
        /// Whether to follow redirects during HTTP health checks.
        /// </summary>
        public bool FollowRedirects { get; set; }

        /// <summary>
        /// Custom headers to send with HTTP health checks.
        /// </summary>
        public Dictionary<string, string> Headers { get; init; } = new();
    }

    /// <summary>
    /// Result of a health check operation.
    /// </summary>
    public class HealthCheckResult
    {
        /// <summary>
        /// Backend that was checked.
        /// </summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>
        /// Whether the health check passed.
        /// </summary>
        public bool IsHealthy { get; init; }

        /// <summary>
        /// Response time of the health check.
        /// </summary>
        public TimeSpan ResponseTime { get; init; }

        /// <summary>
        /// HTTP status code if applicable.
        /// </summary>
        public int? StatusCode { get; init; }

        /// <summary>
        /// Error message if the check failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// When the check was performed.
        /// </summary>
        public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a pooled connection to a backend.
    /// </summary>
    public class PooledConnection : IDisposable
    {
        private readonly ConnectionPool _pool;
        private bool _disposed;

        /// <summary>
        /// Unique identifier for this connection.
        /// </summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Backend this connection is for.
        /// </summary>
        public Backend Backend { get; }

        /// <summary>
        /// Current state of the connection.
        /// </summary>
        public ConnectionState State { get; internal set; } = ConnectionState.Available;

        /// <summary>
        /// When this connection was created.
        /// </summary>
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        /// <summary>
        /// When this connection was last used.
        /// </summary>
        public DateTime LastUsedAt { get; internal set; } = DateTime.UtcNow;

        /// <summary>
        /// Number of times this connection has been reused.
        /// </summary>
        public int UseCount { get; internal set; }

        /// <summary>
        /// Connection lifetime.
        /// </summary>
        public TimeSpan Age => DateTime.UtcNow - CreatedAt;

        /// <summary>
        /// Time since last use.
        /// </summary>
        public TimeSpan IdleTime => DateTime.UtcNow - LastUsedAt;

        internal PooledConnection(Backend backend, ConnectionPool pool)
        {
            Backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        /// <summary>
        /// Returns the connection to the pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pool.Return(this);
        }
    }

    /// <summary>
    /// Configuration for connection pooling.
    /// </summary>
    public class ConnectionPoolConfig
    {
        /// <summary>
        /// Minimum connections to maintain per backend.
        /// </summary>
        public int MinConnectionsPerBackend { get; set; } = 5;

        /// <summary>
        /// Maximum connections per backend.
        /// </summary>
        public int MaxConnectionsPerBackend { get; set; } = 100;

        /// <summary>
        /// Maximum total connections across all backends.
        /// </summary>
        public int MaxTotalConnections { get; set; } = 10000;

        /// <summary>
        /// Connection idle timeout before eviction.
        /// </summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum connection age before forced recycling.
        /// </summary>
        public TimeSpan MaxConnectionAge { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Timeout for acquiring a connection from the pool.
        /// </summary>
        public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Interval for connection pool maintenance.
        /// </summary>
        public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Whether to validate connections before use.
        /// </summary>
        public bool ValidateOnAcquire { get; set; } = true;
    }

    /// <summary>
    /// Connection pool for managing backend connections.
    /// </summary>
    public class ConnectionPool : IDisposable
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<PooledConnection>> _pools = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
        private readonly ConnectionPoolConfig _config;
        private readonly Timer? _maintenanceTimer;
        private readonly object _statsLock = new();
        private long _totalAcquired;
        private long _totalReleased;
        private long _totalCreated;
        private long _totalEvicted;
        private long _acquireTimeouts;
        private bool _disposed;

        /// <summary>
        /// Creates a new connection pool with the specified configuration.
        /// </summary>
        public ConnectionPool(ConnectionPoolConfig? config = null)
        {
            _config = config ?? new ConnectionPoolConfig();

            if (_config.MaintenanceInterval > TimeSpan.Zero)
            {
                _maintenanceTimer = new Timer(
                    _ => PerformMaintenance(),
                    null,
                    _config.MaintenanceInterval,
                    _config.MaintenanceInterval);
            }
        }

        /// <summary>
        /// Acquires a connection for the specified backend.
        /// </summary>
        public async Task<PooledConnection?> AcquireAsync(Backend backend, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool));
            if (backend == null) throw new ArgumentNullException(nameof(backend));

            var semaphore = _semaphores.GetOrAdd(backend.Id,
                _ => new SemaphoreSlim(_config.MaxConnectionsPerBackend, _config.MaxConnectionsPerBackend));

            if (!await semaphore.WaitAsync(_config.AcquireTimeout, ct))
            {
                Interlocked.Increment(ref _acquireTimeouts);
                return null;
            }

            try
            {
                var pool = _pools.GetOrAdd(backend.Id, _ => new ConcurrentBag<PooledConnection>());

                // Try to get an existing connection
                while (pool.TryTake(out var connection))
                {
                    if (IsConnectionValid(connection))
                    {
                        connection.State = ConnectionState.InUse;
                        connection.LastUsedAt = DateTime.UtcNow;
                        connection.UseCount++;
                        Interlocked.Increment(ref _totalAcquired);
                        return connection;
                    }
                    // Connection invalid, let it be garbage collected
                    Interlocked.Increment(ref _totalEvicted);
                }

                // Create new connection
                var newConnection = new PooledConnection(backend, this)
                {
                    State = ConnectionState.InUse
                };
                Interlocked.Increment(ref _totalCreated);
                Interlocked.Increment(ref _totalAcquired);
                return newConnection;
            }
            catch
            {
                semaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// Returns a connection to the pool.
        /// </summary>
        internal void Return(PooledConnection connection)
        {
            if (_disposed || connection == null) return;

            var semaphore = _semaphores.GetOrAdd(connection.Backend.Id,
                _ => new SemaphoreSlim(_config.MaxConnectionsPerBackend, _config.MaxConnectionsPerBackend));

            try
            {
                if (IsConnectionValid(connection))
                {
                    connection.State = ConnectionState.Available;
                    connection.LastUsedAt = DateTime.UtcNow;

                    var pool = _pools.GetOrAdd(connection.Backend.Id, _ => new ConcurrentBag<PooledConnection>());
                    pool.Add(connection);
                    Interlocked.Increment(ref _totalReleased);
                }
                else
                {
                    connection.State = ConnectionState.Closed;
                    Interlocked.Increment(ref _totalEvicted);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Gets pool statistics.
        /// </summary>
        public ConnectionPoolStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                var totalPooled = _pools.Values.Sum(p => p.Count);

                return new ConnectionPoolStatistics
                {
                    TotalPooledConnections = totalPooled,
                    TotalAcquired = Interlocked.Read(ref _totalAcquired),
                    TotalReleased = Interlocked.Read(ref _totalReleased),
                    TotalCreated = Interlocked.Read(ref _totalCreated),
                    TotalEvicted = Interlocked.Read(ref _totalEvicted),
                    AcquireTimeouts = Interlocked.Read(ref _acquireTimeouts),
                    PoolCountByBackend = _pools.ToDictionary(p => p.Key, p => p.Value.Count)
                };
            }
        }

        private bool IsConnectionValid(PooledConnection connection)
        {
            if (connection.State == ConnectionState.Closed) return false;
            if (connection.Age > _config.MaxConnectionAge) return false;
            if (connection.IdleTime > _config.IdleTimeout) return false;
            return true;
        }

        private void PerformMaintenance()
        {
            if (_disposed) return;

            foreach (var kvp in _pools)
            {
                var pool = kvp.Value;
                var validConnections = new ConcurrentBag<PooledConnection>();

                while (pool.TryTake(out var connection))
                {
                    if (IsConnectionValid(connection))
                    {
                        validConnections.Add(connection);
                    }
                    else
                    {
                        connection.State = ConnectionState.Closed;
                        Interlocked.Increment(ref _totalEvicted);
                    }
                }

                foreach (var conn in validConnections)
                {
                    pool.Add(conn);
                }
            }
        }

        /// <summary>
        /// Disposes the connection pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _maintenanceTimer?.Dispose();

            foreach (var pool in _pools.Values)
            {
                while (pool.TryTake(out var connection))
                {
                    connection.State = ConnectionState.Closed;
                }
            }

            foreach (var semaphore in _semaphores.Values)
            {
                semaphore.Dispose();
            }

            _pools.Clear();
            _semaphores.Clear();
        }
    }

    /// <summary>
    /// Connection pool statistics.
    /// </summary>
    public class ConnectionPoolStatistics
    {
        /// <summary>
        /// Total connections currently in the pool.
        /// </summary>
        public int TotalPooledConnections { get; init; }

        /// <summary>
        /// Total connections acquired from the pool.
        /// </summary>
        public long TotalAcquired { get; init; }

        /// <summary>
        /// Total connections returned to the pool.
        /// </summary>
        public long TotalReleased { get; init; }

        /// <summary>
        /// Total connections created.
        /// </summary>
        public long TotalCreated { get; init; }

        /// <summary>
        /// Total connections evicted due to age/idle timeout.
        /// </summary>
        public long TotalEvicted { get; init; }

        /// <summary>
        /// Number of acquire timeouts.
        /// </summary>
        public long AcquireTimeouts { get; init; }

        /// <summary>
        /// Pool counts by backend ID.
        /// </summary>
        public Dictionary<string, int> PoolCountByBackend { get; init; } = new();

        /// <summary>
        /// Pool hit rate (reused connections / total acquired).
        /// </summary>
        public double HitRate => TotalAcquired > 0
            ? (double)(TotalAcquired - TotalCreated) / TotalAcquired
            : 0;
    }

    /// <summary>
    /// Configuration for sticky sessions.
    /// </summary>
    public class StickySessionConfig
    {
        /// <summary>
        /// Whether sticky sessions are enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Name of the cookie for session affinity.
        /// </summary>
        public string CookieName { get; set; } = "DWLB_SESSION";

        /// <summary>
        /// Session timeout duration.
        /// </summary>
        public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Whether to re-route if the target backend is unhealthy.
        /// </summary>
        public bool FailoverOnUnhealthy { get; set; } = true;

        /// <summary>
        /// Maximum number of active sessions to track.
        /// </summary>
        public int MaxSessions { get; set; } = 100000;

        /// <summary>
        /// Hash algorithm for session key generation.
        /// </summary>
        public string HashAlgorithm { get; set; } = "SHA256";
    }

    /// <summary>
    /// Represents an active sticky session.
    /// </summary>
    public class StickySession
    {
        /// <summary>
        /// Session identifier.
        /// </summary>
        public string SessionId { get; init; } = string.Empty;

        /// <summary>
        /// Backend ID this session is pinned to.
        /// </summary>
        public string BackendId { get; set; } = string.Empty;

        /// <summary>
        /// When the session was created.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// When the session was last accessed.
        /// </summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Number of requests using this session.
        /// </summary>
        public long RequestCount { get; set; }
    }

    /// <summary>
    /// Manages sticky session state.
    /// </summary>
    public class StickySessionManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, StickySession> _sessions = new();
        private readonly StickySessionConfig _config;
        private readonly Timer? _cleanupTimer;
        private bool _disposed;

        /// <summary>
        /// Creates a new sticky session manager.
        /// </summary>
        public StickySessionManager(StickySessionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (_config.Enabled)
            {
                _cleanupTimer = new Timer(
                    _ => CleanupExpiredSessions(),
                    null,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1));
            }
        }

        /// <summary>
        /// Gets or creates a session for the given key.
        /// </summary>
        public StickySession? GetOrCreateSession(string sessionKey, string backendId)
        {
            if (!_config.Enabled || string.IsNullOrEmpty(sessionKey))
                return null;

            return _sessions.AddOrUpdate(
                sessionKey,
                key => new StickySession
                {
                    SessionId = key,
                    BackendId = backendId,
                    RequestCount = 1
                },
                (key, existing) =>
                {
                    existing.LastAccessedAt = DateTime.UtcNow;
                    existing.RequestCount++;
                    return existing;
                });
        }

        /// <summary>
        /// Gets an existing session by key.
        /// </summary>
        public StickySession? GetSession(string sessionKey)
        {
            if (!_config.Enabled || string.IsNullOrEmpty(sessionKey))
                return null;

            if (_sessions.TryGetValue(sessionKey, out var session))
            {
                if (DateTime.UtcNow - session.LastAccessedAt <= _config.SessionTimeout)
                {
                    session.LastAccessedAt = DateTime.UtcNow;
                    session.RequestCount++;
                    return session;
                }

                // Session expired
                _sessions.TryRemove(sessionKey, out _);
            }

            return null;
        }

        /// <summary>
        /// Updates the backend for an existing session.
        /// </summary>
        public void UpdateSessionBackend(string sessionKey, string newBackendId)
        {
            if (_sessions.TryGetValue(sessionKey, out var session))
            {
                session.BackendId = newBackendId;
                session.LastAccessedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Removes a session.
        /// </summary>
        public bool RemoveSession(string sessionKey)
        {
            return _sessions.TryRemove(sessionKey, out _);
        }

        /// <summary>
        /// Gets statistics about sticky sessions.
        /// </summary>
        public StickySessionStatistics GetStatistics()
        {
            var now = DateTime.UtcNow;
            var activeSessions = _sessions.Values
                .Where(s => now - s.LastAccessedAt <= _config.SessionTimeout)
                .ToList();

            return new StickySessionStatistics
            {
                TotalSessions = _sessions.Count,
                ActiveSessions = activeSessions.Count,
                ExpiredSessions = _sessions.Count - activeSessions.Count,
                SessionsByBackend = activeSessions
                    .GroupBy(s => s.BackendId)
                    .ToDictionary(g => g.Key, g => g.Count()),
                TotalRequests = activeSessions.Sum(s => s.RequestCount),
                OldestSession = activeSessions.MinBy(s => s.CreatedAt)?.CreatedAt,
                NewestSession = activeSessions.MaxBy(s => s.CreatedAt)?.CreatedAt
            };
        }

        private void CleanupExpiredSessions()
        {
            if (_disposed) return;

            var now = DateTime.UtcNow;
            var expiredKeys = _sessions
                .Where(kv => now - kv.Value.LastAccessedAt > _config.SessionTimeout)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _sessions.TryRemove(key, out _);
            }

            // Enforce max sessions limit
            if (_sessions.Count > _config.MaxSessions)
            {
                var toRemove = _sessions
                    .OrderBy(kv => kv.Value.LastAccessedAt)
                    .Take(_sessions.Count - _config.MaxSessions)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in toRemove)
                {
                    _sessions.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Disposes the sticky session manager.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer?.Dispose();
            _sessions.Clear();
        }
    }

    /// <summary>
    /// Statistics for sticky sessions.
    /// </summary>
    public class StickySessionStatistics
    {
        /// <summary>
        /// Total sessions tracked (including expired).
        /// </summary>
        public int TotalSessions { get; init; }

        /// <summary>
        /// Number of active (non-expired) sessions.
        /// </summary>
        public int ActiveSessions { get; init; }

        /// <summary>
        /// Number of expired sessions pending cleanup.
        /// </summary>
        public int ExpiredSessions { get; init; }

        /// <summary>
        /// Sessions count by backend ID.
        /// </summary>
        public Dictionary<string, int> SessionsByBackend { get; init; } = new();

        /// <summary>
        /// Total requests across all sessions.
        /// </summary>
        public long TotalRequests { get; init; }

        /// <summary>
        /// Timestamp of the oldest session.
        /// </summary>
        public DateTime? OldestSession { get; init; }

        /// <summary>
        /// Timestamp of the newest session.
        /// </summary>
        public DateTime? NewestSession { get; init; }
    }

    /// <summary>
    /// Request context for load balancing decisions.
    /// </summary>
    public class LoadBalancerRequest
    {
        /// <summary>
        /// Request identifier.
        /// </summary>
        public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Client IP address for IP-based routing.
        /// </summary>
        public string? ClientIp { get; set; }

        /// <summary>
        /// Session key for sticky sessions.
        /// </summary>
        public string? SessionKey { get; set; }

        /// <summary>
        /// Hash key for consistent hashing.
        /// </summary>
        public string? HashKey { get; set; }

        /// <summary>
        /// Request headers.
        /// </summary>
        public Dictionary<string, string> Headers { get; init; } = new();

        /// <summary>
        /// When the request was received.
        /// </summary>
        public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Request priority (higher = more priority).
        /// </summary>
        public int Priority { get; set; }

        /// <summary>
        /// Request timeout override.
        /// </summary>
        public TimeSpan? Timeout { get; set; }
    }

    /// <summary>
    /// Result of a load balancing operation.
    /// </summary>
    public class LoadBalancerResult
    {
        /// <summary>
        /// Whether a backend was successfully selected.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Selected backend (null if no backend available).
        /// </summary>
        public Backend? Backend { get; init; }

        /// <summary>
        /// Pooled connection if connection pooling is enabled.
        /// </summary>
        public PooledConnection? Connection { get; init; }

        /// <summary>
        /// Session ID if sticky sessions are enabled.
        /// </summary>
        public string? SessionId { get; init; }

        /// <summary>
        /// Algorithm used for selection.
        /// </summary>
        public LoadBalancingAlgorithm Algorithm { get; init; }

        /// <summary>
        /// Time taken to select the backend.
        /// </summary>
        public TimeSpan SelectionTime { get; init; }

        /// <summary>
        /// Error message if selection failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static LoadBalancerResult Succeeded(Backend backend, LoadBalancingAlgorithm algorithm, TimeSpan selectionTime, string? sessionId = null, PooledConnection? connection = null)
        {
            return new LoadBalancerResult
            {
                Success = true,
                Backend = backend,
                Algorithm = algorithm,
                SelectionTime = selectionTime,
                SessionId = sessionId,
                Connection = connection
            };
        }

        /// <summary>
        /// Creates a failed result.
        /// </summary>
        public static LoadBalancerResult Failed(string errorMessage, LoadBalancingAlgorithm algorithm, TimeSpan selectionTime)
        {
            return new LoadBalancerResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                Algorithm = algorithm,
                SelectionTime = selectionTime
            };
        }
    }

    /// <summary>
    /// Load balancer metrics and statistics.
    /// </summary>
    public class LoadBalancerMetrics
    {
        /// <summary>
        /// Total requests processed.
        /// </summary>
        public long TotalRequests { get; init; }

        /// <summary>
        /// Successful routing decisions.
        /// </summary>
        public long SuccessfulRoutes { get; init; }

        /// <summary>
        /// Failed routing decisions (no backend available).
        /// </summary>
        public long FailedRoutes { get; init; }

        /// <summary>
        /// Total active connections.
        /// </summary>
        public long ActiveConnections { get; init; }

        /// <summary>
        /// Requests per second (current rate).
        /// </summary>
        public double RequestsPerSecond { get; init; }

        /// <summary>
        /// Average backend selection time.
        /// </summary>
        public TimeSpan AverageSelectionTime { get; init; }

        /// <summary>
        /// Number of healthy backends.
        /// </summary>
        public int HealthyBackends { get; init; }

        /// <summary>
        /// Number of unhealthy backends.
        /// </summary>
        public int UnhealthyBackends { get; init; }

        /// <summary>
        /// Total backends in the pool.
        /// </summary>
        public int TotalBackends { get; init; }

        /// <summary>
        /// Metrics by backend.
        /// </summary>
        public Dictionary<string, BackendMetrics> BackendMetrics { get; init; } = new();

        /// <summary>
        /// Connection pool statistics.
        /// </summary>
        public ConnectionPoolStatistics? PoolStatistics { get; init; }

        /// <summary>
        /// Sticky session statistics.
        /// </summary>
        public StickySessionStatistics? SessionStatistics { get; init; }

        /// <summary>
        /// When these metrics were collected.
        /// </summary>
        public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Metrics for a single backend.
    /// </summary>
    public class BackendMetrics
    {
        /// <summary>
        /// Backend identifier.
        /// </summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>
        /// Total requests routed to this backend.
        /// </summary>
        public long TotalRequests { get; init; }

        /// <summary>
        /// Active connections to this backend.
        /// </summary>
        public int ActiveConnections { get; init; }

        /// <summary>
        /// Average response time.
        /// </summary>
        public TimeSpan AverageResponseTime { get; init; }

        /// <summary>
        /// Success rate (0.0 to 1.0).
        /// </summary>
        public double SuccessRate { get; init; }

        /// <summary>
        /// Current health status.
        /// </summary>
        public BackendHealthStatus HealthStatus { get; init; }

        /// <summary>
        /// Last health check result.
        /// </summary>
        public HealthCheckResult? LastHealthCheck { get; init; }
    }

    /// <summary>
    /// Configuration for the load balancer.
    /// </summary>
    public class LoadBalancerConfig
    {
        /// <summary>
        /// Load balancing algorithm to use.
        /// </summary>
        public LoadBalancingAlgorithm Algorithm { get; set; } = LoadBalancingAlgorithm.RoundRobin;

        /// <summary>
        /// Health check configuration.
        /// </summary>
        public HealthCheckConfig HealthCheck { get; init; } = new();

        /// <summary>
        /// Connection pool configuration.
        /// </summary>
        public ConnectionPoolConfig ConnectionPool { get; init; } = new();

        /// <summary>
        /// Sticky session configuration.
        /// </summary>
        public StickySessionConfig StickySession { get; init; } = new();

        /// <summary>
        /// Number of virtual nodes per backend for consistent hashing.
        /// </summary>
        public int ConsistentHashVirtualNodes { get; set; } = 150;

        /// <summary>
        /// Whether to enable connection pooling.
        /// </summary>
        public bool EnableConnectionPooling { get; set; } = true;

        /// <summary>
        /// Request timeout.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum request queue length.
        /// </summary>
        public int MaxQueueLength { get; set; } = 10000;

        /// <summary>
        /// Whether to retry on backend failure.
        /// </summary>
        public bool RetryOnFailure { get; set; } = true;

        /// <summary>
        /// Maximum retry attempts.
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>
        /// Delay between retries.
        /// </summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    }

    #endregion

    #region Consistent Hash Ring

    /// <summary>
    /// Consistent hash ring for backend selection.
    /// Implements ketama-style consistent hashing with virtual nodes.
    /// </summary>
    internal sealed class ConsistentHashRing
    {
        private readonly SortedDictionary<uint, string> _ring = new();
        private readonly Dictionary<string, int> _backendVirtualNodes = new();
        private readonly int _defaultVirtualNodes;
        private readonly object _lock = new();
        private uint[]? _sortedKeys;

        public ConsistentHashRing(int defaultVirtualNodes = 150)
        {
            _defaultVirtualNodes = defaultVirtualNodes;
        }

        public void AddBackend(string backendId, int? virtualNodes = null)
        {
            var vnodes = virtualNodes ?? _defaultVirtualNodes;

            lock (_lock)
            {
                if (_backendVirtualNodes.ContainsKey(backendId))
                    return;

                for (int i = 0; i < vnodes; i++)
                {
                    var key = $"{backendId}:{i}";
                    var hash = ComputeHash(key);
                    _ring[hash] = backendId;
                }

                _backendVirtualNodes[backendId] = vnodes;
                _sortedKeys = null; // Invalidate cache
            }
        }

        public void RemoveBackend(string backendId)
        {
            lock (_lock)
            {
                if (!_backendVirtualNodes.TryGetValue(backendId, out var vnodes))
                    return;

                for (int i = 0; i < vnodes; i++)
                {
                    var key = $"{backendId}:{i}";
                    var hash = ComputeHash(key);
                    _ring.Remove(hash);
                }

                _backendVirtualNodes.Remove(backendId);
                _sortedKeys = null; // Invalidate cache
            }
        }

        public string? GetBackend(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            lock (_lock)
            {
                if (_ring.Count == 0)
                    return null;

                var hash = ComputeHash(key);

                // Ensure sorted keys cache is valid
                if (_sortedKeys == null)
                {
                    _sortedKeys = _ring.Keys.ToArray();
                }

                // Binary search for the first key >= hash
                var index = Array.BinarySearch(_sortedKeys, hash);

                if (index < 0)
                {
                    index = ~index; // Get insertion point
                }

                if (index >= _sortedKeys.Length)
                {
                    index = 0; // Wrap around
                }

                return _ring[_sortedKeys[index]];
            }
        }

        private static uint ComputeHash(string key)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
            return BitConverter.ToUInt32(bytes, 0);
        }
    }

    #endregion

    #region Load Balancer Plugin

    /// <summary>
    /// Production-ready load balancer plugin implementing request distribution across backends.
    ///
    /// Features:
    /// - Multiple load balancing algorithms (round-robin, least-connections, weighted, consistent-hash)
    /// - Backend health checking with configurable intervals and thresholds
    /// - Connection pooling for efficient connection reuse
    /// - Sticky sessions for session affinity
    /// - Comprehensive metrics and statistics
    /// - Graceful backend addition/removal
    /// - Request routing policies
    ///
    /// Message Commands:
    /// - lb.route: Route a request to an available backend
    /// - lb.add_backend: Add a new backend to the pool
    /// - lb.remove_backend: Remove a backend from the pool
    /// - lb.set_backend_weight: Update backend weight
    /// - lb.enable_backend: Enable a backend for traffic
    /// - lb.disable_backend: Disable a backend (maintenance mode)
    /// - lb.get_backends: List all backends and their status
    /// - lb.get_metrics: Get load balancer metrics
    /// - lb.set_algorithm: Change the load balancing algorithm
    /// - lb.health_check: Trigger immediate health check
    /// </summary>
    public sealed class LoadBalancerPlugin : FeaturePluginBase
    {
        private readonly ConcurrentDictionary<string, Backend> _backends = new();
        private readonly ConcurrentDictionary<string, BackendRuntimeStats> _backendStats = new();
        private readonly ConsistentHashRing _hashRing;
        private readonly ConnectionPool? _connectionPool;
        private readonly StickySessionManager? _sessionManager;
        private readonly LoadBalancerConfig _config;
        private readonly Timer? _healthCheckTimer;
        private readonly Timer? _metricsTimer;
        private readonly HttpClient _healthCheckClient;
        private readonly object _roundRobinLock = new();
        private readonly object _metricsLock = new();
        private int _roundRobinIndex;
        private long _totalRequests;
        private long _successfulRoutes;
        private long _failedRoutes;
        private readonly Queue<DateTime> _requestTimestamps = new();
        private readonly List<double> _selectionTimes = new();
        private CancellationTokenSource? _cts;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.resilience.loadbalancer";

        /// <inheritdoc/>
        public override string Name => "Load Balancer Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Creates a new LoadBalancerPlugin with default configuration.
        /// </summary>
        public LoadBalancerPlugin() : this(new LoadBalancerConfig())
        {
        }

        /// <summary>
        /// Creates a new LoadBalancerPlugin with the specified configuration.
        /// </summary>
        /// <param name="config">Load balancer configuration.</param>
        public LoadBalancerPlugin(LoadBalancerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _hashRing = new ConsistentHashRing(_config.ConsistentHashVirtualNodes);

            if (_config.EnableConnectionPooling)
            {
                _connectionPool = new ConnectionPool(_config.ConnectionPool);
            }

            if (_config.StickySession.Enabled)
            {
                _sessionManager = new StickySessionManager(_config.StickySession);
            }

            _healthCheckClient = new HttpClient
            {
                Timeout = _config.HealthCheck.Timeout
            };

            // Initialize timers but don't start them until StartAsync
            _healthCheckTimer = new Timer(
                _ => Task.Run(() => PerformHealthChecksAsync()),
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            _metricsTimer = new Timer(
                _ => CleanupMetrics(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            return await base.OnHandshakeAsync(request);
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Start health check timer
            _healthCheckTimer?.Change(
                _config.HealthCheck.Interval,
                _config.HealthCheck.Interval);

            // Start metrics cleanup timer
            _metricsTimer?.Change(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));

            // Perform initial health check
            await PerformHealthChecksAsync();
        }

        /// <inheritdoc/>
        public override Task StopAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();

            _healthCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _metricsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _healthCheckTimer?.Dispose();
            _metricsTimer?.Dispose();

            _connectionPool?.Dispose();
            _sessionManager?.Dispose();
            _healthCheckClient.Dispose();

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "lb.route", DisplayName = "Route Request", Description = "Route a request to an available backend" },
                new() { Name = "lb.add_backend", DisplayName = "Add Backend", Description = "Add a new backend to the pool" },
                new() { Name = "lb.remove_backend", DisplayName = "Remove Backend", Description = "Remove a backend from the pool" },
                new() { Name = "lb.set_backend_weight", DisplayName = "Set Weight", Description = "Update backend weight" },
                new() { Name = "lb.enable_backend", DisplayName = "Enable Backend", Description = "Enable a backend for traffic" },
                new() { Name = "lb.disable_backend", DisplayName = "Disable Backend", Description = "Disable a backend" },
                new() { Name = "lb.get_backends", DisplayName = "List Backends", Description = "List all backends and their status" },
                new() { Name = "lb.get_metrics", DisplayName = "Get Metrics", Description = "Get load balancer metrics" },
                new() { Name = "lb.set_algorithm", DisplayName = "Set Algorithm", Description = "Change load balancing algorithm" },
                new() { Name = "lb.health_check", DisplayName = "Health Check", Description = "Trigger immediate health check" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "LoadBalancer";
            metadata["Algorithm"] = _config.Algorithm.ToString();
            metadata["BackendCount"] = _backends.Count;
            metadata["HealthyBackends"] = _backends.Values.Count(b => b.HealthStatus == BackendHealthStatus.Healthy);
            metadata["ConnectionPoolingEnabled"] = _config.EnableConnectionPooling;
            metadata["StickySessionsEnabled"] = _config.StickySession.Enabled;
            metadata["TotalRequests"] = Interlocked.Read(ref _totalRequests);
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "lb.route":
                    await HandleRouteAsync(message);
                    break;
                case "lb.add_backend":
                    HandleAddBackend(message);
                    break;
                case "lb.remove_backend":
                    HandleRemoveBackend(message);
                    break;
                case "lb.set_backend_weight":
                    HandleSetBackendWeight(message);
                    break;
                case "lb.enable_backend":
                    HandleEnableBackend(message);
                    break;
                case "lb.disable_backend":
                    HandleDisableBackend(message);
                    break;
                case "lb.get_backends":
                    HandleGetBackends(message);
                    break;
                case "lb.get_metrics":
                    HandleGetMetrics(message);
                    break;
                case "lb.set_algorithm":
                    HandleSetAlgorithm(message);
                    break;
                case "lb.health_check":
                    await HandleHealthCheckAsync(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        #region Public API

        /// <summary>
        /// Adds a new backend to the load balancer pool.
        /// </summary>
        /// <param name="backend">Backend to add.</param>
        /// <returns>True if added, false if already exists.</returns>
        public bool AddBackend(Backend backend)
        {
            if (backend == null) throw new ArgumentNullException(nameof(backend));

            if (_backends.TryAdd(backend.Id, backend))
            {
                _backendStats[backend.Id] = new BackendRuntimeStats();
                _hashRing.AddBackend(backend.Id, backend.Weight);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes a backend from the load balancer pool.
        /// </summary>
        /// <param name="backendId">Backend ID to remove.</param>
        /// <returns>True if removed, false if not found.</returns>
        public bool RemoveBackend(string backendId)
        {
            if (string.IsNullOrEmpty(backendId)) return false;

            if (_backends.TryRemove(backendId, out _))
            {
                _backendStats.TryRemove(backendId, out _);
                _hashRing.RemoveBackend(backendId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a backend by ID.
        /// </summary>
        public Backend? GetBackend(string backendId)
        {
            if (string.IsNullOrEmpty(backendId)) return null;
            _backends.TryGetValue(backendId, out var backend);
            return backend;
        }

        /// <summary>
        /// Gets all backends.
        /// </summary>
        public IReadOnlyList<Backend> GetAllBackends()
        {
            return _backends.Values.ToList();
        }

        /// <summary>
        /// Routes a request to an available backend.
        /// </summary>
        /// <param name="request">Request context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Routing result with selected backend.</returns>
        public async Task<LoadBalancerResult> RouteRequestAsync(LoadBalancerRequest request, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _totalRequests);

            try
            {
                // Check for sticky session
                if (_sessionManager != null && !string.IsNullOrEmpty(request.SessionKey))
                {
                    var session = _sessionManager.GetSession(request.SessionKey);
                    if (session != null && _backends.TryGetValue(session.BackendId, out var stickyBackend))
                    {
                        if (stickyBackend.CanReceiveTraffic)
                        {
                            RecordRequestTimestamp();
                            Interlocked.Increment(ref _successfulRoutes);
                            RecordBackendRequest(stickyBackend.Id);

                            var stickyConnection = _connectionPool != null
                                ? await _connectionPool.AcquireAsync(stickyBackend, ct)
                                : null;

                            sw.Stop();
                            RecordSelectionTime(sw.Elapsed.TotalMilliseconds);

                            return LoadBalancerResult.Succeeded(
                                stickyBackend,
                                _config.Algorithm,
                                sw.Elapsed,
                                session.SessionId,
                                stickyConnection);
                        }

                        // Backend unhealthy, failover if configured
                        if (_config.StickySession.FailoverOnUnhealthy)
                        {
                            _sessionManager.RemoveSession(request.SessionKey);
                        }
                        else
                        {
                            sw.Stop();
                            Interlocked.Increment(ref _failedRoutes);
                            return LoadBalancerResult.Failed(
                                $"Sticky session backend '{session.BackendId}' is unhealthy",
                                _config.Algorithm,
                                sw.Elapsed);
                        }
                    }
                }

                // Select backend using configured algorithm
                var selectedBackend = SelectBackend(request);

                if (selectedBackend == null)
                {
                    sw.Stop();
                    Interlocked.Increment(ref _failedRoutes);
                    return LoadBalancerResult.Failed(
                        "No healthy backends available",
                        _config.Algorithm,
                        sw.Elapsed);
                }

                RecordRequestTimestamp();
                Interlocked.Increment(ref _successfulRoutes);
                RecordBackendRequest(selectedBackend.Id);

                // Create sticky session if enabled
                string? sessionId = null;
                if (_sessionManager != null && !string.IsNullOrEmpty(request.SessionKey))
                {
                    var session = _sessionManager.GetOrCreateSession(request.SessionKey, selectedBackend.Id);
                    sessionId = session?.SessionId;
                }

                // Acquire connection from pool if enabled
                PooledConnection? connection = null;
                if (_connectionPool != null)
                {
                    connection = await _connectionPool.AcquireAsync(selectedBackend, ct);
                }

                sw.Stop();
                RecordSelectionTime(sw.Elapsed.TotalMilliseconds);

                return LoadBalancerResult.Succeeded(
                    selectedBackend,
                    _config.Algorithm,
                    sw.Elapsed,
                    sessionId,
                    connection);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Interlocked.Increment(ref _failedRoutes);
                return LoadBalancerResult.Failed(
                    $"Error routing request: {ex.Message}",
                    _config.Algorithm,
                    sw.Elapsed);
            }
        }

        /// <summary>
        /// Gets current load balancer metrics.
        /// </summary>
        public LoadBalancerMetrics GetMetrics()
        {
            var healthyCount = _backends.Values.Count(b => b.HealthStatus == BackendHealthStatus.Healthy);
            var unhealthyCount = _backends.Values.Count(b => b.HealthStatus == BackendHealthStatus.Unhealthy);

            double avgSelectionTime;
            lock (_metricsLock)
            {
                avgSelectionTime = _selectionTimes.Count > 0 ? _selectionTimes.Average() : 0;
            }

            var backendMetrics = new Dictionary<string, BackendMetrics>();
            foreach (var kvp in _backends)
            {
                var backend = kvp.Value;
                _backendStats.TryGetValue(backend.Id, out var stats);

                backendMetrics[backend.Id] = new BackendMetrics
                {
                    BackendId = backend.Id,
                    TotalRequests = stats?.TotalRequests ?? 0,
                    ActiveConnections = stats?.ActiveConnections ?? 0,
                    AverageResponseTime = TimeSpan.FromMilliseconds(stats?.AverageResponseTimeMs ?? 0),
                    SuccessRate = stats?.SuccessRate ?? 0,
                    HealthStatus = backend.HealthStatus
                };
            }

            return new LoadBalancerMetrics
            {
                TotalRequests = Interlocked.Read(ref _totalRequests),
                SuccessfulRoutes = Interlocked.Read(ref _successfulRoutes),
                FailedRoutes = Interlocked.Read(ref _failedRoutes),
                ActiveConnections = _backendStats.Values.Sum(s => s.ActiveConnections),
                RequestsPerSecond = CalculateRequestRate(),
                AverageSelectionTime = TimeSpan.FromMilliseconds(avgSelectionTime),
                HealthyBackends = healthyCount,
                UnhealthyBackends = unhealthyCount,
                TotalBackends = _backends.Count,
                BackendMetrics = backendMetrics,
                PoolStatistics = _connectionPool?.GetStatistics(),
                SessionStatistics = _sessionManager?.GetStatistics()
            };
        }

        /// <summary>
        /// Sets the load balancing algorithm.
        /// </summary>
        public void SetAlgorithm(LoadBalancingAlgorithm algorithm)
        {
            _config.Algorithm = algorithm;
        }

        /// <summary>
        /// Triggers an immediate health check for all backends.
        /// </summary>
        public async Task PerformHealthChecksAsync()
        {
            var tasks = _backends.Values.Select(PerformHealthCheckAsync);
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Performs a health check for a specific backend.
        /// </summary>
        public async Task<HealthCheckResult> PerformHealthCheckAsync(Backend backend)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new HealthCheckResult
            {
                BackendId = backend.Id,
                CheckedAt = DateTime.UtcNow
            };

            try
            {
                var isHealthy = _config.HealthCheck.Type switch
                {
                    HealthCheckType.Tcp => await PerformTcpHealthCheckAsync(backend),
                    HealthCheckType.Http => await PerformHttpHealthCheckAsync(backend),
                    HealthCheckType.HttpContent => await PerformHttpContentHealthCheckAsync(backend),
                    _ => await PerformTcpHealthCheckAsync(backend)
                };

                sw.Stop();

                result = new HealthCheckResult
                {
                    BackendId = backend.Id,
                    IsHealthy = isHealthy,
                    ResponseTime = sw.Elapsed,
                    CheckedAt = DateTime.UtcNow
                };

                UpdateBackendHealth(backend, isHealthy);
            }
            catch (Exception ex)
            {
                sw.Stop();

                result = new HealthCheckResult
                {
                    BackendId = backend.Id,
                    IsHealthy = false,
                    ResponseTime = sw.Elapsed,
                    ErrorMessage = ex.Message,
                    CheckedAt = DateTime.UtcNow
                };

                UpdateBackendHealth(backend, false);
            }

            backend.LastHealthCheck = DateTime.UtcNow;
            return result;
        }

        #endregion

        #region Backend Selection

        private Backend? SelectBackend(LoadBalancerRequest request)
        {
            var availableBackends = _backends.Values
                .Where(b => b.CanReceiveTraffic)
                .ToList();

            if (availableBackends.Count == 0)
                return null;

            return _config.Algorithm switch
            {
                LoadBalancingAlgorithm.RoundRobin => SelectRoundRobin(availableBackends),
                LoadBalancingAlgorithm.LeastConnections => SelectLeastConnections(availableBackends),
                LoadBalancingAlgorithm.Weighted => SelectWeighted(availableBackends),
                LoadBalancingAlgorithm.ConsistentHash => SelectConsistentHash(request, availableBackends),
                LoadBalancingAlgorithm.WeightedLeastConnections => SelectWeightedLeastConnections(availableBackends),
                LoadBalancingAlgorithm.Random => SelectRandom(availableBackends),
                LoadBalancingAlgorithm.IpHash => SelectIpHash(request, availableBackends),
                LoadBalancingAlgorithm.LeastResponseTime => SelectLeastResponseTime(availableBackends),
                _ => SelectRoundRobin(availableBackends)
            };
        }

        private Backend SelectRoundRobin(List<Backend> backends)
        {
            lock (_roundRobinLock)
            {
                _roundRobinIndex = (_roundRobinIndex + 1) % backends.Count;
                return backends[_roundRobinIndex];
            }
        }

        private Backend SelectLeastConnections(List<Backend> backends)
        {
            return backends
                .OrderBy(b => _backendStats.TryGetValue(b.Id, out var stats) ? stats.ActiveConnections : 0)
                .First();
        }

        private Backend SelectWeighted(List<Backend> backends)
        {
            var totalWeight = backends.Sum(b => b.Weight);
            var random = Random.Shared.Next(totalWeight);
            var currentWeight = 0;

            foreach (var backend in backends)
            {
                currentWeight += backend.Weight;
                if (random < currentWeight)
                    return backend;
            }

            return backends.Last();
        }

        private Backend? SelectConsistentHash(LoadBalancerRequest request, List<Backend> backends)
        {
            var hashKey = request.HashKey ?? request.ClientIp ?? request.RequestId;
            var backendId = _hashRing.GetBackend(hashKey);

            if (backendId != null && _backends.TryGetValue(backendId, out var backend) && backend.CanReceiveTraffic)
                return backend;

            // Fallback to round-robin if consistent hash fails
            return SelectRoundRobin(backends);
        }

        private Backend SelectWeightedLeastConnections(List<Backend> backends)
        {
            return backends
                .OrderBy(b =>
                {
                    _backendStats.TryGetValue(b.Id, out var stats);
                    var connections = stats?.ActiveConnections ?? 0;
                    return (double)connections / Math.Max(1, b.Weight);
                })
                .First();
        }

        private Backend SelectRandom(List<Backend> backends)
        {
            return backends[Random.Shared.Next(backends.Count)];
        }

        private Backend? SelectIpHash(LoadBalancerRequest request, List<Backend> backends)
        {
            if (string.IsNullOrEmpty(request.ClientIp))
                return SelectRoundRobin(backends);

            using var md5 = MD5.Create();
            var hash = BitConverter.ToUInt32(md5.ComputeHash(Encoding.UTF8.GetBytes(request.ClientIp)), 0);
            var index = (int)(hash % backends.Count);
            return backends[index];
        }

        private Backend SelectLeastResponseTime(List<Backend> backends)
        {
            return backends
                .OrderBy(b =>
                {
                    _backendStats.TryGetValue(b.Id, out var stats);
                    return stats?.AverageResponseTimeMs ?? double.MaxValue;
                })
                .First();
        }

        #endregion

        #region Health Checks

        private async Task<bool> PerformTcpHealthCheckAsync(Backend backend)
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(_config.HealthCheck.Timeout);

            try
            {
                await client.ConnectAsync(backend.Host, backend.Port, cts.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> PerformHttpHealthCheckAsync(Backend backend)
        {
            var uri = new Uri($"http://{backend.Host}:{backend.Port}{_config.HealthCheck.HttpPath}");
            var request = new HttpRequestMessage(new HttpMethod(_config.HealthCheck.HttpMethod), uri);

            foreach (var header in _config.HealthCheck.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            try
            {
                var response = await _healthCheckClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                var expectedCodes = _config.HealthCheck.ExpectedStatusCodes
                    .Split(',')
                    .Select(s => int.Parse(s.Trim()));

                return expectedCodes.Contains((int)response.StatusCode);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> PerformHttpContentHealthCheckAsync(Backend backend)
        {
            var uri = new Uri($"http://{backend.Host}:{backend.Port}{_config.HealthCheck.HttpPath}");
            var request = new HttpRequestMessage(new HttpMethod(_config.HealthCheck.HttpMethod), uri);

            foreach (var header in _config.HealthCheck.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            try
            {
                var response = await _healthCheckClient.SendAsync(request);

                var expectedCodes = _config.HealthCheck.ExpectedStatusCodes
                    .Split(',')
                    .Select(s => int.Parse(s.Trim()));

                if (!expectedCodes.Contains((int)response.StatusCode))
                    return false;

                if (!string.IsNullOrEmpty(_config.HealthCheck.ExpectedContent))
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return content.Contains(_config.HealthCheck.ExpectedContent);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateBackendHealth(Backend backend, bool isHealthy)
        {
            if (isHealthy)
            {
                backend.ConsecutiveSuccesses++;
                backend.ConsecutiveFailures = 0;

                if (backend.ConsecutiveSuccesses >= _config.HealthCheck.HealthyThreshold)
                {
                    if (backend.HealthStatus != BackendHealthStatus.Healthy)
                    {
                        backend.HealthStatus = BackendHealthStatus.Healthy;
                        backend.LastHealthyAt = DateTime.UtcNow;
                    }
                }
            }
            else
            {
                backend.ConsecutiveFailures++;
                backend.ConsecutiveSuccesses = 0;

                if (backend.ConsecutiveFailures >= _config.HealthCheck.UnhealthyThreshold)
                {
                    backend.HealthStatus = BackendHealthStatus.Unhealthy;
                }
                else if (backend.HealthStatus == BackendHealthStatus.Healthy)
                {
                    backend.HealthStatus = BackendHealthStatus.Degraded;
                }
            }
        }

        #endregion

        #region Metrics

        private void RecordRequestTimestamp()
        {
            lock (_metricsLock)
            {
                _requestTimestamps.Enqueue(DateTime.UtcNow);
            }
        }

        private void RecordSelectionTime(double ms)
        {
            lock (_metricsLock)
            {
                _selectionTimes.Add(ms);
                if (_selectionTimes.Count > 1000)
                {
                    _selectionTimes.RemoveAt(0);
                }
            }
        }

        private void RecordBackendRequest(string backendId)
        {
            var stats = _backendStats.GetOrAdd(backendId, _ => new BackendRuntimeStats());
            Interlocked.Increment(ref stats.TotalRequests);
        }

        private double CalculateRequestRate()
        {
            lock (_metricsLock)
            {
                var cutoff = DateTime.UtcNow.AddSeconds(-10);
                while (_requestTimestamps.Count > 0 && _requestTimestamps.Peek() < cutoff)
                {
                    _requestTimestamps.Dequeue();
                }

                return _requestTimestamps.Count / 10.0;
            }
        }

        private void CleanupMetrics()
        {
            lock (_metricsLock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-5);
                while (_requestTimestamps.Count > 0 && _requestTimestamps.Peek() < cutoff)
                {
                    _requestTimestamps.Dequeue();
                }
            }
        }

        #endregion

        #region Message Handlers

        private async Task HandleRouteAsync(PluginMessage message)
        {
            var request = new LoadBalancerRequest
            {
                ClientIp = GetString(message.Payload, "clientIp"),
                SessionKey = GetString(message.Payload, "sessionKey"),
                HashKey = GetString(message.Payload, "hashKey")
            };

            var result = await RouteRequestAsync(request);

            message.Payload["result"] = new Dictionary<string, object?>
            {
                ["success"] = result.Success,
                ["backendId"] = result.Backend?.Id,
                ["backendAddress"] = result.Backend?.Address,
                ["sessionId"] = result.SessionId,
                ["algorithm"] = result.Algorithm.ToString(),
                ["selectionTimeMs"] = result.SelectionTime.TotalMilliseconds,
                ["errorMessage"] = result.ErrorMessage
            };
        }

        private void HandleAddBackend(PluginMessage message)
        {
            var backend = new Backend
            {
                Id = GetString(message.Payload, "id") ?? Guid.NewGuid().ToString("N"),
                Name = GetString(message.Payload, "name") ?? string.Empty,
                Host = GetString(message.Payload, "host") ?? throw new ArgumentException("host required"),
                Port = GetInt(message.Payload, "port") ?? throw new ArgumentException("port required"),
                Weight = GetInt(message.Payload, "weight") ?? 100,
                MaxConnections = GetInt(message.Payload, "maxConnections") ?? 1000,
                IsEnabled = true
            };

            var added = AddBackend(backend);
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = added,
                ["backendId"] = backend.Id
            };
        }

        private void HandleRemoveBackend(PluginMessage message)
        {
            var backendId = GetString(message.Payload, "backendId") ?? throw new ArgumentException("backendId required");
            var removed = RemoveBackend(backendId);
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = removed
            };
        }

        private void HandleSetBackendWeight(PluginMessage message)
        {
            var backendId = GetString(message.Payload, "backendId") ?? throw new ArgumentException("backendId required");
            var weight = GetInt(message.Payload, "weight") ?? throw new ArgumentException("weight required");

            if (_backends.TryGetValue(backendId, out var backend))
            {
                backend.Weight = weight;
                _hashRing.RemoveBackend(backendId);
                _hashRing.AddBackend(backendId, weight);
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = true };
            }
            else
            {
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = false, ["error"] = "Backend not found" };
            }
        }

        private void HandleEnableBackend(PluginMessage message)
        {
            var backendId = GetString(message.Payload, "backendId") ?? throw new ArgumentException("backendId required");

            if (_backends.TryGetValue(backendId, out var backend))
            {
                backend.IsEnabled = true;
                backend.HealthStatus = BackendHealthStatus.Unknown;
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = true };
            }
            else
            {
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = false, ["error"] = "Backend not found" };
            }
        }

        private void HandleDisableBackend(PluginMessage message)
        {
            var backendId = GetString(message.Payload, "backendId") ?? throw new ArgumentException("backendId required");

            if (_backends.TryGetValue(backendId, out var backend))
            {
                backend.IsEnabled = false;
                backend.HealthStatus = BackendHealthStatus.Maintenance;
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = true };
            }
            else
            {
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = false, ["error"] = "Backend not found" };
            }
        }

        private void HandleGetBackends(PluginMessage message)
        {
            var backends = _backends.Values.Select(b => new Dictionary<string, object?>
            {
                ["id"] = b.Id,
                ["name"] = b.Name,
                ["host"] = b.Host,
                ["port"] = b.Port,
                ["weight"] = b.Weight,
                ["isEnabled"] = b.IsEnabled,
                ["healthStatus"] = b.HealthStatus.ToString(),
                ["lastHealthCheck"] = b.LastHealthCheck,
                ["lastHealthyAt"] = b.LastHealthyAt,
                ["consecutiveFailures"] = b.ConsecutiveFailures,
                ["consecutiveSuccesses"] = b.ConsecutiveSuccesses
            }).ToList();

            message.Payload["result"] = backends;
        }

        private void HandleGetMetrics(PluginMessage message)
        {
            var metrics = GetMetrics();
            message.Payload["result"] = JsonSerializer.Serialize(metrics);
        }

        private void HandleSetAlgorithm(PluginMessage message)
        {
            var algorithmStr = GetString(message.Payload, "algorithm") ?? throw new ArgumentException("algorithm required");

            if (Enum.TryParse<LoadBalancingAlgorithm>(algorithmStr, true, out var algorithm))
            {
                SetAlgorithm(algorithm);
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = true, ["algorithm"] = algorithm.ToString() };
            }
            else
            {
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = false, ["error"] = "Invalid algorithm" };
            }
        }

        private async Task HandleHealthCheckAsync(PluginMessage message)
        {
            var backendId = GetString(message.Payload, "backendId");

            if (!string.IsNullOrEmpty(backendId))
            {
                if (_backends.TryGetValue(backendId, out var backend))
                {
                    var result = await PerformHealthCheckAsync(backend);
                    message.Payload["result"] = new Dictionary<string, object?>
                    {
                        ["backendId"] = result.BackendId,
                        ["isHealthy"] = result.IsHealthy,
                        ["responseTimeMs"] = result.ResponseTime.TotalMilliseconds,
                        ["errorMessage"] = result.ErrorMessage
                    };
                }
                else
                {
                    message.Payload["result"] = new Dictionary<string, object> { ["error"] = "Backend not found" };
                }
            }
            else
            {
                await PerformHealthChecksAsync();
                message.Payload["result"] = new Dictionary<string, object> { ["success"] = true, ["backendsChecked"] = _backends.Count };
            }
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static int? GetInt(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is double d) return (int)d;
                if (val is string s && int.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        #endregion
    }

    /// <summary>
    /// Runtime statistics for a backend.
    /// </summary>
    internal sealed class BackendRuntimeStats
    {
        public long TotalRequests;
        public int ActiveConnections;
        public double AverageResponseTimeMs;
        public double SuccessRate = 1.0;
    }

    #endregion
}
