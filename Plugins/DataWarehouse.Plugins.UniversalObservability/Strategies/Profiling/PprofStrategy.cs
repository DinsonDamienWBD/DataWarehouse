using System.Net.Http;
using System.Text;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Profiling;

/// <summary>
/// Observability strategy for pprof profiling format.
/// Provides CPU, memory, and goroutine profiling compatible with Go pprof tools.
/// </summary>
/// <remarks>
/// pprof is a profiling format originally from Go that's widely supported
/// across languages and tools for performance analysis and optimization.
/// </remarks>
public sealed class PprofStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _endpoint = "http://localhost:6060/debug/pprof";
    private string _outputDirectory = "./profiles";

    /// <inheritdoc/>
    public override string StrategyId => "pprof";

    /// <inheritdoc/>
    public override string Name => "Pprof";

    /// <summary>
    /// Initializes a new instance of the <see cref="PprofStrategy"/> class.
    /// </summary>
    public PprofStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Pprof", "File" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>
    /// Configures the pprof endpoint and output.
    /// </summary>
    /// <param name="endpoint">Pprof HTTP endpoint.</param>
    /// <param name="outputDirectory">Directory for saving profile files.</param>
    public void Configure(string endpoint, string outputDirectory = "./profiles")
    {
        _endpoint = endpoint;
        _outputDirectory = outputDirectory;

        if (!Directory.Exists(_outputDirectory))
        {
            Directory.CreateDirectory(_outputDirectory);
        }
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        // Trigger profile collection based on metric thresholds
        foreach (var metric in metrics)
        {
            if (ShouldTriggerProfile(metric))
            {
                await CollectProfileAsync("heap", cancellationToken);
            }
        }
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Pprof does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Pprof does not support logging");
    }

    /// <summary>
    /// Collects a CPU profile.
    /// </summary>
    /// <param name="durationSeconds">Duration to collect profile (default 30 seconds).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]?> CollectCpuProfileAsync(int durationSeconds = 30, CancellationToken ct = default)
    {
        return await CollectProfileAsync($"profile?seconds={durationSeconds}", ct);
    }

    /// <summary>
    /// Collects a heap profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]?> CollectHeapProfileAsync(CancellationToken ct = default)
    {
        return await CollectProfileAsync("heap", ct);
    }

    /// <summary>
    /// Collects a goroutine profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]?> CollectGoroutineProfileAsync(CancellationToken ct = default)
    {
        return await CollectProfileAsync("goroutine", ct);
    }

    /// <summary>
    /// Collects an allocation profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]?> CollectAllocProfileAsync(CancellationToken ct = default)
    {
        return await CollectProfileAsync("allocs", ct);
    }

    /// <summary>
    /// Collects a block profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]?> CollectBlockProfileAsync(CancellationToken ct = default)
    {
        return await CollectProfileAsync("block", ct);
    }

    /// <summary>
    /// Collects a mutex profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]?> CollectMutexProfileAsync(CancellationToken ct = default)
    {
        return await CollectProfileAsync("mutex", ct);
    }

    private async Task<byte[]?> CollectProfileAsync(string profileType, CancellationToken ct)
    {
        try
        {
            var url = $"{_endpoint}/{profileType}";
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var profileData = await response.Content.ReadAsByteArrayAsync(ct);

            // Save to file
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            var filename = Path.Combine(_outputDirectory, $"{profileType}_{timestamp}.pb.gz");
            await File.WriteAllBytesAsync(filename, profileData, ct);

            return profileData;
        }
        catch (HttpRequestException)
        {
            // Pprof endpoint unavailable
            return null;
        }
    }

    private static bool ShouldTriggerProfile(MetricValue metric)
    {
        // Trigger profiling on high resource usage
        return metric.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase) && metric.Value > 80 ||
               metric.Name.Contains("memory", StringComparison.OrdinalIgnoreCase) && metric.Value > 85;
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_endpoint}/", cancellationToken);

            var directoryExists = Directory.Exists(_outputDirectory);

            return new HealthCheckResult(
                IsHealthy: response.IsSuccessStatusCode && directoryExists,
                Description: response.IsSuccessStatusCode && directoryExists
                    ? "Pprof endpoint is healthy"
                    : "Pprof endpoint unhealthy or output directory missing",
                Data: new Dictionary<string, object>
                {
                    ["endpoint"] = _endpoint,
                    ["outputDirectory"] = _outputDirectory,
                    ["directoryExists"] = directoryExists
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                IsHealthy: false,
                Description: $"Pprof health check failed: {ex.Message}",
                Data: null);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
