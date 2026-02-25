using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

/// <summary>
/// Anthropic Claude API provider strategy.
/// Supports Claude 3 Opus, Claude 3 Sonnet, Claude 3 Haiku, and legacy Claude 2 models.
/// </summary>
public sealed class ClaudeProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.anthropic.com/v1";
    private const string DefaultModel = "claude-3-5-sonnet-20241022";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-claude";

    /// <inheritdoc/>
    public override string StrategyName => "Claude Provider";

    /// <inheritdoc/>
    public override string ProviderId => "anthropic";

    /// <inheritdoc/>
    public override string DisplayName => "Anthropic Claude";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.ImageAnalysis | AICapabilities.FunctionCalling | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Anthropic",
        Description = "Anthropic Claude models for advanced reasoning, code generation, and analysis",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming | IntelligenceCapabilities.FunctionCalling,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Anthropic API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 4,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "anthropic", "claude", "claude-3", "opus", "sonnet", "haiku" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public ClaudeProviderStrategy() : this(SharedHttpClient) { }

    public ClaudeProviderStrategy(HttpClient httpClient)
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
                system = request.SystemMessage,
                tools = request.Functions?.Select(f => new
                {
                    name = f.Name,
                    description = f.Description,
                    input_schema = JsonSerializer.Deserialize<JsonElement>(f.ParametersSchema)
                }).ToArray()
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/messages");
            httpRequest.Headers.Add("x-api-key", apiKey);
            httpRequest.Headers.Add("anthropic-version", ApiVersion);
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(cancellationToken: ct);

            if (result?.Usage != null)
                RecordTokens(result.Usage.InputTokens + result.Usage.OutputTokens);

            var textContent = result?.Content?.FirstOrDefault(c => c.Type == "text");
            var toolUse = result?.Content?.FirstOrDefault(c => c.Type == "tool_use");

            return new AIResponse
            {
                Success = true,
                Content = textContent?.Text ?? string.Empty,
                FinishReason = result?.StopReason,
                FunctionCall = toolUse != null
                    ? new AIFunctionCall { Name = toolUse.Name ?? "", Arguments = JsonSerializer.Serialize(toolUse.Input) }
                    : null,
                Usage = result?.Usage != null
                    ? new AIUsage { PromptTokens = result.Usage.InputTokens, CompletionTokens = result.Usage.OutputTokens }
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
            system = request.SystemMessage,
            stream = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/messages");
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", ApiVersion);
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
            var eventData = JsonSerializer.Deserialize<ClaudeStreamEvent>(data);

            if (eventData?.Type == "content_block_delta" && eventData.Delta?.Type == "text_delta")
            {
                yield return new AIStreamChunk { Content = eventData.Delta.Text ?? "" };
            }
            else if (eventData?.Type == "message_stop")
            {
                yield return new AIStreamChunk { IsFinal = true, FinishReason = "stop" };
                break;
            }
        }
    }

    /// <inheritdoc/>
    public override Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        // Claude doesn't have a native embeddings API
        // This would need to use a different provider or Anthropic's Voyage partnership
        throw new NotSupportedException("Claude does not provide a native embeddings API. Use OpenAI or a dedicated embedding provider.");
    }

    private static List<object> BuildMessages(AIRequest request)
    {
        var messages = new List<object>();

        foreach (var msg in request.ChatHistory)
        {
            messages.Add(new { role = msg.Role == "assistant" ? "assistant" : "user", content = msg.Content });
        }

        if (!string.IsNullOrEmpty(request.Prompt))
            messages.Add(new { role = "user", content = request.Prompt });

        return messages;
    }

    // Response models
    private sealed class ClaudeResponse
    {
        public List<ClaudeContentBlock>? Content { get; set; }
        public string? StopReason { get; set; }
        public ClaudeUsage? Usage { get; set; }
    }

    private sealed class ClaudeContentBlock
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }
        public string? Name { get; set; }
        public JsonElement? Input { get; set; }
    }

    private sealed class ClaudeUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    private sealed class ClaudeStreamEvent
    {
        public string? Type { get; set; }
        public ClaudeStreamDelta? Delta { get; set; }
    }

    private sealed class ClaudeStreamDelta
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }
}
