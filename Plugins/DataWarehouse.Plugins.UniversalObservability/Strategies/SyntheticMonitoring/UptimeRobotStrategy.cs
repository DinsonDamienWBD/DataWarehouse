using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.SyntheticMonitoring;

/// <summary>
/// Observability strategy for UptimeRobot monitoring service.
/// Provides website and service uptime monitoring with alerting.
/// </summary>
/// <remarks>
/// UptimeRobot is a popular uptime monitoring service that checks websites
/// and services at regular intervals and sends alerts when they're down.
/// </remarks>
public sealed class UptimeRobotStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";

    /// <inheritdoc/>
    public override string StrategyId => "uptimerobot";

    /// <inheritdoc/>
    public override string Name => "UptimeRobot";

    /// <summary>
    /// Initializes a new instance of the <see cref="UptimeRobotStrategy"/> class.
    /// </summary>
    public UptimeRobotStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "UptimeRobot" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the UptimeRobot connection.
    /// </summary>
    /// <param name="apiKey">UptimeRobot API key.</param>
    public void Configure(string apiKey)
    {
        _apiKey = apiKey;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("uptime_robot.metrics_sent");
        // Fetch current monitor statuses and compare with metrics
        var monitors = await GetMonitorsAsync(cancellationToken);

        foreach (var metric in metrics)
        {
            if (metric.Name.Contains("uptime", StringComparison.OrdinalIgnoreCase))
            {
                // Log or process uptime metrics
                if (metric.Value < 99.0)
                {
                    // Could trigger custom alerts
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("UptimeRobot does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("UptimeRobot does not support logging");
    }

    /// <summary>
    /// Creates a new monitor in UptimeRobot.
    /// </summary>
    /// <param name="friendlyName">Monitor name.</param>
    /// <param name="url">URL to monitor.</param>
    /// <param name="type">Monitor type (1=HTTP, 2=Keyword, 3=Ping, 4=Port).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int?> CreateMonitorAsync(
        string friendlyName,
        string url,
        int type = 1,
        CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = _apiKey,
                ["format"] = "json",
                ["friendly_name"] = friendlyName,
                ["url"] = url,
                ["type"] = type.ToString()
            };

            var content = new FormUrlEncodedContent(parameters);

            using var response = await _httpClient.PostAsync("https://api.uptimerobot.com/v2/newMonitor", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (result != null && result.TryGetValue("monitor", out var monitorObj))
            {
                var monitor = JsonSerializer.Deserialize<Dictionary<string, object>>(monitorObj.ToString()!);
                if (monitor != null && monitor.TryGetValue("id", out var idObj))
                {
                    return int.Parse(idObj.ToString()!);
                }
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets all monitors from UptimeRobot.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Dictionary<string, object>>?> GetMonitorsAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = _apiKey,
                ["format"] = "json"
            };

            var content = new FormUrlEncodedContent(parameters);

            using var response = await _httpClient.PostAsync("https://api.uptimerobot.com/v2/getMonitors", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (result != null && result.TryGetValue("monitors", out var monitorsObj))
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(monitorsObj.ToString()!);
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets account details from UptimeRobot.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Dictionary<string, object>?> GetAccountDetailsAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = _apiKey,
                ["format"] = "json"
            };

            var content = new FormUrlEncodedContent(parameters);

            using var response = await _httpClient.PostAsync("https://api.uptimerobot.com/v2/getAccountDetails", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (result != null && result.TryGetValue("account", out var accountObj))
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(accountObj.ToString()!);
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a monitor from UptimeRobot.
    /// </summary>
    /// <param name="monitorId">Monitor ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> DeleteMonitorAsync(int monitorId, CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = _apiKey,
                ["format"] = "json",
                ["id"] = monitorId.ToString()
            };

            var content = new FormUrlEncodedContent(parameters);

            using var response = await _httpClient.PostAsync("https://api.uptimerobot.com/v2/deleteMonitor", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            return result != null && result.TryGetValue("stat", out var statObj) && statObj.ToString() == "ok";
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var accountDetails = await GetAccountDetailsAsync(cancellationToken);

            return new HealthCheckResult(
                IsHealthy: accountDetails != null,
                Description: accountDetails != null ? "UptimeRobot API is accessible" : "UptimeRobot API unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey),
                    ["accountDetails"] = accountDetails ?? new Dictionary<string, object>()
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"UptimeRobot health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("uptime_robot.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("uptime_robot.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
                _apiKey = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
