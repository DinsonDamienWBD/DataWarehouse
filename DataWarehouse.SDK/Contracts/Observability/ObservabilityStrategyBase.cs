using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Observability;

/// <summary>
/// Abstract base class for observability strategy implementations providing common infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// This base class handles:
/// <list type="bullet">
/// <item><description>Lifecycle management (initialization and disposal)</description></item>
/// <item><description>Default health check implementation</description></item>
/// <item><description>Capability validation</description></item>
/// <item><description>Thread-safe state management</description></item>
/// </list>
/// </para>
/// <para>
/// Derived classes must implement the abstract methods for metrics, tracing, and logging,
/// and provide their specific capabilities via the constructor.
/// </para>
/// </remarks>
public abstract class ObservabilityStrategyBase : StrategyBase, IObservabilityStrategy
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    /// <summary>
    /// Gets the unique identifier for this observability strategy.
    /// Default derives from the class name. Override to provide a custom identifier.
    /// </summary>
    public override string StrategyId => GetType().Name.Replace("Strategy", "").ToLowerInvariant();

    /// <summary>
    /// Gets the display name for this observability strategy.
    /// Default derives from the class name. Override to provide a custom name.
    /// </summary>
    public override string Name => GetType().Name.Replace("Strategy", "");

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityStrategyBase"/> class.
    /// </summary>
    /// <param name="capabilities">The capabilities supported by this strategy.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="capabilities"/> is null.</exception>
    protected ObservabilityStrategyBase(ObservabilityCapabilities capabilities)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <inheritdoc/>
    public ObservabilityCapabilities Capabilities { get; }


    /// <inheritdoc/>
    public async Task MetricsAsync(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        EnsureNotDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (!Capabilities.SupportsMetrics)
            throw new NotSupportedException("Metrics are not supported by this observability strategy.");

        await MetricsAsyncCore(metrics, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TracingAsync(IEnumerable<SpanContext> spans, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spans);
        EnsureNotDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (!Capabilities.SupportsTracing)
            throw new NotSupportedException("Tracing is not supported by this observability strategy.");

        await TracingAsyncCore(spans, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task LoggingAsync(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logEntries);
        EnsureNotDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (!Capabilities.SupportsLogging)
            throw new NotSupportedException("Logging is not supported by this observability strategy.");

        await LoggingAsyncCore(logEntries, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public virtual async Task<HealthCheckResult> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        if (!IsInitialized)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: "Observability strategy not initialized",
                Data: null);
        }

        return await HealthCheckAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the core metrics recording operation. Called after validation and initialization.
    /// </summary>
    /// <param name="metrics">Collection of metrics to record.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken);

    /// <summary>
    /// Performs the core tracing operation. Called after validation and initialization.
    /// </summary>
    /// <param name="spans">Collection of trace spans to record.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken);

    /// <summary>
    /// Performs the core logging operation. Called after validation and initialization.
    /// </summary>
    /// <param name="logEntries">Collection of log entries to record.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken);

    /// <summary>
    /// Performs the core health check operation. Override to provide custom health check logic.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that resolves to a health check result.</returns>
    protected virtual Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(new HealthCheckResult(
            IsHealthy: true,
            Description: "Observability strategy is healthy",
            Data: new Dictionary<string, object>
            {
                ["capabilities"] = Capabilities
            }));
    }

    /// <summary>
    /// Initializes the observability strategy. Override to perform custom initialization.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous initialization.</returns>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes resources used by the observability strategy. Override to perform custom cleanup.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources; false if finalizing.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _initializationLock.Dispose();
        }

        base.Dispose(disposing);
    }


    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (IsInitialized)
            return;

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized)
                return;

            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

}
