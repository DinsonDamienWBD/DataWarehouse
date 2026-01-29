using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Cohere AI provider.
    /// Supports Command R+, Command R, and Embed models.
    /// </summary>
    public class CohereProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://api.cohere.ai/v1";

        public string ProviderType => "Cohere";
        public string DefaultModel => _config.DefaultModel ?? "command-r-plus";
        public string? DefaultVisionModel => null; // Cohere doesn't support vision yet
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => false;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "command-r-plus",
            "command-r",
            "command-r-plus-08-2024",
            "command-r-08-2024",
            "command",
            "command-light",
            "embed-english-v3.0",
            "embed-multilingual-v3.0",
            "embed-english-light-v3.0",
            "embed-multilingual-light-v3.0",
            "rerank-english-v3.0",
            "rerank-multilingual-v3.0"
        };

        public string ProviderId => "cohere";
        public string DisplayName => "Cohere";
        public bool IsAvailable => !string.IsNullOrEmpty(_config.ApiKey);
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming |
            AICapabilities.Embeddings;

        public CohereProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat";

            // Convert messages to Cohere format
            var chatHistory = new List<object>();
            string? lastUserMessage = null;

            foreach (var msg in request.Messages)
            {
                if (msg.Role == "user")
                {
                    lastUserMessage = msg.Content;
                }
                else if (msg.Role == "assistant")
                {
                    if (lastUserMessage != null)
                    {
                        chatHistory.Add(new { role = "USER", message = lastUserMessage });
                        lastUserMessage = null;
                    }
                    chatHistory.Add(new { role = "CHATBOT", message = msg.Content });
                }
                else if (msg.Role == "system")
                {
                    // System message handled separately
                }
            }

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["message"] = lastUserMessage ?? request.Messages.Last().Content,
                ["chat_history"] = chatHistory
            };

            var systemMessage = request.Messages.FirstOrDefault(m => m.Role == "system");
            if (systemMessage != null)
                payload["preamble"] = systemMessage.Content;

            if (request.MaxTokens != null)
                payload["max_tokens"] = request.MaxTokens;
            if (request.Temperature != null)
                payload["temperature"] = request.Temperature;
            if (request.StopSequences?.Any() == true)
                payload["stop_sequences"] = request.StopSequences;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Cohere API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var textContent = root.GetProperty("text").GetString() ?? "";

            var inputTokens = 0;
            var outputTokens = 0;
            if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("tokens", out var tokens))
            {
                if (tokens.TryGetProperty("input_tokens", out var it))
                    inputTokens = it.GetInt32();
                if (tokens.TryGetProperty("output_tokens", out var ot))
                    outputTokens = ot.GetInt32();
            }

            return new ChatResponse
            {
                Model = request.Model,
                Content = textContent,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                FinishReason = root.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null
            };
        }

        public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/generate";

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt
            };

            if (request.MaxTokens != null)
                payload["max_tokens"] = request.MaxTokens;
            if (request.Temperature != null)
                payload["temperature"] = request.Temperature;
            if (request.StopSequences?.Any() == true)
                payload["stop_sequences"] = request.StopSequences;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var generations = root.GetProperty("generations");
            var text = generations[0].GetProperty("text").GetString() ?? "";

            return new CompletionResponse
            {
                Text = text,
                InputTokens = 0, // Generate doesn't return token counts directly
                OutputTokens = 0
            };
        }

        public async Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            var embedModel = model ?? "embed-english-v3.0";
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/embed";

            var payload = new
            {
                model = embedModel,
                texts = texts,
                input_type = "search_document",
                truncate = "END"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.GetProperty("embeddings")
                .EnumerateArray()
                .Select(e => e.EnumerateArray()
                    .Select(v => v.GetDouble())
                    .ToArray())
                .ToArray();
        }

        public async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
        {
            var result = await EmbedAsync(new[] { text }, null, ct);
            return result[0].Select(d => (float)d).ToArray();
        }

        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat";

            var chatHistory = new List<object>();
            string? lastUserMessage = null;

            foreach (var msg in request.Messages)
            {
                if (msg.Role == "user")
                {
                    lastUserMessage = msg.Content;
                }
                else if (msg.Role == "assistant")
                {
                    if (lastUserMessage != null)
                    {
                        chatHistory.Add(new { role = "USER", message = lastUserMessage });
                        lastUserMessage = null;
                    }
                    chatHistory.Add(new { role = "CHATBOT", message = msg.Content });
                }
            }

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["message"] = lastUserMessage ?? request.Messages.Last().Content,
                ["chat_history"] = chatHistory,
                ["stream"] = true
            };

            if (request.MaxTokens != null)
                payload["max_tokens"] = request.MaxTokens;
            if (request.Temperature != null)
                payload["temperature"] = request.Temperature;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                string? textStr = null;
                try
                {
                    var evt = JsonDocument.Parse(line);
                    var root = evt.RootElement;

                    if (root.TryGetProperty("event_type", out var eventType) &&
                        eventType.GetString() == "text-generation" &&
                        root.TryGetProperty("text", out var text))
                    {
                        textStr = text.GetString();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CohereProvider] Failed to parse streaming response: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(textStr))
                    yield return textStr;
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat";

            var tools = request.Functions.Select(f => new
            {
                name = f.Name,
                description = f.Description,
                parameter_definitions = f.Parameters
            }).ToList();

            var chatHistory = new List<object>();
            string? lastUserMessage = null;

            foreach (var msg in request.Messages)
            {
                if (msg.Role == "user")
                    lastUserMessage = msg.Content;
                else if (msg.Role == "assistant")
                {
                    if (lastUserMessage != null)
                    {
                        chatHistory.Add(new { role = "USER", message = lastUserMessage });
                        lastUserMessage = null;
                    }
                    chatHistory.Add(new { role = "CHATBOT", message = msg.Content });
                }
            }

            var payload = new
            {
                model = request.Model,
                message = lastUserMessage ?? request.Messages.Last().Content,
                chat_history = chatHistory,
                tools = tools
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            if (root.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                var toolCall = toolCalls[0];
                return new FunctionCallResponse
                {
                    FunctionName = toolCall.GetProperty("name").GetString(),
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        toolCall.GetProperty("parameters").GetRawText())
                };
            }

            return new FunctionCallResponse
            {
                Content = root.TryGetProperty("text", out var t) ? t.GetString() : null
            };
        }

        public Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            throw new NotSupportedException("Cohere does not support vision capabilities");
        }

        /// <summary>
        /// Rerank documents based on relevance to a query.
        /// </summary>
        public async Task<List<(int Index, double Score)>> RerankAsync(string query, string[] documents, string? model = null, int? topN = null, CancellationToken ct = default)
        {
            var rerankModel = model ?? "rerank-english-v3.0";
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/rerank";

            var payload = new Dictionary<string, object>
            {
                ["model"] = rerankModel,
                ["query"] = query,
                ["documents"] = documents
            };

            if (topN.HasValue)
                payload["top_n"] = topN.Value;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.GetProperty("results")
                .EnumerateArray()
                .Select(r => (
                    Index: r.GetProperty("index").GetInt32(),
                    Score: r.GetProperty("relevance_score").GetDouble()
                ))
                .ToList();
        }

        #region SDK IAIProvider Implementation

        public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
        {
            var chatRequest = new ChatRequest
            {
                Messages = request.ChatHistory.Count > 0
                    ? request.ChatHistory.Select(m => new ChatMessage { Role = m.Role.ToString().ToLowerInvariant(), Content = m.Content }).ToList()
                    : new List<ChatMessage> { new() { Role = "user", Content = request.Prompt } },
                Model = request.Model ?? DefaultModel,
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature ?? 0.7f
            };

            if (!string.IsNullOrEmpty(request.SystemMessage))
                chatRequest.Messages.Insert(0, new ChatMessage { Role = "system", Content = request.SystemMessage });

            var response = await ChatAsync(chatRequest, ct);
            return new AIResponse
            {
                Content = response.Content,
                FinishReason = response.FinishReason,
                Usage = new AIUsage
                {
                    PromptTokens = response.InputTokens,
                    CompletionTokens = response.OutputTokens
                }
            };
        }

        public async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var chatRequest = new ChatRequest
            {
                Messages = request.ChatHistory.Count > 0
                    ? request.ChatHistory.Select(m => new ChatMessage { Role = m.Role.ToString().ToLowerInvariant(), Content = m.Content }).ToList()
                    : new List<ChatMessage> { new() { Role = "user", Content = request.Prompt } },
                Model = request.Model ?? DefaultModel,
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature ?? 0.7f,
                Stream = true
            };

            await foreach (var chunk in StreamChatAsync(chatRequest, ct))
            {
                yield return new AIStreamChunk { Content = chunk };
            }
        }

        public async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
        {
            var result = await EmbedAsync(texts, null, ct);
            return result.Select(r => r.Select(d => (float)d).ToArray()).ToArray();
        }

        #endregion
    }
}
