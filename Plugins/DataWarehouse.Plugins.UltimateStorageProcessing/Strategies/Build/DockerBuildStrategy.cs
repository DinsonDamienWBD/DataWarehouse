using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Build;

/// <summary>
/// Docker build strategy that invokes "docker build" or "docker buildx build" via Process.
/// The Source field identifies the Dockerfile path or build context directory.
/// Supports --tag, --platform, --no-cache, and --build-arg options.
/// </summary>
internal sealed class DockerBuildStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "build-docker";

    /// <inheritdoc/>
    public override string Name => "Docker Build Strategy";

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
        var tag = CliProcessHelper.GetOption<string>(query, "tag") ?? "latest";
        var platform = CliProcessHelper.GetOption<string>(query, "platform");
        var noCache = CliProcessHelper.GetOption<bool>(query, "noCache");
        var useBuildx = CliProcessHelper.GetOption<bool>(query, "buildx");

        // Validate user-supplied values before interpolation into CLI args
        CliProcessHelper.ValidateNoShellMetachars(tag, "tag");
        if (platform != null) CliProcessHelper.ValidateNoShellMetachars(platform, "platform");

        var context = Directory.Exists(query.Source) ? query.Source : Path.GetDirectoryName(query.Source) ?? ".";
        var dockerfile = File.Exists(query.Source) ? query.Source : Path.Combine(context, "Dockerfile");

        var command = useBuildx ? "buildx build" : "build";
        var args = $"{command} -f \"{dockerfile}\" -t \"{tag}\"";
        if (platform != null) args += $" --platform \"{platform}\"";
        if (noCache) args += " --no-cache";
        args += $" \"{context}\"";

        var result = await CliProcessHelper.RunAsync("docker", args, context, timeoutMs: 600_000, ct: ct);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "docker build", new Dictionary<string, object?>
        {
            ["tag"] = tag, ["platform"] = platform, ["noCache"] = noCache, ["buildx"] = useBuildx
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".dockerfile", "" }, sw, ct))
        {
            // Also include Dockerfiles (no extension)
            if (r.Data["fileName"]?.ToString()?.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) == true ||
                r.Data["extension"]?.ToString() is ".dockerfile" or ".yaml" or ".yml")
                yield return r;
        }
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, Array.Empty<string>(), ct);
    }
}
