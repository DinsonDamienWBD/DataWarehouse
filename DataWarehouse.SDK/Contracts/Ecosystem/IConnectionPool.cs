// Phase 89: Ecosystem Compatibility - Connection Pooling Contract (ECOS-18)
// Generic SDK connection pool contract for TCP, gRPC, and HTTP/2 transports

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

/// <summary>
/// Represents a leased connection from a pool. Disposing returns the connection to the pool.
/// </summary>
/// <typeparam name="TConnection">The underlying connection type.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public interface IPooledConnection<out TConnection> : IAsyncDisposable
{
    /// <summary>
    /// Gets the underlying connection.
    /// </summary>
    TConnection Connection { get; }

    /// <summary>
    /// Gets when this lease was acquired from the pool.
    /// </summary>
    DateTimeOffset AcquiredAt { get; }

    /// <summary>
    /// Gets when the underlying connection was originally created.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the current health status of this connection.
    /// </summary>
    bool IsHealthy { get; }
}

/// <summary>
/// Generic connection pool that manages per-node bounded connection pools with health checking.
/// </summary>
/// <typeparam name="TConnection">The underlying connection type.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public interface IConnectionPool<TConnection> : IAsyncDisposable
{
    /// <summary>
    /// Acquires a connection from the pool for the specified node, respecting per-node and global limits.
    /// </summary>
    /// <param name="nodeId">The target node identifier (e.g., host:port).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A pooled connection lease.</returns>
    /// <exception cref="TimeoutException">Thrown when acquire timeout is exceeded.</exception>
    ValueTask<IPooledConnection<TConnection>> AcquireAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Explicitly releases a connection back to the pool. Also handled automatically by disposing the pooled connection.
    /// </summary>
    /// <param name="connection">The pooled connection to release.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ReleaseAsync(IPooledConnection<TConnection> connection, CancellationToken ct = default);

    /// <summary>
    /// Gets a health report for the entire pool including per-node status.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pool health report.</returns>
    ValueTask<PoolHealthReport> GetHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Gracefully drains all connections across all nodes, closing each after it is returned.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask DrainAsync(CancellationToken ct = default);

    /// <summary>
    /// Drains all connections for a specific node.
    /// </summary>
    /// <param name="nodeId">The node to drain connections for.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask DrainNodeAsync(string nodeId, CancellationToken ct = default);
}

/// <summary>
/// Factory for creating, validating, and destroying connections of a specific type.
/// </summary>
/// <typeparam name="TConnection">The connection type.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public interface IConnectionFactory<TConnection>
{
    /// <summary>
    /// Creates a new connection to the specified endpoint.
    /// </summary>
    /// <param name="endpoint">The target endpoint (format depends on transport).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new connection instance.</returns>
    ValueTask<TConnection> CreateAsync(string endpoint, CancellationToken ct = default);

    /// <summary>
    /// Validates that a connection is still healthy and usable.
    /// </summary>
    /// <param name="connection">The connection to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the connection is healthy.</returns>
    ValueTask<bool> ValidateAsync(TConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Destroys a connection, releasing all associated resources.
    /// </summary>
    /// <param name="connection">The connection to destroy.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask DestroyAsync(TConnection connection, CancellationToken ct = default);
}

/// <summary>
/// Configuration options for connection pools.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed record ConnectionPoolOptions
{
    /// <summary>
    /// Minimum idle connections maintained per node. Default: 2.
    /// </summary>
    public int MinConnections { get; init; } = 2;

    /// <summary>
    /// Maximum connections allowed per node (hard cap). Default: 100.
    /// </summary>
    public int MaxConnections { get; init; } = 100;

    /// <summary>
    /// Global maximum connections across all nodes. Default: 1000.
    /// </summary>
    public int MaxTotalConnections { get; init; } = 1000;

    /// <summary>
    /// Maximum time to wait when acquiring a connection before throwing TimeoutException. Default: 30 seconds.
    /// </summary>
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an idle connection can remain in the pool before eviction. Default: 5 minutes.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum lifetime for any connection, after which it is forcibly recycled. Default: 1 hour.
    /// </summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Interval between background health check sweeps. Default: 30 seconds.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to validate connections when acquiring from the idle pool. Default: true.
    /// </summary>
    public bool ValidateOnAcquire { get; init; } = true;

    /// <summary>
    /// Whether to validate connections when returning to the pool. Default: false.
    /// </summary>
    public bool ValidateOnRelease { get; init; }

    /// <summary>
    /// Number of connections to pre-create per node during warm-up. Default: 2.
    /// </summary>
    public int WarmUpCountPerNode { get; init; } = 2;
}

/// <summary>
/// Diagnostic report for the overall connection pool.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed record PoolHealthReport
{
    /// <summary>
    /// Total number of connections (active + idle) across all nodes.
    /// </summary>
    public int TotalConnections { get; init; }

    /// <summary>
    /// Number of currently leased (in-use) connections.
    /// </summary>
    public int ActiveConnections { get; init; }

    /// <summary>
    /// Number of idle connections available in pools.
    /// </summary>
    public int IdleConnections { get; init; }

    /// <summary>
    /// Number of connections that failed their last health check.
    /// </summary>
    public int FailedHealthChecks { get; init; }

    /// <summary>
    /// Total connections created since pool initialization.
    /// </summary>
    public int ConnectionsCreated { get; init; }

    /// <summary>
    /// Total connections destroyed since pool initialization.
    /// </summary>
    public int ConnectionsDestroyed { get; init; }

    /// <summary>
    /// Per-node pool status breakdown.
    /// </summary>
    public Dictionary<string, NodePoolStatus> PerNodeStatus { get; init; } = new();

    /// <summary>
    /// Timestamp of the last background health check sweep.
    /// </summary>
    public DateTimeOffset LastHealthCheck { get; init; }
}

/// <summary>
/// Status of a single node's connection pool.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Connection pooling (ECOS-18)")]
public sealed record NodePoolStatus
{
    /// <summary>
    /// The node identifier.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Number of active (leased) connections to this node.
    /// </summary>
    public int Active { get; init; }

    /// <summary>
    /// Number of idle connections to this node.
    /// </summary>
    public int Idle { get; init; }

    /// <summary>
    /// Number of connection creation requests pending for this node.
    /// </summary>
    public int Pending { get; init; }

    /// <summary>
    /// Whether this node's pool is considered healthy.
    /// </summary>
    public bool Healthy { get; init; }
}
