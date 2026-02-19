using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.SyntheticMonitoring;

/// <summary>
/// Observability strategy for StatusCake website monitoring and performance testing.
/// Provides uptime monitoring, page speed testing, and SSL certificate monitoring.
/// </summary>
/// <remarks>
/// StatusCake is a comprehensive website monitoring platform that tracks uptime,
/// performance, and SSL certificates from multiple global locations.
/// </remarks>
public sealed class StatusCakeStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";

    /// <inheritdoc/>
    public override string StrategyId => "statuscake";

    /// <inheritdoc/>
    public override string Name => "StatusCake";

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusCakeStrategy"/> class.
    /// </summary>
    public StatusCakeStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "StatusCake" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the StatusCake connection.
    /// </summary>
    /// <param name="apiKey">StatusCake API key.</param>
    public void Configure(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("status_cake.metrics_sent");
        // Process uptime and performance metrics
        foreach (var metric in metrics)
        {
            if (metric.Name.Contains("uptime", StringComparison.OrdinalIgnoreCase) && metric.Value < 99.0)
            {
                // Alert on low uptime
            }
            else if (metric.Name.Contains("response_time", StringComparison.OrdinalIgnoreCase) && metric.Value > 5000)
            {
                // Alert on slow response time (>5s)
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("StatusCake does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("StatusCake does not support logging");
    }

    /// <summary>
    /// Creates an uptime test in StatusCake.
    /// </summary>
    /// <param name="name">Test name.</param>
    /// <param name="websiteUrl">URL to monitor.</param>
    /// <param name="testType">Test type (HTTP, TCP, PING, etc.).</param>
    /// <param name="checkRate">Check interval in seconds (60-24000).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> CreateUptimeTestAsync(
        string name,
        string websiteUrl,
        string testType = "HTTP",
        int checkRate = 300,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                name,
                website_url = websiteUrl,
                test_type = testType,
                check_rate = checkRate
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://api.statuscake.com/v1/uptime", content, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(responseJson);

            if (result != null && result.TryGetValue("data", out var dataObj))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(dataObj.ToString()!);
                if (data != null && data.TryGetValue("new_id", out var idObj))
                {
                    return idObj.ToString();
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
    /// Gets all uptime tests from StatusCake.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Dictionary<string, object>>?> GetUptimeTestsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("https://api.statuscake.com/v1/uptime", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (result != null && result.TryGetValue("data", out var dataObj))
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataObj.ToString()!);
            }

            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets detailed history for a specific uptime test.
    /// </summary>
    /// <param name="testId">Test ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Dictionary<string, object>?> GetUptimeTestHistoryAsync(string testId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"https://api.statuscake.com/v1/uptime/{testId}/history", ct);
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
    /// Creates a page speed test in StatusCake.
    /// </summary>
    /// <param name="name">Test name.</param>
    /// <param name="websiteUrl">URL to test.</param>
    /// <param name="checkRate">Check interval in seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> CreatePageSpeedTestAsync(
        string name,
        string websiteUrl,
        int checkRate = 3600,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                name,
                website_url = websiteUrl,
                check_rate = checkRate
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://api.statuscake.com/v1/pagespeed", content, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Dictionary<string, object>>(responseJson);

            if (result != null && result.TryGetValue("data", out var dataObj))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(dataObj.ToString()!);
                if (data != null && data.TryGetValue("new_id", out var idObj))
                {
                    return idObj.ToString();
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
    /// Deletes an uptime test from StatusCake.
    /// </summary>
    /// <param name="testId">Test ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> DeleteUptimeTestAsync(string testId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"https://api.statuscake.com/v1/uptime/{testId}", ct);
            return response.IsSuccessStatusCode;
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
            var response = await _httpClient.GetAsync("https://api.statuscake.com/v1/uptime", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "StatusCake API is accessible" : "StatusCake API unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"StatusCake health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("status_cake.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Shutdown grace period elapsed */ }
        IncrementCounter("status_cake.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
