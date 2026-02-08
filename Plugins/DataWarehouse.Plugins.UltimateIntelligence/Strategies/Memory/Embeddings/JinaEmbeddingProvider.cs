using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Jina AI embedding provider with support for long-context embeddings.
///
/// Features:
/// - jina-embeddings-v2-base-en: 8192 token context (8K)
/// - jina-embeddings-v2-small-en: Lightweight variant
/// - jina-embeddings-v3: Latest model with multiple task types
/// - Batch processing support
/// - Multiple task types (retrieval, classification, clustering)
/// - Retry logic with exponential backoff
/// </summary>
public sealed class JinaEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultApiBase = "https://api.jina.ai/v1";
    private const string DefaultModel = "jina-embeddings-v2-base-en";
    private const int MaxBatchSize = 2048;

    private static readonly IReadOnlyList<EmbeddingModelInfo> Models = new[]
    {
        new EmbeddingModelInfo
        {
            ModelId = "jina-embeddings-v2-base-en",
            DisplayName = "Jina Embeddings v2 Base English",
            Dimensions = 768,
            MaxTokens = 8192,
            CostPer1KTokens = 0.00002m,
            IsDefault = true,
            Features = new[] { "long-context", "8192-tokens", "english" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "jina-embeddings-v2-small-en",
            DisplayName = "Jina Embeddings v2 Small English",
            Dimensions = 512,
            MaxTokens = 8192,
            CostPer1KTokens = 0.00002m,
            Features = new[] { "long-context", "lightweight", "english" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "jina-embeddings-v2-base-de",
            DisplayName = "Jina Embeddings v2 Base German",
            Dimensions = 768,
            MaxTokens = 8192,
            CostPer1KTokens = 0.00002m,
            Features = new[] { "long-context", "german" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "jina-embeddings-v2-base-zh",
            DisplayName = "Jina Embeddings v2 Base Chinese",
            Dimensions = 768,
            MaxTokens = 8192,
            CostPer1KTokens = 0.00002m,
            Features = new[] { "long-context", "chinese" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "jina-embeddings-v3",
            DisplayName = "Jina Embeddings v3",
            Dimensions = 1024,
            MaxTokens = 8192,
            CostPer1KTokens = 0.00004m,
            Features = new[] { "latest", "multi-task", "high-quality" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "jina-colbert-v2",
            DisplayName = "Jina ColBERT v2",
            Dimensions = 128, // Per-token dimensions for ColBERT
            MaxTokens = 8192,
            CostPer1KTokens = 0.00002m,
            Features = new[] { "colbert", "late-interaction", "retrieval" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "jina-clip-v1",
            DisplayName = "Jina CLIP v1",
            Dimensions = 768,
            MaxTokens = 77,
            CostPer1KTokens = 0.00002m,
            Features = new[] { "multimodal", "image-text", "clip" }
        }
    };

    private string _currentModel;
    private readonly string _apiBase;
    private JinaTaskType _taskType;
    private bool _lateFusion;
    private int? _dimensionsOverride;

    /// <inheritdoc/>
    public override string ProviderId => "jina";

    /// <inheritdoc/>
    public override string DisplayName => "Jina AI Embeddings";

    /// <inheritdoc/>
    public override int VectorDimensions
    {
        get
        {
            if (_dimensionsOverride.HasValue)
                return _dimensionsOverride.Value;
            return Models.FirstOrDefault(m => m.ModelId == _currentModel)?.Dimensions ?? 768;
        }
    }

    /// <inheritdoc/>
    public override int MaxTokens => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.MaxTokens ?? 8192;

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
    /// Gets or sets the task type for embedding generation (v3 models).
    /// </summary>
    public JinaTaskType TaskType
    {
        get => _taskType;
        set => _taskType = value;
    }

    /// <summary>
    /// Gets or sets whether to use late fusion for multi-vector embeddings (ColBERT).
    /// </summary>
    public bool LateFusion
    {
        get => _lateFusion;
        set => _lateFusion = value;
    }

    /// <summary>
    /// Gets or sets the optional dimension override for models that support it.
    /// </summary>
    public int? DimensionsOverride
    {
        get => _dimensionsOverride;
        set
        {
            if (value.HasValue && value.Value <= 0)
                throw new ArgumentException("Dimensions must be positive");
            _dimensionsOverride = value;
        }
    }

    /// <summary>
    /// Creates a new Jina AI embedding provider.
    /// </summary>
    /// <param name="config">Provider configuration with ApiKey required.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public JinaEmbeddingProvider(EmbeddingProviderConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("ApiKey is required for Jina AI provider");

        _currentModel = config.Model ?? DefaultModel;
        _apiBase = config.Endpoint ?? DefaultApiBase;
        _taskType = JinaTaskType.Retrieval;
        _lateFusion = false;

        if (config.AdditionalConfig.TryGetValue("TaskType", out var taskType) && taskType is string tt)
        {
            _taskType = Enum.TryParse<JinaTaskType>(tt, true, out var parsed) ? parsed : JinaTaskType.Retrieval;
        }

        if (config.AdditionalConfig.TryGetValue("LateFusion", out var lateFusion) && lateFusion is bool lf)
        {
            _lateFusion = lf;
        }

        if (config.AdditionalConfig.TryGetValue("Dimensions", out var dims) && dims is int d)
        {
            _dimensionsOverride = d;
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
        var payload = new JinaEmbeddingRequest
        {
            Input = texts.ToList(),
            Model = _currentModel
        };

        // Add task type for v3 models
        if (_currentModel.Contains("v3"))
        {
            payload.Task = GetTaskTypeString(_taskType);
        }

        // Add dimensions override if specified
        if (_dimensionsOverride.HasValue)
        {
            payload.Dimensions = _dimensionsOverride.Value;
        }

        // Add late fusion for ColBERT models
        if (_currentModel.Contains("colbert") && _lateFusion)
        {
            payload.LateFusion = true;
        }

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

            throw new EmbeddingException($"Jina AI API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode,
                RateLimitInfo = rateLimitInfo
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JinaEmbeddingResponse>(cancellationToken: ct);
        if (result?.Data == null || result.Data.Count == 0)
            throw new EmbeddingException("Empty response from Jina AI API") { ProviderId = ProviderId };

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

    private static string GetTaskTypeString(JinaTaskType taskType) => taskType switch
    {
        JinaTaskType.Retrieval => "retrieval.query",
        JinaTaskType.RetrievalDocument => "retrieval.passage",
        JinaTaskType.Classification => "classification",
        JinaTaskType.TextMatching => "text-matching",
        JinaTaskType.Separation => "separation",
        _ => "retrieval.query"
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
    private sealed class JinaEmbeddingRequest
    {
        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = new();

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("task")]
        public string? Task { get; set; }

        [JsonPropertyName("dimensions")]
        public int? Dimensions { get; set; }

        [JsonPropertyName("late_fusion")]
        public bool? LateFusion { get; set; }
    }

    private sealed class JinaEmbeddingResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("object")]
        public string Object { get; set; } = "";

        [JsonPropertyName("data")]
        public List<JinaEmbeddingData> Data { get; set; } = new();

        [JsonPropertyName("usage")]
        public JinaUsage? Usage { get; set; }
    }

    private sealed class JinaEmbeddingData
    {
        [JsonPropertyName("object")]
        public string Object { get; set; } = "";

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    private sealed class JinaUsage
    {
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }
    }
}

/// <summary>
/// Jina AI task types for embedding generation (v3 models).
/// </summary>
public enum JinaTaskType
{
    /// <summary>For search queries.</summary>
    Retrieval,

    /// <summary>For documents to be searched.</summary>
    RetrievalDocument,

    /// <summary>For classification tasks.</summary>
    Classification,

    /// <summary>For text matching/similarity.</summary>
    TextMatching,

    /// <summary>For text separation/clustering.</summary>
    Separation
}
