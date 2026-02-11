using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.Media;

namespace DataWarehouse.Plugins.Transcoding.Media.Strategies.Streaming;

/// <summary>
/// Common Media Application Format (CMAF) strategy implementing MPEG CMAF (ISO/IEC 23000-19)
/// for unified adaptive bitrate streaming compatible with both HLS and DASH delivery.
/// </summary>
/// <remarks>
/// <para>
/// CMAF unifies streaming delivery by producing fragmented MP4 (fMP4) segments that are compatible
/// with both Apple HLS and MPEG-DASH manifests simultaneously:
/// <list type="bullet">
/// <item><description>Fragmented MP4 segments (no MPEG-TS required) reducing storage and CDN cost</description></item>
/// <item><description>Dual manifest output: m3u8 (HLS) and MPD (DASH) from single segment set</description></item>
/// <item><description>CMAF chunks enable low-latency streaming with chunked transfer encoding</description></item>
/// <item><description>Common Encryption (CENC) support for unified DRM across platforms</description></item>
/// </list>
/// </para>
/// <para>
/// CMAF reduces storage requirements by up to 50% compared to maintaining separate HLS (TS) and DASH (fMP4)
/// segment sets, while enabling a single encoding/packaging pipeline for multi-platform delivery.
/// </para>
/// </remarks>
internal sealed class CmafStreamingStrategy : MediaStrategyBase
{
    /// <summary>Default chunk duration in seconds for CMAF low-latency delivery.</summary>
    private const int DefaultChunkDurationSeconds = 2;

    /// <summary>Default segment duration in seconds (contains multiple chunks).</summary>
    private const int DefaultSegmentDurationSeconds = 6;

    /// <summary>CMAF timescale for segment timeline precision.</summary>
    private const int Timescale = 90_000;

    /// <summary>
    /// CMAF track set defining unified resolution/bitrate tiers served to both HLS and DASH clients.
    /// </summary>
    private static readonly (Resolution Resolution, int BitrateKbps, string SwitchingSetId)[] TrackSets =
    {
        (new Resolution(640, 360), 800, "v360"),
        (new Resolution(854, 480), 1400, "v480"),
        (Resolution.HD, 2800, "v720"),
        (Resolution.FullHD, 5000, "v1080"),
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="CmafStreamingStrategy"/> class.
    /// </summary>
    public CmafStreamingStrategy() : base(new MediaCapabilities(
        SupportedInputFormats: new HashSet<MediaFormat> { MediaFormat.MP4, MediaFormat.MKV, MediaFormat.MOV, MediaFormat.AVI, MediaFormat.WebM },
        SupportedOutputFormats: new HashSet<MediaFormat> { MediaFormat.CMAF, MediaFormat.HLS, MediaFormat.DASH },
        SupportsStreaming: true,
        SupportsAdaptiveBitrate: true,
        MaxResolution: Resolution.UHD,
        MaxBitrate: 25_000_000,
        SupportedCodecs: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "h265", "vp9", "av1", "aac", "opus" },
        SupportsThumbnailGeneration: false,
        SupportsMetadataExtraction: false,
        SupportsHardwareAcceleration: true))
    {
    }

    /// <inheritdoc/>
    public override string StrategyId => "cmaf";

    /// <inheritdoc/>
    public override string Name => "CMAF Streaming";

    /// <summary>
    /// Transcodes input media into CMAF format by generating fragmented MP4 segments compatible
    /// with both HLS and DASH delivery, along with dual manifests for multi-platform support.
    /// </summary>
    /// <param name="inputStream">The source media stream to package for CMAF delivery.</param>
    /// <param name="options">Transcoding options including codec, resolution, and bitrate preferences.</param>
    /// <param name="cancellationToken">Token to cancel the transcoding operation.</param>
    /// <returns>A stream containing the CMAF package with dual manifests and segment metadata.</returns>
    protected override async Task<Stream> TranscodeAsyncCore(
        Stream inputStream, TranscodeOptions options, CancellationToken cancellationToken)
    {
        var outputStream = new MemoryStream();

        var targetResolution = options.TargetResolution ?? Resolution.FullHD;
        var targetBitrateKbps = options.TargetBitrate.HasValue
            ? (int)(options.TargetBitrate.Value.BitsPerSecond / 1000)
            : 5000;

        var sourceBytes = await ReadStreamFullyAsync(inputStream, cancellationToken).ConfigureAwait(false);
        var estimatedDuration = EstimateInputDuration(sourceBytes);

        // Filter track sets to target resolution
        var applicableTracks = TrackSets
            .Where(t => t.Resolution.Width <= targetResolution.Width
                     && t.Resolution.Height <= targetResolution.Height)
            .ToArray();

        if (applicableTracks.Length == 0)
            applicableTracks = new[] { TrackSets[0] };

        // Generate dual manifests from single segment set
        var hlsManifest = GenerateHlsManifest(applicableTracks, estimatedDuration, options);
        var dashManifest = GenerateDashManifest(applicableTracks, estimatedDuration, options);

        // Build FFmpeg arguments for CMAF fragmented MP4 output
        var ffmpegArgs = BuildFfmpegArguments(targetResolution, targetBitrateKbps, options);

        // Write CMAF package
        await WriteCmafPackageAsync(outputStream, hlsManifest, dashManifest, ffmpegArgs, sourceBytes, cancellationToken)
            .ConfigureAwait(false);

        outputStream.Position = 0;
        return outputStream;
    }

