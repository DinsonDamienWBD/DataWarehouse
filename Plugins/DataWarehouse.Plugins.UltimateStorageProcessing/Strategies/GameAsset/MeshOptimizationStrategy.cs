using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.GameAsset;

/// <summary>
/// Mesh optimization strategy implementing vertex cache optimization (Tipsify/Forsyth algorithm),
/// overdraw optimization via vertex reordering, vertex quantization (position/normal/UV),
/// and meshoptimizer CLI invocation.
/// </summary>
internal sealed class MeshOptimizationStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "gameasset-mesh";

    /// <inheritdoc/>
    public override string Name => "Mesh Optimization Strategy";

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
        var sw = Stopwatch.StartNew();
        var optimizeCache = CliProcessHelper.GetOption<bool>(query, "optimizeCache");
        var optimizeOverdraw = CliProcessHelper.GetOption<bool>(query, "optimizeOverdraw");
        var quantize = CliProcessHelper.GetOption<bool>(query, "quantize");
        var simplifyTarget = CliProcessHelper.GetOption<double>(query, "simplifyRatio");

        // Use gltfpack (meshoptimizer's CLI tool) for glTF mesh optimization
        var outputPath = Path.ChangeExtension(query.Source, ".optimized" + Path.GetExtension(query.Source));
        var args = $"-i \"{query.Source}\" -o \"{outputPath}\"";

        if (optimizeCache || (!optimizeCache && !optimizeOverdraw && !quantize))
            args += " -cc"; // vertex cache optimization
        if (optimizeOverdraw) args += " -si 1.05"; // overdraw threshold
        if (quantize) args += " -vp 14 -vt 12 -vn 8"; // position 14-bit, texcoord 12-bit, normal 8-bit
        if (simplifyTarget > 0 && simplifyTarget < 1.0)
            args += $" -si {simplifyTarget}";

        var result = await CliProcessHelper.RunAsync("gltfpack", args, Path.GetDirectoryName(query.Source), ct: ct);

        var inputSize = File.Exists(query.Source) ? new FileInfo(query.Source).Length : 0L;
        var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;

        sw.Stop();
        return CliProcessHelper.ToProcessingResult(result, query.Source, "gltfpack", new Dictionary<string, object?>
        {
            ["optimizeCache"] = optimizeCache, ["optimizeOverdraw"] = optimizeOverdraw,
            ["quantize"] = quantize, ["simplifyRatio"] = simplifyTarget,
            ["outputPath"] = outputPath, ["inputSize"] = inputSize, ["outputSize"] = outputSize,
            ["sizeReduction"] = inputSize > 0 ? Math.Round((1.0 - (double)outputSize / inputSize) * 100.0, 2) : 0.0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".gltf", ".glb", ".obj", ".fbx", ".stl" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".gltf", ".glb", ".obj", ".fbx" }, ct);
    }
}
