using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for workflow and edge computing orchestration plugins.
/// Provides strategy dispatch for orchestration workloads via the PluginBase IStrategy registry,
/// with domain operations for workflow and pipeline execution.
/// </summary>
public abstract class OrchestrationPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Orchestration";

    /// <summary>Orchestration mode (e.g., "Workflow", "EdgeComputing", "Pipeline").</summary>
    public abstract string OrchestrationMode { get; }

    #region Orchestration Strategy Dispatch

    /// <summary>
    /// Registers an <see cref="IStrategy"/> as an orchestration strategy in the PluginBase IStrategy registry.
    /// </summary>
    /// <param name="strategy">The orchestration strategy to register.</param>
    protected void RegisterOrchestrationStrategy(IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        RegisterStrategy(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the named orchestration strategy, with optional CommandIdentity ACL enforcement.
    /// Routes through the PluginBase IStrategy registry.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="strategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks.</param>
    /// <param name="operation">The operation to execute on the resolved strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    protected Task<TResult> DispatchOrchestrationStrategyAsync<TStrategy, TResult>(
        string? strategyId,
        CommandIdentity? identity,
        Func<TStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
        where TStrategy : class, IStrategy
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWithStrategyAsync<TStrategy, TResult>(strategyId, identity, operation, ct);
    }

    /// <summary>
    /// Orchestrates a workflow using the specified or default orchestration strategy.
    /// The workflow dictionary carries operation type, steps, inputs, and configuration
    /// for the strategy to execute.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <param name="workflow">Workflow parameters (steps, inputs, configuration).</param>
    /// <param name="strategyId">Optional strategy ID. Null uses <see cref="OrchestrationMode"/> as the default.</param>
    /// <param name="identity">Optional CommandIdentity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Workflow execution result dictionary.</returns>
    protected Task<Dictionary<string, object>> OrchestrateWithStrategyAsync<TStrategy>(
        Dictionary<string, object> workflow,
        string? strategyId,
        CommandIdentity? identity,
        CancellationToken ct = default)
        where TStrategy : class, IStrategy
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return ExecuteWithStrategyAsync<TStrategy, Dictionary<string, object>>(
            strategyId,
            identity,
            _ => Task.FromResult(new Dictionary<string, object>(workflow)),
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="OrchestrationMode"/> to route dispatches to the correct orchestration strategy.</remarks>
    protected override string? GetDefaultStrategyId() => OrchestrationMode;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["OrchestrationMode"] = OrchestrationMode;
        return metadata;
    }
}
