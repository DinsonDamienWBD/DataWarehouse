using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Content classification feature strategy.
/// Automatically categorizes content using AI-powered classification.
/// </summary>
public sealed class ContentClassificationStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-content-classification";

    /// <inheritdoc/>
    public override string StrategyName => "Content Classification";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Content Classification",
        Description = "AI-powered automatic content categorization and tagging",
        Capabilities = IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Categories", Description = "Comma-separated list of categories", Required = false },
            new ConfigurationRequirement { Key = "ConfidenceThreshold", Description = "Minimum confidence for classification", Required = false, DefaultValue = "0.7" },
            new ConfigurationRequirement { Key = "MultiLabel", Description = "Allow multiple labels", Required = false, DefaultValue = "true" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "classification", "categorization", "tagging", "nlp" }
    };

    /// <summary>
    /// Classifies content into predefined or discovered categories.
    /// </summary>
    /// <param name="content">Content to classify.</param>
    /// <param name="categories">Optional list of target categories.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Classification result with categories and confidence scores.</returns>
    public async Task<ClassificationResult> ClassifyAsync(
        string content,
        string[]? categories = null,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured for classification");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var targetCategories = categories ?? GetConfig("Categories")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var threshold = float.Parse(GetConfig("ConfidenceThreshold") ?? "0.7");
            var multiLabel = bool.Parse(GetConfig("MultiLabel") ?? "true");

            string prompt;
            if (targetCategories?.Length > 0)
            {
                prompt = $@"Classify the following content into one or more of these categories: {string.Join(", ", targetCategories)}

Content:
{content}

Return your classification as JSON with format:
{{
  ""classifications"": [
    {{""category"": ""category_name"", ""confidence"": 0.95, ""reason"": ""brief explanation""}}
  ]
}}";
            }
            else
            {
                prompt = $@"Analyze the following content and suggest appropriate categories/tags.

Content:
{content}

Return your classification as JSON with format:
{{
  ""classifications"": [
    {{""category"": ""discovered_category"", ""confidence"": 0.95, ""reason"": ""brief explanation""}}
  ]
}}";
            }

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 500,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            // Parse response
            var classifications = ParseClassifications(response.Content, threshold, multiLabel);

            return new ClassificationResult
            {
                Content = content.Length > 100 ? content.Substring(0, 100) + "..." : content,
                Classifications = classifications,
                RawResponse = response.Content
            };
        });
    }

    /// <summary>
    /// Classifies multiple content items in batch.
    /// </summary>
    public async Task<IEnumerable<ClassificationResult>> ClassifyBatchAsync(
        IEnumerable<string> contents,
        string[]? categories = null,
        CancellationToken ct = default)
    {
        var results = new List<ClassificationResult>();
        foreach (var content in contents)
        {
            results.Add(await ClassifyAsync(content, categories, ct));
        }
        return results;
    }

    /// <summary>
    /// Trains a custom classifier using examples.
    /// </summary>
    public async Task<CustomClassifier> TrainClassifierAsync(
        IEnumerable<(string Content, string Category)> examples,
        CancellationToken ct = default)
    {
        if (VectorStore == null)
            throw new InvalidOperationException("Vector store required for training custom classifier");
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for training custom classifier");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var exampleList = examples.ToList();
            var classifierId = $"classifier-{Guid.NewGuid():N}";

            // Create embeddings for all examples
            foreach (var (content, category) in exampleList)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync(content, ct);
                await VectorStore.StoreAsync(
                    $"{classifierId}-{Guid.NewGuid():N}",
                    embedding,
                    new Dictionary<string, object>
                    {
                        ["category"] = category,
                        ["classifier_id"] = classifierId,
                        ["text"] = content
                    },
                    ct
                );
                RecordEmbeddings(1);
                RecordVectorsStored(1);
            }

            var categories = exampleList.Select(e => e.Category).Distinct().ToArray();

            return new CustomClassifier
            {
                ClassifierId = classifierId,
                Categories = categories,
                ExampleCount = exampleList.Count,
                CreatedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Classifies content using a trained custom classifier.
    /// </summary>
    public async Task<ClassificationResult> ClassifyWithCustomClassifierAsync(
        string classifierId,
        string content,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (VectorStore == null || AIProvider == null)
            throw new InvalidOperationException("Vector store and AI provider required");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var embedding = await AIProvider.GetEmbeddingsAsync(content, ct);
            RecordEmbeddings(1);

            var matches = await VectorStore.SearchAsync(
                embedding,
                topK,
                0.5f,
                new Dictionary<string, object> { ["classifier_id"] = classifierId },
                ct
            );
            RecordSearch();

            // Aggregate votes by category
            var votes = new Dictionary<string, (float TotalScore, int Count)>();
            foreach (var match in matches)
            {
                if (match.Entry.Metadata.TryGetValue("category", out var cat) && cat != null)
                {
                    var category = cat.ToString()!;
                    if (!votes.ContainsKey(category))
                        votes[category] = (0, 0);
                    var current = votes[category];
                    votes[category] = (current.TotalScore + match.Score, current.Count + 1);
                }
            }

            var classifications = votes
                .OrderByDescending(v => v.Value.TotalScore / v.Value.Count)
                .Select(v => new Classification
                {
                    Category = v.Key,
                    Confidence = v.Value.TotalScore / v.Value.Count,
                    Reason = $"Based on {v.Value.Count} similar examples"
                })
                .ToList();

            return new ClassificationResult
            {
                Content = content.Length > 100 ? content.Substring(0, 100) + "..." : content,
                Classifications = classifications
            };
        });
    }

    private static List<Classification> ParseClassifications(string response, float threshold, bool multiLabel)
    {
        var classifications = new List<Classification>();

        try
        {
            // Simple JSON extraction
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("classifications", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var category = item.GetProperty("category").GetString() ?? "";
                        var confidence = item.TryGetProperty("confidence", out var c) ? c.GetSingle() : 0.8f;
                        var reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null;

                        if (confidence >= threshold)
                        {
                            classifications.Add(new Classification
                            {
                                Category = category,
                                Confidence = confidence,
                                Reason = reason
                            });
                        }
                    }
                }
            }
        }
        catch
        {
            // If parsing fails, return empty list
        }

        if (!multiLabel && classifications.Count > 1)
        {
            classifications = classifications.OrderByDescending(c => c.Confidence).Take(1).ToList();
        }

        return classifications;
    }
}

