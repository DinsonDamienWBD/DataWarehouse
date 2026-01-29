// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Engine for automatic content categorization using AI and statistical methods.
/// Supports hierarchical categories, keyword matching, embedding similarity,
/// and AI-based classification with automatic fallback.
/// </summary>
public sealed class CategorizationEngine
{
    private IExtendedAIProvider? _aiProvider;
    private readonly SDK.AI.IVectorStore? _vectorStore;
    private readonly CategoryHierarchy _categoryHierarchy;
    private readonly ConcurrentDictionary<string, float[]> _categoryEmbeddings;
    private readonly TextTokenizer _tokenizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CategorizationEngine"/> class.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced categorization.</param>
    /// <param name="vectorStore">Optional vector store for embedding operations.</param>
    /// <param name="categoryHierarchy">Optional pre-configured category hierarchy.</param>
    public CategorizationEngine(
        IExtendedAIProvider? aiProvider = null,
        SDK.AI.IVectorStore? vectorStore = null,
        CategoryHierarchy? categoryHierarchy = null)
    {
        _aiProvider = aiProvider;
        _vectorStore = vectorStore;
        _categoryHierarchy = categoryHierarchy ?? new CategoryHierarchy();
        _categoryEmbeddings = new ConcurrentDictionary<string, float[]>();
        _tokenizer = new TextTokenizer();

        // Initialize default categories if empty
        if (_categoryHierarchy.Count == 0)
        {
            _categoryHierarchy.InitializeDefaults();
        }
    }

    /// <summary>
    /// Sets or updates the AI provider.
    /// </summary>
    /// <param name="provider">The AI provider to use.</param>
    public void SetProvider(IExtendedAIProvider? provider)
    {
        _aiProvider = provider;
    }

    /// <summary>
    /// Gets whether an AI provider is available.
    /// </summary>
    public bool IsAIAvailable => _aiProvider?.IsAvailable ?? false;

