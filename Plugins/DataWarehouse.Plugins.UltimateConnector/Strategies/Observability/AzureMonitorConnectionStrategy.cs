using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>Azure Monitor connection strategy. HTTPS to *.ingest.monitor.azure.com. Azure native monitoring.</summary>
public sealed class AzureMonitorConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "azure-monitor";public override string DisplayName => "Azure Monitor";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "Azure Monitor. Full observability for Azure resources with metrics, logs, and Application Insights.";public override string[] Tags => ["azure", "monitor", "observability", "metrics", "logs", "cloud"];
    public AzureMonitorConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? throw new ArgumentException("Azure Monitor ingestion endpoint required");var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };if (config.Properties.TryGetValue("Token", out var token)){httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.ToString()!);}return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "AzureMonitor", ["BaseUrl"] = baseUrl });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        // Finding 2078: Probe the Azure Monitor ingestion endpoint to verify real connectivity.
        try
        {
            using var response = await handle.GetConnection<HttpClient>().GetAsync("/health", ct);
            return (int)response.StatusCode != 503;
        }
        catch { return false; }
    }
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var isHealthy = await TestCoreAsync(handle, ct);
        sw.Stop();
        return new ConnectionHealth(isHealthy, isHealthy ? "Azure Monitor reachable" : "Azure Monitor unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
    }
    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { data = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        // Finding 2078: Azure Monitor Data Collection Endpoint (DCE) path format:
        // POST /dataCollectionRules/{dcrImmutableId}/streams/{streamName}?api-version=2021-11-01-preview
        var response = await handle.GetConnection<HttpClient>().PostAsync("/dataCollectionRules/metrics/streams/Custom-MetricsStream?api-version=2021-11-01-preview", content, ct);
        response.EnsureSuccessStatusCode();
    }
    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(logs);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        // Finding 2078: Azure Monitor Data Collection Endpoint (DCE) path format.
        var response = await handle.GetConnection<HttpClient>().PostAsync("/dataCollectionRules/logs/streams/Custom-LogsStream?api-version=2021-11-01-preview", content, ct);
        response.EnsureSuccessStatusCode();
    }
    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Use Application Insights for traces.");
}
