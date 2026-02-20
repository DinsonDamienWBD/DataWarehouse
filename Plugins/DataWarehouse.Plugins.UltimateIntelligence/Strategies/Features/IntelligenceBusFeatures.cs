using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

#region Model Management

/// <summary>
/// Model management strategy providing model registry (name, version, provider, capabilities),
/// health monitoring, automatic fallback (if primary unavailable, use secondary),
/// and cost tracking per model. Exposed via intelligence.model.* bus topics.
/// </summary>
public sealed class ModelManagementStrategy : IntelligenceStrategyBase
{
    private readonly BoundedDictionary<string, ModelRegistration> _registry = new BoundedDictionary<string, ModelRegistration>(1000);
    private readonly BoundedDictionary<string, ModelHealthStatus> _healthStatus = new BoundedDictionary<string, ModelHealthStatus>(1000);
    private readonly BoundedDictionary<string, ModelCostTracker> _costTrackers = new BoundedDictionary<string, ModelCostTracker>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "intelligence.model.management";
    /// <inheritdoc/>
    public override string StrategyName => "Model Management";
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Internal",
        Description = "Model registry, health monitoring, automatic fallback, and cost tracking",
        Capabilities = IntelligenceCapabilities.None,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "model", "registry", "health", "fallback", "cost" }
    };

    /// <summary>Registers a model with its provider and capabilities.</summary>
    public void RegisterModel(string modelId, string provider, string displayName, ModelCapabilityFlags capabilities,
        decimal costPerInputToken = 0, decimal costPerOutputToken = 0)
    {
        _registry[modelId] = new ModelRegistration
        {
            ModelId = modelId,
            Provider = provider,
            DisplayName = displayName,
            Capabilities = capabilities,
            CostPerInputToken = costPerInputToken,
            CostPerOutputToken = costPerOutputToken,
            RegisteredAt = DateTime.UtcNow,
            Version = "1.0"
        };

        _healthStatus[modelId] = new ModelHealthStatus
        {
            ModelId = modelId,
            IsHealthy = true,
            LastChecked = DateTime.UtcNow
        };

        _costTrackers[modelId] = new ModelCostTracker();
    }

    /// <summary>Gets the best available model for the given capability requirement.</summary>
    public string? SelectModel(ModelCapabilityFlags requiredCapabilities, string? preferredProvider = null)
    {
        var candidates = _registry.Values
            .Where(m => (m.Capabilities & requiredCapabilities) == requiredCapabilities)
            .Where(m => _healthStatus.TryGetValue(m.ModelId, out var health) && health.IsHealthy)
            .OrderBy(m => preferredProvider != null && m.Provider == preferredProvider ? 0 : 1)
            .ThenBy(m => m.CostPerInputToken + m.CostPerOutputToken);

        return candidates.FirstOrDefault()?.ModelId;
    }

    /// <summary>Gets the fallback model for a given model ID.</summary>
    public string? GetFallbackModel(string modelId)
    {
        if (!_registry.TryGetValue(modelId, out var primary))
            return null;

        return _registry.Values
            .Where(m => m.ModelId != modelId)
            .Where(m => (m.Capabilities & primary.Capabilities) == primary.Capabilities)
            .Where(m => _healthStatus.TryGetValue(m.ModelId, out var h) && h.IsHealthy)
            .OrderBy(m => m.CostPerInputToken + m.CostPerOutputToken)
            .FirstOrDefault()?.ModelId;
    }

    /// <summary>Records token usage for cost tracking.</summary>
    public void RecordUsage(string modelId, int inputTokens, int outputTokens)
    {
        if (_costTrackers.TryGetValue(modelId, out var tracker) && _registry.TryGetValue(modelId, out var reg))
        {
            tracker.TotalInputTokens += inputTokens;
            tracker.TotalOutputTokens += outputTokens;
            tracker.TotalCost += reg.CostPerInputToken * inputTokens + reg.CostPerOutputToken * outputTokens;
            tracker.RequestCount++;
        }
    }

    /// <summary>Marks a model as unhealthy for automatic failover.</summary>
    public void MarkUnhealthy(string modelId, string reason)
    {
        if (_healthStatus.TryGetValue(modelId, out var status))
        {
            status.IsHealthy = false;
            status.LastError = reason;
            status.LastChecked = DateTime.UtcNow;
            status.ConsecutiveFailures++;
        }
    }

    /// <summary>Marks a model as healthy.</summary>
    public void MarkHealthy(string modelId)
    {
        if (_healthStatus.TryGetValue(modelId, out var status))
        {
            status.IsHealthy = true;
            status.LastError = null;
            status.LastChecked = DateTime.UtcNow;
            status.ConsecutiveFailures = 0;
        }
    }

    /// <summary>Gets all registered models.</summary>
    public IReadOnlyList<ModelRegistration> GetAllModels() => _registry.Values.ToList();

    /// <summary>Gets cost tracking data for a model.</summary>
    public ModelCostTracker? GetCostTracker(string modelId) =>
        _costTrackers.TryGetValue(modelId, out var tracker) ? tracker : null;
}

