using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Azure OpenAI embedding provider for Azure-hosted OpenAI models.
///
/// Features:
/// - Support for Azure-specific endpoints and deployment names
/// - API key and Azure AD/Managed Identity authentication
/// - Same models as OpenAI (text-embedding-3-small/large, ada-002)
/// - Batch processing up to 2048 texts
/// - Retry logic with exponential backoff
/// - Rate limiting awareness
/// </summary>
public sealed class AzureOpenAIEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultApiVersion = "2024-02-01";
    private const int MaxBatchSize = 2048;

    private static readonly IReadOnlyList<EmbeddingModelInfo> Models = new[]
    {
        new EmbeddingModelInfo
        {
            ModelId = "text-embedding-3-small",
            DisplayName = "Text Embedding 3 Small",
            Dimensions = 1536,
            MaxTokens = 8191,
            CostPer1KTokens = 0.00002m,
            IsDefault = true,
            Features = new[] { "dimension-reduction", "multilingual" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "text-embedding-3-large",
            DisplayName = "Text Embedding 3 Large",
            Dimensions = 3072,
            MaxTokens = 8191,
            CostPer1KTokens = 0.00013m,
            Features = new[] { "dimension-reduction", "multilingual", "high-performance" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "text-embedding-ada-002",
            DisplayName = "Ada 002",
            Dimensions = 1536,
            MaxTokens = 8191,
            CostPer1KTokens = 0.0001m,
            Features = new[] { "legacy" }
        }
    };

    private string _currentModel;
    private int? _dimensionsOverride;
    private readonly string _endpoint;
    private readonly string _deploymentName;
    private readonly string _apiVersion;
    private readonly bool _useManagedIdentity;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;

    /// <inheritdoc/>
    public override string ProviderId => "azure-openai";

    /// <inheritdoc/>
    public override string DisplayName => "Azure OpenAI Embeddings";

    /// <inheritdoc/>
    public override int VectorDimensions
    {
        get
        {
            if (_dimensionsOverride.HasValue)
                return _dimensionsOverride.Value;
            return Models.FirstOrDefault(m => m.ModelId == _currentModel)?.Dimensions ?? 1536;
        }
    }

    /// <inheritdoc/>
    public override int MaxTokens => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.MaxTokens ?? 8191;

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
    /// Gets or sets the optional dimension override for text-embedding-3 models.
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
    /// Gets the deployment name for the Azure OpenAI resource.
    /// </summary>
    public string DeploymentName => _deploymentName;

    /// <summary>
    /// Creates a new Azure OpenAI embedding provider with API key authentication.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="deploymentName">Azure OpenAI deployment name.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public AzureOpenAIEmbeddingProvider(
        EmbeddingProviderConfig config,
        string deploymentName,
        HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new ArgumentException("Endpoint is required for Azure OpenAI provider");
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("DeploymentName is required", nameof(deploymentName));
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("ApiKey is required for Azure OpenAI provider");

        _endpoint = config.Endpoint.TrimEnd('/');
        _deploymentName = deploymentName;
        _currentModel = config.Model ?? "text-embedding-3-small";
        _apiVersion = config.AdditionalConfig.TryGetValue("ApiVersion", out var ver) ? ver?.ToString() ?? DefaultApiVersion : DefaultApiVersion;
        _useManagedIdentity = false;

        if (config.AdditionalConfig.TryGetValue("Dimensions", out var dims) && dims is int d)
            _dimensionsOverride = d;
    }

    /// <summary>
    /// Creates a new Azure OpenAI embedding provider with Managed Identity authentication.
    /// </summary>
    /// <param name="config">Provider configuration (ApiKey not required).</param>
    /// <param name="deploymentName">Azure OpenAI deployment name.</param>
    /// <param name="tokenProvider">Function to retrieve Azure AD tokens.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public AzureOpenAIEmbeddingProvider(
        EmbeddingProviderConfig config,
        string deploymentName,
        Func<CancellationToken, Task<string>> tokenProvider,
        HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new ArgumentException("Endpoint is required for Azure OpenAI provider");
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("DeploymentName is required", nameof(deploymentName));
        if (tokenProvider == null)
            throw new ArgumentNullException(nameof(tokenProvider));

        _endpoint = config.Endpoint.TrimEnd('/');
        _deploymentName = deploymentName;
        _currentModel = config.Model ?? "text-embedding-3-small";
        _apiVersion = config.AdditionalConfig.TryGetValue("ApiVersion", out var ver) ? ver?.ToString() ?? DefaultApiVersion : DefaultApiVersion;
        _useManagedIdentity = true;
        _tokenProvider = tokenProvider;

        if (config.AdditionalConfig.TryGetValue("Dimensions", out var dims) && dims is int d)
            _dimensionsOverride = d;
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
        var url = $"{_endpoint}/openai/deployments/{_deploymentName}/embeddings?api-version={_apiVersion}";

        var payload = new AzureEmbeddingRequest
        {
            Input = texts
        };

        if (_dimensionsOverride.HasValue && _currentModel.StartsWith("text-embedding-3"))
        {
            payload.Dimensions = _dimensionsOverride.Value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (_useManagedIdentity && _tokenProvider != null)
        {
            var token = await _tokenProvider(ct);
            request.Headers.Add("Authorization", $"Bearer {token}");
        }
        else
        {
            request.Headers.Add("api-key", Config.ApiKey);
        }

        request.Content = JsonContent.Create(payload, options: new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var response = await HttpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(ct);
            var statusCode = (int)response.StatusCode;
            var isTransient = statusCode >= 500 || statusCode == 429 || statusCode == 503;

            RateLimitInfo? rateLimitInfo = null;
            if (statusCode == 429)
            {
                rateLimitInfo = ParseRateLimitHeaders(response);
            }

            throw new EmbeddingException($"Azure OpenAI API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode,
                RateLimitInfo = rateLimitInfo
            };
        }

        var result = await response.Content.ReadFromJsonAsync<AzureEmbeddingResponse>(cancellationToken: ct);
        if (result?.Data == null || result.Data.Count == 0)
            throw new EmbeddingException("Empty response from Azure OpenAI API") { ProviderId = ProviderId };

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

        if (response.Headers.TryGetValues("x-ratelimit-remaining-requests", out var remainingValues))
        {
            if (int.TryParse(remainingValues.FirstOrDefault(), out var rem))
                remaining = rem;
        }

        if (response.Headers.TryGetValues("x-ratelimit-limit-requests", out var limitValues))
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
    private sealed class AzureEmbeddingRequest
    {
        [JsonPropertyName("input")]
        public string[] Input { get; set; } = Array.Empty<string>();

        [JsonPropertyName("dimensions")]
        public int? Dimensions { get; set; }
    }

    private sealed class AzureEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<AzureEmbeddingData> Data { get; set; } = new();

        [JsonPropertyName("usage")]
        public AzureUsage? Usage { get; set; }
    }

    private sealed class AzureEmbeddingData
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    private sealed class AzureUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
