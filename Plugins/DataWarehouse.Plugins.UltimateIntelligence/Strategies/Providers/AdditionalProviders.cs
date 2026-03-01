using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

#region Gemini Provider

/// <summary>
/// Google Gemini API provider strategy.
/// Supports Gemini Pro, Gemini Pro Vision, and Gemini Ultra models.
/// </summary>
public sealed class GeminiProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://generativelanguage.googleapis.com/v1beta";
    private const string DefaultModel = "gemini-pro";
    private const string DefaultEmbeddingModel = "embedding-001";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-gemini";

    /// <inheritdoc/>
    public override string StrategyName => "Google Gemini Provider";

    /// <inheritdoc/>
    public override string ProviderId => "gemini";

    /// <inheritdoc/>
    public override string DisplayName => "Google Gemini";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.ImageAnalysis | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Google Gemini",
        Description = "Google Gemini models for multimodal AI including chat, vision, and embeddings",
        Capabilities = IntelligenceCapabilities.AllAIProvider,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Google AI API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Model for embeddings", Required = false, DefaultValue = DefaultEmbeddingModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "google", "gemini", "gemini-pro", "gemini-ultra", "bard", "vision", "multimodal" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public GeminiProviderStrategy() : this(SharedHttpClient) { }

    public GeminiProviderStrategy(HttpClient httpClient)
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

            var contents = BuildContents(request);
            var payload = new
            {
                contents,
                generationConfig = new
                {
                    temperature = request.Temperature ?? 0.7f,
                    maxOutputTokens = request.MaxTokens ?? 4096,
                }
            };

            // Finding 3200: Use x-goog-api-key header instead of URL query parameter to prevent
            // API key from appearing in proxy/server access logs.
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{apiBase}/models/{model}:generateContent");
            httpRequest.Headers.Add("x-goog-api-key", apiKey);
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
            var candidate = result?.Candidates?.FirstOrDefault();
            var textPart = candidate?.Content?.Parts?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Text));

            // Gemini uses prompt token count from response
            if (result?.UsageMetadata != null)
                RecordTokens(result.UsageMetadata.TotalTokenCount);

            return new AIResponse
            {
                Success = true,
                Content = textPart?.Text ?? string.Empty,
                FinishReason = candidate?.FinishReason?.ToLowerInvariant(),
                Usage = result?.UsageMetadata != null
                    ? new AIUsage { PromptTokens = result.UsageMetadata.PromptTokenCount, CompletionTokens = result.UsageMetadata.CandidatesTokenCount }
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

        var contents = BuildContents(request);
        var payload = new
        {
            contents,
            generationConfig = new
            {
                temperature = request.Temperature ?? 0.7f,
                maxOutputTokens = request.MaxTokens ?? 4096,
            }
        };

        // Finding 3200: Use x-goog-api-key header for streaming too.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            $"{apiBase}/models/{model}:streamGenerateContent");
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var chunk = JsonSerializer.Deserialize<GeminiResponse>(line);
            var candidate = chunk?.Candidates?.FirstOrDefault();
            var textPart = candidate?.Content?.Parts?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Text));

            if (textPart?.Text != null)
            {
                yield return new AIStreamChunk { Content = textPart.Text };
            }

            if (candidate?.FinishReason != null)
            {
                yield return new AIStreamChunk { IsFinal = true, FinishReason = candidate.FinishReason.ToLowerInvariant() };
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
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
            var model = GetConfig("EmbeddingModel") ?? DefaultEmbeddingModel;

            var payload = new { content = new { parts = new[] { new { text } } } };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{apiBase}/models/{model}:embedContent?key={apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GeminiEmbeddingResponse>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result?.Embedding?.Values ?? Array.Empty<float>();
        });
    }

    private static List<object> BuildContents(AIRequest request)
    {
        var contents = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemMessage))
        {
            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = $"[System]: {request.SystemMessage}" } }
            });
        }

        foreach (var msg in request.ChatHistory)
        {
            contents.Add(new
            {
                role = msg.Role == "assistant" ? "model" : "user",
                parts = new[] { new { text = msg.Content } }
            });
        }

        if (!string.IsNullOrEmpty(request.Prompt))
        {
            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = request.Prompt } }
            });
        }

        return contents;
    }

    // Response models
    private sealed class GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
        public GeminiUsageMetadata? UsageMetadata { get; set; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class GeminiContent
    {
        public List<GeminiPart>? Parts { get; set; }
    }

    private sealed class GeminiPart
    {
        public string? Text { get; set; }
    }

    private sealed class GeminiUsageMetadata
    {
        public int PromptTokenCount { get; set; }
        public int CandidatesTokenCount { get; set; }
        public int TotalTokenCount { get; set; }
    }

    private sealed class GeminiEmbeddingResponse
    {
        public GeminiEmbedding? Embedding { get; set; }
    }

    private sealed class GeminiEmbedding
    {
        public float[]? Values { get; set; }
    }
}