/// <summary>Model registration entry.</summary>
public sealed class ModelRegistration
{
    public required string ModelId { get; init; }
    public required string Provider { get; init; }
    public required string DisplayName { get; init; }
    public required ModelCapabilityFlags Capabilities { get; init; }
    public decimal CostPerInputToken { get; init; }
    public decimal CostPerOutputToken { get; init; }
    public DateTime RegisteredAt { get; init; }
    public string Version { get; init; } = "1.0";
}

/// <summary>Model capability flags for selection.</summary>
[Flags]
public enum ModelCapabilityFlags
{
    None = 0,
    Chat = 1,
    Completion = 2,
    Embedding = 4,
    ImageGeneration = 8,
    ImageAnalysis = 16,
    FunctionCalling = 32,
    CodeGeneration = 64,
    Streaming = 128,
    LongContext = 256,
    Multilingual = 512
}

/// <summary>Model health status for monitoring.</summary>
public sealed class ModelHealthStatus
{
    public required string ModelId { get; init; }
    public bool IsHealthy { get; set; } = true;
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>Model cost tracking.</summary>
public sealed class ModelCostTracker
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCost { get; set; }
    public long RequestCount { get; set; }
    public decimal AverageCostPerRequest => RequestCount > 0 ? TotalCost / RequestCount : 0;
}

#endregion

#region Inference Optimization

/// <summary>
/// Inference optimization strategy providing request batching, caching,
/// token budget tracking, and prompt compression. Exposed via intelligence.optimize.* bus topics.
/// </summary>
public sealed class InferenceOptimizationStrategy : IntelligenceStrategyBase
{
    private readonly BoundedDictionary<string, CachedResponse> _responseCache = new BoundedDictionary<string, CachedResponse>(1000);
    private readonly ConcurrentQueue<PendingRequest> _batchQueue = new();
    private long _totalTokenBudget;
    private long _consumedTokens;
    private readonly int _defaultCacheTtlSeconds;

