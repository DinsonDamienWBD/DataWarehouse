using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Voyage AI embedding provider specialized for code and document embeddings.
///
/// Features:
/// - voyage-large-2: General-purpose high-quality embeddings
/// - voyage-code-2: Specialized for code embeddings
/// - voyage-law-2: Specialized for legal documents
/// - voyage-finance-2: Specialized for financial documents
/// - Batch processing up to 128 texts
/// - Input type optimization (query vs document)
/// - Retry logic with exponential backoff
/// </summary>
public sealed class VoyageAIEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultApiBase = "https://api.voyageai.com/v1";
    private const string DefaultModel = "voyage-large-2";
    private const int MaxBatchSize = 128;

    private static readonly IReadOnlyList<EmbeddingModelInfo> Models = new[]
    {
        new EmbeddingModelInfo
        {
            ModelId = "voyage-large-2",
            DisplayName = "Voyage Large 2",
            Dimensions = 1024,
            MaxTokens = 16000,
            CostPer1KTokens = 0.00012m,
            IsDefault = true,
            Features = new[] { "general-purpose", "high-quality", "long-context" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "voyage-code-2",
            DisplayName = "Voyage Code 2",
            Dimensions = 1536,
            MaxTokens = 16000,
            CostPer1KTokens = 0.00012m,
            Features = new[] { "code", "programming", "technical" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "voyage-law-2",
            DisplayName = "Voyage Law 2",
            Dimensions = 1024,
            MaxTokens = 16000,
            CostPer1KTokens = 0.00012m,
            Features = new[] { "legal", "contracts", "regulations" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "voyage-finance-2",
            DisplayName = "Voyage Finance 2",
            Dimensions = 1024,
            MaxTokens = 16000,
            CostPer1KTokens = 0.00012m,
            Features = new[] { "finance", "financial-documents", "reports" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "voyage-multilingual-2",
            DisplayName = "Voyage Multilingual 2",
            Dimensions = 1024,
            MaxTokens = 16000,
            CostPer1KTokens = 0.00012m,
            Features = new[] { "multilingual", "cross-lingual" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "voyage-lite-02-instruct",
            DisplayName = "Voyage Lite 02 Instruct",
            Dimensions = 1024,
            MaxTokens = 4000,
            CostPer1KTokens = 0.00002m,
            Features = new[] { "lightweight", "fast", "cost-effective" }
        }
    };

    private string _currentModel;
    private readonly string _apiBase;
    private VoyageInputType _inputType;
    private bool _truncation;

    /// <inheritdoc/>
    public override string ProviderId => "voyageai";

    /// <inheritdoc/>
    public override string DisplayName => "Voyage AI Embeddings";

    /// <inheritdoc/>
    public override int VectorDimensions => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.Dimensions ?? 1024;

    /// <inheritdoc/>
    public override int MaxTokens => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.MaxTokens ?? 16000;

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
    /// Use Document for texts to be stored, Query for search queries.
    /// </summary>
    public VoyageInputType InputType
    {
        get => _inputType;
        set => _inputType = value;
    }

    /// <summary>
    /// Gets or sets whether to truncate texts that exceed the token limit.
    /// </summary>
    public bool Truncation
    {
        get => _truncation;
        set => _truncation = value;
    }

    /// <summary>
    /// Creates a new Voyage AI embedding provider.
    /// </summary>
    /// <param name="config">Provider configuration with ApiKey required.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public VoyageAIEmbeddingProvider(EmbeddingProviderConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("ApiKey is required for Voyage AI provider");

        _currentModel = config.Model ?? DefaultModel;
        _apiBase = config.Endpoint ?? DefaultApiBase;
        _inputType = VoyageInputType.Document;
        _truncation = true;

        if (config.AdditionalConfig.TryGetValue("InputType", out var inputType) && inputType is string it)
        {
            _inputType = Enum.TryParse<VoyageInputType>(it, true, out var parsed) ? parsed : VoyageInputType.Document;
        }

        if (config.AdditionalConfig.TryGetValue("Truncation", out var truncate) && truncate is bool tr)
        {
            _truncation = tr;
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
        var payload = new VoyageEmbeddingRequest
        {
            Input = texts.ToList(),
            Model = _currentModel,
            InputType = GetInputTypeString(_inputType),
            Truncation = _truncation
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/embeddings");
        request.Headers.Add("Authorization", $"Bearer {Config.ApiKey}");

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

            throw new EmbeddingException($"Voyage AI API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode,
                RateLimitInfo = rateLimitInfo
            };
        }

        var result = await response.Content.ReadFromJsonAsync<VoyageEmbeddingResponse>(cancellationToken: ct);
        if (result?.Data == null || result.Data.Count == 0)
            throw new EmbeddingException("Empty response from Voyage AI API") { ProviderId = ProviderId };

        // Sort by index to ensure correct order
        var sortedData = result.Data.OrderBy(d => d.Index).ToArray();
        return sortedData.Select(d => d.Embedding).ToArray();
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

    private static string GetInputTypeString(VoyageInputType inputType) => inputType switch
    {
        VoyageInputType.Query => "query",
        VoyageInputType.Document => "document",
        _ => "document"
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
    private sealed class VoyageEmbeddingRequest
    {
        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = new();

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("input_type")]
        public string? InputType { get; set; }

        [JsonPropertyName("truncation")]
        public bool? Truncation { get; set; }
    }

    private sealed class VoyageEmbeddingResponse
    {
        [JsonPropertyName("object")]
        public string Object { get; set; } = "";

        [JsonPropertyName("data")]
        public List<VoyageEmbeddingData> Data { get; set; } = new();

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("usage")]
        public VoyageUsage? Usage { get; set; }
    }

    private sealed class VoyageEmbeddingData
    {
        [JsonPropertyName("object")]
        public string Object { get; set; } = "";

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    private sealed class VoyageUsage
    {
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}

/// <summary>
/// Voyage AI input types for embedding generation.
/// </summary>
public enum VoyageInputType
{
    /// <summary>For search queries.</summary>
    Query,

    /// <summary>For documents to be indexed/stored.</summary>
    Document
}