    /// <summary>
    /// Categorizes content using the best available method.
    /// </summary>
    /// <param name="text">Text to categorize.</param>
    /// <param name="options">Categorization options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Categorization result.</returns>
    public async Task<CategorizationResult> CategorizeAsync(
        string text,
        CategorizationOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new CategorizationOptions();

        try
        {
            // Try AI categorization first if available and preferred
            if (options.PreferAI && IsAIAvailable)
            {
                var aiResult = await CategorizeWithAIAsync(text, options, ct);
                if (aiResult != null)
                {
                    sw.Stop();
                    return aiResult with { Duration = sw.Elapsed, Method = CategorizationMethod.AI };
                }
            }

            // Try embedding-based categorization
            if (_vectorStore != null && _aiProvider != null && _categoryEmbeddings.Count > 0)
            {
                var embeddingResult = await CategorizeWithEmbeddingsAsync(text, options, ct);
                if (embeddingResult != null && embeddingResult.PrimaryConfidence > options.MinConfidence)
                {
                    sw.Stop();
                    return embeddingResult with { Duration = sw.Elapsed, Method = CategorizationMethod.Statistical };
                }
            }

            // Fall back to keyword-based categorization
            var keywordResult = CategorizeWithKeywords(text, options);
            sw.Stop();

            return keywordResult with { Duration = sw.Elapsed, Method = CategorizationMethod.RuleBased };
        }
        catch (Exception)
        {
            sw.Stop();
            return new CategorizationResult
            {
                PrimaryCategory = "uncategorized",
                PrimaryConfidence = 0,
                Method = CategorizationMethod.RuleBased,
                Duration = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Categorizes content using AI.
    /// </summary>
    private async Task<CategorizationResult?> CategorizeWithAIAsync(
        string text,
        CategorizationOptions options,
        CancellationToken ct)
    {
        if (_aiProvider == null || !_aiProvider.IsAvailable)
            return null;

        // Build category list for the prompt
        var categories = _categoryHierarchy.GetAllCategories()
            .Select(c => $"- {c.Id}: {c.Name}" + (c.Description != null ? $" ({c.Description})" : ""))
            .ToList();

        var categoryList = string.Join("\n", categories.Take(50)); // Limit to avoid token limits

        var prompt = $@"Classify the following document into the most appropriate category.

Available categories:
{categoryList}

Document:
{TruncateText(text, 2000)}

Respond in this exact format:
CATEGORY: [category_id]
CONFIDENCE: [0.0-1.0]
SECONDARY: [comma-separated list of secondary category_ids]
FEATURES: [comma-separated key features that influenced classification]";

        var response = await _aiProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a document classification system. Analyze content and assign appropriate categories with confidence scores.",
            MaxTokens = 300,
            Temperature = 0.3f
        }, ct);

        if (!response.Success)
            return null;

        return ParseAICategorization(response.Content);
    }

    /// <summary>
    /// Categorizes content using embedding similarity.
    /// </summary>
    private async Task<CategorizationResult?> CategorizeWithEmbeddingsAsync(
        string text,
        CategorizationOptions options,
        CancellationToken ct)
    {
        if (_aiProvider == null)
            return null;

        // Get embedding for the text
        var textEmbedding = await _aiProvider.GetEmbeddingsAsync(TruncateText(text, 5000), ct);
        if (textEmbedding == null || textEmbedding.Length == 0)
            return null;

        // Ensure all categories have embeddings
        await EnsureCategoryEmbeddingsAsync(ct);

        // Find most similar category
        var similarities = new List<(string CategoryId, float Similarity)>();

        foreach (var category in _categoryHierarchy.GetAllCategories())
        {
            if (_categoryEmbeddings.TryGetValue(category.Id, out var catEmbedding))
            {
                var similarity = VectorMath.CosineSimilarity(textEmbedding, catEmbedding);
                similarities.Add((category.Id, similarity));
            }
        }

        if (similarities.Count == 0)
            return null;

        similarities = similarities.OrderByDescending(s => s.Similarity).ToList();
        var best = similarities[0];

        var secondaryCategories = similarities
            .Skip(1)
            .Take(5)
            .Where(s => s.Similarity > options.MinConfidence)
            .ToDictionary(s => s.CategoryId, s => s.Similarity);

        return new CategorizationResult
        {
            PrimaryCategory = best.CategoryId,
            PrimaryConfidence = best.Similarity,
            SecondaryCategories = secondaryCategories,
            CategoryPath = _categoryHierarchy.GetCategoryPath(best.CategoryId),
            HierarchyLevel = _categoryHierarchy.GetHierarchyLevel(best.CategoryId)
        };
    }

    /// <summary>
    /// Categorizes content using keyword matching.
    /// </summary>
    private CategorizationResult CategorizeWithKeywords(string text, CategorizationOptions options)
    {
        var tokens = _tokenizer.Tokenize(text.ToLowerInvariant());
        var tokenSet = new HashSet<string>(tokens);

        var scores = new Dictionary<string, (float Score, List<string> Features)>();

        foreach (var category in _categoryHierarchy.GetAllCategories())
        {
            var matchCount = 0;
            var matchedKeywords = new List<string>();

            foreach (var keyword in category.Keywords)
            {
                if (tokenSet.Contains(keyword.ToLowerInvariant()) ||
                    text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matchCount++;
                    matchedKeywords.Add(keyword);
                }
            }

            // Check patterns
            foreach (var pattern in category.Patterns)
            {
                try
                {
                    if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    {
                        matchCount += 2; // Patterns weighted higher
                        matchedKeywords.Add($"pattern:{pattern.Substring(0, Math.Min(20, pattern.Length))}");
                    }
                }
                catch { /* Invalid regex or timeout */ }
            }

            if (matchCount > 0)
            {
                var score = (float)matchCount / Math.Max(category.Keywords.Count + category.Patterns.Count, 1);
                score = Math.Min(score, 1.0f);
                scores[category.Id] = (score, matchedKeywords);
            }
        }

        if (scores.Count == 0)
        {
            return new CategorizationResult
            {
                PrimaryCategory = "uncategorized",
                PrimaryConfidence = 0
            };
        }

        var best = scores.OrderByDescending(s => s.Value.Score).First();
        var secondaryCategories = scores
            .Where(s => s.Key != best.Key && s.Value.Score > options.MinConfidence)
            .OrderByDescending(s => s.Value.Score)
            .Take(5)
            .ToDictionary(s => s.Key, s => s.Value.Score);

        return new CategorizationResult
        {
            PrimaryCategory = best.Key,
            PrimaryConfidence = best.Value.Score,
            SecondaryCategories = secondaryCategories,
            CategoryPath = _categoryHierarchy.GetCategoryPath(best.Key),
            HierarchyLevel = _categoryHierarchy.GetHierarchyLevel(best.Key),
            InfluencingFeatures = best.Value.Features
        };
    }

    /// <summary>
    /// Trains a category with example documents.
    /// </summary>
    /// <param name="categoryId">Category ID to train.</param>
    /// <param name="examples">Example documents.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TrainCategoryAsync(
        string categoryId,
        IEnumerable<string> examples,
        CancellationToken ct = default)
    {
        var category = _categoryHierarchy.GetCategory(categoryId);
        if (category == null)
            throw new ArgumentException($"Category not found: {categoryId}");

        var exampleList = examples.ToList();

        // Extract keywords from examples
        var allTokens = new List<string>();
        foreach (var example in exampleList)
        {
            var tokens = _tokenizer.Tokenize(example.ToLowerInvariant());
            allTokens.AddRange(tokens);
        }

        // Find most common tokens as keywords
        var tokenCounts = allTokens
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => g.Key)
            .ToList();