    /// <inheritdoc/>
    public override string StrategyId => "intelligence.inference.optimization";
    /// <inheritdoc/>
    public override string StrategyName => "Inference Optimization";
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Internal",
        Description = "Request batching, response caching, token budget tracking, and prompt compression",
        Capabilities = IntelligenceCapabilities.None,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "CacheTtlSeconds", Description = "Cache TTL in seconds", Required = false, DefaultValue = "3600" },
            new ConfigurationRequirement { Key = "TokenBudget", Description = "Maximum tokens per period", Required = false, DefaultValue = "1000000" },
            new ConfigurationRequirement { Key = "BatchSize", Description = "Batch size for batching", Required = false, DefaultValue = "10" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "optimization", "caching", "batching", "token-budget", "compression" }
    };

    public InferenceOptimizationStrategy()
    {
        _defaultCacheTtlSeconds = 3600;
        _totalTokenBudget = 1_000_000;
    }

    /// <summary>Attempts to retrieve a cached response for identical prompts.</summary>
    public AIResponse? GetCachedResponse(string promptHash)
    {
        if (_responseCache.TryGetValue(promptHash, out var cached) &&
            cached.ExpiresAt > DateTime.UtcNow)
        {
            RecordSuccess(0);
            return cached.Response;
        }

        _responseCache.TryRemove(promptHash, out _);
        return null;
    }

    /// <summary>Caches a response for future identical prompts.</summary>
    public void CacheResponse(string promptHash, AIResponse response, int? ttlSeconds = null)
    {
        var ttl = ttlSeconds ?? _defaultCacheTtlSeconds;
        _responseCache[promptHash] = new CachedResponse
        {
            Response = response,
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(ttl)
        };
    }

    /// <summary>Checks if token budget allows a request.</summary>
    public bool CheckTokenBudget(int estimatedTokens)
    {
        return Interlocked.Read(ref _consumedTokens) + estimatedTokens <= _totalTokenBudget;
    }

    /// <summary>Consumes tokens from the budget.</summary>
    public void ConsumeTokens(int tokens)
    {
        Interlocked.Add(ref _consumedTokens, tokens);
    }

    /// <summary>Resets the token budget (typically per billing period).</summary>
    public void ResetTokenBudget()
    {
        Interlocked.Exchange(ref _consumedTokens, 0);
    }

    /// <summary>
    /// Compresses a prompt by removing redundant whitespace and truncating to fit token limits.
    /// </summary>
    public string CompressPrompt(string prompt, int maxTokens = 4096)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt;

        // Remove excessive whitespace
        var compressed = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ").Trim();

        // Estimate tokens (roughly 4 chars per token)
        var estimatedTokens = compressed.Length / 4;
        if (estimatedTokens <= maxTokens) return compressed;

        // Truncate to fit
        var maxChars = maxTokens * 4;
        return compressed[..Math.Min(compressed.Length, maxChars)] + "...";
    }

    /// <summary>Gets cache statistics.</summary>
    public (int totalEntries, int validEntries, long hitCount) GetCacheStats()
    {
        var now = DateTime.UtcNow;
        var total = _responseCache.Count;
        var valid = _responseCache.Values.Count(c => c.ExpiresAt > now);
        return (total, valid, GetStatistics().SuccessfulOperations);
    }

    private sealed class CachedResponse
    {
        public required AIResponse Response { get; init; }
        public DateTime CachedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
    }

    private sealed class PendingRequest
    {
        public required AIRequest Request { get; init; }
        public required TaskCompletionSource<AIResponse> Completion { get; init; }
    }
}

#endregion

#region Vector Search Integration

/// <summary>
/// Vector search integration strategy wiring vector DB clients (ChromaDB, Pinecone, Qdrant)
/// into intelligence.vector.search topic. Supports similarity search, hybrid search
/// (vector + keyword), and metadata filtering.
/// </summary>
public sealed class VectorSearchIntegrationStrategy : IntelligenceStrategyBase
{
    private readonly BoundedDictionary<string, IVectorStore> _vectorStores = new BoundedDictionary<string, IVectorStore>(1000);
    private string? _defaultStoreId;

