namespace DataWarehouse.SDK.Contracts.Media;

/// <summary>
/// Describes the capabilities of a media processing strategy, including supported formats,
/// streaming capabilities, and technical limits.
/// </summary>
/// <param name="SupportedInputFormats">
/// The set of media formats that can be processed as input.
/// </param>
/// <param name="SupportedOutputFormats">
/// The set of media formats that can be produced as output.
/// </param>
/// <param name="SupportsStreaming">
/// Indicates whether the strategy supports media streaming protocols (HLS, DASH).
/// </param>
/// <param name="SupportsAdaptiveBitrate">
/// Indicates whether the strategy supports adaptive bitrate streaming with multiple quality levels.
/// </param>
/// <param name="MaxResolution">
/// The maximum video resolution supported (e.g., 3840x2160 for 4K).
/// Null if there is no resolution limit.
/// </param>
/// <param name="MaxBitrate">
/// The maximum bitrate supported in bits per second.
/// Null if there is no bitrate limit.
/// </param>
/// <param name="SupportedCodecs">
/// The set of video and audio codecs supported for encoding/decoding.
/// Empty set if codec information is not available.
/// </param>
/// <param name="SupportsThumbnailGeneration">
/// Indicates whether the strategy supports generating thumbnails from video content.
/// </param>
/// <param name="SupportsMetadataExtraction">
/// Indicates whether the strategy supports extracting metadata from media files.
/// </param>
/// <param name="SupportsHardwareAcceleration">
/// Indicates whether the strategy can utilize hardware acceleration (GPU encoding/decoding).
/// </param>
/// <remarks>
/// <para>
/// This record is immutable and should be constructed once during strategy initialization.
/// </para>
/// <para>
/// <strong>Example Usage:</strong>
/// <code>
/// var capabilities = new MediaCapabilities(
///     SupportedInputFormats: new[] { MediaFormat.MP4, MediaFormat.WebM },
///     SupportedOutputFormats: new[] { MediaFormat.HLS, MediaFormat.DASH },
///     SupportsStreaming: true,
///     SupportsAdaptiveBitrate: true,
///     MaxResolution: new Resolution(3840, 2160),
///     MaxBitrate: 50_000_000,
///     SupportedCodecs: new[] { "h264", "h265", "vp9", "aac", "opus" },
///     SupportsThumbnailGeneration: true,
///     SupportsMetadataExtraction: true,
///     SupportsHardwareAcceleration: true
/// );
/// </code>
/// </para>
/// </remarks>
public sealed record MediaCapabilities(
    IReadOnlySet<MediaFormat> SupportedInputFormats,
    IReadOnlySet<MediaFormat> SupportedOutputFormats,
    bool SupportsStreaming,
    bool SupportsAdaptiveBitrate,
    Resolution? MaxResolution,
    long? MaxBitrate,
    IReadOnlySet<string> SupportedCodecs,
    bool SupportsThumbnailGeneration,
    bool SupportsMetadataExtraction,
    bool SupportsHardwareAcceleration)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediaCapabilities"/> record with minimal capabilities.
    /// </summary>
    public MediaCapabilities()
        : this(
            SupportedInputFormats: new HashSet<MediaFormat>(),
            SupportedOutputFormats: new HashSet<MediaFormat>(),
            SupportsStreaming: false,
            SupportsAdaptiveBitrate: false,
            MaxResolution: null,
            MaxBitrate: null,
            SupportedCodecs: new HashSet<string>(),
            SupportsThumbnailGeneration: false,
            SupportsMetadataExtraction: false,
            SupportsHardwareAcceleration: false)
    {
    }

    /// <summary>
    /// Determines whether the specified input and output formats are both supported.
    /// </summary>
    /// <param name="inputFormat">The input format to check.</param>
    /// <param name="outputFormat">The output format to check.</param>
    /// <returns>
    /// <c>true</c> if both formats are supported; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsTranscode(MediaFormat inputFormat, MediaFormat outputFormat)
        => SupportedInputFormats.Contains(inputFormat) && SupportedOutputFormats.Contains(outputFormat);

    /// <summary>
    /// Determines whether the specified resolution is within the maximum supported resolution.
    /// </summary>
    /// <param name="resolution">The resolution to check.</param>
    /// <returns>
    /// <c>true</c> if the resolution is supported or no limit is defined; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsResolution(Resolution resolution)
    {
        if (MaxResolution is null)
            return true;

        return resolution.Width <= MaxResolution.Value.Width && resolution.Height <= MaxResolution.Value.Height;
    }

    /// <summary>
    /// Determines whether the specified bitrate is within the maximum supported bitrate.
    /// </summary>
    /// <param name="bitrate">The bitrate to check in bits per second.</param>
    /// <returns>
    /// <c>true</c> if the bitrate is supported or no limit is defined; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsBitrate(long bitrate)
    {
        if (MaxBitrate is null)
            return true;

        return bitrate <= MaxBitrate.Value;
    }

    /// <summary>
    /// Determines whether the specified codec is supported.
    /// </summary>
    /// <param name="codec">The codec name to check (case-insensitive).</param>
    /// <returns>
    /// <c>true</c> if the codec is supported; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsCodec(string codec)
        => SupportedCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase);
}
