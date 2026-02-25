using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.RealUserMonitoring;

/// <summary>
/// Observability strategy for Amplitude product analytics and user behavior tracking.
/// Provides event tracking, user analytics, and behavioral cohort analysis.
/// </summary>
/// <remarks>
/// Amplitude is a product analytics platform that helps teams understand user behavior,
/// track engagement, and optimize the product experience with behavioral data.
/// </remarks>
public sealed class AmplitudeStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";
    private string _userId = "";
    private string _deviceId = Guid.NewGuid().ToString();

    /// <inheritdoc/>
    public override string StrategyId => "amplitude";

    /// <inheritdoc/>
    public override string Name => "Amplitude";

    /// <summary>
    /// Initializes a new instance of the <see cref="AmplitudeStrategy"/> class.
    /// </summary>
    public AmplitudeStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Amplitude" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Amplitude connection.
    /// </summary>
    /// <param name="apiKey">Amplitude API key.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="deviceId">Device identifier (generated if not provided).</param>
    public void Configure(string apiKey, string userId = "system", string? deviceId = null)
    {
        _apiKey = apiKey;
        _userId = userId;
        _deviceId = deviceId ?? Guid.NewGuid().ToString();
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("amplitude.metrics_sent");
        var events = metrics.Select(metric => new
        {
            user_id = _userId,
            device_id = _deviceId,
            event_type = metric.Name,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            event_properties = new Dictionary<string, object>
            {
                ["value"] = metric.Value,
                ["type"] = metric.Type.ToString()
            }.Concat(metric.Labels?.Select(l => new KeyValuePair<string, object>(l.Name, l.Value)) ?? Enumerable.Empty<KeyValuePair<string, object>>())
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            user_properties = new Dictionary<string, object>
            {
                ["platform"] = "DataWarehouse",
                ["environment"] = "server"
            }
        }).ToList();

        await SendEventsAsync(events, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Amplitude does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Amplitude does not support logging");
    }

    /// <summary>
    /// Tracks a custom event in Amplitude.
    /// </summary>
    /// <param name="eventType">Event type name.</param>
    /// <param name="eventProperties">Event properties.</param>
    /// <param name="userProperties">User properties.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TrackEventAsync(
        string eventType,
        Dictionary<string, object>? eventProperties = null,
        Dictionary<string, object>? userProperties = null,
        CancellationToken ct = default)
    {
        var evt = new
        {
            user_id = _userId,
            device_id = _deviceId,
            event_type = eventType,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            event_properties = eventProperties ?? new Dictionary<string, object>(),
            user_properties = userProperties ?? new Dictionary<string, object>()
        };

        await SendEventsAsync(new[] { evt }, ct);
    }

    /// <summary>
    /// Identifies a user with properties in Amplitude.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="userProperties">User properties to set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task IdentifyUserAsync(
        string userId,
        Dictionary<string, object> userProperties,
        CancellationToken ct = default)
    {
        var identification = new
        {
            user_id = userId,
            device_id = _deviceId,
            user_properties = userProperties
        };

        var payload = new
        {
            api_key = _apiKey,
            identification = new[] { identification }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://api2.amplitude.com/identify", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Amplitude unavailable
        }
    }

    private async Task SendEventsAsync(IEnumerable<object> events, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                api_key = _apiKey,
                events
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://api2.amplitude.com/2/httpapi", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Amplitude unavailable
        }
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_apiKey);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Amplitude is configured" : "Amplitude API key not configured",
            Data: new Dictionary<string, object>
            {
                ["userId"] = _userId,
                ["deviceId"] = _deviceId,
                ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("amplitude.initialized");
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
        IncrementCounter("amplitude.shutdown");
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
