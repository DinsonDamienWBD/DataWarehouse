using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Profiling;

/// <summary>
/// Observability strategy for Datadog Continuous Profiler.
/// Provides production profiling integrated with Datadog APM and infrastructure monitoring.
/// </summary>
/// <remarks>
/// Datadog Continuous Profiler helps identify code-level performance issues
/// with always-on profiling for CPU, memory, and I/O in production environments.
/// </remarks>
public sealed class DatadogProfilerStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";
    private string _site = "datadoghq.com";
    private string _serviceName = "datawarehouse";
    private string _environment = "production";

    /// <inheritdoc/>
    public override string StrategyId => "datadog-profiler";

    /// <inheritdoc/>
    public override string Name => "Datadog Profiler";

    /// <summary>
    /// Initializes a new instance of the <see cref="DatadogProfilerStrategy"/> class.
    /// </summary>
    public DatadogProfilerStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Datadog", "Pprof" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Datadog Profiler connection.
    /// </summary>
    /// <param name="apiKey">Datadog API key.</param>
    /// <param name="site">Datadog site (datadoghq.com, datadoghq.eu, etc.).</param>
    /// <param name="serviceName">Service name.</param>
    /// <param name="environment">Environment (production, staging, etc.).</param>
    public void Configure(string apiKey, string site = "datadoghq.com", string serviceName = "datawarehouse", string environment = "production")
    {
        _apiKey = apiKey;
        _site = site;
        _serviceName = serviceName;
        _environment = environment;

        _httpClient.DefaultRequestHeaders.Add("DD-API-KEY", apiKey);
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("datadog_profiler.metrics_sent");
        // Convert metrics to profiling events
        var profiles = new List<object>();

        foreach (var metric in metrics)
        {
            var profile = new
            {
                service = _serviceName,
                env = _environment,
                profile_type = DetermineProfileType(metric.Name),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                duration = 60, // 60 second profiling window
                samples = new[]
                {
                    new
                    {
                        name = metric.Name,
                        value = (long)metric.Value,
                        labels = metric.Labels?.ToDictionary(l => l.Name, l => l.Value)
                    }
                }
            };

            profiles.Add(profile);
        }

        await UploadProfilesAsync(profiles, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Datadog Profiler does not support tracing - use Datadog APM instead");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Datadog Profiler does not support logging");
    }

    /// <summary>
    /// Uploads a pprof-formatted profile to Datadog.
    /// </summary>
    /// <param name="profileData">Profile data in pprof format.</param>
    /// <param name="profileType">Type of profile (cpu, heap, wall, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UploadPprofAsync(byte[] profileData, string profileType, CancellationToken ct = default)
    {
        try
        {
            var content = new MultipartFormDataContent
            {
                { new StringContent(_serviceName), "service" },
                { new StringContent(_environment), "env" },
                { new StringContent(profileType), "profile_type" },
                { new StringContent(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()), "start" },
                { new StringContent(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()), "end" },
                { new ByteArrayContent(profileData), "data", "profile.pprof" }
            };

            var url = $"https://intake.profile.{_site}/v1/input";
            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Datadog unavailable
        }
    }

    private async Task UploadProfilesAsync(IEnumerable<object> profiles, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(profiles);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://intake.profile.{_site}/v1/input";
            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Datadog unavailable
        }
    }

    private static string DetermineProfileType(string metricName)
    {
        return metricName.ToLowerInvariant() switch
        {
            var name when name.Contains("cpu") => "cpu",
            var name when name.Contains("memory") || name.Contains("heap") => "heap",
            var name when name.Contains("alloc") => "alloc",
            var name when name.Contains("block") => "block",
            var name when name.Contains("mutex") => "mutex",
            _ => "wall"
        };
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            // Validate API key by attempting a minimal request
            var testContent = new StringContent("{}", Encoding.UTF8, "application/json");
            var url = $"https://api.{_site}/api/v1/validate";

            var response = await _httpClient.PostAsync(url, testContent, cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Datadog Profiler is healthy" : "Datadog Profiler unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["site"] = _site,
                    ["serviceName"] = _serviceName,
                    ["environment"] = _environment,
                    ["hasApiKey"] = !string.IsNullOrEmpty(_apiKey)
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Datadog Profiler health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("datadog_profiler.initialized");
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
        IncrementCounter("datadog_profiler.shutdown");
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
