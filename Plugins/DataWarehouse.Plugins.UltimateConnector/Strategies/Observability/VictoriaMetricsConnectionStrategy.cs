using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Observability;

/// <summary>
/// VictoriaMetrics connection strategy for high-performance metrics storage and querying.
/// Connects to VictoriaMetrics HTTP API on port 8428. Prometheus-compatible remote write and PromQL.
/// Provides long-term metrics storage with superior compression and query performance.
/// </summary>
public sealed class VictoriaMetricsConnectionStrategy : ObservabilityConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "victoriametrics";

    /// <inheritdoc/>
    public override string DisplayName => "VictoriaMetrics";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "VictoriaMetrics time-series database. Prometheus-compatible with better performance and compression. " +
        "Supports PromQL, Graphite, InfluxDB protocols. Ideal for long-term metrics storage, " +
        "high-cardinality data, and cost-effective monitoring at scale.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "victoriametrics", "metrics", "time-series", "prometheus-compatible",
        "high-performance", "compression", "long-term-storage", "monitoring"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="VictoriaMetricsConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public VictoriaMetricsConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:8428";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        if (config.Properties.TryGetValue("Username", out var username) &&
            config.Properties.TryGetValue("Password", out var password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "VictoriaMetrics",
            ["BaseUrl"] = baseUrl
        };

        return new DefaultConnectionHandle(httpClient, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        try
        {
            using var response = await httpClient.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        httpClient.Dispose();
        if (handle is DefaultConnectionHandle defaultHandle)
        {
            defaultHandle.MarkDisconnected();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.GetAsync("/health", ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: response.IsSuccessStatusCode,
                StatusMessage: response.IsSuccessStatusCode ? "VictoriaMetrics is healthy" : $"Status: {response.StatusCode}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc/>
    public override async Task PushMetricsAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> metrics,
        CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { series = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync("/api/v1/import/prometheus", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public override Task PushLogsAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> logs,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("VictoriaMetrics focuses on metrics. Use VictoriaLogs for log storage.");
    }

    /// <inheritdoc/>
    public override Task PushTracesAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> traces,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("VictoriaMetrics does not support traces. Use Tempo or Jaeger for traces.");
    }
}
