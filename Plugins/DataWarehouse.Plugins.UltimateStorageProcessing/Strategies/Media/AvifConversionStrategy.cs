using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// AVIF conversion strategy that invokes "avifenc" for AVIF encoding.
/// Supports --min/--max quality, --speed (0-10), --depth 8/10/12, and --yuv 420/422/444 chroma subsampling.
/// </summary>
internal sealed class AvifConversionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-avif";

    /// <inheritdoc/>
    public override string Name => "AVIF Conversion Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 4
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        // Use nullable int? to distinguish "not set" from "explicitly 0" (finding 4295).
        var minQuality = CliProcessHelper.GetOption<int?>(query, "minQuality") ?? 18;
        var maxQuality = CliProcessHelper.GetOption<int?>(query, "maxQuality") ?? 28;
        var rawSpeed = CliProcessHelper.GetOption<int?>(query, "speed");
        var speed = rawSpeed.HasValue ? Math.Clamp(rawSpeed.Value, 0, 10) : 6;
        var depth = CliProcessHelper.GetOption<int?>(query, "depth") ?? 8;
        var yuv = CliProcessHelper.GetOption<string>(query, "yuv");

        var outputPath = Path.ChangeExtension(query.Source, ".avif");
        var args = $"\"{query.Source}\" \"{outputPath}\" --min {minQuality} --max {maxQuality} --speed {speed} --depth {depth}";
        if (yuv != null) args += $" --yuv {yuv}";

        var result = await CliProcessHelper.RunAsync("avifenc", args, Path.GetDirectoryName(query.Source), ct: ct);

        var inputSize = File.Exists(query.Source) ? new FileInfo(query.Source).Length : 0L;
        var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;

        return CliProcessHelper.ToProcessingResult(result, query.Source, "avifenc", new Dictionary<string, object?>
        {
            ["minQuality"] = minQuality, ["maxQuality"] = maxQuality, ["speed"] = speed,
            ["depth"] = depth, ["yuv"] = yuv, ["outputPath"] = outputPath,
            ["outputExists"] = File.Exists(outputPath), ["inputSize"] = inputSize, ["outputSize"] = outputSize,
            ["compressionRatio"] = inputSize > 0 ? Math.Round((double)outputSize / inputSize, 4) : 1.0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".avif", ".png", ".jpg", ".jpeg", ".y4m" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".avif", ".png", ".jpg" }, ct);
    }
}
