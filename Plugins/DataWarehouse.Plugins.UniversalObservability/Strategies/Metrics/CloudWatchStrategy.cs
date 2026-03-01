using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Metrics;

/// <summary>
/// Observability strategy for AWS CloudWatch metrics and logs.
/// Provides comprehensive AWS-native monitoring with CloudWatch Metrics, Logs, and Alarms.
/// </summary>
/// <remarks>
/// Amazon CloudWatch provides monitoring and observability for AWS resources and applications,
/// with support for custom metrics, log aggregation, alarms, and dashboards.
/// </remarks>
public sealed class CloudWatchStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentQueue<MetricDatum> _metricsBatch = new();
    private readonly ConcurrentQueue<LogEvent> _logsBatch = new();
    private string _region = "us-east-1";
    private string _accessKeyId = "";
    private string _secretAccessKey = "";
    private string _namespace = "DataWarehouse";
    private string _logGroupName = "/datawarehouse/application";
    private string _logStreamName = "";
    private readonly int _batchSize = 20; // CloudWatch limit
    private volatile bool _logStreamEnsured = false;

    private record MetricDatum(string Name, double Value, string Unit, DateTimeOffset Timestamp, Dictionary<string, string> Dimensions);
    private record LogEvent(string Message, DateTimeOffset Timestamp);

    /// <inheritdoc/>
    public override string StrategyId => "cloudwatch";

    /// <inheritdoc/>
    public override string Name => "AWS CloudWatch";

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudWatchStrategy"/> class.
    /// </summary>
    public CloudWatchStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "CloudWatch", "CloudWatchLogs", "CloudWatchAlarms" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logStreamName = $"{Environment.MachineName}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    /// <summary>
    /// Configures the CloudWatch connection.
    /// </summary>
    /// <param name="region">AWS region.</param>
    /// <param name="accessKeyId">AWS access key ID.</param>
    /// <param name="secretAccessKey">AWS secret access key.</param>
    /// <param name="metricsNamespace">CloudWatch namespace for metrics.</param>
    /// <param name="logGroupName">CloudWatch Logs group name.</param>
    public void Configure(string region, string accessKeyId, string secretAccessKey,
        string metricsNamespace = "DataWarehouse", string logGroupName = "/datawarehouse/application")
    {
        _region = region;
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _namespace = metricsNamespace;
        _logGroupName = logGroupName;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("cloud_watch.metrics_sent");
        var metricData = new List<Dictionary<string, object>>();

        foreach (var metric in metrics)
        {
            var dimensions = metric.Labels?.Select(l => new Dictionary<string, string>
            {
                ["Name"] = l.Name,
                ["Value"] = l.Value
            }).ToList() ?? new List<Dictionary<string, string>>();

            var unit = metric.Unit?.ToUpperInvariant() switch
            {
                "SECONDS" => "Seconds",
                "BYTES" => "Bytes",
                "PERCENT" => "Percent",
                "COUNT" => "Count",
                "MILLISECONDS" => "Milliseconds",
                _ => "None"
            };

            metricData.Add(new Dictionary<string, object>
            {
                ["MetricName"] = metric.Name,
                ["Value"] = metric.Value,
                ["Unit"] = unit,
                ["Timestamp"] = metric.Timestamp.ToString("o"),
                ["Dimensions"] = dimensions
            });
        }

        // Batch metrics (CloudWatch accepts max 20 per request)
        foreach (var batch in metricData.Chunk(_batchSize))
        {
            await PutMetricDataAsync(batch, cancellationToken);
        }
    }

    private async Task PutMetricDataAsync(IEnumerable<Dictionary<string, object>> metrics, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Action"] = "PutMetricData",
            ["Version"] = "2010-08-01",
            ["Namespace"] = _namespace
        };

        var idx = 1;
        foreach (var metric in metrics)
        {
            parameters[$"MetricData.member.{idx}.MetricName"] = metric["MetricName"].ToString()!;
            parameters[$"MetricData.member.{idx}.Value"] = metric["Value"].ToString()!;
            parameters[$"MetricData.member.{idx}.Unit"] = metric["Unit"].ToString()!;
            parameters[$"MetricData.member.{idx}.Timestamp"] = metric["Timestamp"].ToString()!;

            if (metric["Dimensions"] is List<Dictionary<string, string>> dimensions)
            {
                var dimIdx = 1;
                foreach (var dim in dimensions.Take(10)) // CloudWatch limit: 10 dimensions
                {
                    parameters[$"MetricData.member.{idx}.Dimensions.member.{dimIdx}.Name"] = dim["Name"];
                    parameters[$"MetricData.member.{idx}.Dimensions.member.{dimIdx}.Value"] = dim["Value"];
                    dimIdx++;
                }
            }
            idx++;
        }

        await SendSignedRequestAsync("monitoring", parameters, ct);
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("cloud_watch.logs_sent");
        // Ensure log group and stream exist — called once per session, not every batch.
        if (!_logStreamEnsured)
        {
            await EnsureLogStreamExistsAsync(cancellationToken);
            _logStreamEnsured = true;
        }

        var logEvents = logEntries.Select(entry => new Dictionary<string, object>
        {
            ["timestamp"] = entry.Timestamp.ToUnixTimeMilliseconds(),
            ["message"] = FormatLogMessage(entry)
        }).ToList();

        // CloudWatch Logs accepts max 10,000 events per request
        foreach (var batch in logEvents.Chunk(10000))
        {
            await PutLogEventsAsync(batch, cancellationToken);
        }
    }

    private string FormatLogMessage(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append($"[{entry.Level}] {entry.Message}");

        if (entry.Properties != null && entry.Properties.Count > 0)
        {
            sb.Append(" | ");
            sb.Append(string.Join(", ", entry.Properties.Select(p => $"{p.Key}={p.Value}")));
        }

        if (entry.Exception != null)
        {
            sb.AppendLine();
            sb.Append($"Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}");
            if (!string.IsNullOrEmpty(entry.Exception.StackTrace))
            {
                sb.AppendLine();
                sb.Append(entry.Exception.StackTrace);
            }
        }

        return sb.ToString();
    }

    private async Task EnsureLogStreamExistsAsync(CancellationToken ct)
    {
        // Create log group (ignores if exists)
        var createGroupParams = new Dictionary<string, string>
        {
            ["Action"] = "CreateLogGroup",
            ["Version"] = "2014-03-28",
            ["logGroupName"] = _logGroupName
        };

        try
        {
            await SendSignedRequestAsync("logs", createGroupParams, ct);
        }
        catch { /* Ignore if exists */ }

        // Create log stream
        var createStreamParams = new Dictionary<string, string>
        {
            ["Action"] = "CreateLogStream",
            ["Version"] = "2014-03-28",
            ["logGroupName"] = _logGroupName,
            ["logStreamName"] = _logStreamName
        };

        try
        {
            await SendSignedRequestAsync("logs", createStreamParams, ct);
        }
        catch { /* Ignore if exists */ }
    }

    private async Task PutLogEventsAsync(IEnumerable<Dictionary<string, object>> events, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Action"] = "PutLogEvents",
            ["Version"] = "2014-03-28",
            ["logGroupName"] = _logGroupName,
            ["logStreamName"] = _logStreamName
        };

        var idx = 1;
        foreach (var evt in events)
        {
            parameters[$"logEvents.member.{idx}.timestamp"] = evt["timestamp"].ToString()!;
            parameters[$"logEvents.member.{idx}.message"] = evt["message"].ToString()!;
            idx++;
        }

        await SendSignedRequestAsync("logs", parameters, ct);
    }

    private async Task SendSignedRequestAsync(string service, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var host = $"{service}.{_region}.amazonaws.com";
        var endpoint = $"https://{host}/";

        var queryString = string.Join("&", parameters
            .OrderBy(p => p.Key)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        // AWS Signature Version 4
        var canonicalUri = "/";
        var canonicalQuerystring = queryString;
        var canonicalHeaders = $"host:{host}\nx-amz-date:{amzDate}\n";
        var signedHeaders = "host;x-amz-date";
        var payloadHash = GetHash("");
        var canonicalRequest = $"POST\n{canonicalUri}\n{canonicalQuerystring}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/{_region}/{service}/aws4_request";
        var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{GetHash(canonicalRequest)}";

        var signingKey = GetSignatureKey(_secretAccessKey, dateStamp, _region, service);
        var signature = ToHexString(HmacSHA256(signingKey, stringToSign));

        var authorizationHeader = $"{algorithm} Credential={_accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "?" + queryString);
        request.Headers.Add("X-Amz-Date", amzDate);
        request.Headers.Add("Authorization", authorizationHeader);
        request.Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string GetHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return ToHexString(bytes);
    }

    private static byte[] HmacSHA256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kDate = HmacSHA256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
        var kRegion = HmacSHA256(kDate, regionName);
        var kService = HmacSHA256(kRegion, serviceName);
        return HmacSHA256(kService, "aws4_request");
    }

    private static string ToHexString(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("CloudWatch does not support tracing directly - use X-Ray strategy instead");
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "ListMetrics",
                ["Version"] = "2010-08-01",
                ["Namespace"] = _namespace
            };

            await SendSignedRequestAsync("monitoring", parameters, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: true,
                Description: "CloudWatch connection is healthy",
                Data: new Dictionary<string, object>
                {
                    ["region"] = _region,
                    ["namespace"] = _namespace,
                    ["logGroup"] = _logGroupName
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"CloudWatch health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_region))
            throw new InvalidOperationException("CloudWatchStrategy: AWS region is required. Call Configure() before initialization.");
        if (string.IsNullOrWhiteSpace(_accessKeyId))
            throw new InvalidOperationException("CloudWatchStrategy: AWS access key ID is required. Call Configure() before initialization.");
        if (string.IsNullOrWhiteSpace(_secretAccessKey))
            throw new InvalidOperationException("CloudWatchStrategy: AWS secret access key is required. Call Configure() before initialization.");
        IncrementCounter("cloud_watch.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Shutdown grace period elapsed */ }
        IncrementCounter("cloud_watch.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
                _accessKeyId = string.Empty;
                _secretAccessKey = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
