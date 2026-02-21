using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Media;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for media processing plugins (transcoding, image processing, audio).
/// </summary>
public abstract class MediaPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Media";

    /// <summary>Media type handled (e.g., "Video", "Image", "Audio").</summary>
    public abstract string MediaType { get; }

    #region Typed Media Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IMediaStrategy"/> instances.
    /// Key selector uses StrategyId from MediaStrategyBase, falling back to type name.
    /// </summary>
    private StrategyRegistry<IMediaStrategy>? _mediaStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_mediaStrategyRegistry"/>.</summary>
    private readonly object _mediaRegistryLock = new();

    /// <summary>
    /// Gets the typed media strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IMediaStrategy> MediaStrategyRegistry
    {
        get
        {
            if (_mediaStrategyRegistry is not null) return _mediaStrategyRegistry;
            lock (_mediaRegistryLock)
            {
                _mediaStrategyRegistry ??= new StrategyRegistry<IMediaStrategy>(
                    s => (s as MediaStrategyBase)?.StrategyId ?? s.GetType().Name);
            }
            return _mediaStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a media strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The media strategy to register.</param>
    protected void RegisterMediaStrategy(IMediaStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        MediaStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the specified or default media strategy,
    /// with optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the request for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved media strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchMediaStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IMediaStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : GetDefaultStrategyId()
                ?? throw new InvalidOperationException(
                    "No strategy specified and no default configured. " +
                    "Call RegisterMediaStrategy and specify a strategyId, or configure a default.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use media strategy '{strategyId}'.");
        }

        var strategy = MediaStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Media strategy '{strategyId}' is not registered. " +
                $"Call RegisterMediaStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Processes media using the specified or default media strategy.
    /// Resolves the strategy from the typed <see cref="MediaStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="media">The media parameters including input stream and processing options.</param>
    /// <param name="strategyId">Optional strategy ID. When null, falls back to MediaType.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream containing the processed media output.</returns>
    protected virtual async Task<Stream> ProcessMediaWithStrategyAsync(
        Dictionary<string, object> media,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        return await DispatchMediaStrategyAsync<Stream>(
            strategyId,
            identity,
            media,
            async strategy =>
            {
                // Extract input stream and transcoding options from media context
                var inputStream = media.TryGetValue("inputStream", out var s) && s is Stream stream
                    ? stream
                    : Stream.Null;

                var targetFormat = media.TryGetValue("targetFormat", out var tf) && tf is Media.MediaFormat fmt
                    ? fmt
                    : Media.MediaFormat.MP4;

                var options = new Media.TranscodeOptions(TargetFormat: targetFormat);

                return await strategy.TranscodeAsync(inputStream, options, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="MediaType"/> to route media dispatches to the default media strategy.</remarks>
    protected override string? GetDefaultStrategyId() => MediaType;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["MediaType"] = MediaType;
        return metadata;
    }
}