        // Update category with new keywords
        var updatedCategory = new CategoryDefinition
        {
            Id = category.Id,
            Name = category.Name,
            Description = category.Description,
            ParentId = category.ParentId,
            Keywords = category.Keywords.Union(tokenCounts).Distinct().ToList(),
            Patterns = category.Patterns,
            MinConfidenceThreshold = category.MinConfidenceThreshold,
            ChildIds = category.ChildIds,
            Examples = category.Examples.Concat(
                exampleList.Select(e => new CategoryExample { Text = e })
            ).ToList()
        };

        // Generate category embedding from all examples
        if (_aiProvider != null)
        {
            try
            {
                var combinedText = string.Join(" ", exampleList);
                var embedding = await _aiProvider.GetEmbeddingsAsync(TruncateText(combinedText, 5000), ct);
                if (embedding != null && embedding.Length > 0)
                {
                    _categoryEmbeddings[categoryId] = embedding;
                }
            }
            catch { /* Continue without embedding */ }
        }

        _categoryHierarchy.AddCategory(updatedCategory);
    }

    /// <summary>
    /// Adds a custom category.
    /// </summary>
    /// <param name="category">Category definition.</param>
    public void AddCategory(CategoryDefinition category)
    {
        _categoryHierarchy.AddCategory(category);
    }

    /// <summary>
    /// Gets all registered categories.
    /// </summary>
    /// <returns>All category definitions.</returns>
    public IEnumerable<CategoryDefinition> GetCategories()
    {
        return _categoryHierarchy.GetAllCategories();
    }

    /// <summary>
    /// Ensures all categories have computed embeddings.
    /// </summary>
    private async Task EnsureCategoryEmbeddingsAsync(CancellationToken ct)
    {
        if (_aiProvider == null)
            return;

        foreach (var category in _categoryHierarchy.GetAllCategories())
        {
            if (!_categoryEmbeddings.ContainsKey(category.Id))
            {
                try
                {
                    // Create text from category info
                    var categoryText = $"{category.Name} {category.Description} " +
                                       string.Join(" ", category.Keywords);

                    if (category.Examples.Count > 0)
                    {
                        categoryText += " " + string.Join(" ", category.Examples.Take(3).Select(e => e.Text));
                    }

                    var embedding = await _aiProvider.GetEmbeddingsAsync(categoryText, ct);
                    if (embedding != null && embedding.Length > 0)
                    {
                        _categoryEmbeddings[category.Id] = embedding;
                    }
                }
                catch { /* Continue without this category's embedding */ }
            }
        }
    }

    private CategorizationResult ParseAICategorization(string response)
    {
        var result = new CategorizationResult
        {
            PrimaryCategory = "uncategorized",
            PrimaryConfidence = 0
        };

        var categoryMatch = Regex.Match(response, @"CATEGORY:\s*(\S+)");
        if (categoryMatch.Success)
        {
            result = result with { PrimaryCategory = categoryMatch.Groups[1].Value };
        }

        var confidenceMatch = Regex.Match(response, @"CONFIDENCE:\s*([0-9.]+)");
        if (confidenceMatch.Success && float.TryParse(confidenceMatch.Groups[1].Value, out var confidence))
        {
            result = result with { PrimaryConfidence = Math.Clamp(confidence, 0, 1) };
        }

        var secondaryMatch = Regex.Match(response, @"SECONDARY:\s*(.+?)(?:FEATURES:|$)", RegexOptions.Singleline);
        if (secondaryMatch.Success)
        {
            var secondaries = secondaryMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToDictionary(s => s, s => 0.5f);
            result = result with { SecondaryCategories = secondaries };
        }

        var featuresMatch = Regex.Match(response, @"FEATURES:\s*(.+?)$", RegexOptions.Singleline);
        if (featuresMatch.Success)
        {
            var features = featuresMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            result = result with { InfluencingFeatures = features };
        }

        // Fill in hierarchy info
        if (result.PrimaryCategory != "uncategorized")
        {
            result = result with
            {
                CategoryPath = _categoryHierarchy.GetCategoryPath(result.PrimaryCategory),
                HierarchyLevel = _categoryHierarchy.GetHierarchyLevel(result.PrimaryCategory)
            };
        }

        return result;
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text[..maxLength];
    }
}

