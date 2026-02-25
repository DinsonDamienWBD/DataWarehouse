using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for replication pipeline plugins. Distributes data across nodes.
/// </summary>
public abstract class ReplicationPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>Default replication mode (Sync or Async).</summary>
    public virtual string DefaultReplicationMode => "Async";

    /// <summary>Replicate an object to target nodes.</summary>
    public abstract Task<Dictionary<string, object>> ReplicateAsync(string key, string[] targetNodes, CancellationToken ct = default);

    /// <summary>Get sync status for a key.</summary>
    public abstract Task<Dictionary<string, object>> GetSyncStatusAsync(string key, CancellationToken ct = default);

    #region Typed Replication Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IReplicationStrategy"/> instances.
    /// Uses consistency model as the key since <see cref="IReplicationStrategy"/> exposes
    /// <see cref="IReplicationStrategy.ConsistencyModel"/> which uniquely identifies a strategy mode.
    /// </summary>
    private StrategyRegistry<IReplicationStrategy>? _replicationStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_replicationStrategyRegistry"/>.</summary>
    private readonly object _replicationRegistryLock = new();

    /// <summary>
    /// Gets the typed replication strategy registry. Lazily initialized on first access.
    /// The key selector uses the strategy's ConsistencyModel string representation,
    /// which matches the <see cref="DefaultReplicationMode"/> convention.
    /// </summary>
    protected StrategyRegistry<IReplicationStrategy> ReplicationStrategyRegistry
    {
        get
        {
            if (_replicationStrategyRegistry is not null) return _replicationStrategyRegistry;
            lock (_replicationRegistryLock)
            {
                _replicationStrategyRegistry ??= new StrategyRegistry<IReplicationStrategy>(
                    s => s.ConsistencyModel.ToString());
            }
            return _replicationStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a replication strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The replication strategy to register.</param>
    protected void RegisterReplicationStrategy(IReplicationStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ReplicationStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the optimal replication strategy. Routes through the typed
    /// <see cref="ReplicationStrategyRegistry"/> with CommandIdentity ACL enforcement.
    /// Falls back to <see cref="DefaultReplicationMode"/> when no explicit strategy is given.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use DefaultReplicationMode).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the operation for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved replication strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchReplicationStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IReplicationStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : DefaultReplicationMode;

        if (string.IsNullOrEmpty(strategyId))
            throw new InvalidOperationException(
                $"No replication strategy ID provided and no default strategy is configured for plugin '{Id}'.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use replication strategy '{strategyId}'.");
        }

        var strategy = ReplicationStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Replication strategy '{strategyId}' is not registered. " +
                $"Call RegisterReplicationStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Replicates data to target nodes using the specified or default replication strategy.
    /// Resolves the strategy from the typed <see cref="ReplicationStrategyRegistry"/> with
    /// optional CommandIdentity ACL. Falls back to <see cref="DefaultReplicationMode"/>.
    /// </summary>
    /// <param name="key">The data key to replicate.</param>
    /// <param name="targetNodes">The target node identifiers.</param>
    /// <param name="strategyId">Optional strategy ID. When null, DefaultReplicationMode applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Replication result dictionary.</returns>
    protected virtual Task<Dictionary<string, object>> ReplicateWithStrategyAsync(
        string key,
        string[] targetNodes,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(targetNodes);

        return DispatchReplicationStrategyAsync<Dictionary<string, object>>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "replicate",
                ["key"] = key,
                ["targetNodeCount"] = targetNodes.Length
            },
            strategy => strategy.ReplicateAsync(
                Id,
                targetNodes,
                ReadOnlyMemory<byte>.Empty,
                new Dictionary<string, string> { ["key"] = key },
                ct).ContinueWith(_ => new Dictionary<string, object>
                {
                    ["key"] = key,
                    ["targetNodes"] = targetNodes,
                    ["status"] = "replicated"
                }, ct, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion, System.Threading.Tasks.TaskScheduler.Default),
            ct);
    }

    /// <summary>
    /// Gets the synchronization status for a key using the specified or default replication strategy.
    /// Resolves the strategy from the typed <see cref="ReplicationStrategyRegistry"/> with
    /// optional CommandIdentity ACL.
    /// </summary>
    /// <param name="key">The data key to check.</param>
    /// <param name="strategyId">Optional strategy ID. When null, DefaultReplicationMode applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sync status result dictionary.</returns>
    protected virtual Task<Dictionary<string, object>> GetSyncStatusWithStrategyAsync(
        string key,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return DispatchReplicationStrategyAsync<Dictionary<string, object>>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "syncStatus",
                ["key"] = key
            },
            strategy => strategy.VerifyConsistencyAsync(Array.Empty<string>(), key, ct)
                .ContinueWith(t => new Dictionary<string, object>
                {
                    ["key"] = key,
                    ["consistent"] = t.Result,
                    ["strategy"] = strategyId ?? DefaultReplicationMode
                }, ct, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion, System.Threading.Tasks.TaskScheduler.Default),
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="DefaultReplicationMode"/> as the default strategy ID.</remarks>
    protected override string? GetDefaultStrategyId() => DefaultReplicationMode;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["DefaultReplicationMode"] = DefaultReplicationMode;
        return metadata;
    }
}
