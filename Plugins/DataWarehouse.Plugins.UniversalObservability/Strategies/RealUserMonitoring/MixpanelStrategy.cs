using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.RealUserMonitoring;

/// <summary>
/// Observability strategy for Mixpanel product analytics and user engagement tracking.
/// Provides event tracking, funnel analysis, and user journey insights.
/// </summary>
/// <remarks>
/// Mixpanel is a product analytics platform that helps teams analyze user behavior,
/// measure retention, and optimize product experiences with event-based analytics.
/// </remarks>
public sealed class MixpanelStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _projectToken = "";
    private string _distinctId = Guid.NewGuid().ToString();

    /// <inheritdoc/>
    public override string StrategyId => "mixpanel";

    /// <inheritdoc/>
    public override string Name => "Mixpanel";

    /// <summary>
    /// Initializes a new instance of the <see cref="MixpanelStrategy"/> class.
    /// </summary>
    public MixpanelStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Mixpanel" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Mixpanel connection.
    /// </summary>
    /// <param name="projectToken">Mixpanel project token.</param>
    /// <param name="distinctId">Distinct user/device identifier (generated if not provided).</param>
    public void Configure(string projectToken, string? distinctId = null)
    {
        _projectToken = projectToken;
        _distinctId = distinctId ?? Guid.NewGuid().ToString();
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("mixpanel.metrics_sent");
        var events = metrics.Select(metric =>
        {
            var properties = new Dictionary<string, object>
            {
                ["token"] = _projectToken,
                ["distinct_id"] = _distinctId,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["value"] = metric.Value,
                ["metric_type"] = metric.Type.ToString()
            };

            // Add labels as properties
            if (metric.Labels != null)
            {
                foreach (var label in metric.Labels)
                {
                    properties[label.Name] = label.Value;
                }
            }

            return new
            {
                @event = metric.Name,
                properties
            };
        }).ToList();

        await TrackEventsAsync(events, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Mixpanel does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Mixpanel does not support logging");
    }

    /// <summary>
    /// Tracks a custom event in Mixpanel.
    /// </summary>
    /// <param name="eventName">Event name.</param>
    /// <param name="properties">Event properties.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TrackAsync(
        string eventName,
        Dictionary<string, object>? properties = null,
        CancellationToken ct = default)
    {
        var eventProperties = new Dictionary<string, object>
        {
            ["token"] = _projectToken,
            ["distinct_id"] = _distinctId,
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (properties != null)
        {
            foreach (var (key, value) in properties)
            {
                eventProperties[key] = value;
            }
        }

        var evt = new
        {
            @event = eventName,
            properties = eventProperties
        };

        await TrackEventsAsync(new[] { evt }, ct);
    }

    /// <summary>
    /// Sets user profile properties in Mixpanel.
    /// </summary>
    /// <param name="distinctId">User identifier.</param>
    /// <param name="properties">Profile properties to set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetProfileAsync(
        string distinctId,
        Dictionary<string, object> properties,
        CancellationToken ct = default)
    {
        var profile = new
        {
            token = _projectToken,
            distinct_id = distinctId,
            @set = properties
        };

        try
        {
            var json = JsonSerializer.Serialize(new[] { profile });
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("data", base64)
            });

            using var response = await _httpClient.PostAsync("https://api.mixpanel.com/engage", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Mixpanel unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Increments a numeric property on a user profile.
    /// </summary>
    /// <param name="distinctId">User identifier.</param>
    /// <param name="property">Property name to increment.</param>
    /// <param name="value">Value to increment by (default: 1).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task IncrementProfilePropertyAsync(
        string distinctId,
        string property,
        double value = 1,
        CancellationToken ct = default)
    {
        var profile = new
        {
            token = _projectToken,
            distinct_id = distinctId,
            add = new Dictionary<string, double>
            {
                [property] = value
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(new[] { profile });
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("data", base64)
            });

            using var response = await _httpClient.PostAsync("https://api.mixpanel.com/engage", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Mixpanel unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task TrackEventsAsync(IEnumerable<object> events, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(events);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("data", base64)
            });

            using var response = await _httpClient.PostAsync("https://api.mixpanel.com/track", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Mixpanel unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var isHealthy = !string.IsNullOrEmpty(_projectToken);

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Mixpanel is configured" : "Mixpanel project token not configured",
            Data: new Dictionary<string, object>
            {
                ["distinctId"] = _distinctId,
                ["hasProjectToken"] = !string.IsNullOrEmpty(_projectToken)
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("mixpanel.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: removed decorative Task.Delay(100ms) — no real in-flight queue to drain.
        IncrementCounter("mixpanel.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
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
