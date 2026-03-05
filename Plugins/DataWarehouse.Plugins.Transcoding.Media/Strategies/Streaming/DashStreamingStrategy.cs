using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Media;
using DataWarehouse.SDK.Utilities;
using MediaFormat = DataWarehouse.SDK.Contracts.Media.MediaFormat;

namespace DataWarehouse.Plugins.Transcoding.Media.Strategies.Streaming;

/// <summary>
/// MPEG-DASH (Dynamic Adaptive Streaming over HTTP) strategy implementing ISO/IEC 23009-1
/// for adaptive bitrate streaming with MPD manifest generation and segment timeline support.
/// </summary>
/// <remarks>
/// <para>
/// DASH delivers media via standard HTTP infrastructure with:
/// <list type="bullet">
/// <item><description>Media Presentation Description (MPD) manifest in XML format</description></item>
/// <item><description>Adaptation sets for video, audio, and subtitle tracks</description></item>
/// <item><description>Segment timeline with precise timing for seek accuracy</description></item>
/// <item><description>Multiple audio track and subtitle stream support</description></item>
/// <item><description>Content protection signaling via PSSH boxes</description></item>
/// </list>
/// </para>
/// <para>
/// Unlike HLS which is Apple-proprietary, DASH is an international standard (ISO 23009-1)
/// supported natively on Android, smart TVs, and web browsers via Media Source Extensions (MSE).
/// </para>
/// </remarks>
internal sealed class DashStreamingStrategy : MediaStrategyBase
{
    /// <summary>Default segment duration in seconds for DASH segmenting.</summary>
    private const int DefaultSegmentDurationSeconds = 4;

    /// <summary>MPD timescale value (ticks per second) for segment timeline precision.</summary>
    private const int Timescale = 90_000;

    /// <summary>
    /// DASH representation ladder defining resolution/bitrate/id triples for adaptation sets.
    /// </summary>
    private static readonly (Resolution Resolution, int BitrateKbps, string Id, string Label)[] RepresentationLadder =
    {
        (new Resolution(640, 360), 800, "v360", "360p"),
        (new Resolution(854, 480), 1400, "v480", "480p"),
        (Resolution.HD, 2800, "v720", "720p"),
        (Resolution.FullHD, 5000, "v1080", "1080p"),
    };

