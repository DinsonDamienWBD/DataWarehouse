using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Category assignment for a data object.
/// </summary>
public sealed class CategoryAssignment
{
    /// <summary>
    /// Primary category.
    /// </summary>
    public required string PrimaryCategory { get; init; }

    /// <summary>
    /// Secondary categories.
    /// </summary>
    public IReadOnlyList<string> SecondaryCategories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Confidence in the assignment (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Whether this was AI-generated.
    /// </summary>
    public bool IsAiGenerated { get; init; }
}

/// <summary>
/// Tag suggestion for a data object.
/// </summary>
public sealed class TagSuggestion
{
    /// <summary>
    /// Suggested tag.
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// Relevance score (0.0-1.0).
    /// </summary>
    public double Relevance { get; init; }

    /// <summary>
    /// Source of the suggestion.
    /// </summary>
    public string Source { get; init; } = "ai";
}

/// <summary>
/// Folder/structure suggestion.
/// </summary>
public sealed class StructureSuggestion
{
    /// <summary>
    /// Suggested path or folder.
    /// </summary>
    public required string SuggestedPath { get; init; }

    /// <summary>
    /// Confidence in the suggestion (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Reasoning for the suggestion.
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// Alternative paths.
    /// </summary>
    public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Organization result for a data object.
/// </summary>
public sealed class OrganizationResult
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Category assignment.
    /// </summary>
    public CategoryAssignment? Category { get; init; }

    /// <summary>
    /// Suggested tags.
    /// </summary>
    public IReadOnlyList<TagSuggestion> SuggestedTags { get; init; } = Array.Empty<TagSuggestion>();

    /// <summary>
    /// Structure suggestion.
    /// </summary>
    public StructureSuggestion? Structure { get; init; }

