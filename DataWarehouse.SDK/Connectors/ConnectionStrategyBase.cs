using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
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
    /// connection metrics, optional logging, and Intelligence context support.
    /// Concrete strategies should derive from this class and implement the protected Core methods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This base class now supports <see cref="IntelligenceContext"/> for AI-enhanced connection operations:
    /// </para>
    /// <list type="bullet">
    ///   <item>Connection optimization based on AI analysis</item>
    ///   <item>Predictive failure detection</item>
    ///   <item>Intelligent retry strategies</item>
    ///   <item>AI-assisted health monitoring</item>
    /// </list>
    /// </remarks>
    public abstract class ConnectionStrategyBase : StrategyBase, IConnectionStrategy
    {
        private readonly ILogger? _logger;

        // Connection metrics
        private long _totalConnections;
        private long _totalFailures;
        private long _totalLatencyTicks;
        private long _successfulConnections;

        // P2-2158: Cache health-check results for HealthCacheTtl to avoid high-frequency
        // full round-trips when callers poll rapidly. Cache is keyed by ConnectionId.
        private static readonly TimeSpan HealthCacheTtl = TimeSpan.FromSeconds(10);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Expiry, ConnectionHealth Health)> _healthCache = new();

        /// <inheritdoc/>
        public override abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Gets the human-readable name. Delegates to DisplayName for backward compatibility.
        /// </summary>
        public override string Name => DisplayName;

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

        /// <summary>
        /// Gets the connector category string for metadata and knowledge registration.
        /// Override in derived classes to provide specific category names.
        /// </summary>
        protected virtual string GetConnectorCategory() => Category.ToString();

        /// <summary>
        /// Gets the connection capabilities for metadata and knowledge registration.
        /// Override in derived classes to provide specific capabilities.
        /// </summary>
        protected virtual Dictionary<string, object> GetConnectionCapabilities() => new();

        /// <inheritdoc/>
        public async Task<IConnectionHandle> ConnectAsync(ConnectionConfig config, CancellationToken ct = default)
        {
            return await ConnectAsync(config, null, ct);
        }

        /// <summary>
        /// Establishes a connection with optional Intelligence context for AI-enhanced operations.
        /// </summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="intelligenceContext">Optional Intelligence context for AI-enhanced connection behavior.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A connection handle representing the established connection.</returns>
        /// <exception cref="InvalidOperationException">Thrown when all connection attempts fail.</exception>
        /// <remarks>
        /// <para>
        /// When an <see cref="IntelligenceContext"/> is provided, the connection strategy can:
        /// </para>
        /// <list type="bullet">
        ///   <item>Use AI to predict optimal connection parameters</item>
        ///   <item>Adjust retry behavior based on failure pattern analysis</item>
        ///   <item>Log enhanced diagnostics for AI training</item>
        ///   <item>Apply intelligent circuit-breaking</item>
        /// </list>
        /// <para>
        /// If Intelligence is unavailable or the context is null, standard connection behavior is used.
        /// </para>
        /// </remarks>
        public async Task<IConnectionHandle> ConnectAsync(
            ConnectionConfig config,
            IntelligenceContext? intelligenceContext,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(config);

            var maxRetries = config.MaxRetries;
            Exception? lastException = null;

            // Notify derived classes of Intelligence context availability
            if (intelligenceContext != null)
            {
                await OnIntelligenceContextAvailableAsync(intelligenceContext, ct);
            }

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    // Calculate delay - can be overridden for intelligent backoff
                    var delay = await CalculateRetryDelayAsync(attempt, maxRetries, lastException, intelligenceContext, ct);

                    _logger?.LogWarning(
                        "Connection attempt {Attempt}/{MaxRetries} for strategy {StrategyId} failed. Retrying in {Delay}ms...",
                        attempt, maxRetries, StrategyId, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }

                Interlocked.Increment(ref _totalConnections);
                var sw = Stopwatch.StartNew();

                try
                {
                    var handle = await ConnectCoreAsync(config, intelligenceContext, ct);
                    sw.Stop();

                    Interlocked.Increment(ref _successfulConnections);
                    Interlocked.Add(ref _totalLatencyTicks, sw.Elapsed.Ticks);

                    _logger?.LogInformation(
                        "Connected via strategy {StrategyId} in {ElapsedMs}ms (attempt {Attempt})",
                        StrategyId, sw.ElapsedMilliseconds, attempt + 1);

                    // Record success metrics for Intelligence if available
                    if (intelligenceContext != null)
                    {
                        await OnConnectionSuccessAsync(handle, sw.Elapsed, attempt, intelligenceContext, ct);
                    }

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

                    // Allow Intelligence-aware decision on whether to continue retrying
                    if (intelligenceContext != null)
                    {
                        var shouldContinue = await OnConnectionFailureAsync(ex, attempt, maxRetries, intelligenceContext, ct);
                        if (!shouldContinue)
                        {
                            throw new InvalidOperationException(
                                $"Connection aborted by Intelligence decision for strategy '{StrategyId}'. " +
                                $"Error: {ex.Message}", ex);
                        }
                    }
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

            // P2-2158: Return a cached result if it has not yet expired to avoid
            // one full network round-trip per caller per poll interval.
            var cacheKey = handle.ConnectionId;
            var now = DateTimeOffset.UtcNow;
            if (_healthCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > now)
                return cached.Health;

            var sw = Stopwatch.StartNew();
            try
            {
                var health = await GetHealthCoreAsync(handle, ct);
                sw.Stop();
                _healthCache[cacheKey] = (now.Add(HealthCacheTtl), health);
                return health;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger?.LogWarning(ex,
                    "Health check failed for strategy {StrategyId}, connection {ConnectionId}",
                    StrategyId, handle.ConnectionId);

                var unhealthy = new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"Health check failed: {ex.Message}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
                // Cache failures for a shorter period so errors resolve promptly.
                _healthCache[cacheKey] = (now.AddSeconds(2), unhealthy);
                return unhealthy;
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
        /// Core connection logic with Intelligence context support.
        /// Override in derived classes to leverage AI-enhanced connection behavior.
        /// </summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="intelligenceContext">Optional Intelligence context for AI-enhanced operations.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A connection handle for the established connection.</returns>
        /// <remarks>
        /// <para>
        /// Default implementation delegates to <see cref="ConnectCoreAsync(ConnectionConfig, CancellationToken)"/>.
        /// Override to implement Intelligence-aware connection logic such as:
        /// </para>
        /// <list type="bullet">
        ///   <item>Using AI to optimize connection parameters</item>
        ///   <item>Predictive pre-warming of connections</item>
        ///   <item>Intelligent endpoint selection</item>
        /// </list>
        /// </remarks>
        protected virtual Task<IConnectionHandle> ConnectCoreAsync(
            ConnectionConfig config,
            IntelligenceContext? intelligenceContext,
            CancellationToken ct)
        {
            // Default: delegate to the non-Intelligence-aware version
            return ConnectCoreAsync(config, ct);
        }

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
        /// Parses a connection string of the form <c>host:port</c> or <c>[::1]:port</c> (IPv6)
        /// into separate host and port components, correctly handling IPv6 bracket notation.
        /// </summary>
        /// <param name="connectionString">The raw connection string (e.g. "myhost:5432" or "[::1]:5432").</param>
        /// <param name="defaultPort">Port to use when the connection string does not include one.</param>
        /// <returns>A tuple of (host, port).</returns>
        /// <remarks>
        /// P2-2132: A naive <c>Split(':')</c> breaks for IPv6 addresses which contain colons.
        /// This helper uses <see cref="Uri.TryCreate"/> with a dummy scheme to correctly extract
        /// the host and port fields from both IPv4 and IPv6 connection strings.
        /// Subclass implementations named <c>ParseHostPort</c> continue to work unchanged.
        /// </remarks>
        protected static (string host, int port) ParseHostPortSafe(string connectionString, int defaultPort)
        {
            // Try Uri-based parsing first (handles IPv6 "[::1]:port" and plain "host:port")
            if (Uri.TryCreate("tcp://" + connectionString, UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Host)
                && uri.Port > 0)
            {
                return (uri.Host, uri.Port);
            }

            // Fallback: simple split for bare "host:port" strings that Uri cannot handle
            var colonIdx = connectionString.LastIndexOf(':');
            if (colonIdx > 0
                && int.TryParse(connectionString.AsSpan(colonIdx + 1), out var parsedPort)
                && parsedPort > 0)
            {
                return (connectionString[..colonIdx], parsedPort);
            }

            // No port found — use entire string as host with the default port
            return (connectionString, defaultPort);
        }

        // ========================================
        // Intelligence Hooks for Derived Classes
        // ========================================

        /// <summary>
        /// Called when an Intelligence context is available for connection operations.
        /// Override to initialize Intelligence-aware features.
        /// </summary>
        /// <param name="context">The Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        protected virtual Task OnIntelligenceContextAvailableAsync(
            IntelligenceContext context,
            CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called on successful connection to record metrics for Intelligence learning.
        /// Override to provide connection telemetry for AI analysis.
        /// </summary>
        /// <param name="handle">The established connection handle.</param>
        /// <param name="connectionTime">Time taken to establish the connection.</param>
        /// <param name="attemptNumber">The attempt number (0-based).</param>
        /// <param name="context">The Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        protected virtual Task OnConnectionSuccessAsync(
            IConnectionHandle handle,
            TimeSpan connectionTime,
            int attemptNumber,
            IntelligenceContext context,
            CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called on connection failure to allow Intelligence-informed retry decisions.
        /// Override to implement AI-driven retry logic.
        /// </summary>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="attemptNumber">The attempt number (0-based).</param>
        /// <param name="maxRetries">Maximum number of retries allowed.</param>
        /// <param name="context">The Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if retries should continue; false to abort.</returns>
        protected virtual Task<bool> OnConnectionFailureAsync(
            Exception exception,
            int attemptNumber,
            int maxRetries,
            IntelligenceContext context,
            CancellationToken ct)
        {
            // Default: continue with standard retry behavior
            return Task.FromResult(true);
        }

        /// <summary>
        /// Calculates retry delay, optionally using Intelligence for adaptive backoff.
        /// Override to implement AI-driven backoff strategies.
        /// </summary>
        /// <param name="attemptNumber">The attempt number (1-based for this method).</param>
        /// <param name="maxRetries">Maximum number of retries allowed.</param>
        /// <param name="lastException">The exception from the last attempt.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The delay before the next retry attempt.</returns>
        protected virtual Task<TimeSpan> CalculateRetryDelayAsync(
            int attemptNumber,
            int maxRetries,
            Exception? lastException,
            IntelligenceContext? context,
            CancellationToken ct)
        {
            // Default: exponential backoff (200ms, 400ms, 800ms, ...)
            var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attemptNumber - 1));
            return Task.FromResult(delay);
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
