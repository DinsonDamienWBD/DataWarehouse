using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Factory for creating embedding provider instances from configuration.
///
/// Features:
/// - Configuration-based instantiation
/// - Support for all built-in providers
/// - Custom provider registration
/// - Connection validation on creation
/// - Provider pooling and reuse
/// </summary>
public sealed class EmbeddingProviderFactory : IDisposable
{
    private readonly EmbeddingProviderRegistry _registry;
    private readonly HttpClient _sharedHttpClient;
    private bool _disposed;

    /// <summary>
    /// Creates a new embedding provider factory.
    /// </summary>
    public EmbeddingProviderFactory()
        : this(EmbeddingProviderRegistry.Instance, null)
    {
    }

    /// <summary>
    /// Creates a new embedding provider factory with a custom registry.
    /// </summary>
    /// <param name="registry">The provider registry to use.</param>
    /// <param name="httpClient">Optional shared HTTP client.</param>
    public EmbeddingProviderFactory(EmbeddingProviderRegistry registry, HttpClient? httpClient = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sharedHttpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>
    /// Creates an embedding provider from configuration.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <returns>The created provider instance.</returns>
    public IEmbeddingProvider Create(EmbeddingProviderConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        // Determine provider ID from config
        var providerId = DetermineProviderId(config);

        return CreateByProviderId(providerId, config);
    }

    /// <summary>
    /// Creates an embedding provider by provider ID.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="config">Provider configuration.</param>
    /// <returns>The created provider instance.</returns>
    public IEmbeddingProvider CreateByProviderId(string providerId, EmbeddingProviderConfig config)
    {
        return providerId.ToLowerInvariant() switch
        {
            "openai" => new OpenAIEmbeddingProvider(config, _sharedHttpClient),
            "azure-openai" or "azureopenai" => CreateAzureOpenAI(config),
            "cohere" => new CohereEmbeddingProvider(config, _sharedHttpClient),
            "huggingface" or "hf" => new HuggingFaceEmbeddingProvider(config, _sharedHttpClient),
            "ollama" => new OllamaEmbeddingProvider(config, _sharedHttpClient),
            "onnx" => CreateOnnx(config),
            "voyageai" or "voyage" => new VoyageAIEmbeddingProvider(config, _sharedHttpClient),
            "jina" => new JinaEmbeddingProvider(config, _sharedHttpClient),
            _ => throw new ArgumentException($"Unknown provider: {providerId}")
        };
    }

    /// <summary>
    /// Creates an embedding provider from a JSON configuration string.
    /// </summary>
    /// <param name="jsonConfig">JSON configuration string.</param>
    /// <returns>The created provider instance.</returns>
    public IEmbeddingProvider CreateFromJson(string jsonConfig)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var factoryConfig = JsonSerializer.Deserialize<FactoryConfiguration>(jsonConfig, options)
            ?? throw new ArgumentException("Invalid JSON configuration");

        var providerConfig = new EmbeddingProviderConfig
        {
            ApiKey = factoryConfig.ApiKey,
            Endpoint = factoryConfig.Endpoint,
            Model = factoryConfig.Model,
            TimeoutSeconds = factoryConfig.TimeoutSeconds ?? 60,
            MaxRetries = factoryConfig.MaxRetries ?? 3,
            RetryDelayMs = factoryConfig.RetryDelayMs ?? 1000,
            MaxRetryDelayMs = factoryConfig.MaxRetryDelayMs ?? 30000,
            EnableLogging = factoryConfig.EnableLogging ?? false,
            AdditionalConfig = factoryConfig.AdditionalConfig ?? new()
        };

        var providerId = factoryConfig.Provider
            ?? DetermineProviderId(providerConfig);

        return CreateByProviderId(providerId, providerConfig);
    }

    /// <summary>
    /// Creates an embedding provider from a dictionary configuration.
    /// </summary>
    /// <param name="config">Dictionary configuration.</param>
    /// <returns>The created provider instance.</returns>
    public IEmbeddingProvider CreateFromDictionary(IDictionary<string, object> config)
    {
        var providerConfig = new EmbeddingProviderConfig
        {
            ApiKey = GetStringValue(config, "ApiKey"),
            Endpoint = GetStringValue(config, "Endpoint"),
            Model = GetStringValue(config, "Model"),
            TimeoutSeconds = GetIntValue(config, "TimeoutSeconds") ?? 60,
            MaxRetries = GetIntValue(config, "MaxRetries") ?? 3,
            RetryDelayMs = GetIntValue(config, "RetryDelayMs") ?? 1000,
            MaxRetryDelayMs = GetIntValue(config, "MaxRetryDelayMs") ?? 30000,
            EnableLogging = GetBoolValue(config, "EnableLogging") ?? false,
            AdditionalConfig = ExtractAdditionalConfig(config)
        };

        var providerId = GetStringValue(config, "Provider")
            ?? DetermineProviderId(providerConfig);

        return CreateByProviderId(providerId, providerConfig);
    }

