using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Ollama embedding provider for local model inference.
///
/// Features:
/// - Fully local/offline embedding generation
/// - Support for nomic-embed-text, mxbai-embed-large, all-minilm, and other Ollama models
/// - Configurable local HTTP endpoint (default: http://localhost:11434)
/// - No rate limiting (local resource)
/// - Low latency for edge/offline deployments
/// - Automatic model pulling if not available
/// </summary>
public sealed class OllamaEmbeddingProvider : EmbeddingProviderBase
{
    private const string DefaultEndpoint = "http://localhost:11434";
    private const string DefaultModel = "nomic-embed-text";

    private static readonly IReadOnlyList<EmbeddingModelInfo> Models = new[]
    {
        new EmbeddingModelInfo
        {
            ModelId = "nomic-embed-text",
            DisplayName = "Nomic Embed Text",
            Dimensions = 768,
            MaxTokens = 8192,
            CostPer1KTokens = 0m, // Free (local)
            IsDefault = true,
            Features = new[] { "local", "high-performance", "large-context" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "mxbai-embed-large",
            DisplayName = "MxBai Embed Large",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "local", "high-quality", "large-dimensions" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "all-minilm",
            DisplayName = "All MiniLM",
            Dimensions = 384,
            MaxTokens = 256,
            CostPer1KTokens = 0m,
            Features = new[] { "local", "lightweight", "fast" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "snowflake-arctic-embed",
            DisplayName = "Snowflake Arctic Embed",
            Dimensions = 1024,
            MaxTokens = 512,
            CostPer1KTokens = 0m,
            Features = new[] { "local", "retrieval-optimized" }
        },
        new EmbeddingModelInfo
        {
            ModelId = "bge-m3",
            DisplayName = "BGE M3",
            Dimensions = 1024,
            MaxTokens = 8192,
            CostPer1KTokens = 0m,
            Features = new[] { "local", "multilingual", "multi-task" }
        }
    };

    private string _currentModel;
    private int _currentDimensions;
    private readonly string _endpoint;
    private bool _autoPull;
    private bool _keepAlive;

    /// <inheritdoc/>
    public override string ProviderId => "ollama";

    /// <inheritdoc/>
    public override string DisplayName => "Ollama Local Embeddings";

    /// <inheritdoc/>
    public override int VectorDimensions => _currentDimensions;

    /// <inheritdoc/>
    public override int MaxTokens => Models.FirstOrDefault(m => m.ModelId == _currentModel)?.MaxTokens ?? 512;

    /// <inheritdoc/>
    public override bool SupportsMultipleTexts => false; // Ollama processes one at a time

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
            _currentDimensions = modelInfo?.Dimensions ?? 768;
        }
    }

    /// <summary>
    /// Gets or sets whether to automatically pull the model if not available.
    /// </summary>
    public bool AutoPull
    {
        get => _autoPull;
        set => _autoPull = value;
    }

    /// <summary>
    /// Gets or sets whether to keep the model loaded in memory after requests.
    /// </summary>
    public bool KeepAlive
    {
        get => _keepAlive;
        set => _keepAlive = value;
    }

    /// <summary>
    /// Creates a new Ollama embedding provider.
    /// </summary>
    /// <param name="config">Provider configuration. ApiKey is not required for local Ollama.</param>
    /// <param name="httpClient">Optional HTTP client.</param>
    public OllamaEmbeddingProvider(EmbeddingProviderConfig config, HttpClient? httpClient = null)
        : base(config, httpClient)
    {
        _endpoint = config.Endpoint?.TrimEnd('/') ?? DefaultEndpoint;
        _currentModel = config.Model ?? DefaultModel;

        var modelInfo = Models.FirstOrDefault(m => m.ModelId == _currentModel);
        _currentDimensions = modelInfo?.Dimensions ?? 768;

        _autoPull = true;
        if (config.AdditionalConfig.TryGetValue("AutoPull", out var pull) && pull is bool p)
            _autoPull = p;

        _keepAlive = true;
        if (config.AdditionalConfig.TryGetValue("KeepAlive", out var keep) && keep is bool k)
            _keepAlive = k;

        // Allow custom dimensions for unknown models
        if (config.AdditionalConfig.TryGetValue("Dimensions", out var dims) && dims is int d)
            _currentDimensions = d;
    }