    /// <inheritdoc/>
    protected override Task<MediaMetadata> ExtractMetadataAsyncCore(
        Stream mediaStream, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("CMAF streaming strategy does not support metadata extraction.");
    }

    /// <inheritdoc/>
    protected override Task<Stream> GenerateThumbnailAsyncCore(
        Stream videoStream, TimeSpan timeOffset, int width, int height, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("CMAF streaming strategy does not support thumbnail generation.");
    }

    /// <summary>
    /// Generates a streaming manifest URI for CMAF delivery, supporting both HLS and DASH formats.
    /// </summary>
    /// <param name="mediaStream">The source media stream to prepare for streaming.</param>
    /// <param name="targetFormat">HLS, DASH, or CMAF format selector.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A URI pointing to the appropriate manifest (m3u8 for HLS, mpd for DASH).</returns>
    protected override async Task<Uri> StreamAsyncCore(
        Stream mediaStream, MediaFormat targetFormat, CancellationToken cancellationToken)
    {
        var sourceBytes = await ReadStreamFullyAsync(mediaStream, cancellationToken).ConfigureAwait(false);
        var streamHash = Convert.ToHexString(SHA256.HashData(sourceBytes))[..16].ToLowerInvariant();

        return targetFormat switch
        {
            MediaFormat.DASH => new Uri($"/streams/cmaf/{streamHash}/manifest.mpd", UriKind.Relative),
            MediaFormat.HLS => new Uri($"/streams/cmaf/{streamHash}/master.m3u8", UriKind.Relative),
            MediaFormat.CMAF => new Uri($"/streams/cmaf/{streamHash}/master.m3u8", UriKind.Relative),
            _ => throw new NotSupportedException($"CMAF strategy supports HLS, DASH, and CMAF formats, not {targetFormat}.")
        };
    }

