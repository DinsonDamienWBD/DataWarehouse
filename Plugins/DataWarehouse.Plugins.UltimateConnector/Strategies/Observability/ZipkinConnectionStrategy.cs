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
/// Zipkin connection strategy for distributed tracing. HTTP to port 9411 (/api/v2/spans).
/// Collects timing data for troubleshooting latency problems in microservice architectures.
/// </summary>
public sealed class ZipkinConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "zipkin";
    public override string DisplayName => "Zipkin";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "Zipkin distributed tracing system. Collects timing data to troubleshoot latency in microservices. Twitter-originated open-source project.";
    public override string[] Tags => ["zipkin", "tracing", "distributed-tracing", "microservices", "latency-analysis"];

    public ZipkinConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:9411";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };
        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Zipkin", ["BaseUrl"] = baseUrl });
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
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/health", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "Zipkin ready", sw.Elapsed, DateTimeOffset.UtcNow); }
        catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }
    }

    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default) => throw new NotSupportedException("Zipkin is for traces.");
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default) => throw new NotSupportedException("Zipkin is for traces.");

    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(traces);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/api/v2/spans", content, ct);
        response.EnsureSuccessStatusCode();
    }
}
