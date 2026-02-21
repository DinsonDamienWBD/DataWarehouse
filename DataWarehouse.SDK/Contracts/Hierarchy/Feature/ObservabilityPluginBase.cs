using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for observability plugins (metrics, tracing, logging, health).
/// </summary>
public abstract class ObservabilityPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Observability";

    /// <summary>Observability domain (e.g., "Metrics", "Tracing", "Logging", "Health").</summary>
    public abstract string ObservabilityDomain { get; }

    #region Typed Observability Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IObservabilityStrategy"/> instances.
    /// Key selector uses StrategyId from ObservabilityStrategyBase, falling back to type name.
    /// </summary>
    private StrategyRegistry<IObservabilityStrategy>? _observabilityStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_observabilityStrategyRegistry"/>.</summary>
    private readonly object _observabilityRegistryLock = new();

    /// <summary>
    /// Gets the typed observability strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IObservabilityStrategy> ObservabilityStrategyRegistry
    {
        get
        {
            if (_observabilityStrategyRegistry is not null) return _observabilityStrategyRegistry;
            lock (_observabilityRegistryLock)
            {
                _observabilityStrategyRegistry ??= new StrategyRegistry<IObservabilityStrategy>(
                    s => (s as ObservabilityStrategyBase)?.StrategyId ?? s.GetType().Name);
            }
            return _observabilityStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers an observability strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The observability strategy to register.</param>
    protected void RegisterObservabilityStrategy(IObservabilityStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ObservabilityStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the specified or default observability strategy,
    /// with optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the request for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved observability strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchObservabilityStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IObservabilityStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : GetDefaultStrategyId()
                ?? throw new InvalidOperationException(
                    "No strategy specified and no default configured. " +
                    "Call RegisterObservabilityStrategy and specify a strategyId, or configure a default.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use observability strategy '{strategyId}'.");
        }

        var strategy = ObservabilityStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Observability strategy '{strategyId}' is not registered. " +
                $"Call RegisterObservabilityStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Records a metric using the specified or default observability strategy.
    /// Resolves the strategy from the typed <see cref="ObservabilityStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="metric">The metric data to record.</param>
    /// <param name="strategyId">Optional strategy ID. When null, default strategy applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual async Task RecordMetricWithStrategyAsync(
        Dictionary<string, object> metric,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metric);

        await DispatchObservabilityStrategyAsync<bool>(
            strategyId,
            identity,
            metric,
            async strategy =>
            {
                var name = metric.TryGetValue("name", out var n) ? n?.ToString() ?? "unknown" : "unknown";
                var value = metric.TryGetValue("value", out var v) && v is double d ? d : 0.0;
                var metricValue = MetricValue.Gauge(name, value);

                await strategy.MetricsAsync(new[] { metricValue }, ct).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a trace span using the specified or default observability strategy.
    /// Resolves the strategy from the typed <see cref="ObservabilityStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="span">The span data to record.</param>
    /// <param name="strategyId">Optional strategy ID. When null, default strategy applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual async Task TraceWithStrategyAsync(
        Dictionary<string, object> span,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(span);

        await DispatchObservabilityStrategyAsync<bool>(
            strategyId,
            identity,
            span,
            async strategy =>
            {
                var operationName = span.TryGetValue("operationName", out var on) ? on?.ToString() ?? "unknown" : "unknown";
                var spanContext = SpanContext.CreateRoot(operationName);

                await strategy.TracingAsync(new[] { spanContext }, ct).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override string? GetDefaultStrategyId() => null;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ObservabilityDomain"] = ObservabilityDomain;
        return metadata;
    }
}
