// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using DataWarehouse.SDK.AI;
using SDKAIProvider = DataWarehouse.SDK.AI.IAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Engine for text summarization supporting both extractive (sentence selection)
/// and abstractive (AI-generated) approaches with configurable length and style.
/// </summary>
public sealed class SummarizationEngine
{
    private SDKAIProvider? _aiProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SummarizationEngine"/> class.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for abstractive summarization.</param>
    public SummarizationEngine(SDKAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
    }

    /// <summary>
    /// Sets or updates the AI provider.
    /// </summary>
    /// <param name="provider">The AI provider to use.</param>
    public void SetProvider(SDKAIProvider? provider)
    {
        _aiProvider = provider;
    }

    /// <summary>
    /// Gets whether an AI provider is available.
    /// </summary>
    public bool IsAIAvailable => _aiProvider?.IsAvailable ?? false;

    /// <summary>
    /// Generates a summary of the given text.
    /// </summary>
    /// <param name="text">Text to summarize.</param>
    /// <param name="options">Summarization options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summarization result.</returns>
    public async Task<SummarizationResult> SummarizeAsync(
        string text,
        SummarizationOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new SummarizationOptions();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new SummarizationResult
            {
                Summary = string.Empty,
                Type = SummarizationType.None,
                CompressionRatio = 1.0f,
                Duration = sw.Elapsed
            };
        }

        // Determine target length based on options
        var targetLength = CalculateTargetLength(text.Length, options);

        // If text is already short enough, return as-is
        if (text.Length <= targetLength)
        {
            sw.Stop();
            return new SummarizationResult
            {
                Summary = text.Trim(),
                Type = SummarizationType.None,
                OriginalLength = text.Length,
                SummaryLength = text.Length,
                CompressionRatio = 1.0f,
                Duration = sw.Elapsed
            };
        }

        // Try abstractive summarization with AI first
        if (options.PreferAbstractive && IsAIAvailable)
        {
            try
            {
                var abstractiveResult = await GenerateAbstractiveSummaryAsync(text, options, targetLength, ct);
                if (abstractiveResult != null)
                {
                    sw.Stop();
                    return abstractiveResult with { Duration = sw.Elapsed };
                }
            }
            catch { /* Fall back to extractive */ }
        }

        // Fall back to extractive summarization
        var extractiveResult = GenerateExtractiveSummary(text, options, targetLength);
        sw.Stop();

