using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// DASH (Dynamic Adaptive Streaming over HTTP) packaging strategy using ffmpeg or "mp4box".
/// Supports segment duration, fragmentation, manifest generation, and multi-period support.
/// </summary>
internal sealed class DashPackagingStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-dash";

    /// <inheritdoc/>
    public override string Name => "DASH Packaging Strategy";

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
        if (segmentDuration <= 0) segmentDuration = 4;
        var useMp4Box = CliProcessHelper.GetOption<bool>(query, "useMp4Box");

        var outputDir = Path.Combine(Path.GetDirectoryName(query.Source)!, "dash_output");
        Directory.CreateDirectory(outputDir);

        CliOutput result;
        string manifestPath;

        if (useMp4Box)
        {
            manifestPath = Path.Combine(outputDir, "manifest.mpd");
            var args = $"-dash {segmentDuration * 1000} -frag {segmentDuration * 1000} " +
                       $"-rap -segment-name segment_ -out \"{manifestPath}\" \"{query.Source}\"";
            result = await CliProcessHelper.RunAsync("mp4box", args, Path.GetDirectoryName(query.Source), timeoutMs: 600_000, ct: ct);
        }
        else
        {
            manifestPath = Path.Combine(outputDir, "manifest.mpd");
            var args = $"-i \"{query.Source}\" " +
                       $"-c:v libx264 -c:a aac " +
                       $"-f dash -seg_duration {segmentDuration} " +
                       $"-init_seg_name init_$RepresentationID$.m4s " +
                       $"-media_seg_name chunk_$RepresentationID$_$Number%05d$.m4s " +
                       $"-use_template 1 -use_timeline 1 " +
                       $"-y \"{manifestPath}\"";
            result = await CliProcessHelper.RunAsync("ffmpeg", args, Path.GetDirectoryName(query.Source), timeoutMs: 600_000, ct: ct);
        }

        var segmentCount = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*.m4s").Length : 0;

        return CliProcessHelper.ToProcessingResult(result, query.Source, useMp4Box ? "mp4box" : "ffmpeg dash", new Dictionary<string, object?>
        {
            ["segmentDuration"] = segmentDuration, ["useMp4Box"] = useMp4Box,
            ["manifestPath"] = manifestPath, ["outputDir"] = outputDir,
            ["segmentCount"] = segmentCount, ["manifestExists"] = File.Exists(manifestPath)
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".mp4", ".mpd", ".m4s", ".webm" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".mp4", ".m4s" }, ct);
    }
}
