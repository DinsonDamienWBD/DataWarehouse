using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>
/// Cortex connection strategy for horizontally scalable Prometheus as a Service.
/// Prometheus-compatible remote write with multi-tenancy and long-term storage in object stores.
/// </summary>
public sealed class CortexConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "cortex";
    public override string DisplayName => "Cortex";

    public override ConnectionStrategyCapabilities Capabilities => new();

    public override string SemanticDescription =>
        "Cortex horizontally scalable Prometheus backend. Multi-tenant with object storage. " +
        "Ideal for large-scale Prometheus deployments and SaaS monitoring platforms.";

    public override string[] Tags =>
    [
        "cortex", "prometheus", "multi-tenant", "scalable", "cloud-native", "cncf"
    ];

    public CortexConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? throw new ArgumentException("Cortex endpoint required");
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        if (config.Properties.TryGetValue("TenantId", out var tenantId))
        {
            httpClient.DefaultRequestHeaders.Add("X-Scope-OrgID", tenantId.ToString()!);
        }

        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
        {
            ["Provider"] = "Cortex",
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
            return new ConnectionHealth(response.IsSuccessStatusCode, "Cortex ready", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }

    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { timeseries = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await handle.GetConnection<HttpClient>().PostAsync("/api/v1/push", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        throw new NotSupportedException("Cortex is for metrics only.");
    }

    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        throw new NotSupportedException("Cortex does not support traces.");
    }
}
