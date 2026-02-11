using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// TypeScript build strategy that invokes "npx tsc" or "tsc" via Process.
/// Parses TypeScript compiler output for diagnostics, supports --strict, --outDir,
/// and custom tsconfig.json location via query options.
/// </summary>
internal sealed class TypeScriptBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-typescript";

    /// <inheritdoc/>
    public override string Name => "TypeScript Build Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 4
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var strict = CliProcessHelper.GetOption<bool>(query, "strict");
        var outDir = CliProcessHelper.GetOption<string>(query, "outDir");
        var tsconfig = CliProcessHelper.GetOption<string>(query, "tsconfig");

        var args = tsconfig != null ? $"--project \"{tsconfig}\"" : $"--project \"{query.Source}\"";
        if (strict) args += " --strict";
        if (outDir != null) args += $" --outDir \"{outDir}\"";

        var result = await CliProcessHelper.RunAsync("npx", $"tsc {args}", Path.GetDirectoryName(query.Source), ct: ct);

        var diagnostics = Regex.Matches(result.StandardOutput, @"error TS\d+:");
        return CliProcessHelper.ToProcessingResult(result, query.Source, "tsc", new Dictionary<string, object?>
        {
            ["strict"] = strict, ["diagnosticCount"] = diagnostics.Count
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".ts", ".tsx", ".json" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".ts", ".tsx" }, ct);
    }
}
