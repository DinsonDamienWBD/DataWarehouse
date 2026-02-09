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

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return;

        var tokenRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = $"{_apiClientName}@{_accountName}",
            ["client_secret"] = _apiClientSecret
        };

        var content = new FormUrlEncodedContent(tokenRequest);
        var response = await _httpClient.PostAsync($"{_controllerUrl}/controller/api/oauth/access_token", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        _accessToken = result.GetProperty("access_token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(result.GetProperty("expires_in").GetInt32() - 60);

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        foreach (var metric in metrics)
        {
            var metricData = new
            {
                metricPath = $"Custom Metrics|DataWarehouse|{metric.Name.Replace(".", "|")}",
                aggregatorType = metric.Type == MetricType.Counter ? "OBSERVATION" : "AVERAGE",
                value = (long)metric.Value
            };

            var json = JsonSerializer.Serialize(new[] { metricData });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(
                $"{_controllerUrl}/controller/rest/applications/{_applicationName}/metric-data",
                content, cancellationToken);
        }
    }

    protected override async Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // AppDynamics uses its own agent for tracing, but we can report custom events
        foreach (var span in spans)
        {
            var eventData = new
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
            };

            var json = JsonSerializer.Serialize(eventData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(
                $"{_controllerUrl}/controller/rest/applications/{_applicationName}/events",
                content, cancellationToken);
        }
    }

    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken ct)
        => throw new NotSupportedException("AppDynamics does not support log ingestion - use Log Analytics");

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            await EnsureAuthenticatedAsync(ct);
            var response = await _httpClient.GetAsync($"{_controllerUrl}/controller/rest/applications", ct);
            return new HealthCheckResult(response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "AppDynamics is healthy" : "AppDynamics unhealthy",
                new Dictionary<string, object> { ["controllerUrl"] = _controllerUrl, ["application"] = _applicationName });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"AppDynamics health check failed: {ex.Message}", null); }
    }

    protected override void Dispose(bool disposing) { if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
