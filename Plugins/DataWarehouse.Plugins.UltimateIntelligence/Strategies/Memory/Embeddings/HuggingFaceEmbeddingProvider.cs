using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// HuggingFace Inference API embedding provider for accessing HuggingFace-hosted models.
///
/// Features:
/// - Access to thousands of sentence-transformer models
/// - Popular models: all-MiniLM-L6-v2, all-mpnet-base-v2, e5-large-v2
/// - API token authentication
/// - Configurable model selection
/// - Retry logic with exponential backoff
/// - Support for custom model endpoints
/// </summary>
public sealed class HuggingFaceEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultApiBase = "https://api-inference.huggingface.co/pipeline/feature-extraction";
    private const string DefaultModel = "sentence-transformers/all-MiniLM-L6-v2";

    private static readonly IReadOnlyList<EmbeddingModelInfo> Models = new[]
    {
        new EmbeddingModelInfo
        {
            ModelId = "sentence-transformers/all-MiniLM-L6-v2",
            DisplayName = "All MiniLM L6 v2",
            Dimensions = 384,
            MaxTokens = 256,
            CostPer1KTokens = 0m, // Free tier available
            IsDefault = true,
            Features = new[] { "lightweight", "fast", "english" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "sentence-transformers/all-mpnet-base-v2",
            DisplayName = "All MPNet Base v2",
            Dimensions = 768,
            MaxTokens = 384,
            CostPer1KTokens = 0m,
            Features = new[] { "high-quality", "english" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2",
            DisplayName = "Multilingual MiniLM L12 v2",
            Dimensions = 384,
            MaxTokens = 256,
            CostPer1KTokens = 0m,
            Features = new[] { "multilingual", "50+ languages" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "intfloat/e5-large-v2",
            DisplayName = "E5 Large v2",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "high-quality", "instruction-tuned" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "intfloat/e5-base-v2",
            DisplayName = "E5 Base v2",
            Dimensions = 768,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "balanced", "instruction-tuned" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "BAAI/bge-large-en-v1.5",
            DisplayName = "BGE Large English v1.5",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "high-quality", "english", "retrieval-optimized" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "BAAI/bge-base-en-v1.5",
            DisplayName = "BGE Base English v1.5",
            Dimensions = 768,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "balanced", "english", "retrieval-optimized" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "thenlper/gte-large",
            DisplayName = "GTE Large",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "high-quality", "general-purpose" }
        }
    };

    private string _currentModel;
    private int _currentDimensions;
    private readonly string _apiBase;
    private bool _useOptions;
    private bool _waitForModel;

    /// <inheritdoc/>
    public override string ProviderId => "huggingface";

    /// <inheritdoc/>
    public override string DisplayName => "HuggingFace Embeddings";

    /// <inheritdoc/>
    public override int VectorDimensions => _currentDimensions;

    /// <inheritdoc/>
    public override int MaxTokens => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.MaxTokens ?? 256;

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
            _currentModel = value;
            var modelInfo = Models.FirstOrDefault(m => m.ModelId == value);
            _currentDimensions = modelInfo?.Dimensions ?? 384;
        }
    }

    /// <summary>
    /// Gets or sets whether to wait for the model to be loaded if it's not ready.
    /// </summary>
    public bool WaitForModel
    {
        get => _waitForModel;
        set => _waitForModel = value;
    }

    /// <summary>
    /// Creates a new HuggingFace embedding provider.
    /// </summary>
    /// <param name="config">Provider configuration with ApiKey (token) required.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public HuggingFaceEmbeddingProvider(EmbeddingProviderConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("ApiKey (HuggingFace token) is required");

        _currentModel = config.Model ?? DefaultModel;
        _apiBase = config.Endpoint ?? DefaultApiBase;

        var modelInfo = Models.FirstOrDefault(m => m.ModelId == _currentModel);
        _currentDimensions = modelInfo?.Dimensions ?? 384;

        _waitForModel = true;
        if (config.AdditionalConfig.TryGetValue("WaitForModel", out var wait) && wait is bool w)
            _waitForModel = w;

        _useOptions = true;
        if (config.AdditionalConfig.TryGetValue("UseOptions", out var opts) && opts is bool o)
            _useOptions = o;

        // Allow custom dimensions for unknown models
        if (config.AdditionalConfig.TryGetValue("Dimensions", out var dims) && dims is int d)
            _currentDimensions = d;
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
        var url = $"{_apiBase}/{_currentModel}";

        object payload;
        if (_useOptions)
        {
            payload = new HuggingFaceRequest
            {
                Inputs = texts.ToList(),
                Options = new HuggingFaceOptions
                {
                    WaitForModel = _waitForModel
                }
            };
        }
        else
        {
            payload = new { inputs = texts };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
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

            // Check for model loading status
            bool isModelLoading = false;
            int? estimatedTime = null;

            if (statusCode == 503)
            {
                try
                {
                    var errorJson = JsonDocument.Parse(errorContent);
                    if (errorJson.RootElement.TryGetProperty("error", out var errorProp))
                    {
                        var errorMsg = errorProp.GetString() ?? "";
                        isModelLoading = errorMsg.Contains("loading") || errorMsg.Contains("currently loading");
                    }
                    if (errorJson.RootElement.TryGetProperty("estimated_time", out var timeProp))
                    {
                        estimatedTime = (int)timeProp.GetDouble();
                    }
                }
                catch { /* JSON parsing failure — use defaults */ }
            }

            var isTransient = statusCode >= 500 || statusCode == 429 || isModelLoading;

            RateLimitInfo? rateLimitInfo = null;
            if (statusCode == 429 || isModelLoading)
            {
                rateLimitInfo = new RateLimitInfo
                {
                    RetryAfterSeconds = estimatedTime ?? 30,
                    ResetAt = DateTime.UtcNow.AddSeconds(estimatedTime ?? 30)
                };
            }

            throw new EmbeddingException($"HuggingFace API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode,
                RateLimitInfo = rateLimitInfo
            };
        }

        var content = await response.Content.ReadAsStringAsync(ct);

        // HuggingFace returns nested arrays for batch requests
        // Format: [[embedding1], [embedding2], ...] where each embedding is a flat array
        var embeddings = ParseEmbeddingResponse(content);
        if (embeddings == null || embeddings.Length == 0)
            throw new EmbeddingException("Empty response from HuggingFace API") { ProviderId = ProviderId };

        return embeddings;
    }

    private float[][]? ParseEmbeddingResponse(string jsonContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            // Handle nested array format: [[...], [...], ...]
            if (root.ValueKind == JsonValueKind.Array)
            {
                var results = new List<float[]>();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        // Check if this is a 2D array (tokens x dimensions) that needs averaging
                        // or a 1D array (sentence embedding)
                        var firstElement = item.EnumerateArray().FirstOrDefault();
                        if (firstElement.ValueKind == JsonValueKind.Array)
                        {
                            // It's a 2D array - need to pool (mean pooling)
                            var tokenEmbeddings = new List<float[]>();
                            foreach (var token in item.EnumerateArray())
                            {
                                var tokenEmb = token.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                                tokenEmbeddings.Add(tokenEmb);
                            }
                            // Mean pooling
                            var dims = tokenEmbeddings[0].Length;
                            var pooled = new float[dims];
                            for (int d = 0; d < dims; d++)
                            {
                                pooled[d] = tokenEmbeddings.Average(t => t[d]);
                            }
                            results.Add(pooled);
                        }
                        else
                        {
                            // It's a 1D array - direct embedding
                            var embedding = item.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                            results.Add(embedding);
                        }
                    }
                }
                return results.ToArray();
            }

            return null;
        }
        catch (Exception ex)
        {
            throw new EmbeddingException($"Failed to parse HuggingFace response: {ex.Message}", ex)
            {
                ProviderId = ProviderId
            };
        }
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
            Debug.WriteLine($"Caught exception in HuggingFaceEmbeddingProvider.cs");
            return false;
        }
    }

    // Request models
    private sealed class HuggingFaceRequest
    {
        [JsonPropertyName("inputs")]
        public List<string> Inputs { get; set; } = new();

        [JsonPropertyName("options")]
        public HuggingFaceOptions? Options { get; set; }
    }

    private sealed class HuggingFaceOptions
    {
        [JsonPropertyName("wait_for_model")]
        public bool WaitForModel { get; set; } = true;

        [JsonPropertyName("use_cache")]
        public bool UseCache { get; set; } = true;
    }
}