/// <summary>
/// Result of content classification.
/// </summary>
public sealed class ClassificationResult
{
    /// <summary>Truncated content for reference.</summary>
    public string Content { get; init; } = "";

    /// <summary>List of classifications with confidence scores.</summary>
    public List<Classification> Classifications { get; init; } = new();

    /// <summary>Raw response from AI model.</summary>
    public string? RawResponse { get; init; }

    /// <summary>Primary classification (highest confidence).</summary>
    public Classification? PrimaryClassification => Classifications.OrderByDescending(c => c.Confidence).FirstOrDefault();
}

/// <summary>
/// Individual classification with confidence.
/// </summary>
public sealed class Classification
{
    /// <summary>Category name.</summary>
    public string Category { get; init; } = "";

    /// <summary>Confidence score (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Explanation for classification.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Custom trained classifier.
/// </summary>
public sealed class CustomClassifier
{
    /// <summary>Classifier identifier.</summary>
    public string ClassifierId { get; init; } = "";

    /// <summary>Available categories.</summary>
    public string[] Categories { get; init; } = Array.Empty<string>();

    /// <summary>Number of training examples.</summary>
    public int ExampleCount { get; init; }

    /// <summary>When the classifier was created.</summary>
    public DateTime CreatedAt { get; init; }
}
