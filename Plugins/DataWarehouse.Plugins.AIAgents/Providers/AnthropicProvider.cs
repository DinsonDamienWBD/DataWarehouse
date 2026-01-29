using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Anthropic Claude AI provider.
    /// Supports Claude 3 (Opus, Sonnet, Haiku) and Claude 4 models.
    /// </summary>
    public class AnthropicProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://api.anthropic.com/v1";

        public string ProviderType => "Anthropic";
        public string DefaultModel => _config.DefaultModel ?? "claude-sonnet-4-20250514";
        public string? DefaultVisionModel => "claude-sonnet-4-20250514";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => false; // Anthropic doesn't offer embeddings yet

        public string[] AvailableModels => new[]
        {
            "claude-opus-4-20250514",
            "claude-sonnet-4-20250514",
            "claude-3-5-sonnet-20241022",
            "claude-3-5-haiku-20241022",
            "claude-3-opus-20240229",
            "claude-3-sonnet-20240229",
            "claude-3-haiku-20240307"
        };

        // SDK IAIProvider properties
        public string ProviderId => "anthropic";
        public string DisplayName => "Anthropic Claude";
        public bool IsAvailable => !string.IsNullOrEmpty(_config.ApiKey);
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming |
            AICapabilities.ImageAnalysis |
            AICapabilities.FunctionCalling |
            AICapabilities.CodeGeneration;

        public AnthropicProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/messages";

            var systemMessage = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
            var messages = request.Messages.Where(m => m.Role != "system").Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new
            {
                model = request.Model,
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                system = systemMessage,
                messages = messages,
                stop_sequences = request.StopSequences
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            httpRequest.Headers.Add("x-api-key", _config.ApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Anthropic API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var textContent = root.GetProperty("content")
                .EnumerateArray()
                .FirstOrDefault(c => c.GetProperty("type").GetString() == "text")
                .GetProperty("text")
                .GetString() ?? "";

            var usage = root.GetProperty("usage");

            return new ChatResponse
            {
                Model = root.GetProperty("model").GetString() ?? request.Model,
                Content = textContent,
                InputTokens = usage.GetProperty("input_tokens").GetInt32(),
                OutputTokens = usage.GetProperty("output_tokens").GetInt32(),
                FinishReason = root.GetProperty("stop_reason").GetString()
            };
        }

        public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            // Claude uses chat API for completions
            var chatRequest = new ChatRequest
            {
                Model = request.Model,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "user", Content = request.Prompt }
                },
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
            throw new NotSupportedException("Anthropic does not support embeddings. Use OpenAI or Cohere for embeddings.");
        }

        public Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
        {
            throw new NotSupportedException("Anthropic does not support embeddings. Use OpenAI or Cohere for embeddings.");
        }

        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/messages";

            var systemMessage = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
            var messages = request.Messages.Where(m => m.Role != "system").Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new
            {
                model = request.Model,
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                system = systemMessage,
                messages = messages,
                stream = true
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            httpRequest.Headers.Add("x-api-key", _config.ApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                    continue;

                var data = line.Substring(6);
                if (data == "[DONE]")
                    break;

                string? textChunk = null;
                try
                {
                    var evt = JsonDocument.Parse(data);
                    if (evt.RootElement.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("text", out var text))
                    {
                        textChunk = text.GetString();
                    }
                }
                catch
                {
                    // Skip malformed JSON
                }

                if (textChunk != null)
                    yield return textChunk;
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/messages";

            var tools = request.Functions.Select(f => new
            {
                name = f.Name,
                description = f.Description,
                input_schema = f.Parameters
            }).ToList();

            var systemMessage = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
            var messages = request.Messages.Where(m => m.Role != "system").Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new
            {
                model = request.Model,
                max_tokens = 4096,
                system = systemMessage,
                messages = messages,
                tools = tools
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            httpRequest.Headers.Add("x-api-key", _config.ApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var contentArray = root.GetProperty("content");
            foreach (var item in contentArray.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "tool_use")
                {
                    return new FunctionCallResponse
                    {
                        FunctionName = item.GetProperty("name").GetString(),
                        Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(item.GetProperty("input").GetRawText())
                    };
                }
                if (item.GetProperty("type").GetString() == "text")
                {
                    return new FunctionCallResponse
                    {
                        Content = item.GetProperty("text").GetString()
                    };
                }
            }

            return new FunctionCallResponse();
        }

        public async Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/messages";

            var imageContent = new List<object>();

            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                imageContent.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/png",
                        data = request.ImageBase64
                    }
                });
            }
            else if (!string.IsNullOrEmpty(request.ImageUrl))
            {
                imageContent.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "url",
                        url = request.ImageUrl
                    }
                });
            }

            imageContent.Add(new { type = "text", text = request.Prompt });

            var payload = new
            {
                model = request.Model,
                max_tokens = request.MaxTokens,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = imageContent
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            httpRequest.Headers.Add("x-api-key", _config.ApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var textContent = root.GetProperty("content")
                .EnumerateArray()
                .FirstOrDefault(c => c.GetProperty("type").GetString() == "text")
                .GetProperty("text")
                .GetString() ?? "";

            var usage = root.GetProperty("usage");

            return new VisionResponse
            {
                Content = textContent,
                InputTokens = usage.GetProperty("input_tokens").GetInt32(),
                OutputTokens = usage.GetProperty("output_tokens").GetInt32()
            };
        }

        #region SDK IAIProvider Implementation

        public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
        {
            try
            {
                var endpoint = $"{_config.Endpoint ?? BaseUrl}/messages";

                var messages = new List<object>();
                foreach (var msg in request.ChatHistory)
                {
                    messages.Add(new { role = msg.Role, content = msg.Content });
                }
                messages.Add(new { role = "user", content = request.Prompt });

                var payload = new
                {
                    model = request.Model ?? DefaultModel,
                    max_tokens = request.MaxTokens ?? 4096,
                    temperature = request.Temperature,
                    system = request.SystemMessage,
                    messages = messages
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                httpRequest.Headers.Add("x-api-key", _config.ApiKey);
                httpRequest.Headers.Add("anthropic-version", "2023-06-01");

                var response = await _httpClient.SendAsync(httpRequest, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    return new AIResponse
                    {
                        Success = false,
                        ErrorMessage = $"Anthropic API error: {responseBody}"
                    };
                }

                var result = JsonDocument.Parse(responseBody);
                var root = result.RootElement;

                var textContent = root.GetProperty("content")
                    .EnumerateArray()
                    .FirstOrDefault(c => c.GetProperty("type").GetString() == "text")
                    .GetProperty("text")
                    .GetString() ?? "";

                var usage = root.GetProperty("usage");

                return new AIResponse
                {
                    Success = true,
                    Content = textContent,
                    FinishReason = root.GetProperty("stop_reason").GetString(),
                    Usage = new AIUsage
                    {
                        PromptTokens = usage.GetProperty("input_tokens").GetInt32(),
                        CompletionTokens = usage.GetProperty("output_tokens").GetInt32()
                    }
                };
            }
            catch (Exception ex)
            {
                return new AIResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/messages";

            var messages = new List<object>();
            foreach (var msg in request.ChatHistory)
            {
                messages.Add(new { role = msg.Role, content = msg.Content });
            }
            messages.Add(new { role = "user", content = request.Prompt });

            var payload = new
            {
                model = request.Model ?? DefaultModel,
                max_tokens = request.MaxTokens ?? 4096,
                temperature = request.Temperature,
                system = request.SystemMessage,
                messages = messages,
                stream = true
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            httpRequest.Headers.Add("x-api-key", _config.ApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                    continue;

                var data = line.Substring(6);
                if (data == "[DONE]")
                {
                    yield return new AIStreamChunk { Content = "", IsFinal = true };
                    break;
                }

                string? textChunk = null;
                bool shouldBreak = false;

                try
                {
                    var evt = JsonDocument.Parse(data);
                    if (evt.RootElement.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("text", out var text))
                    {
                        textChunk = text.GetString();
                    }
                    if (evt.RootElement.TryGetProperty("type", out var typeElem) &&
                        typeElem.GetString() == "message_stop")
                    {
                        shouldBreak = true;
                    }
                }
                catch
                {
                    continue;
                }

                if (shouldBreak)
                {
                    yield return new AIStreamChunk { Content = "", IsFinal = true };
                    break;
                }

                if (textChunk != null)
                {
                    yield return new AIStreamChunk
                    {
                        Content = textChunk,
                        IsFinal = false
                    };
                }
            }
        }

        public Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
        {
            throw new NotSupportedException("Anthropic does not support embeddings. Use OpenAI or Cohere for embeddings.");
        }

        #endregion
    }
}
