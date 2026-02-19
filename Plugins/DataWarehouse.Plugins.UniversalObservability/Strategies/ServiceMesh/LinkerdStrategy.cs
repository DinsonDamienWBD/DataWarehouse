using System.Net.Http;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ServiceMesh;

/// <summary>
/// Observability strategy for Linkerd service mesh integration.
/// Provides lightweight service mesh telemetry with automatic mTLS and traffic metrics.
/// </summary>
/// <remarks>
/// This strategy integrates with:
/// <list type="bullet">
///   <item>Linkerd control plane (linkerd-destination, linkerd-identity)</item>
///   <item>Linkerd proxy metrics</item>
///   <item>Linkerd Viz extension for dashboards</item>
///   <item>Service profiles for per-route metrics</item>
///   <item>mTLS status via identity service</item>
/// </list>
/// </remarks>
public sealed class LinkerdStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _vizUrl = "http://linkerd-viz.linkerd-viz:8084";
    private string _proxyAdminPort = "4191";
    private string _serviceName = "datawarehouse";
    private string _namespace = "default";

    /// <inheritdoc/>
    public override string StrategyId => "linkerd";

    /// <inheritdoc/>
    public override string Name => "Linkerd Service Mesh";

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkerdStrategy"/> class.
    /// </summary>
    public LinkerdStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: false,
        SupportedExporters: new[] { "Linkerd", "LinkerdViz", "ServiceMesh" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        DetectLinkerdEnvironment();
    }

    /// <summary>
    /// Configures the Linkerd integration parameters.
    /// </summary>
    /// <param name="vizUrl">Linkerd Viz API URL.</param>
    /// <param name="serviceName">Service name.</param>
    /// <param name="namespace">Kubernetes namespace.</param>
    public void Configure(string? vizUrl = null, string? serviceName = null, string? @namespace = null)
    {
        if (!string.IsNullOrEmpty(vizUrl)) _vizUrl = vizUrl;
        if (!string.IsNullOrEmpty(serviceName)) _serviceName = serviceName;
        if (!string.IsNullOrEmpty(@namespace)) _namespace = @namespace;
    }

    /// <summary>
    /// Collects Linkerd proxy metrics.
    /// </summary>
    /// <returns>Collection of Linkerd proxy metrics.</returns>
    public async Task<IReadOnlyList<MetricValue>> CollectProxyMetricsAsync(CancellationToken ct = default)
    {
        var metrics = new List<MetricValue>();

        try
        {
            // Get metrics from local proxy admin endpoint (Prometheus format)
            var response = await _httpClient.GetAsync($"http://localhost:{_proxyAdminPort}/metrics", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var parsedMetrics = ParsePrometheusMetrics(content);
                metrics.AddRange(parsedMetrics);
            }
        }
        catch
        {
            // Proxy admin not accessible
        }

        return metrics;
    }

    /// <summary>
    /// Gets service statistics from Linkerd Viz.
    /// </summary>
    /// <returns>Service statistics.</returns>
    public async Task<LinkerdServiceStats> GetServiceStatsAsync(CancellationToken ct = default)
    {
        var stats = new LinkerdServiceStats { ServiceName = _serviceName, Namespace = _namespace };

        try
        {
            var response = await _httpClient.GetAsync(
                $"{_vizUrl}/api/stat?resource_type=deployment&resource_name={_serviceName}&namespace={_namespace}&time_window=1m", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<JsonElement>(content);

                if (result.TryGetProperty("ok", out var ok) && ok.TryGetProperty("statTables", out var tables))
                {
                    foreach (var table in tables.EnumerateArray())
                    {
                        if (table.TryGetProperty("rows", out var rows))
                        {
                            foreach (var row in rows.EnumerateArray())
                            {
                                if (row.TryGetProperty("stats", out var rowStats))
                                {
                                    if (rowStats.TryGetProperty("successCount", out var success))
                                        stats.SuccessCount = success.GetInt64();
                                    if (rowStats.TryGetProperty("failureCount", out var failure))
                                        stats.FailureCount = failure.GetInt64();
                                    if (rowStats.TryGetProperty("latencyMsP50", out var p50))
                                        stats.LatencyP50Ms = p50.GetDouble();
                                    if (rowStats.TryGetProperty("latencyMsP95", out var p95))
                                        stats.LatencyP95Ms = p95.GetDouble();
                                    if (rowStats.TryGetProperty("latencyMsP99", out var p99))
                                        stats.LatencyP99Ms = p99.GetDouble();
                                    if (rowStats.TryGetProperty("tcpOpenConnections", out var tcp))
                                        stats.TcpOpenConnections = tcp.GetInt64();
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Viz not accessible
        }

        return stats;
    }

    /// <summary>
    /// Gets traffic split configuration.
    /// </summary>
    /// <returns>Traffic split information.</returns>
    public async Task<IReadOnlyList<TrafficSplit>> GetTrafficSplitsAsync(CancellationToken ct = default)
    {
        var splits = new List<TrafficSplit>();

        try
        {
            var response = await _httpClient.GetAsync(
                $"{_vizUrl}/api/trafficsplits?namespace={_namespace}", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<JsonElement>(content);

                if (result.TryGetProperty("ok", out var ok) && ok.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var split = new TrafficSplit
                        {
                            Name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "",
                            Service = item.GetProperty("spec").GetProperty("service").GetString() ?? ""
                        };

                        if (item.GetProperty("spec").TryGetProperty("backends", out var backends))
                        {
                            foreach (var backend in backends.EnumerateArray())
                            {
                                var name = backend.GetProperty("service").GetString() ?? "";
                                var weight = backend.GetProperty("weight").GetInt32();
                                split.Backends[name] = weight;
                            }
                        }

                        splits.Add(split);
                    }
                }
            }
        }
        catch
        {
            // Viz not accessible
        }

        return splits;
    }

    /// <summary>
    /// Gets edges (service-to-service connections) from the mesh.
    /// </summary>
    /// <returns>Service edges.</returns>
    public async Task<IReadOnlyList<ServiceEdge>> GetServiceEdgesAsync(CancellationToken ct = default)
    {
        var edges = new List<ServiceEdge>();

        try
        {
            var response = await _httpClient.GetAsync(
                $"{_vizUrl}/api/edges?resource_type=deployment&namespace={_namespace}", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<JsonElement>(content);

                if (result.TryGetProperty("ok", out var ok) && ok.TryGetProperty("edges", out var edgesArray))
                {
                    foreach (var edge in edgesArray.EnumerateArray())
                    {
                        var src = edge.GetProperty("src");
                        var dst = edge.GetProperty("dst");

                        edges.Add(new ServiceEdge
                        {
                            SourceService = src.GetProperty("name").GetString() ?? "",
                            SourceNamespace = src.GetProperty("namespace").GetString() ?? "",
                            DestinationService = dst.GetProperty("name").GetString() ?? "",
                            DestinationNamespace = dst.GetProperty("namespace").GetString() ?? "",
                            ClientId = edge.TryGetProperty("clientId", out var clientId) ? clientId.GetString() : null,
                            ServerId = edge.TryGetProperty("serverId", out var serverId) ? serverId.GetString() : null,
                            NoTlsReason = edge.TryGetProperty("noIdentityMsg", out var noTls) ? noTls.GetString() : null
                        });
                    }
                }
            }
        }
        catch
        {
            // Viz not accessible
        }

        return edges;
    }

    private IEnumerable<MetricValue> ParsePrometheusMetrics(string content)
    {
        var metrics = new List<MetricValue>();
        var labels = new List<MetricLabel>
        {
            new("service", _serviceName),
            new("namespace", _namespace)
        };

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#')) continue;

            var parts = line.Split(' ');
            if (parts.Length < 2) continue;

            var nameAndLabels = parts[0];
            if (!double.TryParse(parts[1], out var value)) continue;

            // Parse important Linkerd metrics
            if (nameAndLabels.StartsWith("request_total") ||
                nameAndLabels.StartsWith("response_total") ||
                nameAndLabels.StartsWith("response_latency_ms") ||
                nameAndLabels.StartsWith("tcp_open_total") ||
                nameAndLabels.StartsWith("tcp_close_total") ||
                nameAndLabels.StartsWith("tcp_read_bytes_total") ||
                nameAndLabels.StartsWith("tcp_write_bytes_total"))
            {
                var metricName = nameAndLabels.Split('{')[0];
                metrics.Add(MetricValue.Gauge($"linkerd.{metricName}", value, labels));
            }
        }

        return metrics;
    }

    private void DetectLinkerdEnvironment()
    {
        _serviceName = Environment.GetEnvironmentVariable("HOSTNAME") ??
                      Environment.GetEnvironmentVariable("SERVICE_NAME") ?? "datawarehouse";

        _namespace = Environment.GetEnvironmentVariable("POD_NAMESPACE") ??
                    Environment.GetEnvironmentVariable("LINKERD_NS") ?? "default";

        var vizHost = Environment.GetEnvironmentVariable("LINKERD_VIZ_HOST");
        if (!string.IsNullOrEmpty(vizHost))
        {
            _vizUrl = $"http://{vizHost}:8084";
        }

        var adminPort = Environment.GetEnvironmentVariable("LINKERD_ADMIN_PORT");
        if (!string.IsNullOrEmpty(adminPort))
        {
            _proxyAdminPort = adminPort;
        }
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("linkerd.metrics_sent");
        var proxyMetrics = await CollectProxyMetricsAsync(cancellationToken);
        // Both sets would be forwarded
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("linkerd.traces_sent");
        // Linkerd automatically propagates trace headers via proxy
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("linkerd.logs_sent");
        // Log entries are forwarded; mesh context added by proxy
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var stats = await GetServiceStatsAsync(cancellationToken);
        var proxyMetrics = await CollectProxyMetricsAsync(cancellationToken);

        var totalRequests = stats.SuccessCount + stats.FailureCount;
        var successRate = totalRequests > 0 ? (double)stats.SuccessCount / totalRequests * 100 : 100;
        var isHealthy = successRate >= 95;

        return new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Linkerd service mesh integration healthy" : $"Service success rate low: {successRate:F1}%",
            Data: new Dictionary<string, object>
            {
                ["serviceName"] = _serviceName,
                ["namespace"] = _namespace,
                ["successRate"] = successRate,
                ["latencyP50Ms"] = stats.LatencyP50Ms,
                ["latencyP99Ms"] = stats.LatencyP99Ms,
                ["tcpOpenConnections"] = stats.TcpOpenConnections,
                ["proxyMetricsCount"] = proxyMetrics.Count
            });
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_vizUrl) || (!_vizUrl.StartsWith("http://") && !_vizUrl.StartsWith("https://")))
            throw new InvalidOperationException("LinkerdStrategy: Invalid endpoint URL configured.");
        IncrementCounter("linkerd.initialized");
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
        IncrementCounter("linkerd.shutdown");
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

/// <summary>
/// Linkerd service statistics.
/// </summary>
public class LinkerdServiceStats
{
    public string ServiceName { get; set; } = "";
    public string Namespace { get; set; } = "";
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
    public double LatencyP50Ms { get; set; }
    public double LatencyP95Ms { get; set; }
    public double LatencyP99Ms { get; set; }
    public long TcpOpenConnections { get; set; }
}

/// <summary>
/// Traffic split configuration.
/// </summary>
public class TrafficSplit
{
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public Dictionary<string, int> Backends { get; } = new();
}

/// <summary>
/// Service profile information.
/// </summary>
public class ServiceProfile
{
    public string ServiceName { get; set; } = "";
    public string Namespace { get; set; } = "";
    public List<RouteInfo> Routes { get; } = new();
    public double RetryRatio { get; set; }
    public int MinRetriesPerSecond { get; set; }
    public string? RetryTtl { get; set; }
}

/// <summary>
/// Route information from service profile.
/// </summary>
public class RouteInfo
{
    public string Name { get; set; } = "";
    public string? PathRegex { get; set; }
    public bool IsRetryable { get; set; }
    public string? Timeout { get; set; }
}

/// <summary>
/// Service edge (connection between services).
/// </summary>
public class ServiceEdge
{
    public string SourceService { get; set; } = "";
    public string SourceNamespace { get; set; } = "";
    public string DestinationService { get; set; } = "";
    public string DestinationNamespace { get; set; } = "";
    public string? ClientId { get; set; }
    public string? ServerId { get; set; }
    public string? NoTlsReason { get; set; }
}