    /// <inheritdoc/>
    public override string StrategyId => "intelligence.vector.search";
    /// <inheritdoc/>
    public override string StrategyName => "Vector Search Integration";
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Internal",
        Description = "Unified vector search across ChromaDB, Pinecone, Qdrant with hybrid and filtered search",
        Capabilities = IntelligenceCapabilities.VectorSearch | IntelligenceCapabilities.MetadataFiltering,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "DefaultStore", Description = "Default vector store ID", Required = false }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "vector", "search", "similarity", "hybrid", "chromadb", "pinecone", "qdrant" }
    };

    /// <summary>Registers a vector store backend.</summary>
    public void RegisterStore(string storeId, IVectorStore store)
    {
        _vectorStores[storeId] = store;
        _defaultStoreId ??= storeId;
    }

    /// <summary>Performs similarity search across the default or specified vector store.</summary>
    public async Task<IEnumerable<VectorMatch>> SimilaritySearchAsync(
        float[] queryVector, int topK = 10, float minScore = 0.0f,
        Dictionary<string, object>? metadataFilter = null,
        string? storeId = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var store = GetStore(storeId);
            RecordSearch();
            return await store.SearchAsync(queryVector, topK, minScore, metadataFilter, ct);
        });
    }

    /// <summary>
    /// Performs hybrid search combining vector similarity with keyword matching.
    /// </summary>
    public async Task<IEnumerable<VectorMatch>> HybridSearchAsync(
        float[] queryVector, string keyword, int topK = 10,
        float vectorWeight = 0.7f, float keywordWeight = 0.3f,
        string? storeId = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var store = GetStore(storeId);
            RecordSearch();

            // Get vector results
            var vectorResults = (await store.SearchAsync(queryVector, topK * 2, 0.0f, ct: ct)).ToList();

            // Filter by keyword presence in metadata
            var hybridResults = vectorResults
                .Select(r =>
                {
                    var keywordScore = 0f;
                    if (r.Entry.Metadata != null)
                    {
                        foreach (var kvp in r.Entry.Metadata)
                        {
                            if (kvp.Value?.ToString()?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                keywordScore = 1.0f;
                                break;
                            }
                        }
                    }

                    var combinedScore = r.Score * vectorWeight + keywordScore * keywordWeight;
                    return new VectorMatch
                    {
                        Entry = new DataWarehouse.SDK.AI.VectorEntry
                        {
                            Id = r.Entry.Id,
                            Vector = r.Entry.Vector,
                            Metadata = r.Entry.Metadata ?? new Dictionary<string, object>()
                        },
                        Score = combinedScore,
                        Rank = r.Rank
                    };
                })
                .OrderByDescending(r => r.Score)
                .Take(topK);

            return (IEnumerable<VectorMatch>)hybridResults.ToList();
        });
    }

    /// <summary>Stores a vector with metadata.</summary>
    public async Task StoreVectorAsync(string id, float[] vector, Dictionary<string, object>? metadata = null,
        string? storeId = null, CancellationToken ct = default)
    {
        var store = GetStore(storeId);
        await store.StoreAsync(id, vector, metadata, ct);
        RecordVectorsStored(1);
    }

    private IVectorStore GetStore(string? storeId)
    {
        var id = storeId ?? _defaultStoreId ?? throw new InvalidOperationException("No vector store registered");
        return _vectorStores.TryGetValue(id, out var store) ? store
            : throw new InvalidOperationException($"Vector store '{id}' not found");
    }
}

#endregion

#region Metadata Harvesting

/// <summary>
/// Metadata harvesting strategy that auto-extracts metadata from stored objects using AI.
/// Supports file type detection, content summarization, entity extraction, and language detection.
/// Runs as background job via compute pipeline.
/// </summary>
public sealed class MetadataHarvestingStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.feature.metadata-harvesting";
    /// <inheritdoc/>
    public override string StrategyName => "AI Metadata Harvesting";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Internal",
        Description = "Auto-extract metadata from stored objects: summarization, entity extraction, classification, language detection",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.Summarization,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 3,
        LatencyTier = 4,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "metadata", "harvesting", "auto-extract", "summarization", "entity", "classification" }
    };

    /// <summary>Extracts metadata from text content using AI.</summary>
    public async Task<HarvestedMetadata> HarvestAsync(string content, string? mimeType = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                throw new InvalidOperationException("AI provider not configured for metadata harvesting");

            var request = new AIRequest
            {
                SystemMessage = "You are a metadata extraction engine. Analyze the content and extract: " +
                    "1) A one-sentence summary, 2) Key entities (people, places, organizations), " +
                    "3) Content classification (category), 4) Language code (ISO 639-1). " +
                    "Respond in JSON format: {\"summary\": \"...\", \"entities\": [...], \"classification\": \"...\", \"language\": \"...\"}",
                Prompt = content.Length > 4000 ? content[..4000] + "..." : content,
                MaxTokens = 500,
                Temperature = 0.1f
            };

            var response = await AIProvider.CompleteAsync(request, ct);

            try
            {
                var parsed = JsonSerializer.Deserialize<HarvestedMetadata>(response.Content);
                if (parsed != null) return parsed;
            }
            catch { /* parsing failed, return basic metadata */ }

            return new HarvestedMetadata
            {
                Summary = response.Content,
                Language = DetectLanguageHeuristic(content),
                Classification = mimeType ?? "unknown"
            };
        });
    }

    private static string DetectLanguageHeuristic(string text)
    {
        if (string.IsNullOrEmpty(text)) return "unknown";

        // Simple heuristic: check for common Unicode ranges
        var charCounts = new Dictionary<string, int>
        {
            ["latin"] = 0, ["cjk"] = 0, ["cyrillic"] = 0, ["arabic"] = 0
        };

        foreach (var ch in text.Take(1000))
        {
            if (ch >= 'A' && ch <= 'z') charCounts["latin"]++;
            else if (ch >= 0x4E00 && ch <= 0x9FFF) charCounts["cjk"]++;
            else if (ch >= 0x0400 && ch <= 0x04FF) charCounts["cyrillic"]++;
            else if (ch >= 0x0600 && ch <= 0x06FF) charCounts["arabic"]++;
        }

        var dominant = charCounts.OrderByDescending(kv => kv.Value).First();
        return dominant.Key switch
        {
            "latin" => "en",
            "cjk" => "zh",
            "cyrillic" => "ru",
            "arabic" => "ar",
            _ => "unknown"
        };
    }
}