#endregion

#region Mistral Provider

/// <summary>
/// Mistral AI provider strategy.
/// Supports Mistral Tiny, Small, Medium, and Large models.
/// </summary>
public sealed class MistralProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.mistral.ai/v1";
    private const string DefaultModel = "mistral-medium";
    private const string DefaultEmbeddingModel = "mistral-embed";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-mistral";

    /// <inheritdoc/>
    public override string StrategyName => "Mistral AI Provider";

    /// <inheritdoc/>
    public override string ProviderId => "mistral";

    /// <inheritdoc/>
    public override string DisplayName => "Mistral AI";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.FunctionCalling | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Mistral AI",
        Description = "Mistral AI models for efficient and powerful language understanding",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming | IntelligenceCapabilities.Embeddings | IntelligenceCapabilities.FunctionCalling,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Mistral AI API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Model for embeddings", Required = false, DefaultValue = DefaultEmbeddingModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "mistral", "mistral-ai", "european-ai", "mixtral" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public MistralProviderStrategy() : this(SharedHttpClient) { }

    public MistralProviderStrategy(HttpClient httpClient)
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
                tools = request.Functions?.Select(f => new
                {
                    type = "function",
                    function = new
                    {
                        name = f.Name,
                        description = f.Description,
                        parameters = JsonSerializer.Deserialize<JsonElement>(f.ParametersSchema)
                    }
                }).ToArray()
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MistralChatResponse>(cancellationToken: ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (result?.Usage != null)
                RecordTokens(result.Usage.TotalTokens);

            return new AIResponse
            {
                Success = true,
                Content = choice?.Message?.Content ?? string.Empty,
                FinishReason = choice?.FinishReason,
                FunctionCall = choice?.Message?.ToolCalls?.FirstOrDefault()?.Function != null
                    ? new AIFunctionCall
                    {
                        Name = choice.Message.ToolCalls[0].Function.Name,
                        Arguments = choice.Message.ToolCalls[0].Function.Arguments
                    }
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

            var chunk = JsonSerializer.Deserialize<MistralStreamChunk>(data);
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

            var payload = new { model, input = new[] { text } };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/embeddings");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MistralEmbeddingResponse>(cancellationToken: ct);
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

    // Response models
    private sealed class MistralChatResponse
    {
        public List<MistralChoice>? Choices { get; set; }
        public MistralUsage? Usage { get; set; }
    }

    private sealed class MistralChoice
    {
        public MistralMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class MistralMessage
    {
        public string? Content { get; set; }
        public List<MistralToolCall>? ToolCalls { get; set; }
    }

    private sealed class MistralToolCall
    {
        public MistralFunction Function { get; set; } = new();
    }

    private sealed class MistralFunction
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "{}";
    }

    private sealed class MistralUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class MistralStreamChunk
    {
        public List<MistralStreamChoice>? Choices { get; set; }
    }

    private sealed class MistralStreamChoice
    {
        public MistralStreamDelta? Delta { get; set; }
    }

    private sealed class MistralStreamDelta
    {
        public string? Content { get; set; }
    }

    private sealed class MistralEmbeddingResponse
    {
        public List<MistralEmbeddingData>? Data { get; set; }
    }

    private sealed class MistralEmbeddingData
    {
        public float[]? Embedding { get; set; }
    }
}

#endregion

#region Cohere Provider

/// <summary>
/// Cohere API provider strategy.
/// Supports Command, Command Light, Command R models with chat, embeddings, and rerank.
/// </summary>
public sealed class CohereProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.cohere.ai/v1";
    private const string DefaultModel = "command-r-plus";
    private const string DefaultEmbeddingModel = "embed-english-v3.0";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-cohere";

    /// <inheritdoc/>
    public override string StrategyName => "Cohere Provider";

    /// <inheritdoc/>
    public override string ProviderId => "cohere";

    /// <inheritdoc/>
    public override string DisplayName => "Cohere";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Cohere",
        Description = "Cohere models for enterprise-grade language AI with retrieval and reranking",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming | IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Cohere API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Model for embeddings", Required = false, DefaultValue = DefaultEmbeddingModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "cohere", "command", "enterprise-ai", "rerank", "rag" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public CohereProviderStrategy() : this(SharedHttpClient) { }

    public CohereProviderStrategy(HttpClient httpClient)
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

            var chatHistory = BuildChatHistory(request);
            var payload = new
            {
                model,
                message = request.Prompt ?? "",
                chat_history = chatHistory,
                preamble = request.SystemMessage,
                max_tokens = request.MaxTokens ?? 4096,
                temperature = request.Temperature ?? 0.7f,
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CohereChatResponse>(cancellationToken: ct);

            if (result?.Meta?.Tokens != null)
                RecordTokens(result.Meta.Tokens.InputTokens + result.Meta.Tokens.OutputTokens);

            return new AIResponse
            {
                Success = true,
                Content = result?.Text ?? string.Empty,
                FinishReason = result?.FinishReason,
                Usage = result?.Meta?.Tokens != null
                    ? new AIUsage { PromptTokens = result.Meta.Tokens.InputTokens, CompletionTokens = result.Meta.Tokens.OutputTokens }
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

        var chatHistory = BuildChatHistory(request);
        var payload = new
        {
            model,
            message = request.Prompt ?? "",
            chat_history = chatHistory,
            preamble = request.SystemMessage,
            max_tokens = request.MaxTokens ?? 4096,
            temperature = request.Temperature ?? 0.7f,
            stream = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat");
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var chunk = JsonSerializer.Deserialize<CohereStreamChunk>(line);

            if (chunk?.EventType == "text-generation")
            {
                yield return new AIStreamChunk { Content = chunk.Text ?? "" };
            }
            else if (chunk?.EventType == "stream-end")
            {
                yield return new AIStreamChunk { IsFinal = true, FinishReason = chunk.FinishReason };
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
            var apiBase = GetConfig("ApiBase") ?? DefaultApiBase;
            var model = GetConfig("EmbeddingModel") ?? DefaultEmbeddingModel;

            var payload = new
            {
                model,
                texts = new[] { text },
                input_type = "search_document"
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/embed");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CohereEmbeddingResponse>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result?.Embeddings?.FirstOrDefault() ?? Array.Empty<float>();
        });
    }

    private static List<object> BuildChatHistory(AIRequest request)
    {
        var history = new List<object>();

        foreach (var msg in request.ChatHistory)
        {
            history.Add(new { role = msg.Role.ToUpperInvariant(), message = msg.Content });
        }

        return history;
    }

    // Response models
    private sealed class CohereChatResponse
    {
        public string? Text { get; set; }
        public string? FinishReason { get; set; }
        public CohereMeta? Meta { get; set; }
    }

    private sealed class CohereMeta
    {
        public CohereTokens? Tokens { get; set; }
    }

    private sealed class CohereTokens
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    private sealed class CohereStreamChunk
    {
        public string? EventType { get; set; }
        public string? Text { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class CohereEmbeddingResponse
    {
        public List<float[]>? Embeddings { get; set; }
    }
}

#endregion

#region Perplexity Provider

/// <summary>
/// Perplexity AI provider strategy.
/// Supports Perplexity models with live web search capabilities.
/// </summary>
public sealed class PerplexityProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.perplexity.ai";
    private const string DefaultModel = "llama-3-sonar-large-32k-online";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-perplexity";

    /// <inheritdoc/>
    public override string StrategyName => "Perplexity AI Provider";

    /// <inheritdoc/>
    public override string ProviderId => "perplexity";

    /// <inheritdoc/>
    public override string DisplayName => "Perplexity AI";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Perplexity AI",
        Description = "Perplexity AI models with real-time web search and reasoning capabilities",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Perplexity API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 3,
        LatencyTier = 3, // Higher latency due to web search
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "perplexity", "search", "web-search", "real-time", "llama-3" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public PerplexityProviderStrategy() : this(SharedHttpClient) { }

    public PerplexityProviderStrategy(HttpClient httpClient)
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
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PerplexityChatResponse>(cancellationToken: ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (result?.Usage != null)
                RecordTokens(result.Usage.TotalTokens);

            return new AIResponse
            {
                Success = true,
                Content = choice?.Message?.Content ?? string.Empty,
                FinishReason = choice?.FinishReason,
                Usage = result?.Usage != null
                    ? new AIUsage { PromptTokens = result.Usage.PromptTokens, CompletionTokens = result.Usage.CompletionTokens }
                    : null,
                Metadata = result?.Citations != null
                    ? new Dictionary<string, object> { ["citations"] = result.Citations }
                    : new Dictionary<string, object>()
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

            var chunk = JsonSerializer.Deserialize<PerplexityStreamChunk>(data);
            var delta = chunk?.Choices?.FirstOrDefault()?.Delta;
            if (delta?.Content != null)
            {
                yield return new AIStreamChunk { Content = delta.Content };
            }
        }
    }

    /// <inheritdoc/>
    public override Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        throw new NotSupportedException("Perplexity AI does not provide an embeddings API.");
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
    private sealed class PerplexityChatResponse
    {
        public List<PerplexityChoice>? Choices { get; set; }
        public PerplexityUsage? Usage { get; set; }
        public List<string>? Citations { get; set; }
    }

    private sealed class PerplexityChoice
    {
        public PerplexityMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class PerplexityMessage
    {
        public string? Content { get; set; }
    }

    private sealed class PerplexityUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class PerplexityStreamChunk
    {
        public List<PerplexityStreamChoice>? Choices { get; set; }
    }

    private sealed class PerplexityStreamChoice
    {
        public PerplexityStreamDelta? Delta { get; set; }
    }

    private sealed class PerplexityStreamDelta
    {
        public string? Content { get; set; }
    }
}

#endregion

#region Groq Provider

/// <summary>
/// Groq API provider strategy.
/// Provides ultra-fast inference for Llama 3, Mixtral, and Gemma models.
/// </summary>
public sealed class GroqProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.groq.com/openai/v1";
    private const string DefaultModel = "llama-3.1-70b-versatile";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-groq";

    /// <inheritdoc/>
    public override string StrategyName => "Groq Provider";

    /// <inheritdoc/>
    public override string ProviderId => "groq";

    /// <inheritdoc/>
    public override string DisplayName => "Groq";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.FunctionCalling | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Groq",
        Description = "Groq provides ultra-fast inference for open-source models using LPU technology",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming | IntelligenceCapabilities.FunctionCalling,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Groq API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 2,
        LatencyTier = 1, // Ultra-fast inference
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "groq", "fast-inference", "llama-3", "mixtral", "gemma", "lpu" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public GroqProviderStrategy() : this(SharedHttpClient) { }

    public GroqProviderStrategy(HttpClient httpClient)
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
                tools = request.Functions?.Select(f => new
                {
                    type = "function",
                    function = new
                    {
                        name = f.Name,
                        description = f.Description,
                        parameters = JsonSerializer.Deserialize<JsonElement>(f.ParametersSchema)
                    }
                }).ToArray()
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GroqChatResponse>(cancellationToken: ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (result?.Usage != null)
                RecordTokens(result.Usage.TotalTokens);

            return new AIResponse
            {
                Success = true,
                Content = choice?.Message?.Content ?? string.Empty,
                FinishReason = choice?.FinishReason,
                FunctionCall = choice?.Message?.ToolCalls?.FirstOrDefault()?.Function != null
                    ? new AIFunctionCall
                    {
                        Name = choice.Message.ToolCalls[0].Function.Name,
                        Arguments = choice.Message.ToolCalls[0].Function.Arguments
                    }
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

            var chunk = JsonSerializer.Deserialize<GroqStreamChunk>(data);
            var delta = chunk?.Choices?.FirstOrDefault()?.Delta;
            if (delta?.Content != null)
            {
                yield return new AIStreamChunk { Content = delta.Content };
            }
        }
    }

    /// <inheritdoc/>
    public override Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        throw new NotSupportedException("Groq does not provide an embeddings API.");
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
    private sealed class GroqChatResponse
    {
        public List<GroqChoice>? Choices { get; set; }
        public GroqUsage? Usage { get; set; }
    }

    private sealed class GroqChoice
    {
        public GroqMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class GroqMessage
    {
        public string? Content { get; set; }
        public List<GroqToolCall>? ToolCalls { get; set; }
    }

    private sealed class GroqToolCall
    {
        public GroqFunction Function { get; set; } = new();
    }

    private sealed class GroqFunction
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "{}";
    }

    private sealed class GroqUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class GroqStreamChunk
    {
        public List<GroqStreamChoice>? Choices { get; set; }
    }

    private sealed class GroqStreamChoice
    {
        public GroqStreamDelta? Delta { get; set; }
    }

    private sealed class GroqStreamDelta
    {
        public string? Content { get; set; }
    }
}

#endregion

#region Together Provider

/// <summary>
/// Together AI provider strategy.
/// Provides access to hundreds of open-source models with fine-tuning support.
/// </summary>
public sealed class TogetherProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultApiBase = "https://api.together.xyz/v1";
    private const string DefaultModel = "mistralai/Mixtral-8x7B-Instruct-v0.1";
    private const string DefaultEmbeddingModel = "togethercomputer/m2-bert-80M-8k-retrieval";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-together";

    /// <inheritdoc/>
    public override string StrategyName => "Together AI Provider";

    /// <inheritdoc/>
    public override string ProviderId => "together";

    /// <inheritdoc/>
    public override string DisplayName => "Together AI";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.CodeGeneration | AICapabilities.ImageGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Together AI",
        Description = "Together AI provides scalable inference for hundreds of open-source models",
        Capabilities = IntelligenceCapabilities.TextCompletion | IntelligenceCapabilities.ChatCompletion |
                      IntelligenceCapabilities.Streaming | IntelligenceCapabilities.Embeddings | IntelligenceCapabilities.ImageGeneration,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Together AI API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Model", Description = "Default model to use", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Model for embeddings", Required = false, DefaultValue = DefaultEmbeddingModel },
            new ConfigurationRequirement { Key = "ApiBase", Description = "API base URL", Required = false, DefaultValue = DefaultApiBase }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "together", "open-source", "fine-tuning", "mixtral", "llama" }
    };

    private static readonly HttpClient SharedHttpClient = // Cat 15 (finding 3246): AI completions routinely take 60-120s; 30s causes spurious timeouts.
