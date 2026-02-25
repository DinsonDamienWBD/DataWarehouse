using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>
/// Grafana Loki connection strategy for log aggregation.
/// Connects to Loki Push API on port 3100 (/loki/api/v1/push).
/// Provides horizontally scalable log storage with label-based indexing.
/// </summary>
public sealed class GrafanaLokiConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "grafana-loki";
    public override string DisplayName => "Grafana Loki";

    public override ConnectionStrategyCapabilities Capabilities => new();

    public override string SemanticDescription =>
        "Grafana Loki log aggregation system. Label-based indexing like Prometheus for logs. " +
        "Cost-effective alternative to Elasticsearch. Ideal for cloud-native log management.";

    public override string[] Tags =>
    [
        "loki", "grafana", "logs", "log-aggregation", "observability", "cloud-native"
    ];

    public GrafanaLokiConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:3100";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        if (config.Properties.TryGetValue("TenantId", out var tenantId))
        {
            httpClient.DefaultRequestHeaders.Add("X-Scope-OrgID", tenantId.ToString()!);
        }

        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
        {
            ["Provider"] = "Loki",
            ["BaseUrl"] = baseUrl
        });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var response = await handle.GetConnection<HttpClient>().GetAsync("/ready", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
        try
        {
            var response = await handle.GetConnection<HttpClient>().GetAsync("/ready", ct);
            sw.Stop();
            return new ConnectionHealth(response.IsSuccessStatusCode, "Loki ready", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }

    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        throw new NotSupportedException("Loki is for logs only. Use Prometheus for metrics.");
    }

    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { streams = logs });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/loki/api/v1/push", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        throw new NotSupportedException("Loki does not support traces. Use Tempo for traces.");
    }
}