    /// <summary>
    /// Creates an OpenAI embedding provider.
    /// </summary>
    /// <param name="apiKey">OpenAI API key.</param>
    /// <param name="model">Model name (default: text-embedding-3-small).</param>
    /// <param name="organization">Optional organization ID.</param>
    /// <returns>The created provider instance.</returns>
    public OpenAIEmbeddingProvider CreateOpenAI(
        string apiKey,
        string? model = null,
        string? organization = null)
    {
        var config = new EmbeddingProviderConfig
        {
            ApiKey = apiKey,
            Model = model,
            AdditionalConfig = organization != null
                ? new() { ["Organization"] = organization }
                : new()
        };
        return new OpenAIEmbeddingProvider(config, _sharedHttpClient);
    }

    /// <summary>
    /// Creates an Azure OpenAI embedding provider.
    /// </summary>
    /// <param name="endpoint">Azure OpenAI endpoint URL.</param>
    /// <param name="apiKey">Azure OpenAI API key.</param>
    /// <param name="deploymentName">Deployment name.</param>
    /// <param name="model">Model name for cost estimation.</param>
    /// <returns>The created provider instance.</returns>
    public AzureOpenAIEmbeddingProvider CreateAzureOpenAI(
        string endpoint,
        string apiKey,
        string deploymentName,
        string? model = null)
    {
        var config = new EmbeddingProviderConfig
        {
            ApiKey = apiKey,
            Endpoint = endpoint,
            Model = model
        };
        return new AzureOpenAIEmbeddingProvider(config, deploymentName, _sharedHttpClient);
    }

    /// <summary>
    /// Creates a Cohere embedding provider.
    /// </summary>
    /// <param name="apiKey">Cohere API key.</param>
    /// <param name="model">Model name (default: embed-english-v3.0).</param>
    /// <param name="inputType">Input type for optimization.</param>
    /// <returns>The created provider instance.</returns>
    public CohereEmbeddingProvider CreateCohere(
        string apiKey,
        string? model = null,
        CohereInputType inputType = CohereInputType.SearchDocument)
    {
        var config = new EmbeddingProviderConfig
        {
            ApiKey = apiKey,
            Model = model,
            AdditionalConfig = new() { ["InputType"] = inputType.ToString() }
        };
        return new CohereEmbeddingProvider(config, _sharedHttpClient);
    }

    /// <summary>
    /// Creates a HuggingFace embedding provider.
    /// </summary>
    /// <param name="apiToken">HuggingFace API token.</param>
    /// <param name="model">Model name (default: sentence-transformers/all-MiniLM-L6-v2).</param>
    /// <returns>The created provider instance.</returns>
    public HuggingFaceEmbeddingProvider CreateHuggingFace(
        string apiToken,
        string? model = null)
    {
        var config = new EmbeddingProviderConfig
        {
            ApiKey = apiToken,
            Model = model
        };
        return new HuggingFaceEmbeddingProvider(config, _sharedHttpClient);
    }

    /// <summary>
    /// Creates an Ollama embedding provider.
    /// </summary>
    /// <param name="endpoint">Ollama endpoint (default: http://localhost:11434).</param>
    /// <param name="model">Model name (default: nomic-embed-text).</param>
    /// <returns>The created provider instance.</returns>
    public OllamaEmbeddingProvider CreateOllama(
        string? endpoint = null,
        string? model = null)
    {
        var config = new EmbeddingProviderConfig
        {
            Endpoint = endpoint,
            Model = model
        };
        return new OllamaEmbeddingProvider(config, _sharedHttpClient);
    }

    /// <summary>
    /// Creates an ONNX embedding provider.
    /// </summary>
    /// <param name="modelPath">Path to the .onnx model file.</param>
    /// <param name="tokenizerPath">Optional path to tokenizer files.</param>
    /// <param name="useGpu">Whether to use GPU acceleration.</param>
    /// <returns>The created provider instance.</returns>
    public ONNXEmbeddingProvider CreateOnnx(
        string modelPath,
        string? tokenizerPath = null,
        bool useGpu = false)
    {
        var config = new EmbeddingProviderConfig
        {
            AdditionalConfig = new()
            {
                ["UseGpu"] = useGpu
            }
        };
        return new ONNXEmbeddingProvider(config, modelPath, tokenizerPath, _sharedHttpClient);
    }

    /// <summary>
    /// Creates a Voyage AI embedding provider.
    /// </summary>
    /// <param name="apiKey">Voyage AI API key.</param>
    /// <param name="model">Model name (default: voyage-large-2).</param>
    /// <param name="inputType">Input type for optimization.</param>
    /// <returns>The created provider instance.</returns>
    public VoyageAIEmbeddingProvider CreateVoyageAI(
        string apiKey,
        string? model = null,
        VoyageInputType inputType = VoyageInputType.Document)
    {
        var config = new EmbeddingProviderConfig
        {
            ApiKey = apiKey,
            Model = model,
            AdditionalConfig = new() { ["InputType"] = inputType.ToString() }
        };
        return new VoyageAIEmbeddingProvider(config, _sharedHttpClient);
    }

