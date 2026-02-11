using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// WebP conversion strategy that invokes "cwebp" for encoding or "dwebp" for decoding.
/// Supports -q quality (0-100), -lossless mode, -mt multi-threading, and -resize options.
/// </summary>
internal sealed class WebPConversionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-webp";

    /// <inheritdoc/>
    public override string Name => "WebP Conversion Strategy";

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
        var mode = CliProcessHelper.GetOption<string>(query, "mode") ?? "encode";
        var quality = Math.Clamp(CliProcessHelper.GetOption<int>(query, "quality"), 0, 100);
        if (quality == 0) quality = 80;
        var lossless = CliProcessHelper.GetOption<bool>(query, "lossless");
        var mt = CliProcessHelper.GetOption<bool>(query, "mt");
        var resize = CliProcessHelper.GetOption<string>(query, "resize");

        string args;
        string outputPath;

        if (mode == "decode")
        {
            outputPath = Path.ChangeExtension(query.Source, ".png");
            args = $"\"{query.Source}\" -o \"{outputPath}\"";
            var result = await CliProcessHelper.RunAsync("dwebp", args, Path.GetDirectoryName(query.Source), ct: ct);
            return CliProcessHelper.ToProcessingResult(result, query.Source, "dwebp", new Dictionary<string, object?>
            {
                ["mode"] = "decode", ["outputPath"] = outputPath,
                ["outputExists"] = File.Exists(outputPath),
                ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
            });
        }

        outputPath = Path.ChangeExtension(query.Source, ".webp");
        args = $"-q {quality}";
        if (lossless) args += " -lossless";
        if (mt) args += " -mt";
        if (resize != null) args += $" -resize {resize}";
        args += $" \"{query.Source}\" -o \"{outputPath}\"";

        var encResult = await CliProcessHelper.RunAsync("cwebp", args, Path.GetDirectoryName(query.Source), ct: ct);

        var inputSize = File.Exists(query.Source) ? new FileInfo(query.Source).Length : 0L;
        var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;

        return CliProcessHelper.ToProcessingResult(encResult, query.Source, "cwebp", new Dictionary<string, object?>
        {
            ["mode"] = "encode", ["quality"] = quality, ["lossless"] = lossless, ["mt"] = mt,
            ["outputPath"] = outputPath, ["outputExists"] = File.Exists(outputPath),
            ["inputSize"] = inputSize, ["outputSize"] = outputSize,
            ["compressionRatio"] = inputSize > 0 ? Math.Round((double)outputSize / inputSize, 4) : 1.0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".webp", ".png", ".jpg", ".jpeg" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".webp", ".png", ".jpg" }, ct);
    }
}
