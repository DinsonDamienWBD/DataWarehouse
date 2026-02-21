using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data transit pipeline plugins. Moves data between nodes/sites.
/// </summary>
public abstract class DataTransitPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <summary>Transport protocol (e.g., "tcp", "quic", "rdma").</summary>
    public virtual string TransportProtocol => "tcp";

    /// <summary>Transfer data to a target.</summary>
    public abstract Task<Dictionary<string, object>> TransferAsync(string key, Dictionary<string, object> target, CancellationToken ct = default);

    /// <summary>Get transfer status.</summary>
    public abstract Task<Dictionary<string, object>> GetTransferStatusAsync(string transferId, CancellationToken ct = default);

    #region Typed Transit Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IDataTransitStrategy"/> instances.
    /// Uses its own dedicated registry to support typed dispatch beyond the PluginBase IStrategy registry.
    /// </summary>
    private StrategyRegistry<IDataTransitStrategy>? _transitStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_transitStrategyRegistry"/>.</summary>
    private readonly object _transitRegistryLock = new();

    /// <summary>
    /// Gets the typed transit strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IDataTransitStrategy> TransitStrategyRegistry
    {
        get
        {
            if (_transitStrategyRegistry is not null) return _transitStrategyRegistry;
            lock (_transitRegistryLock)
            {
                _transitStrategyRegistry ??= new StrategyRegistry<IDataTransitStrategy>(s => s.StrategyId);
            }
            return _transitStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a data transit strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The transit strategy to register.</param>
    protected void RegisterTransitStrategy(IDataTransitStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        TransitStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the optimal transit strategy. Routes through the typed
    /// <see cref="TransitStrategyRegistry"/> with CommandIdentity ACL enforcement.
    /// Falls back to <see cref="TransportProtocol"/> when no explicit strategy is given.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use TransportProtocol as default).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the operation for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved transit strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchTransitStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IDataTransitStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : TransportProtocol;

        if (string.IsNullOrEmpty(strategyId))
            throw new InvalidOperationException(
                $"No transit strategy ID provided and no default strategy is configured for plugin '{Id}'.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use transit strategy '{strategyId}'.");
        }

        var strategy = TransitStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Transit strategy '{strategyId}' is not registered. " +
                $"Call RegisterTransitStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Transfers data using the specified or default transit strategy.
    /// Resolves the strategy from the typed <see cref="TransitStrategyRegistry"/> with
    /// optional CommandIdentity ACL. Falls back to <see cref="TransportProtocol"/> as the default.
    /// </summary>
    /// <param name="key">The data key to transfer.</param>
    /// <param name="target">Target endpoint parameters. May include "uri" for endpoint URI, "protocol" for override.</param>
    /// <param name="strategyId">Optional strategy ID. When null, TransportProtocol applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer result dictionary with status and transfer ID.</returns>
    protected virtual Task<Dictionary<string, object>> TransferWithStrategyAsync(
        string key,
        Dictionary<string, object> target,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(target);

        var targetUri = target.TryGetValue("uri", out var uriObj) && uriObj is string uriStr
            ? new Uri(uriStr)
            : new Uri($"transit://{Id}/{key}");

        var request = new TransitRequest
        {
            TransferId = Guid.NewGuid().ToString("N"),
            Source = new TransitEndpoint { Uri = new Uri($"local://{Id}/{key}") },
            Destination = new TransitEndpoint { Uri = targetUri },
            Metadata = new Dictionary<string, string> { ["key"] = key }
        };

        return DispatchTransitStrategyAsync<Dictionary<string, object>>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "transfer",
                ["key"] = key,
                ["targetCount"] = target.Count
            },
            async strategy =>
            {
                var result = await strategy.TransferAsync(request, null, ct).ConfigureAwait(false);
                return new Dictionary<string, object>
                {
                    ["transferId"] = result.TransferId,
                    ["success"] = result.Success,
                    ["bytesTransferred"] = result.BytesTransferred,
                    ["key"] = key
                };
            },
            ct);
    }

    /// <summary>
    /// Gets the transfer status using the specified or default transit strategy.
    /// Resolves the strategy from the typed <see cref="TransitStrategyRegistry"/> with
    /// optional CommandIdentity ACL.
    /// </summary>
    /// <param name="transferId">The unique identifier of the transfer to check.</param>
    /// <param name="strategyId">Optional strategy ID. When null, TransportProtocol applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer health status as a dictionary.</returns>
    protected virtual Task<Dictionary<string, object>> GetTransferStatusWithStrategyAsync(
        string transferId,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transferId);

        return DispatchTransitStrategyAsync<Dictionary<string, object>>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "transferStatus",
                ["transferId"] = transferId
            },
            async strategy =>
            {
                var health = await strategy.GetHealthAsync(ct).ConfigureAwait(false);
                return new Dictionary<string, object>
                {
                    ["transferId"] = transferId,
                    ["isHealthy"] = health.IsHealthy,
                    ["activeTransfers"] = health.ActiveTransfers,
                    ["strategy"] = strategyId ?? TransportProtocol
                };
            },
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="TransportProtocol"/> as the default strategy ID.</remarks>
    protected override string? GetDefaultStrategyId() => TransportProtocol;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TransportProtocol"] = TransportProtocol;
        return metadata;
    }
}
