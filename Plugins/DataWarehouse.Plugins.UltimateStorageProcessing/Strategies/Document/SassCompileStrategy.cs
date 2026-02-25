using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Document;

/// <summary>
/// Sass/SCSS compilation strategy that invokes "sass" CLI or "npx sass" via Process.
/// Compiles .scss/.sass files to .css with source maps, supports --style compressed/expanded
/// and import path resolution.
/// </summary>
internal sealed class SassCompileStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "document-sass";

    /// <inheritdoc/>
    public override string Name => "Sass Compile Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 3
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var style = CliProcessHelper.GetOption<string>(query, "style") ?? "expanded";
        var sourceMap = CliProcessHelper.GetOption<bool>(query, "sourceMap");
        var loadPaths = CliProcessHelper.GetOption<string>(query, "loadPaths");

        var outputPath = Path.ChangeExtension(query.Source, ".css");
        var args = $"\"{query.Source}\" \"{outputPath}\" --style {style}";
        if (!sourceMap) args += " --no-source-map";
        if (loadPaths != null)
        {
            foreach (var lp in loadPaths.Split(';', StringSplitOptions.RemoveEmptyEntries))
                args += $" --load-path \"{lp}\"";
        }

        var result = await CliProcessHelper.RunAsync("sass", args, Path.GetDirectoryName(query.Source), ct: ct);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "sass", new Dictionary<string, object?>
        {
            ["style"] = style, ["sourceMap"] = sourceMap, ["outputPath"] = outputPath,
            ["outputExists"] = File.Exists(outputPath),
            ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".scss", ".sass", ".css" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".scss", ".sass" }, ct);
    }
}
