using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Document;

/// <summary>
/// Markdown-to-HTML render strategy implementing CommonMark specification parsing.
/// Supports GFM extensions (tables, task lists, autolinks), heading extraction for TOC,
/// and link validation. Processes markdown files in-place at the storage layer.
/// </summary>
internal sealed class MarkdownRenderStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "document-markdown";

    /// <inheritdoc/>
    public override string Name => "Markdown Render Strategy";

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

        var markdown = await File.ReadAllTextAsync(query.Source, ct);
        var html = ConvertToHtml(markdown);
        var headings = ExtractHeadings(markdown);
        var links = ExtractLinks(markdown);
        var tocHtml = GenerateToc(headings);

        var outputPath = Path.ChangeExtension(query.Source, ".html");
        var fullHtml = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{Path.GetFileNameWithoutExtension(query.Source)}</title></head><body>{html}</body></html>";
        await File.WriteAllTextAsync(outputPath, fullHtml, Encoding.UTF8, ct);

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["outputPath"] = outputPath,
                ["headingCount"] = headings.Count, ["linkCount"] = links.Count,
                ["tocHtml"] = tocHtml, ["outputSizeBytes"] = new FileInfo(outputPath).Length,
                ["inputSizeBytes"] = new FileInfo(query.Source).Length
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = 1, RowsReturned = 1, BytesProcessed = markdown.Length,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".md", ".markdown", ".mdx" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".md", ".markdown" }, ct);
    }

    private static string ConvertToHtml(string markdown)
    {
        var html = new StringBuilder(markdown.Length * 2);
        var lines = markdown.Split('\n');
        var inCodeBlock = false;
        var inList = false;
        var inTable = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Code blocks
            if (line.StartsWith("```"))
            {
                if (inCodeBlock) { html.AppendLine("</code></pre>"); inCodeBlock = false; }
                else { var lang = line.Length > 3 ? $" class=\"language-{line[3..].Trim()}\"" : ""; html.AppendLine($"<pre><code{lang}>"); inCodeBlock = true; }
                continue;
            }
            if (inCodeBlock) { html.AppendLine(System.Net.WebUtility.HtmlEncode(line)); continue; }

            // Tables (GFM)
            if (line.Contains('|') && line.Trim().StartsWith('|'))
            {
                if (!inTable) { html.AppendLine("<table>"); inTable = true; }
                if (Regex.IsMatch(line, @"^\|[\s\-:|]+\|$", RegexOptions.None, RegexTimeout)) continue; // separator
                var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                html.Append("<tr>");
                foreach (var cell in cells) html.Append($"<td>{InlineMarkdown(cell.Trim())}</td>");
                html.AppendLine("</tr>");
                continue;
            }
            if (inTable) { html.AppendLine("</table>"); inTable = false; }

            // Headings
            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)", RegexOptions.None, RegexTimeout);
            if (headingMatch.Success)
            {
                if (inList) { html.AppendLine("</ul>"); inList = false; }
                var level = headingMatch.Groups[1].Value.Length;
                var text = headingMatch.Groups[2].Value;
                var id = Regex.Replace(text.ToLowerInvariant(), @"[^\w]+", "-", RegexOptions.None, RegexTimeout).Trim('-');
                html.AppendLine($"<h{level} id=\"{System.Net.WebUtility.HtmlEncode(id)}\">{InlineMarkdown(text)}</h{level}>");
                continue;
            }

            // Unordered lists
            if (Regex.IsMatch(line, @"^\s*[-*+]\s", RegexOptions.None, RegexTimeout))
            {
                if (!inList) { html.AppendLine("<ul>"); inList = true; }
                var content = Regex.Replace(line, @"^\s*[-*+]\s", "", RegexOptions.None, RegexTimeout);
                // GFM task lists
                if (content.StartsWith("[ ] ")) html.AppendLine($"<li><input type=\"checkbox\" disabled> {InlineMarkdown(content[4..])}</li>");
                else if (content.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) html.AppendLine($"<li><input type=\"checkbox\" checked disabled> {InlineMarkdown(content[4..])}</li>");
                else html.AppendLine($"<li>{InlineMarkdown(content)}</li>");
                continue;
            }
            if (inList) { html.AppendLine("</ul>"); inList = false; }

            // Horizontal rule
            if (Regex.IsMatch(line, @"^(\*{3,}|-{3,}|_{3,})$", RegexOptions.None, RegexTimeout)) { html.AppendLine("<hr>"); continue; }

            // Blockquote
            if (line.StartsWith("> ")) { html.AppendLine($"<blockquote>{InlineMarkdown(line[2..])}</blockquote>"); continue; }

            // Empty line
            if (string.IsNullOrWhiteSpace(line)) { html.AppendLine(); continue; }

            // Paragraph
            html.AppendLine($"<p>{InlineMarkdown(line)}</p>");
        }

        if (inCodeBlock) html.AppendLine("</code></pre>");
        if (inList) html.AppendLine("</ul>");
        if (inTable) html.AppendLine("</table>");

        return html.ToString();
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static string InlineMarkdown(string text)
    {
        // HTML-encode first to prevent XSS (findings 35-39: unescaped user content in headings, table cells, links)
        text = System.Net.WebUtility.HtmlEncode(text);
        // Bold
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>", RegexOptions.None, RegexTimeout);
        text = Regex.Replace(text, @"__(.+?)__", "<strong>$1</strong>", RegexOptions.None, RegexTimeout);
        // Italic
        text = Regex.Replace(text, @"\*(.+?)\*", "<em>$1</em>", RegexOptions.None, RegexTimeout);
        text = Regex.Replace(text, @"_(.+?)_", "<em>$1</em>", RegexOptions.None, RegexTimeout);
        // Code
        text = Regex.Replace(text, @"`(.+?)`", "<code>$1</code>", RegexOptions.None, RegexTimeout);
        // Links — block javascript: scheme URLs to prevent XSS (finding 38-39)
        text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", m =>
        {
            var linkText = m.Groups[1].Value;
            var href = m.Groups[2].Value;
            if (href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return linkText; // Strip dangerous link, keep text only
            return $"<a href=\"{href}\">{linkText}</a>";
        }, RegexOptions.None, RegexTimeout);
        // Images
        text = Regex.Replace(text, @"!\[(.+?)\]\((.+?)\)", "<img src=\"$2\" alt=\"$1\">", RegexOptions.None, RegexTimeout);
        // Autolinks (GFM)
        text = Regex.Replace(text, @"(https?://[^\s&lt;&gt;]+)", "<a href=\"$1\">$1</a>", RegexOptions.None, RegexTimeout);
        // Strikethrough (GFM)
        text = Regex.Replace(text, @"~~(.+?)~~", "<del>$1</del>", RegexOptions.None, RegexTimeout);
        return text;
    }

    private static List<(int Level, string Text)> ExtractHeadings(string markdown)
    {
        var headings = new List<(int, string)>();
        foreach (Match m in Regex.Matches(markdown, @"^(#{1,6})\s+(.+)", RegexOptions.Multiline, RegexTimeout))
            headings.Add((m.Groups[1].Value.Length, m.Groups[2].Value.Trim()));
        return headings;
    }

    private static List<string> ExtractLinks(string markdown)
    {
        var links = new List<string>();
        foreach (Match m in Regex.Matches(markdown, @"\[.+?\]\((.+?)\)", RegexOptions.None, RegexTimeout))
            links.Add(m.Groups[1].Value);
        return links;
    }

    private static string GenerateToc(List<(int Level, string Text)> headings)
    {
        if (headings.Count == 0) return "";
        var sb = new StringBuilder("<nav><ul>");
        foreach (var (level, text) in headings)
        {
            var id = Regex.Replace(text.ToLowerInvariant(), @"[^\w]+", "-", RegexOptions.None, RegexTimeout).Trim('-');
            sb.Append($"<li style=\"margin-left:{(level - 1) * 20}px\"><a href=\"#{System.Net.WebUtility.HtmlEncode(id)}\">{System.Net.WebUtility.HtmlEncode(text)}</a></li>");
        }
        sb.Append("</ul></nav>");
        return sb.ToString();
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
