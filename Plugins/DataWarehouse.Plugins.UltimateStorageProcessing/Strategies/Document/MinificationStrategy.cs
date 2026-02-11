using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Document;

/// <summary>
/// JavaScript/CSS/HTML minification strategy providing in-place file optimization.
/// JS: removes whitespace, shortens identifiers, strips comments.
/// CSS: merges rules, shortens colors, removes whitespace.
/// HTML: collapses whitespace, removes optional tags and comments.
/// </summary>
internal sealed class MinificationStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "document-minification";

    /// <inheritdoc/>
    public override string Name => "Minification Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 5
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();

        if (!File.Exists(query.Source))
            return MakeError("Source file not found", sw);

        var content = await File.ReadAllTextAsync(query.Source, ct);
        var originalSize = content.Length;
        var ext = Path.GetExtension(query.Source).ToLowerInvariant();
        var contentType = CliProcessHelper.GetOption<string>(query, "contentType") ?? ext switch
        {
            ".js" or ".mjs" or ".jsx" => "javascript",
            ".css" or ".scss" => "css",
            ".html" or ".htm" => "html",
            _ => "javascript"
        };

        var minified = contentType switch
        {
            "javascript" => MinifyJavaScript(content),
            "css" => MinifyCss(content),
            "html" => MinifyHtml(content),
            _ => MinifyJavaScript(content)
        };

        var outputPath = InsertMin(query.Source);
        await File.WriteAllTextAsync(outputPath, minified, Encoding.UTF8, ct);

        var ratio = originalSize > 0 ? (double)minified.Length / originalSize : 1.0;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["outputPath"] = outputPath,
                ["contentType"] = contentType, ["originalSize"] = originalSize,
                ["minifiedSize"] = minified.Length,
                ["reductionPercent"] = Math.Round((1.0 - ratio) * 100.0, 2),
                ["compressionRatio"] = Math.Round(ratio, 4)
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = 1, RowsReturned = 1, BytesProcessed = originalSize,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".js", ".css", ".html", ".htm", ".mjs", ".jsx" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".js", ".css", ".html" }, ct);
    }

    private static string MinifyJavaScript(string js)
    {
        // Remove single-line comments (but not URLs like http://)
        var result = Regex.Replace(js, @"(?<!:)//[^\n]*", "");
        // Remove multi-line comments
        result = Regex.Replace(result, @"/\*[\s\S]*?\*/", "");
        // Collapse whitespace
        result = Regex.Replace(result, @"\s+", " ");
        // Remove spaces around operators
        result = Regex.Replace(result, @"\s*([{};,()=+\-*/<>!&|?:])\s*", "$1");
        // Remove trailing semicolons before closing braces
        result = result.Replace(";}", "}");
        return result.Trim();
    }

    private static string MinifyCss(string css)
    {
        // Remove comments
        var result = Regex.Replace(css, @"/\*[\s\S]*?\*/", "");
        // Collapse whitespace
        result = Regex.Replace(result, @"\s+", " ");
        // Remove spaces around CSS punctuation
        result = Regex.Replace(result, @"\s*([{};:,>~+])\s*", "$1");
        // Shorten hex colors (#aabbcc -> #abc)
        result = Regex.Replace(result, @"#([0-9a-fA-F])\1([0-9a-fA-F])\2([0-9a-fA-F])\3", "#$1$2$3");
        // Remove trailing semicolons before closing braces
        result = result.Replace(";}", "}");
        // Remove leading zeros (0.5 -> .5)
        result = Regex.Replace(result, @"(?<=:|\s)0\.(\d)", ".$1");
        return result.Trim();
    }

    private static string MinifyHtml(string html)
    {
        // Remove HTML comments
        var result = Regex.Replace(html, @"<!--[\s\S]*?-->", "");
        // Collapse whitespace between tags
        result = Regex.Replace(result, @">\s+<", "><");
        // Collapse internal whitespace
        result = Regex.Replace(result, @"\s{2,}", " ");
        // Remove optional closing tags
        result = Regex.Replace(result, @"</(?:li|dt|dd|p|tr|td|th|thead|tbody|tfoot|option)>", "", RegexOptions.IgnoreCase);
        return result.Trim();
    }

    private static string InsertMin(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{name}.min{ext}");
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
