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
/// Splunk HEC connection strategy. HTTPS to port 8088 (/services/collector).
/// HTTP Event Collector for high-performance data ingestion into Splunk.
/// </summary>
public sealed class SplunkHecConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "splunk-hec";
    public override string DisplayName => "Splunk HEC";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "Splunk HTTP Event Collector for high-throughput data ingestion. Supports logs, metrics, and events. Ideal for enterprise monitoring and SIEM.";
    public override string[] Tags => ["splunk", "hec", "logs", "metrics", "events", "enterprise", "siem"];

    public SplunkHecConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://localhost:8088";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        if (config.Properties.TryGetValue("Token", out var token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Splunk", token.ToString()!);
        }

        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "SplunkHEC", ["BaseUrl"] = baseUrl });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/services/collector/health", ct); return response.IsSuccessStatusCode; } catch { return false; /* Connection validation - failure acceptable */ }
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
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/services/collector/health", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "HEC ready", sw.Elapsed, DateTimeOffset.UtcNow); }
        catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }
    }

    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { @event = "metric", fields = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/services/collector", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(logs.Select(log => new { @event = log }));
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/services/collector/event", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Splunk APM handles traces separately.");
}
