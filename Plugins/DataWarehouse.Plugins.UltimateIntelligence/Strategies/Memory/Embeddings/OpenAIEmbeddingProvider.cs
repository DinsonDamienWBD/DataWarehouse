using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// OpenAI embedding provider supporting text-embedding-3-small, text-embedding-3-large,
/// and legacy text-embedding-ada-002 models.
///
/// Features:
/// - Multiple model support with automatic dimension configuration
/// - Batch processing up to 2048 texts per request
/// - Retry logic with exponential backoff
/// - Rate limiting with configurable concurrency
/// - Cost tracking per request
/// - Support for dimension reduction (text-embedding-3 models)
/// </summary>
public sealed class OpenAIEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultApiBase = "https://api.openai.com/v1";
    private const string DefaultModel = "text-embedding-3-small";
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
            DisplayName = "Ada 002 (Legacy)",
            Dimensions = 1536,
            MaxTokens = 8191,
            CostPer1KTokens = 0.0001m,
            Features = new[] { "legacy" }
        }
    };

    private string _currentModel;
    private int? _dimensionsOverride;
    private readonly string _apiBase;
    private readonly string? _organization;

    /// <inheritdoc/>
    public override string ProviderId => "openai";

    /// <inheritdoc/>
    public override string DisplayName => "OpenAI Embeddings";

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
    /// Set to null to use the model's default dimensions.
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
    /// Creates a new OpenAI embedding provider.
    /// </summary>
    /// <param name="config">Provider configuration with ApiKey required.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public OpenAIEmbeddingProvider(EmbeddingProviderConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("ApiKey is required for OpenAI provider");

        _currentModel = config.Model ?? DefaultModel;
        _apiBase = config.Endpoint ?? DefaultApiBase;
        _organization = config.AdditionalConfig.TryGetValue("Organization", out var org) ? org?.ToString() : null;

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
            // Process in chunks
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
        var payload = new OpenAIEmbeddingRequest
        {
            Input = texts,
            Model = _currentModel,
            EncodingFormat = "float"
        };

        // Add dimensions parameter for text-embedding-3 models
        if (_dimensionsOverride.HasValue && _currentModel.StartsWith("text-embedding-3"))
        {
            payload.Dimensions = _dimensionsOverride.Value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/embeddings");
        request.Headers.Add("Authorization", $"Bearer {Config.ApiKey}");
        if (!string.IsNullOrEmpty(_organization))
            request.Headers.Add("OpenAI-Organization", _organization);

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

            throw new EmbeddingException($"OpenAI API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode,
                RateLimitInfo = rateLimitInfo
            };
        }

        var result = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>(cancellationToken: ct);
        if (result?.Data == null || result.Data.Count == 0)
            throw new EmbeddingException("Empty response from OpenAI API") { ProviderId = ProviderId };

        // Sort by index to ensure correct order
        var sortedData = result.Data.OrderBy(d => d.Index).ToArray();
        return sortedData.Select(d => d.Embedding).ToArray();
    }

    /// <inheritdoc/>
    public override async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Send a minimal request to validate credentials
            var result = await GetEmbeddingAsync("test", ct);
            return result.Length > 0;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in OpenAIEmbeddingProvider.cs");
            return false;
        }
    }

    private static RateLimitInfo? ParseRateLimitHeaders(HttpResponseMessage response)
    {
        int? retryAfter = null;
        int? remaining = null;
        int? limit = null;
        DateTime? resetAt = null;

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

        if (response.Headers.TryGetValues("x-ratelimit-reset-requests", out var resetValues))
        {
            var resetStr = resetValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(resetStr))
            {
                // Parse duration like "1s" or "1m30s"
                var seconds = ParseDurationToSeconds(resetStr);
                resetAt = DateTime.UtcNow.AddSeconds(seconds);
            }
        }

        if (retryAfter.HasValue || remaining.HasValue)
        {
            return new RateLimitInfo
            {
                RetryAfterSeconds = retryAfter ?? 60,
                RemainingRequests = remaining ?? 0,
                TotalRequests = limit ?? 0,
                ResetAt = resetAt ?? DateTime.UtcNow.AddSeconds(retryAfter ?? 60)
            };
        }

        return null;
    }

    private static int ParseDurationToSeconds(string duration)
    {
        var totalSeconds = 0;
        var currentNumber = new StringBuilder();

        foreach (var c in duration)
        {
            if (char.IsDigit(c) || c == '.')
            {
                currentNumber.Append(c);
            }
            else if (currentNumber.Length > 0)
            {
                // P2-3149: Use InvariantCulture to avoid FormatException on non-US locales
                // where the decimal separator is a comma instead of a period.
                var value = double.Parse(currentNumber.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                currentNumber.Clear();

                totalSeconds += c switch
                {
                    'h' => (int)(value * 3600),
                    'm' => (int)(value * 60),
                    's' => (int)value,
                    _ => 0
                };
            }
        }

        return totalSeconds > 0 ? totalSeconds : 60;
    }

    // Request/Response models
    private sealed class OpenAIEmbeddingRequest
    {
        public string[] Input { get; set; } = Array.Empty<string>();
        public string Model { get; set; } = "";
        public string? EncodingFormat { get; set; }
        public int? Dimensions { get; set; }
    }

    private sealed class OpenAIEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAIEmbeddingData> Data { get; set; } = new();

        [JsonPropertyName("usage")]
        public OpenAIUsage? Usage { get; set; }
    }

    private sealed class OpenAIEmbeddingData
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    private sealed class OpenAIUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
