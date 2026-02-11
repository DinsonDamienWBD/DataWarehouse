using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// Gradle build strategy that invokes "gradle build" or "./gradlew" via Process.
/// Parses output for BUILD SUCCESSFUL/FAILED markers, supports --no-daemon, --parallel,
/// and custom task specification.
/// </summary>
internal sealed class GradleBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-gradle";

    /// <inheritdoc/>
    public override string Name => "Gradle Build Strategy";

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
        var task = CliProcessHelper.GetOption<string>(query, "task") ?? "build";
        var noDaemon = CliProcessHelper.GetOption<bool>(query, "noDaemon");
        var parallel = CliProcessHelper.GetOption<bool>(query, "parallel");

        var workDir = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source);

        // Detect wrapper
        var gradlew = Path.Combine(workDir!, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        var executable = File.Exists(gradlew) ? gradlew : "gradle";

        var args = task;
        if (noDaemon) args += " --no-daemon";
        if (parallel) args += " --parallel";

        var result = await CliProcessHelper.RunAsync(executable, args, workDir, ct: ct);

        var buildSuccessful = result.StandardOutput.Contains("BUILD SUCCESSFUL");
        var buildFailed = result.StandardOutput.Contains("BUILD FAILED");

        return CliProcessHelper.ToProcessingResult(result, query.Source, "gradle", new Dictionary<string, object?>
        {
            ["task"] = task, ["noDaemon"] = noDaemon, ["parallel"] = parallel,
            ["buildSuccessful"] = buildSuccessful, ["buildFailed"] = buildFailed,
            ["usedWrapper"] = executable != "gradle"
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".gradle", ".gradle.kts", ".java", ".kt", ".groovy" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".java", ".kt", ".gradle" }, ct);
    }
}