/// <summary>
/// Internal text tokenizer for keyword extraction.
/// </summary>
internal sealed class TextTokenizer
{
    private static readonly Regex TokenRegex = new(@"\b[a-zA-Z][a-zA-Z0-9]*\b", RegexOptions.Compiled);

    /// <summary>
    /// Tokenizes text into words.
    /// </summary>
    /// <param name="text">Text to tokenize.</param>
    /// <returns>List of tokens.</returns>
    public List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var matches = TokenRegex.Matches(text);
        return matches.Select(m => m.Value.ToLowerInvariant()).ToList();
    }
}

/// <summary>
/// Options for categorization.
/// </summary>
public sealed record CategorizationOptions
{
    /// <summary>Prefer AI over statistical methods when available.</summary>
    public bool PreferAI { get; init; } = true;

    /// <summary>Minimum confidence threshold for a valid categorization.</summary>
    public float MinConfidence { get; init; } = 0.3f;

    /// <summary>Maximum number of categories to return.</summary>
    public int MaxCategories { get; init; } = 5;

    /// <summary>Include hierarchy path in results.</summary>
    public bool IncludeHierarchy { get; init; } = true;
}

/// <summary>
/// Result of automatic categorization.
/// </summary>
public sealed record CategorizationResult
{
    /// <summary>Primary category assigned.</summary>
    public string PrimaryCategory { get; init; } = string.Empty;

    /// <summary>Confidence score for primary category (0-1).</summary>
    public float PrimaryConfidence { get; init; }

    /// <summary>Secondary categories with confidence scores.</summary>
    public Dictionary<string, float> SecondaryCategories { get; init; } = new();

    /// <summary>Full category path in hierarchy (e.g., "Documents/Legal/Contracts").</summary>
    public string? CategoryPath { get; init; }

    /// <summary>Category hierarchy level (0 = root).</summary>
    public int HierarchyLevel { get; init; }

    /// <summary>Key features that influenced the categorization.</summary>
    public List<string> InfluencingFeatures { get; init; } = new();

