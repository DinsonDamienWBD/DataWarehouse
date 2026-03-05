using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>AWS CloudWatch connection strategy. HTTPS to monitoring.amazonaws.com. AWS native monitoring service.</summary>
public sealed class AwsCloudWatchConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "aws-cloudwatch";public override string DisplayName => "AWS CloudWatch";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "AWS CloudWatch monitoring service. Metrics, logs, alarms, and dashboards for AWS resources.";public override string[] Tags => ["aws", "cloudwatch", "monitoring", "logs", "metrics", "cloud"];
    public AwsCloudWatchConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://monitoring.amazonaws.com";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "CloudWatch", ["BaseUrl"] = baseUrl });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        // Finding 2077: Probe the CloudWatch ListMetrics endpoint to verify real connectivity.
        try
        {
            // POST to / with Action=ListMetrics returns a valid XML response when credentials are valid.
            var content = new System.Net.Http.StringContent("Action=ListMetrics&Version=2010-08-01", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await handle.GetConnection<HttpClient>().PostAsync("/", content, ct);
            // 200 OK or 403 Forbidden both mean the endpoint is reachable; 503 means unavailable.
            return (int)response.StatusCode != 503;
        }
        catch { return false; }
    }
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var isHealthy = await TestCoreAsync(handle, ct);
        sw.Stop();
        return new ConnectionHealth(isHealthy, isHealthy ? "CloudWatch reachable" : "CloudWatch unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
    }
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default){var json = JsonSerializer.Serialize(new { MetricData = metrics });var content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");var response = await handle.GetConnection<HttpClient>().PostAsync("/", content, ct);response.EnsureSuccessStatusCode();}
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default){var json = JsonSerializer.Serialize(new { logEvents = logs });var content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");var response = await handle.GetConnection<HttpClient>().PostAsync("/", content, ct);response.EnsureSuccessStatusCode();}
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Use AWS X-Ray for traces.");
}
