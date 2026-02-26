using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Health;

/// <summary>
/// Observability strategy for Icinga monitoring platform.
/// Provides comprehensive infrastructure and application monitoring with flexible alerting.
/// </summary>
/// <remarks>
/// Icinga is an open-source monitoring system that checks the availability of network resources,
/// notifies users of outages, and generates performance data for reporting.
/// </remarks>
public sealed class IcingaStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiUrl = "https://localhost:5665";
    private string _username = "root";
    private string _password = "";
    private bool _verifySsl = true;

    /// <inheritdoc/>
    public override string StrategyId => "icinga";

    /// <inheritdoc/>
    public override string Name => "Icinga";

    /// <summary>
    /// Initializes a new instance of the <see cref="IcingaStrategy"/> class.
    /// </summary>
    public IcingaStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Icinga", "Graphite", "InfluxDB" }))
    {
        var handler = new HttpClientHandler();
        // SECURITY: TLS certificate validation is enabled by default.
        // Only bypass when explicitly configured to false.
        if (!_verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Icinga API connection.
    /// </summary>
    /// <param name="apiUrl">Icinga API URL.</param>
    /// <param name="username">API username.</param>
    /// <param name="password">API password.</param>
    /// <param name="verifySsl">Whether to verify SSL certificates. Default is true.</param>
    public void Configure(string apiUrl, string username, string password, bool verifySsl = true)
    {
        _apiUrl = apiUrl;
        _username = username;
        _password = password;
        _verifySsl = verifySsl;

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("icinga.metrics_sent");
        // Submit passive check results for metrics
        foreach (var metric in metrics)
        {
            var exitStatus = DetermineExitStatus(metric);
            var pluginOutput = $"{metric.Name}: {metric.Value}";

            var perfData = $"{SanitizeName(metric.Name)}={metric.Value}";

            var checkResult = new
            {
                type = "Service",
                filter = $"service.name==\"{SanitizeName(metric.Name)}\" && host.name==\"datawarehouse\"",
                exit_status = exitStatus,
                plugin_output = pluginOutput,
                performance_data = new[] { perfData },
                check_source = "DataWarehouse"
            };

            await SubmitCheckResultAsync(checkResult, cancellationToken);
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Icinga does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Icinga does not support direct logging - use check results instead");
    }

    /// <summary>
    /// Submits a custom check result to Icinga.
    /// </summary>
    /// <param name="serviceName">Service name.</param>
    /// <param name="exitStatus">Exit status (0=OK, 1=WARNING, 2=CRITICAL, 3=UNKNOWN).</param>
    /// <param name="pluginOutput">Check output message.</param>
    /// <param name="performanceData">Optional performance data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SubmitServiceCheckAsync(
        string serviceName,
        int exitStatus,
        string pluginOutput,
        string[]? performanceData = null,
        CancellationToken ct = default)
    {
        var checkResult = new
        {
            type = "Service",
            filter = $"service.name==\"{serviceName}\" && host.name==\"datawarehouse\"",
            exit_status = exitStatus,
            plugin_output = pluginOutput,
            performance_data = performanceData ?? Array.Empty<string>(),
            check_source = "DataWarehouse"
        };

        await SubmitCheckResultAsync(checkResult, ct);
    }

    private async Task SubmitCheckResultAsync(object checkResult, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(checkResult);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{_apiUrl}/v1/actions/process-check-result";

            using var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Icinga API unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int DetermineExitStatus(MetricValue metric)
    {
        // Simple threshold logic (could be configurable)
        return metric.Value switch
        {
            > 90 => 2,    // CRITICAL
            > 75 => 1,    // WARNING
            _ => 0        // OK
        };
    }

    private static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("-", "_").Replace(".", "_").ToLowerInvariant();
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Query Icinga status
            using var response = await _httpClient.GetAsync($"{_apiUrl}/v1/status", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var status = JsonSerializer.Deserialize<Dictionary<string, object>>(content);

                return new HealthCheckResult(
                    IsHealthy: true,
                    Description: "Icinga API is healthy",
                    Data: new Dictionary<string, object>
                    {
                        ["apiUrl"] = _apiUrl,
                        ["username"] = _username,
                        ["status"] = status ?? new Dictionary<string, object>()
                    });
            }

            return new HealthCheckResult(
                IsHealthy: false,
                Description: "Icinga API unhealthy",
                Data: null);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Icinga health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiUrl) || (!_apiUrl.StartsWith("http://") && !_apiUrl.StartsWith("https://")))
            throw new InvalidOperationException("IcingaStrategy: Invalid endpoint URL configured.");
        IncrementCounter("icinga.initialized");
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
        IncrementCounter("icinga.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
                _password = string.Empty;
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