        return extractiveResult with { Duration = sw.Elapsed };
    }

    /// <summary>
    /// Generates a multi-document summary.
    /// </summary>
    /// <param name="documents">Documents to summarize.</param>
    /// <param name="options">Summarization options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Multi-document summarization result.</returns>
    public async Task<MultiDocumentSummaryResult> SummarizeMultipleAsync(
        IEnumerable<DocumentForSummarization> documents,
        SummarizationOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new SummarizationOptions();
        var docList = documents.ToList();

        if (docList.Count == 0)
        {
            return new MultiDocumentSummaryResult
            {
                OverallSummary = string.Empty,
                DocumentCount = 0,
                Duration = sw.Elapsed
            };
        }

        // Summarize each document first
        var individualSummaries = new List<DocumentSummary>();
        foreach (var doc in docList)
        {
            ct.ThrowIfCancellationRequested();

            var summary = await SummarizeAsync(doc.Content, options with
            {
                TargetLength = SummaryLength.Short // Keep individual summaries short
            }, ct);

            individualSummaries.Add(new DocumentSummary
            {
                DocumentId = doc.Id,
                Title = doc.Title,
                Summary = summary.Summary,
                KeyPoints = summary.KeySentences.Take(3).ToList()
            });
        }

        // Generate overall summary
        var combinedText = string.Join("\n\n", individualSummaries.Select(s => s.Summary));
        var overallSummary = await SummarizeAsync(combinedText, options with
        {
            TargetLength = options.TargetLength
        }, ct);

        // Extract common themes
        var commonThemes = ExtractCommonThemes(individualSummaries);

        sw.Stop();

        return new MultiDocumentSummaryResult
        {
            OverallSummary = overallSummary.Summary,
            IndividualSummaries = individualSummaries,
            CommonThemes = commonThemes,
            DocumentCount = docList.Count,
            TotalOriginalLength = docList.Sum(d => d.Content.Length),
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Extracts key points from text.
    /// </summary>
    /// <param name="text">Text to analyze.</param>
    /// <param name="maxPoints">Maximum number of key points.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of key points.</returns>
    public async Task<List<string>> ExtractKeyPointsAsync(
        string text,
        int maxPoints = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        // Try AI extraction first
        if (IsAIAvailable)
        {
            try
            {
                var aiPoints = await ExtractKeyPointsWithAIAsync(text, maxPoints, ct);
                if (aiPoints.Count > 0)
                    return aiPoints;
            }
            catch { /* Fall back to extractive */ }
        }

        // Extractive key point extraction
        return ExtractKeyPointsStatistically(text, maxPoints);
    }

    /// <summary>
    /// Generates an executive summary (high-level overview).
    /// </summary>
    /// <param name="text">Text to summarize.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Executive summary.</returns>
    public async Task<ExecutiveSummaryResult> GenerateExecutiveSummaryAsync(
        string text,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var keyPoints = await ExtractKeyPointsAsync(text, 5, ct);
        var summary = await SummarizeAsync(text, new SummarizationOptions
        {
            TargetLength = SummaryLength.Brief,
            PreferAbstractive = true,
            Style = SummaryStyle.Professional
        }, ct);

        var mainTopics = ExtractMainTopics(text);

        sw.Stop();

        return new ExecutiveSummaryResult
        {
            Summary = summary.Summary,
            KeyPoints = keyPoints,
            MainTopics = mainTopics,
            OriginalLength = text.Length,
            Duration = sw.Elapsed
        };
    }

    #region Private Methods

    private int CalculateTargetLength(int originalLength, SummarizationOptions options)
    {
        if (options.MaxCharacters > 0)
            return options.MaxCharacters;

        var ratio = options.TargetLength switch
        {
            SummaryLength.Brief => 0.10f,
            SummaryLength.Short => 0.20f,
            SummaryLength.Medium => 0.35f,
            SummaryLength.Long => 0.50f,
            _ => 0.25f
        };

        return Math.Max(100, (int)(originalLength * ratio));
    }

    private async Task<SummarizationResult?> GenerateAbstractiveSummaryAsync(
        string text,
        SummarizationOptions options,
        int targetLength,
        CancellationToken ct)
    {
        if (_aiProvider == null || !_aiProvider.IsAvailable)
            return null;

        // Truncate very long texts
        var maxInputLength = 8000;
        var inputText = text.Length > maxInputLength ? text[..maxInputLength] : text;

        var lengthInstruction = options.TargetLength switch
        {
            SummaryLength.Brief => "very brief (1-2 sentences)",
            SummaryLength.Short => "short (2-3 sentences)",
            SummaryLength.Medium => "moderate (4-6 sentences)",
            SummaryLength.Long => "comprehensive (7-10 sentences)",
            _ => "concise"
        };

        var styleInstruction = options.Style switch
        {
            SummaryStyle.Professional => "professional and formal",
            SummaryStyle.Casual => "casual and conversational",
            SummaryStyle.Technical => "technical and precise",
            SummaryStyle.Simple => "simple and easy to understand",
            _ => "clear and informative"
        };

        var formatInstruction = options.Format switch
        {
            SummaryFormat.BulletPoints => "\nFormat as bullet points.",
            SummaryFormat.NumberedList => "\nFormat as a numbered list.",
            SummaryFormat.Structured => "\nFormat with clear sections: Overview, Key Points, Conclusion.",
            _ => ""
        };

        var focusInstruction = !string.IsNullOrEmpty(options.FocusArea)
            ? $"\nFocus particularly on: {options.FocusArea}"
            : "";

        var prompt = $@"Summarize the following text. Make it {lengthInstruction}, {styleInstruction}.{formatInstruction}{focusInstruction}

Text:
{inputText}

Summary:";

        var response = await _aiProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a professional text summarizer. Create concise, accurate summaries that capture the essential information.",
            MaxTokens = Math.Min(500, targetLength / 2),
            Temperature = 0.3f
        }, ct);

        if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
            return null;

        var summary = response.Content.Trim();

        // Extract key sentences for reference
        var keySentences = ExtractKeyPointsStatistically(text, 3);

        return new SummarizationResult
        {
            Summary = summary,
            Type = SummarizationType.Abstractive,
            OriginalLength = text.Length,
            SummaryLength = summary.Length,
            CompressionRatio = (float)summary.Length / text.Length,
            KeySentences = keySentences,
            MainTopics = ExtractMainTopics(text),
            UsedAI = true
        };
    }

    private SummarizationResult GenerateExtractiveSummary(
        string text,
        SummarizationOptions options,
        int targetLength)
    {
        var sentences = SplitIntoSentences(text);

        if (sentences.Count == 0)
        {
            return new SummarizationResult
            {
                Summary = text.Length <= targetLength ? text : text[..targetLength],
                Type = SummarizationType.Extractive,
                OriginalLength = text.Length,
                SummaryLength = Math.Min(text.Length, targetLength),
                CompressionRatio = (float)Math.Min(text.Length, targetLength) / text.Length
            };
        }

        // Score sentences by importance
        var scoredSentences = sentences
            .Select((s, i) => new
            {
                Sentence = s,
                Index = i,
                Score = CalculateSentenceScore(s, i, sentences.Count, text)
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Select sentences up to target length
        var selectedSentences = new List<(string Sentence, int Index)>();
        var currentLength = 0;

        foreach (var scored in scoredSentences)
        {
            if (currentLength + scored.Sentence.Length <= targetLength)
            {
                selectedSentences.Add((scored.Sentence, scored.Index));
                currentLength += scored.Sentence.Length + 1; // +1 for space
            }

            if (currentLength >= targetLength)
                break;
        }

        // Sort by original order for coherence
        selectedSentences = selectedSentences.OrderBy(x => x.Index).ToList();

        var summary = string.Join(" ", selectedSentences.Select(x => x.Sentence));

        // Format according to options
        if (options.Format == SummaryFormat.BulletPoints)
        {
            summary = string.Join("\n", selectedSentences.Select(x => $"- {x.Sentence}"));
        }
        else if (options.Format == SummaryFormat.NumberedList)
        {
            summary = string.Join("\n", selectedSentences.Select((x, i) => $"{i + 1}. {x.Sentence}"));
        }

        return new SummarizationResult
        {
            Summary = summary,
            Type = SummarizationType.Extractive,
            OriginalLength = text.Length,
            SummaryLength = summary.Length,
            CompressionRatio = (float)summary.Length / text.Length,
            KeySentences = selectedSentences.Select(x => x.Sentence).ToList(),
            MainTopics = ExtractMainTopics(text),
            UsedAI = false
        };
    }

    private float CalculateSentenceScore(string sentence, int position, int totalSentences, string fullText)
    {
        var score = 0f;

        // Position score (first and last sentences often important)
        if (position == 0)
            score += 0.3f;
        else if (position == totalSentences - 1)
            score += 0.15f;
        else if (position < totalSentences * 0.2)
            score += 0.1f;

        // Length score (prefer medium-length sentences)
        var words = sentence.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries);
        var wordCount = words.Length;
        if (wordCount >= 10 && wordCount <= 30)
            score += 0.2f;
        else if (wordCount >= 5 && wordCount <= 50)
            score += 0.1f;

        // Contains key indicators
        var lowerSentence = sentence.ToLowerInvariant();
        if (ContainsKeyIndicator(lowerSentence))
            score += 0.25f;

        // Contains proper nouns (capitalized words not at start)
        var properNouns = words.Skip(1).Count(w => char.IsUpper(w[0]) && w.Length > 1);
        score += Math.Min(0.15f, properNouns * 0.05f);

        // Contains numbers (often factual/important)
        var hasNumbers = words.Any(w => w.Any(char.IsDigit));
        if (hasNumbers)
            score += 0.1f;

        // Term frequency score
        var sentenceWords = new HashSet<string>(words.Select(w => w.ToLowerInvariant()));
        var fullTextWords = fullText.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .GroupBy(w => w)
            .ToDictionary(g => g.Key, g => g.Count());

        var totalWords = fullTextWords.Values.Sum();
        var tfScore = sentenceWords.Sum(w =>
            fullTextWords.TryGetValue(w, out var count) ? (float)count / totalWords : 0);
        score += Math.Min(0.2f, tfScore * 10);

        return score;
    }

    private bool ContainsKeyIndicator(string sentence)
    {
        var indicators = new[]
        {
            "important", "significant", "key", "main", "primary",
            "conclusion", "summary", "result", "finding", "shows",
            "demonstrates", "indicates", "reveals", "suggests",
            "in conclusion", "to summarize", "in summary", "overall",
            "therefore", "consequently", "as a result", "notably"
        };

        return indicators.Any(ind => sentence.Contains(ind));
    }

    private List<string> SplitIntoSentences(string text)
    {
        // Split on sentence-ending punctuation, preserving some edge cases
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z])")
            .Select(s => s.Trim())
            .Where(s => s.Length >= 10) // Filter out very short fragments
            .ToList();

        // Handle cases where split didn't work well
        if (sentences.Count == 0 && text.Length > 10)
        {
            sentences = new List<string> { text };
        }

        return sentences;
    }

    private async Task<List<string>> ExtractKeyPointsWithAIAsync(
        string text,
        int maxPoints,
        CancellationToken ct)
    {
        if (_aiProvider == null || !_aiProvider.IsAvailable)
            return new List<string>();

        var truncatedText = text.Length > 4000 ? text[..4000] : text;

        var response = await _aiProvider.CompleteAsync(new AIRequest
        {
            Prompt = $@"Extract the {maxPoints} most important key points from this text.
Format each point as a single, clear sentence.

Text:
{truncatedText}

Key points (one per line, no numbering or bullets):",
            SystemMessage = "You extract key points from text concisely and accurately.",
            MaxTokens = 300,
            Temperature = 0.2f
        }, ct);

        if (!response.Success)
            return new List<string>();

        return response.Content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')').Trim())
            .Where(p => p.Length > 10)
            .Take(maxPoints)
            .ToList();
    }

    private List<string> ExtractKeyPointsStatistically(string text, int maxPoints)
    {
        var sentences = SplitIntoSentences(text);

        if (sentences.Count == 0)
            return new List<string>();

        // Score and select top sentences
        var scored = sentences
            .Select((s, i) => new { Sentence = s, Score = CalculateSentenceScore(s, i, sentences.Count, text) })
            .OrderByDescending(x => x.Score)
            .Take(maxPoints)
            .Select(x => x.Sentence)
            .ToList();

        return scored;
    }

    private List<string> ExtractMainTopics(string text)
    {
        // Simple TF-IDF-like topic extraction
        var stopWords = new HashSet<string>
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "must", "shall", "can", "of", "in", "to",
            "for", "with", "on", "at", "by", "from", "as", "into", "through",
            "during", "before", "after", "above", "below", "between", "under",
            "again", "further", "then", "once", "here", "there", "when", "where",
            "why", "how", "all", "each", "few", "more", "most", "other", "some",
            "such", "no", "nor", "not", "only", "own", "same", "so", "than",
            "too", "very", "just", "also", "now", "and", "but", "or", "this",
            "that", "these", "those", "it", "its"
        };

        var words = Regex.Matches(text.ToLowerInvariant(), @"\b[a-z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopWords.Contains(w))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToList();

        return words;
    }

    private List<string> ExtractCommonThemes(List<DocumentSummary> summaries)
    {
        var allTopics = summaries
            .SelectMany(s => s.KeyPoints)
            .SelectMany(p => Regex.Matches(p.ToLowerInvariant(), @"\b[a-z]{4,}\b").Select(m => m.Value))
            .GroupBy(w => w)
            .Where(g => g.Count() > 1) // Appears in multiple documents
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToList();

        return allTopics;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Result of summarization.
/// </summary>
public sealed record SummarizationResult
{
    /// <summary>Generated summary text.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Type of summarization used.</summary>
    public SummarizationType Type { get; init; }

    /// <summary>Original text length in characters.</summary>
    public int OriginalLength { get; init; }

    /// <summary>Summary length in characters.</summary>
    public int SummaryLength { get; init; }

    /// <summary>Compression ratio (summary/original).</summary>
    public float CompressionRatio { get; init; }

    /// <summary>Key sentences extracted (for extractive).</summary>
    public List<string> KeySentences { get; init; } = new();

    /// <summary>Main topics identified.</summary>
    public List<string> MainTopics { get; init; } = new();

    /// <summary>Whether AI was used.</summary>
    public bool UsedAI { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Type of summarization.
/// </summary>
public enum SummarizationType
{
    /// <summary>No summarization performed (text already short).</summary>
    None,

    /// <summary>Extractive summary (key sentences from text).</summary>
    Extractive,

    /// <summary>Abstractive summary (AI-generated).</summary>
    Abstractive,

    /// <summary>Hybrid approach.</summary>
    Hybrid,

    /// <summary>Multi-document summary.</summary>
    MultiDocument
}

/// <summary>
/// Target length for summaries.
/// </summary>
public enum SummaryLength
{
    /// <summary>Very brief (10% of original).</summary>
    Brief,

    /// <summary>Short (20% of original).</summary>
    Short,

    /// <summary>Medium (35% of original).</summary>
    Medium,

    /// <summary>Long/comprehensive (50% of original).</summary>
    Long
}

/// <summary>
/// Output format for summaries.
/// </summary>
public enum SummaryFormat
{
    /// <summary>Continuous paragraph.</summary>
    Paragraph,

    /// <summary>Bullet point list.</summary>
    BulletPoints,

    /// <summary>Numbered list.</summary>
    NumberedList,

    /// <summary>Structured with sections.</summary>
    Structured
}

/// <summary>
/// Writing style for summaries.
/// </summary>
public enum SummaryStyle
{
    /// <summary>Neutral, balanced.</summary>
    Neutral,

    /// <summary>Professional, formal.</summary>
    Professional,

    /// <summary>Casual, conversational.</summary>
    Casual,

    /// <summary>Technical, precise.</summary>
    Technical,

    /// <summary>Simple, easy to understand.</summary>
    Simple
}

/// <summary>
/// Options for summarization.
/// </summary>
public sealed record SummarizationOptions
{
    /// <summary>Target summary length.</summary>
    public SummaryLength TargetLength { get; init; } = SummaryLength.Short;

    /// <summary>Maximum characters (overrides TargetLength if > 0).</summary>
    public int MaxCharacters { get; init; } = 0;

    /// <summary>Output format.</summary>
    public SummaryFormat Format { get; init; } = SummaryFormat.Paragraph;

    /// <summary>Writing style.</summary>
    public SummaryStyle Style { get; init; } = SummaryStyle.Neutral;

    /// <summary>Prefer abstractive over extractive.</summary>
    public bool PreferAbstractive { get; init; } = true;

    /// <summary>Specific area to focus on.</summary>
    public string? FocusArea { get; init; }

    /// <summary>Whether to preserve key numerical details.</summary>
    public bool PreserveKeyDetails { get; init; } = true;
}

/// <summary>
/// Document input for summarization.
/// </summary>
public sealed class DocumentForSummarization
{
    /// <summary>Unique document identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Document title.</summary>
    public string? Title { get; init; }

    /// <summary>Document content.</summary>
    public required string Content { get; init; }

    /// <summary>Optional metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Summary of a single document.
/// </summary>
public sealed class DocumentSummary
{
    /// <summary>Document identifier.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Document title.</summary>
    public string? Title { get; init; }

    /// <summary>Generated summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Key points extracted.</summary>
    public List<string> KeyPoints { get; init; } = new();
}

/// <summary>
/// Result of multi-document summarization.
/// </summary>
public sealed class MultiDocumentSummaryResult
{
    /// <summary>Overall summary across all documents.</summary>
    public string OverallSummary { get; init; } = string.Empty;

    /// <summary>Individual document summaries.</summary>
    public List<DocumentSummary> IndividualSummaries { get; init; } = new();

    /// <summary>Common themes across documents.</summary>
    public List<string> CommonThemes { get; init; } = new();

    /// <summary>Number of documents summarized.</summary>
    public int DocumentCount { get; init; }

    /// <summary>Total original content length.</summary>
    public int TotalOriginalLength { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Result of executive summary generation.
/// </summary>
public sealed class ExecutiveSummaryResult
{
    /// <summary>High-level summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Key points/takeaways.</summary>
    public List<string> KeyPoints { get; init; } = new();

    /// <summary>Main topics identified.</summary>
    public List<string> MainTopics { get; init; } = new();

    /// <summary>Original content length.</summary>
    public int OriginalLength { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }
}

#endregion
