using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// npm build strategy that invokes "npm run build" via Process.
/// Parses exit code and stdout/stderr output, supports --prefix for working directory
/// and custom script names.
/// </summary>
internal sealed class NpmBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-npm";

    /// <inheritdoc/>
    public override string Name => "npm Build Strategy";

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
        var script = CliProcessHelper.GetOption<string>(query, "script") ?? "build";
        var prefix = CliProcessHelper.GetOption<string>(query, "prefix");

        var args = $"run {script}";
        if (prefix != null) args += $" --prefix \"{prefix}\"";

        var workDir = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source);
        var result = await CliProcessHelper.RunAsync("npm", args, workDir, ct: ct);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "npm", new Dictionary<string, object?>
        {
            ["script"] = script, ["prefix"] = prefix
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".js", ".jsx", ".ts", ".tsx", ".json", ".mjs" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".js", ".jsx", ".ts", ".tsx" }, ct);
    }
}
