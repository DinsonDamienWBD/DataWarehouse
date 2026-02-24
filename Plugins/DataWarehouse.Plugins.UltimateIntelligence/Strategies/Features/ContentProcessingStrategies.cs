using DataWarehouse.SDK.AI;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Content extraction feature strategy (90.R2.2).
/// Extracts text content from various document formats including PDFs, Office documents, and images.
/// </summary>
public sealed class ContentExtractionStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-content-extraction";

    /// <inheritdoc/>
    public override string StrategyName => "Content Extraction";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Content Extraction",
        Description = "Extracts text content from PDFs, Office documents, images, and other formats using AI-powered OCR and parsing",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ImageAnalysis,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "EnableOCR", Description = "Enable OCR for images", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "OcrLanguages", Description = "OCR languages (comma-separated)", Required = false, DefaultValue = "en" },
            new ConfigurationRequirement { Key = "ExtractMetadata", Description = "Extract document metadata", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "MaxContentLength", Description = "Maximum content length to extract", Required = false, DefaultValue = "1000000" }
        },
        CostTier = 2,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        Tags = new[] { "content-extraction", "ocr", "pdf", "office", "text-extraction" }
    };

    /// <summary>
    /// Extracts text content from a document.
    /// </summary>
    /// <param name="content">Document content as bytes.</param>
    /// <param name="contentType">MIME type of the content.</param>
    /// <param name="fileName">Optional file name for format detection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extraction result with text and metadata.</returns>
    public async Task<ContentExtractionResult> ExtractAsync(
        byte[] content,
        string contentType,
        string? fileName = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var maxLength = int.Parse(GetConfig("MaxContentLength") ?? "1000000");
            var extractMetadata = bool.Parse(GetConfig("ExtractMetadata") ?? "true");

            var result = new ContentExtractionResult
            {
                OriginalFormat = contentType,
                FileName = fileName,
                ExtractedAt = DateTime.UtcNow
            };

            // Determine extraction method based on content type
            var extractedText = contentType.ToLowerInvariant() switch
            {
                "text/plain" => ExtractPlainText(content, maxLength),
                "text/html" => ExtractHtml(content, maxLength),
                "text/markdown" or "text/x-markdown" => ExtractMarkdown(content, maxLength),
                "application/pdf" => await ExtractPdfAsync(content, ct),
                "application/msword" or
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" =>
                    await ExtractWordAsync(content, ct),
                "application/vnd.ms-excel" or
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" =>
                    await ExtractExcelAsync(content, ct),
                "application/vnd.ms-powerpoint" or
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" =>
                    await ExtractPowerPointAsync(content, ct),
                "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/tiff" =>
                    await ExtractImageTextAsync(content, contentType, ct),
                "application/json" => ExtractJson(content, maxLength),
                "application/xml" or "text/xml" => ExtractXml(content, maxLength),
                _ => await ExtractUnknownAsync(content, contentType, ct)
            };

            result.ExtractedText = extractedText;
            result.CharacterCount = extractedText.Length;
            result.WordCount = CountWords(extractedText);

            // Extract metadata if enabled
            if (extractMetadata)
            {
                result.Metadata = await ExtractMetadataAsync(content, contentType, ct);
            }

            // Detect language
            result.DetectedLanguage = await DetectLanguageAsync(extractedText, ct);

            return result;
        });
    }

    /// <summary>
    /// Extracts content from multiple documents in batch.
    /// </summary>
    public async Task<IEnumerable<ContentExtractionResult>> ExtractBatchAsync(
        IEnumerable<(byte[] Content, string ContentType, string? FileName)> documents,
        CancellationToken ct = default)
    {
        var results = new List<ContentExtractionResult>();
        foreach (var doc in documents)
        {
            results.Add(await ExtractAsync(doc.Content, doc.ContentType, doc.FileName, ct));
        }
        return results;
    }

    private static string ExtractPlainText(byte[] content, int maxLength)
    {
        var text = Encoding.UTF8.GetString(content);
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string ExtractHtml(byte[] content, int maxLength)
    {
        var html = Encoding.UTF8.GetString(content);
        // Remove scripts and styles
        html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        // Remove tags but keep content
        var text = Regex.Replace(html, @"<[^>]+>", " ");
        // Normalize whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();
        // Decode entities
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string ExtractMarkdown(byte[] content, int maxLength)
    {
        var md = Encoding.UTF8.GetString(content);
        // Remove markdown formatting
        var text = Regex.Replace(md, @"!\[.*?\]\(.*?\)", ""); // Images
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1"); // Links
        text = Regex.Replace(text, @"[#*_`~]", ""); // Formatting chars
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "", RegexOptions.Multiline); // List markers
        text = Regex.Replace(text, @"^\s*\d+\.\s+", "", RegexOptions.Multiline); // Numbered lists
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private async Task<string> ExtractPdfAsync(byte[] content, CancellationToken ct)
    {
        // For production, integrate with actual PDF library (PdfPig, iTextSharp, etc.)
        // Using AI for extraction when vision is available
        if (AIProvider != null && AIProvider.Capabilities.HasFlag(AICapabilities.ImageAnalysis))
        {
            var base64Content = Convert.ToBase64String(content);
            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = "Extract all text content from this PDF document. Maintain paragraph structure. Return only the extracted text.",
                MaxTokens = 4000,
                ExtendedParameters = new Dictionary<string, object>
                {
                    ["image_data"] = base64Content,
                    ["image_mime_type"] = "application/pdf"
                }
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);
            return response.Content;
        }

        // Fallback: Extract embedded text (simple ASCII extraction for text-based PDFs)
        return ExtractTextFromBinary(content);
    }

    private async Task<string> ExtractWordAsync(byte[] content, CancellationToken ct)
    {
        // For DOCX files (ZIP-based XML), extract document.xml content
        if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B) // ZIP signature
        {
            try
            {
                using var stream = new MemoryStream(content);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

                var docEntry = archive.GetEntry("word/document.xml");
                if (docEntry != null)
                {
                    using var entryStream = docEntry.Open();
                    using var reader = new StreamReader(entryStream);
                    var xml = await reader.ReadToEndAsync(ct);
                    // Extract text from XML
                    return Regex.Replace(xml, @"<[^>]+>", " ").Replace("  ", " ").Trim();
                }
            }
            catch { /* ZIP extraction failure — fall back to AI */ }
        }

        return await ExtractWithAIAsync(content, "Microsoft Word document", ct);
    }

    private async Task<string> ExtractExcelAsync(byte[] content, CancellationToken ct)
    {
        // For XLSX files, extract shared strings and sheet data
        if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B) // ZIP signature
        {
            try
            {
                using var stream = new MemoryStream(content);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

                var result = new StringBuilder();

                var sharedStrings = archive.GetEntry("xl/sharedStrings.xml");
                if (sharedStrings != null)
                {
                    using var entryStream = sharedStrings.Open();
                    using var reader = new StreamReader(entryStream);
                    var xml = await reader.ReadToEndAsync(ct);
                    // Extract string values
                    var matches = Regex.Matches(xml, @"<t[^>]*>([^<]+)</t>");
                    foreach (Match match in matches)
                    {
                        result.Append(match.Groups[1].Value).Append(' ');
                    }
                }

                if (result.Length > 0)
                    return result.ToString().Trim();
            }
            catch { /* ZIP extraction failure — fall back to AI */ }
        }

        return await ExtractWithAIAsync(content, "Microsoft Excel spreadsheet", ct);
    }

    private async Task<string> ExtractPowerPointAsync(byte[] content, CancellationToken ct)
    {
        // For PPTX files, extract slide content
        if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B) // ZIP signature
        {
            try
            {
                using var stream = new MemoryStream(content);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

                var result = new StringBuilder();

                // Extract text from all slides
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml")))
                {
                    using var entryStream = entry.Open();
                    using var reader = new StreamReader(entryStream);
                    var xml = await reader.ReadToEndAsync(ct);
                    // Extract text from <a:t> tags
                    var matches = Regex.Matches(xml, @"<a:t>([^<]+)</a:t>");
                    foreach (Match match in matches)
                    {
                        result.Append(match.Groups[1].Value).Append(' ');
                    }
                    result.AppendLine();
                }

                if (result.Length > 0)
                    return result.ToString().Trim();
            }
            catch { /* ZIP extraction failure — fall back to AI */ }
        }

        return await ExtractWithAIAsync(content, "Microsoft PowerPoint presentation", ct);
    }

    private async Task<string> ExtractImageTextAsync(byte[] content, string contentType, CancellationToken ct)
    {
        var enableOcr = bool.Parse(GetConfig("EnableOCR") ?? "true");
        if (!enableOcr)
            return "[Image - OCR disabled]";

        if (AIProvider == null || !AIProvider.Capabilities.HasFlag(AICapabilities.ImageAnalysis))
            return "[Image - No vision capability available]";

        var base64Content = Convert.ToBase64String(content);
        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = "Extract all visible text from this image using OCR. Return only the extracted text, maintaining the original layout where possible.",
            MaxTokens = 2000,
            ExtendedParameters = new Dictionary<string, object>
            {
                ["image_data"] = base64Content,
                ["image_mime_type"] = contentType
            }
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);
        return response.Content;
    }

    private static string ExtractJson(byte[] content, int maxLength)
    {
        var json = Encoding.UTF8.GetString(content);
        // Extract string values from JSON
        var matches = Regex.Matches(json, "\"([^\"]+)\"\\s*:");
        var result = new StringBuilder();
        foreach (Match match in matches)
        {
            result.Append(match.Groups[1].Value).Append(' ');
        }
        var text = result.ToString();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string ExtractXml(byte[] content, int maxLength)
    {
        var xml = Encoding.UTF8.GetString(content);
        var text = Regex.Replace(xml, @"<[^>]+>", " ").Replace("  ", " ").Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private async Task<string> ExtractUnknownAsync(byte[] content, string contentType, CancellationToken ct)
    {
        // Try text extraction first
        var text = ExtractTextFromBinary(content);
        if (text.Length > 100)
            return text;

        return await ExtractWithAIAsync(content, contentType, ct);
    }

    private async Task<string> ExtractWithAIAsync(byte[] content, string format, CancellationToken ct)
    {
        if (AIProvider == null)
            return $"[{format} - No AI provider available for extraction]";

        var base64Content = Convert.ToBase64String(content[..Math.Min(content.Length, 50000)]);
        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = $"This is a {format}. Extract and return all readable text content.",
            MaxTokens = 2000,
            ExtendedParameters = new Dictionary<string, object>
            {
                ["image_data"] = base64Content
            }
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);
        return response.Content;
    }

    private static string ExtractTextFromBinary(byte[] content)
    {
        var result = new StringBuilder();
        var inText = false;
        var textStart = 0;

        for (int i = 0; i < content.Length; i++)
        {
            var b = content[i];
            var isPrintable = (b >= 32 && b <= 126) || b == '\n' || b == '\r' || b == '\t';

            if (isPrintable && !inText)
            {
                inText = true;
                textStart = i;
            }
            else if (!isPrintable && inText)
            {
                if (i - textStart >= 4) // Minimum text run length
                {
                    var text = Encoding.ASCII.GetString(content, textStart, i - textStart);
                    if (Regex.IsMatch(text, @"\w{2,}"))
                        result.Append(text).Append(' ');
                }
                inText = false;
            }
        }

        return result.ToString().Trim();
    }

    private async Task<Dictionary<string, object>> ExtractMetadataAsync(byte[] content, string contentType, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object>
        {
            ["contentType"] = contentType,
            ["sizeBytes"] = content.Length,
            ["extractedAt"] = DateTime.UtcNow
        };

        // Try to extract document properties from Office formats
        if (contentType.Contains("openxmlformats") && content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B)
        {
            try
            {
                using var stream = new MemoryStream(content);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

                var coreProps = archive.GetEntry("docProps/core.xml");
                if (coreProps != null)
                {
                    using var entryStream = coreProps.Open();
                    using var reader = new StreamReader(entryStream);
                    var xml = await reader.ReadToEndAsync(ct);

                    var titleMatch = Regex.Match(xml, @"<dc:title>([^<]+)</dc:title>");
                    if (titleMatch.Success) metadata["title"] = titleMatch.Groups[1].Value;

                    var creatorMatch = Regex.Match(xml, @"<dc:creator>([^<]+)</dc:creator>");
                    if (creatorMatch.Success) metadata["author"] = creatorMatch.Groups[1].Value;

                    var createdMatch = Regex.Match(xml, @"<dcterms:created[^>]*>([^<]+)</dcterms:created>");
                    if (createdMatch.Success) metadata["created"] = createdMatch.Groups[1].Value;

                    var modifiedMatch = Regex.Match(xml, @"<dcterms:modified[^>]*>([^<]+)</dcterms:modified>");
                    if (modifiedMatch.Success) metadata["modified"] = modifiedMatch.Groups[1].Value;
                }
            }
            catch { /* ZIP extraction failure — return empty metadata */ }
        }

        return metadata;
    }

    private async Task<string?> DetectLanguageAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 20)
            return null;

        // Simple language detection using common words
        var lowerText = text.ToLowerInvariant();
        var langScores = new Dictionary<string, int>
        {
            ["en"] = CountMatches(lowerText, new[] { "the", "and", "is", "are", "was", "have", "been" }),
            ["es"] = CountMatches(lowerText, new[] { "que", "de", "en", "el", "la", "los", "las" }),
            ["fr"] = CountMatches(lowerText, new[] { "le", "la", "les", "de", "et", "est", "sont" }),
            ["de"] = CountMatches(lowerText, new[] { "der", "die", "das", "und", "ist", "sind", "ein" }),
            ["it"] = CountMatches(lowerText, new[] { "che", "di", "il", "la", "per", "sono", "una" }),
            ["pt"] = CountMatches(lowerText, new[] { "que", "de", "em", "os", "para", "uma", "como" })
        };

        var maxLang = langScores.OrderByDescending(x => x.Value).First();
        return maxLang.Value > 3 ? maxLang.Key : null;
    }

    private static int CountMatches(string text, string[] words)
    {
        return words.Sum(w => Regex.Matches(text, $@"\b{w}\b").Count);
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"\b\w+\b").Count;
    }
}

