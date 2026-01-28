using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Mistral AI provider.
    /// Supports Mistral Large, Medium, Small, and Codestral models.
    /// </summary>
    public class MistralProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://api.mistral.ai/v1";

        public string ProviderType => "Mistral AI";
        public string DefaultModel => _config.DefaultModel ?? "mistral-large-latest";
        public string? DefaultVisionModel => "pixtral-large-latest";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "mistral-large-latest",
            "mistral-large-2411",
            "pixtral-large-latest",
            "ministral-3b-latest",
            "ministral-8b-latest",
            "mistral-small-latest",
            "codestral-latest",
            "mistral-embed",
            "mistral-moderation-latest"
        };

        public string ProviderId => "mistral";
        public string DisplayName => "Mistral AI";
        public bool IsAvailable => !string.IsNullOrEmpty(_config.ApiKey);
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming |
            AICapabilities.Embeddings;

        public MistralProvider(HttpClient httpClient, ProviderConfig config)
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
            if (request.StopSequences?.Any() == true)
                payload["stop"] = request.StopSequences;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Mistral API error: {responseBody}");
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
            // Mistral uses chat completions for everything
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

        public async Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            var embedModel = model ?? "mistral-embed";
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/embeddings";

            var payload = new
            {
                model = embedModel,
                input = texts
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.GetProperty("data")
                .EnumerateArray()
                .OrderBy(e => e.GetProperty("index").GetInt32())
                .Select(e => e.GetProperty("embedding")
                    .EnumerateArray()
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

                string? text = null;
                try
                {
                    var evt = JsonDocument.Parse(data);
                    var choices = evt.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var deltaContent))
                        {
                            text = deltaContent.GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MistralProvider] Failed to parse streaming response: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat/completions";

            var tools = request.Functions.Select(f => new
            {
                type = "function",
                function = new
                {
                    name = f.Name,
                    description = f.Description,
                    parameters = f.Parameters
                }
            }).ToList();

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new { model = request.Model, messages = messages, tools = tools };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var choice = result.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                var toolCall = toolCalls[0];
                var function = toolCall.GetProperty("function");
                return new FunctionCallResponse
                {
                    FunctionName = function.GetProperty("name").GetString(),
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(function.GetProperty("arguments").GetString() ?? "{}")
                };
            }

            return new FunctionCallResponse
            {
                Content = message.TryGetProperty("content", out var c) ? c.GetString() : null
            };
        }

        public async Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/chat/completions";

            var contentParts = new List<object>
            {
                new { type = "text", text = request.Prompt }
            };

            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                contentParts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:image/png;base64,{request.ImageBase64}" }
                });
            }
            else if (!string.IsNullOrEmpty(request.ImageUrl))
            {
                contentParts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = request.ImageUrl }
                });
            }

            var payload = new
            {
                model = request.Model,
                messages = new[]
                {
                    new { role = "user", content = contentParts }
                },
                max_tokens = request.MaxTokens ?? 4096
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var textContent = result.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString() ?? "";

            return new VisionResponse { Content = textContent };
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
