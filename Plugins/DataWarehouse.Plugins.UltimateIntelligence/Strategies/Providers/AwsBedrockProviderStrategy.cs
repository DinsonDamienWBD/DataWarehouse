using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Providers;

/// <summary>
/// AWS Bedrock provider strategy.
/// Supports Claude, Llama, Titan, and other foundation models on AWS.
/// </summary>
public sealed class AwsBedrockProviderStrategy : AIProviderStrategyBase
{
    private const string DefaultRegion = "us-east-1";
    private const string DefaultModel = "anthropic.claude-3-sonnet-20240229-v1:0";
    private const string DefaultEmbeddingModel = "amazon.titan-embed-text-v2:0";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "provider-aws-bedrock";

    /// <inheritdoc/>
    public override string StrategyName => "AWS Bedrock Provider";

    /// <inheritdoc/>
    public override string ProviderId => "aws-bedrock";

    /// <inheritdoc/>
    public override string DisplayName => "AWS Bedrock";

    /// <inheritdoc/>
    public override AICapabilities Capabilities =>
        AICapabilities.TextCompletion | AICapabilities.ChatCompletion | AICapabilities.Streaming |
        AICapabilities.Embeddings | AICapabilities.ImageGeneration | AICapabilities.CodeGeneration;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "AWS Bedrock",
        Description = "AWS-hosted foundation models including Claude, Llama, Titan with enterprise security",
        Capabilities = IntelligenceCapabilities.AllAIProvider | IntelligenceCapabilities.ImageGeneration,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "AccessKeyId", Description = "AWS Access Key ID", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "SecretAccessKey", Description = "AWS Secret Access Key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Region", Description = "AWS Region", Required = false, DefaultValue = DefaultRegion },
            new ConfigurationRequirement { Key = "Model", Description = "Default model ID", Required = false, DefaultValue = DefaultModel },
            new ConfigurationRequirement { Key = "EmbeddingModel", Description = "Embedding model ID", Required = false, DefaultValue = DefaultEmbeddingModel }
        },
        CostTier = 4,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "aws", "bedrock", "claude", "titan", "llama", "enterprise" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public AwsBedrockProviderStrategy() : this(SharedHttpClient) { }

    public AwsBedrockProviderStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public override async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var region = GetConfig("Region") ?? DefaultRegion;
            var model = request.Model ?? GetConfig("Model") ?? DefaultModel;

            var endpoint = $"https://bedrock-runtime.{region}.amazonaws.com";
            var path = $"/model/{model}/invoke";

            // Build model-specific payload
            var payload = BuildModelPayload(model, request);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}{path}");
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            // Sign request with AWS SigV4
            await SignRequestAsync(httpRequest, region, "bedrock");

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            return ParseModelResponse(model, responseBody);
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var region = GetConfig("Region") ?? DefaultRegion;
        var model = request.Model ?? GetConfig("Model") ?? DefaultModel;

        var endpoint = $"https://bedrock-runtime.{region}.amazonaws.com";
        var path = $"/model/{model}/invoke-with-response-stream";

        var payload = BuildModelPayload(model, request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}{path}");
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        await SignRequestAsync(httpRequest, region, "bedrock");

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line))
                continue;

            // Parse AWS event stream format
            var chunk = ParseStreamChunk(model, line);
            if (chunk != null)
            {
                yield return chunk;
                if (chunk.IsFinal)
                    break;
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var region = GetConfig("Region") ?? DefaultRegion;
            var model = GetConfig("EmbeddingModel") ?? DefaultEmbeddingModel;

            var endpoint = $"https://bedrock-runtime.{region}.amazonaws.com";
            var path = $"/model/{model}/invoke";

            var payload = new { inputText = text };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}{path}");
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            await SignRequestAsync(httpRequest, region, "bedrock");

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BedrockEmbeddingResponse>(cancellationToken: ct);
            RecordEmbeddings(1);

            return result?.Embedding ?? Array.Empty<float>();
        });
    }

    private object BuildModelPayload(string model, AIRequest request)
    {
        if (model.StartsWith("anthropic.claude"))
        {
            return new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = request.MaxTokens ?? 4096,
                system = request.SystemMessage,
                messages = BuildClaudeMessages(request)
            };
        }
        else if (model.StartsWith("amazon.titan"))
        {
            return new
            {
                inputText = BuildTitanPrompt(request),
                textGenerationConfig = new
                {
                    maxTokenCount = request.MaxTokens ?? 4096,
                    temperature = request.Temperature ?? 0.7f
                }
            };
        }
        else if (model.StartsWith("meta.llama"))
        {
            return new
            {
                prompt = BuildLlamaPrompt(request),
                max_gen_len = request.MaxTokens ?? 4096,
                temperature = request.Temperature ?? 0.7f
            };
        }

        // Default/generic payload
        return new { prompt = request.Prompt };
    }

    private List<object> BuildClaudeMessages(AIRequest request)
    {
        var messages = new List<object>();
        foreach (var msg in request.ChatHistory)
            messages.Add(new { role = msg.Role == "assistant" ? "assistant" : "user", content = msg.Content });
        if (!string.IsNullOrEmpty(request.Prompt))
            messages.Add(new { role = "user", content = request.Prompt });
        return messages;
    }

    private string BuildTitanPrompt(AIRequest request)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(request.SystemMessage))
            sb.AppendLine($"System: {request.SystemMessage}");
        foreach (var msg in request.ChatHistory)
            sb.AppendLine($"{msg.Role}: {msg.Content}");
        if (!string.IsNullOrEmpty(request.Prompt))
            sb.AppendLine($"User: {request.Prompt}");
        return sb.ToString();
    }

    private string BuildLlamaPrompt(AIRequest request)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(request.SystemMessage))
            sb.AppendLine($"<|system|>{request.SystemMessage}<|end|>");
        foreach (var msg in request.ChatHistory)
            sb.AppendLine($"<|{msg.Role}|>{msg.Content}<|end|>");
        if (!string.IsNullOrEmpty(request.Prompt))
            sb.AppendLine($"<|user|>{request.Prompt}<|end|><|assistant|>");
        return sb.ToString();
    }

    private AIResponse ParseModelResponse(string model, string responseBody)
    {
        if (model.StartsWith("anthropic.claude"))
        {
            var result = JsonSerializer.Deserialize<BedrockClaudeResponse>(responseBody);
            var textContent = result?.Content?.FirstOrDefault(c => c.Type == "text");
            RecordTokens((result?.Usage?.InputTokens ?? 0) + (result?.Usage?.OutputTokens ?? 0));
            return new AIResponse
            {
                Success = true,
                Content = textContent?.Text ?? string.Empty,
                FinishReason = result?.StopReason,
                Usage = new AIUsage
                {
                    PromptTokens = result?.Usage?.InputTokens ?? 0,
                    CompletionTokens = result?.Usage?.OutputTokens ?? 0
                }
            };
        }
        else if (model.StartsWith("amazon.titan"))
        {
            var result = JsonSerializer.Deserialize<BedrockTitanResponse>(responseBody);
            return new AIResponse
            {
                Success = true,
                Content = result?.Results?.FirstOrDefault()?.OutputText ?? string.Empty,
                FinishReason = result?.Results?.FirstOrDefault()?.CompletionReason
            };
        }
        else if (model.StartsWith("meta.llama"))
        {
            var result = JsonSerializer.Deserialize<BedrockLlamaResponse>(responseBody);
            return new AIResponse
            {
                Success = true,
                Content = result?.Generation ?? string.Empty,
                FinishReason = result?.StopReason
            };
        }

        return new AIResponse { Success = false, ErrorMessage = "Unknown model type" };
    }

    private AIStreamChunk? ParseStreamChunk(string model, string line)
    {
        // Simplified stream parsing - real implementation would handle AWS event stream format
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(line);
            if (data.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text))
            {
                return new AIStreamChunk { Content = text.GetString() ?? "" };
            }
            if (data.TryGetProperty("stop_reason", out _))
            {
                return new AIStreamChunk { IsFinal = true, FinishReason = "stop" };
            }
        }
        catch { /* JSON parsing failure — return null */ }
        return null;
    }

    private async Task SignRequestAsync(HttpRequestMessage request, string region, string service)
    {
        // AWS SigV4 signing implementation
        var accessKey = GetRequiredConfig("AccessKeyId");
        var secretKey = GetRequiredConfig("SecretAccessKey");

        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        request.Headers.Add("x-amz-date", amzDate);
        request.Headers.Add("host", request.RequestUri!.Host);

        // Create canonical request
        var method = request.Method.Method;
        var canonicalUri = request.RequestUri.AbsolutePath;
        var canonicalQuerystring = request.RequestUri.Query.TrimStart('?');
        var payload = request.Content != null ? await request.Content.ReadAsStringAsync() : "";
        var payloadHash = ComputeSha256Hash(payload);

        var canonicalHeaders = $"host:{request.RequestUri.Host}\nx-amz-date:{amzDate}\n";
        var signedHeaders = "host;x-amz-date";

        var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQuerystring}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        // Create string to sign
        var algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ComputeSha256Hash(canonicalRequest)}";

        // Calculate signature
        var signingKey = GetSignatureKey(secretKey, dateStamp, region, service);
        var signature = ComputeHmacSha256(signingKey, stringToSign);

        // Add authorization header
        var authHeader = $"{algorithm} Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.Add("Authorization", authHeader);
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string region, string service)
    {
        var kDate = ComputeHmacSha256Bytes(Encoding.UTF8.GetBytes($"AWS4{key}"), dateStamp);
        var kRegion = ComputeHmacSha256Bytes(kDate, region);
        var kService = ComputeHmacSha256Bytes(kRegion, service);
        return ComputeHmacSha256Bytes(kService, "aws4_request");
    }

    private static byte[] ComputeHmacSha256Bytes(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string ComputeHmacSha256(byte[] key, string data)
    {
        return Convert.ToHexString(ComputeHmacSha256Bytes(key, data)).ToLowerInvariant();
    }

    // Response models
    private sealed class BedrockClaudeResponse
    {
        public List<BedrockClaudeContent>? Content { get; set; }
        public string? StopReason { get; set; }
        public BedrockClaudeUsage? Usage { get; set; }
    }

    private sealed class BedrockClaudeContent
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }
    }

    private sealed class BedrockClaudeUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    private sealed class BedrockTitanResponse
    {
        public List<BedrockTitanResult>? Results { get; set; }
    }

    private sealed class BedrockTitanResult
    {
        public string? OutputText { get; set; }
        public string? CompletionReason { get; set; }
    }

    private sealed class BedrockLlamaResponse
    {
        public string? Generation { get; set; }
        public string? StopReason { get; set; }
    }

    private sealed class BedrockEmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}
