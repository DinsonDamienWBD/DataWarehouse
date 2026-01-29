using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Perplexity AI provider for search-enhanced AI.
    /// Models have built-in web search capabilities.
    /// </summary>
    public class PerplexityProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://api.perplexity.ai";

        public string ProviderType => "Perplexity";
        public string DefaultModel => _config.DefaultModel ?? "llama-3.1-sonar-large-128k-online";
        public string? DefaultVisionModel => null;
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => false;
        public bool SupportsVision => false;
        public bool SupportsEmbeddings => false;

        public string[] AvailableModels => new[]
        {
            "llama-3.1-sonar-small-128k-online",
            "llama-3.1-sonar-large-128k-online",
            "llama-3.1-sonar-huge-128k-online",
            "llama-3.1-sonar-small-128k-chat",
            "llama-3.1-sonar-large-128k-chat",
            "llama-3.1-8b-instruct",
            "llama-3.1-70b-instruct"
        };

        public string ProviderId => "perplexity";
        public string DisplayName => "Perplexity AI";
        public bool IsAvailable => !string.IsNullOrEmpty(_config.ApiKey);
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming;

        public PerplexityProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat/completions";

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["messages"] = messages
            };

            if (request.MaxTokens != null)
                payload["max_tokens"] = request.MaxTokens;
            if (request.Temperature != null)
                payload["temperature"] = request.Temperature;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Perplexity API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var usage = root.GetProperty("usage");

            return new ChatResponse
            {
                Model = request.Model,
                Content = message.GetProperty("content").GetString() ?? "",
                InputTokens = usage.GetProperty("prompt_tokens").GetInt32(),
                OutputTokens = usage.GetProperty("completion_tokens").GetInt32(),
                FinishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null
            };
        }

        public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            var chatRequest = new ChatRequest
            {
                Model = request.Model,
                Messages = new List<ChatMessage> { new() { Role = "user", Content = request.Prompt } },
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature,
                StopSequences = request.StopSequences
            };

            var response = await ChatAsync(chatRequest, ct);
            return new CompletionResponse
            {
                Text = response.Content,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens
            };
        }

        public Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("Perplexity does not support embeddings");
        }

        public Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
        {
            throw new NotSupportedException("Perplexity does not support embeddings");
        }

        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat/completions";

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["messages"] = messages,
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
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

                var data = line.Substring(6);
                if (data == "[DONE]") break;

                string? textToYield = null;
                try
                {
                    var evt = JsonDocument.Parse(data);
                    var choices = evt.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var deltaContent))
                        {
                            var text = deltaContent.GetString();
                            if (!string.IsNullOrEmpty(text))
                                textToYield = text;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PerplexityProvider] Failed to parse streaming response: {ex.Message}");
                }

                if (textToYield != null)
                    yield return textToYield;
            }
        }

        public Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            throw new NotSupportedException("Perplexity does not support function calling");
        }

        public Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            throw new NotSupportedException("Perplexity does not support vision");
        }

        /// <summary>
        /// Perform a search-enhanced chat with citations.
        /// </summary>
        public async Task<(string Content, List<string> Citations)> SearchChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            // Use an online model for search
            var model = request.Model;
            if (!model.Contains("online"))
            {
                model = "llama-3.1-sonar-large-128k-online";
            }

            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat/completions";

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["return_citations"] = true
            };

            if (request.MaxTokens != null)
                payload["max_tokens"] = request.MaxTokens;
            if (request.Temperature != null)
                payload["temperature"] = request.Temperature;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var textContent = root.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString() ?? "";

            var citations = new List<string>();
            if (root.TryGetProperty("citations", out var citationsArray))
            {
                foreach (var citation in citationsArray.EnumerateArray())
                {
                    var url = citation.GetString();
                    if (!string.IsNullOrEmpty(url))
                        citations.Add(url);
                }
            }

            return (textContent, citations);
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
            throw new NotSupportedException("Perplexity does not support embeddings");
        }

        #endregion
    }
}
