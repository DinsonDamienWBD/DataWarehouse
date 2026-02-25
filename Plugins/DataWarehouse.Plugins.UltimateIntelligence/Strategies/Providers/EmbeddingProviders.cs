using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.AI;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

#region Cohere Embedding Provider

/// <summary>
/// Cohere embedding provider strategy for text-embedding via Cohere's embed API.
/// Supports embed-english-v3.0, embed-multilingual-v3.0, and legacy models.
/// </summary>
public sealed class CohereEmbeddingProvider : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.cohere.ai/v1";
    private const string DefaultModel = "embed-english-v3.0";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-cohere-embedding";
    /// <inheritdoc/>
    public override string StrategyName => "Cohere Embedding Provider";
    /// <inheritdoc/>
    public override string ProviderId => "cohere";
    /// <inheritdoc/>
    public override string DisplayName => "Cohere Embed";

    /// <inheritdoc/>
    public override AICapabilities Capabilities => AICapabilities.Embeddings;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Cohere",
        Description = "Cohere embedding models for semantic text representation with multilingual support",
        Capabilities = IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Cohere API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Embedding model", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "InputType", Description = "Input type: search_document, search_query, classification, clustering", Required = false, DefaultValue = "search_document" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "cohere", "embedding", "multilingual", "semantic" }
    };

    private static readonly HttpClient SharedHttpClient = new();
    public CohereEmbeddingProvider() : this(SharedHttpClient) { }
    public CohereEmbeddingProvider(HttpClient httpClient) { _httpClient = httpClient; }

    /// <inheritdoc/>
    public override Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Cohere embedding provider does not support chat completion. Use GetEmbeddingsAsync.");

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        throw new NotSupportedException("Cohere embedding provider does not support streaming.");
#pragma warning disable CS0162 // Unreachable code to satisfy IAsyncEnumerable
        yield break;
#pragma warning restore CS0162
    }

    /// <inheritdoc/>
    public override async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var model = GetConfig("Model") ?? DefaultModel;
            var inputType = GetConfig("InputType") ?? "search_document";

            var payload = new
            {
                texts = new[] { text },
                model,
                input_type = inputType,
                truncate = "END"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{DefaultApiBase}/embed");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CohereEmbedResponse>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result?.Embeddings?.FirstOrDefault() ?? Array.Empty<float>();
        });
    }

    /// <inheritdoc/>
    public override async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
    {
        var apiKey = GetRequiredConfig("ApiKey");
        var model = GetConfig("Model") ?? DefaultModel;
        var inputType = GetConfig("InputType") ?? "search_document";

        var payload = new { texts, model, input_type = inputType, truncate = "END" };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DefaultApiBase}/embed");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CohereEmbedResponse>(cancellationToken: ct);
        RecordEmbeddings(texts.Length);

        return result?.Embeddings ?? Array.Empty<float[]>();
    }

    private sealed class CohereEmbedResponse
    {
        public float[][]? Embeddings { get; set; }
    }
}

#endregion

#region HuggingFace Embedding Provider (Sentence Transformers)

/// <summary>
/// HuggingFace Inference API embedding provider for sentence-transformers models.
/// Supports all-MiniLM-L6-v2, all-mpnet-base-v2, and custom models.
/// </summary>
public sealed class HuggingFaceEmbeddingProvider : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api-inference.huggingface.co/pipeline/feature-extraction";
    private const string DefaultModel = "sentence-transformers/all-MiniLM-L6-v2";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-huggingface-embedding";
    /// <inheritdoc/>
    public override string StrategyName => "HuggingFace Embedding Provider";
    /// <inheritdoc/>
    public override string ProviderId => "huggingface-embed";
    /// <inheritdoc/>
    public override string DisplayName => "HuggingFace Sentence Transformers";

    /// <inheritdoc/>
    public override AICapabilities Capabilities => AICapabilities.Embeddings;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "HuggingFace",
        Description = "HuggingFace sentence-transformers for text embeddings via Inference API",
        Capabilities = IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "HuggingFace API token", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Model ID", Required = false, DefaultValue = DefaultModel }
        },
        CostTier = 1,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "huggingface", "sentence-transformers", "embedding", "open-source" }
    };

    private static readonly HttpClient SharedHttpClient = new();
    public HuggingFaceEmbeddingProvider() : this(SharedHttpClient) { }
    public HuggingFaceEmbeddingProvider(HttpClient httpClient) { _httpClient = httpClient; }

    /// <inheritdoc/>
    public override Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("HuggingFace embedding provider does not support chat completion.");

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        throw new NotSupportedException("HuggingFace embedding provider does not support streaming.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <inheritdoc/>
    public override async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var model = GetConfig("Model") ?? DefaultModel;

            var payload = new { inputs = text, options = new { wait_for_model = true } };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{DefaultApiBase}/{model}");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<float[]>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result ?? Array.Empty<float>();
        });
    }
}

#endregion

#region Normalized Embedding Interface

/// <summary>
/// Unified embedding provider that normalizes embedding generation across providers.
/// Common interface: GenerateEmbedding(text) -> float[].
/// Supports provider selection, automatic fallback, and batch operations.
/// </summary>
public sealed class UnifiedEmbeddingProvider : IntelligenceStrategyBase
{
    private readonly List<AIProviderStrategyBase> _providers = new();
    private AIProviderStrategyBase? _primaryProvider;

    /// <inheritdoc/>
    public override string StrategyId => "intelligence.embedding.unified";
    /// <inheritdoc/>
    public override string StrategyName => "Unified Embedding Provider";
    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;
    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Unified",
        Description = "Unified embedding provider normalizing across OpenAI, Cohere, and HuggingFace",
        Capabilities = IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "PrimaryProvider", Description = "Primary embedding provider ID", Required = false, DefaultValue = "provider-openai" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "embedding", "unified", "normalized", "multi-provider" }
    };

    /// <summary>Registers an embedding-capable provider.</summary>
    public void RegisterProvider(AIProviderStrategyBase provider)
    {
        _providers.Add(provider);
        if (_primaryProvider == null || provider.StrategyId == GetConfig("PrimaryProvider"))
            _primaryProvider = provider;
    }

    /// <summary>Generates embeddings using the primary provider with automatic fallback.</summary>
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            // Try primary provider first
            if (_primaryProvider != null)
            {
                try
                {
                    return await _primaryProvider.GetEmbeddingsAsync(text, ct);
                }
                catch when (_providers.Count > 1)
                {
                    // Fallback to other providers
                }
            }

            // Fallback chain
            foreach (var provider in _providers.Where(p => p != _primaryProvider))
            {
                try
                {
                    return await provider.GetEmbeddingsAsync(text, ct);
                }
                catch { continue; }
            }

            throw new InvalidOperationException("All embedding providers failed");
        });
    }

    /// <summary>Generates batch embeddings with provider-specific batch optimization.</summary>
    public async Task<float[][]> GenerateEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
    {
        if (_primaryProvider != null)
            return await _primaryProvider.GetEmbeddingsBatchAsync(texts, ct);

        // Sequential fallback
        var results = new float[texts.Length][];
        for (int i = 0; i < texts.Length; i++)
            results[i] = await GenerateEmbeddingAsync(texts[i], ct);
        return results;
    }
}

#endregion