    /// <summary>
    /// Creates a Jina AI embedding provider.
    /// </summary>
    /// <param name="apiKey">Jina AI API key.</param>
    /// <param name="model">Model name (default: jina-embeddings-v2-base-en).</param>
    /// <param name="taskType">Task type for optimization.</param>
    /// <returns>The created provider instance.</returns>
    public JinaEmbeddingProvider CreateJina(
        string apiKey,
        string? model = null,
        JinaTaskType taskType = JinaTaskType.Retrieval)
    {
        var config = new EmbeddingProviderConfig
        {
            ApiKey = apiKey,
            Model = model,
            AdditionalConfig = new() { ["TaskType"] = taskType.ToString() }
        };
        return new JinaEmbeddingProvider(config, _sharedHttpClient);
    }

    /// <summary>
    /// Creates a provider and validates its connection.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="config">Provider configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validated provider instance.</returns>
    /// <exception cref="EmbeddingException">Thrown if validation fails.</exception>
    public async Task<IEmbeddingProvider> CreateAndValidateAsync(
        string providerId,
        EmbeddingProviderConfig config,
        CancellationToken ct = default)
    {
        var provider = CreateByProviderId(providerId, config);

        if (!await provider.ValidateConnectionAsync(ct))
        {
            provider.Dispose();
            throw new EmbeddingException($"Failed to validate connection to {providerId} provider")
            {
                ProviderId = providerId
            };
        }

        return provider;
    }

    private AzureOpenAIEmbeddingProvider CreateAzureOpenAI(EmbeddingProviderConfig config)
    {
        var deploymentName = config.AdditionalConfig.TryGetValue("DeploymentName", out var dn)
            ? dn?.ToString() ?? throw new ArgumentException("DeploymentName is required for Azure OpenAI")
            : throw new ArgumentException("DeploymentName is required for Azure OpenAI");

        return new AzureOpenAIEmbeddingProvider(config, deploymentName, _sharedHttpClient);
    }

    private ONNXEmbeddingProvider CreateOnnx(EmbeddingProviderConfig config)
    {
        var modelPath = config.AdditionalConfig.TryGetValue("ModelPath", out var mp)
            ? mp?.ToString() ?? throw new ArgumentException("ModelPath is required for ONNX")
            : throw new ArgumentException("ModelPath is required for ONNX");

        var tokenizerPath = config.AdditionalConfig.TryGetValue("TokenizerPath", out var tp)
            ? tp?.ToString()
            : null;

        return new ONNXEmbeddingProvider(config, modelPath, tokenizerPath, _sharedHttpClient);
    }

    private static string DetermineProviderId(EmbeddingProviderConfig config)
    {
        // Try to determine provider from endpoint
        if (!string.IsNullOrEmpty(config.Endpoint))
        {
            var endpoint = config.Endpoint.ToLowerInvariant();

            if (endpoint.Contains("openai.azure.com"))
                return "azure-openai";
            if (endpoint.Contains("openai.com"))
                return "openai";
            if (endpoint.Contains("cohere"))
                return "cohere";
            if (endpoint.Contains("huggingface.co"))
                return "huggingface";
            if (endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1"))
            {
                if (endpoint.Contains("11434"))
                    return "ollama";
            }
            if (endpoint.Contains("voyageai"))
                return "voyageai";
            if (endpoint.Contains("jina"))
                return "jina";
        }

        // Default to OpenAI if API key is provided
        if (!string.IsNullOrEmpty(config.ApiKey))
            return "openai";

        // Default to Ollama for local usage
        return "ollama";
    }

    private static string? GetStringValue(IDictionary<string, object> config, string key)
    {
        return config.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int? GetIntValue(IDictionary<string, object> config, string key)
    {
        if (config.TryGetValue(key, out var value))
        {
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (int.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? GetBoolValue(IDictionary<string, object> config, string key)
    {
        if (config.TryGetValue(key, out var value))
        {
            if (value is bool b) return b;
            if (bool.TryParse(value?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static Dictionary<string, object> ExtractAdditionalConfig(IDictionary<string, object> config)
    {
        var standardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Provider", "ApiKey", "Endpoint", "Model", "TimeoutSeconds",
            "MaxRetries", "RetryDelayMs", "MaxRetryDelayMs", "EnableLogging"
        };

        return config
            .Where(kvp => !standardKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sharedHttpClient.Dispose();
    }

    private sealed class FactoryConfiguration
    {
        public string? Provider { get; set; }
        public string? ApiKey { get; set; }
        public string? Endpoint { get; set; }
        public string? Model { get; set; }
        public int? TimeoutSeconds { get; set; }
        public int? MaxRetries { get; set; }
        public int? RetryDelayMs { get; set; }
        public int? MaxRetryDelayMs { get; set; }
        public bool? EnableLogging { get; set; }
        public Dictionary<string, object>? AdditionalConfig { get; set; }
    }
}
