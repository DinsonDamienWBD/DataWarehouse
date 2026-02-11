using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// Maven build strategy that invokes "mvn package" via Process.
/// Parses [INFO]/[ERROR] output lines, supports -DskipTests, -pl for module selection,
/// and Maven profile activation.
/// </summary>
internal sealed class MavenBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-maven";

    /// <inheritdoc/>
    public override string Name => "Maven Build Strategy";

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
        var goal = CliProcessHelper.GetOption<string>(query, "goal") ?? "package";
        var skipTests = CliProcessHelper.GetOption<bool>(query, "skipTests");
        var modules = CliProcessHelper.GetOption<string>(query, "modules");
        var profiles = CliProcessHelper.GetOption<string>(query, "profiles");

        var args = goal;
        if (skipTests) args += " -DskipTests";
        if (modules != null) args += $" -pl {modules}";
        if (profiles != null) args += $" -P{profiles}";

        var workDir = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source);
        var result = await CliProcessHelper.RunAsync("mvn", args, workDir, ct: ct);

        var infoCount = Regex.Matches(result.StandardOutput, @"^\[INFO\]", RegexOptions.Multiline).Count;
        var errorCount = Regex.Matches(result.StandardOutput, @"^\[ERROR\]", RegexOptions.Multiline).Count;
        var buildSuccess = result.StandardOutput.Contains("BUILD SUCCESS");

        return CliProcessHelper.ToProcessingResult(result, query.Source, "mvn", new Dictionary<string, object?>
        {
            ["goal"] = goal, ["skipTests"] = skipTests, ["modules"] = modules,
            ["infoLines"] = infoCount, ["errorLines"] = errorCount, ["buildSuccess"] = buildSuccess
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".java", ".xml", ".pom" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".java", ".xml" }, ct);
    }
}
