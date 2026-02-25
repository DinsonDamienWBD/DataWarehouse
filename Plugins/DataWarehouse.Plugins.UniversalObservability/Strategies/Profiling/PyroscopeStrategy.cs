using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Profiling;

/// <summary>
/// Observability strategy for Pyroscope continuous profiling platform.
/// Provides low-overhead continuous profiling for CPU, memory, and other resources.
/// </summary>
/// <remarks>
/// Pyroscope is an open-source continuous profiling platform that helps identify
/// performance bottlenecks and optimize resource usage in production applications.
/// </remarks>
public sealed class PyroscopeStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _serverUrl = "http://localhost:4040";
    private string _applicationName = "datawarehouse";

    /// <inheritdoc/>
    public override string StrategyId => "pyroscope";

    /// <inheritdoc/>
    public override string Name => "Pyroscope";

    /// <summary>
    /// Initializes a new instance of the <see cref="PyroscopeStrategy"/> class.
    /// </summary>
    public PyroscopeStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Pyroscope", "Pprof" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Pyroscope server connection.
    /// </summary>
    /// <param name="serverUrl">Pyroscope server URL.</param>
    /// <param name="applicationName">Application name for profiling data.</param>
    public void Configure(string serverUrl, string applicationName = "datawarehouse")
    {
        _serverUrl = serverUrl;
        _applicationName = applicationName;
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("pyroscope.metrics_sent");
        // Convert metrics to profiling samples
        var samples = new List<object>();

        foreach (var metric in metrics)
        {
            var sample = new
            {
                name = $"{_applicationName}.{metric.Name}",
                value = (long)metric.Value,
                labels = metric.Labels?.ToDictionary(l => l.Name, l => l.Value) ?? new Dictionary<string, string>(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            samples.Add(sample);
        }

        await UploadProfileAsync(samples, cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Pyroscope does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Pyroscope does not support logging");
    }

    /// <summary>
    /// Uploads a CPU profile to Pyroscope.
    /// </summary>
    /// <param name="profileData">Profile data in pprof format.</param>
    /// <param name="profileType">Type of profile (cpu, heap, goroutine, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UploadCpuProfileAsync(byte[] profileData, string profileType = "cpu", CancellationToken ct = default)
    {
        try
        {
            var content = new ByteArrayContent(profileData);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            var url = $"{_serverUrl}/ingest?" +
                      $"name={Uri.EscapeDataString(_applicationName)}" +
                      $"&from={DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()}" +
                      $"&until={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" +
                      $"&spyName=dotnet" +
                      $"&sampleRate=100";

            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Pyroscope unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task UploadProfileAsync(IEnumerable<object> samples, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(samples);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_serverUrl}/ingest?" +
                      $"name={Uri.EscapeDataString(_applicationName)}" +
                      $"&from={DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()}" +
                      $"&until={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            var response = await _httpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {

            // Pyroscope unavailable
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_serverUrl}/healthz", cancellationToken);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode,
                Description: response.IsSuccessStatusCode ? "Pyroscope server is healthy" : "Pyroscope server unhealthy",
                Data: new Dictionary<string, object>
                {
                    ["serverUrl"] = _serverUrl,
                    ["applicationName"] = _applicationName
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Pyroscope health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_serverUrl) || (!_serverUrl.StartsWith("http://") && !_serverUrl.StartsWith("https://")))
            throw new InvalidOperationException("PyroscopeStrategy: Invalid endpoint URL configured.");
        IncrementCounter("pyroscope.initialized");
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
        IncrementCounter("pyroscope.shutdown");
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
