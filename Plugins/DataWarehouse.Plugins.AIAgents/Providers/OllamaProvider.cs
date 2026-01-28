using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Ollama provider for local/self-hosted AI models.
    /// Supports Llama, Mistral, CodeLlama, Phi, and other open models.
    /// </summary>
    public class OllamaProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string DefaultBaseUrl = "http://localhost:11434";

        public string ProviderType => "Ollama";
        public string DefaultModel => _config.DefaultModel ?? "llama3.2";
        public string? DefaultVisionModel => "llava";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true; // Ollama 0.3+ supports tools
        public bool SupportsVision => true; // With llava, bakllava models
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "llama3.2",
            "llama3.2:70b",
            "llama3.1",
            "llama3.1:405b",
            "mistral",
            "mistral-nemo",
            "mixtral",
            "codellama",
            "codellama:70b",
            "phi3",
            "phi3:medium",
            "gemma2",
            "gemma2:27b",
            "qwen2.5",
            "qwen2.5:72b",
            "llava",
            "llava:34b",
            "bakllava",
            "deepseek-coder-v2",
            "command-r",
            "command-r-plus"
        };

        public string ProviderId => "ollama";
        public string DisplayName => "Ollama";
        public bool IsAvailable => true; // Local Ollama is always "available" if the service is running
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming |
            AICapabilities.Embeddings;

        public OllamaProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        private string BaseUrl => _config.Endpoint ?? DefaultBaseUrl;

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{BaseUrl}/api/chat";

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["messages"] = messages,
                ["stream"] = false
            };

            if (request.MaxTokens != null || request.Temperature != null)
            {
                var options = new Dictionary<string, object>();
                if (request.MaxTokens != null)
                    options["num_predict"] = request.MaxTokens;
                if (request.Temperature != null)
                    options["temperature"] = request.Temperature;
                if (request.StopSequences?.Any() == true)
                    options["stop"] = request.StopSequences;
                payload["options"] = options;
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Ollama API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var message = root.GetProperty("message");
            var textContent = message.GetProperty("content").GetString() ?? "";

            // Ollama provides token counts in eval_count and prompt_eval_count
            var promptTokens = root.TryGetProperty("prompt_eval_count", out var pt) ? pt.GetInt32() : 0;
            var completionTokens = root.TryGetProperty("eval_count", out var evalCount) ? evalCount.GetInt32() : 0;

            return new ChatResponse
            {
                Model = request.Model,
                Content = textContent,
                InputTokens = promptTokens,
                OutputTokens = completionTokens,
                FinishReason = root.TryGetProperty("done_reason", out var dr) ? dr.GetString() : "stop"
            };
        }

        public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{BaseUrl}/api/generate";

            var payload = new Dictionary<string, object>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["stream"] = false
            };

            if (request.MaxTokens != null || request.Temperature != null)
            {
                var options = new Dictionary<string, object>();
                if (request.MaxTokens != null)
                    options["num_predict"] = request.MaxTokens;
                if (request.Temperature != null)
                    options["temperature"] = request.Temperature;
                if (request.StopSequences?.Any() == true)
                    options["stop"] = request.StopSequences;
                payload["options"] = options;
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            return new CompletionResponse
            {
                Text = root.GetProperty("response").GetString() ?? "",
                InputTokens = root.TryGetProperty("prompt_eval_count", out var pt) ? pt.GetInt32() : 0,
                OutputTokens = root.TryGetProperty("eval_count", out var evalCount) ? evalCount.GetInt32() : 0
            };
        }

        public async Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            var embedModel = model ?? "nomic-embed-text";
            var endpoint = $"{BaseUrl}/api/embed";

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
            var endpoint = $"{BaseUrl}/api/chat";

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

            if (request.MaxTokens != null || request.Temperature != null)
            {
                var options = new Dictionary<string, object>();
                if (request.MaxTokens != null)
                    options["num_predict"] = request.MaxTokens;
                if (request.Temperature != null)
                    options["temperature"] = request.Temperature;
                payload["options"] = options;
            }

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

                string? textToYield = null;
                bool shouldBreak = false;

                try
                {
                    var evt = JsonDocument.Parse(line);
                    var root = evt.RootElement;

                    if (root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var msgContent))
                    {
                        var text = msgContent.GetString();
                        if (!string.IsNullOrEmpty(text))
                            textToYield = text;
                    }

                    // Check if done
                    if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                        shouldBreak = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OllamaProvider] Failed to parse streaming response: {ex.Message}");
                }

                if (textToYield != null)
                    yield return textToYield;

                if (shouldBreak)
                    break;
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{BaseUrl}/api/chat";

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

            var payload = new
            {
                model = request.Model,
                messages = messages,
                tools = tools,
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var message = result.RootElement.GetProperty("message");

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                var toolCall = toolCalls[0];
                var function = toolCall.GetProperty("function");
                return new FunctionCallResponse
                {
                    FunctionName = function.GetProperty("name").GetString(),
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        function.GetProperty("arguments").GetRawText())
                };
            }

            return new FunctionCallResponse
            {
                Content = message.TryGetProperty("content", out var c) ? c.GetString() : null
            };
        }

        public async Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{BaseUrl}/api/chat";

            var images = new List<string>();
            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                images.Add(request.ImageBase64);
            }
            else if (!string.IsNullOrEmpty(request.ImageUrl))
            {
                // Download and convert to base64
                var imageBytes = await _httpClient.GetByteArrayAsync(request.ImageUrl, ct);
                images.Add(Convert.ToBase64String(imageBytes));
            }

            var payload = new
            {
                model = request.Model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = request.Prompt,
                        images = images
                    }
                },
                stream = false,
                options = new { num_predict = request.MaxTokens ?? 4096 }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var textContent = result.RootElement.GetProperty("message")
                .GetProperty("content").GetString() ?? "";

            return new VisionResponse { Content = textContent };
        }

        /// <summary>
        /// List available models from the Ollama server.
        /// </summary>
        public async Task<string[]> ListModelsAsync(CancellationToken ct = default)
        {
            var endpoint = $"{BaseUrl}/api/tags";
            var response = await _httpClient.GetAsync(endpoint, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
        }

        /// <summary>
        /// Pull a model from the Ollama library.
        /// </summary>
        public async Task PullModelAsync(string modelName, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var endpoint = $"{BaseUrl}/api/pull";
            var payload = new { name = modelName, stream = true };
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

                try
                {
                    var evt = JsonDocument.Parse(line);
                    var root = evt.RootElement;

                    if (root.TryGetProperty("completed", out var completed) &&
                        root.TryGetProperty("total", out var total) &&
                        total.GetInt64() > 0)
                    {
                        var pct = (double)completed.GetInt64() / total.GetInt64();
                        progress?.Report(pct);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OllamaProvider] Failed to parse streaming response: {ex.Message}");
                }
            }
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
