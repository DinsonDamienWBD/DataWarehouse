using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.RealUserMonitoring;

/// <summary>
/// Observability strategy for Google Analytics 4 (GA4) user behavior tracking.
/// Provides event tracking, user analytics, and conversion measurement.
/// </summary>
/// <remarks>
/// Google Analytics 4 is a next-generation analytics service that measures traffic
/// and engagement across websites and apps with event-based data collection.
/// </remarks>
public sealed class GoogleAnalyticsStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _measurementId = "";
    private string _apiSecret = "";
    private string _clientId = Guid.NewGuid().ToString();

    /// <inheritdoc/>
    public override string StrategyId => "google-analytics";

    /// <inheritdoc/>
    public override string Name => "Google Analytics";

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleAnalyticsStrategy"/> class.
    /// </summary>
    public GoogleAnalyticsStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "GoogleAnalytics", "GA4" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Google Analytics connection.
    /// </summary>
    /// <param name="measurementId">GA4 Measurement ID (G-XXXXXXXXXX).</param>
    /// <param name="apiSecret">Measurement Protocol API secret.</param>
    /// <param name="clientId">Optional client ID (generated if not provided).</param>
    public void Configure(string measurementId, string apiSecret, string? clientId = null)
    {
        _measurementId = measurementId;
        _apiSecret = apiSecret;
        _clientId = clientId ?? Guid.NewGuid().ToString();
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("google_analytics.metrics_sent");
        var events = metrics.Select(metric => new
        {
            name = SanitizeEventName(metric.Name),
            @params = new Dictionary<string, object>
            {
                ["value"] = metric.Value,
                ["metric_type"] = metric.Type.ToString(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }.Concat(metric.Labels?.Select(l => new KeyValuePair<string, object>(l.Name, l.Value)) ?? Enumerable.Empty<KeyValuePair<string, object>>())
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToList();

        await SendEventsAsync(events, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Google Analytics does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Google Analytics does not support logging");
    }

    /// <summary>
    /// Tracks a custom event in Google Analytics.
    /// </summary>
    /// <param name="eventName">Event name.</param>
    /// <param name="parameters">Event parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TrackEventAsync(
        string eventName,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        var evt = new
        {
            name = SanitizeEventName(eventName),
            @params = parameters ?? new Dictionary<string, object>()
        };

        await SendEventsAsync(new[] { evt }, ct);
    }

    /// <summary>
    /// Tracks a page view in Google Analytics.
    /// </summary>
    /// <param name="pageTitle">Page title.</param>
    /// <param name="pageLocation">Page URL.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TrackPageViewAsync(
        string pageTitle,
        string pageLocation,
        CancellationToken ct = default)
    {
        var evt = new
        {
            name = "page_view",
            @params = new Dictionary<string, object>
            {
                ["page_title"] = pageTitle,
                ["page_location"] = pageLocation,
                ["engagement_time_msec"] = 100
            }
        };

        await SendEventsAsync(new[] { evt }, ct);
    }

    private async Task SendEventsAsync(IEnumerable<object> events, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                client_id = _clientId,
                events
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://www.google-analytics.com/mp/collect?measurement_id={_measurementId}&api_secret={_apiSecret}";
            using var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Google Analytics unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SanitizeEventName(string name)
    {
        // GA4 event names must start with a letter, contain only letters, numbers, and underscores
        var sanitized = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (!char.IsLetter(sanitized[0]))
        {
            sanitized = "metric_" + sanitized;
        }
        return sanitized.ToLowerInvariant();
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_measurementId) && !string.IsNullOrEmpty(_apiSecret);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Google Analytics is configured" : "Google Analytics credentials not configured",
            Data: new Dictionary<string, object>
            {
                ["measurementId"] = _measurementId,
                ["clientId"] = _clientId,
                ["hasApiSecret"] = !string.IsNullOrEmpty(_apiSecret)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("google_analytics.initialized");
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
        IncrementCounter("google_analytics.shutdown");
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
