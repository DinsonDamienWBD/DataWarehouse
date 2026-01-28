using DataWarehouse.Plugins.SemanticUnderstanding.Analyzers;
using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for automatic content categorization using AI and statistical methods.
    /// </summary>
    public sealed class CategorizationEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly IVectorStore? _vectorStore;
        private readonly CategoryHierarchy _categoryHierarchy;
        private readonly ConcurrentDictionary<string, float[]> _categoryEmbeddings;
        private readonly TextTokenizer _tokenizer;

        public CategorizationEngine(
            IAIProvider? aiProvider = null,
            IVectorStore? vectorStore = null,
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
        /// Categorizes content using the best available method.
        /// </summary>
        public async Task<CategorizationResult> CategorizeAsync(
            string text,
            CategorizationOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new CategorizationOptions();

            try
            {
                // Try AI categorization first if available
                if (options.PreferAI && _aiProvider != null && _aiProvider.IsAvailable)
                {
                    var aiResult = await CategorizeWithAIAsync(text, options, ct);
                    if (aiResult != null)
                    {
                        sw.Stop();
                        return aiResult with { Duration = sw.Elapsed, Method = CategorizationMethod.AI };
                    }
                }

                // Try embedding-based categorization
                if (_vectorStore != null && _aiProvider?.Capabilities.HasFlag(AICapabilities.Embeddings) == true)
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

            var request = new AIRequest
            {
                Prompt = $@"Classify the following document into the most appropriate category.

Available categories:
{categoryList}

Document:
{TruncateText(text, 2000)}

Respond in this exact format:
CATEGORY: [category_id]
CONFIDENCE: [0.0-1.0]
SECONDARY: [comma-separated list of secondary category_ids]
FEATURES: [comma-separated key features that influenced classification]",
                SystemMessage = "You are a document classification system. Analyze content and assign appropriate categories with confidence scores.",
                MaxTokens = 300
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

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
                        if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                        {
                            matchCount += 2; // Patterns weighted higher
                            matchedKeywords.Add($"pattern:{pattern}");
                        }
                    }
                    catch { /* Invalid regex */ }
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
        public async Task TrainCategoryAsync(
            CategoryTrainingRequest request,
            CancellationToken ct = default)
        {
            var category = _categoryHierarchy.GetCategory(request.CategoryId);
            if (category == null)
                throw new ArgumentException($"Category not found: {request.CategoryId}");

            // Extract keywords from examples
            var allTokens = new List<string>();
            foreach (var example in request.Examples)
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
                    request.Examples.Select(e => new CategoryExample { Text = e })
                ).ToList()
            };

            // Generate category embedding from all examples
            if (_aiProvider != null && _aiProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
            {
                var combinedText = string.Join(" ", request.Examples);
                var embedding = await _aiProvider.GetEmbeddingsAsync(TruncateText(combinedText, 5000), ct);
                if (embedding != null)
                {
                    _categoryEmbeddings[category.Id] = embedding;
                }
            }

            _categoryHierarchy.AddCategory(updatedCategory);

            // Propagate to hierarchy if requested
            if (request.PropagateToHierarchy && category.ParentId != null)
            {
                var parentRequest = new CategoryTrainingRequest
                {
                    CategoryId = category.ParentId,
                    Examples = request.Examples.Take(5).ToList(),
                    PropagateToHierarchy = true
                };
                await TrainCategoryAsync(parentRequest, ct);
            }
        }

        /// <summary>
        /// Adds a custom category.
        /// </summary>
        public void AddCategory(CategoryDefinition category)
        {
            _categoryHierarchy.AddCategory(category);
        }

        /// <summary>
        /// Ensures all categories have computed embeddings.
        /// </summary>
        private async Task EnsureCategoryEmbeddingsAsync(CancellationToken ct)
        {
            if (_aiProvider == null || !_aiProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
                return;

            foreach (var category in _categoryHierarchy.GetAllCategories())
            {
                if (!_categoryEmbeddings.ContainsKey(category.Id))
                {
                    // Create text from category info
                    var categoryText = $"{category.Name} {category.Description} " +
                                       string.Join(" ", category.Keywords);

                    if (category.Examples.Count > 0)
                    {
                        categoryText += " " + string.Join(" ", category.Examples.Take(3).Select(e => e.Text));
                    }

                    var embedding = await _aiProvider.GetEmbeddingsAsync(categoryText, ct);
                    if (embedding != null)
                    {
                        _categoryEmbeddings[category.Id] = embedding;
                    }
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
            if (text.Length <= maxLength)
                return text;
            return text[..maxLength];
        }

        /// <summary>
        /// Internal text tokenizer.
        /// </summary>
        private sealed class TextTokenizer
        {
            private static readonly Regex TokenRegex = new(@"\b[a-zA-Z][a-zA-Z0-9]*\b", RegexOptions.Compiled);

            public List<string> Tokenize(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return new List<string>();

                var matches = TokenRegex.Matches(text);
                return matches.Select(m => m.Value.ToLowerInvariant()).ToList();
            }
        }
    }

    /// <summary>
    /// Options for categorization.
    /// </summary>
    public sealed class CategorizationOptions
    {
        /// <summary>Prefer AI over statistical methods.</summary>
        public bool PreferAI { get; init; } = true;

        /// <summary>Minimum confidence threshold.</summary>
        public float MinConfidence { get; init; } = 0.3f;

        /// <summary>Maximum categories to return.</summary>
        public int MaxCategories { get; init; } = 5;

        /// <summary>Include hierarchy path in results.</summary>
        public bool IncludeHierarchy { get; init; } = true;
    }
}
