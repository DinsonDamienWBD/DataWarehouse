using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Azure OpenAI provider (powers Microsoft Copilot).
    /// Supports GPT-4, GPT-4 Turbo, GPT-3.5 Turbo deployments.
    /// </summary>
    public class AzureOpenAIProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private readonly string _apiVersion;

        public string ProviderType => "Azure OpenAI";
        public string DefaultModel => _config.DefaultModel ?? "gpt-4";
        public string? DefaultVisionModel => "gpt-4-vision";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "gpt-4",
            "gpt-4-turbo",
            "gpt-4-vision",
            "gpt-4o",
            "gpt-4o-mini",
            "gpt-35-turbo",
            "text-embedding-ada-002",
            "text-embedding-3-small",
            "text-embedding-3-large"
        };

        // SDK IAIProvider properties
        public string ProviderId => "azure-openai";
        public string DisplayName => "Azure OpenAI";
        public bool IsAvailable => !string.IsNullOrEmpty(_config.ApiKey) && !string.IsNullOrEmpty(_config.Endpoint);
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming |
            AICapabilities.Embeddings |
            AICapabilities.ImageAnalysis |
            AICapabilities.FunctionCalling |
            AICapabilities.CodeGeneration;

        public AzureOpenAIProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;
            _apiVersion = config.AdditionalSettings?.TryGetValue("api_version", out var version) == true
                ? version?.ToString() ?? "2024-02-15-preview"
                : "2024-02-15-preview";

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("api-key", config.ApiKey);
        }

        private string GetEndpoint(string deploymentName, string operation = "chat/completions")
        {
            // Azure format: https://{resource}.openai.azure.com/openai/deployments/{deployment}/{operation}?api-version={version}
            var baseUrl = _config.Endpoint?.TrimEnd('/') ?? throw new InvalidOperationException("Azure endpoint is required");
            return $"{baseUrl}/openai/deployments/{deploymentName}/{operation}?api-version={_apiVersion}";
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new Dictionary<string, object>
            {
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

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Azure OpenAI API error: {responseBody}");
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

        public async Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            var deploymentName = model ?? "text-embedding-ada-002";
            var endpoint = GetEndpoint(deploymentName, "embeddings");

            var payload = new { input = texts };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            var response = await _httpClient.SendAsync(httpRequest, ct);
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
            var endpoint = GetEndpoint(request.Model);

            var messages = request.Messages.Select(m => new
            {
                role = m.Role,
                content = m.Content
            }).ToList();

            var payload = new Dictionary<string, object>
            {
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
                    Console.WriteLine($"[AzureOpenAIProvider] Failed to parse streaming response chunk: {ex.Message}");
                }

                if (textToYield != null)
                    yield return textToYield;
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

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

            var payload = new { messages = messages, tools = tools };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            var response = await _httpClient.SendAsync(httpRequest, ct);
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
            var endpoint = GetEndpoint(request.Model);

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
                messages = new[]
                {
                    new { role = "user", content = contentParts }
                },
                max_tokens = request.MaxTokens ?? 4096
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var textContent = result.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString() ?? "";

            return new VisionResponse { Content = textContent };
        }

        #region SDK IAIProvider Implementation

        public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
        {
            try
            {
                var messages = new List<ChatMessage>();
                if (!string.IsNullOrEmpty(request.SystemMessage))
                {
                    messages.Add(new ChatMessage { Role = "system", Content = request.SystemMessage });
                }
                foreach (var msg in request.ChatHistory)
                {
                    messages.Add(new ChatMessage { Role = msg.Role, Content = msg.Content });
                }
                messages.Add(new ChatMessage { Role = "user", Content = request.Prompt });

                var chatRequest = new ChatRequest
                {
                    Model = request.Model ?? DefaultModel,
                    Messages = messages,
                    MaxTokens = request.MaxTokens,
                    Temperature = request.Temperature.HasValue ? (double)request.Temperature.Value : null
                };

                var response = await ChatAsync(chatRequest, ct);
                return new AIResponse
                {
                    Success = true,
                    Content = response.Content,
                    FinishReason = response.FinishReason,
                    Usage = new AIUsage
                    {
                        PromptTokens = response.InputTokens,
                        CompletionTokens = response.OutputTokens
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
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(request.SystemMessage))
            {
                messages.Add(new ChatMessage { Role = "system", Content = request.SystemMessage });
            }
            foreach (var msg in request.ChatHistory)
            {
                messages.Add(new ChatMessage { Role = msg.Role, Content = msg.Content });
            }
            messages.Add(new ChatMessage { Role = "user", Content = request.Prompt });

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? DefaultModel,
                Messages = messages,
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature.HasValue ? (double)request.Temperature.Value : null
            };

            await foreach (var chunk in StreamChatAsync(chatRequest, ct))
            {
                yield return new AIStreamChunk { Content = chunk, IsFinal = false };
            }

            yield return new AIStreamChunk { Content = "", IsFinal = true };
        }

        public async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
        {
            var result = await EmbedAsync(texts, null, ct);
            return result.Select(d => d.Select(v => (float)v).ToArray()).ToArray();
        }

        #endregion
    }
}
