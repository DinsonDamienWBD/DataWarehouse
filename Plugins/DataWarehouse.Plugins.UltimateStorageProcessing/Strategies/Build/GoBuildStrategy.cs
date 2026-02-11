using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// Go build strategy that invokes "go build" via Process.
/// Supports -o output path, -race flag for race detection, -trimpath for reproducible builds,
/// and module-aware build mode.
/// </summary>
internal sealed class GoBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-go";

    /// <inheritdoc/>
    public override string Name => "Go Build Strategy";

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
        var outputPath = CliProcessHelper.GetOption<string>(query, "output");
        var race = CliProcessHelper.GetOption<bool>(query, "race");
        var trimpath = CliProcessHelper.GetOption<bool>(query, "trimpath");

        var args = "build";
        if (outputPath != null) args += $" -o \"{outputPath}\"";
        if (race) args += " -race";
        if (trimpath) args += " -trimpath";
        args += " ./...";

        var workDir = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source);
        var result = await CliProcessHelper.RunAsync("go", args, workDir, ct: ct);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "go build", new Dictionary<string, object?>
        {
            ["race"] = race, ["trimpath"] = trimpath, ["outputPath"] = outputPath
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".go", ".mod", ".sum" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".go" }, ct);
    }
}