/// <summary>
/// Result of content extraction.
/// </summary>
public sealed class ContentExtractionResult
{
    /// <summary>Extracted text content.</summary>
    public string ExtractedText { get; set; } = "";

    /// <summary>Original file format/MIME type.</summary>
    public string OriginalFormat { get; init; } = "";

    /// <summary>Original file name.</summary>
    public string? FileName { get; init; }

    /// <summary>Number of characters extracted.</summary>
    public int CharacterCount { get; set; }

    /// <summary>Number of words extracted.</summary>
    public int WordCount { get; set; }

    /// <summary>Detected language code.</summary>
    public string? DetectedLanguage { get; set; }

    /// <summary>Extracted document metadata.</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>When extraction was performed.</summary>
    public DateTime ExtractedAt { get; init; }
}

/// <summary>
/// Content summarization feature strategy (90.R2.4).
/// Auto-generates summaries of text content using AI.
/// </summary>
public sealed class ContentSummarizationStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-content-summarization";

    /// <inheritdoc/>
    public override string StrategyName => "Content Summarization";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Content Summarization",
        Description = "AI-powered automatic summarization of text content with multiple styles and lengths",
        Capabilities = IntelligenceCapabilities.Summarization | IntelligenceCapabilities.TextCompletion,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "DefaultStyle", Description = "Default summary style (brief|detailed|bullet)", Required = false, DefaultValue = "brief" },
            new ConfigurationRequirement { Key = "MaxOutputTokens", Description = "Maximum tokens for summary", Required = false, DefaultValue = "500" },
            new ConfigurationRequirement { Key = "PreserveKeyFacts", Description = "Explicitly preserve key facts", Required = false, DefaultValue = "true" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "summarization", "tldr", "abstract", "condensing" }
    };

    /// <summary>
    /// Summarizes text content.
    /// </summary>
    /// <param name="content">Text to summarize.</param>
    /// <param name="style">Summary style (brief, detailed, bullet, executive, technical).</param>
    /// <param name="targetLength">Target length in words (approximate).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary result.</returns>
    public async Task<SummarizationResult> SummarizeAsync(
        string content,
        SummaryStyle? style = null,
        int? targetLength = null,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for summarization");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var summaryStyle = style ?? ParseStyle(GetConfig("DefaultStyle") ?? "brief");
            var maxTokens = int.Parse(GetConfig("MaxOutputTokens") ?? "500");
            var preserveKeyFacts = bool.Parse(GetConfig("PreserveKeyFacts") ?? "true");

            var prompt = BuildSummarizationPrompt(content, summaryStyle, targetLength, preserveKeyFacts);

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = maxTokens,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            var summary = response.Content.Trim();
            var (parsedSummary, keyFacts) = ParseSummaryResponse(summary, summaryStyle);

            return new SummarizationResult
            {
                OriginalLength = content.Length,
                OriginalWordCount = CountWords(content),
                Summary = parsedSummary,
                SummaryWordCount = CountWords(parsedSummary),
                CompressionRatio = content.Length > 0 ? (float)parsedSummary.Length / content.Length : 0,
                Style = summaryStyle,
                KeyFacts = keyFacts,
                GeneratedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Generates summaries at multiple detail levels.
    /// </summary>
    public async Task<MultiLevelSummary> SummarizeMultiLevelAsync(
        string content,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for summarization");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var prompt = $@"Create summaries of this content at three detail levels:

CONTENT:
{content[..Math.Min(content.Length, 8000)]}

Provide your response in this exact JSON format:
{{
  ""one_liner"": ""Single sentence capturing the main point"",
  ""paragraph"": ""2-3 paragraph summary with key details"",
  ""detailed"": ""Comprehensive summary with all important points"",
  ""key_topics"": [""topic1"", ""topic2"", ""topic3""],
  ""key_entities"": [""entity1"", ""entity2""]
}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 1500,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseMultiLevelSummary(response.Content, content);
        });
    }

    /// <summary>
    /// Summarizes a collection of documents into a unified summary.
    /// </summary>
    public async Task<CollectionSummary> SummarizeCollectionAsync(
        IEnumerable<(string Title, string Content)> documents,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for summarization");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var docs = documents.ToList();

            // First, summarize each document
            var docSummaries = new List<string>();
            foreach (var doc in docs.Take(20)) // Limit to prevent token overflow
            {
                var summary = await SummarizeAsync(doc.Content, SummaryStyle.Brief, 50, ct);
                docSummaries.Add($"- {doc.Title}: {summary.Summary}");
            }

            // Then create a unified summary
            var combinedPrompt = $@"Create a unified summary of these {docs.Count} documents:

DOCUMENT SUMMARIES:
{string.Join("\n", docSummaries)}

Provide:
1. An overall summary of all documents (2-3 paragraphs)
2. Common themes across documents
3. Key differences or unique points

Format as JSON:
{{{{
  ""unified_summary"": ""..."",
  ""common_themes"": [""theme1"", ""theme2""],
  ""unique_points"": [""point1"", ""point2""],
  ""document_count"": {docs.Count}
}}}}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = combinedPrompt,
                MaxTokens = 1000,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            return ParseCollectionSummary(response.Content, docs.Count);
        });
    }

    private static string BuildSummarizationPrompt(string content, SummaryStyle style, int? targetLength, bool preserveKeyFacts)
    {
        var styleInstruction = style switch
        {
            SummaryStyle.Brief => "Create a brief, concise summary in 1-2 sentences.",
            SummaryStyle.Detailed => "Create a detailed summary covering all major points.",
            SummaryStyle.Bullet => "Create a bullet-point summary with key points.",
            SummaryStyle.Executive => "Create an executive summary suitable for leadership briefing.",
            SummaryStyle.Technical => "Create a technical summary preserving key terminology and details.",
            _ => "Create a concise summary."
        };

        var lengthInstruction = targetLength.HasValue
            ? $" Target approximately {targetLength} words."
            : "";

        var keyFactsInstruction = preserveKeyFacts
            ? "\n\nAlso list 3-5 key facts that must not be lost in the summary."
            : "";

        return $@"{styleInstruction}{lengthInstruction}

CONTENT TO SUMMARIZE:
{content[..Math.Min(content.Length, 12000)]}

{(style == SummaryStyle.Bullet ? "Use bullet points (-)." : "")}
{keyFactsInstruction}";
    }

    private static (string Summary, List<string> KeyFacts) ParseSummaryResponse(string response, SummaryStyle style)
    {
        var keyFacts = new List<string>();

        // Check for key facts section
        var keyFactsMatch = Regex.Match(response, @"Key [Ff]acts?:?\s*([\s\S]*?)(?:\n\n|$)");
        if (keyFactsMatch.Success)
        {
            var factsText = keyFactsMatch.Groups[1].Value;
            keyFacts = Regex.Matches(factsText, @"[-*]\s*(.+)")
                .Select(m => m.Groups[1].Value.Trim())
                .ToList();
            response = response.Replace(keyFactsMatch.Value, "").Trim();
        }

        return (response, keyFacts);
    }

    private static MultiLevelSummary ParseMultiLevelSummary(string response, string originalContent)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                return new MultiLevelSummary
                {
                    OneLiner = doc.RootElement.GetProperty("one_liner").GetString() ?? "",
                    Paragraph = doc.RootElement.GetProperty("paragraph").GetString() ?? "",
                    Detailed = doc.RootElement.GetProperty("detailed").GetString() ?? "",
                    KeyTopics = doc.RootElement.GetProperty("key_topics").EnumerateArray()
                        .Select(e => e.GetString() ?? "").ToList(),
                    KeyEntities = doc.RootElement.GetProperty("key_entities").EnumerateArray()
                        .Select(e => e.GetString() ?? "").ToList(),
                    OriginalWordCount = CountWords(originalContent)
                };
            }
        }
        catch { /* Parsing failure — return simple summary */ }

        return new MultiLevelSummary
        {
            OneLiner = response,
            OriginalWordCount = CountWords(originalContent)
        };
    }

    private static CollectionSummary ParseCollectionSummary(string response, int docCount)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                return new CollectionSummary
                {
                    UnifiedSummary = doc.RootElement.GetProperty("unified_summary").GetString() ?? "",
                    CommonThemes = doc.RootElement.GetProperty("common_themes").EnumerateArray()
                        .Select(e => e.GetString() ?? "").ToList(),
                    UniquePoints = doc.RootElement.GetProperty("unique_points").EnumerateArray()
                        .Select(e => e.GetString() ?? "").ToList(),
                    DocumentCount = docCount
                };
            }
        }
        catch { /* Parsing failure — return simple summary */ }

        return new CollectionSummary
        {
            UnifiedSummary = response,
            DocumentCount = docCount
        };
    }

    private static SummaryStyle ParseStyle(string style) => style.ToLowerInvariant() switch
    {
        "brief" => SummaryStyle.Brief,
        "detailed" => SummaryStyle.Detailed,
        "bullet" => SummaryStyle.Bullet,
        "executive" => SummaryStyle.Executive,
        "technical" => SummaryStyle.Technical,
        _ => SummaryStyle.Brief
    };

    private static int CountWords(string text) => Regex.Matches(text, @"\b\w+\b").Count;
}

