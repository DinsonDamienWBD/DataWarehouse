using System;
using System.Collections.Generic;
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
/// Datadog connection strategy. HTTPS to api.datadoghq.com (DD-API-KEY header).
/// Cloud monitoring platform for infrastructure, applications, and logs.
/// </summary>
public sealed class DatadogConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "datadog";
    public override string DisplayName => "Datadog";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "Datadog cloud monitoring platform. Infrastructure metrics, APM, logs, and synthetic monitoring. Enterprise-grade SaaS observability.";
    public override string[] Tags => ["datadog", "commercial", "apm", "monitoring", "saas", "cloud"];

    public DatadogConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://api.datadoghq.com";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        if (config.Properties.TryGetValue("ApiKey", out var apiKey))
        {
            httpClient.DefaultRequestHeaders.Remove("DD-API-KEY");
            httpClient.DefaultRequestHeaders.Add("DD-API-KEY", apiKey.ToString()!);
        }

        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Datadog", ["BaseUrl"] = baseUrl });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/v1/validate", ct); return response.IsSuccessStatusCode; } catch { return false; /* Connection validation - failure acceptable */ }
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
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/v1/validate", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "Datadog API ready", sw.Elapsed, DateTimeOffset.UtcNow); }
        catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }
    }

    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { series = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/api/v1/series", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(logs);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/v1/input", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(traces);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/v0.3/traces", content, ct);
        response.EnsureSuccessStatusCode();
    }
}
