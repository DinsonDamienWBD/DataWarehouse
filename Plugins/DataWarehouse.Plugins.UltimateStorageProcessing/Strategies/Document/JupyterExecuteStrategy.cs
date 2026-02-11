using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Document;

/// <summary>
/// Jupyter notebook execution strategy that invokes "jupyter nbconvert --execute" via Process.
/// Supports --to html/pdf/notebook output formats, kernel specification, and per-cell timeout.
/// </summary>
internal sealed class JupyterExecuteStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "document-jupyter";

    /// <inheritdoc/>
    public override string Name => "Jupyter Execute Strategy";

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
        var format = CliProcessHelper.GetOption<string>(query, "format") ?? "html";
        var kernel = CliProcessHelper.GetOption<string>(query, "kernel");
        var cellTimeout = CliProcessHelper.GetOption<int>(query, "cellTimeout");

        var args = $"nbconvert --execute --to {format} \"{query.Source}\"";
        if (kernel != null) args += $" --ExecutePreprocessor.kernel_name={kernel}";
        if (cellTimeout > 0) args += $" --ExecutePreprocessor.timeout={cellTimeout}";

        var result = await CliProcessHelper.RunAsync("jupyter", args, Path.GetDirectoryName(query.Source), timeoutMs: 600_000, ct: ct);

        var outputExt = format switch { "html" => ".html", "pdf" => ".pdf", "notebook" => ".nbconvert.ipynb", _ => $".{format}" };
        var outputPath = Path.ChangeExtension(query.Source, outputExt);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "jupyter nbconvert", new Dictionary<string, object?>
        {
            ["format"] = format, ["kernel"] = kernel, ["cellTimeout"] = cellTimeout,
            ["outputPath"] = outputPath, ["outputExists"] = File.Exists(outputPath)
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".ipynb" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".ipynb" }, ct);
    }
}
