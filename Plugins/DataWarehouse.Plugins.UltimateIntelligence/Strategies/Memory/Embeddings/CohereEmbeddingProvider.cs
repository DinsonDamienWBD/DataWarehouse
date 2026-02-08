using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Cohere embedding provider supporting embed-english-v3.0 and embed-multilingual-v3.0 models.
///
/// Features:
/// - Multiple input types: search_document, search_query, classification, clustering
/// - Batch processing up to 96 texts per request
/// - English and multilingual model support
/// - Truncation options (START, END, NONE)
/// - Retry logic with exponential backoff
/// - Rate limiting awareness
/// </summary>
public sealed class CohereEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultApiBase = "https://api.cohere.ai/v1";
    private const string DefaultModel = "embed-english-v3.0";
    private const int MaxBatchSize = 96;

    private static readonly IReadOnlyList<EmbeddingModelInfo> Models = new[]
    {
        new EmbeddingModelInfo
        {
            ModelId = "embed-english-v3.0",
            DisplayName = "Embed English v3.0",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0.0001m,
            IsDefault = true,
            Features = new[] { "english", "high-performance", "search-optimized" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "embed-multilingual-v3.0",
            DisplayName = "Embed Multilingual v3.0",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0.0001m,
            Features = new[] { "multilingual", "100+ languages", "search-optimized" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "embed-english-light-v3.0",
            DisplayName = "Embed English Light v3.0",
            Dimensions = 384,
            MaxTokens = 512,
            CostPer1KTokens = 0.0001m,
            Features = new[] { "english", "lightweight", "fast" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "embed-multilingual-light-v3.0",
            DisplayName = "Embed Multilingual Light v3.0",
            Dimensions = 384,
            MaxTokens = 512,
            CostPer1KTokens = 0.0001m,
            Features = new[] { "multilingual", "lightweight", "fast" }
        }
    };

    private string _currentModel;
    private readonly string _apiBase;
    private CohereInputType _inputType;
    private CohereTruncate _truncate;

    /// <inheritdoc/>
    public override string ProviderId => "cohere";

    /// <inheritdoc/>
    public override string DisplayName => "Cohere Embeddings";

    /// <inheritdoc/>
    public override int VectorDimensions => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.Dimensions ?? 1024;

    /// <inheritdoc/>
    public override int MaxTokens => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.MaxTokens ?? 512;

    /// <inheritdoc/>
    public override bool SupportsMultipleTexts => true;

    /// <inheritdoc/>
    public override IReadOnlyList<EmbeddingModelInfo> AvailableModels => Models;

    /// <inheritdoc/>
    public override string CurrentModel
    {
        get => _currentModel;
        set
        {
            if (!Models.Any(m => m.ModelId == value))
                throw new ArgumentException($"Unknown model: {value}");
            _currentModel = value;
        }
    }

    /// <summary>
    /// Gets or sets the input type for embedding generation.
    /// Use search_document for documents to be stored, search_query for queries.
    /// </summary>
    public CohereInputType InputType
    {
        get => _inputType;
        set => _inputType = value;
    }

    /// <summary>
    /// Gets or sets the truncation behavior for texts exceeding the token limit.
    /// </summary>
    public CohereTruncate Truncate
    {
        get => _truncate;
        set => _truncate = value;
    }

    /// <summary>
    /// Creates a new Cohere embedding provider.
    /// </summary>
    /// <param name="config">Provider configuration with ApiKey required.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public CohereEmbeddingProvider(EmbeddingProviderConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("ApiKey is required for Cohere provider");

        _currentModel = config.Model ?? DefaultModel;
        _apiBase = config.Endpoint ?? DefaultApiBase;
        _inputType = CohereInputType.SearchDocument;
        _truncate = CohereTruncate.End;

        if (config.AdditionalConfig.TryGetValue("InputType", out var inputType) && inputType is string it)
        {
            _inputType = Enum.TryParse<CohereInputType>(it, true, out var parsed) ? parsed : CohereInputType.SearchDocument;
        }

        if (config.AdditionalConfig.TryGetValue("Truncate", out var truncate) && truncate is string tr)
        {
            _truncate = Enum.TryParse<CohereTruncate>(tr, true, out var parsed) ? parsed : CohereTruncate.End;
        }
    }

    /// <inheritdoc/>
    protected override async Task<float[]> GetEmbeddingCoreAsync(string text, CancellationToken ct)
    {
        var results = await GetEmbeddingsBatchCoreAsync(new[] { text }, ct);
        return results[0];
    }

    /// <inheritdoc/>
    protected override async Task<float[][]> GetEmbeddingsBatchCoreAsync(string[] texts, CancellationToken ct)
    {
        if (texts.Length > MaxBatchSize)
        {
            var allResults = new List<float[]>();
            for (int i = 0; i < texts.Length; i += MaxBatchSize)
            {
                var chunk = texts.Skip(i).Take(MaxBatchSize).ToArray();
                var chunkResults = await ProcessBatchAsync(chunk, ct);
                allResults.AddRange(chunkResults);
            }
            return allResults.ToArray();
        }

        return await ProcessBatchAsync(texts, ct);
    }

    private async Task<float[][]> ProcessBatchAsync(string[] texts, CancellationToken ct)
    {
        var payload = new CohereEmbeddingRequest
        {
            Texts = texts.ToList(),
            Model = _currentModel,
            InputType = GetInputTypeString(_inputType),
            Truncate = GetTruncateString(_truncate)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/embed");
        request.Headers.Add("Authorization", $"Bearer {Config.ApiKey}");
        request.Headers.Add("Accept", "application/json");

        request.Content = JsonContent.Create(payload, options: new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        using var response = await HttpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(ct);
            var statusCode = (int)response.StatusCode;
            var isTransient = statusCode >= 500 || statusCode == 429;

            RateLimitInfo? rateLimitInfo = null;
            if (statusCode == 429)
            {
                rateLimitInfo = ParseRateLimitHeaders(response);
            }

            throw new EmbeddingException($"Cohere API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode,
                RateLimitInfo = rateLimitInfo
            };
        }

        var result = await response.Content.ReadFromJsonAsync<CohereEmbeddingResponse>(cancellationToken: ct);
        if (result?.Embeddings == null || result.Embeddings.Count == 0)
            throw new EmbeddingException("Empty response from Cohere API") { ProviderId = ProviderId };

        return result.Embeddings.ToArray();
    }

    /// <inheritdoc/>
    public override async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await GetEmbeddingAsync("test", ct);
            return result.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetInputTypeString(CohereInputType inputType) => inputType switch
    {
        CohereInputType.SearchDocument => "search_document",
        CohereInputType.SearchQuery => "search_query",
        CohereInputType.Classification => "classification",
        CohereInputType.Clustering => "clustering",
        _ => "search_document"
    };

    private static string GetTruncateString(CohereTruncate truncate) => truncate switch
    {
        CohereTruncate.Start => "START",
        CohereTruncate.End => "END",
        CohereTruncate.None => "NONE",
        _ => "END"
    };

    private static RateLimitInfo? ParseRateLimitHeaders(HttpResponseMessage response)
    {
        int? retryAfter = null;
        int? remaining = null;
        int? limit = null;

        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            if (int.TryParse(retryValues.FirstOrDefault(), out var ra))
                retryAfter = ra;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
        {
            if (int.TryParse(remainingValues.FirstOrDefault(), out var rem))
                remaining = rem;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues))
        {
            if (int.TryParse(limitValues.FirstOrDefault(), out var lim))
                limit = lim;
        }

        if (retryAfter.HasValue || remaining.HasValue)
        {
            return new RateLimitInfo
            {
                RetryAfterSeconds = retryAfter ?? 60,
                RemainingRequests = remaining ?? 0,
                TotalRequests = limit ?? 0,
                ResetAt = DateTime.UtcNow.AddSeconds(retryAfter ?? 60)
            };
        }

        return null;
    }

    // Request/Response models
    private sealed class CohereEmbeddingRequest
    {
        [JsonPropertyName("texts")]
        public List<string> Texts { get; set; } = new();

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("input_type")]
        public string? InputType { get; set; }

        [JsonPropertyName("truncate")]
        public string? Truncate { get; set; }
    }

    private sealed class CohereEmbeddingResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; } = new();

        [JsonPropertyName("texts")]
        public List<string> Texts { get; set; } = new();

        [JsonPropertyName("meta")]
        public CohereMeta? Meta { get; set; }
    }

    private sealed class CohereMeta
    {
        [JsonPropertyName("api_version")]
        public CohereApiVersion? ApiVersion { get; set; }

        [JsonPropertyName("billed_units")]
        public CohereBilledUnits? BilledUnits { get; set; }
    }

    private sealed class CohereApiVersion
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
    }

    private sealed class CohereBilledUnits
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }
    }
}

/// <summary>
/// Cohere input types for embedding generation.
/// </summary>
public enum CohereInputType
{
    /// <summary>For documents to be indexed/stored.</summary>
    SearchDocument,

    /// <summary>For search queries.</summary>
    SearchQuery,

    /// <summary>For classification tasks.</summary>
    Classification,

    /// <summary>For clustering tasks.</summary>
    Clustering
}

/// <summary>
/// Cohere truncation options for texts exceeding token limits.
/// </summary>
public enum CohereTruncate
{
    /// <summary>Truncate from the start of the text.</summary>
    Start,

    /// <summary>Truncate from the end of the text.</summary>
    End,

    /// <summary>Do not truncate (will fail if text exceeds limit).</summary>
    None
}
