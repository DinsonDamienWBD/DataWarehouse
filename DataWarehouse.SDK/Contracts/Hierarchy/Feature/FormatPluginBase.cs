using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.DataFormat;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for data format plugins (columnar, graph, scientific, lakehouse).
/// Provides typed strategy dispatch for <see cref="IDataFormatStrategy"/> implementations,
/// with domain operations for serialization and deserialization.
/// </summary>
public abstract class FormatPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Format";

    /// <summary>Format family (e.g., "Columnar", "Graph", "Scientific", "Lakehouse").</summary>
    public abstract string FormatFamily { get; }

    #region Typed Format Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IDataFormatStrategy"/> instances.
    /// IDataFormatStrategy does not inherit IStrategy, so it uses its own dedicated registry
    /// rather than the PluginBase IStrategy registry.
    /// </summary>
    private StrategyRegistry<IDataFormatStrategy>? _formatStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_formatStrategyRegistry"/>.</summary>
    private readonly object _formatRegistryLock = new();

    /// <summary>
    /// Gets the typed format strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IDataFormatStrategy> FormatStrategyRegistry
    {
        get
        {
            if (_formatStrategyRegistry is not null) return _formatStrategyRegistry;
            lock (_formatRegistryLock)
            {
                _formatStrategyRegistry ??= new StrategyRegistry<IDataFormatStrategy>(s => s.StrategyId);
            }
            return _formatStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a data format strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The data format strategy to register.</param>
    protected void RegisterFormatStrategy(IDataFormatStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        FormatStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the optimal data format strategy, with optional CommandIdentity ACL enforcement.
    /// Routes through the typed <see cref="FormatStrategyRegistry"/>.
    /// When no explicit strategy is specified, falls back to <see cref="FormatFamily"/> as the strategy ID.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use FormatFamily).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="operation">The operation to execute on the resolved format strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchFormatStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Func<IDataFormatStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : (GetDefaultStrategyId() ?? FormatFamily);

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use format strategy '{strategyId}'.");
        }

        var strategy = FormatStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Format strategy '{strategyId}' is not registered. " +
                $"Call RegisterFormatStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Serializes an object to bytes using the specified or default format strategy.
    /// Resolves the strategy from the typed <see cref="FormatStrategyRegistry"/> with optional ACL enforcement.
    /// </summary>
    /// <param name="data">The object to serialize.</param>
    /// <param name="strategyId">Optional strategy ID. When null, uses <see cref="FormatFamily"/>.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized bytes.</returns>
    protected async Task<byte[]> SerializeWithStrategyAsync(
        object data,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return await DispatchFormatStrategyAsync<byte[]>(
            strategyId,
            identity,
            async strategy =>
            {
                using var ms = new MemoryStream();
                var context = new DataFormatContext { ExtractMetadata = false };
                var result = await strategy.SerializeAsync(data, ms, context, ct).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException(
                        $"Format strategy '{strategy.StrategyId}' serialization failed: {result.ErrorMessage}");
                return ms.ToArray();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes bytes to an object using the specified or default format strategy.
    /// Resolves the strategy from the typed <see cref="FormatStrategyRegistry"/> with optional ACL enforcement.
    /// </summary>
    /// <param name="data">The bytes to deserialize.</param>
    /// <param name="strategyId">Optional strategy ID. When null, uses <see cref="FormatFamily"/>.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deserialized object.</returns>
    protected async Task<object> DeserializeWithStrategyAsync(
        byte[] data,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return await DispatchFormatStrategyAsync<object>(
            strategyId,
            identity,
            async strategy =>
            {
                using var ms = new MemoryStream(data);
                var context = new DataFormatContext { ExtractMetadata = true };
                var result = await strategy.ParseAsync(ms, context, ct).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException(
                        $"Format strategy '{strategy.StrategyId}' deserialization failed: {result.ErrorMessage}");
                return result.Data ?? new Dictionary<string, object>();
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="FormatFamily"/> to route format dispatches to the correct strategy.</remarks>
    protected override string? GetDefaultStrategyId() => FormatFamily;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FormatFamily"] = FormatFamily;
        return metadata;
    }
}
