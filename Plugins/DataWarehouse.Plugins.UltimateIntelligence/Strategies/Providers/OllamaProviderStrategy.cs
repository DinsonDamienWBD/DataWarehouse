using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

/// <summary>
/// Ollama local LLM provider strategy.
/// Supports running local models like Llama 2, Mistral, CodeLlama, and many others.
/// </summary>
public sealed class OllamaProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "http://localhost:11434";
    private const string DefaultModel = "llama3.2";
    private const string DefaultEmbeddingModel = "nomic-embed-text";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-ollama";

    /// <inheritdoc/>
    public override string StrategyName => "Ollama Provider";

    /// <inheritdoc/>
    public override string ProviderId => "ollama";

    /// <inheritdoc/>
    public override string DisplayName => "Ollama (Local)";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Ollama",
        Description = "Local LLM inference using Ollama - supports Llama, Mistral, CodeLlama, and many more",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming | IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiBase", Description = "Ollama API base URL", Required = false, DefaultValue = DefaultApiBase },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Model for embeddings", Required = false, DefaultValue = DefaultEmbeddingModel }
        },
        CostTier = 1, // Free - runs locally
        LatencyTier = 3, // Depends on hardware
        RequiresNetworkAccess = false, // Runs locally
        SupportsOfflineMode = true,
        Tags = new[] { "ollama", "local", "llama", "mistral", "codellama", "offline", "self-hosted" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public OllamaProviderStrategy() : this(SharedHttpClient) { }

    public OllamaProviderStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public override async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
            var model = request.Model ?? GetConfig("Model") ?? DefaultModel;

            var messages = BuildMessages(request);
            var payload = new
            {
                model,
                messages,
                stream = false,
                options = new
                {
                    num_predict = request.MaxTokens ?? 4096,
                    temperature = request.Temperature ?? 0.7f
                }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/api/chat");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: ct);

            if (result != null)
            {
                RecordTokens(result.PromptEvalCount + result.EvalCount);
            }

            return new AIResponse
            {
                Success = true,
                Content = result?.Message?.Content ?? string.Empty,
                FinishReason = result?.Done == true ? "stop" : "length",
                Usage = result != null
                    ? new AIUsage { PromptTokens = result.PromptEvalCount, CompletionTokens = result.EvalCount }
                    : null
            };
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
        var model = request.Model ?? GetConfig("Model") ?? DefaultModel;

        var messages = BuildMessages(request);
        var payload = new
        {
            model,
            messages,
            stream = true,
            options = new
            {
                num_predict = request.MaxTokens ?? 4096,
                temperature = request.Temperature ?? 0.7f
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/api/chat");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line))
                continue;

            var chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line);
            if (chunk?.Message?.Content != null)
            {
                yield return new AIStreamChunk { Content = chunk.Message.Content };
            }

            if (chunk?.Done == true)
            {
                yield return new AIStreamChunk { IsFinal = true, FinishReason = "stop" };
                break;
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
            var model = GetConfig("EmbeddingModel") ?? DefaultEmbeddingModel;

            var payload = new { model, prompt = text };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/api/embeddings");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result?.Embedding ?? Array.Empty<float>();
        });
    }

    private static List<object> BuildMessages(AIRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemMessage))
            messages.Add(new { role = "system", content = request.SystemMessage });

        foreach (var msg in request.ChatHistory)
            messages.Add(new { role = msg.Role, content = msg.Content });

        if (!string.IsNullOrEmpty(request.Prompt))
            messages.Add(new { role = "user", content = request.Prompt });

        return messages;
    }

    // Response models
    private sealed class OllamaChatResponse
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
        public int PromptEvalCount { get; set; }
        public int EvalCount { get; set; }
    }

    private sealed class OllamaMessage
    {
        public string? Content { get; set; }
    }

    private sealed class OllamaStreamChunk
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
    }

    private sealed class OllamaEmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}
