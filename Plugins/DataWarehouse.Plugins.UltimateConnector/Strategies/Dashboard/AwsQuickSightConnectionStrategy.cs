using System;using System.Collections.Generic;using System.Net.Http;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Dashboard;

/// <summary>AWS QuickSight. HTTPS to quicksight.*.amazonaws.com. Cloud-native BI for AWS.</summary>
public sealed class AwsQuickSightConnectionStrategy : DashboardConnectionStrategyBase
{
    public override string StrategyId => "aws-quicksight";public override string DisplayName => "AWS QuickSight";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "AWS QuickSight cloud BI service. Serverless BI with ML-powered insights.";public override string[] Tags => ["aws", "quicksight", "bi", "cloud", "serverless"];
    public AwsQuickSightConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://quicksight.us-east-1.amazonaws.com";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "QuickSight", ["BaseUrl"] = baseUrl });}
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(true);
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(true, "QuickSight configured", TimeSpan.Zero, DateTimeOffset.UtcNow));
    public override async Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default){var httpClient = handle.GetConnection<HttpClient>();var content = new StringContent(dashboardDefinition, Encoding.UTF8, "application/json");var response = await httpClient.PostAsync("/accounts/ACCOUNT_ID/dashboards", content, ct);response.EnsureSuccessStatusCode();var result = await response.Content.ReadAsStringAsync(ct);var json = JsonDocument.Parse(result);return json.RootElement.GetProperty("DashboardId").GetString() ?? "";}
    public override async Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default){var json = JsonSerializer.Serialize(new { Rows = data });var content = new StringContent(json, Encoding.UTF8, "application/json");var response = await handle.GetConnection<HttpClient>().PostAsync($"/accounts/ACCOUNT_ID/data-sets/{datasetId}/ingestions", content, ct);response.EnsureSuccessStatusCode();}
}
