using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Dashboard;

/// <summary>Klipfolio dashboard. HTTPS to app.klipfolio.com/api.</summary>
public sealed class KlipfolioConnectionStrategy : DashboardConnectionStrategyBase
{
    public override string StrategyId => "klipfolio";public override string DisplayName => "Klipfolio";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "Klipfolio cloud dashboards. Real-time KPI monitoring for business teams.";public override string[] Tags => ["klipfolio", "commercial", "kpi", "cloud", "real-time"];
    public KlipfolioConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var httpClient = new HttpClient { BaseAddress = new Uri("https://app.klipfolio.com/api"), Timeout = config.Timeout };if (config.Properties.TryGetValue("ApiKey", out var apiKey)){httpClient.DefaultRequestHeaders.Add("kf-api-key", apiKey.ToString()!);}return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Klipfolio" });}
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(true);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(true, "Klipfolio configured", TimeSpan.Zero, DateTimeOffset.UtcNow));
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default){var httpClient = handle.GetConnection<HttpClient>();var content = new StringContent(dashboardDefinition, Encoding.UTF8, "application/json");var response = await httpClient.PostAsync("/v1/dashboards", content, ct);response.EnsureSuccessStatusCode();var result = await response.Content.ReadAsStringAsync(ct);var json = JsonDocument.Parse(result);return json.RootElement.GetProperty("id").GetString() ?? "";}
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default){var json = JsonSerializer.Serialize(data);var content = new StringContent(json, Encoding.UTF8, "application/json");var response = await handle.GetConnection<HttpClient>().PostAsync($"/v1/datasources/{datasetId}/data", content, ct);response.EnsureSuccessStatusCode();}
}
