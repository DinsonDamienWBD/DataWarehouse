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
/// Elasticsearch logging connection strategy. Connects to Elasticsearch port 9200 (/_bulk endpoint).
/// Full-text search indexing for logs with powerful query capabilities.
/// </summary>
public sealed class ElasticsearchLoggingConnectionStrategy : ObservabilityConnectionStrategyBase
{
    public override string StrategyId => "elasticsearch-logging";
    public override string DisplayName => "Elasticsearch Logging";
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription => "Elasticsearch for log storage and analysis. Full-text search, aggregations, and analytics. Ideal for centralized logging and log analytics.";
    public override string[] Tags => ["elasticsearch", "logs", "full-text-search", "analytics", "elk-stack"];

    public ElasticsearchLoggingConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:9200";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };
        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Elasticsearch", ["BaseUrl"] = baseUrl });
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode; } catch { return false; /* Connection validation - failure acceptable */ }
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
        try { var response = await handle.GetConnection<HttpClient>().GetAsync("/_cluster/health", ct); sw.Stop(); return new ConnectionHealth(response.IsSuccessStatusCode, "Elasticsearch healthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        catch (Exception ex) { sw.Stop(); return new ConnectionHealth(false, ex.Message, sw.Elapsed, DateTimeOffset.UtcNow); }
    }

    public override Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default) => throw new NotSupportedException("Use Elasticsearch metrics exporter.");

    public override async Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var bulkData = new StringBuilder();
        foreach (var log in logs)
        {
            bulkData.AppendLine(JsonSerializer.Serialize(new { index = new { _index = "logs" } }));
            bulkData.AppendLine(JsonSerializer.Serialize(log));
        }
        var content = new StringContent(bulkData.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var response = await httpClient.PostAsync("/_bulk", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default) => throw new NotSupportedException("Use Elasticsearch APM for traces.");
}
