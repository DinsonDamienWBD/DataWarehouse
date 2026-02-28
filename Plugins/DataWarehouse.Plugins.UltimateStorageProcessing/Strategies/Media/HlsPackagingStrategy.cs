using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// HLS (HTTP Live Streaming) packaging strategy using ffmpeg with HLS muxer.
/// Supports -hls_time segment duration, -hls_list_size playlist length,
/// -hls_segment_type mpegts/fmp4, and master playlist generation.
/// </summary>
internal sealed class HlsPackagingStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-hls";

    /// <inheritdoc/>
    public override string Name => "HLS Packaging Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 5
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var segmentDuration = CliProcessHelper.GetOption<int>(query, "segmentDuration");
        if (segmentDuration <= 0) segmentDuration = 6;
        var listSize = CliProcessHelper.GetOption<int>(query, "listSize");
        var segmentType = CliProcessHelper.GetOption<string>(query, "segmentType") ?? "mpegts";

        // Allowlist segmentType to prevent injection into ffmpeg -hls_segment_type arg
        var allowedSegmentTypes = new HashSet<string>(StringComparer.Ordinal) { "mpegts", "fmp4" };
        CliProcessHelper.ValidateAllowlist(segmentType, "segmentType", allowedSegmentTypes);

        // Validate segment duration range
        if (segmentDuration < 1 || segmentDuration > 3600)
            throw new ArgumentException("'segmentDuration' must be between 1 and 3600 seconds.", nameof(segmentDuration));

        var outputDir = Path.Combine(Path.GetDirectoryName(query.Source)!, "hls_output");
        Directory.CreateDirectory(outputDir);

        var playlistPath = Path.Combine(outputDir, "playlist.m3u8");
        var segmentPattern = Path.Combine(outputDir, "segment_%03d." + (segmentType == "fmp4" ? "m4s" : "ts"));

        var args = $"-i \"{query.Source}\" -c:v libx264 -c:a aac " +
                   $"-hls_time {segmentDuration} " +
                   $"-hls_segment_type {segmentType} " +
                   $"-hls_segment_filename \"{segmentPattern}\" ";
        if (listSize > 0) args += $"-hls_list_size {listSize} ";
        args += $"-y \"{playlistPath}\"";

        var result = await CliProcessHelper.RunAsync("ffmpeg", args, Path.GetDirectoryName(query.Source), timeoutMs: 600_000, ct: ct);

        var segmentCount = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, segmentType == "fmp4" ? "*.m4s" : "*.ts").Length
            : 0;

        return CliProcessHelper.ToProcessingResult(result, query.Source, "ffmpeg hls", new Dictionary<string, object?>
        {
            ["segmentDuration"] = segmentDuration, ["segmentType"] = segmentType,
            ["playlistPath"] = playlistPath, ["outputDir"] = outputDir,
            ["segmentCount"] = segmentCount, ["playlistExists"] = File.Exists(playlistPath)
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".mp4", ".mkv", ".m3u8", ".ts", ".m4s" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".mp4", ".ts", ".m4s" }, ct);
    }
}
