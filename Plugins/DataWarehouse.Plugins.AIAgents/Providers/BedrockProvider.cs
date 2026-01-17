using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIAgents
{
    /// <summary>
    /// AWS Bedrock provider for enterprise AI models.
    /// Supports Claude, Llama, Titan, and other models via AWS.
    /// </summary>
    public class BedrockProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ProviderConfig _config;
        private readonly string _region;
        private readonly string _accessKey;
        private readonly string _secretKey;

        public string ProviderType => "AWS Bedrock";
        public string DefaultModel => _config.DefaultModel ?? "anthropic.claude-3-5-sonnet-20241022-v2:0";
        public string? DefaultVisionModel => "anthropic.claude-3-5-sonnet-20241022-v2:0";
        public bool SupportsStreaming => true;
        public bool SupportsFunctionCalling => true;
        public bool SupportsVision => true;
        public bool SupportsEmbeddings => true;

        public string[] AvailableModels => new[]
        {
            "anthropic.claude-3-5-sonnet-20241022-v2:0",
            "anthropic.claude-3-5-haiku-20241022-v1:0",
            "anthropic.claude-3-opus-20240229-v1:0",
            "anthropic.claude-3-sonnet-20240229-v1:0",
            "anthropic.claude-3-haiku-20240307-v1:0",
            "meta.llama3-2-90b-instruct-v1:0",
            "meta.llama3-2-11b-instruct-v1:0",
            "meta.llama3-1-405b-instruct-v1:0",
            "meta.llama3-1-70b-instruct-v1:0",
            "mistral.mistral-large-2407-v1:0",
            "mistral.mixtral-8x7b-instruct-v0:1",
            "amazon.titan-text-premier-v1:0",
            "amazon.titan-text-express-v1",
            "amazon.titan-embed-text-v2:0",
            "cohere.command-r-plus-v1:0",
            "cohere.embed-english-v3"
        };

        public BedrockProvider(HttpClient httpClient, ProviderConfig config)
        {
            _httpClient = httpClient;
            _config = config;

            _region = config.AdditionalSettings?.TryGetValue("region", out var region) == true
                ? region?.ToString() ?? "us-east-1"
                : "us-east-1";

            _accessKey = config.AdditionalSettings?.TryGetValue("access_key", out var ak) == true
                ? ak?.ToString() ?? ""
                : config.ApiKey ?? "";

            _secretKey = config.AdditionalSettings?.TryGetValue("secret_key", out var sk) == true
                ? sk?.ToString() ?? ""
                : "";
        }

        private string GetEndpoint(string modelId)
        {
            var baseUrl = _config.Endpoint ?? $"https://bedrock-runtime.{_region}.amazonaws.com";
            return $"{baseUrl}/model/{modelId}/invoke";
        }

        private async Task<HttpResponseMessage> SendSignedRequestAsync(string endpoint, string body, CancellationToken ct = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            // AWS Signature Version 4
            var datetime = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var date = datetime.Substring(0, 8);

            request.Headers.Add("x-amz-date", datetime);
            request.Headers.Add("host", new Uri(endpoint).Host);

            // Simplified signing - in production use AWS SDK
            var signature = SignRequest(request, body, datetime, date);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "AWS4-HMAC-SHA256",
                $"Credential={_accessKey}/{date}/{_region}/bedrock/aws4_request, " +
                $"SignedHeaders=content-type;host;x-amz-date, Signature={signature}");

            return await _httpClient.SendAsync(request, ct);
        }

        private string SignRequest(HttpRequestMessage request, string body, string datetime, string date)
        {
            // Simplified AWS Signature V4 implementation
            var canonicalRequest = CreateCanonicalRequest(request, body, datetime);
            var stringToSign = CreateStringToSign(canonicalRequest, datetime, date);
            var signingKey = GetSigningKey(date);
            return ComputeSignature(signingKey, stringToSign);
        }

        private string CreateCanonicalRequest(HttpRequestMessage request, string body, string datetime)
        {
            var uri = new Uri(request.RequestUri?.ToString() ?? "");
            var payloadHash = ComputeSha256Hash(body);
            return $"POST\n{uri.AbsolutePath}\n\ncontent-type:application/json\nhost:{uri.Host}\nx-amz-date:{datetime}\n\ncontent-type;host;x-amz-date\n{payloadHash}";
        }

        private string CreateStringToSign(string canonicalRequest, string datetime, string date)
        {
            var hash = ComputeSha256Hash(canonicalRequest);
            return $"AWS4-HMAC-SHA256\n{datetime}\n{date}/{_region}/bedrock/aws4_request\n{hash}";
        }

        private byte[] GetSigningKey(string date)
        {
            var kSecret = Encoding.UTF8.GetBytes("AWS4" + _secretKey);
            var kDate = HmacSha256(kSecret, date);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "bedrock");
            return HmacSha256(kService, "aws4_request");
        }

        private string ComputeSignature(byte[] signingKey, string stringToSign)
        {
            var hash = HmacSha256(signingKey, stringToSign);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string ComputeSha256Hash(string data)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model);

            // Bedrock uses different payload formats per model family
            object payload;
            if (request.Model.StartsWith("anthropic."))
            {
                payload = new
                {
                    anthropic_version = "bedrock-2023-05-31",
                    max_tokens = request.MaxTokens ?? 4096,
                    messages = request.Messages.Where(m => m.Role != "system").Select(m => new
                    {
                        role = m.Role,
                        content = m.Content
                    }),
                    system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content,
                    temperature = request.Temperature
                };
            }
            else if (request.Model.StartsWith("meta."))
            {
                var prompt = string.Join("\n", request.Messages.Select(m =>
                    m.Role == "user" ? $"<|start_header_id|>user<|end_header_id|>\n{m.Content}<|eot_id|>" :
                    m.Role == "assistant" ? $"<|start_header_id|>assistant<|end_header_id|>\n{m.Content}<|eot_id|>" :
                    $"<|start_header_id|>system<|end_header_id|>\n{m.Content}<|eot_id|>"));
                prompt += "<|start_header_id|>assistant<|end_header_id|>\n";

                payload = new
                {
                    prompt = prompt,
                    max_gen_len = request.MaxTokens ?? 2048,
                    temperature = request.Temperature ?? 0.7
                };
            }
            else
            {
                // Generic format for other models
                payload = new
                {
                    inputText = string.Join("\n", request.Messages.Select(m => $"{m.Role}: {m.Content}")),
                    textGenerationConfig = new
                    {
                        maxTokenCount = request.MaxTokens ?? 4096,
                        temperature = request.Temperature ?? 0.7,
                        stopSequences = request.StopSequences
                    }
                };
            }

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var response = await SendSignedRequestAsync(endpoint, json, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Bedrock API error: {responseBody}");
            }

            var result = JsonDocument.Parse(responseBody);
            var root = result.RootElement;

            string textContent;
            int inputTokens = 0, outputTokens = 0;

            if (request.Model.StartsWith("anthropic."))
            {
                textContent = root.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
                if (root.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.GetProperty("input_tokens").GetInt32();
                    outputTokens = usage.GetProperty("output_tokens").GetInt32();
                }
            }
            else if (request.Model.StartsWith("meta."))
            {
                textContent = root.GetProperty("generation").GetString() ?? "";
                if (root.TryGetProperty("prompt_token_count", out var pt))
                    inputTokens = pt.GetInt32();
                if (root.TryGetProperty("generation_token_count", out var gt))
                    outputTokens = gt.GetInt32();
            }
            else
            {
                textContent = root.GetProperty("results")[0].GetProperty("outputText").GetString() ?? "";
            }

            return new ChatResponse
            {
                Model = request.Model,
                Content = textContent,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                FinishReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "stop"
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
            var embedModel = model ?? "amazon.titan-embed-text-v2:0";
            var endpoint = GetEndpoint(embedModel);

            var results = new List<double[]>();

            foreach (var text in texts)
            {
                ct.ThrowIfCancellationRequested();
                var payload = new { inputText = text };
                var json = JsonSerializer.Serialize(payload);

                var response = await SendSignedRequestAsync(endpoint, json, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                var result = JsonDocument.Parse(responseBody);

                var embedding = result.RootElement.GetProperty("embedding")
                    .EnumerateArray()
                    .Select(v => v.GetDouble())
                    .ToArray();

                results.Add(embedding);
            }

            return results.ToArray();
        }

        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var endpoint = GetEndpoint(request.Model).Replace("/invoke", "/invoke-with-response-stream");

            object payload;
            if (request.Model.StartsWith("anthropic."))
            {
                payload = new
                {
                    anthropic_version = "bedrock-2023-05-31",
                    max_tokens = request.MaxTokens ?? 4096,
                    messages = request.Messages.Where(m => m.Role != "system").Select(m => new
                    {
                        role = m.Role,
                        content = m.Content
                    }),
                    system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content,
                    temperature = request.Temperature
                };
            }
            else
            {
                // Fallback to non-streaming for unsupported models
                var response = await ChatAsync(request, ct);
                yield return response.Content;
                yield break;
            }

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var datetime = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var date = datetime.Substring(0, 8);
            httpRequest.Headers.Add("x-amz-date", datetime);
            httpRequest.Headers.Add("host", new Uri(endpoint).Host);

            var signature = SignRequest(httpRequest, json, datetime, date);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "AWS4-HMAC-SHA256",
                $"Credential={_accessKey}/{date}/{_region}/bedrock/aws4_request, " +
                $"SignedHeaders=content-type;host;x-amz-date, Signature={signature}");

            var response2 = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            using var stream = await response2.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    // Bedrock streaming uses event-stream format
                    if (line.StartsWith(":"))
                        continue;

                    var evt = JsonDocument.Parse(line);
                    if (evt.RootElement.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("text", out var text))
                    {
                        var textStr = text.GetString();
                        if (!string.IsNullOrEmpty(textStr))
                            yield return textStr;
                    }
                }
                catch { }
            }
        }

        public async Task<FunctionCallResponse> FunctionCallAsync(FunctionCallRequest request, CancellationToken ct = default)
        {
            if (!request.Model.StartsWith("anthropic."))
            {
                throw new NotSupportedException($"Function calling not supported for {request.Model}");
            }

            var endpoint = GetEndpoint(request.Model);

            var tools = request.Functions.Select(f => new
            {
                name = f.Name,
                description = f.Description,
                input_schema = f.Parameters
            }).ToList();

            var payload = new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = 4096,
                messages = request.Messages.Where(m => m.Role != "system").Select(m => new
                {
                    role = m.Role,
                    content = m.Content
                }),
                system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content,
                tools = tools
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var response = await SendSignedRequestAsync(endpoint, json, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            foreach (var content in result.RootElement.GetProperty("content").EnumerateArray())
            {
                if (content.TryGetProperty("type", out var type) && type.GetString() == "tool_use")
                {
                    return new FunctionCallResponse
                    {
                        FunctionName = content.GetProperty("name").GetString(),
                        Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(content.GetProperty("input").GetRawText())
                    };
                }
                if (content.TryGetProperty("text", out var text))
                {
                    return new FunctionCallResponse { Content = text.GetString() };
                }
            }

            return new FunctionCallResponse();
        }

        public async Task<VisionResponse> VisionAsync(VisionRequest request, CancellationToken ct = default)
        {
            if (!request.Model.StartsWith("anthropic."))
            {
                throw new NotSupportedException($"Vision not supported for {request.Model}");
            }

            var endpoint = GetEndpoint(request.Model);

            var contentParts = new List<object>
            {
                new { type = "text", text = request.Prompt }
            };

            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                contentParts.Insert(0, new
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
                var imageBytes = await _httpClient.GetByteArrayAsync(request.ImageUrl, ct);
                contentParts.Insert(0, new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/png",
                        data = Convert.ToBase64String(imageBytes)
                    }
                });
            }

            var payload = new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = request.MaxTokens ?? 4096,
                messages = new[]
                {
                    new { role = "user", content = contentParts }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var response = await SendSignedRequestAsync(endpoint, json, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(responseBody);

            var textContent = result.RootElement.GetProperty("content")[0]
                .GetProperty("text").GetString() ?? "";

            return new VisionResponse { Content = textContent };
        }
    }
}