    /// <summary>Categorization method used.</summary>
    public CategorizationMethod Method { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Categorization method used.
/// </summary>
public enum CategorizationMethod
{
    /// <summary>Rule-based (keyword matching).</summary>
    RuleBased,
    /// <summary>Statistical (embedding similarity).</summary>
    Statistical,
    /// <summary>AI/LLM-based classification.</summary>
    AI,
    /// <summary>Hybrid of multiple methods.</summary>
    Hybrid
}

/// <summary>
/// Category definition for the hierarchy.
/// </summary>
public sealed class CategoryDefinition
{
    /// <summary>Unique category identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>Category description.</summary>
    public string? Description { get; init; }

    /// <summary>Parent category ID (null for root).</summary>
    public string? ParentId { get; init; }

    /// <summary>Keywords for matching.</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>Regex patterns for matching.</summary>
    public List<string> Patterns { get; init; } = new();

    /// <summary>Minimum confidence threshold.</summary>
    public float MinConfidenceThreshold { get; init; } = 0.5f;

    /// <summary>Child category IDs.</summary>
    public List<string> ChildIds { get; init; } = new();

    /// <summary>Example documents for training.</summary>
    public List<CategoryExample> Examples { get; init; } = new();
}

/// <summary>
/// Training example for a category.
/// </summary>
public sealed class CategoryExample
{
    /// <summary>Example text content.</summary>
    public required string Text { get; init; }

    /// <summary>Pre-computed embedding.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Weight of this example.</summary>
    public float Weight { get; init; } = 1.0f;
}

/// <summary>
/// Manages category hierarchy and definitions.
/// </summary>
public sealed class CategoryHierarchy
{
    private readonly ConcurrentDictionary<string, CategoryDefinition> _categories = new();
    private readonly ConcurrentDictionary<string, List<string>> _childrenMap = new();

    /// <summary>Root category IDs.</summary>
    public IEnumerable<string> RootCategories =>
        _categories.Values.Where(c => c.ParentId == null).Select(c => c.Id);

    /// <summary>Total category count.</summary>
    public int Count => _categories.Count;

    /// <summary>
    /// Adds or updates a category.
    /// </summary>
    public void AddCategory(CategoryDefinition category)
    {
        _categories[category.Id] = category;

        if (category.ParentId != null)
        {
            _childrenMap.AddOrUpdate(
                category.ParentId,
                _ => new List<string> { category.Id },
                (_, list) =>
                {
                    if (!list.Contains(category.Id))
                        list.Add(category.Id);
                    return list;
                });
        }
    }

    /// <summary>
    /// Gets a category by ID.
    /// </summary>
    public CategoryDefinition? GetCategory(string id) =>
        _categories.TryGetValue(id, out var cat) ? cat : null;

    /// <summary>
    /// Gets all categories.
    /// </summary>
    public IEnumerable<CategoryDefinition> GetAllCategories() => _categories.Values;

    /// <summary>
    /// Gets the full path to a category.
    /// </summary>
    public string GetCategoryPath(string categoryId)
    {
        var path = new List<string>();
        var current = GetCategory(categoryId);

        while (current != null)
        {
            path.Insert(0, current.Name);
            current = current.ParentId != null ? GetCategory(current.ParentId) : null;
        }

        return string.Join("/", path);
    }

    /// <summary>
    /// Gets hierarchy level for a category.
    /// </summary>
    public int GetHierarchyLevel(string categoryId)
    {
        var level = 0;
        var current = GetCategory(categoryId);

        while (current?.ParentId != null)
        {
            level++;
            current = GetCategory(current.ParentId);
        }

        return level;
    }

    /// <summary>
    /// Initializes with default categories.
    /// </summary>
    public void InitializeDefaults()
    {
        // Document types
        AddCategory(new CategoryDefinition { Id = "documents", Name = "Documents", Keywords = new() { "document", "file", "paper" } });
        AddCategory(new CategoryDefinition { Id = "documents.legal", Name = "Legal", ParentId = "documents", Keywords = new() { "contract", "agreement", "legal", "law", "attorney", "court" } });
        AddCategory(new CategoryDefinition { Id = "documents.financial", Name = "Financial", ParentId = "documents", Keywords = new() { "invoice", "receipt", "payment", "financial", "budget", "expense" } });
        AddCategory(new CategoryDefinition { Id = "documents.technical", Name = "Technical", ParentId = "documents", Keywords = new() { "technical", "specification", "api", "documentation", "manual" } });
        AddCategory(new CategoryDefinition { Id = "documents.hr", Name = "HR", ParentId = "documents", Keywords = new() { "employee", "resume", "cv", "hiring", "hr", "human resources", "benefits" } });

        // Media
        AddCategory(new CategoryDefinition { Id = "media", Name = "Media", Keywords = new() { "media", "image", "video", "audio" } });
        AddCategory(new CategoryDefinition { Id = "media.images", Name = "Images", ParentId = "media", Keywords = new() { "image", "photo", "picture", "screenshot" } });
        AddCategory(new CategoryDefinition { Id = "media.videos", Name = "Videos", ParentId = "media", Keywords = new() { "video", "movie", "clip", "recording" } });
        AddCategory(new CategoryDefinition { Id = "media.audio", Name = "Audio", ParentId = "media", Keywords = new() { "audio", "music", "podcast", "recording", "voice" } });

        // Communications
        AddCategory(new CategoryDefinition { Id = "communications", Name = "Communications", Keywords = new() { "email", "message", "chat", "communication" } });
        AddCategory(new CategoryDefinition { Id = "communications.email", Name = "Email", ParentId = "communications", Keywords = new() { "email", "mail", "inbox", "outbox" } });
        AddCategory(new CategoryDefinition { Id = "communications.chat", Name = "Chat", ParentId = "communications", Keywords = new() { "chat", "message", "instant", "slack", "teams" } });

        // Code
        AddCategory(new CategoryDefinition { Id = "code", Name = "Code", Keywords = new() { "code", "source", "programming", "software" } });
        AddCategory(new CategoryDefinition { Id = "code.source", Name = "Source Code", ParentId = "code", Keywords = new() { "code", "function", "class", "method", "variable" } });
        AddCategory(new CategoryDefinition { Id = "code.config", Name = "Configuration", ParentId = "code", Keywords = new() { "config", "configuration", "settings", "json", "yaml", "xml" } });

        // Data
        AddCategory(new CategoryDefinition { Id = "data", Name = "Data", Keywords = new() { "data", "database", "dataset", "records" } });
        AddCategory(new CategoryDefinition { Id = "data.structured", Name = "Structured Data", ParentId = "data", Keywords = new() { "csv", "excel", "spreadsheet", "table", "database" } });
        AddCategory(new CategoryDefinition { Id = "data.logs", Name = "Logs", ParentId = "data", Keywords = new() { "log", "logging", "trace", "debug", "error" } });
    }
}
