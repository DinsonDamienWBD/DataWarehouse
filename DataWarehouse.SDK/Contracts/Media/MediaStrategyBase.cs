using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.SDK.Contracts.Media;

/// <summary>
/// Abstract base class for media strategy implementations providing common infrastructure
/// for transcoding, metadata extraction, thumbnail generation, and streaming.
/// </summary>
/// <remarks>
/// <para>
/// This base class handles:
/// <list type="bullet">
/// <item><description>Input validation for all media operations</description></item>
/// <item><description>Capability checks before dispatching to core implementations</description></item>
/// <item><description>Thread-safe operation with proper error handling</description></item>
/// <item><description>Intelligence integration for AI-enhanced media processing</description></item>
/// </list>
/// </para>
/// <para>
/// Derived classes must implement the abstract core methods for transcoding, metadata extraction,
/// thumbnail generation, and streaming, and provide their specific capabilities via the constructor.
/// </para>
/// </remarks>
public abstract class MediaStrategyBase : IMediaStrategy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStrategyBase"/> class.
    /// </summary>
    /// <param name="capabilities">The capabilities supported by this media strategy.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="capabilities"/> is null.</exception>
    protected MediaStrategyBase(MediaCapabilities capabilities)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <inheritdoc/>
    public MediaCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the unique identifier for this media strategy.
    /// </summary>
    public abstract string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable name of this media strategy.
    /// </summary>
    public abstract string Name { get; }

    #region IMediaStrategy Implementation

    /// <inheritdoc/>
    public async Task<Stream> TranscodeAsync(
        Stream inputStream,
        TranscodeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(options);

        if (!Capabilities.SupportedInputFormats.Contains(options.TargetFormat) &&
            !Capabilities.SupportedOutputFormats.Contains(options.TargetFormat))
        {
            throw new NotSupportedException(
                $"Media format {options.TargetFormat} is not supported by {Name}.");
        }

        if (options.VideoCodec != null && !Capabilities.SupportsCodec(options.VideoCodec))
        {
            throw new NotSupportedException(
                $"Video codec '{options.VideoCodec}' is not supported by {Name}.");
        }

        if (options.AudioCodec != null && !Capabilities.SupportsCodec(options.AudioCodec))
        {
            throw new NotSupportedException(
                $"Audio codec '{options.AudioCodec}' is not supported by {Name}.");
        }

        if (options.TargetResolution.HasValue && !Capabilities.SupportsResolution(options.TargetResolution.Value))
        {
            throw new NotSupportedException(
                $"Resolution {options.TargetResolution.Value} exceeds maximum supported by {Name}.");
        }

        return await TranscodeAsyncCore(inputStream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MediaMetadata> ExtractMetadataAsync(
        Stream mediaStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaStream);

        if (!Capabilities.SupportsMetadataExtraction)
        {
            throw new NotSupportedException(
                $"Metadata extraction is not supported by {Name}.");
        }

        return await ExtractMetadataAsyncCore(mediaStream, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Stream> GenerateThumbnailAsync(
        Stream videoStream,
        TimeSpan timeOffset,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoStream);

        if (!Capabilities.SupportsThumbnailGeneration)
        {
            throw new NotSupportedException(
                $"Thumbnail generation is not supported by {Name}.");
        }

        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be greater than zero.");

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than zero.");

        if (timeOffset < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeOffset), "Time offset must be non-negative.");

        return await GenerateThumbnailAsyncCore(videoStream, timeOffset, width, height, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Uri> StreamAsync(
        Stream mediaStream,
        MediaFormat targetFormat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaStream);

        if (!Capabilities.SupportsStreaming)
        {
            throw new NotSupportedException(
                $"Streaming is not supported by {Name}.");
        }

        return await StreamAsyncCore(mediaStream, targetFormat, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Abstract Core Methods

    /// <summary>
    /// Core transcoding implementation. Called after input validation and capability checks.
    /// </summary>
    /// <param name="inputStream">The validated input media stream.</param>
    /// <param name="options">The validated transcoding options.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The transcoded media stream.</returns>
    protected abstract Task<Stream> TranscodeAsyncCore(
        Stream inputStream,
        TranscodeOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Core metadata extraction implementation. Called after input validation and capability checks.
    /// </summary>
    /// <param name="mediaStream">The validated media stream.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The extracted media metadata.</returns>
    protected abstract Task<MediaMetadata> ExtractMetadataAsyncCore(
        Stream mediaStream,
        CancellationToken cancellationToken);

    /// <summary>
    /// Core thumbnail generation implementation. Called after input validation and capability checks.
    /// </summary>
    /// <param name="videoStream">The validated video stream.</param>
    /// <param name="timeOffset">The validated time offset.</param>
    /// <param name="width">The validated width in pixels.</param>
    /// <param name="height">The validated height in pixels.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The thumbnail image stream.</returns>
    protected abstract Task<Stream> GenerateThumbnailAsyncCore(
        Stream videoStream,
        TimeSpan timeOffset,
        int width,
        int height,
        CancellationToken cancellationToken);

    /// <summary>
    /// Core streaming implementation. Called after input validation and capability checks.
    /// </summary>
    /// <param name="mediaStream">The validated source media stream.</param>
    /// <param name="targetFormat">The target streaming format.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The streaming manifest or playlist URI.</returns>
    protected abstract Task<Uri> StreamAsyncCore(
        Stream mediaStream,
        MediaFormat targetFormat,
        CancellationToken cancellationToken);

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Gets the message bus for Intelligence communication.
    /// </summary>
    protected IMessageBus? MessageBus { get; private set; }

    /// <summary>
    /// Configures Intelligence integration for this media strategy.
    /// </summary>
    /// <param name="messageBus">Optional message bus for Intelligence communication.</param>
    public virtual void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    /// <summary>
    /// Gets a value indicating whether Intelligence integration is available.
    /// </summary>
    protected bool IsIntelligenceAvailable => MessageBus != null;

    /// <summary>
    /// Gets static knowledge about this media strategy for Intelligence registration.
    /// </summary>
    /// <returns>A KnowledgeObject describing this strategy's capabilities.</returns>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"media.{StrategyId}",
            Topic = "media.strategy",
            SourcePluginId = "sdk.media",
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"{Name} media processing strategy for transcoding, streaming, and metadata",
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["supportsStreaming"] = Capabilities.SupportsStreaming,
                ["supportsAdaptiveBitrate"] = Capabilities.SupportsAdaptiveBitrate,
                ["supportsHardwareAcceleration"] = Capabilities.SupportsHardwareAcceleration,
                ["supportsThumbnails"] = Capabilities.SupportsThumbnailGeneration
            },
            Tags = new[] { "media", "transcoding", "streaming", "strategy" }
        };
    }

    /// <summary>
    /// Gets the registered capability for this media strategy.
    /// </summary>
    /// <returns>A RegisteredCapability describing this strategy.</returns>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"media.{StrategyId}",
            DisplayName = Name,
            Description = $"{Name} media processing strategy",
            Category = CapabilityCategory.DataManagement,
            SubCategory = "Media",
            PluginId = "sdk.media",
            PluginName = Name,
            PluginVersion = "1.0.0",
            Tags = new[] { "media", "transcoding", "streaming" },
            SemanticDescription = $"Use {Name} for media transcoding, streaming, and metadata extraction"
        };
    }

    #endregion
}
