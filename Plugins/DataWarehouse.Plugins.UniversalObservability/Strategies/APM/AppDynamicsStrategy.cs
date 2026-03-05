using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.APM;

/// <summary>
/// Observability strategy for AppDynamics application performance monitoring.
/// Provides business transaction monitoring with automatic baseline and alerting.
/// </summary>
public sealed class AppDynamicsStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private readonly object _tokenLock = new();
    private string _controllerUrl = "";
    private string _accountName = "";
    private string _apiClientName = "";
    private string _apiClientSecret = "";
    private string _applicationName = "datawarehouse";
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public override string StrategyId => "appdynamics";
    public override string Name => "AppDynamics";

    public AppDynamicsStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true, SupportsTracing: true, SupportsLogging: false,
        SupportsDistributedTracing: true, SupportsAlerting: true,
        SupportedExporters: new[] { "AppDynamics", "AppDynamicsAgent", "BRUM" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string controllerUrl, string accountName, string apiClientName, string apiClientSecret, string applicationName = "datawarehouse")
    {
        _controllerUrl = controllerUrl.TrimEnd('/');
        _accountName = accountName;
        _apiClientName = apiClientName;
        _apiClientSecret = apiClientSecret;
        _applicationName = applicationName;
    }

    /// <summary>
    /// Ensures a valid OAuth access token is cached and returns it.
    /// Thread-safe: locks around token reads and writes to prevent races on DefaultRequestHeaders.
    /// Token is injected per-request rather than on DefaultRequestHeaders to avoid data races.
    /// </summary>
    private async Task<string> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        lock (_tokenLock)
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;
        }

        var tokenRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = $"{_apiClientName}@{_accountName}",
            ["client_secret"] = _apiClientSecret
        };

        var content = new FormUrlEncodedContent(tokenRequest);
        using var response = await _httpClient.PostAsync($"{_controllerUrl}/controller/api/oauth/access_token", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var newToken = result.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("AppDynamics OAuth response did not include an access_token.");
        var expiresIn = result.GetProperty("expires_in").GetInt32();

        lock (_tokenLock)
        {
            _accessToken = newToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
        }

        return newToken;
    }

    /// <summary>
    /// Creates an HttpRequestMessage with the Bearer token injected per-request (thread-safe).
    /// </summary>
    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var token = await EnsureAuthenticatedAsync(ct);
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        var metricList = metrics.Select(metric => new
        {
            metricPath = $"Custom Metrics|DataWarehouse|{metric.Name.Replace(".", "|")}",
            aggregatorType = metric.Type == MetricType.Counter ? "OBSERVATION" : "AVERAGE",
            value = (long)metric.Value
        }).ToList();

        if (metricList.Count == 0) return;

        IncrementCounter("app_dynamics.metrics_sent");

        // Batch all metrics in a single POST request rather than one per metric.
        var json = JsonSerializer.Serialize(metricList);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"{_controllerUrl}/controller/rest/applications/{_applicationName}/metric-data",
            content, cancellationToken);
        using var resp = await _httpClient.SendAsync(request, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            IncrementCounter("app_dynamics.metrics_error");
            System.Diagnostics.Trace.TraceWarning(
                "[AppDynamics] Metrics POST returned {0}: {1}", (int)resp.StatusCode, body);
        }
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        // AppDynamics uses its own agent for tracing; we report custom events via REST API.
        // Batch all spans into a single events array and post in one request.
        var eventList = spans.Select(span => new
        {
            eventType = "CUSTOM",
            summary = span.OperationName,
            severity = span.Status == SpanStatus.Error ? "ERROR" : "INFO",
            customEventDetails = new
            {
                traceId = span.TraceId,
                spanId = span.SpanId,
                parentSpanId = span.ParentSpanId,
                duration = span.Duration.TotalMilliseconds,
                attributes = span.Attributes
            }
        }).ToList();

        if (eventList.Count == 0) return;

        IncrementCounter("app_dynamics.traces_sent");

        var json = JsonSerializer.Serialize(eventList);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Post,
            $"{_controllerUrl}/controller/rest/applications/{_applicationName}/events",
            content, cancellationToken);
        using var resp = await _httpClient.SendAsync(request, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            IncrementCounter("app_dynamics.traces_error");
            System.Diagnostics.Trace.TraceWarning(
                "[AppDynamics] Traces POST returned {0}: {1}", (int)resp.StatusCode, body);
        }
    }

    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct)
        => throw new NotSupportedException("AppDynamics does not support log ingestion - use Log Analytics");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            using var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"{_controllerUrl}/controller/rest/applications",
                null, ct);
            using var response = await _httpClient.SendAsync(request, ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "AppDynamics is healthy" : "AppDynamics unhealthy",
                new Dictionary<string, object> { ["controllerUrl"] = _controllerUrl, ["application"] = _applicationName });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"AppDynamics health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_controllerUrl) || (!_controllerUrl.StartsWith("http://") && !_controllerUrl.StartsWith("https://")))
            throw new InvalidOperationException("AppDynamicsStrategy: Invalid endpoint URL configured.");
        IncrementCounter("app_dynamics.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // Finding 4584: this strategy sends metrics synchronously via HTTP; there is no
        // in-memory buffer to drain so no grace-period delay is needed.
        IncrementCounter("app_dynamics.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
