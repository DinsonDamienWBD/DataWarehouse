using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Logging;

/// <summary>
/// Observability strategy for Loggly cloud-based log management.
/// Provides centralized log aggregation, search, and analytics.
/// </summary>
/// <remarks>
/// Loggly is a cloud-based log management and analytics service that simplifies
/// log aggregation, search, and visualization for modern applications.
/// </remarks>
public sealed class LogglyStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _token = "";
    private string _tag = "datawarehouse";
    private string _endpoint = "https://logs-01.loggly.com";

    /// <inheritdoc/>
    public override string StrategyId => "loggly";

    /// <inheritdoc/>
    public override string Name => "Loggly";

    /// <summary>
    /// Initializes a new instance of the <see cref="LogglyStrategy"/> class.
    /// </summary>
    public LogglyStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Loggly", "HTTP" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Loggly connection.
    /// </summary>
    /// <param name="token">Loggly customer token.</param>
    /// <param name="tag">Tag for log categorization.</param>
    public void Configure(string token, string tag = "datawarehouse")
    {
        _token = token;
        _tag = tag;
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Loggly does not support metrics - use logging instead");
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Loggly does not support tracing");
    }

    /// <inheritdoc/>
    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("loggly.logs_sent");
        var logs = logEntries.Select(log => new
        {
            timestamp = log.Timestamp.ToString("o"),
            level = log.Level.ToString(),
            message = log.Message,
            source = log.Properties?.GetValueOrDefault("Source")?.ToString(),
            category = log.Properties?.GetValueOrDefault("Category")?.ToString(),
            eventId = log.Properties?.GetValueOrDefault("EventId")?.ToString(),
            exception = log.Exception?.ToString(),
            tags = new[] { _tag }
        }).ToList();

        // Loggly supports bulk upload
        await SendLogsAsync(logs, cancellationToken);
    }

    private async Task SendLogsAsync(IEnumerable<object> logs, CancellationToken ct)
    {
        try
        {
            // Send as bulk JSON
            var json = JsonSerializer.Serialize(logs);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_endpoint}/bulk/{_token}/tag/{_tag}";
            using var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Loggly unavailable - logs lost
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a single log event to Loggly.
    /// </summary>
    /// <param name="message">Log message.</param>
    /// <param name="level">Log level.</param>
    /// <param name="additionalData">Optional additional fields.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendLogAsync(
        string message,
        LogLevel level,
        Dictionary<string, object>? additionalData = null,
        CancellationToken ct = default)
    {
        var logEvent = new Dictionary<string, object>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
            ["level"] = level.ToString(),
            ["message"] = message,
            ["tags"] = new[] { _tag }
        };

        if (additionalData != null)
        {
            foreach (var (key, value) in additionalData)
            {
                logEvent[key] = value;
            }
        }

        var json = JsonSerializer.Serialize(logEvent);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = $"{_endpoint}/inputs/{_token}/tag/{_tag}";

        try
        {
            using var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Loggly unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Send a test log to verify connectivity
            var testLog = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                level = "Info",
                message = "Loggly health check",
                tags = new[] { _tag, "health-check" }
            };

            var json = JsonSerializer.Serialize(testLog);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{_endpoint}/inputs/{_token}/tag/{_tag}";

            using var response = await _httpClient.PostAsync(url, content, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Loggly is healthy" : "Loggly unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["endpoint"] = _endpoint,
                    ["tag"] = _tag,
                    ["hasToken"] = !string.IsNullOrEmpty(_token)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Loggly health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_endpoint) || (!_endpoint.StartsWith("http://") && !_endpoint.StartsWith("https://")))
            throw new InvalidOperationException("LogglyStrategy: Invalid endpoint URL configured.");
        IncrementCounter("loggly.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("loggly.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
                _token = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
