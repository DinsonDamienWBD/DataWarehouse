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
/// Thanos connection strategy for highly available Prometheus setup with global query view.
/// Connects to Thanos Receiver for remote write. Provides unlimited retention and downsampling.
/// </summary>
public sealed class ThanosConnectionStrategy : ObservabilityConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "thanos";

    /// <inheritdoc/>
    public override string DisplayName => "Thanos";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Thanos global view for Prometheus with object storage backend. Provides unlimited retention, " +
        "downsampling, query federation, and high availability. Ideal for multi-cluster monitoring and long-term storage.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "thanos", "prometheus", "global-view", "object-storage", "downsampling",
        "long-term-storage", "multi-cluster", "high-availability", "cncf"
    ];

    public ThanosConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "http://localhost:19291";
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

        return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
        {
            ["Provider"] = "Thanos",
            ["BaseUrl"] = baseUrl
        });
    }

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

    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        handle.GetConnection<HttpClient>().Dispose();
        if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();
        return Task.CompletedTask;
    }

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
                StatusMessage: response.IsSuccessStatusCode ? "Thanos is ready" : $"Status: {response.StatusCode}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(false, $"Health check failed: {ex.Message}", sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }

    public override async Task PushMetricsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> metrics, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var json = JsonSerializer.Serialize(new { timeseries = metrics });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("/api/v1/receive", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public override Task PushLogsAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> logs, CancellationToken ct = default)
    {
        throw new NotSupportedException("Thanos is for metrics only. Use Loki for logs.");
    }

    public override Task PushTracesAsync(IConnectionHandle handle, IReadOnlyList<Dictionary<string, object>> traces, CancellationToken ct = default)
    {
        throw new NotSupportedException("Thanos does not support traces.");
    }
}
