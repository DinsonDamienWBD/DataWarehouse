using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Health;

/// <summary>
/// Observability strategy for HashiCorp Consul health checks.
/// Provides service discovery health checks, TTL-based health updates, and health status queries.
/// </summary>
public sealed class ConsulHealthStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _consulUrl = "http://localhost:8500";
    private string _token = "";
    private string _serviceId = "datawarehouse";

    public override string StrategyId => "consul-health";
    public override string Name => "Consul Health";

    public ConsulHealthStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: true,
        SupportedExporters: new[] { "Consul", "ConsulHealth", "ServiceMesh" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string consulUrl, string token = "", string serviceId = "datawarehouse")
    {
        _consulUrl = consulUrl.TrimEnd('/');
        _token = token;
        _serviceId = serviceId;

        _httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrEmpty(_token))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-Consul-Token");
            _httpClient.DefaultRequestHeaders.Add("X-Consul-Token", _token);
        }
    }

    /// <summary>
    /// Registers a service with health check in Consul.
    /// </summary>
    public async Task RegisterServiceAsync(string serviceName, int port, string[]? tags = null,
        int ttlSeconds = 30, CancellationToken ct = default)
    {
        var service = new
        {
            ID = _serviceId,
            Name = serviceName,
            Tags = tags ?? new[] { "datawarehouse" },
            Port = port,
            Check = new
            {
                CheckID = $"{_serviceId}-ttl",
                Name = $"{serviceName} TTL Check",
                TTL = $"{ttlSeconds}s",
                Notes = "TTL-based health check for datawarehouse service"
            }
        };

        var json = JsonSerializer.Serialize(service);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync($"{_consulUrl}/v1/agent/service/register", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Deregisters a service from Consul.
    /// </summary>
    public async Task DeregisterServiceAsync(CancellationToken ct = default)
    {
        await _httpClient.PutAsync($"{_consulUrl}/v1/agent/service/deregister/{_serviceId}", null, ct);
    }

    /// <summary>
    /// Updates TTL check status to passing.
    /// </summary>
    public async Task PassTtlCheckAsync(string? note = null, CancellationToken ct = default)
    {
        var checkId = $"{_serviceId}-ttl";
        var url = $"{_consulUrl}/v1/agent/check/pass/{checkId}";
        if (!string.IsNullOrEmpty(note)) url += $"?note={Uri.EscapeDataString(note)}";
        await _httpClient.PutAsync(url, null, ct);
    }

    /// <summary>
    /// Updates TTL check status to warning.
    /// </summary>
    public async Task WarnTtlCheckAsync(string? note = null, CancellationToken ct = default)
    {
        var checkId = $"{_serviceId}-ttl";
        var url = $"{_consulUrl}/v1/agent/check/warn/{checkId}";
        if (!string.IsNullOrEmpty(note)) url += $"?note={Uri.EscapeDataString(note)}";
        await _httpClient.PutAsync(url, null, ct);
    }

    /// <summary>
    /// Updates TTL check status to failing.
    /// </summary>
    public async Task FailTtlCheckAsync(string? note = null, CancellationToken ct = default)
    {
        var checkId = $"{_serviceId}-ttl";
        var url = $"{_consulUrl}/v1/agent/check/fail/{checkId}";
        if (!string.IsNullOrEmpty(note)) url += $"?note={Uri.EscapeDataString(note)}";
        await _httpClient.PutAsync(url, null, ct);
    }

    /// <summary>
    /// Gets health status of a service across all nodes.
    /// </summary>
    public async Task<string> GetServiceHealthAsync(string serviceName, bool passingOnly = false, CancellationToken ct = default)
    {
        var url = $"{_consulUrl}/v1/health/service/{serviceName}";
        if (passingOnly) url += "?passing=true";
        using var response = await _httpClient.GetAsync(url, ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Gets all health checks in a specific state.
    /// </summary>
    public async Task<string> GetHealthChecksByStateAsync(string state = "any", CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"{_consulUrl}/v1/health/state/{state}", ct);
 response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("consul_health.metrics_sent");
        // Update TTL based on metrics - if any error metrics, warn/fail
        var hasErrors = metrics.Any(m => m.Name.Contains("error", StringComparison.OrdinalIgnoreCase) && m.Value > 0);
        var hasCritical = metrics.Any(m => m.Name.Contains("critical", StringComparison.OrdinalIgnoreCase) && m.Value > 0);

        if (hasCritical)
            await FailTtlCheckAsync("Critical metric threshold exceeded", cancellationToken);
        else if (hasErrors)
            await WarnTtlCheckAsync("Error metric detected", cancellationToken);
        else
            await PassTtlCheckAsync($"Metrics OK - {metrics.Count()} metrics reported", cancellationToken);
    }

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("Consul does not support tracing");

    protected override async Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("consul_health.logs_sent");
        var hasCritical = logEntries.Any(e => e.Level == LogLevel.Critical);
        var hasErrors = logEntries.Any(e => e.Level == LogLevel.Error);

        if (hasCritical)
            await FailTtlCheckAsync("Critical log entries detected", cancellationToken);
        else if (hasErrors)
            await WarnTtlCheckAsync("Error log entries detected", cancellationToken);
    }

    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_consulUrl}/v1/agent/self", ct);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(content);
            var nodeName = result.GetProperty("Config").GetProperty("NodeName").GetString();

            return new HealthCheckResult(response.IsSuccessStatusCode, $"Consul agent is healthy (node: {nodeName})",
                new Dictionary<string, object> { ["url"] = _consulUrl, ["serviceId"] = _serviceId, ["node"] = nodeName ?? "unknown" });
        }
        catch (Exception ex) { return new HealthCheckResult(false, $"Consul health check failed: {ex.Message}", null); }
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_consulUrl) || (!_consulUrl.StartsWith("http://") && !_consulUrl.StartsWith("https://")))
            throw new InvalidOperationException("ConsulHealthStrategy: Invalid endpoint URL configured.");
        IncrementCounter("consul_health.initialized");
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
        IncrementCounter("consul_health.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing) {
                _token = string.Empty; if (disposing) _httpClient.Dispose(); base.Dispose(disposing); }
}
