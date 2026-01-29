using DataWarehouse.SDK.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Google Gemini AI provider.
    /// Supports Gemini Pro, Ultra, Flash, and experimental models.
    /// Security: API keys are sent via headers, not URL parameters.
    /// </summary>
    public class GeminiProvider : IExtendedAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
        private const string ApiKeyHeader = "x-goog-api-key";

        public string ProviderType => "Google Gemini";
        public string DefaultModel => _config.DefaultModel ?? "gemini-2.0-flash";
        public string? DefaultVisionModel => "gemini-2.0-flash";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "gemini-2.0-flash",
            "gemini-2.0-flash-thinking",
            "gemini-1.5-pro",
            "gemini-1.5-flash",
            "gemini-1.5-flash-8b",
            "gemini-pro",
            "gemini-pro-vision"
        };

        // SDK IAIProvider properties
        public string ProviderId => "google-gemini";
        public string DisplayName => "Google Gemini";
        public bool IsAvailable => !string.IsNullOrEmpty(_config.ApiKey);
        public AICapabilities Capabilities =>
            AICapabilities.TextCompletion |
            AICapabilities.ChatCompletion |
            AICapabilities.Streaming |
            AICapabilities.Embeddings |
            AICapabilities.ImageAnalysis |
            AICapabilities.FunctionCalling |
            AICapabilities.CodeGeneration;

        public GeminiProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/models/{request.Model}:generateContent";

            var contents = request.Messages.Select(m => new
            {
                role = m.Role == "assistant" ? "model" : m.Role,
                parts = new[] { new { text = m.Content } }
            }).ToList();

            var payload = new
            {
                contents = contents,
                generationConfig = new
                {
                    maxOutputTokens = request.MaxTokens,
                    temperature = request.Temperature,
                    stopSequences = request.StopSequences
                }
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = content;
            httpRequest.Headers.Add(ApiKeyHeader, _config.ApiKey);

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Gemini API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var candidate = root.GetProperty("candidates")[0];
            var textContent = candidate.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

            var usageMetadata = root.TryGetProperty("usageMetadata", out var usage) ? usage : default;
            var promptTokens = usageMetadata.ValueKind != JsonValueKind.Undefined && usageMetadata.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            var candidateTokens = usageMetadata.ValueKind != JsonValueKind.Undefined && usageMetadata.TryGetProperty("candidatesTokenCount", out var ctTokens) ? ctTokens.GetInt32() : 0;

            return new ChatResponse
            {
                Model = request.Model,
                Content = textContent,
                InputTokens = promptTokens,
                OutputTokens = candidateTokens,
                FinishReason = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null
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
            var embedModel = model ?? "text-embedding-004";
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/models/{embedModel}:batchEmbedContents";

            var payload = new
            {
                requests = texts.Select(t => new
                {
                    model = $"models/{embedModel}",
                    content = new { parts = new[] { new { text = t } } }
                })
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = content;
            httpRequest.Headers.Add(ApiKeyHeader, _config.ApiKey);

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.GetProperty("embeddings")
                .EnumerateArray()
                .Select(e => e.GetProperty("values")
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
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/models/{request.Model}:streamGenerateContent";

            var contents = request.Messages.Select(m => new
            {
                role = m.Role == "assistant" ? "model" : m.Role,
                parts = new[] { new { text = m.Content } }
            }).ToList();

            var payload = new
            {
                contents = contents,
                generationConfig = new { maxOutputTokens = request.MaxTokens, temperature = request.Temperature }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = content;
            httpRequest.Headers.Add(ApiKeyHeader, _config.ApiKey);

            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var buffer = new StringBuilder();
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                buffer.Append(line);
                if (line.EndsWith("}"))
                {
                    string? textToYield = null;
                    try
                    {
                        var evt = JsonDocument.Parse(buffer.ToString());
                        if (evt.RootElement.TryGetProperty("candidates", out var candidates) &&
                            candidates.GetArrayLength() > 0)
                        {
                            var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                            if (!string.IsNullOrEmpty(text))
                                textToYield = text;
                        }
                    }
                    catch (Exception ex)
                {
                    Console.WriteLine($"[GeminiProvider] Failed to parse streaming response: {ex.Message}");
                }
                    buffer.Clear();

                    if (textToYield != null)
                        yield return textToYield;
                }
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/models/{request.Model}:generateContent";

            var tools = new[]
            {
                new
                {
                    function_declarations = request.Functions.Select(f => new
                    {
                        name = f.Name,
                        description = f.Description,
                        parameters = f.Parameters
                    })
                }
            };

            var contents = request.Messages.Select(m => new
            {
                role = m.Role == "assistant" ? "model" : m.Role,
                parts = new[] { new { text = m.Content } }
            }).ToList();

            var payload = new { contents = contents, tools = tools };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = content;
            httpRequest.Headers.Add(ApiKeyHeader, _config.ApiKey);

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var candidate = result.RootElement.GetProperty("candidates")[0];
            var parts = candidate.GetProperty("content").GetProperty("parts");

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var functionCall))
                {
                    return new FunctionCallResponse
                    {
                        FunctionName = functionCall.GetProperty("name").GetString(),
                        Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(functionCall.GetProperty("args").GetRawText())
                    };
                }
                if (part.TryGetProperty("text", out var text))
                {
                    return new FunctionCallResponse { Content = text.GetString() };
                }
            }

            return new FunctionCallResponse();
        }

        public async Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/models/{request.Model}:generateContent";

            var parts = new List<object> { new { text = request.Prompt } };

            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                parts.Add(new { inline_data = new { mime_type = "image/png", data = request.ImageBase64 } });
            }
            else if (!string.IsNullOrEmpty(request.ImageUrl))
            {
                // Download and convert to base64 for Gemini
                var imageBytes = await _httpClient.GetByteArrayAsync(request.ImageUrl, ct);
                parts.Add(new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(imageBytes) } });
            }

            var payload = new
            {
                contents = new[] { new { parts = parts } },
                generationConfig = new { maxOutputTokens = request.MaxTokens }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = content;
            httpRequest.Headers.Add(ApiKeyHeader, _config.ApiKey);

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var textContent = result.RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

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