new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    public TogetherProviderStrategy() : this(SharedHttpClient) { }

    public TogetherProviderStrategy(HttpClient httpClient)
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
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/chat/completions");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TogetherChatResponse>(cancellationToken: ct);
            var choice = result?.Choices?.FirstOrDefault();

            if (result?.Usage != null)
                RecordTokens(result.Usage.TotalTokens);

            return new AIResponse
            {
                Success = true,
                Content = choice?.Message?.Content ?? string.Empty,
                FinishReason = choice?.FinishReason,
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

            var chunk = JsonSerializer.Deserialize<TogetherStreamChunk>(data);
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

            var result = await response.Content.ReadFromJsonAsync<TogetherEmbeddingResponse>(cancellationToken: ct);
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

    // Response models
    private sealed class TogetherChatResponse
    {
        public List<TogetherChoice>? Choices { get; set; }
        public TogetherUsage? Usage { get; set; }
    }

    private sealed class TogetherChoice
    {
        public TogetherMessage? Message { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class TogetherMessage
    {
        public string? Content { get; set; }
    }

    private sealed class TogetherUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    private sealed class TogetherStreamChunk
    {
        public List<TogetherStreamChoice>? Choices { get; set; }
    }

    private sealed class TogetherStreamChoice
    {
        public TogetherStreamDelta? Delta { get; set; }
    }

    private sealed class TogetherStreamDelta
    {
        public string? Content { get; set; }
    }

    private sealed class TogetherEmbeddingResponse
    {
        public List<TogetherEmbeddingData>? Data { get; set; }
    }

    private sealed class TogetherEmbeddingData
    {
        public float[]? Embedding { get; set; }
    }
}

#endregion
