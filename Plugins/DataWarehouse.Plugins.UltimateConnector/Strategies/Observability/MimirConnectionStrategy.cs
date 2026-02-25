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
/// Grafana Mimir connection strategy for horizontally scalable metrics storage.
/// Prometheus remote write compatible. Designed for multi-tenancy and massive scale.
/// Provides long-term storage with query federation and global views across tenants.
/// </summary>
public sealed class MimirConnectionStrategy : ObservabilityConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "mimir";

    /// <inheritdoc/>
    public override string DisplayName => "Grafana Mimir";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Grafana Mimir horizontally scalable long-term storage for Prometheus. " +
        "Multi-tenant architecture with global query views. Ideal for large-scale " +
        "metrics aggregation, SaaS platforms, and enterprise monitoring at scale.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "mimir", "grafana", "prometheus", "multi-tenant", "scalable",
        "long-term-storage", "metrics", "cloud-native", "cncf"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="MimirConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public MimirConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? throw new ArgumentException("Mimir endpoint URL is required");
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        if (config.Properties.TryGetValue("TenantId", out var tenantId))
        {
            httpClient.DefaultRequestHeaders.Remove("X-Scope-OrgID");
            httpClient.DefaultRequestHeaders.Add("X-Scope-OrgID", tenantId.ToString()!);
        }

        if (config.Properties.TryGetValue("Username", out var username) &&
            config.Properties.TryGetValue("Password", out var password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "Mimir",
            ["BaseUrl"] = baseUrl,
            ["TenantId"] = tenantId?.ToString() ?? "default"
        };

        return new DefaultConnectionHandle(httpClient, connectionInfo);
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        try
        {
            var response = await httpClient.GetAsync("/ready", ct);
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
            var response = await httpClient.GetAsync("/ready", ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: response.IsSuccessStatusCode,
                StatusMessage: response.IsSuccessStatusCode ? "Mimir is ready" : $"Status: {response.StatusCode}",
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
        var json = JsonSerializer.Serialize(new { timeseries = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync("/api/v1/push", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public override Task PushLogsAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> logs,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Mimir is for metrics only. Use Loki for logs.");
    }

    /// <inheritdoc/>
    public override Task PushTracesAsync(
        IConnectionHandle handle,
        IReadOnlyList<Dictionary<string, object>> traces,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Mimir does not support traces. Use Tempo for traces.");
    }
}
