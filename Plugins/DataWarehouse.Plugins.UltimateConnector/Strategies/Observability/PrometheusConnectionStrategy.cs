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
/// Prometheus connection strategy for metrics collection and querying.
/// Connects to Prometheus HTTP API on port 9090 (/api/v1/write, /api/v1/query).
/// Supports remote write protocol for pushing metrics and PromQL for querying.
/// </summary>
public sealed class PrometheusConnectionStrategy : ObservabilityConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "prometheus";

    /// <inheritdoc/>
    public override string DisplayName => "Prometheus";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Prometheus metrics monitoring system. Supports time-series metrics collection, " +
        "remote write protocol, PromQL querying, and alerting. Ideal for infrastructure monitoring, " +
        "application performance tracking, and service health metrics.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "prometheus", "metrics", "monitoring", "time-series", "observability",
        "alerting", "promql", "open-source", "cncf"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="PrometheusConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PrometheusConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:9090";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        // Add basic auth if provided
        if (config.Properties.TryGetValue("Username", out var username) &&
            config.Properties.TryGetValue("Password", out var password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "Prometheus",
            ["BaseUrl"] = baseUrl,
            ["Endpoint"] = "/api/v1"
        };

        return new DefaultConnectionHandle(httpClient, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        try
        {
            var response = await httpClient.GetAsync("/-/healthy", ct);
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
            var response = await httpClient.GetAsync("/-/ready", ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: response.IsSuccessStatusCode,
                StatusMessage: response.IsSuccessStatusCode ? "Prometheus is ready" : $"Status: {response.StatusCode}",
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

        var response = await httpClient.PostAsync("/api/v1/write", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public override Task PushLogsAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> logs,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Prometheus does not support log ingestion. Use Loki for logs.");
    }

    /// <inheritdoc/>
    public override Task PushTracesAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> traces,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Prometheus does not support trace ingestion. Use Tempo for traces.");
    }
}