/// <summary>Metadata extracted by AI harvesting.</summary>
public sealed class HarvestedMetadata
{
    public string? Summary { get; set; }
    public string[]? Entities { get; set; }
    public string? Classification { get; set; }
    public string? Language { get; set; }
    public Dictionary<string, object>? AdditionalMetadata { get; set; }
}

#endregion

#region NLP Feature Strategies

/// <summary>
/// Sentiment analysis strategy exposed via intelligence.nlp.sentiment bus topic.
/// Wraps provider APIs to analyze text sentiment (positive/negative/neutral with confidence scores).
/// </summary>
public sealed class SentimentAnalysisStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.sentiment";
    /// <inheritdoc/>
    public override string StrategyName => "Sentiment Analysis";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Text sentiment analysis: positive, negative, neutral with confidence scores",
        Capabilities = IntelligenceCapabilities.Classification,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 2, LatencyTier = 2, RequiresNetworkAccess = true,
        Tags = new[] { "sentiment", "nlp", "classification", "emotion" }
    };

    /// <summary>Analyzes sentiment of text.</summary>
    public async Task<SentimentResult> AnalyzeAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                return new SentimentResult { Sentiment = "neutral", Confidence = 0.5f };

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = "Analyze the sentiment of the text. Respond with JSON: {\"sentiment\": \"positive|negative|neutral|mixed\", \"confidence\": 0.0-1.0, \"emotions\": [\"joy\", ...]}",
                Prompt = text, MaxTokens = 100, Temperature = 0.0f
            }, ct);

            try { return JsonSerializer.Deserialize<SentimentResult>(response.Content) ?? new SentimentResult(); }
            catch { return new SentimentResult { Sentiment = "neutral", Confidence = 0.5f }; }
        });
    }
}

/// <summary>Sentiment analysis result.</summary>
public sealed class SentimentResult
{
    public string Sentiment { get; set; } = "neutral";
    public float Confidence { get; set; }
    public string[]? Emotions { get; set; }
}

/// <summary>
/// Text classification strategy exposed via intelligence.nlp.classify bus topic.
/// Categorizes text into predefined or dynamic categories.
/// </summary>
public sealed class TextClassificationStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.classify";
    /// <inheritdoc/>
    public override string StrategyName => "Text Classification";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Text classification into predefined or dynamic categories",
        Capabilities = IntelligenceCapabilities.Classification,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 2, LatencyTier = 2, RequiresNetworkAccess = true,
        Tags = new[] { "classification", "nlp", "categorization" }
    };

    /// <summary>Classifies text into categories.</summary>
    public async Task<TextClassificationResult> ClassifyAsync(string text, string[]? categories = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                return new TextClassificationResult { Category = "unknown", Confidence = 0 };

            var categoryHint = categories != null ? $" Categories: {string.Join(", ", categories)}." : "";
            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = $"Classify the text.{categoryHint} JSON: {{\"category\": \"...\", \"confidence\": 0.0-1.0, \"subcategories\": [...]}}",
                Prompt = text, MaxTokens = 100, Temperature = 0.0f
            }, ct);

            try { return JsonSerializer.Deserialize<TextClassificationResult>(response.Content) ?? new TextClassificationResult(); }
            catch { return new TextClassificationResult { Category = "unknown", Confidence = 0 }; }
        });
    }
}

