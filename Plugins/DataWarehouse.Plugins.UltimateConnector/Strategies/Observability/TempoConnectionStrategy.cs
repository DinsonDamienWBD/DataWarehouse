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
/// Grafana Tempo connection strategy for traces. HTTP OTLP/Jaeger-compatible.
/// Cost-effective trace storage with object storage backend.
/// </summary>
public sealed class TempoConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "tempo";
    public override string DisplayName => "Grafana Tempo";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "Grafana Tempo distributed tracing backend. Object storage based. Jaeger, Zipkin, OTLP compatible.";
    public override string[] Tags => ["tempo", "grafana", "tracing", "object-storage", "otlp", "jaeger-compatible"];

    public TempoConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:3200";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };
        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Tempo", ["BaseUrl"] = baseUrl });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/ready", ct); return response.IsSuccessStatusCode; } catch { return false; /* Connection validation - failure acceptable */ }
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
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/ready", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "Tempo ready", sw.Elapsed, DateTimeOffset.UtcNow); }
        catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }
    }

    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default) => throw new NotSupportedException("Tempo is for traces only.");
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default) => throw new NotSupportedException("Tempo is for traces only.");

    public override async Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { batches = traces });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/v1/traces", content, ct);
        response.EnsureSuccessStatusCode();
    }
}