    /// <summary>
    /// Related object IDs.
    /// </summary>
    public IReadOnlyList<string> RelatedObjects { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether AI was used.
    /// </summary>
    public bool UsedAi { get; init; }

    /// <summary>
    /// Processing duration.
    /// </summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Content for organization analysis.
/// </summary>
public sealed class OrganizableContent
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Filename or path.
    /// </summary>
    public string? Filename { get; init; }

    /// <summary>
    /// Text content for analysis.
    /// </summary>
    public string? TextContent { get; init; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Existing tags.
    /// </summary>
    public string[]? ExistingTags { get; init; }

    /// <summary>
    /// Existing category.
    /// </summary>
    public string? ExistingCategory { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Self-organizing data strategy using AI for autonomous categorization,
/// tagging, and structure suggestions.
/// </summary>
/// <remarks>
/// Features:
/// - AI-powered auto-categorization
/// - Intelligent auto-tagging
/// - Folder/structure suggestions
/// - Related content discovery
/// - Graceful fallback to rule-based organization
/// </remarks>
public sealed class SelfOrganizingDataStrategy : AiEnhancedStrategyBase
{
    private readonly ConcurrentDictionary<string, OrganizationResult> _organizationCache = new();
    private readonly ConcurrentDictionary<string, float[]> _embeddingCache = new();
    private readonly string[] _defaultCategories;
    private readonly TimeSpan _cacheTtl;

    /// <summary>
    /// Initializes a new SelfOrganizingDataStrategy.
    /// </summary>
    public SelfOrganizingDataStrategy() : this(GetDefaultCategories(), TimeSpan.FromHours(24)) { }

    /// <summary>
    /// Initializes a new SelfOrganizingDataStrategy with custom categories.
    /// </summary>
    /// <param name="categories">Available categories for classification.</param>
    /// <param name="cacheTtl">Cache TTL for organization results.</param>
    public SelfOrganizingDataStrategy(string[] categories, TimeSpan cacheTtl)
    {
        _defaultCategories = categories ?? GetDefaultCategories();
        _cacheTtl = cacheTtl;
    }

    private static string[] GetDefaultCategories() => new[]
    {
        "Documents", "Images", "Audio", "Video", "Archives",
        "Code", "Data", "Configurations", "Logs", "Reports",
        "Presentations", "Spreadsheets", "Emails", "Backups", "Other"
    };

    /// <inheritdoc/>
    public override string StrategyId => "ai.self-organizing";

    /// <inheritdoc/>
    public override string DisplayName => "Self-Organizing Data";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.SelfOrganizing;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.Classification | IntelligenceCapabilities.KeywordExtraction;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 500,
        TypicalLatencyMs = 100.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Self-organizing data strategy using AI for autonomous categorization, " +
        "intelligent tagging, and folder structure suggestions based on content analysis.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "organization", "categorization", "tagging", "auto-organize"];

    /// <summary>
    /// Gets available categories.
    /// </summary>
    public IReadOnlyList<string> Categories => _defaultCategories;

    /// <summary>
    /// Organizes content and returns categorization, tags, and structure suggestions.
    /// </summary>
    /// <param name="content">Content to organize.</param>
    /// <param name="forceRefresh">Force new analysis instead of using cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Organization result.</returns>
    public async Task<OrganizationResult> OrganizeAsync(
        OrganizableContent content,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(content.ObjectId);

        // Check cache
        if (!forceRefresh && _organizationCache.TryGetValue(content.ObjectId, out var cached))
        {
            return cached;
        }

        var sw = Stopwatch.StartNew();

        OrganizationResult result;
        if (IsAiAvailable)
        {
            result = await OrganizeWithAiAsync(content, ct);
        }
        else
        {
            result = OrganizeWithFallback(content);
        }

        sw.Stop();
        result = new OrganizationResult
        {
            ObjectId = result.ObjectId,
            Category = result.Category,
            SuggestedTags = result.SuggestedTags,
            Structure = result.Structure,
            RelatedObjects = result.RelatedObjects,
            UsedAi = result.UsedAi,
            Duration = sw.Elapsed
        };

        _organizationCache[content.ObjectId] = result;
        RecordAiOperation(result.UsedAi, false, result.Category?.Confidence ?? 0, sw.Elapsed.TotalMilliseconds);

        return result;
    }

    /// <summary>
    /// Gets suggested tags for content.
    /// </summary>
    /// <param name="textContent">Text content to analyze.</param>
    /// <param name="maxTags">Maximum number of tags to suggest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of tag suggestions.</returns>
    public async Task<IReadOnlyList<TagSuggestion>> GetTagSuggestionsAsync(
        string textContent,
        int maxTags = 10,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        if (string.IsNullOrWhiteSpace(textContent))
            return Array.Empty<TagSuggestion>();

        if (IsAiAvailable)
        {
            var entities = await RequestEntityExtractionAsync(textContent, null, DefaultContext, ct);
            if (entities != null)
            {
                return entities
                    .Select(e => new TagSuggestion
                    {
                        Tag = e.Text.ToLowerInvariant(),
                        Relevance = e.Confidence,
                        Source = "ai-entity"
                    })
                    .DistinctBy(t => t.Tag)
                    .OrderByDescending(t => t.Relevance)
                    .Take(maxTags)
                    .ToList();
            }
        }

        // Fallback: extract keywords using simple frequency analysis
        return ExtractKeywordsFallback(textContent, maxTags);
    }

    /// <summary>
    /// Gets category assignment for content.
    /// </summary>
    /// <param name="content">Content to categorize.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Category assignment.</returns>
    public async Task<CategoryAssignment> CategorizeAsync(
        OrganizableContent content,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(content);

        var textForAnalysis = BuildTextForAnalysis(content);

        if (IsAiAvailable && !string.IsNullOrWhiteSpace(textForAnalysis))
        {
            var classifications = await RequestClassificationAsync(
                textForAnalysis,
                _defaultCategories,
                true,
                DefaultContext,
                ct);

            if (classifications != null && classifications.Length > 0)
            {
                var sorted = classifications.OrderByDescending(c => c.Confidence).ToList();
                return new CategoryAssignment
                {
                    PrimaryCategory = sorted[0].Category,
                    SecondaryCategories = sorted.Skip(1).Take(2).Select(c => c.Category).ToList(),
                    Confidence = sorted[0].Confidence,
                    IsAiGenerated = true
                };
            }
        }

        // Fallback: rule-based categorization
        return CategorizeByRules(content);
    }

    /// <summary>
    /// Gets folder structure suggestion.
    /// </summary>
    /// <param name="content">Content to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Structure suggestion.</returns>
    public async Task<StructureSuggestion> GetStructureSuggestionAsync(
        OrganizableContent content,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(content);

        var category = await CategorizeAsync(content, ct);
        var year = DateTime.UtcNow.Year.ToString();
        var month = DateTime.UtcNow.ToString("MM");

        var basePath = $"/{category.PrimaryCategory}";

        // Add date-based organization for certain categories
        var needsDateOrg = new[] { "Documents", "Reports", "Logs", "Backups", "Emails" };
        if (needsDateOrg.Contains(category.PrimaryCategory))
        {
            basePath = $"{basePath}/{year}/{month}";
        }

        // Add content-type based subfolders
        if (!string.IsNullOrEmpty(content.ContentType))
        {
            var subtype = GetContentSubtype(content.ContentType);
            if (!string.IsNullOrEmpty(subtype))
            {
                basePath = $"{basePath}/{subtype}";
            }
        }

        var alternatives = new List<string>
        {
            $"/{category.PrimaryCategory}",
            $"/{category.PrimaryCategory}/{year}"
        };

        if (category.SecondaryCategories.Count > 0)
        {
            alternatives.Add($"/{category.SecondaryCategories[0]}");
        }

        return new StructureSuggestion
        {
            SuggestedPath = basePath,
            Confidence = category.Confidence * 0.9,
            Reasoning = $"Based on {(category.IsAiGenerated ? "AI" : "rule-based")} categorization as '{category.PrimaryCategory}'",
            Alternatives = alternatives.Distinct().Where(a => a != basePath).ToList()
        };
    }

    /// <summary>
    /// Finds related objects based on content similarity.
    /// </summary>
    /// <param name="content">Content to find relations for.</param>
    /// <param name="maxResults">Maximum number of related objects.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of related object IDs with similarity scores.</returns>
    public async Task<IReadOnlyList<(string ObjectId, double Similarity)>> FindRelatedAsync(
        OrganizableContent content,
        int maxResults = 10,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(content.TextContent))
            return Array.Empty<(string, double)>();

        if (!IsAiAvailable)
            return Array.Empty<(string, double)>();

        // Get embedding for this content
        var embeddings = await RequestEmbeddingsAsync(new[] { content.TextContent }, DefaultContext, ct);
        if (embeddings == null || embeddings.Length == 0)
            return Array.Empty<(string, double)>();

        var embedding = embeddings[0];
        _embeddingCache[content.ObjectId] = embedding;

        // Find similar objects
        var results = new List<(string ObjectId, double Similarity)>();
        foreach (var (otherId, otherEmbedding) in _embeddingCache)
        {
            if (otherId == content.ObjectId)
                continue;

            var similarity = CalculateCosineSimilarity(embedding, otherEmbedding);
            if (similarity > 0.5)
            {
                results.Add((otherId, similarity));
            }
        }

        return results
            .OrderByDescending(r => r.Similarity)
            .Take(maxResults)
            .ToList();
    }

    private async Task<OrganizationResult> OrganizeWithAiAsync(OrganizableContent content, CancellationToken ct)
    {
        var categoryTask = CategorizeAsync(content, ct);
        var textForTags = BuildTextForAnalysis(content);

        var tagsTask = !string.IsNullOrWhiteSpace(textForTags)
            ? GetTagSuggestionsAsync(textForTags, 10, ct)
            : Task.FromResult<IReadOnlyList<TagSuggestion>>(Array.Empty<TagSuggestion>());

        await Task.WhenAll(categoryTask, tagsTask);

        var category = await categoryTask;
        var tags = await tagsTask;
        var structure = await GetStructureSuggestionAsync(content, ct);
        var related = await FindRelatedAsync(content, 5, ct);

        return new OrganizationResult
        {
            ObjectId = content.ObjectId,
            Category = category,
            SuggestedTags = tags,
            Structure = structure,
            RelatedObjects = related.Select(r => r.ObjectId).ToList(),
            UsedAi = true,
            Duration = TimeSpan.Zero
        };
    }

    private OrganizationResult OrganizeWithFallback(OrganizableContent content)
    {
        var category = CategorizeByRules(content);
        var tags = !string.IsNullOrWhiteSpace(content.TextContent)
            ? ExtractKeywordsFallback(content.TextContent, 10)
            : Array.Empty<TagSuggestion>();

        var year = DateTime.UtcNow.Year.ToString();
        var structure = new StructureSuggestion
        {
            SuggestedPath = $"/{category.PrimaryCategory}/{year}",
            Confidence = 0.5,
            Reasoning = "Based on content type rules"
        };

        return new OrganizationResult
        {
            ObjectId = content.ObjectId,
            Category = category,
            SuggestedTags = tags,
            Structure = structure,
            RelatedObjects = Array.Empty<string>(),
            UsedAi = false,
            Duration = TimeSpan.Zero
        };
    }

    private static string BuildTextForAnalysis(OrganizableContent content)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(content.Filename))
            parts.Add(content.Filename);
        if (!string.IsNullOrWhiteSpace(content.TextContent))
            parts.Add(content.TextContent);
        if (content.ExistingTags != null)
            parts.AddRange(content.ExistingTags);

        return string.Join(" ", parts);
    }

    private CategoryAssignment CategorizeByRules(OrganizableContent content)
    {
        var contentType = content.ContentType?.ToLowerInvariant() ?? "";
        var filename = content.Filename?.ToLowerInvariant() ?? "";

        string category;
        double confidence = 0.6;

        if (contentType.StartsWith("image/"))
            category = "Images";
        else if (contentType.StartsWith("audio/"))
            category = "Audio";
        else if (contentType.StartsWith("video/"))
            category = "Video";
        else if (contentType.Contains("pdf") || contentType.Contains("document") || contentType.Contains("text/"))
            category = "Documents";
        else if (contentType.Contains("spreadsheet") || filename.EndsWith(".xlsx") || filename.EndsWith(".csv"))
            category = "Spreadsheets";
        else if (contentType.Contains("presentation") || filename.EndsWith(".pptx"))
            category = "Presentations";
        else if (contentType.Contains("zip") || contentType.Contains("tar") || contentType.Contains("rar"))
            category = "Archives";
        else if (filename.EndsWith(".log") || filename.Contains("log"))
            category = "Logs";
        else if (filename.EndsWith(".json") || filename.EndsWith(".xml") || filename.EndsWith(".yaml") || filename.EndsWith(".config"))
            category = "Configurations";
        else if (IsCodeFile(filename))
            category = "Code";
        else
        {
            category = "Other";
            confidence = 0.3;
        }

        return new CategoryAssignment
        {
            PrimaryCategory = category,
            SecondaryCategories = Array.Empty<string>(),
            Confidence = confidence,
            IsAiGenerated = false
        };
    }

    private static bool IsCodeFile(string filename)
    {
        var codeExtensions = new[] { ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".rb", ".php" };
        return codeExtensions.Any(ext => filename.EndsWith(ext));
    }

    private static string? GetContentSubtype(string contentType)
    {
        var parts = contentType.Split('/');
        if (parts.Length >= 2)
        {
            var subtype = parts[1].Split(';')[0].Trim();
            if (subtype.StartsWith("vnd."))
                return null; // Skip vendor-specific types
            if (subtype == "plain" || subtype == "octet-stream")
                return null;
            return subtype;
        }
        return null;
    }

    private static IReadOnlyList<TagSuggestion> ExtractKeywordsFallback(string text, int maxTags)
    {
        var words = text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && w.Length < 30)
            .Where(w => !StopWords.Contains(w))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(maxTags)
            .Select(g => new TagSuggestion
            {
                Tag = g.Key,
                Relevance = Math.Min(1.0, g.Count() / 10.0),
                Source = "frequency"
            })
            .ToList();

        return words;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "had", "her", "was", "one", "our", "out",
        "this", "that", "with", "have", "from", "they", "been", "said", "each", "which", "their", "will",
        "about", "would", "there", "could", "other", "into", "than", "some", "these", "then", "what"
    };
}