    /// <inheritdoc/>
    protected override async Task<float[]> GetEmbeddingCoreAsync(string text, CancellationToken ct)
    {
        var payload = new OllamaEmbeddingRequest
        {
            Model = _currentModel,
            Prompt = text
        };

        if (_keepAlive)
        {
            payload.KeepAlive = "5m"; // Keep model loaded for 5 minutes
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/embeddings");
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

            // Check if model needs to be pulled
            if (statusCode == 404 && errorContent.Contains("not found") && _autoPull)
            {
                await PullModelAsync(_currentModel, ct);
                // Retry after pulling
                return await GetEmbeddingCoreAsync(text, ct);
            }

            var isTransient = statusCode >= 500;

            throw new EmbeddingException($"Ollama API error: {response.StatusCode} - {errorContent}")
            {
                ProviderId = ProviderId,
                IsTransient = isTransient,
                HttpStatusCode = statusCode
            };
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
        if (result?.Embedding == null || result.Embedding.Length == 0)
            throw new EmbeddingException("Empty response from Ollama API") { ProviderId = ProviderId };

        // Update dimensions if different from expected
        if (result.Embedding.Length != _currentDimensions)
        {
            _currentDimensions = result.Embedding.Length;
        }

        return result.Embedding;
    }

    /// <inheritdoc/>
    protected override async Task<float[][]> GetEmbeddingsBatchCoreAsync(string[] texts, CancellationToken ct)
    {
        // Ollama doesn't support batch embeddings, so process sequentially
        var results = new float[texts.Length][];
        for (int i = 0; i < texts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = await GetEmbeddingCoreAsync(texts[i], ct);
        }
        return results;
    }

    /// <inheritdoc/>
    public override async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Check if Ollama is running
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/api/tags");
            using var response = await HttpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in OllamaEmbeddingProvider.cs");
            return false;
        }
    }

    /// <summary>
    /// Lists all locally available models.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListLocalModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/api/tags");
            using var response = await HttpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: ct);
            return result?.Models?.Select(m => m.Name).ToList() ?? new List<string>();
        }
        catch
        {
            Debug.WriteLine($"Caught exception in OllamaEmbeddingProvider.cs");
            return new List<string>();
        }
    }

    /// <summary>
    /// Pulls a model from the Ollama registry.
    /// </summary>
    public async Task PullModelAsync(string modelName, CancellationToken ct = default)
    {
        var payload = new { name = modelName };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/pull");
        request.Content = JsonContent.Create(payload);

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Read the streaming response to completion
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(line)) continue;

            // Parse progress updates (optional logging)
            try
            {
                using var progress = JsonDocument.Parse(line);
                if (progress.RootElement.TryGetProperty("status", out var status))
                {
                    var statusStr = status.GetString();
                    if (statusStr == "success")
                        break;
                }
            }
            catch { /* JSON parsing failure — continue polling */ }
        }
    }

    /// <summary>
    /// Checks if a specific model is available locally.
    /// </summary>
    public async Task<bool> IsModelAvailableAsync(string modelName, CancellationToken ct = default)
    {
        var models = await ListLocalModelsAsync(ct);
        return models.Any(m => m.StartsWith(modelName, StringComparison.OrdinalIgnoreCase));
    }

    // Request/Response models
    private sealed class OllamaEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";

        [JsonPropertyName("keep_alive")]
        public string? KeepAlive { get; set; }
    }

    private sealed class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel>? Models { get; set; }
    }

    private sealed class OllamaModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("modified_at")]
        public string ModifiedAt { get; set; } = "";
    }
}
