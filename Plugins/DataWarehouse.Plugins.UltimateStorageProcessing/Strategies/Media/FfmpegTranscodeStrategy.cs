using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// FFmpeg transcode strategy that invokes "ffmpeg" via Process for video/audio transcoding.
/// Supports -c:v/-c:a codec selection, -b:v bitrate, -s resolution, -preset speed control,
/// and progress parsing via stderr output.
/// </summary>
internal sealed class FfmpegTranscodeStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-ffmpeg";

    /// <inheritdoc/>
    public override string Name => "FFmpeg Transcode Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 6
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var videoCodec = CliProcessHelper.GetOption<string>(query, "videoCodec") ?? "libx264";
        var audioCodec = CliProcessHelper.GetOption<string>(query, "audioCodec") ?? "aac";
        // Finding 4296: validate non-empty before allowlist check to produce a clear error message.
        if (string.IsNullOrWhiteSpace(videoCodec)) videoCodec = "libx264";
        if (string.IsNullOrWhiteSpace(audioCodec)) audioCodec = "aac";
        var bitrate = CliProcessHelper.GetOption<string>(query, "bitrate");
        var resolution = CliProcessHelper.GetOption<string>(query, "resolution");
        var preset = CliProcessHelper.GetOption<string>(query, "preset") ?? "medium";
        var outputFormat = CliProcessHelper.GetOption<string>(query, "outputFormat") ?? "mp4";

        // Allowlist video codecs, audio codecs, presets to prevent injection
        var allowedVideoCodecs = new HashSet<string>(StringComparer.Ordinal) { "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "vp8", "vp9", "av1", "libvpx", "libvpx-vp9", "copy" };
        var allowedAudioCodecs = new HashSet<string>(StringComparer.Ordinal) { "aac", "mp3", "libmp3lame", "opus", "libopus", "flac", "pcm_s16le", "copy" };
        var allowedPresets = new HashSet<string>(StringComparer.Ordinal) { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
        var allowedFormats = new HashSet<string>(StringComparer.Ordinal) { "mp4", "mkv", "avi", "mov", "webm", "flv", "ts", "m4v" };

        CliProcessHelper.ValidateAllowlist(videoCodec, "videoCodec", allowedVideoCodecs);
        CliProcessHelper.ValidateAllowlist(audioCodec, "audioCodec", allowedAudioCodecs);
        CliProcessHelper.ValidateAllowlist(preset, "preset", allowedPresets);
        CliProcessHelper.ValidateAllowlist(outputFormat, "outputFormat", allowedFormats);

        // bitrate and resolution validated as identifiers (e.g. "5000k", "1280x720")
        if (bitrate != null) CliProcessHelper.ValidateIdentifier(bitrate, "bitrate");
        if (resolution != null) CliProcessHelper.ValidateNoShellMetachars(resolution, "resolution");

        var outputPath = Path.ChangeExtension(query.Source, $".transcoded.{outputFormat}");
        var args = $"-i \"{query.Source}\" -c:v {videoCodec} -c:a {audioCodec} -preset {preset}";
        if (bitrate != null) args += $" -b:v {bitrate}";
        if (resolution != null) args += $" -s {resolution}";
        args += $" -y \"{outputPath}\"";

        var result = await CliProcessHelper.RunAsync("ffmpeg", args, Path.GetDirectoryName(query.Source), timeoutMs: 600_000, ct: ct);

        // Parse duration and speed from ffmpeg stderr
        var durationMatch = Regex.Match(result.StandardError, @"Duration: (\d+:\d+:\d+\.\d+)");
        var speedMatch = Regex.Match(result.StandardError, @"speed=\s*([\d.]+)x");

        return CliProcessHelper.ToProcessingResult(result, query.Source, "ffmpeg", new Dictionary<string, object?>
        {
            ["videoCodec"] = videoCodec, ["audioCodec"] = audioCodec, ["preset"] = preset,
            ["bitrate"] = bitrate, ["resolution"] = resolution, ["outputPath"] = outputPath,
            ["outputExists"] = File.Exists(outputPath),
            ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0,
            ["duration"] = durationMatch.Success ? durationMatch.Groups[1].Value : null,
            ["speed"] = speedMatch.Success ? speedMatch.Groups[1].Value : null
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".mp3", ".aac", ".flac", ".ogg", ".wav" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm" }, ct);
    }
}
