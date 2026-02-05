using System;using System.Collections.Generic;using System.Net.Http;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>Netdata connection strategy. HTTP to port 19999. Real-time performance monitoring.</summary>
public sealed class NetdataConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "netdata";public override string DisplayName => "Netdata";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "Netdata real-time performance monitoring. Per-second metrics with low overhead. Ideal for system and container monitoring.";public override string[] Tags => ["netdata", "monitoring", "real-time", "performance", "open-source"];
    public NetdataConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:19999";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Netdata", ["BaseUrl"] = baseUrl });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct){try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/v1/info", ct); return response.IsSuccessStatusCode; } catch { return false; }}
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){var sw = System.Diagnostics.Stopwatch.StartNew();try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/v1/info", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "Netdata ready", sw.Elapsed, DateTimeOffset.UtcNow); }catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }}
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default){var json = JsonSerializer.Serialize(metrics);var content = new StringContent(json, Encoding.UTF8, "application/json");var response = await handle.GetConnection<HttpClient>().PostAsync("/api/v1/data", content, ct);response.EnsureSuccessStatusCode();}
    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default) => throw new NotSupportedException("Netdata is for metrics.");
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Netdata does not support traces.");
}