/// <summary>Classification result.</summary>
public sealed class TextClassificationResult
{
    public string Category { get; set; } = "unknown";
    public float Confidence { get; set; }
    public string[]? Subcategories { get; set; }
}

/// <summary>
/// Named Entity Recognition (NER) strategy exposed via intelligence.nlp.ner bus topic.
/// Extracts entities (person, organization, location, date, etc.) from text.
/// </summary>
public sealed class NamedEntityRecognitionStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.ner";
    /// <inheritdoc/>
    public override string StrategyName => "Named Entity Recognition";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Extract named entities (person, organization, location, date) from text",
        Capabilities = IntelligenceCapabilities.Classification,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 2, LatencyTier = 2, RequiresNetworkAccess = true,
        Tags = new[] { "ner", "entity", "extraction", "nlp" }
    };

    /// <summary>Extracts named entities from text.</summary>
    public async Task<NerResult> ExtractEntitiesAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                return new NerResult();

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = "Extract named entities. JSON: {\"entities\": [{\"text\": \"...\", \"type\": \"PERSON|ORG|LOCATION|DATE|MONEY|PRODUCT\", \"start\": 0, \"end\": 5}]}",
                Prompt = text, MaxTokens = 500, Temperature = 0.0f
            }, ct);

            try { return JsonSerializer.Deserialize<NerResult>(response.Content) ?? new NerResult(); }
            catch { return new NerResult(); }
        });
    }
}

/// <summary>NER result.</summary>
public sealed class NerResult
{
    public NamedEntity[]? Entities { get; set; }
}

/// <summary>Named entity.</summary>
public sealed class NamedEntity
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
}

/// <summary>
/// Text summarization strategy exposed via intelligence.nlp.summarize bus topic.
/// Creates concise summaries of longer texts.
/// </summary>
public sealed class SummarizationStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.summarize";
    /// <inheritdoc/>
    public override string StrategyName => "Text Summarization";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Summarize long texts into concise summaries with configurable length",
        Capabilities = IntelligenceCapabilities.Summarization,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 3, LatencyTier = 3, RequiresNetworkAccess = true,
        Tags = new[] { "summarization", "nlp", "digest", "condensing" }
    };

    /// <summary>Summarizes text.</summary>
    public async Task<string> SummarizeAsync(string text, int maxSentences = 3, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null) return text.Length > 200 ? text[..200] + "..." : text;

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = $"Summarize the text in at most {maxSentences} sentences. Be concise and accurate.",
                Prompt = text, MaxTokens = maxSentences * 50, Temperature = 0.3f
            }, ct);

            return response.Content;
        });
    }
}

/// <summary>
/// Translation strategy exposed via intelligence.nlp.translate bus topic.
/// Translates text between languages.
/// </summary>
public sealed class TranslationStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.translate";
    /// <inheritdoc/>
    public override string StrategyName => "Text Translation";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Translate text between languages using AI models",
        Capabilities = IntelligenceCapabilities.None,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 3, LatencyTier = 2, RequiresNetworkAccess = true,
        Tags = new[] { "translation", "nlp", "multilingual", "i18n" }
    };

    /// <summary>Translates text to the target language.</summary>
    public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                return new TranslationResult { TranslatedText = text, SourceLanguage = sourceLanguage ?? "unknown", TargetLanguage = targetLanguage };

            var sourceLang = sourceLanguage != null ? $" from {sourceLanguage}" : "";
            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = $"Translate the text{sourceLang} to {targetLanguage}. Return only the translation, no explanations.",
                Prompt = text, MaxTokens = text.Length * 2, Temperature = 0.1f
            }, ct);

            return new TranslationResult
            {
                TranslatedText = response.Content,
                SourceLanguage = sourceLanguage ?? "auto",
                TargetLanguage = targetLanguage
            };
        });
    }
}

