using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for compute runtime plugins (WASM, container, sandbox, GPU).
/// </summary>
public abstract class ComputePluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Compute";

    /// <summary>Runtime type (e.g., "WASM", "Container", "Sandbox", "GPU").</summary>
    public abstract string RuntimeType { get; }

    /// <summary>Execute a compute workload.</summary>
    public abstract Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default);

    #region Typed Compute Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IComputeRuntimeStrategy"/> instances.
    /// Key selector uses the Runtime enum name as the strategy identifier.
    /// </summary>
    private StrategyRegistry<IComputeRuntimeStrategy>? _computeStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_computeStrategyRegistry"/>.</summary>
    private readonly object _computeRegistryLock = new();

    /// <summary>
    /// Gets the typed compute strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IComputeRuntimeStrategy> ComputeStrategyRegistry
    {
        get
        {
            if (_computeStrategyRegistry is not null) return _computeStrategyRegistry;
            lock (_computeRegistryLock)
            {
                _computeStrategyRegistry ??= new StrategyRegistry<IComputeRuntimeStrategy>(s => s.Runtime.ToString());
            }
            return _computeStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a compute runtime strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The compute runtime strategy to register.</param>
    protected void RegisterComputeStrategy(IComputeRuntimeStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ComputeStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the specified or default compute strategy,
    /// with optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the request for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved compute strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchComputeStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IComputeRuntimeStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : GetDefaultStrategyId()
                ?? throw new InvalidOperationException(
                    "No strategy specified and no default configured. " +
                    "Call RegisterComputeStrategy and specify a strategyId, or configure a default.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use compute strategy '{strategyId}'.");
        }

        var strategy = ComputeStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Compute strategy '{strategyId}' is not registered. " +
                $"Call RegisterComputeStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Executes a compute workload using the specified or default compute strategy.
    /// Resolves the strategy from the typed <see cref="ComputeStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="workload">The workload parameters including code, language, and input data.</param>
    /// <param name="strategyId">Optional strategy ID. When null, falls back to RuntimeType.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A compute result with output data and execution metrics.</returns>
    protected virtual async Task<ComputeResult> ExecuteWorkloadWithStrategyAsync(
        Dictionary<string, object> workload,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workload);

        return await DispatchComputeStrategyAsync<ComputeResult>(
            strategyId,
            identity,
            workload,
            async strategy =>
            {
                var id = workload.TryGetValue("id", out var wid) ? wid?.ToString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                var language = workload.TryGetValue("language", out var lang) ? lang?.ToString() ?? "unknown" : "unknown";
                var code = workload.TryGetValue("code", out var c) && c is string codeStr
                    ? System.Text.Encoding.UTF8.GetBytes(codeStr)
                    : Array.Empty<byte>();

                var task = new ComputeTask(
                    Id: id,
                    Code: code,
                    Language: language);

                return await strategy.ExecuteAsync(task, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="RuntimeType"/> to route compute dispatches to the default runtime.</remarks>
    protected override string? GetDefaultStrategyId() => RuntimeType;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["RuntimeType"] = RuntimeType;
        return metadata;
    }
}
