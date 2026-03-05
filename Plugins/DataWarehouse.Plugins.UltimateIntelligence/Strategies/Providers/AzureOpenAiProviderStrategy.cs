using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

/// <summary>
/// Azure OpenAI Service provider strategy.
/// Provides enterprise-grade OpenAI models with Azure compliance and security.
/// </summary>
public sealed class AzureOpenAiProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiVersion = "2024-02-15-preview";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-azure-openai";

    /// <inheritdoc/>
    public override string StrategyName => "Azure OpenAI Provider";

    /// <inheritdoc/>
    public override string ProviderId => "azure-openai";

    /// <inheritdoc/>
    public override string DisplayName => "Azure OpenAI";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.ImageGeneration | AICapabilities.FunctionCalling |
        AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Azure OpenAI",
        Description = "Azure-hosted OpenAI models with enterprise compliance, security, and SLA guarantees",
        Capabilities = IntelligenceCapabilities.AllAIProvider | IntelligenceCapabilities.ImageGeneration,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Azure OpenAI API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Endpoint", Description = "Azure OpenAI endpoint URL", Required = true },
            new ConfigurationRequirement { Key = "DeploymentName", Description = "Model deployment name", Required = true },
            new ConfigurationRequirement { Key = "EmbeddingDeployment", Description = "Embedding model deployment name", Required = false },
            new ConfigurationRequirement { Key = "ApiVersion", Description = "API version", Required = false, DefaultValue = DefaultApiVersion }
        },
        CostTier = 4,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "azure", "openai", "enterprise", "compliant", "gpt", "embeddings" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public AzureOpenAiProviderStrategy() : this(SharedHttpClient) { }

    public AzureOpenAiProviderStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public override async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var endpoint = GetRequiredConfig("Endpoint").TrimEnd('/');
            var deployment = request.Model ?? GetRequiredConfig("DeploymentName");
            var apiVersion = GetConfig("ApiVersion") ?? DefaultApiVersion;

            var messages = BuildMessages(request);
            var payload = new
            {
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

            var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("api-key", apiKey);
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AzureOpenAiResponse>(cancellationToken: ct);
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
        var endpoint = GetRequiredConfig("Endpoint").TrimEnd('/');
        var deployment = request.Model ?? GetRequiredConfig("DeploymentName");
        var apiVersion = GetConfig("ApiVersion") ?? DefaultApiVersion;

        var messages = BuildMessages(request);
        var payload = new
        {
            messages,
            max_tokens = request.MaxTokens ?? 4096,
            temperature = request.Temperature ?? 0.7f,
            stream = true
        };

        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("api-key", apiKey);
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

            var chunk = JsonSerializer.Deserialize<AzureOpenAiStreamChunk>(data);
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
            var endpoint = GetRequiredConfig("Endpoint").TrimEnd('/');
            var deployment = GetConfig("EmbeddingDeployment") ?? GetRequiredConfig("DeploymentName");
            var apiVersion = GetConfig("ApiVersion") ?? DefaultApiVersion;

            var payload = new { input = text };

            var url = $"{endpoint}/openai/deployments/{deployment}/embeddings?api-version={apiVersion}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("api-key", apiKey);
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AzureOpenAiEmbeddingResponse>(cancellationToken: ct);
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
            messages.Add(new { role = msg.Role, content = msg.Content });

        if (!string.IsNullOrEmpty(request.Prompt))
            messages.Add(new { role = "user", content = request.Prompt });

        return messages;
    }

    // Response models (same structure as OpenAI)
    private sealed class AzureOpenAiResponse
    {
        public List<AzureChoice>? Choices { get; set; }
        public AzureUsage? Usage { get; set; }
    }

    private sealed class AzureChoice
    {
        public AzureMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class AzureMessage
    {
        public string? Content { get; set; }
        public AzureFunctionCall? FunctionCall { get; set; }
    }

    private sealed class AzureFunctionCall
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "{}";
    }

    private sealed class AzureUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class AzureOpenAiStreamChunk
    {
        public List<AzureStreamChoice>? Choices { get; set; }
    }

    private sealed class AzureStreamChoice
    {
        public AzureStreamDelta? Delta { get; set; }
    }

    private sealed class AzureStreamDelta
    {
        public string? Content { get; set; }
    }

    private sealed class AzureOpenAiEmbeddingResponse
    {
        public List<AzureEmbeddingData>? Data { get; set; }
    }

    private sealed class AzureEmbeddingData
    {
        public float[]? Embedding { get; set; }
    }
}
