using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.SyntheticMonitoring;

/// <summary>
/// Observability strategy for Pingdom synthetic monitoring and uptime tracking.
/// Provides website monitoring, uptime tracking, and performance testing.
/// </summary>
/// <remarks>
/// Pingdom is a synthetic monitoring service that tracks website uptime,
/// performance, and real user experience from multiple global locations.
/// </remarks>
public sealed class PingdomStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiToken = "";

    /// <inheritdoc/>
    public override string StrategyId => "pingdom";

    /// <inheritdoc/>
    public override string Name => "Pingdom";

    /// <summary>
    /// Initializes a new instance of the <see cref="PingdomStrategy"/> class.
    /// </summary>
    public PingdomStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Pingdom" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Pingdom connection.
    /// </summary>
    /// <param name="apiToken">Pingdom API token.</param>
    public void Configure(string apiToken)
    {
        _apiToken = apiToken;
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        // For each metric representing uptime/availability, we could create or update checks
        // This is a read-heavy API, so we'll log metrics for now
        foreach (var metric in metrics)
        {
            if (metric.Name.Contains("uptime", StringComparison.OrdinalIgnoreCase) ||
                metric.Name.Contains("availability", StringComparison.OrdinalIgnoreCase))
            {
                // Could trigger alerts if uptime drops below threshold
                if (metric.Value < 99.0)
                {
                    await TriggerAlertAsync($"Low uptime detected: {metric.Name} = {metric.Value}%", cancellationToken);
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Pingdom does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Pingdom does not support logging");
    }

    /// <summary>
    /// Creates an uptime check in Pingdom.
    /// </summary>
    /// <param name="name">Check name.</param>
    /// <param name="host">Host to monitor.</param>
    /// <param name="type">Check type (http, ping, tcp, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int?> CreateCheckAsync(
        string name,
        string host,
        string type = "http",
        CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["name"] = name,
                ["host"] = host,
                ["type"] = type,
                ["resolution"] = "1" // Check every 1 minute
            };

            var content = new FormUrlEncodedContent(parameters);

            var response = await _httpClient.PostAsync("https://api.pingdom.com/api/3.1/checks", content, ct);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonResponse);

            if (result != null && result.TryGetValue("check", out var checkObj))
            {
                var check = JsonSerializer.Deserialize<Dictionary<string, object>>(checkObj.ToString()!);
                if (check != null && check.TryGetValue("id", out var idObj))
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
    /// Gets the results of a Pingdom check.
    /// </summary>
    /// <param name="checkId">Check ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Dictionary<string, object>?> GetCheckResultsAsync(int checkId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"https://api.pingdom.com/api/3.1/checks/{checkId}", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all Pingdom checks.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Dictionary<string, object>>?> ListChecksAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("https://api.pingdom.com/api/3.1/checks", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (result != null && result.TryGetValue("checks", out var checksObj))
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(checksObj.ToString()!);
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task TriggerAlertAsync(string message, CancellationToken ct)
    {
        // Pingdom handles alerts automatically based on check configurations
        // This is a no-op placeholder for custom alert logic
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("https://api.pingdom.com/api/3.1/checks", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Pingdom API is accessible" : "Pingdom API unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["hasApiToken"] = !string.IsNullOrEmpty(_apiToken)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Pingdom health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