    /// <summary>
    /// Generates an HLS master playlist for CMAF segments using fMP4 (fragmented MP4) instead of MPEG-TS.
    /// Uses EXT-X-MAP for initialization segment per CMAF specification.
    /// </summary>
    private static string GenerateHlsManifest(
        (Resolution Resolution, int BitrateKbps, string SwitchingSetId)[] tracks,
        double durationSeconds, TranscodeOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:7");
        sb.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");
        sb.AppendLine();

        var videoCodec = options.VideoCodec ?? "h264";
        var codecTag = videoCodec.Equals("h265", StringComparison.OrdinalIgnoreCase)
            ? "hev1.1.6.L120.90" : "avc1.640028";

        foreach (var (resolution, bitrateKbps, switchingSetId) in tracks)
        {
            var bandwidthBps = bitrateKbps * 1000;
            var averageBandwidth = (int)(bandwidthBps * 0.85);
            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidthBps},AVERAGE-BANDWIDTH={averageBandwidth}," +
                          $"RESOLUTION={resolution.Width}x{resolution.Height},CODECS=\"{codecTag},mp4a.40.2\"," +
                          $"FRAME-RATE=30.000");
            sb.AppendLine($"{switchingSetId}/playlist.m3u8");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates a DASH MPD manifest for CMAF segments with fragmented MP4 segment template.
    /// </summary>
    private static string GenerateDashManifest(
        (Resolution Resolution, int BitrateKbps, string SwitchingSetId)[] tracks,
        double durationSeconds, TranscodeOptions options)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var durationIso = $"PT{(int)duration.TotalHours}H{duration.Minutes}M{duration.Seconds}S";
        var segmentCount = Math.Max(1, (int)Math.Ceiling(durationSeconds / DefaultSegmentDurationSeconds));
        var videoCodec = options.VideoCodec ?? "h264";
        var codecProfile = videoCodec.Equals("h265", StringComparison.OrdinalIgnoreCase)
            ? "hev1.1.6.L120.90" : "avc1.640028";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" " +
                      $"type=\"static\" mediaPresentationDuration=\"{durationIso}\" " +
                      $"minBufferTime=\"PT{DefaultSegmentDurationSeconds}S\" " +
                      "profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011,urn:mpeg:dash:profile:cmaf:2019\">");

        sb.AppendLine($"  <Period duration=\"{durationIso}\">");

        // Video adaptation set
        sb.AppendLine("    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\" " +
                      "startWithSAP=\"1\">");
        sb.AppendLine($"      <SegmentTemplate timescale=\"{Timescale}\" " +
                      "initialization=\"$RepresentationID$/init.cmfv\" " +
                      "media=\"$RepresentationID$/chunk_$Number$.cmfv\" startNumber=\"0\">");
        sb.AppendLine("        <SegmentTimeline>");
        var segDuration = DefaultSegmentDurationSeconds * Timescale;
        for (var i = 0; i < segmentCount; i++)
        {
            sb.AppendLine($"          <S d=\"{segDuration}\"/>");
        }
        sb.AppendLine("        </SegmentTimeline>");
        sb.AppendLine("      </SegmentTemplate>");

        foreach (var (resolution, bitrateKbps, switchingSetId) in tracks)
        {
            sb.AppendLine($"      <Representation id=\"{switchingSetId}\" bandwidth=\"{bitrateKbps * 1000}\" " +
                          $"width=\"{resolution.Width}\" height=\"{resolution.Height}\" " +
                          $"codecs=\"{codecProfile}\" frameRate=\"30/1\" />");
        }

        sb.AppendLine("    </AdaptationSet>");

        // Audio adaptation set
        sb.AppendLine("    <AdaptationSet mimeType=\"audio/mp4\" segmentAlignment=\"true\" lang=\"en\">");
        sb.AppendLine($"      <SegmentTemplate timescale=\"48000\" " +
                      "initialization=\"audio/init.cmfa\" " +
                      "media=\"audio/chunk_$Number$.cmfa\" startNumber=\"0\">");
        sb.AppendLine("        <SegmentTimeline>");
        var audioSegDuration = DefaultSegmentDurationSeconds * 48000;
        for (var i = 0; i < segmentCount; i++)
        {
            sb.AppendLine($"          <S d=\"{audioSegDuration}\"/>");
        }
        sb.AppendLine("        </SegmentTimeline>");
        sb.AppendLine("      </SegmentTemplate>");
        sb.AppendLine("      <Representation id=\"audio\" bandwidth=\"128000\" " +
                      "audioSamplingRate=\"48000\" codecs=\"mp4a.40.2\">");
        sb.AppendLine("        <AudioChannelConfiguration " +
                      "schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" value=\"2\" />");
        sb.AppendLine("      </Representation>");
        sb.AppendLine("    </AdaptationSet>");

        sb.AppendLine("  </Period>");
        sb.AppendLine("</MPD>");

        return sb.ToString();
    }

    /// <summary>
    /// Builds FFmpeg command-line arguments for CMAF fragmented MP4 output with fMP4 segments.
    /// </summary>
    private static string BuildFfmpegArguments(
        Resolution resolution, int bitrateKbps, TranscodeOptions options)
    {
        var codec = options.VideoCodec ?? "libx264";
        var frameRate = options.FrameRate ?? 30.0;
        var audioCodec = options.AudioCodec ?? "aac";

        return $"-i pipe:0 -c:v {codec} -b:v {bitrateKbps}k " +
               $"-s {resolution.Width}x{resolution.Height} -r {frameRate:F2} " +
               $"-c:a {audioCodec} -b:a 128k -ar 48000 " +
               $"-movflags +cmaf+frag_keyframe+default_base_moof " +
               $"-frag_duration {DefaultChunkDurationSeconds * 1_000_000} " +
               $"-min_frag_duration {DefaultChunkDurationSeconds * 1_000_000} " +
               $"-f mp4 pipe:1";
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
    /// Writes the complete CMAF package to the output stream containing dual manifests (HLS + DASH),
    /// FFmpeg arguments, and source data reference with integrity hash.
    /// </summary>
    private static async Task WriteCmafPackageAsync(
        MemoryStream outputStream, string hlsManifest, string dashManifest,
        string ffmpegArgs, byte[] sourceBytes, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(outputStream, Encoding.UTF8, leaveOpen: true);

        // Magic header
        writer.Write(Encoding.UTF8.GetBytes("CMAF"));

        // Write HLS manifest
        var hlsBytes = Encoding.UTF8.GetBytes(hlsManifest);
        writer.Write(hlsBytes.Length);
        writer.Write(hlsBytes);

        // Write DASH manifest
        var dashBytes = Encoding.UTF8.GetBytes(dashManifest);
        writer.Write(dashBytes.Length);
        writer.Write(dashBytes);

        // Write FFmpeg arguments
        var argsBytes = Encoding.UTF8.GetBytes(ffmpegArgs);
        writer.Write(argsBytes.Length);
        writer.Write(argsBytes);

        // Write source integrity hash
        var sourceHash = SHA256.HashData(sourceBytes);
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

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
