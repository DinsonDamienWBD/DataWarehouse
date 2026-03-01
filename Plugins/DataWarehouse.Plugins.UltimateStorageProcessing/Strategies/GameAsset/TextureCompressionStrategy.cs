using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.GameAsset;

/// <summary>
/// Texture compression strategy supporting BC1-BC7 (DXT), ASTC, and ETC2 formats
/// via compressonator or texconv CLI. Includes mipmap generation, sRGB/linear color space
/// selection, and platform-specific format targeting.
/// </summary>
internal sealed class TextureCompressionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "gameasset-texture";

    /// <inheritdoc/>
    public override string Name => "Texture Compression Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 5
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var format = CliProcessHelper.GetOption<string>(query, "format") ?? "BC7";
        var generateMipmaps = CliProcessHelper.GetOption<bool>(query, "mipmaps");
        var colorSpace = CliProcessHelper.GetOption<string>(query, "colorSpace") ?? "sRGB";
        var tool = CliProcessHelper.GetOption<string>(query, "tool") ?? "compressonatorcli";

        var outputPath = Path.ChangeExtension(query.Source, ".dds");

        string args;
        if (tool == "texconv")
        {
            args = $"-f {format} -y";
            if (generateMipmaps) args += " -m 0"; // auto mip count
            // Finding 4297: -srgb flag should be applied when colorSpace is "sRGB" (marks texture as
            // sRGB-encoded, not linear). The previous logic was inverted — applying sRGB flag for linear.
            if (colorSpace == "sRGB" || colorSpace == "srgb") args += " -srgb";
            args += $" -o \"{Path.GetDirectoryName(outputPath)}\" \"{query.Source}\"";
        }
        else
        {
            args = $"-fd {format}";
            if (generateMipmaps) args += " -miplevels 12";
            args += $" \"{query.Source}\" \"{outputPath}\"";
        }

        var result = await CliProcessHelper.RunAsync(tool, args, Path.GetDirectoryName(query.Source), ct: ct);

        var inputSize = File.Exists(query.Source) ? new FileInfo(query.Source).Length : 0L;
        var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;

        return CliProcessHelper.ToProcessingResult(result, query.Source, tool, new Dictionary<string, object?>
        {
            ["format"] = format, ["generateMipmaps"] = generateMipmaps, ["colorSpace"] = colorSpace,
            ["outputPath"] = outputPath, ["inputSize"] = inputSize, ["outputSize"] = outputSize,
            ["compressionRatio"] = inputSize > 0 ? Math.Round((double)outputSize / inputSize, 4) : 1.0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".png", ".jpg", ".tga", ".bmp", ".dds", ".ktx", ".astc" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".png", ".dds", ".tga" }, ct);
    }
}
