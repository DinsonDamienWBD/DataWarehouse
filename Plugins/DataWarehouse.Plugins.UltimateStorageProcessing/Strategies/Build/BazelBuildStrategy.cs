using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// Bazel build strategy that invokes "bazel build" via Process.
/// Parses build event protocol output, supports --config, --jobs, and target label specification.
/// </summary>
internal sealed class BazelBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-bazel";

    /// <inheritdoc/>
    public override string Name => "Bazel Build Strategy";

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
        var config = CliProcessHelper.GetOption<string>(query, "config");
        var jobs = CliProcessHelper.GetOption<int>(query, "jobs");
        var target = CliProcessHelper.GetOption<string>(query, "target") ?? "//...";

        // Validate user-supplied values before interpolation into CLI args
        CliProcessHelper.ValidateIdentifier(target, "target");
        if (config != null) CliProcessHelper.ValidateIdentifier(config, "config");
        if (jobs < 0 || jobs > 1000)
            throw new ArgumentException("'jobs' must be between 0 and 1000.", nameof(jobs));

        var args = $"build {target}";
        if (config != null) args += $" --config={config}";
        if (jobs > 0) args += $" --jobs={jobs}";

        var workDir = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source);
        var result = await CliProcessHelper.RunAsync("bazel", args, workDir, ct: ct);

        var buildSuccess = result.StandardError.Contains("Build completed successfully") || result.Success;

        return CliProcessHelper.ToProcessingResult(result, query.Source, "bazel build", new Dictionary<string, object?>
        {
            ["target"] = target, ["config"] = config, ["jobs"] = jobs, ["buildSuccess"] = buildSuccess
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".bzl", ".bazel", "" }, sw, ct))
        {
            var name = r.Data["fileName"]?.ToString() ?? "";
            if (name is "BUILD" or "BUILD.bazel" or "WORKSPACE" or "WORKSPACE.bazel" || name.EndsWith(".bzl"))
                yield return r;
        }
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".bzl", ".bazel" }, ct);
    }
}
