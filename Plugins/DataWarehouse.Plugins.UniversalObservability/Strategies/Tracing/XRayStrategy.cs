using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Tracing;

/// <summary>
/// Observability strategy for AWS X-Ray distributed tracing.
/// Provides AWS-native distributed tracing with service map visualization.
/// </summary>
public sealed class XRayStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _region = "us-east-1";
    private string _accessKeyId = "";
    private string _secretAccessKey = "";
    private string _serviceName = "datawarehouse";

    public override string StrategyId => "xray";
    public override string Name => "AWS X-Ray";

    public XRayStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: true, SupportsLogging: false,
        SupportsDistributedTracing: true, SupportsAlerting: false,
        SupportedExporters: new[] { "XRay", "XRayDaemon", "OTLPXRay" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string region, string accessKeyId, string secretAccessKey, string serviceName = "datawarehouse")
    {
        _region = region;
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _serviceName = serviceName;
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("x_ray.traces_sent");
        var segments = new List<string>();

        foreach (var span in spans)
        {
            var segment = new Dictionary<string, object>
            {
                ["name"] = span.OperationName,
                ["id"] = span.SpanId.Length >= 16 ? span.SpanId[..16] : span.SpanId.PadLeft(16, '0'),
                ["trace_id"] = FormatXRayTraceId(span.TraceId, span.StartTime),
                // P2-4669: .Millisecond / .Duration.Milliseconds are 0-999 components — use
                // ToUnixTimeMilliseconds() / 1000.0 to get sub-second precision across boundaries.
                ["start_time"] = span.StartTime.ToUnixTimeMilliseconds() / 1000.0,
                ["end_time"] = span.StartTime.Add(span.Duration).ToUnixTimeMilliseconds() / 1000.0,
                ["service"] = new { version = "1.0" },
                ["origin"] = "AWS::EC2::Instance"
            };

            if (span.ParentSpanId != null)
            {
                segment["parent_id"] = span.ParentSpanId.Length >= 16 ? span.ParentSpanId[..16] : span.ParentSpanId.PadLeft(16, '0');
                segment["type"] = "subsegment";
            }

            if (span.Status == SpanStatus.Error)
            {
                segment["fault"] = true;
            }

            if (span.Attributes != null && span.Attributes.Count > 0)
            {
                segment["annotations"] = span.Attributes
                    .Where(a => a.Value is string or int or long or double or bool)
                    .ToDictionary(a => SanitizeAnnotationKey(a.Key), a => a.Value);

                segment["metadata"] = new Dictionary<string, object>
                {
                    ["default"] = span.Attributes.ToDictionary(a => a.Key, a => a.Value)
                };
            }

            segments.Add(JsonSerializer.Serialize(segment));
        }

        var documents = string.Join("\n", segments.Select(s => $"{{\"format\":\"json\",\"version\":1}}\n{s}"));
        await PutTraceSegmentsAsync(documents, cancellationToken);
    }

    private string FormatXRayTraceId(string traceId, DateTimeOffset timestamp)
    {
        // X-Ray trace ID format: 1-{unix epoch time}-{96 bits of random}
        var epochHex = timestamp.ToUnixTimeSeconds().ToString("x8");
        var randomPart = traceId.Length >= 24 ? traceId[..24] : traceId.PadLeft(24, '0');
        return $"1-{epochHex}-{randomPart}";
    }

    private static string SanitizeAnnotationKey(string key)
    {
        return key.Replace(".", "_").Replace("-", "_").Replace(" ", "_");
    }

    private async Task PutTraceSegmentsAsync(string documents, CancellationToken ct)
    {
        // Filter only actual JSON segment lines (each starts with '{'); envelope header lines are excluded.
        var segmentDocs = documents.Split('\n')
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.TrimStart().StartsWith("{\"name", StringComparison.Ordinal))
            .ToArray();
        var payload = JsonSerializer.Serialize(new { TraceSegmentDocuments = segmentDocs });
        var host = $"xray.{_region}.amazonaws.com";
        var endpoint = $"https://{host}/TraceSegments";

        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.ToString("yyyyMMdd");

        var contentHash = GetSha256Hash(payload);
        var canonicalHeaders = $"host:{host}\nx-amz-date:{amzDate}\n";
        var signedHeaders = "host;x-amz-date";
        var canonicalRequest = $"POST\n/TraceSegments\n\n{canonicalHeaders}\n{signedHeaders}\n{contentHash}";

        var algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/{_region}/xray/aws4_request";
        var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{GetSha256Hash(canonicalRequest)}";

        var signingKey = GetSignatureKey(_secretAccessKey, dateStamp, _region, "xray");
        var signature = ToHexString(HmacSHA256(signingKey, stringToSign));

        var authorization = $"{algorithm} Credential={_accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Amz-Date", amzDate);
        request.Headers.Add("Authorization", authorization);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string GetSha256Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static byte[] HmacSHA256(byte[] key, string data) { using var hmac = new HMACSHA256(key); return hmac.ComputeHash(Encoding.UTF8.GetBytes(data)); }
    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kDate = HmacSHA256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
        var kRegion = HmacSHA256(kDate, regionName);
        var kService = HmacSHA256(kRegion, serviceName);
        return HmacSHA256(kService, "aws4_request");
    }
    private static string ToHexString(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
        => throw new NotSupportedException("X-Ray does not support metrics - use CloudWatch");

    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct)
        => throw new NotSupportedException("X-Ray does not support logging - use CloudWatch Logs");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        // P2-4673: Verify actual connectivity by sending a minimal well-formed batch rather than
        // only checking whether credentials are non-empty (wrong credentials still report healthy).
        if (string.IsNullOrEmpty(_accessKeyId))
        {
            return new HealthCheckResult(false, "X-Ray not configured: accessKeyId is empty",
                new Dictionary<string, object> { ["region"] = _region, ["service"] = _serviceName });
        }

        try
        {
            // Attempt a real API call with an empty segment list; X-Ray returns 200 for an empty batch.
            await PutTraceSegmentsAsync(string.Empty, ct);
            return new HealthCheckResult(true, "X-Ray configured and reachable",
                new Dictionary<string, object> { ["region"] = _region, ["service"] = _serviceName });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"X-Ray connectivity check failed: {ex.Message}",
                new Dictionary<string, object> { ["region"] = _region, ["service"] = _serviceName });
        }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("x_ray.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("x_ray.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    protected override void Dispose(bool disposing) {
                _accessKeyId = string.Empty;
                _secretAccessKey = string.Empty; if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
