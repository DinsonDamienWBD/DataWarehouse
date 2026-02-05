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
/// OpenTelemetry Collector connection strategy. HTTP/gRPC to port 4317/4318.
/// Vendor-agnostic telemetry data pipeline for metrics, logs, and traces.
/// </summary>
public sealed class OtlpCollectorConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "otlp-collector";
    public override string DisplayName => "OTLP Collector";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "OpenTelemetry Collector. Vendor-agnostic telemetry pipeline. Receives, processes, and exports metrics, logs, and traces.";
    public override string[] Tags => ["otlp", "opentelemetry", "collector", "observability", "vendor-agnostic", "cncf"];

    public OtlpCollectorConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:4318";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };
        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "OTLPCollector", ["BaseUrl"] = baseUrl });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/health", ct); return response.IsSuccessStatusCode; } catch { return false; }
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
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/health", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "OTLP ready", sw.Elapsed, DateTimeOffset.UtcNow); }
        catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }
    }

    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { resourceMetrics = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/v1/metrics", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { resourceLogs = logs });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/v1/logs", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { resourceSpans = traces });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/v1/traces", content, ct);
        response.EnsureSuccessStatusCode();
    }
}