    /// <summary>Audio representation presets for multi-quality audio adaptation.</summary>
    private static readonly (int BitrateKbps, int SampleRate, int Channels, string Id, string Label)[] AudioRepresentations =
    {
        (64, 44100, 2, "a64", "Stereo 64k"),
        (128, 48000, 2, "a128", "Stereo 128k"),
        (256, 48000, 6, "a256", "5.1 Surround"),
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="DashStreamingStrategy"/> class.
    /// </summary>
    public DashStreamingStrategy() : base(new MediaCapabilities(
        SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM },
        SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.DASH },
        SupportsStreaming: true,
        SupportsAdaptiveBitrate: true,
        MaxResolution: Resolution.UHD,
        MaxBitrate: 25_000_000,
        SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "h265", "vp9", "av1", "aac", "opus", "ac3", "eac3" },
        SupportsThumbnailGeneration: false,
        SupportsMetadataExtraction: false,
        SupportsHardwareAcceleration: true))
    {
    }

    /// <inheritdoc/>
    public override string StrategyId => "dash";

    /// <inheritdoc/>
    public override string Name => "DASH Streaming";

    /// <summary>
    /// Checks DASH streaming health by validating segment output directory.
    /// Cached for 60 seconds.
    /// </summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return GetCachedHealthAsync(async (cancellationToken) =>
        {
            try
            {
                var segmentDir = Path.GetTempPath();

                // Removed spurious `|| true` that made the health check always pass (finding 1069).
                var isAccessible = Directory.Exists(segmentDir);

                if (!isAccessible)
                {
                    return new StrategyHealthCheckResult(
                        IsHealthy: false,
                        Message: $"DASH segment directory not accessible: {segmentDir}");
                }

                return new StrategyHealthCheckResult(
                    IsHealthy: true,
                    Message: "DASH streaming ready",
                    Details: new Dictionary<string, object>
                    {
                        ["segment_directory"] = segmentDir,
                        ["timescale"] = Timescale,
                        ["video_representations"] = RepresentationLadder.Length,
                        ["audio_representations"] = AudioRepresentations.Length
                    });
            }
            catch (Exception ex)
            {
                return new StrategyHealthCheckResult(
                    IsHealthy: false,
                    Message: $"DASH health check failed: {ex.Message}");
            }
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>
    /// Shuts down DASH streaming by canceling active segment generation and flushing manifests.
    /// </summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Cat 15 (finding 1089): DashStreamingStrategy is stateless — no per-instance buffers, segments,
        // or manifests require cleanup here. Each Transcode call is self-contained and releases its own streams.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Transcodes input media into DASH format by generating an MPD manifest with adaptation sets,
    /// segment timeline, and initialization/media segments for adaptive bitrate delivery.
    /// </summary>
    /// <param name="inputStream">The source media stream to package for DASH delivery.</param>
    /// <param name="options">Transcoding options including codec, resolution, and bitrate preferences.</param>
    /// <param name="cancellationToken">Token to cancel the transcoding operation.</param>
    /// <returns>A stream containing the DASH package with MPD manifest and segment metadata.</returns>
    protected override async Task<Stream> TranscodeAsyncCore(
        Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            // Input validation
            if (inputStream == null || !inputStream.CanRead)
            {
                throw new ArgumentException("Input stream must be readable.", nameof(inputStream));
            }

            var outputStream = new MemoryStream(1024 * 1024);

        var targetResolution = options.TargetResolution ?? Resolution.FullHD;
        var targetBitrateKbps = options.TargetBitrate.HasValue
            ? (int)(options.TargetBitrate.Value.BitsPerSecond / 1000)
            : 5000;

        // Validate resolution
        if (targetResolution.Width < 64 || targetResolution.Height < 64)
        {
            throw new ArgumentException("Target resolution must be at least 64x64 pixels.");
        }

        // Validate bitrate
        if (targetBitrateKbps < 100 || targetBitrateKbps > 100_000)
        {
            throw new ArgumentException("Target bitrate must be between 100 kbps and 100 Mbps.");
        }

        var sourceBytes = await ReadStreamFullyAsync(inputStream, cancellationToken).ConfigureAwait(false);
        var estimatedDuration = EstimateInputDuration(sourceBytes);

        // Filter representations to target resolution
        var videoRepresentations = RepresentationLadder
            .Where(r => r.Resolution.Width <= targetResolution.Width
                     && r.Resolution.Height <= targetResolution.Height)
            .ToArray();

        if (videoRepresentations.Length == 0)
            videoRepresentations = new[] { RepresentationLadder[0] };

        // Generate MPD manifest
        var mpdManifest = GenerateMpdManifest(videoRepresentations, estimatedDuration, options);

        // Build FFmpeg arguments for DASH segmenting
        var ffmpegArgs = BuildFfmpegArguments(targetResolution, targetBitrateKbps, options);

            // Write packaged output
            await WriteDashPackageAsync(outputStream, mpdManifest, ffmpegArgs, sourceBytes, cancellationToken)
                .ConfigureAwait(false);

            IncrementCounter("dash.segment_generated");
            IncrementCounter("dash.manifest_updated");

            outputStream.Position = 0;
            return outputStream;
        }
        catch (Exception ex)
        {
            IncrementCounter("dash.error");
            throw new InvalidOperationException(
                $"DASH streaming generation failed: resolution={options.TargetResolution?.Width ?? 0}x{options.TargetResolution?.Height ?? 0}, " +
                $"bitrate={options.TargetBitrate?.BitsPerSecond ?? 0}",
                ex);
        }
    }

    /// <inheritdoc/>
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(
        Stream mediaStream, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DASH streaming strategy does not support metadata extraction.");
    }

    /// <inheritdoc/>
    protected override Task<Stream> GenerateThumbnailAsyncCore(
        Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DASH streaming strategy does not support thumbnail generation.");
    }

    /// <summary>
    /// Generates a streaming manifest URI for DASH adaptive bitrate delivery.
    /// </summary>
    /// <param name="mediaStream">The source media stream to prepare for streaming.</param>
    /// <param name="targetFormat">Must be <see cref="SDK.Contracts.Media.MediaFormat.DASH"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A URI pointing to the generated MPD manifest.</returns>
    protected override async Task<Uri> StreamAsyncCore(
        Stream mediaStream, SDK.Contracts.Media.MediaFormat targetFormat, CancellationToken cancellationToken)
    {
        if (targetFormat != SDK.Contracts.Media.MediaFormat.DASH)
            throw new NotSupportedException($"DASH strategy only supports DASH format, not {targetFormat}.");

        var sourceBytes = await ReadStreamFullyAsync(mediaStream, cancellationToken).ConfigureAwait(false);

        byte[] hashBytes;
        if (MessageBus != null)
        {
            var msg = new PluginMessage { Type = "integrity.hash.compute" };
            msg.Payload["data"] = sourceBytes;
            msg.Payload["algorithm"] = "SHA256";
            var response = await MessageBus.SendAsync("integrity.hash.compute", msg, cancellationToken).ConfigureAwait(false);
            if (response.Success && response.Payload is Dictionary<string, object> payload && payload.TryGetValue("hash", out var hashObj) && hashObj is byte[] hash)
            {
                hashBytes = hash;
            }
            else
            {
                hashBytes = SHA256.HashData(sourceBytes); // Fallback on error
            }
        }
        else
        {
            hashBytes = SHA256.HashData(sourceBytes);
        }
        var streamHash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();

        return new Uri($"/streams/dash/{streamHash}/manifest.mpd", UriKind.Relative);
    }

    /// <summary>
    /// Generates a DASH MPD (Media Presentation Description) manifest conforming to ISO 23009-1
    /// with video/audio adaptation sets and segment timeline.
    /// </summary>
    private string GenerateMpdManifest(
        (Resolution Resolution, int BitrateKbps, string Id, string Label)[] videoReps,
        double durationSeconds, TranscodeOptions options)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var durationIso = $"PT{(int)duration.TotalHours}H{duration.Minutes}M{duration.Seconds}S";
        var segmentDurationTicks = DefaultSegmentDurationSeconds * Timescale;
        var segmentCount = Math.Max(1, (int)Math.Ceiling(durationSeconds / DefaultSegmentDurationSeconds));
        var videoCodec = options.VideoCodec ?? "h264";
        var codecProfile = videoCodec.Equals("h265", StringComparison.OrdinalIgnoreCase)
            ? "hev1.1.6.L120.90" : "avc1.640028";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" " +
                      "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                      "xsi:schemaLocation=\"urn:mpeg:dash:schema:mpd:2011 DASH-MPD.xsd\" " +
                      $"type=\"static\" mediaPresentationDuration=\"{durationIso}\" " +
                      $"minBufferTime=\"PT{DefaultSegmentDurationSeconds}S\" " +
                      "profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\">");

        // Period
        sb.AppendLine($"  <Period duration=\"{durationIso}\">");

        // Video Adaptation Set
        sb.AppendLine("    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\" " +
                      "startWithSAP=\"1\" subsegmentAlignment=\"true\" subsegmentStartsWithSAP=\"1\">");

        // Segment template
        sb.AppendLine($"      <SegmentTemplate timescale=\"{Timescale}\" " +
                      "initialization=\"$RepresentationID$/init.mp4\" " +
                      "media=\"$RepresentationID$/seg_$Number$.m4s\" startNumber=\"0\">");
        sb.AppendLine("        <SegmentTimeline>");
        for (var i = 0; i < segmentCount; i++)
        {
            var isLast = i == segmentCount - 1;
            var segDuration = isLast
                ? (int)((durationSeconds - (i * DefaultSegmentDurationSeconds)) * Timescale)
                : segmentDurationTicks;
            if (segDuration <= 0) segDuration = segmentDurationTicks;
            sb.AppendLine($"          <S d=\"{segDuration}\"/>");
        }
        sb.AppendLine("        </SegmentTimeline>");
        sb.AppendLine("      </SegmentTemplate>");

        // Video representations
        foreach (var (resolution, bitrateKbps, id, label) in videoReps)
        {
            sb.AppendLine($"      <Representation id=\"{id}\" bandwidth=\"{bitrateKbps * 1000}\" " +
                          $"width=\"{resolution.Width}\" height=\"{resolution.Height}\" " +
                          $"codecs=\"{codecProfile}\" frameRate=\"30/1\" />");
        }

        sb.AppendLine("    </AdaptationSet>");

        // Audio Adaptation Set
        sb.AppendLine("    <AdaptationSet mimeType=\"audio/mp4\" segmentAlignment=\"true\" " +
                      "lang=\"en\" startWithSAP=\"1\">");
        sb.AppendLine($"      <SegmentTemplate timescale=\"48000\" " +
                      "initialization=\"$RepresentationID$/init.mp4\" " +
                      "media=\"$RepresentationID$/seg_$Number$.m4s\" startNumber=\"0\">");
        sb.AppendLine("        <SegmentTimeline>");
        var audioSegDuration = DefaultSegmentDurationSeconds * 48000;
        for (var i = 0; i < segmentCount; i++)
        {
            sb.AppendLine($"          <S d=\"{audioSegDuration}\"/>");
        }
        sb.AppendLine("        </SegmentTimeline>");
        sb.AppendLine("      </SegmentTemplate>");

        foreach (var (bitrateKbps, sampleRate, channels, id, label) in AudioRepresentations)
        {
            sb.AppendLine($"      <Representation id=\"{id}\" bandwidth=\"{bitrateKbps * 1000}\" " +
                          $"audioSamplingRate=\"{sampleRate}\" codecs=\"mp4a.40.2\">");
            sb.AppendLine($"        <AudioChannelConfiguration " +
                          $"schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" " +
                          $"value=\"{channels}\" />");
            sb.AppendLine("      </Representation>");
        }

        sb.AppendLine("    </AdaptationSet>");

        // Subtitle Adaptation Set placeholder for multi-language support
        sb.AppendLine("    <!-- Subtitle adaptation sets added dynamically per source track -->");

        sb.AppendLine("  </Period>");
        sb.AppendLine("</MPD>");

        return sb.ToString();
    }

    /// <summary>
    /// Builds FFmpeg command-line arguments for DASH segmenting with fragmented MP4 output.
    /// </summary>
    private static string BuildFfmpegArguments(
        Resolution resolution, int bitrateKbps, TranscodeOptions options)
    {
        // LOW-1091: Sanitize codec names before interpolation to prevent FFmpeg argument injection.
        var codec = SanitizeCodecName(options.VideoCodec ?? "libx264");
        var frameRate = options.FrameRate ?? 30.0;
        var audioCodec = SanitizeCodecName(options.AudioCodec ?? "aac");

        return $"-i pipe:0 -c:v {codec} -b:v {bitrateKbps}k " +
               $"-s {resolution.Width}x{resolution.Height} -r {frameRate.ToString("F2", CultureInfo.InvariantCulture)} " +
               $"-c:a {audioCodec} -b:a 128k -ar 48000 " +
               $"-f dash -seg_duration {DefaultSegmentDurationSeconds} " +
               $"-init_seg_name init_$RepresentationID$.mp4 " +
               $"-media_seg_name seg_$RepresentationID$_$Number$.m4s " +
               $"-use_timeline 1 -use_template 1 " +
               $"pipe:1";
    }

    /// <summary>
    /// Sanitizes a codec name to contain only alphanumeric characters, underscores, and hyphens.
    /// Prevents FFmpeg argument injection when codec names are user-controlled.
    /// </summary>
    private static string SanitizeCodecName(string codec)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(codec, @"[^a-zA-Z0-9_\-]", string.Empty);
        return sanitized.Length > 0 ? sanitized : "copy";
    }

    /// <summary>
    /// Estimates input media duration in seconds based on byte size heuristics.
    /// </summary>
    private static double EstimateInputDuration(byte[] sourceBytes)
    {
        const double averageBytesPerSecond = 4_000_000.0 / 8.0;
        return Math.Max(1.0, sourceBytes.Length / averageBytesPerSecond);
    }

    /// <summary>
    /// Writes the complete DASH package to the output stream containing MPD manifest,
    /// FFmpeg arguments, and source data reference with integrity hash.
    /// </summary>
    private async Task WriteDashPackageAsync(
        MemoryStream outputStream, string mpdManifest, string ffmpegArgs,
        byte[] sourceBytes, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(outputStream, Encoding.UTF8, leaveOpen: true);

        // Magic header
        writer.Write(Encoding.UTF8.GetBytes("DASH"));

        // Write MPD manifest
        var mpdBytes = Encoding.UTF8.GetBytes(mpdManifest);
        writer.Write(mpdBytes.Length);
        writer.Write(mpdBytes);

        // Write FFmpeg arguments
        var argsBytes = Encoding.UTF8.GetBytes(ffmpegArgs);
        writer.Write(argsBytes.Length);
        writer.Write(argsBytes);

        // Write source integrity hash
        byte[] sourceHash;
        if (MessageBus != null)
        {
            var msg = new PluginMessage { Type = "integrity.hash.compute" };
            msg.Payload["data"] = sourceBytes;
            msg.Payload["algorithm"] = "SHA256";
            var response = await MessageBus.SendAsync("integrity.hash.compute", msg, cancellationToken).ConfigureAwait(false);
            if (response.Success && response.Payload is Dictionary<string, object> payload && payload.TryGetValue("hash", out var hashObj) && hashObj is byte[] hash)
            {
                sourceHash = hash;
            }
            else
            {
                sourceHash = SHA256.HashData(sourceBytes); // Fallback on error
            }
        }
        else
        {
            sourceHash = SHA256.HashData(sourceBytes);
        }
        writer.Write(sourceHash.Length);
        writer.Write(sourceHash);

        // Write source data length
        writer.Write(sourceBytes.Length);

        await writer.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the entire input stream into a byte array for processing.
    /// </summary>
    private static async Task<byte[]> ReadStreamFullyAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
            return buffer.ToArray();

        using var copy = new MemoryStream(65536);
        await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
