using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// HuggingFace Inference API provider.
    /// Supports any model hosted on HuggingFace Hub.
    /// </summary>
    public class HuggingFaceProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private const string BaseUrl = "https://api-inference.huggingface.co";

        public string ProviderType => "HuggingFace";
        public string DefaultModel => _config.DefaultModel ?? "meta-llama/Llama-3.2-3B-Instruct";
        public string? DefaultVisionModel => "llava-hf/llava-1.5-7b-hf";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => false;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "meta-llama/Llama-3.2-3B-Instruct",
            "meta-llama/Llama-3.2-1B-Instruct",
            "mistralai/Mistral-7B-Instruct-v0.3",
            "microsoft/Phi-3.5-mini-instruct",
            "google/gemma-2-2b-it",
            "Qwen/Qwen2.5-7B-Instruct",
            "HuggingFaceH4/zephyr-7b-beta",
            "sentence-transformers/all-MiniLM-L6-v2",
            "BAAI/bge-large-en-v1.5",
            "llava-hf/llava-1.5-7b-hf",
            "Salesforce/blip2-opt-2.7b"
        };

        public HuggingFaceProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        private string GetEndpoint(string model)
        {
            var baseUrl = _config.Endpoint ?? BaseUrl;
            return $"{baseUrl}/models/{model}";
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

            // HuggingFace uses text-generation format
            var prompt = FormatMessagesAsPrompt(request.Messages);

            var payload = new Dictionary<string, object>
            {
                ["inputs"] = prompt,
                ["parameters"] = new Dictionary<string, object>
                {
                    ["max_new_tokens"] = request.MaxTokens ?? 1024,
                    ["temperature"] = request.Temperature ?? 0.7,
                    ["return_full_text"] = false
                }
            };

            if (request.StopSequences?.Any() == true)
            {
                ((Dictionary<string, object>)payload["parameters"])["stop_sequences"] = request.StopSequences;
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HuggingFace API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            string textContent;
            if (root.ValueKind == JsonValueKind.Array)
            {
                textContent = root[0].GetProperty("generated_text").GetString() ?? "";
            }
            else
            {
                textContent = root.GetProperty("generated_text").GetString() ?? "";
            }

            return new ChatResponse
            {
                Model = request.Model,
                Content = textContent,
                InputTokens = 0, // HuggingFace doesn't always return token counts
                OutputTokens = 0,
                FinishReason = "stop"
            };
        }

        private string FormatMessagesAsPrompt(List<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
            {
                if (msg.Role == "system")
                {
                    sb.AppendLine($"<|system|>\n{msg.Content}</s>");
                }
                else if (msg.Role == "user")
                {
                    sb.AppendLine($"<|user|>\n{msg.Content}</s>");
                }
                else if (msg.Role == "assistant")
                {
                    sb.AppendLine($"<|assistant|>\n{msg.Content}</s>");
                }
            }
            sb.AppendLine("<|assistant|>");
            return sb.ToString();
        }

        public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

            var payload = new Dictionary<string, object>
            {
                ["inputs"] = request.Prompt,
                ["parameters"] = new Dictionary<string, object>
                {
                    ["max_new_tokens"] = request.MaxTokens ?? 1024,
                    ["temperature"] = request.Temperature ?? 0.7,
                    ["return_full_text"] = false
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            string textContent;
            if (root.ValueKind == JsonValueKind.Array)
            {
                textContent = root[0].GetProperty("generated_text").GetString() ?? "";
            }
            else
            {
                textContent = root.GetProperty("generated_text").GetString() ?? "";
            }

            return new CompletionResponse
            {
                Text = textContent,
                InputTokens = 0,
                OutputTokens = 0
            };
        }

        public async Task<double[][]> EmbedAsync(string[] texts, string? model = null, CancellationToken ct = default)
        {
            var embedModel = model ?? "sentence-transformers/all-MiniLM-L6-v2";
            var endpoint = GetEndpoint(embedModel);

            var payload = new { inputs = texts };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.EnumerateArray()
                .Select(e => e.EnumerateArray()
                    .Select(v => v.GetDouble())
                    .ToArray())
                .ToArray();
        }

        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

            var prompt = FormatMessagesAsPrompt(request.Messages);

            var payload = new Dictionary<string, object>
            {
                ["inputs"] = prompt,
                ["parameters"] = new Dictionary<string, object>
                {
                    ["max_new_tokens"] = request.MaxTokens ?? 1024,
                    ["temperature"] = request.Temperature ?? 0.7,
                    ["return_full_text"] = false
                },
                ["stream"] = true
            };

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

                // Handle SSE format
                if (line.StartsWith("data:"))
                {
                    var data = line.Substring(5).Trim();
                    if (string.IsNullOrEmpty(data)) continue;

                    string? valueToYield = null;
                    try
                    {
                        var evt = JsonDocument.Parse(data);
                        if (evt.RootElement.TryGetProperty("token", out var token) &&
                            token.TryGetProperty("text", out var text))
                        {
                            var textStr = text.GetString();
                            if (!string.IsNullOrEmpty(textStr))
                                valueToYield = textStr;
                        }
                    }
                    catch { }

                    if (valueToYield != null)
                        yield return valueToYield;
                }
            }
        }

        public Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            throw new NotSupportedException("HuggingFace Inference API does not support function calling");
        }

        public async Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

            byte[] imageData;
            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                imageData = Convert.FromBase64String(request.ImageBase64);
            }
            else if (!string.IsNullOrEmpty(request.ImageUrl))
            {
                imageData = await _httpClient.GetByteArrayAsync(request.ImageUrl, ct);
            }
            else
            {
                throw new ArgumentException("Image data required for vision request");
            }

            // For vision models, we send the image directly with the prompt
            var payload = new
            {
                inputs = new
                {
                    image = Convert.ToBase64String(imageData),
                    text = request.Prompt
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            string textContent;
            if (result.RootElement.ValueKind == JsonValueKind.Array)
            {
                textContent = result.RootElement[0].TryGetProperty("generated_text", out var gt)
                    ? gt.GetString() ?? ""
                    : result.RootElement[0].ToString();
            }
            else if (result.RootElement.TryGetProperty("generated_text", out var genText))
            {
                textContent = genText.GetString() ?? "";
            }
            else
            {
                textContent = result.RootElement.ToString();
            }

            return new VisionResponse { Content = textContent };
        }

        /// <summary>
        /// Perform image classification.
        /// </summary>
        public async Task<List<(string Label, double Score)>> ClassifyImageAsync(byte[] imageData, string? model = null)
        {
            var classifyModel = model ?? "google/vit-base-patch16-224";
            var endpoint = GetEndpoint(classifyModel);

            using var content = new ByteArrayContent(imageData);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = await _httpClient.PostAsync(endpoint, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.EnumerateArray()
                .Select(e => (
                    Label: e.GetProperty("label").GetString() ?? "",
                    Score: e.GetProperty("score").GetDouble()
                ))
                .ToList();
        }

        /// <summary>
        /// Perform text classification/sentiment analysis.
        /// </summary>
        public async Task<List<(string Label, double Score)>> ClassifyTextAsync(string text, string? model = null)
        {
            var classifyModel = model ?? "distilbert-base-uncased-finetuned-sst-2-english";
            var endpoint = GetEndpoint(classifyModel);

            var payload = new { inputs = text };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(responseBody);

            var results = new List<(string Label, double Score)>();
            var root = result.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root[0].ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root[0].EnumerateArray())
                {
                    results.Add((
                        Label: item.GetProperty("label").GetString() ?? "",
                        Score: item.GetProperty("score").GetDouble()
                    ));
                }
            }

            return results;
        }

        /// <summary>
        /// Perform named entity recognition.
        /// </summary>
        public async Task<List<(string Entity, string Type, double Score, int Start, int End)>> ExtractEntitiesAsync(string text, string? model = null)
        {
            var nerModel = model ?? "dslim/bert-base-NER";
            var endpoint = GetEndpoint(nerModel);

            var payload = new { inputs = text };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(responseBody);

            return result.RootElement.EnumerateArray()
                .Select(e => (
                    Entity: e.GetProperty("word").GetString() ?? "",
                    Type: e.GetProperty("entity_group").GetString() ?? "",
                    Score: e.GetProperty("score").GetDouble(),
                    Start: e.GetProperty("start").GetInt32(),
                    End: e.GetProperty("end").GetInt32()
                ))
                .ToList();
        }

        /// <summary>
        /// Perform question answering.
        /// </summary>
        public async Task<(string Answer, double Score, int Start, int End)> AnswerQuestionAsync(string question, string context, string? model = null)
        {
            var qaModel = model ?? "deepset/roberta-base-squad2";
            var endpoint = GetEndpoint(qaModel);

            var payload = new
            {
                inputs = new
                {
                    question = question,
                    context = context
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(endpoint, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            return (
                Answer: root.GetProperty("answer").GetString() ?? "",
                Score: root.GetProperty("score").GetDouble(),
                Start: root.GetProperty("start").GetInt32(),
                End: root.GetProperty("end").GetInt32()
            );
        }
    }
}
