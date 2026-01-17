using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Licensing;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Multi-provider AI Agent plugin for DataWarehouse.
    /// Supports multiple AI providers with unified interface, automatic failover,
    /// load balancing, and intelligent routing.
    ///
    /// Supported Providers:
    /// - Anthropic Claude (claude-3-opus, claude-3-sonnet, claude-3-haiku, claude-4-opus, etc.)
    /// - OpenAI (gpt-4, gpt-4-turbo, gpt-4o, o1, o1-mini, o3, etc.)
    /// - Google Gemini (gemini-pro, gemini-ultra, gemini-2.0-flash, etc.)
    /// - Microsoft Copilot (via Azure OpenAI)
    /// - Ollama (local models)
    /// - Mistral AI (mistral-large, mixtral, etc.)
    /// - Cohere (command-r, command-r-plus)
    /// - Perplexity (sonar-pro, sonar-reasoning)
    /// - Groq (llama-3, mixtral on Groq)
    /// - Together AI (various open models)
    /// - AWS Bedrock (multi-model)
    /// - Hugging Face Inference API
    ///
    /// Features:
    /// - Unified chat/completion interface across all providers
    /// - Automatic failover between providers
    /// - Load balancing and rate limiting
    /// - Token counting and cost estimation
    /// - Streaming responses
    /// - Function/tool calling
    /// - Vision/multimodal support
    /// - Embeddings generation
    /// - Conversation history management
    /// - Prompt caching and optimization
    ///
    /// Message Commands:
    /// - ai.chat: Send a chat message
    /// - ai.complete: Generate a completion
    /// - ai.embed: Generate embeddings
    /// - ai.stream: Stream a response
    /// - ai.function: Call a function/tool
    /// - ai.analyze: Analyze data with AI
    /// - ai.providers: List available providers
    /// - ai.configure: Configure a provider
    /// </summary>
    public sealed class AIAgentPlugin : FeaturePluginBase
    {
        public override string Id => "datawarehouse.plugins.ai";
        public override string Name => "AI Agents";
        public override string Version => "1.0.0";
        public override PluginCategory Category => PluginCategory.AIProvider;

        private readonly ConcurrentDictionary<string, IAIProvider> _providers = new();
        private readonly ConcurrentDictionary<string, ConversationHistory> _conversations = new();
        private readonly ConcurrentDictionary<string, ProviderStats> _providerStats = new();
        private readonly AIAgentConfig _config;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _rateLimiter;
        private readonly CancellationTokenSource _shutdownCts = new();

        public AIAgentPlugin(AIAgentConfig? config = null)
        {
            _config = config ?? new AIAgentConfig();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _rateLimiter = new SemaphoreSlim(_config.MaxConcurrentRequests, _config.MaxConcurrentRequests);
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "ai.chat", DisplayName = "Chat", Description = "Send a chat message to AI" },
                new() { Name = "ai.complete", DisplayName = "Complete", Description = "Generate text completion" },
                new() { Name = "ai.embed", DisplayName = "Embed", Description = "Generate embeddings" },
                new() { Name = "ai.stream", DisplayName = "Stream", Description = "Stream AI response" },
                new() { Name = "ai.function", DisplayName = "Function", Description = "Execute function calling" },
                new() { Name = "ai.analyze", DisplayName = "Analyze", Description = "Analyze data with AI" },
                new() { Name = "ai.vision", DisplayName = "Vision", Description = "Process images with AI" },
                new() { Name = "ai.providers", DisplayName = "Providers", Description = "List available providers" },
                new() { Name = "ai.configure", DisplayName = "Configure", Description = "Configure provider settings" },
                new() { Name = "ai.stats", DisplayName = "Statistics", Description = "Get usage statistics" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "AIAgent";
            metadata["ProviderCount"] = _providers.Count;
            metadata["ActiveProviders"] = _providers.Keys.ToList();
            metadata["SupportsStreaming"] = true;
            metadata["SupportsFunctionCalling"] = true;
            metadata["SupportsVision"] = true;
            metadata["SupportsEmbeddings"] = true;
            metadata["MaxConcurrentRequests"] = _config.MaxConcurrentRequests;
            return metadata;
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            // Initialize configured providers
            foreach (var providerConfig in _config.Providers)
            {
                try
                {
                    var provider = CreateProvider(providerConfig);
                    if (provider != null)
                    {
                        _providers[providerConfig.Name] = provider;
                        _providerStats[providerConfig.Name] = new ProviderStats { ProviderName = providerConfig.Name };
                    }
                }
                catch (Exception ex)
                {
                    // Log but continue with other providers
                    System.Diagnostics.Debug.WriteLine($"Failed to initialize provider {providerConfig.Name}: {ex.Message}");
                }
            }
        }

        public override async Task StopAsync()
        {
            _shutdownCts.Cancel();
            _httpClient.Dispose();
            _rateLimiter.Dispose();

            foreach (var provider in _providers.Values.OfType<IDisposable>())
            {
                provider.Dispose();
            }
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            object? response = message.Type switch
            {
                "ai.chat" => await HandleChatAsync(message.Payload),
                "ai.complete" => await HandleCompleteAsync(message.Payload),
                "ai.embed" => await HandleEmbedAsync(message.Payload),
                "ai.stream" => await HandleStreamAsync(message.Payload),
                "ai.function" => await HandleFunctionAsync(message.Payload),
                "ai.analyze" => await HandleAnalyzeAsync(message.Payload),
                "ai.vision" => await HandleVisionAsync(message.Payload),
                "ai.providers" => HandleProviders(),
                "ai.configure" => HandleConfigure(message.Payload),
                "ai.stats" => HandleStats(),
                "ai.conversation.create" => HandleConversationCreate(message.Payload),
                "ai.conversation.continue" => await HandleConversationContinueAsync(message.Payload),
                "ai.conversation.clear" => HandleConversationClear(message.Payload),
                _ => new { error = $"Unknown command: {message.Type}" }
            };

            if (response != null && message.Payload != null)
            {
                message.Payload["_response"] = response;
            }
        }

        #region Message Handlers

        private async Task<object> HandleChatAsync(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider") ?? _config.DefaultProvider;
            var model = GetString(payload, "model");
            var message = GetString(payload, "message");
            var systemPrompt = GetString(payload, "system");
            var conversationId = GetString(payload, "conversationId");

            if (string.IsNullOrEmpty(message))
            {
                return new { error = "message is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            await _rateLimiter.WaitAsync();
            try
            {
                var request = new ChatRequest
                {
                    Model = model ?? provider.DefaultModel,
                    Messages = BuildMessages(conversationId, message, systemPrompt),
                    MaxTokens = GetInt(payload, "maxTokens") ?? _config.DefaultMaxTokens,
                    Temperature = GetDouble(payload, "temperature") ?? _config.DefaultTemperature
                };

                var startTime = DateTime.UtcNow;
                var response = await provider.ChatAsync(request);
                var duration = DateTime.UtcNow - startTime;

                // Update stats
                UpdateProviderStats(providerName, response, duration);

                // Update conversation if tracking
                if (!string.IsNullOrEmpty(conversationId) && _conversations.TryGetValue(conversationId, out var history))
                {
                    history.Messages.Add(new ChatMessage { Role = "user", Content = message });
                    history.Messages.Add(new ChatMessage { Role = "assistant", Content = response.Content });
                }

                return new
                {
                    success = true,
                    provider = providerName,
                    model = response.Model,
                    content = response.Content,
                    usage = new
                    {
                        inputTokens = response.InputTokens,
                        outputTokens = response.OutputTokens,
                        totalTokens = response.TotalTokens
                    },
                    durationMs = duration.TotalMilliseconds,
                    finishReason = response.FinishReason
                };
            }
            catch (Exception ex)
            {
                // Try failover if enabled
                if (_config.EnableFailover)
                {
                    return await TryFailoverAsync(payload, providerName, ex);
                }
                return new { error = ex.Message, provider = providerName };
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        private async Task<object> HandleCompleteAsync(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider") ?? _config.DefaultProvider;
            var prompt = GetString(payload, "prompt");

            if (string.IsNullOrEmpty(prompt))
            {
                return new { error = "prompt is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            await _rateLimiter.WaitAsync();
            try
            {
                var request = new CompletionRequest
                {
                    Model = GetString(payload, "model") ?? provider.DefaultModel,
                    Prompt = prompt,
                    MaxTokens = GetInt(payload, "maxTokens") ?? _config.DefaultMaxTokens,
                    Temperature = GetDouble(payload, "temperature") ?? _config.DefaultTemperature,
                    StopSequences = GetStringArray(payload, "stop")
                };

                var response = await provider.CompleteAsync(request);

                return new
                {
                    success = true,
                    provider = providerName,
                    completion = response.Text,
                    usage = new { inputTokens = response.InputTokens, outputTokens = response.OutputTokens }
                };
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        private async Task<object> HandleEmbedAsync(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider") ?? _config.DefaultEmbeddingProvider;
            var text = GetString(payload, "text");
            var texts = GetStringArray(payload, "texts");

            if (string.IsNullOrEmpty(text) && (texts == null || texts.Length == 0))
            {
                return new { error = "text or texts is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            var inputTexts = texts ?? new[] { text! };
            var embeddings = await provider.EmbedAsync(inputTexts, GetString(payload, "model"));

            return new
            {
                success = true,
                provider = providerName,
                count = embeddings.Length,
                dimensions = embeddings.FirstOrDefault()?.Length ?? 0,
                embeddings = embeddings
            };
        }

        private async Task<object> HandleStreamAsync(Dictionary<string, object?>? payload)
        {
            // Streaming would typically use Server-Sent Events or WebSocket
            // For message-based architecture, we return chunks in response
            var providerName = GetString(payload, "provider") ?? _config.DefaultProvider;
            var message = GetString(payload, "message");

            if (string.IsNullOrEmpty(message))
            {
                return new { error = "message is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            var chunks = new List<string>();
            var request = new ChatRequest
            {
                Model = GetString(payload, "model") ?? provider.DefaultModel,
                Messages = new List<ChatMessage> { new() { Role = "user", Content = message } },
                MaxTokens = GetInt(payload, "maxTokens") ?? _config.DefaultMaxTokens,
                Stream = true
            };

            await foreach (var chunk in provider.StreamChatAsync(request))
            {
                chunks.Add(chunk);
            }

            return new
            {
                success = true,
                provider = providerName,
                chunks = chunks,
                fullContent = string.Concat(chunks)
            };
        }

        private async Task<object> HandleFunctionAsync(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider") ?? _config.DefaultProvider;
            var message = GetString(payload, "message");

            if (string.IsNullOrEmpty(message))
            {
                return new { error = "message is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            // Extract function definitions from payload
            var functions = ExtractFunctions(payload);

            var request = new FunctionCallRequest
            {
                Model = GetString(payload, "model") ?? provider.DefaultModel,
                Messages = new List<ChatMessage> { new() { Role = "user", Content = message } },
                Functions = functions,
                FunctionCall = GetString(payload, "functionCall") ?? "auto"
            };

            var response = await provider.FunctionCallAsync(request);

            return new
            {
                success = true,
                provider = providerName,
                functionCall = response.FunctionName != null ? new
                {
                    name = response.FunctionName,
                    arguments = response.Arguments
                } : null,
                content = response.Content
            };
        }

        private async Task<object> HandleAnalyzeAsync(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider") ?? _config.DefaultProvider;
            var data = GetString(payload, "data");
            var analysisType = GetString(payload, "type") ?? "general";

            if (string.IsNullOrEmpty(data))
            {
                return new { error = "data is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            var systemPrompt = analysisType switch
            {
                "summary" => "Summarize the following data concisely.",
                "insights" => "Analyze the data and provide key insights and patterns.",
                "anomalies" => "Identify any anomalies or unusual patterns in the data.",
                "trends" => "Identify trends and projections based on the data.",
                "sentiment" => "Analyze the sentiment expressed in the data.",
                "classification" => "Classify and categorize the data.",
                _ => "Analyze the following data and provide useful insights."
            };

            var request = new ChatRequest
            {
                Model = GetString(payload, "model") ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = data }
                },
                MaxTokens = GetInt(payload, "maxTokens") ?? 2048
            };

            var response = await provider.ChatAsync(request);

            return new
            {
                success = true,
                provider = providerName,
                analysisType,
                result = response.Content,
                usage = new { inputTokens = response.InputTokens, outputTokens = response.OutputTokens }
            };
        }

        private async Task<object> HandleVisionAsync(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider") ?? _config.DefaultVisionProvider;
            var imageUrl = GetString(payload, "imageUrl");
            var imageBase64 = GetString(payload, "imageBase64");
            var prompt = GetString(payload, "prompt") ?? "Describe this image.";

            if (string.IsNullOrEmpty(imageUrl) && string.IsNullOrEmpty(imageBase64))
            {
                return new { error = "imageUrl or imageBase64 is required" };
            }

            var provider = GetProvider(providerName);
            if (provider == null)
            {
                return new { error = $"Provider not found: {providerName}" };
            }

            var request = new VisionRequest
            {
                Model = GetString(payload, "model") ?? provider.DefaultVisionModel,
                ImageUrl = imageUrl,
                ImageBase64 = imageBase64,
                Prompt = prompt,
                MaxTokens = GetInt(payload, "maxTokens") ?? 1024
            };

            var response = await provider.VisionAsync(request);

            return new
            {
                success = true,
                provider = providerName,
                description = response.Content,
                usage = new { inputTokens = response.InputTokens, outputTokens = response.OutputTokens }
            };
        }

        private object HandleProviders()
        {
            return new
            {
                success = true,
                providers = _providers.Select(p => new
                {
                    name = p.Key,
                    type = p.Value.ProviderType,
                    defaultModel = p.Value.DefaultModel,
                    supportsStreaming = p.Value.SupportsStreaming,
                    supportsFunctionCalling = p.Value.SupportsFunctionCalling,
                    supportsVision = p.Value.SupportsVision,
                    supportsEmbeddings = p.Value.SupportsEmbeddings,
                    availableModels = p.Value.AvailableModels
                })
            };
        }

        private object HandleConfigure(Dictionary<string, object?>? payload)
        {
            var providerName = GetString(payload, "provider");
            var apiKey = GetString(payload, "apiKey");
            var endpoint = GetString(payload, "endpoint");
            var model = GetString(payload, "model");

            if (string.IsNullOrEmpty(providerName))
            {
                return new { error = "provider name is required" };
            }

            var providerConfig = new ProviderConfig
            {
                Name = providerName,
                Type = GetString(payload, "type") ?? providerName,
                ApiKey = apiKey,
                Endpoint = endpoint,
                DefaultModel = model
            };

            var provider = CreateProvider(providerConfig);
            if (provider != null)
            {
                _providers[providerName] = provider;
                _providerStats[providerName] = new ProviderStats { ProviderName = providerName };
                return new { success = true, provider = providerName };
            }

            return new { error = $"Failed to configure provider: {providerName}" };
        }

        private object HandleStats()
        {
            return new
            {
                success = true,
                providers = _providerStats.Select(s => new
                {
                    name = s.Key,
                    totalRequests = s.Value.TotalRequests,
                    successfulRequests = s.Value.SuccessfulRequests,
                    failedRequests = s.Value.FailedRequests,
                    totalInputTokens = s.Value.TotalInputTokens,
                    totalOutputTokens = s.Value.TotalOutputTokens,
                    averageLatencyMs = s.Value.TotalRequests > 0
                        ? s.Value.TotalLatencyMs / s.Value.TotalRequests
                        : 0,
                    estimatedCost = s.Value.EstimatedCostUsd
                })
            };
        }

        private object HandleConversationCreate(Dictionary<string, object?>? payload)
        {
            var conversationId = Guid.NewGuid().ToString("N");
            var systemPrompt = GetString(payload, "system");

            var history = new ConversationHistory
            {
                Id = conversationId,
                CreatedAt = DateTime.UtcNow,
                SystemPrompt = systemPrompt
            };

            _conversations[conversationId] = history;

            return new { success = true, conversationId };
        }

        private async Task<object> HandleConversationContinueAsync(Dictionary<string, object?>? payload)
        {
            var conversationId = GetString(payload, "conversationId");
            if (string.IsNullOrEmpty(conversationId) || !_conversations.ContainsKey(conversationId))
            {
                return new { error = "Invalid conversationId" };
            }

            // Delegate to chat handler with conversation context
            payload!["conversationId"] = conversationId;
            return await HandleChatAsync(payload);
        }

        private object HandleConversationClear(Dictionary<string, object?>? payload)
        {
            var conversationId = GetString(payload, "conversationId");
            if (string.IsNullOrEmpty(conversationId))
            {
                return new { error = "conversationId is required" };
            }

            _conversations.TryRemove(conversationId, out _);
            return new { success = true, conversationId };
        }

        #endregion

        #region Provider Management

        private IAIProvider? GetProvider(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _providers.TryGetValue(name, out var provider) ? provider : null;
        }

        private IAIProvider? CreateProvider(ProviderConfig config)
        {
            return config.Type.ToLowerInvariant() switch
            {
                "anthropic" or "claude" => new AnthropicProvider(_httpClient, config),
                "openai" or "gpt" => new OpenAIProvider(_httpClient, config),
                "google" or "gemini" => new GeminiProvider(_httpClient, config),
                "azure" or "copilot" or "azureopenai" => new AzureOpenAIProvider(_httpClient, config),
                "ollama" => new OllamaProvider(_httpClient, config),
                "mistral" => new MistralProvider(_httpClient, config),
                "cohere" => new CohereProvider(_httpClient, config),
                "perplexity" => new PerplexityProvider(_httpClient, config),
                "groq" => new GroqProvider(_httpClient, config),
                "together" => new TogetherProvider(_httpClient, config),
                "bedrock" => new BedrockProvider(_httpClient, config),
                "huggingface" => new HuggingFaceProvider(_httpClient, config),
                _ => null
            };
        }

        private async Task<object> TryFailoverAsync(Dictionary<string, object?>? payload, string failedProvider, Exception ex)
        {
            var fallbackProviders = _config.FailoverOrder
                .Where(p => p != failedProvider && _providers.ContainsKey(p))
                .ToList();

            foreach (var fallbackName in fallbackProviders)
            {
                try
                {
                    payload!["provider"] = fallbackName;
                    var result = await HandleChatAsync(payload);
                    if (result is Dictionary<string, object> dict && dict.ContainsKey("success"))
                    {
                        dict["failedOver"] = true;
                        dict["originalProvider"] = failedProvider;
                        dict["failoverReason"] = ex.Message;
                        return dict;
                    }
                    return result;
                }
                catch
                {
                    continue;
                }
            }

            return new { error = $"All providers failed. Original error: {ex.Message}" };
        }

        private void UpdateProviderStats(string providerName, ChatResponse response, TimeSpan duration)
        {
            if (!_providerStats.TryGetValue(providerName, out var stats)) return;

            Interlocked.Increment(ref stats.TotalRequests);
            Interlocked.Increment(ref stats.SuccessfulRequests);
            Interlocked.Add(ref stats.TotalInputTokens, response.InputTokens);
            Interlocked.Add(ref stats.TotalOutputTokens, response.OutputTokens);
            Interlocked.Add(ref stats.TotalLatencyMs, (long)duration.TotalMilliseconds);

            // Estimate cost (rough estimates)
            var cost = EstimateCost(providerName, response.InputTokens, response.OutputTokens);
            lock (stats)
            {
                stats.EstimatedCostUsd += cost;
            }
        }

        private double EstimateCost(string provider, int inputTokens, int outputTokens)
        {
            // Rough cost estimates per 1M tokens
            var (inputRate, outputRate) = provider.ToLowerInvariant() switch
            {
                "anthropic" or "claude" => (3.0, 15.0),    // Claude 3 Sonnet pricing
                "openai" => (2.5, 10.0),                    // GPT-4 Turbo pricing
                "google" or "gemini" => (0.5, 1.5),         // Gemini Pro pricing
                "azure" => (3.0, 12.0),                     // Azure OpenAI
                "mistral" => (2.0, 6.0),                    // Mistral Large
                "cohere" => (1.0, 2.0),                     // Command-R
                "groq" => (0.27, 0.27),                     // Groq (very cheap)
                _ => (1.0, 2.0)                             // Default estimate
            };

            return (inputTokens * inputRate + outputTokens * outputRate) / 1_000_000;
        }

        #endregion

        #region Helpers

        private List<ChatMessage> BuildMessages(string? conversationId, string message, string? systemPrompt)
        {
            var messages = new List<ChatMessage>();

            if (!string.IsNullOrEmpty(conversationId) && _conversations.TryGetValue(conversationId, out var history))
            {
                if (!string.IsNullOrEmpty(history.SystemPrompt))
                {
                    messages.Add(new ChatMessage { Role = "system", Content = history.SystemPrompt });
                }
                messages.AddRange(history.Messages);
            }
            else if (!string.IsNullOrEmpty(systemPrompt))
            {
                messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
            }

            messages.Add(new ChatMessage { Role = "user", Content = message });
            return messages;
        }

        private List<FunctionDefinition> ExtractFunctions(Dictionary<string, object?>? payload)
        {
            if (payload?.TryGetValue("functions", out var funcs) != true || funcs == null)
                return new List<FunctionDefinition>();

            if (funcs is JsonElement element)
            {
                return JsonSerializer.Deserialize<List<FunctionDefinition>>(element.GetRawText()) ?? new();
            }

            return new List<FunctionDefinition>();
        }

        private static string? GetString(Dictionary<string, object?>? payload, string key)
        {
            if (payload?.TryGetValue(key, out var val) != true) return null;
            return val switch
            {
                string s => s,
                JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
                _ => val?.ToString()
            };
        }

        private static int? GetInt(Dictionary<string, object?>? payload, string key)
        {
            if (payload?.TryGetValue(key, out var val) != true) return null;
            return val switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt32(),
                _ => null
            };
        }

        private static double? GetDouble(Dictionary<string, object?>? payload, string key)
        {
            if (payload?.TryGetValue(key, out var val) != true) return null;
            return val switch
            {
                double d => d,
                int i => i,
                long l => l,
                JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetDouble(),
                _ => null
            };
        }

        private static string[]? GetStringArray(Dictionary<string, object?>? payload, string key)
        {
            if (payload?.TryGetValue(key, out var val) != true || val == null) return null;
            if (val is string[] arr) return arr;
            if (val is JsonElement e && e.ValueKind == JsonValueKind.Array)
            {
                return e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            }
            return null;
        }

        #endregion
    }

    #region Provider Interface

    public interface IAIProvider
    {
        string ProviderType { get; }
        string DefaultModel { get; }
        string? DefaultVisionModel { get; }
        bool SupportsStreaming { get; }
        bool SupportsFunctionCalling { get; }
        bool SupportsVision { get; }
        bool SupportsEmbeddings { get; }
        string[] AvailableModels { get; }

        Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);
        Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default);
        Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default);
        IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, CancellationToken ct = default);
        Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default);
        Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default);
    }

    #endregion

    #region Request/Response Types

    public class ChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
        public bool Stream { get; set; }
        public string[]? StopSequences { get; set; }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    public class ChatResponse
    {
        public string Model { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens => InputTokens + OutputTokens;
        public string? FinishReason { get; set; }
    }

    public class CompletionRequest
    {
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
        public string[]? StopSequences { get; set; }
    }

    public class CompletionResponse
    {
        public string Text { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    public class FunctionCallRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public List<FunctionDefinition> Functions { get; set; } = new();
        public string FunctionCall { get; set; } = "auto";
    }

    public class FunctionDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public class FunctionCallResponse
    {
        public string? FunctionName { get; set; }
        public Dictionary<string, object>? Arguments { get; set; }
        public string? Content { get; set; }
    }

    public class VisionRequest
    {
        public string Model { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string? ImageBase64 { get; set; }
        public string Prompt { get; set; } = string.Empty;
        public int? MaxTokens { get; set; }
    }

    public class VisionResponse
    {
        public string Content { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    #endregion

    #region Configuration

    public class AIAgentConfig
    {
        public string DefaultProvider { get; set; } = "openai";
        public string DefaultEmbeddingProvider { get; set; } = "openai";
        public string DefaultVisionProvider { get; set; } = "openai";
        public int DefaultMaxTokens { get; set; } = 4096;
        public double DefaultTemperature { get; set; } = 0.7;
        public int MaxConcurrentRequests { get; set; } = 10;
        public bool EnableFailover { get; set; } = true;
        public List<string> FailoverOrder { get; set; } = new() { "openai", "anthropic", "google" };
        public List<ProviderConfig> Providers { get; set; } = new();
    }

    public class ProviderConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public string? Endpoint { get; set; }
        public string? DefaultModel { get; set; }
        public string? Organization { get; set; }
        public Dictionary<string, object>? Options { get; set; }
        /// <summary>
        /// Additional provider-specific settings (e.g., api_version for Azure, region for Bedrock).
        /// </summary>
        public Dictionary<string, object?>? AdditionalSettings { get; set; }
    }

    public class ConversationHistory
    {
        public string Id { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? SystemPrompt { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
    }

    public class ProviderStats
    {
        public string ProviderName { get; set; } = string.Empty;
        public long TotalRequests;
        public long SuccessfulRequests;
        public long FailedRequests;
        public long TotalInputTokens;
        public long TotalOutputTokens;
        public long TotalLatencyMs;
        public double EstimatedCostUsd;
    }

    #endregion

    #region Authentication and Usage Limits

    /// <summary>
    /// User quota tier levels.
    /// </summary>
    public enum QuotaTier
    {
        /// <summary>Free tier - Limited requests, basic models only</summary>
        Free,
        /// <summary>Basic tier - More requests, standard models</summary>
        Basic,
        /// <summary>Pro tier - High volume, all models, priority</summary>
        Pro,
        /// <summary>Enterprise tier - Unlimited, custom models, dedicated support</summary>
        Enterprise,
        /// <summary>BYOK (Bring Your Own Key) - User provides their own API keys</summary>
        BringYourOwnKey
    }

    /// <summary>
    /// Usage limits per quota tier.
    /// </summary>
    public class UsageLimits
    {
        public int DailyRequestLimit { get; set; }
        public int DailyTokenLimit { get; set; }
        public int MaxTokensPerRequest { get; set; }
        public int MaxConcurrentRequests { get; set; }
        public string[] AllowedModels { get; set; } = Array.Empty<string>();
        public string[] AllowedProviders { get; set; } = Array.Empty<string>();
        public bool StreamingEnabled { get; set; }
        public bool FunctionCallingEnabled { get; set; }
        public bool VisionEnabled { get; set; }
        public bool EmbeddingsEnabled { get; set; }
        public double? MonthlyBudgetUsd { get; set; }

        public static UsageLimits GetDefaultLimits(QuotaTier tier) => tier switch
        {
            QuotaTier.Free => new UsageLimits
            {
                DailyRequestLimit = 50,
                DailyTokenLimit = 50_000,
                MaxTokensPerRequest = 1024,
                MaxConcurrentRequests = 1,
                AllowedModels = new[] { "gpt-4o-mini", "claude-3-haiku-20240307", "gemini-1.5-flash" },
                AllowedProviders = new[] { "openai", "anthropic", "google" },
                StreamingEnabled = false,
                FunctionCallingEnabled = false,
                VisionEnabled = false,
                EmbeddingsEnabled = false,
                MonthlyBudgetUsd = null
            },
            QuotaTier.Basic => new UsageLimits
            {
                DailyRequestLimit = 500,
                DailyTokenLimit = 500_000,
                MaxTokensPerRequest = 4096,
                MaxConcurrentRequests = 3,
                AllowedModels = new[] { "gpt-4o-mini", "gpt-4o", "claude-3-haiku-20240307", "claude-3-sonnet-20240229", "gemini-1.5-flash", "gemini-1.5-pro" },
                AllowedProviders = new[] { "openai", "anthropic", "google", "mistral" },
                StreamingEnabled = true,
                FunctionCallingEnabled = true,
                VisionEnabled = false,
                EmbeddingsEnabled = true,
                MonthlyBudgetUsd = 20.0
            },
            QuotaTier.Pro => new UsageLimits
            {
                DailyRequestLimit = 5000,
                DailyTokenLimit = 5_000_000,
                MaxTokensPerRequest = 16384,
                MaxConcurrentRequests = 10,
                AllowedModels = new[] { "*" }, // All models
                AllowedProviders = new[] { "*" }, // All providers
                StreamingEnabled = true,
                FunctionCallingEnabled = true,
                VisionEnabled = true,
                EmbeddingsEnabled = true,
                MonthlyBudgetUsd = 100.0
            },
            QuotaTier.Enterprise => new UsageLimits
            {
                DailyRequestLimit = int.MaxValue,
                DailyTokenLimit = int.MaxValue,
                MaxTokensPerRequest = 128000,
                MaxConcurrentRequests = 100,
                AllowedModels = new[] { "*" },
                AllowedProviders = new[] { "*" },
                StreamingEnabled = true,
                FunctionCallingEnabled = true,
                VisionEnabled = true,
                EmbeddingsEnabled = true,
                MonthlyBudgetUsd = null // Unlimited
            },
            QuotaTier.BringYourOwnKey => new UsageLimits
            {
                DailyRequestLimit = int.MaxValue,
                DailyTokenLimit = int.MaxValue,
                MaxTokensPerRequest = 128000,
                MaxConcurrentRequests = 50,
                AllowedModels = new[] { "*" },
                AllowedProviders = new[] { "*" },
                StreamingEnabled = true,
                FunctionCallingEnabled = true,
                VisionEnabled = true,
                EmbeddingsEnabled = true,
                MonthlyBudgetUsd = null // User pays directly
            },
            _ => GetDefaultLimits(QuotaTier.Free)
        };

        public bool IsModelAllowed(string model)
        {
            if (AllowedModels.Contains("*")) return true;
            return AllowedModels.Any(m => model.StartsWith(m, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsProviderAllowed(string provider)
        {
            if (AllowedProviders.Contains("*")) return true;
            return AllowedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Per-user quota tracking.
    /// </summary>
    public class UserQuota
    {
        public string UserId { get; set; } = string.Empty;
        public QuotaTier Tier { get; set; } = QuotaTier.Free;
        public UsageLimits Limits { get; set; } = UsageLimits.GetDefaultLimits(QuotaTier.Free);
        public DateTime PeriodStart { get; set; } = DateTime.UtcNow.Date;
        public int RequestsToday { get; set; }
        public int TokensToday { get; set; }
        public double SpentThisMonth { get; set; }
        public Dictionary<string, string>? UserApiKeys { get; set; } // Provider -> API Key for BYOK

        public bool CanMakeRequest(int estimatedTokens, out string? reason)
        {
            reason = null;

            // Check if period needs reset
            if (DateTime.UtcNow.Date > PeriodStart)
            {
                RequestsToday = 0;
                TokensToday = 0;
                PeriodStart = DateTime.UtcNow.Date;
            }

            if (RequestsToday >= Limits.DailyRequestLimit)
            {
                reason = $"Daily request limit ({Limits.DailyRequestLimit}) exceeded. Upgrade tier or wait until tomorrow.";
                return false;
            }

            if (TokensToday + estimatedTokens > Limits.DailyTokenLimit)
            {
                reason = $"Daily token limit ({Limits.DailyTokenLimit:N0}) exceeded. Upgrade tier or wait until tomorrow.";
                return false;
            }

            if (Limits.MonthlyBudgetUsd.HasValue && SpentThisMonth >= Limits.MonthlyBudgetUsd.Value)
            {
                reason = $"Monthly budget (${Limits.MonthlyBudgetUsd:F2}) exceeded. Upgrade tier or add funds.";
                return false;
            }

            return true;
        }

        public void RecordUsage(int inputTokens, int outputTokens, double costUsd)
        {
            RequestsToday++;
            TokensToday += inputTokens + outputTokens;
            SpentThisMonth += costUsd;
        }

        /// <summary>
        /// Get API key for provider - either user's own key (BYOK) or null for system key.
        /// </summary>
        public string? GetApiKeyForProvider(string providerType)
        {
            if (Tier != QuotaTier.BringYourOwnKey || UserApiKeys == null)
                return null;

            return UserApiKeys.TryGetValue(providerType, out var key) ? key : null;
        }
    }

    /// <summary>
    /// Authentication result from external auth provider.
    /// </summary>
    public class AuthenticationResult
    {
        public bool IsAuthenticated { get; set; }
        public string? UserId { get; set; }
        public QuotaTier Tier { get; set; }
        public Dictionary<string, string>? UserApiKeys { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Interface for external authentication providers.
    /// Implement this to integrate with OAuth, API keys, or subscription systems.
    /// </summary>
    public interface IAIAuthProvider
    {
        /// <summary>
        /// Authenticate a request and return user quota information.
        /// </summary>
        Task<AuthenticationResult> AuthenticateAsync(string? token, Dictionary<string, object?>? context);

        /// <summary>
        /// Called after successful request to record usage for billing.
        /// </summary>
        Task RecordUsageAsync(string userId, string provider, string model, int inputTokens, int outputTokens, double costUsd);

        /// <summary>
        /// Get current quota status for a user.
        /// </summary>
        Task<UserQuota?> GetUserQuotaAsync(string userId);
    }

    /// <summary>
    /// Default in-memory auth provider for development/testing.
    /// Replace with database-backed implementation for production.
    /// </summary>
    public class InMemoryAuthProvider : IAIAuthProvider
    {
        private readonly ConcurrentDictionary<string, UserQuota> _quotas = new();
        private readonly string? _defaultApiKey;

        public InMemoryAuthProvider(string? defaultApiKey = null)
        {
            _defaultApiKey = defaultApiKey;
        }

        public Task<AuthenticationResult> AuthenticateAsync(string? token, Dictionary<string, object?>? context)
        {
            // Simple token-based auth for demo
            if (string.IsNullOrEmpty(token))
            {
                // Anonymous user gets free tier
                return Task.FromResult(new AuthenticationResult
                {
                    IsAuthenticated = true,
                    UserId = "anonymous",
                    Tier = QuotaTier.Free
                });
            }

            // Check if token is a valid API key format (BYOK)
            if (token.StartsWith("sk-") || token.StartsWith("pk-"))
            {
                return Task.FromResult(new AuthenticationResult
                {
                    IsAuthenticated = true,
                    UserId = $"byok-{token.GetHashCode():X8}",
                    Tier = QuotaTier.BringYourOwnKey,
                    UserApiKeys = new Dictionary<string, string>
                    {
                        ["openai"] = token.StartsWith("sk-") ? token : "",
                        ["anthropic"] = token.StartsWith("pk-") ? token : ""
                    }
                });
            }

            // Default authenticated user
            return Task.FromResult(new AuthenticationResult
            {
                IsAuthenticated = true,
                UserId = token,
                Tier = QuotaTier.Basic
            });
        }

        public Task RecordUsageAsync(string userId, string provider, string model, int inputTokens, int outputTokens, double costUsd)
        {
            var quota = _quotas.GetOrAdd(userId, id => new UserQuota
            {
                UserId = id,
                Tier = QuotaTier.Free,
                Limits = UsageLimits.GetDefaultLimits(QuotaTier.Free)
            });

            quota.RecordUsage(inputTokens, outputTokens, costUsd);
            return Task.CompletedTask;
        }

        public Task<UserQuota?> GetUserQuotaAsync(string userId)
        {
            return Task.FromResult(_quotas.TryGetValue(userId, out var quota) ? quota : null);
        }
    }

    #endregion

    #region Customer Tier Integration

    /// <summary>
    /// Provides integration between the SDK CustomerTier system and the AI plugin's QuotaTier system.
    /// </summary>
    public static class TierIntegration
    {
        /// <summary>
        /// Maps a CustomerTier to the corresponding AI QuotaTier.
        /// </summary>
        public static QuotaTier ToQuotaTier(CustomerTier customerTier) => customerTier switch
        {
            CustomerTier.Individual => QuotaTier.Free,
            CustomerTier.SMB => QuotaTier.Basic,
            CustomerTier.HighStakes => QuotaTier.Pro,
            CustomerTier.Hyperscale => QuotaTier.Enterprise,
            _ => QuotaTier.Free
        };

        /// <summary>
        /// Maps a QuotaTier to the corresponding CustomerTier.
        /// </summary>
        public static CustomerTier ToCustomerTier(QuotaTier quotaTier) => quotaTier switch
        {
            QuotaTier.Free => CustomerTier.Individual,
            QuotaTier.Basic => CustomerTier.SMB,
            QuotaTier.Pro => CustomerTier.HighStakes,
            QuotaTier.Enterprise => CustomerTier.Hyperscale,
            QuotaTier.BringYourOwnKey => CustomerTier.SMB, // BYOK users at least need SMB for API access
            _ => CustomerTier.Individual
        };

        /// <summary>
        /// Gets the AI features available for a customer tier.
        /// </summary>
        public static (bool Streaming, bool FunctionCalling, bool Vision, bool Embeddings) GetAIFeatures(CustomerTier tier)
        {
            return (
                Streaming: TierManager.HasFeature(tier, Feature.AIStreaming),
                FunctionCalling: TierManager.HasFeature(tier, Feature.AIFunctionCalling),
                Vision: TierManager.HasFeature(tier, Feature.AIVision),
                Embeddings: TierManager.HasFeature(tier, Feature.AIEmbeddings)
            );
        }

        /// <summary>
        /// Creates UsageLimits from a CustomerTier.
        /// </summary>
        public static UsageLimits CreateLimitsFromCustomerTier(CustomerTier tier)
        {
            var limits = TierManager.GetLimits(tier);
            var features = GetAIFeatures(tier);

            return new UsageLimits
            {
                DailyRequestLimit = limits.MaxDailyApiRequests,
                DailyTokenLimit = (int)Math.Min(limits.MaxDailyAITokens, int.MaxValue),
                MaxTokensPerRequest = tier switch
                {
                    CustomerTier.Individual => 1024,
                    CustomerTier.SMB => 4096,
                    CustomerTier.HighStakes => 16384,
                    CustomerTier.Hyperscale => 128000,
                    _ => 1024
                },
                MaxConcurrentRequests = limits.MaxConcurrentOperations,
                AllowedModels = tier >= CustomerTier.HighStakes ? new[] { "*" } : GetTierModels(tier),
                AllowedProviders = tier >= CustomerTier.HighStakes ? new[] { "*" } : GetTierProviders(tier),
                StreamingEnabled = features.Streaming,
                FunctionCallingEnabled = features.FunctionCalling,
                VisionEnabled = features.Vision,
                EmbeddingsEnabled = features.Embeddings,
                MonthlyBudgetUsd = limits.MonthlyPriceUsd.HasValue ? (double)limits.MonthlyPriceUsd.Value : null
            };
        }

        private static string[] GetTierModels(CustomerTier tier) => tier switch
        {
            CustomerTier.Individual => new[] { "gpt-4o-mini", "claude-3-haiku-20240307", "gemini-1.5-flash" },
            CustomerTier.SMB => new[] { "gpt-4o-mini", "gpt-4o", "claude-3-haiku-20240307", "claude-3-sonnet-20240229", "gemini-1.5-flash", "gemini-1.5-pro" },
            _ => new[] { "*" }
        };

        private static string[] GetTierProviders(CustomerTier tier) => tier switch
        {
            CustomerTier.Individual => new[] { "openai", "anthropic", "google" },
            CustomerTier.SMB => new[] { "openai", "anthropic", "google", "mistral", "cohere" },
            _ => new[] { "*" }
        };

        /// <summary>
        /// Validates that a customer can use a specific AI feature.
        /// Throws FeatureNotAvailableException if not allowed.
        /// </summary>
        public static void ValidateAIFeature(CustomerTier tier, string featureName)
        {
            var feature = featureName.ToLowerInvariant() switch
            {
                "chat" or "basic" => Feature.AIBasicChat,
                "streaming" or "stream" => Feature.AIStreaming,
                "function" or "functioncalling" or "tools" => Feature.AIFunctionCalling,
                "vision" or "image" => Feature.AIVision,
                "embedding" or "embeddings" or "embed" => Feature.AIEmbeddings,
                _ => Feature.AIBasicChat
            };

            if (!TierManager.HasFeature(tier, feature))
            {
                throw new FeatureNotAvailableException(feature, tier);
            }
        }
    }

    #endregion
}
