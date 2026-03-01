using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Dashboard;

/// <summary>Power BI connection strategy. HTTPS to api.powerbi.com. Microsoft BI platform.</summary>
public sealed class PowerBiConnectionStrategy : DashboardConnectionStrategyBase
{
    public override string StrategyId => "powerbi";public override string DisplayName => "Power BI";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "Microsoft Power BI. Cloud BI service with natural language queries and Azure integration.";public override string[] Tags => ["powerbi", "microsoft", "bi", "cloud", "azure"];
    public PowerBiConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://api.powerbi.com";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };if (config.Properties.TryGetValue("Token", out var token)){httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.ToString()!);}return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "PowerBI", ["BaseUrl"] = baseUrl });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct){try{var httpClient = handle.GetConnection<HttpClient>();using var response = await httpClient.GetAsync("/", ct);return response.IsSuccessStatusCode;}catch(OperationCanceledException){throw;}catch{return false;}}
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){var sw = System.Diagnostics.Stopwatch.StartNew();try{var httpClient = handle.GetConnection<HttpClient>();using var response = await httpClient.GetAsync("/", ct);sw.Stop();return new ConnectionHealth(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "PowerBi connected" : "PowerBi error: HTTP " + (int)response.StatusCode, sw.Elapsed, DateTimeOffset.UtcNow);}catch(OperationCanceledException){throw;}catch(Exception ex){sw.Stop();return new ConnectionHealth(false, $"PowerBi error: {ex.Message}", sw.Elapsed, DateTimeOffset.UtcNow);}}
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default){var httpClient = handle.GetConnection<HttpClient>();var content = new StringContent(dashboardDefinition, Encoding.UTF8, "application/json");using var response = await httpClient.PostAsync("/v1.0/myorg/dashboards", content, ct);response.EnsureSuccessStatusCode();var result = await response.Content.ReadAsStringAsync(ct);using var json = JsonDocument.Parse(result);return json.RootElement.GetProperty("id").GetString() ?? "";}
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Power BI PushDataAsync requires a specific table name that cannot be hardcoded. " +
            "Use the Power BI REST API directly: POST /v1.0/myorg/datasets/{datasetId}/tables/{tableName}/rows " +
            "with a valid OAuth2 Bearer token obtained via MSAL (Microsoft.Identity.Client).");
}
