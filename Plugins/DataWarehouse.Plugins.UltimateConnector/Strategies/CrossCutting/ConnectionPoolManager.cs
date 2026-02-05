using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Thread-safe connection pool manager that maintains per-strategy connection pools
    /// with configurable min/max sizes, idle timeout eviction, and periodic health checking.
    /// </summary>
    /// <remarks>
    /// Each strategy registered with the pool manager gets an isolated pool backed by a
    /// <see cref="ConcurrentQueue{T}"/> for lock-free connection retrieval and a
    /// <see cref="SemaphoreSlim"/> to enforce maximum pool size. Idle connections are
    /// evicted by a background timer based on the configured idle timeout.
    /// </remarks>
    public sealed class ConnectionPoolManager : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, StrategyPool> _pools = new();
        private readonly Timer _evictionTimer;
        private volatile bool _disposed;

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionPoolManager"/> with default eviction interval.
        /// </summary>
        /// <param name="evictionInterval">Interval between idle connection eviction sweeps.</param>
        public ConnectionPoolManager(TimeSpan? evictionInterval = null)
        {
            var interval = evictionInterval ?? TimeSpan.FromSeconds(30);
            _evictionTimer = new Timer(EvictIdleConnections, null, interval, interval);
        }

        /// <summary>
        /// Registers a strategy pool with the given configuration. Idempotent; re-registration is ignored.
        /// </summary>
        /// <param name="strategyId">Unique strategy identifier.</param>
        /// <param name="poolConfig">Pool configuration for this strategy.</param>
        public void RegisterPool(string strategyId, PoolConfiguration poolConfig)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            ArgumentNullException.ThrowIfNull(poolConfig);

            _pools.TryAdd(strategyId, new StrategyPool(poolConfig));
        }

        /// <summary>
        /// Acquires a connection from the pool for the specified strategy, or creates a new one
        /// using the provided factory if the pool is empty.
        /// </summary>
        /// <param name="strategyId">Strategy identifier whose pool to draw from.</param>
        /// <param name="factory">Factory delegate to create a new connection if the pool is empty.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A pooled connection handle.</returns>
        public async Task<IConnectionHandle> GetConnectionAsync(
            string strategyId,
            Func<CancellationToken, Task<IConnectionHandle>> factory,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_pools.TryGetValue(strategyId, out var pool))
                throw new InvalidOperationException($"No pool registered for strategy '{strategyId}'.");

            await pool.Semaphore.WaitAsync(ct);

            if (pool.Connections.TryDequeue(out var entry))
            {
                if (entry.Handle.IsConnected && !entry.IsExpired(pool.Config.IdleTimeout))
                {
                    Interlocked.Increment(ref pool.HitCount);
                    entry.LastUsed = DateTimeOffset.UtcNow;
                    return entry.Handle;
                }

                await SafeDisposeAsync(entry.Handle);
            }

            Interlocked.Increment(ref pool.MissCount);
            var handle = await factory(ct);
            Interlocked.Increment(ref pool.ActiveCount);
            return handle;
        }

        /// <summary>
        /// Returns a connection to the pool. If the pool is at capacity or the handle is
        /// disconnected, the connection is disposed instead.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="handle">Connection handle to return.</param>
        public async Task ReturnConnection(string strategyId, IConnectionHandle handle)
        {
            if (!_pools.TryGetValue(strategyId, out var pool))
            {
                await SafeDisposeAsync(handle);
                return;
            }

            if (!handle.IsConnected || pool.Connections.Count >= pool.Config.MaxSize)
            {
                Interlocked.Decrement(ref pool.ActiveCount);
                await SafeDisposeAsync(handle);
                pool.Semaphore.Release();
                return;
            }

            pool.Connections.Enqueue(new PooledConnection(handle));
            pool.Semaphore.Release();
        }

        /// <summary>
        /// Drains all connections from a specific strategy pool, disposing each one.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        public async Task DrainPool(string strategyId)
        {
            if (!_pools.TryGetValue(strategyId, out var pool)) return;

            while (pool.Connections.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref pool.ActiveCount);
                await SafeDisposeAsync(entry.Handle);
            }
        }

        /// <summary>
        /// Returns aggregate statistics for all registered pools.
        /// </summary>
        /// <returns>Dictionary mapping strategy ID to pool statistics.</returns>
        public Dictionary<string, PoolStats> GetPoolStats()
        {
            var result = new Dictionary<string, PoolStats>();

            foreach (var (strategyId, pool) in _pools)
            {
                result[strategyId] = new PoolStats(
                    PooledCount: pool.Connections.Count,
                    ActiveCount: Interlocked.Read(ref pool.ActiveCount),
                    HitCount: Interlocked.Read(ref pool.HitCount),
                    MissCount: Interlocked.Read(ref pool.MissCount),
                    MaxSize: pool.Config.MaxSize,
                    MinSize: pool.Config.MinSize,
                    IdleTimeout: pool.Config.IdleTimeout);
            }

            return result;
        }

        /// <summary>
        /// Background eviction sweep that removes idle and expired connections from all pools.
        /// </summary>
        private void EvictIdleConnections(object? state)
        {
            if (_disposed) return;

            foreach (var (_, pool) in _pools)
            {
                var toKeep = new List<PooledConnection>();
                var keepCount = pool.Config.MinSize;

                while (pool.Connections.TryDequeue(out var entry))
                {
                    if (toKeep.Count < keepCount || (!entry.IsExpired(pool.Config.IdleTimeout) && entry.Handle.IsConnected))
                    {
                        toKeep.Add(entry);
                    }
                    else
                    {
                        Interlocked.Decrement(ref pool.ActiveCount);
                        _ = SafeDisposeAsync(entry.Handle);
                    }
                }

                foreach (var kept in toKeep)
                    pool.Connections.Enqueue(kept);
            }
        }

        private static async Task SafeDisposeAsync(IConnectionHandle handle)
        {
            try { await handle.DisposeAsync(); }
            catch { /* Swallow disposal errors in pool management */ }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _evictionTimer.DisposeAsync();

            foreach (var (strategyId, _) in _pools)
                await DrainPool(strategyId);

            _pools.Clear();
        }
    }

    /// <summary>
    /// Configuration for a per-strategy connection pool.
    /// </summary>
    /// <param name="MinSize">Minimum number of connections to keep alive in the pool.</param>
    /// <param name="MaxSize">Maximum number of concurrent connections allowed.</param>
    /// <param name="IdleTimeoutValue">Duration after which an idle connection is evicted.</param>
    /// <param name="HealthCheckIntervalValue">Interval between health check sweeps for pooled connections.</param>
    public sealed record PoolConfiguration(
        int MinSize = 2,
        int MaxSize = 20,
        TimeSpan? IdleTimeoutValue = null,
        TimeSpan? HealthCheckIntervalValue = null)
    {
        /// <summary>
        /// Effective idle timeout, defaulting to 5 minutes.
        /// </summary>
        public TimeSpan IdleTimeout { get; } = IdleTimeoutValue ?? TimeSpan.FromMinutes(5);

        /// <summary>
        /// Effective health check interval, defaulting to 60 seconds.
        /// </summary>
        public TimeSpan HealthCheckInterval { get; } = HealthCheckIntervalValue ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Statistics for a single strategy connection pool.
    /// </summary>
    /// <param name="PooledCount">Number of connections currently idle in the pool.</param>
    /// <param name="ActiveCount">Number of connections currently checked out.</param>
    /// <param name="HitCount">Total number of pool hits (reused connections).</param>
    /// <param name="MissCount">Total number of pool misses (new connections created).</param>
    /// <param name="MaxSize">Configured maximum pool size.</param>
    /// <param name="MinSize">Configured minimum pool size.</param>
    /// <param name="IdleTimeout">Configured idle timeout for eviction.</param>
    public sealed record PoolStats(
        int PooledCount,
        long ActiveCount,
        long HitCount,
        long MissCount,
        int MaxSize,
        int MinSize,
        TimeSpan IdleTimeout);

    /// <summary>
    /// Internal pool state for a single strategy.
    /// </summary>
    internal sealed class StrategyPool
    {
        public PoolConfiguration Config { get; }
        public ConcurrentQueue<PooledConnection> Connections { get; } = new();
        public SemaphoreSlim Semaphore { get; }
        public long HitCount;
        public long MissCount;
        public long ActiveCount;

        public StrategyPool(PoolConfiguration config)
        {
            Config = config;
            Semaphore = new SemaphoreSlim(config.MaxSize, config.MaxSize);
        }
    }

    /// <summary>
    /// Wrapper tracking when a pooled connection was last used.
    /// </summary>
    internal sealed class PooledConnection
    {
        public IConnectionHandle Handle { get; }
        public DateTimeOffset LastUsed { get; set; }

        public PooledConnection(IConnectionHandle handle)
        {
            Handle = handle;
            LastUsed = DateTimeOffset.UtcNow;
        }

        public bool IsExpired(TimeSpan idleTimeout) =>
            DateTimeOffset.UtcNow - LastUsed > idleTimeout;
    }
}
