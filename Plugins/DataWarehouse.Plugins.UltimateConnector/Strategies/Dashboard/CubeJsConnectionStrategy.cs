using System;using System.Collections.Generic;using System.Net.Http;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Dashboard;

/// <summary>Cube.js embedded analytics. HTTP to port 4000 (/cubejs-api/v1).</summary>
public sealed class CubeJsConnectionStrategy : DashboardConnectionStrategyBase
{
    public override string StrategyId => "cubejs";public override string DisplayName => "Cube.js";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "Cube.js headless BI platform. Semantic layer for embedding analytics in applications.";public override string[] Tags => ["cubejs", "embedded", "semantic-layer", "headless-bi", "open-source"];
    public CubeJsConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:4000";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "CubeJS", ["BaseUrl"] = baseUrl });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct){try { var response = await handle.GetConnection<HttpClient>().GetAsync("/cubejs-api/v1/meta", ct); return response.IsSuccessStatusCode; } catch { return false; }}
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){var sw = System.Diagnostics.Stopwatch.StartNew();try { var response = await handle.GetConnection<HttpClient>().GetAsync("/readyz", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "Cube.js ready", sw.Elapsed, DateTimeOffset.UtcNow); }catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }}
    public override Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default) => Task.FromResult("cubejs-uses-data-schema");
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default){var json = JsonSerializer.Serialize(new { query = data });var content = new StringContent(json, Encoding.UTF8, "application/json");var response = await handle.GetConnection<HttpClient>().PostAsync("/cubejs-api/v1/load", content, ct);response.EnsureSuccessStatusCode();}
}
