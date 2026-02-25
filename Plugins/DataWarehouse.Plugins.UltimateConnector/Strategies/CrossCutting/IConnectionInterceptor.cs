using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Interface for connection interceptors that plug into the connection lifecycle pipeline.
    /// Interceptors can observe and modify connection operations at defined hook points:
    /// before request, after response, on schema discovery, on error, and on connection
    /// establishment.
    /// </summary>
    /// <remarks>
    /// Interceptors are executed in priority order (lower values execute first). Multiple
    /// interceptors can be registered and they form a pipeline where each interceptor
    /// receives the context potentially modified by previous interceptors.
    ///
    /// Interceptor implementations should be stateless or thread-safe, as they may be
    /// invoked concurrently for different connections.
    /// </remarks>
    public interface IConnectionInterceptor
    {
        /// <summary>
        /// Display name for this interceptor, used in logging and diagnostics.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Execution priority. Lower values execute first in the pipeline.
        /// Recommended ranges: 0-99 (system), 100-499 (framework), 500-999 (application).
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Whether this interceptor is currently enabled. Disabled interceptors are
        /// skipped during pipeline execution.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Called before a connection operation is executed. Interceptors may modify the
        /// request payload or cancel the operation entirely.
        /// </summary>
        /// <param name="context">Mutable context containing request details.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The potentially modified context.</returns>
        Task<BeforeRequestContext> OnBeforeRequestAsync(BeforeRequestContext context, CancellationToken ct = default);

        /// <summary>
        /// Called after a connection operation completes (successfully or not). Interceptors
        /// may inspect and log the response or modify response metadata.
        /// </summary>
        /// <param name="context">Context containing response details.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The potentially modified context.</returns>
        Task<AfterResponseContext> OnAfterResponseAsync(AfterResponseContext context, CancellationToken ct = default);

        /// <summary>
        /// Called when a schema is discovered during connection or query operations.
        /// Interceptors may enrich schema metadata or trigger indexing operations.
        /// </summary>
        /// <param name="context">Context containing discovered schema details.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the asynchronous operation.</returns>
        Task OnSchemaDiscoveredAsync(SchemaDiscoveryContext context, CancellationToken ct = default);

        /// <summary>
        /// Called when an error occurs during a connection operation. Interceptors may
        /// handle the error, log it, or provide a fallback result.
        /// </summary>
        /// <param name="context">Context containing error details. Set IsHandled to prevent propagation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The potentially modified error context.</returns>
        Task<ErrorContext> OnErrorAsync(ErrorContext context, CancellationToken ct = default);

        /// <summary>
        /// Called when a new connection is successfully established. Interceptors may
        /// perform post-connection setup or metadata registration.
        /// </summary>
        /// <param name="context">Context containing connection establishment details.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the asynchronous operation.</returns>
        Task OnConnectionEstablishedAsync(ConnectionEstablishedContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// Base class for connection interceptors that provides no-op default implementations
    /// for all hooks. Derive from this class to implement only the hooks you need.
    /// </summary>
    public abstract class ConnectionInterceptorBase : IConnectionInterceptor
    {
        /// <inheritdoc/>
        public abstract string Name { get; }

        /// <inheritdoc/>
        public virtual int Priority => 500;

        /// <inheritdoc/>
        public virtual bool IsEnabled => true;

        /// <inheritdoc/>
        public virtual Task<BeforeRequestContext> OnBeforeRequestAsync(
            BeforeRequestContext context, CancellationToken ct = default)
            => Task.FromResult(context);

        /// <inheritdoc/>
        public virtual Task<AfterResponseContext> OnAfterResponseAsync(
            AfterResponseContext context, CancellationToken ct = default)
            => Task.FromResult(context);

        /// <inheritdoc/>
        public virtual Task OnSchemaDiscoveredAsync(
            SchemaDiscoveryContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        /// <inheritdoc/>
        public virtual Task<ErrorContext> OnErrorAsync(
            ErrorContext context, CancellationToken ct = default)
            => Task.FromResult(context);

        /// <inheritdoc/>
        public virtual Task OnConnectionEstablishedAsync(
            ConnectionEstablishedContext context, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
