using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

/// <summary>
/// Hugging Face Inference API provider strategy.
/// Supports thousands of open-source models for text generation, embeddings, and more.
/// </summary>
public sealed class HuggingFaceProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api-inference.huggingface.co";
    private const string DefaultModel = "mistralai/Mistral-7B-Instruct-v0.2";
    private const string DefaultEmbeddingModel = "sentence-transformers/all-MiniLM-L6-v2";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-huggingface";

    /// <inheritdoc/>
    public override string StrategyName => "Hugging Face Provider";

    /// <inheritdoc/>
    public override string ProviderId => "huggingface";

    /// <inheritdoc/>
    public override string DisplayName => "Hugging Face";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion |
        AICapabilities.Embeddings | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Hugging Face",
        Description = "Access to thousands of open-source models via Hugging Face Inference API",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Hugging Face API token", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model ID", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Embedding model ID", Required = false, DefaultValue = DefaultEmbeddingModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase },
            new ConfigurationRequirement { Key = "InferenceEndpoint", Description = "Custom inference endpoint URL", Required = false }
        },
        CostTier = 2, // Free tier available
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "huggingface", "open-source", "mistral", "llama", "embeddings", "transformers" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public HuggingFaceProviderStrategy() : this(SharedHttpClient) { }

    public HuggingFaceProviderStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public override async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var model = request.Model ?? GetConfig("Model") ?? DefaultModel;
            var endpoint = GetConfig("InferenceEndpoint") ?? $"{GetConfig("ApiBase") ?? DefaultApiBase}/models/{model}";

            var prompt = BuildPrompt(request);
            var payload = new
            {
                inputs = prompt,
                parameters = new
                {
                    max_new_tokens = request.MaxTokens ?? 4096,
                    temperature = request.Temperature ?? 0.7f,
                    return_full_text = false
                }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<List<HuggingFaceTextGeneration>>(cancellationToken: ct);
            var generation = result?.FirstOrDefault();

            return new AIResponse
            {
                Success = true,
                Content = generation?.GeneratedText ?? string.Empty,
                FinishReason = "stop"
            };
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = GetRequiredConfig("ApiKey");
        var model = request.Model ?? GetConfig("Model") ?? DefaultModel;
        var endpoint = GetConfig("InferenceEndpoint") ?? $"{GetConfig("ApiBase") ?? DefaultApiBase}/models/{model}";

        var prompt = BuildPrompt(request);
        var payload = new
        {
            inputs = prompt,
            parameters = new
            {
                max_new_tokens = request.MaxTokens ?? 4096,
                temperature = request.Temperature ?? 0.7f,
                return_full_text = false
            },
            stream = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data:"))
                continue;

            var data = line.Substring(5).Trim();
            if (string.IsNullOrEmpty(data))
                continue;

            HuggingFaceStreamChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<HuggingFaceStreamChunk>(data);
            }
            catch { continue; }

            if (chunk?.Token?.Text != null)
            {
                yield return new AIStreamChunk { Content = chunk.Token.Text };
            }
            if (chunk?.Token?.Special == true)
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
            var apiKey = GetRequiredConfig("ApiKey");
            var model = GetConfig("EmbeddingModel") ?? DefaultEmbeddingModel;
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;

            var payload = new { inputs = text };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/pipeline/feature-extraction/{model}");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<float[][]>(cancellationToken: ct);
            RecordEmbeddings(1);

            // Return the first embedding (sentence-level)
            return result?.FirstOrDefault() ?? Array.Empty<float>();
        });
    }

    private static string BuildPrompt(AIRequest request)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(request.SystemMessage))
            parts.Add($"<|system|>\n{request.SystemMessage}\n<|end|>");

        foreach (var msg in request.ChatHistory)
            parts.Add($"<|{msg.Role}|>\n{msg.Content}\n<|end|>");

        if (!string.IsNullOrEmpty(request.Prompt))
            parts.Add($"<|user|>\n{request.Prompt}\n<|end|>\n<|assistant|>");

        return string.Join("\n", parts);
    }

    // Response models
    private sealed class HuggingFaceTextGeneration
    {
        public string? GeneratedText { get; set; }
    }

    private sealed class HuggingFaceStreamChunk
    {
        public HuggingFaceToken? Token { get; set; }
    }

    private sealed class HuggingFaceToken
    {
        public string? Text { get; set; }
        public bool Special { get; set; }
    }
}
