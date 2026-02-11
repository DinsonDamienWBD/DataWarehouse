using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// Rust build strategy that invokes "cargo build" via Process.
/// Parses cargo output including compiler warnings and errors,
/// supports --release, --target, and --features flags.
/// </summary>
internal sealed class RustBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-rust";

    /// <inheritdoc/>
    public override string Name => "Rust Build Strategy";

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
        var release = CliProcessHelper.GetOption<bool>(query, "release");
        var target = CliProcessHelper.GetOption<string>(query, "target");
        var features = CliProcessHelper.GetOption<string>(query, "features");

        var args = "build";
        if (release) args += " --release";
        if (target != null) args += $" --target {target}";
        if (features != null) args += $" --features {features}";

        var workDir = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source);
        var result = await CliProcessHelper.RunAsync("cargo", args, workDir, ct: ct);

        var errors = Regex.Matches(result.StandardError, @"^error", RegexOptions.Multiline);
        var warnings = Regex.Matches(result.StandardError, @"^warning", RegexOptions.Multiline);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "cargo build", new Dictionary<string, object?>
        {
            ["release"] = release, ["target"] = target,
            ["errorCount"] = errors.Count, ["warningCount"] = warnings.Count
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".rs", ".toml" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".rs" }, ct);
    }
}
