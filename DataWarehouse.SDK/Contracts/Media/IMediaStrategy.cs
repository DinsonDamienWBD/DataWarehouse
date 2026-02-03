namespace DataWarehouse.SDK.Contracts.Media;

/// <summary>
/// Defines the contract for media processing strategies that handle transcoding,
/// streaming, metadata extraction, and thumbnail generation across various media formats.
/// </summary>
/// <remarks>
/// <para>
/// Implementations provide media processing capabilities including:
/// <list type="bullet">
/// <item><description>Video and audio transcoding with format conversion</description></item>
/// <item><description>Metadata extraction from media containers</description></item>
/// <item><description>Thumbnail generation for preview purposes</description></item>
/// <item><description>Streaming with adaptive bitrate support</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Strategy Examples:</strong>
/// <list type="bullet">
/// <item><description>FFmpeg-based local transcoding</description></item>
/// <item><description>AWS Elemental MediaConvert for cloud processing</description></item>
/// <item><description>Azure Media Services integration</description></item>
/// <item><description>GCP Transcoder API implementation</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Implementations should be thread-safe for concurrent media operations.
/// </para>
/// </remarks>
public interface IMediaStrategy
{
    /// <summary>
    /// Gets the capabilities of this media processing strategy, including supported formats,
    /// streaming capabilities, and resolution/bitrate limits.
    /// </summary>
    /// <value>
    /// A <see cref="MediaCapabilities"/> instance describing the strategy's capabilities.
    /// </value>
    MediaCapabilities Capabilities { get; }

    /// <summary>
    /// Transcodes media from one format to another asynchronously.
    /// </summary>
    /// <param name="inputStream">The input media stream to transcode.</param>
    /// <param name="options">Transcoding options including target format, codec, resolution, and bitrate.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the transcoded media stream.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inputStream"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the requested format or codec is not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when transcoding fails due to invalid input or processing errors.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The input stream is not disposed by this method. Callers are responsible for
    /// stream lifecycle management.
    /// </para>
    /// <para>
    /// For large files, consider using progress reporting via <see cref="TranscodeOptions.ProgressCallback"/>.
    /// </para>
    /// </remarks>
    Task<Stream> TranscodeAsync(
        Stream inputStream,
        TranscodeOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts metadata from a media file asynchronously.
    /// </summary>
    /// <param name="mediaStream">The media stream to analyze.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the extracted media metadata including duration, codecs, resolution, and bitrate.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="mediaStream"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the media format cannot be parsed or is corrupted.
    /// </exception>
    /// <remarks>
    /// The media stream position may be reset during analysis. The stream is not disposed.
    /// </remarks>
    Task<MediaMetadata> ExtractMetadataAsync(
        Stream mediaStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a thumbnail image from a video at the specified time offset.
    /// </summary>
    /// <param name="videoStream">The video stream to extract a thumbnail from.</param>
    /// <param name="timeOffset">The time offset at which to capture the thumbnail.</param>
    /// <param name="width">The desired thumbnail width in pixels.</param>
    /// <param name="height">The desired thumbnail height in pixels.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the thumbnail image as a stream (typically JPEG or PNG format).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="videoStream"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeOffset"/> exceeds the video duration,
    /// or when <paramref name="width"/> or <paramref name="height"/> is less than or equal to zero.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the video format does not support thumbnail extraction.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The aspect ratio is preserved by default. If width and height don't match
    /// the video aspect ratio, the thumbnail may be letterboxed or pillarboxed.
    /// </para>
    /// <para>
    /// For efficiency, consider extracting thumbnails at keyframe positions.
    /// </para>
    /// </remarks>
    Task<Stream> GenerateThumbnailAsync(
        Stream videoStream,
        TimeSpan timeOffset,
        int width,
        int height,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams media content with optional adaptive bitrate support.
    /// </summary>
    /// <param name="mediaStream">The source media stream.</param>
    /// <param name="targetFormat">The target streaming format (e.g., HLS, DASH).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the streaming manifest or playlist URI.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="mediaStream"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the target streaming format is not supported by this strategy.
    /// </exception>
    /// <remarks>
    /// <para>
    /// For adaptive bitrate streaming (HLS/DASH), the strategy may generate multiple
    /// quality variants automatically based on the source media characteristics.
    /// </para>
    /// <para>
    /// The returned URI points to a manifest file (e.g., .m3u8 for HLS, .mpd for DASH).
    /// </para>
    /// </remarks>
    Task<Uri> StreamAsync(
        Stream mediaStream,
        MediaFormat targetFormat,
        CancellationToken cancellationToken = default);
}