/// <summary>
/// Summary style options.
/// </summary>
public enum SummaryStyle
{
    /// <summary>Brief 1-2 sentence summary.</summary>
    Brief,
    /// <summary>Detailed multi-paragraph summary.</summary>
    Detailed,
    /// <summary>Bullet-point list summary.</summary>
    Bullet,
    /// <summary>Executive briefing style.</summary>
    Executive,
    /// <summary>Technical summary with terminology.</summary>
    Technical
}

/// <summary>
/// Result of content summarization.
/// </summary>
public sealed class SummarizationResult
{
    /// <summary>Original content length in characters.</summary>
    public int OriginalLength { get; init; }

    /// <summary>Original word count.</summary>
    public int OriginalWordCount { get; init; }

    /// <summary>Generated summary.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Summary word count.</summary>
    public int SummaryWordCount { get; init; }

    /// <summary>Compression ratio (summary/original).</summary>
    public float CompressionRatio { get; init; }

    /// <summary>Summary style used.</summary>
    public SummaryStyle Style { get; init; }

    /// <summary>Key facts extracted.</summary>
    public List<string> KeyFacts { get; init; } = new();

    /// <summary>When summary was generated.</summary>
    public DateTime GeneratedAt { get; init; }
}

/// <summary>
/// Multi-level summary result.
/// </summary>
public sealed class MultiLevelSummary
{
    /// <summary>Single sentence summary.</summary>
    public string OneLiner { get; init; } = "";

    /// <summary>Paragraph-length summary.</summary>
    public string Paragraph { get; init; } = "";

    /// <summary>Detailed comprehensive summary.</summary>
    public string Detailed { get; init; } = "";

    /// <summary>Key topics identified.</summary>
    public List<string> KeyTopics { get; init; } = new();

    /// <summary>Key entities mentioned.</summary>
    public List<string> KeyEntities { get; init; } = new();

    /// <summary>Original word count.</summary>
    public int OriginalWordCount { get; init; }
}

/// <summary>
/// Summary of a document collection.
/// </summary>
public sealed class CollectionSummary
{
    /// <summary>Unified summary of all documents.</summary>
    public string UnifiedSummary { get; init; } = "";

    /// <summary>Common themes across documents.</summary>
    public List<string> CommonThemes { get; init; } = new();

    /// <summary>Unique points from individual documents.</summary>
    public List<string> UniquePoints { get; init; } = new();

    /// <summary>Number of documents summarized.</summary>
    public int DocumentCount { get; init; }
}