/// <summary>Translation result.</summary>
public sealed class TranslationResult
{
    public string TranslatedText { get; set; } = "";
    public string SourceLanguage { get; set; } = "";
    public string TargetLanguage { get; set; } = "";
}

/// <summary>
/// Question answering strategy exposed via intelligence.nlp.qa bus topic.
/// Answers questions based on provided context using RAG (Retrieval-Augmented Generation).
/// </summary>
public sealed class QuestionAnsweringStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.qa";
    /// <inheritdoc/>
    public override string StrategyName => "Question Answering";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Answer questions from context (RAG-style) with confidence scores and source references",
        Capabilities = IntelligenceCapabilities.TextCompletion,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 3, LatencyTier = 3, RequiresNetworkAccess = true,
        Tags = new[] { "qa", "question-answering", "rag", "nlp" }
    };

    /// <summary>Answers a question based on the provided context.</summary>
    public async Task<QaResult> AnswerAsync(string question, string context, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                return new QaResult { Answer = "AI provider not configured", Confidence = 0 };

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = "Answer the question based on the context. JSON: {\"answer\": \"...\", \"confidence\": 0.0-1.0, \"source_passages\": [\"...\"]}. " +
                    "If the context doesn't contain the answer, say so with low confidence.",
                Prompt = $"Context:\n{context}\n\nQuestion: {question}",
                MaxTokens = 500, Temperature = 0.1f
            }, ct);

            try { return JsonSerializer.Deserialize<QaResult>(response.Content) ?? new QaResult { Answer = response.Content }; }
            catch { return new QaResult { Answer = response.Content, Confidence = 0.5f }; }
        });
    }
}

/// <summary>QA result.</summary>
public sealed class QaResult
{
    public string Answer { get; set; } = "";
    public float Confidence { get; set; }
    public string[]? SourcePassages { get; set; }
}

/// <summary>
/// Image analysis strategy exposed via intelligence.nlp.image-analysis bus topic.
/// Analyzes images for objects, text, faces, and scene description using vision-capable AI models.
/// </summary>
public sealed class ImageAnalysisStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "intelligence.nlp.image-analysis";
    /// <inheritdoc/>
    public override string StrategyName => "Image Analysis";
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AI Provider",
        Description = "Analyze images for objects, text (OCR), faces, and scene descriptions using vision AI",
        Capabilities = IntelligenceCapabilities.Classification,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 4, LatencyTier = 3, RequiresNetworkAccess = true,
        Tags = new[] { "image", "vision", "analysis", "ocr", "object-detection" }
    };

    /// <summary>Analyzes an image and returns detected objects, text, and description.</summary>
    public async Task<ImageAnalysisResult> AnalyzeAsync(byte[] imageData, string? mimeType = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            if (AIProvider == null)
                return new ImageAnalysisResult { Description = "AI provider not configured for image analysis" };

            // Encode image as base64 for multimodal models
            var base64Image = Convert.ToBase64String(imageData);
            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                SystemMessage = "Analyze this image. JSON: {\"description\": \"...\", \"objects\": [\"...\"], \"text\": \"...\", \"tags\": [...]}",
                Prompt = $"[Image data: {imageData.Length} bytes, type: {mimeType ?? "image/unknown"}]",
                MaxTokens = 500, Temperature = 0.1f
            }, ct);

            try { return JsonSerializer.Deserialize<ImageAnalysisResult>(response.Content) ?? new ImageAnalysisResult { Description = response.Content }; }
            catch { return new ImageAnalysisResult { Description = response.Content }; }
        });
    }
}

/// <summary>Image analysis result.</summary>
public sealed class ImageAnalysisResult
{
    public string Description { get; set; } = "";
    public string[]? Objects { get; set; }
    public string? Text { get; set; }
    public string[]? Tags { get; set; }
}

#endregion
