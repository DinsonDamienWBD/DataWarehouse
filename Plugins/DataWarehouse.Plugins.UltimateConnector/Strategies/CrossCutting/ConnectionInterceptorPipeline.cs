using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Pipeline that chains multiple <see cref="IConnectionInterceptor"/> instances in priority
    /// order and executes them sequentially for each hook point. Each interceptor receives the
    /// context potentially modified by the preceding interceptor, forming a middleware pipeline.
    /// </summary>
    /// <remarks>
    /// Thread-safe interceptor registration is supported via a lock-protected sorted list.
    /// Pipeline execution is sequential within a single hook invocation to maintain deterministic
    /// ordering, but different connections may invoke the pipeline concurrently.
    /// </remarks>
    public sealed class ConnectionInterceptorPipeline
    {
        private readonly List<IConnectionInterceptor> _interceptors = new();
        private readonly object _registrationLock = new();
        private readonly ILogger? _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionInterceptorPipeline"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public ConnectionInterceptorPipeline(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the current number of registered interceptors.
        /// </summary>
        public int Count
        {
            get { lock (_registrationLock) { return _interceptors.Count; } }
        }

        /// <summary>
        /// Registers an interceptor in priority order. Thread-safe.
        /// </summary>
        /// <param name="interceptor">Interceptor to register.</param>
        /// <exception cref="ArgumentNullException">If interceptor is null.</exception>
        public void Register(IConnectionInterceptor interceptor)
        {
            ArgumentNullException.ThrowIfNull(interceptor);

            lock (_registrationLock)
            {
                _interceptors.Add(interceptor);
                _interceptors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            }

            _logger?.LogInformation(
                "Registered interceptor '{Name}' with priority {Priority}",
                interceptor.Name, interceptor.Priority);
        }

        /// <summary>
        /// Removes a registered interceptor by reference. Thread-safe.
        /// </summary>
        /// <param name="interceptor">Interceptor to remove.</param>
        /// <returns>True if the interceptor was found and removed.</returns>
        public bool Unregister(IConnectionInterceptor interceptor)
        {
            lock (_registrationLock)
            {
                var removed = _interceptors.Remove(interceptor);
                if (removed)
                    _logger?.LogInformation("Unregistered interceptor '{Name}'", interceptor.Name);
                return removed;
            }
        }

        /// <summary>
        /// Returns a snapshot of all registered interceptors in priority order.
        /// </summary>
        public IReadOnlyList<IConnectionInterceptor> GetInterceptors()
        {
            lock (_registrationLock) { return _interceptors.ToList(); }
        }

        /// <summary>
        /// Executes the before-request hook across all enabled interceptors in priority order.
        /// Short-circuits if any interceptor cancels the request.
        /// </summary>
        /// <param name="context">Initial before-request context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The final context after all interceptors have executed.</returns>
        public async Task<BeforeRequestContext> ExecuteBeforeRequestAsync(
            BeforeRequestContext context, CancellationToken ct = default)
        {
            foreach (var interceptor in GetEnabledInterceptors())
            {
                try
                {
                    context = await interceptor.OnBeforeRequestAsync(context, ct);

                    if (context.IsCancelled)
                    {
                        _logger?.LogInformation(
                            "Request cancelled by interceptor '{Name}': {Reason}",
                            interceptor.Name, context.CancellationReason);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Interceptor '{Name}' threw during OnBeforeRequest for {StrategyId}/{ConnectionId}",
                        interceptor.Name, context.StrategyId, context.ConnectionId);
                }
            }

            return context;
        }

        /// <summary>
        /// Executes the after-response hook across all enabled interceptors in priority order.
        /// </summary>
        /// <param name="context">Initial after-response context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The final context after all interceptors have executed.</returns>
        public async Task<AfterResponseContext> ExecuteAfterResponseAsync(
            AfterResponseContext context, CancellationToken ct = default)
        {
            foreach (var interceptor in GetEnabledInterceptors())
            {
                try
                {
                    context = await interceptor.OnAfterResponseAsync(context, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Interceptor '{Name}' threw during OnAfterResponse for {StrategyId}/{ConnectionId}",
                        interceptor.Name, context.StrategyId, context.ConnectionId);
                }
            }

            return context;
        }

        /// <summary>
        /// Executes the schema-discovered hook across all enabled interceptors.
        /// </summary>
        /// <param name="context">Schema discovery context.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteSchemaDiscoveredAsync(
            SchemaDiscoveryContext context, CancellationToken ct = default)
        {
            foreach (var interceptor in GetEnabledInterceptors())
            {
                try
                {
                    await interceptor.OnSchemaDiscoveredAsync(context, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Interceptor '{Name}' threw during OnSchemaDiscovered for schema '{Schema}'",
                        interceptor.Name, context.SchemaName);
                }
            }
        }

        /// <summary>
        /// Executes the error hook across all enabled interceptors. If any interceptor
        /// marks the error as handled, subsequent interceptors still execute but the
        /// error will not propagate to the caller.
        /// </summary>
        /// <param name="context">Initial error context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The final error context after all interceptors have executed.</returns>
        public async Task<ErrorContext> ExecuteErrorAsync(
            ErrorContext context, CancellationToken ct = default)
        {
            foreach (var interceptor in GetEnabledInterceptors())
            {
                try
                {
                    context = await interceptor.OnErrorAsync(context, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Interceptor '{Name}' threw during OnError for {StrategyId}/{ConnectionId}",
                        interceptor.Name, context.StrategyId, context.ConnectionId);
                }
            }

            return context;
        }

        /// <summary>
        /// Executes the connection-established hook across all enabled interceptors.
        /// </summary>
        /// <param name="context">Connection established context.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteConnectionEstablishedAsync(
            ConnectionEstablishedContext context, CancellationToken ct = default)
        {
            foreach (var interceptor in GetEnabledInterceptors())
            {
                try
                {
                    await interceptor.OnConnectionEstablishedAsync(context, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Interceptor '{Name}' threw during OnConnectionEstablished for {StrategyId}/{ConnectionId}",
                        interceptor.Name, context.StrategyId, context.ConnectionId);
                }
            }
        }

        /// <summary>
        /// Returns a filtered list of enabled interceptors in priority order.
        /// </summary>
        private IReadOnlyList<IConnectionInterceptor> GetEnabledInterceptors()
        {
            lock (_registrationLock)
            {
                return _interceptors.Where(i => i.IsEnabled).ToList();
            }
        }
    }
}
