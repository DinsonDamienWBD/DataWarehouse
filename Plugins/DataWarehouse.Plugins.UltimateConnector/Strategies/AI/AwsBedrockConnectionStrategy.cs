using System;using System.Collections.Generic;using System.Net.Http;using System.Runtime.CompilerServices;using System.Security.Cryptography;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.AI;

/// <summary>AWS Bedrock. HTTPS to bedrock-runtime.*.amazonaws.com. Claude, Llama, Titan models on AWS.</summary>
public sealed class AwsBedrockConnectionStrategy : AiConnectionStrategyBase
{
    public override string StrategyId => "aws-bedrock";public override string DisplayName => "AWS Bedrock";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "AWS Bedrock managed foundation models. Claude, Llama, Titan with AWS integration.";public override string[] Tags => ["aws", "bedrock", "llm", "managed", "multi-model"];
    public AwsBedrockConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var region = config.Properties.GetValueOrDefault("Region", "us-east-1")?.ToString() ?? "us-east-1";
        var accessKey = config.Properties.GetValueOrDefault("AccessKeyId", "")?.ToString() ?? "";
        var secretKey = config.Properties.GetValueOrDefault("SecretAccessKey", "")?.ToString() ?? "";
        var sessionToken = config.Properties.GetValueOrDefault("SessionToken", "")?.ToString();
        var baseUrl = $"https://bedrock-runtime.{region}.amazonaws.com";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };
        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
        {
            ["Provider"] = "Bedrock", ["Region"] = region,
            ["AccessKeyId"] = accessKey, ["SecretAccessKey"] = secretKey,
            ["SessionToken"] = sessionToken ?? "", ["BaseUrl"] = baseUrl
        });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            // List foundation models is a lightweight auth-validated probe.
            var region = handle.ConnectionInfo["Region"].ToString()!;
            var url = $"https://bedrock.{region}.amazonaws.com/foundation-models";
            using var httpClient = new HttpClient();
            using var request = BuildSignedRequest(HttpMethod.Get, url, "", handle);
            using var response = await httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden;
        }
        catch { return false; }
    }

    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        handle.GetConnection<HttpClient>().Dispose();
        if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();
        return Task.CompletedTask;
    }

    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var isHealthy = await TestCoreAsync(handle, ct);
        sw.Stop();
        return new ConnectionHealth(isHealthy, isHealthy ? "AwsBedrock reachable" : "AwsBedrock unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
    }

    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var model = options?.GetValueOrDefault("model", "anthropic.claude-v2")?.ToString() ?? "anthropic.claude-v2";
        var payload = new { prompt = $"\n\nHuman: {prompt}\n\nAssistant:", max_tokens_to_sample = 2000 };
        var json = JsonSerializer.Serialize(payload);
        var url = $"/model/{Uri.EscapeDataString(model)}/invoke";
        using var request = BuildSignedRequest(HttpMethod.Post, (httpClient.BaseAddress + url.TrimStart('/')), json, handle);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("completion").GetString() ?? "";
    }

    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var model = options?.GetValueOrDefault("model", "anthropic.claude-v2")?.ToString() ?? "anthropic.claude-v2";
        var payload = new { prompt = $"\n\nHuman: {prompt}\n\nAssistant:", max_tokens_to_sample = 2000 };
        var json = JsonSerializer.Serialize(payload);
        var url = $"/model/{Uri.EscapeDataString(model)}/invoke-with-response-stream";
        using var request = BuildSignedRequest(HttpMethod.Post, (httpClient.BaseAddress + url.TrimStart('/')), json, handle);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);
        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var items = new List<string>();
            try
            {
                using var evt = JsonDocument.Parse(line);
                if (evt.RootElement.TryGetProperty("completion", out var comp))
                    items.Add(comp.GetString() ?? "");
            }
            catch { continue; }
            foreach (var item in items) yield return item;
        }
    }

    /// <summary>
    /// Builds an AWS SigV4-signed HTTP request.
    /// Implements the AWS Signature Version 4 signing process:
    /// https://docs.aws.amazon.com/general/latest/gr/sigv4-create-canonical-request.html
    /// </summary>
    private static HttpRequestMessage BuildSignedRequest(HttpMethod method, string url, string body, IConnectionHandle handle)
    {
        var info = handle.ConnectionInfo;
        var accessKey = info.GetValueOrDefault("AccessKeyId", "")?.ToString() ?? "";
        var secretKey = info.GetValueOrDefault("SecretAccessKey", "")?.ToString() ?? "";
        var sessionToken = info.GetValueOrDefault("SessionToken", "")?.ToString();
        var region = info.GetValueOrDefault("Region", "us-east-1")?.ToString() ?? "us-east-1";

        const string service = "bedrock-runtime";
        var uri = new Uri(url);
        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        // Step 1: Build canonical request
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var bodyHash = BitConverter.ToString(SHA256.HashData(bodyBytes)).Replace("-", "").ToLowerInvariant();

        var canonicalUri = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var canonicalQueryString = uri.Query.TrimStart('?');
        var host = uri.Host;

        var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/json",
            ["host"] = host,
            ["x-amz-content-sha256"] = bodyHash,
            ["x-amz-date"] = amzDate
        };
        if (!string.IsNullOrEmpty(sessionToken))
            headers["x-amz-security-token"] = sessionToken;

        var canonicalHeaders = string.Join("\n", headers.Select(kv => $"{kv.Key.ToLowerInvariant()}:{kv.Value.Trim()}")) + "\n";
        var signedHeaders = string.Join(";", headers.Keys.Select(k => k.ToLowerInvariant()));
        var canonicalRequest = string.Join("\n", method.Method, canonicalUri, canonicalQueryString, canonicalHeaders, signedHeaders, bodyHash);

        // Step 2: Build string to sign
        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var canonicalRequestHash = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).Replace("-", "").ToLowerInvariant();
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, credentialScope, canonicalRequestHash);

        // Step 3: Calculate signature
        var signingKey = GetSigningKey(secretKey, dateStamp, region, service);
        var signature = BitConverter.ToString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign))).Replace("-", "").ToLowerInvariant();

        // Step 4: Build Authorization header
        var authorization = $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", bodyHash);
        if (!string.IsNullOrEmpty(sessionToken))
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);

        return request;
    }

    private static byte[] GetSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }
}
