using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Streaming;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for real-time data streaming plugins.
/// </summary>
public abstract class StreamingPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Streaming";

    /// <summary>Publish data to a topic.</summary>
    public abstract Task PublishAsync(string topic, Stream data, CancellationToken ct = default);

    /// <summary>Subscribe to a topic.</summary>
    public abstract IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, CancellationToken ct = default);

    #region Typed Streaming Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IStreamingStrategy"/> instances.
    /// </summary>
    private StrategyRegistry<IStreamingStrategy>? _streamingStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_streamingStrategyRegistry"/>.</summary>
    private readonly object _streamingRegistryLock = new();

    /// <summary>
    /// Gets the typed streaming strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IStreamingStrategy> StreamingStrategyRegistry
    {
        get
        {
            if (_streamingStrategyRegistry is not null) return _streamingStrategyRegistry;
            lock (_streamingRegistryLock)
            {
                _streamingStrategyRegistry ??= new StrategyRegistry<IStreamingStrategy>(s => s.StrategyId);
            }
            return _streamingStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a streaming strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The streaming strategy to register.</param>
    protected void RegisterStreamingStrategy(IStreamingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        StreamingStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the specified or default streaming strategy,
    /// with optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the request for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved streaming strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchStreamingStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IStreamingStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : GetDefaultStrategyId()
                ?? throw new InvalidOperationException(
                    "No strategy specified and no default configured. " +
                    "Call RegisterStreamingStrategy and specify a strategyId, or configure a default.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use streaming strategy '{strategyId}'.");
        }

        var strategy = StreamingStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Streaming strategy '{strategyId}' is not registered. " +
                $"Call RegisterStreamingStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Publishes data to a topic using the specified or default streaming strategy.
    /// Resolves the strategy from the typed <see cref="StreamingStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="topic">The topic/stream name to publish to.</param>
    /// <param name="data">The data stream to publish.</param>
    /// <param name="strategyId">Optional strategy ID. When null, default strategy applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A publish result with message ID and metadata.</returns>
    protected virtual async Task<Streaming.PublishResult> PublishWithStrategyAsync(
        string topic,
        Stream data,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(data);

        return await DispatchStreamingStrategyAsync<Streaming.PublishResult>(
            strategyId,
            identity,
            new Dictionary<string, object> { ["topic"] = topic },
            async strategy =>
            {
                // Read stream data into bytes for the StreamMessage
                using var ms = new MemoryStream();
                await data.CopyToAsync(ms, ct).ConfigureAwait(false);
                var message = new StreamMessage { Data = ms.ToArray() };
                return await strategy.PublishAsync(topic, message, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes to a topic using the specified or default streaming strategy.
    /// Resolves the strategy from the typed <see cref="StreamingStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="topic">The topic/stream name to subscribe to.</param>
    /// <param name="strategyId">Optional strategy ID. When null, default strategy applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of stream messages from the strategy.</returns>
    protected virtual async Task<IAsyncEnumerable<StreamMessage>> SubscribeWithStrategyAsync(
        string topic,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        return await DispatchStreamingStrategyAsync<IAsyncEnumerable<StreamMessage>>(
            strategyId,
            identity,
            new Dictionary<string, object> { ["topic"] = topic },
            strategy =>
            {
                var subscription = strategy.SubscribeAsync(topic, null, null, ct);
                return Task.FromResult(subscription);
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
        metadata["SupportsStreaming"] = true;
        return metadata;
    }
}
