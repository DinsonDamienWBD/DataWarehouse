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
        var bitrate = CliProcessHelper.GetOption<string>(query, "bitrate");
        var resolution = CliProcessHelper.GetOption<string>(query, "resolution");
        var preset = CliProcessHelper.GetOption<string>(query, "preset") ?? "medium";
        var outputFormat = CliProcessHelper.GetOption<string>(query, "outputFormat") ?? "mp4";

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
