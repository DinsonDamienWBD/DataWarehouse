using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Connectors
{
    /// <summary>
    /// Abstract base class implementing <see cref="IConnectionStrategy"/> with retry logic,
    /// connection metrics, and optional logging. Concrete strategies should derive from this
    /// class and implement the protected Core methods.
    /// </summary>
    public abstract class ConnectionStrategyBase : IConnectionStrategy
    {
        private readonly ILogger? _logger;

        // Connection metrics
        private long _totalConnections;
        private long _totalFailures;
        private long _totalLatencyTicks;
        private long _successfulConnections;

        /// <inheritdoc/>
        public abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string DisplayName { get; }

        /// <inheritdoc/>
        public abstract ConnectorCategory Category { get; }

        /// <inheritdoc/>
        public abstract ConnectionStrategyCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public abstract string SemanticDescription { get; }

        /// <inheritdoc/>
        public abstract string[] Tags { get; }

        /// <summary>
        /// Gets the total number of connection attempts.
        /// </summary>
        public long TotalConnections => Interlocked.Read(ref _totalConnections);

        /// <summary>
        /// Gets the total number of connection failures.
        /// </summary>
        public long TotalFailures => Interlocked.Read(ref _totalFailures);

        /// <summary>
        /// Gets the average connection latency.
        /// </summary>
        public TimeSpan AverageLatency
        {
            get
            {
                var successful = Interlocked.Read(ref _successfulConnections);
                if (successful == 0) return TimeSpan.Zero;
                var totalTicks = Interlocked.Read(ref _totalLatencyTicks);
                return TimeSpan.FromTicks(totalTicks / successful);
            }
        }

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionStrategyBase"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics. May be null.</param>
        protected ConnectionStrategyBase(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task<IConnectionHandle> ConnectAsync(ConnectionConfig config, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(config);

            var maxRetries = config.MaxRetries;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    // Exponential backoff: 200ms, 400ms, 800ms, ...
                    var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                    _logger?.LogWarning(
                        "Connection attempt {Attempt}/{MaxRetries} for strategy {StrategyId} failed. Retrying in {Delay}ms...",
                        attempt, maxRetries, StrategyId, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }

                Interlocked.Increment(ref _totalConnections);
                var sw = Stopwatch.StartNew();

                try
                {
                    var handle = await ConnectCoreAsync(config, ct);
                    sw.Stop();

                    Interlocked.Increment(ref _successfulConnections);
                    Interlocked.Add(ref _totalLatencyTicks, sw.Elapsed.Ticks);

                    _logger?.LogInformation(
                        "Connected via strategy {StrategyId} in {ElapsedMs}ms (attempt {Attempt})",
                        StrategyId, sw.ElapsedMilliseconds, attempt + 1);

                    return handle;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    lastException = ex;
                    Interlocked.Increment(ref _totalFailures);
                    sw.Stop();

                    _logger?.LogWarning(ex,
                        "Connection attempt {Attempt}/{MaxRetries} failed for strategy {StrategyId}",
                        attempt + 1, maxRetries + 1, StrategyId);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _totalFailures);
                    sw.Stop();
                    lastException = ex;
                }
            }

            _logger?.LogError(lastException,
                "All {MaxRetries} connection attempts exhausted for strategy {StrategyId}",
                maxRetries + 1, StrategyId);

            throw new InvalidOperationException(
                $"Failed to connect using strategy '{StrategyId}' after {maxRetries + 1} attempts. " +
                $"Last error: {lastException?.Message}", lastException);
        }

        /// <inheritdoc/>
        public async Task<bool> TestConnectionAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);

            try
            {
                return await TestCoreAsync(handle, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Connection test failed for strategy {StrategyId}, connection {ConnectionId}",
                    StrategyId, handle.ConnectionId);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);

            try
            {
                await DisconnectCoreAsync(handle, ct);
                _logger?.LogInformation(
                    "Disconnected connection {ConnectionId} via strategy {StrategyId}",
                    handle.ConnectionId, StrategyId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Error disconnecting connection {ConnectionId} via strategy {StrategyId}",
                    handle.ConnectionId, StrategyId);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<ConnectionHealth> GetHealthAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);

            var sw = Stopwatch.StartNew();
            try
            {
                var health = await GetHealthCoreAsync(handle, ct);
                sw.Stop();
                return health;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger?.LogWarning(ex,
                    "Health check failed for strategy {StrategyId}, connection {ConnectionId}",
                    StrategyId, handle.ConnectionId);

                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"Health check failed: {ex.Message}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
        }

        /// <inheritdoc/>
        public virtual Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
            ConnectionConfig config, CancellationToken ct = default)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(config.ConnectionString))
                errors.Add("ConnectionString is required.");

            if (config.Timeout <= TimeSpan.Zero)
                errors.Add("Timeout must be a positive duration.");

            if (config.MaxRetries < 0)
                errors.Add("MaxRetries must be non-negative.");

            if (config.PoolSize < 1)
                errors.Add("PoolSize must be at least 1.");

            return Task.FromResult((errors.Count == 0, errors.ToArray()));
        }

        /// <summary>
        /// Core connection logic. Implement in derived classes to establish the actual connection.
        /// This method is called by <see cref="ConnectAsync"/> with retry and metrics wrappers.
        /// </summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A connection handle for the established connection.</returns>
        protected abstract Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct);

        /// <summary>
        /// Core connection test logic. Implement in derived classes to test an active connection.
        /// </summary>
        /// <param name="handle">The connection handle to test.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the connection is alive and responsive.</returns>
        protected abstract Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct);

        /// <summary>
        /// Core disconnect logic. Implement in derived classes to cleanly close a connection.
        /// </summary>
        /// <param name="handle">The connection handle to disconnect.</param>
        /// <param name="ct">Cancellation token.</param>
        protected abstract Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct);

        /// <summary>
        /// Core health check logic. Implement in derived classes to perform a health check.
        /// </summary>
        /// <param name="handle">The connection handle to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Health status of the connection.</returns>
        protected abstract Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct);

        /// <summary>
        /// Reads a typed configuration value from <see cref="ConnectionConfig.Properties"/>.
        /// </summary>
        /// <typeparam name="T">The target type to convert the value to.</typeparam>
        /// <param name="config">The connection configuration.</param>
        /// <param name="key">The property key.</param>
        /// <param name="defaultValue">Default value if key is not found.</param>
        /// <returns>The converted value or the default.</returns>
        protected static T GetConfiguration<T>(ConnectionConfig config, string key, T defaultValue = default!)
        {
            if (!config.Properties.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Default implementation of <see cref="IConnectionHandle"/> for use by derived strategies.
        /// </summary>
        protected class DefaultConnectionHandle : IConnectionHandle
        {
            private volatile bool _isConnected = true;

            /// <inheritdoc/>
            public string ConnectionId { get; }

            /// <inheritdoc/>
            public bool IsConnected => _isConnected;

            /// <inheritdoc/>
            public object UnderlyingConnection { get; }

            /// <inheritdoc/>
            public Dictionary<string, object> ConnectionInfo { get; }

            /// <summary>
            /// Initializes a new instance of <see cref="DefaultConnectionHandle"/>.
            /// </summary>
            /// <param name="underlyingConnection">The underlying connection object.</param>
            /// <param name="connectionInfo">Optional metadata about the connection.</param>
            /// <param name="connectionId">Optional explicit connection ID. Auto-generated if null.</param>
            public DefaultConnectionHandle(
                object underlyingConnection,
                Dictionary<string, object>? connectionInfo = null,
                string? connectionId = null)
            {
                ConnectionId = connectionId ?? Guid.NewGuid().ToString("N");
                UnderlyingConnection = underlyingConnection ?? throw new ArgumentNullException(nameof(underlyingConnection));
                ConnectionInfo = connectionInfo ?? new Dictionary<string, object>();
            }

            /// <inheritdoc/>
            public T GetConnection<T>()
            {
                if (UnderlyingConnection is T typed)
                    return typed;

                throw new InvalidCastException(
                    $"Cannot cast underlying connection of type {UnderlyingConnection.GetType().FullName} to {typeof(T).FullName}");
            }

            /// <summary>
            /// Marks this handle as disconnected.
            /// </summary>
            public void MarkDisconnected()
            {
                _isConnected = false;
            }

            /// <inheritdoc/>
            public async ValueTask DisposeAsync()
            {
                _isConnected = false;
                if (UnderlyingConnection is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (UnderlyingConnection is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                GC.SuppressFinalize(this);
            }
        }
    }
}
