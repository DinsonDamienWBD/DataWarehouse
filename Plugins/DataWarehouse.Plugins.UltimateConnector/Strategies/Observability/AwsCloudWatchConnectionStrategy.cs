using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>AWS CloudWatch connection strategy. HTTPS to monitoring.amazonaws.com. AWS native monitoring service.</summary>
public sealed class AwsCloudWatchConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "aws-cloudwatch";public override string DisplayName => "AWS CloudWatch";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "AWS CloudWatch monitoring service. Metrics, logs, alarms, and dashboards for AWS resources.";public override string[] Tags => ["aws", "cloudwatch", "monitoring", "logs", "metrics", "cloud"];
    public AwsCloudWatchConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://monitoring.amazonaws.com";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "CloudWatch", ["BaseUrl"] = baseUrl });}
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(true);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(true, "CloudWatch configured", TimeSpan.Zero, DateTimeOffset.UtcNow));
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default){var json = JsonSerializer.Serialize(new { MetricData = metrics });var content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");var response = await handle.GetConnection<HttpClient>().PostAsync("/", content, ct);response.EnsureSuccessStatusCode();}
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default){var json = JsonSerializer.Serialize(new { logEvents = logs });var content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1");var response = await handle.GetConnection<HttpClient>().PostAsync("/", content, ct);response.EnsureSuccessStatusCode();}
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Use AWS X-Ray for traces.");
}
