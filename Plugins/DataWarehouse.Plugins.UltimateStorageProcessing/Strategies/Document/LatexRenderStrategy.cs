using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Document;

/// <summary>
/// LaTeX render strategy that invokes "pdflatex" or "lualatex" via Process.
/// Handles auxiliary files (.aux, .log, .toc), performs multiple passes for cross-references,
/// and parses the error log for compilation issues.
/// </summary>
internal sealed class LatexRenderStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "document-latex";

    /// <inheritdoc/>
    public override string Name => "LaTeX Render Strategy";

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
        var engine = CliProcessHelper.GetOption<string>(query, "engine") ?? "pdflatex";
        var passes = Math.Clamp(CliProcessHelper.GetOption<int>(query, "passes"), 0, 5);
        if (passes == 0) passes = 2; // default: two passes for references

        var sw = Stopwatch.StartNew();
        var workDir = Path.GetDirectoryName(query.Source);
        var fileName = Path.GetFileName(query.Source);

        CliOutput? lastResult = null;
        for (var i = 0; i < passes; i++)
        {
            ct.ThrowIfCancellationRequested();
            var args = $"-interaction=nonstopmode -halt-on-error \"{fileName}\"";
            lastResult = await CliProcessHelper.RunAsync(engine, args, workDir, ct: ct);
        }

        var logFile = Path.ChangeExtension(query.Source, ".log");
        var errorCount = 0;
        var warningCount = 0;
        if (File.Exists(logFile))
        {
            var logContent = await File.ReadAllTextAsync(logFile, ct);
            errorCount = System.Text.RegularExpressions.Regex.Matches(logContent, @"^!", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
            warningCount = System.Text.RegularExpressions.Regex.Matches(logContent, @"LaTeX Warning:", System.Text.RegularExpressions.RegexOptions.None).Count;
        }

        var pdfPath = Path.ChangeExtension(query.Source, ".pdf");
        var pdfExists = File.Exists(pdfPath);

        return CliProcessHelper.ToProcessingResult(lastResult!, query.Source, engine, new Dictionary<string, object?>
        {
            ["engine"] = engine, ["passes"] = passes, ["pdfPath"] = pdfPath,
            ["pdfExists"] = pdfExists, ["pdfSize"] = pdfExists ? new FileInfo(pdfPath).Length : 0,
            ["errorCount"] = errorCount, ["warningCount"] = warningCount
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".tex", ".sty", ".cls", ".bib" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".tex" }, ct);
    }
}
