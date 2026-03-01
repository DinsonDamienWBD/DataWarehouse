using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

/// <summary>
/// OpenAI API provider strategy.
/// Supports GPT-4, GPT-3.5-turbo, text-embedding-ada-002, and DALL-E models.
/// </summary>
public sealed class OpenAiProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.openai.com/v1";
    private const string DefaultModel = "gpt-4-turbo-preview";
    private const string DefaultEmbeddingModel = "text-embedding-3-small";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-openai";

    /// <inheritdoc/>
    public override string StrategyName => "OpenAI Provider";

    /// <inheritdoc/>
    public override string ProviderId => "openai";

    /// <inheritdoc/>
    public override string DisplayName => "OpenAI";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.ImageGeneration | AICapabilities.FunctionCalling |
        AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "OpenAI",
        Description = "OpenAI GPT models for text completion, chat, embeddings, and image generation",
        Capabilities = IntelligenceCapabilities.AllAIProvider | IntelligenceCapabilities.ImageGeneration,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "OpenAI API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Organization", Description = "OpenAI organization ID", Required = false },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Model for embeddings", Required = false, DefaultValue = DefaultEmbeddingModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 4,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "openai", "gpt", "gpt-4", "chatgpt", "embeddings", "dalle" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public OpenAiProviderStrategy() : this(SharedHttpClient) { }

    public OpenAiProviderStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public override async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
            var model = request.Model ?? GetConfig("Model") ?? DefaultModel;

            var messages = BuildMessages(request);
            var payload = new
            {
                model,
                messages,
                max_tokens = request.MaxTokens ?? 4096,
                temperature = request.Temperature ?? 0.7f,
                functions = request.Functions?.Select(f => new
                {
                    name = f.Name,
                    description = f.Description,
                    parameters = JsonSerializer.Deserialize<JsonElement>(f.ParametersSchema)
                }).ToArray()
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            if (GetConfig("Organization") is string org)
                httpRequest.Headers.Add("OpenAI-Organization", org);
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken: ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (result?.Usage != null)
                RecordTokens(result.Usage.TotalTokens);

            return new AIResponse
            {
                Success = true,
                Content = choice?.Message?.Content ?? string.Empty,
                FinishReason = choice?.FinishReason,
                FunctionCall = choice?.Message?.FunctionCall != null
                    ? new AIFunctionCall { Name = choice.Message.FunctionCall.Name, Arguments = choice.Message.FunctionCall.Arguments }
                    : null,
                Usage = result?.Usage != null
                    ? new AIUsage { PromptTokens = result.Usage.PromptTokens, CompletionTokens = result.Usage.CompletionTokens }
                    : null
            };
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = GetRequiredConfig("ApiKey");
        var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
        var model = request.Model ?? GetConfig("Model") ?? DefaultModel;

        var messages = BuildMessages(request);
        var payload = new
        {
            model,
            messages,
            max_tokens = request.MaxTokens ?? 4096,
            temperature = request.Temperature ?? 0.7f,
            stream = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions");
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        if (GetConfig("Organization") is string org)
            httpRequest.Headers.Add("OpenAI-Organization", org);
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var data = line.Substring(6);
            if (data == "[DONE]")
            {
                yield return new AIStreamChunk { IsFinal = true, FinishReason = "stop" };
                break;
            }

            var chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(data);
            var delta = chunk?.Choices?.FirstOrDefault()?.Delta;
            if (delta?.Content != null)
            {
                yield return new AIStreamChunk { Content = delta.Content };
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
            var model = GetConfig("EmbeddingModel") ?? DefaultEmbeddingModel;

            var payload = new { model, input = text };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/embeddings");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result?.Data?.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
        });
    }

    private static List<object> BuildMessages(AIRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemMessage))
            messages.Add(new { role = "system", content = request.SystemMessage });

        foreach (var msg in request.ChatHistory)
            messages.Add(new { role = msg.Role, content = msg.Content, name = msg.FunctionName });

        if (!string.IsNullOrEmpty(request.Prompt))
            messages.Add(new { role = "user", content = request.Prompt });

        return messages;
    }

    // Response models
    private sealed class OpenAiChatResponse
    {
        public List<OpenAiChoice>? Choices { get; set; }
        public OpenAiUsage? Usage { get; set; }
    }

    private sealed class OpenAiChoice
    {
        public OpenAiMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class OpenAiMessage
    {
        public string? Content { get; set; }
        public OpenAiFunctionCall? FunctionCall { get; set; }
    }

    private sealed class OpenAiFunctionCall
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "{}";
    }

    private sealed class OpenAiUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class OpenAiStreamChunk
    {
        public List<OpenAiStreamChoice>? Choices { get; set; }
    }

    private sealed class OpenAiStreamChoice
    {
        public OpenAiStreamDelta? Delta { get; set; }
    }

    private sealed class OpenAiStreamDelta
    {
        public string? Content { get; set; }
    }

    private sealed class OpenAiEmbeddingResponse
    {
        public List<OpenAiEmbeddingData>? Data { get; set; }
    }

    private sealed class OpenAiEmbeddingData
    {
        public float[]? Embedding { get; set; }
    }
}
