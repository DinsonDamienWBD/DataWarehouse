using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// .NET build strategy that invokes "dotnet build" via Process.
/// Parses MSBuild output for errors, warnings, and build timing.
/// Supports --configuration, --output, and --verbosity options.
/// </summary>
internal sealed class DotNetBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-dotnet";

    /// <inheritdoc/>
    public override string Name => ".NET Build Strategy";

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
        var configuration = CliProcessHelper.GetOption<string>(query, "configuration") ?? "Release";
        var verbosity = CliProcessHelper.GetOption<string>(query, "verbosity") ?? "minimal";
        var outputDir = CliProcessHelper.GetOption<string>(query, "output");

        var args = $"build \"{query.Source}\" --configuration {configuration} --verbosity {verbosity}";
        if (outputDir != null) args += $" --output \"{outputDir}\"";

        var result = await CliProcessHelper.RunAsync("dotnet", args, Path.GetDirectoryName(query.Source), ct: ct);

        var errors = Regex.Matches(result.StandardOutput + result.StandardError, @": error \w+:");
        var warnings = Regex.Matches(result.StandardOutput + result.StandardError, @": warning \w+:");

        return CliProcessHelper.ToProcessingResult(result, query.Source, "dotnet build", new Dictionary<string, object?>
        {
            ["configuration"] = configuration, ["verbosity"] = verbosity,
            ["errorCount"] = errors.Count, ["warningCount"] = warnings.Count
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".cs", ".csproj" }, ct);
    }
}
