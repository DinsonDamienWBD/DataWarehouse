using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// Together AI provider for open-source model hosting.
    /// Supports Llama, Mistral, Qwen, and many other open models.
    /// </summary>
    public class TogetherProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://api.together.xyz/v1";

        public string ProviderType => "Together AI";
        public string DefaultModel => _config.DefaultModel ?? "meta-llama/Llama-3.3-70B-Instruct-Turbo";
        public string? DefaultVisionModel => "meta-llama/Llama-3.2-90B-Vision-Instruct-Turbo";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "meta-llama/Llama-3.3-70B-Instruct-Turbo",
            "meta-llama/Llama-3.2-90B-Vision-Instruct-Turbo",
            "meta-llama/Llama-3.2-11B-Vision-Instruct-Turbo",
            "meta-llama/Meta-Llama-3.1-405B-Instruct-Turbo",
            "meta-llama/Meta-Llama-3.1-70B-Instruct-Turbo",
            "meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo",
            "mistralai/Mixtral-8x22B-Instruct-v0.1",
            "mistralai/Mistral-7B-Instruct-v0.3",
            "Qwen/Qwen2.5-72B-Instruct-Turbo",
            "Qwen/Qwen2.5-Coder-32B-Instruct",
            "deepseek-ai/DeepSeek-V3",
            "deepseek-ai/DeepSeek-R1-Distill-Llama-70B",
            "google/gemma-2-27b-it",
            "databricks/dbrx-instruct",
            "togethercomputer/m2-bert-80M-8k-retrieval",
            "WhereIsAI/UAE-Large-V1"
        };

        public TogetherProvider(HttpClient httpClient, ProviderConfig config)
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
                throw new HttpRequestException($"Together API error: {responseBody}");
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
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/completions";

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
                payload["stop"] = request.StopSequences;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            var choice = root.GetProperty("choices")[0];
            var usage = root.GetProperty("usage");

            return new CompletionResponse
            {
                Text = choice.GetProperty("text").GetString() ?? "",
                InputTokens = usage.GetProperty("prompt_tokens").GetInt32(),
                OutputTokens = usage.GetProperty("completion_tokens").GetInt32()
            };
        }

        public async Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            var embedModel = model ?? "togethercomputer/m2-bert-80M-8k-retrieval";
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
                    Console.WriteLine($"[TogetherProvider] Failed to parse streaming response: {ex.Message}");
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

        /// <summary>
        /// Generate images using Together's image generation models.
        /// </summary>
        public async Task<byte[]> GenerateImageAsync(string prompt, string? model = null, int width = 1024, int height = 1024)
        {
            var imageModel = model ?? "stabilityai/stable-diffusion-xl-base-1.0";
            var endpoint = $"{_config.Endpoint ?? BaseUrl}/images/generations";

            var payload = new
            {
                model = imageModel,
                prompt = prompt,
                width = width,
                height = height,
                n = 1,
                response_format = "b64_json"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(responseBody);

            var b64 = result.RootElement.GetProperty("data")[0]
                .GetProperty("b64_json").GetString() ?? "";

            return Convert.FromBase64String(b64);
        }
    }
}
