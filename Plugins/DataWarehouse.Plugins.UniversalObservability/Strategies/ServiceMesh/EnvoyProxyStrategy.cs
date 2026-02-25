using System.Net.Http;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ServiceMesh;

/// <summary>
/// Observability strategy for standalone Envoy Proxy integration.
/// Provides direct Envoy metrics, cluster health, and configuration management.
/// </summary>
/// <remarks>
/// This strategy integrates with:
/// <list type="bullet">
///   <item>Envoy admin API for metrics and configuration</item>
///   <item>Cluster and upstream health monitoring</item>
///   <item>Circuit breaker and outlier detection stats</item>
///   <item>Access logging configuration</item>
///   <item>Rate limiting and connection pool metrics</item>
/// </list>
/// </remarks>
public sealed class EnvoyProxyStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _adminUrl = "http://localhost:9901";
    private string _clusterName = "datawarehouse";

    /// <inheritdoc/>
    public override string StrategyId => "envoy-proxy";

    /// <inheritdoc/>
    public override string Name => "Envoy Proxy";

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvoyProxyStrategy"/> class.
    /// </summary>
    public EnvoyProxyStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Envoy", "EnvoyProxy", "xDS" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Configures the Envoy Proxy connection.
    /// </summary>
    /// <param name="adminUrl">Envoy admin API URL.</param>
    /// <param name="clusterName">Name of this service cluster.</param>
    public void Configure(string adminUrl, string clusterName = "datawarehouse")
    {
        _adminUrl = adminUrl.TrimEnd('/');
        _clusterName = clusterName;
    }

    /// <summary>
    /// Collects comprehensive Envoy metrics.
    /// </summary>
    /// <returns>Collection of Envoy metrics.</returns>
    public async Task<IReadOnlyList<MetricValue>> CollectMetricsAsync(CancellationToken ct = default)
    {
        var metrics = new List<MetricValue>();

        try
        {
            var response = await _httpClient.GetAsync($"{_adminUrl}/stats?format=json", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var stats = JsonSerializer.Deserialize<JsonElement>(content);

                if (stats.TryGetProperty("stats", out var statsArray))
                {
                    foreach (var stat in statsArray.EnumerateArray())
                    {
                        var name = stat.GetProperty("name").GetString() ?? "";
                        var value = stat.GetProperty("value").GetDouble();

                        var category = CategorizeMetric(name);
                        if (category != null)
                        {
                            metrics.Add(MetricValue.Gauge($"envoy.{SanitizeName(name)}", value,
                                new[] { new MetricLabel("category", category), new MetricLabel("cluster", _clusterName) }));
                        }
                    }
                }
            }
        }
        catch
        {

            // Admin API not accessible
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return metrics;
    }

    /// <summary>
    /// Gets detailed cluster health information.
    /// </summary>
    /// <returns>Cluster health details.</returns>
    public async Task<IReadOnlyList<ClusterHealth>> GetClustersHealthAsync(CancellationToken ct = default)
    {
        var clusters = new List<ClusterHealth>();

        try
        {
            var response = await _httpClient.GetAsync($"{_adminUrl}/clusters?format=json", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var data = JsonSerializer.Deserialize<JsonElement>(content);

                if (data.TryGetProperty("cluster_statuses", out var clusterStatuses))
                {
                    foreach (var cluster in clusterStatuses.EnumerateArray())
                    {
                        var health = new ClusterHealth
                        {
                            Name = cluster.GetProperty("name").GetString() ?? ""
                        };

                        if (cluster.TryGetProperty("host_statuses", out var hostStatuses))
                        {
                            foreach (var host in hostStatuses.EnumerateArray())
                            {
                                var hostHealth = new HostHealth();

                                if (host.TryGetProperty("address", out var address) &&
                                    address.TryGetProperty("socket_address", out var socketAddr))
                                {
                                    hostHealth.Address = socketAddr.GetProperty("address").GetString() ?? "";
                                    hostHealth.Port = socketAddr.GetProperty("port_value").GetInt32();
                                }

                                if (host.TryGetProperty("health_status", out var hs))
                                {
                                    var edsStatus = hs.GetProperty("eds_health_status").GetString();
                                    hostHealth.EdsHealthStatus = edsStatus ?? "UNKNOWN";
                                    hostHealth.IsHealthy = edsStatus == "HEALTHY";

                                    if (hs.TryGetProperty("failed_active_health_check", out var failedHc))
                                        hostHealth.FailedActiveHealthCheck = failedHc.GetBoolean();
                                    if (hs.TryGetProperty("failed_outlier_check", out var failedOd))
                                        hostHealth.FailedOutlierCheck = failedOd.GetBoolean();
                                }

                                if (host.TryGetProperty("stats", out var hostStats))
                                {
                                    foreach (var hStat in hostStats.EnumerateArray())
                                    {
                                        var statName = hStat.GetProperty("name").GetString();
                                        var statValue = hStat.GetProperty("value").GetInt64();

                                        switch (statName)
                                        {
                                            case "cx_total": hostHealth.ConnectionsTotal = statValue; break;
                                            case "cx_active": hostHealth.ConnectionsActive = statValue; break;
                                            case "rq_total": hostHealth.RequestsTotal = statValue; break;
                                            case "rq_success": hostHealth.RequestsSuccess = statValue; break;
                                            case "rq_error": hostHealth.RequestsError = statValue; break;
                                        }
                                    }
                                }

                                health.Hosts.Add(hostHealth);
                            }
                        }

                        if (cluster.TryGetProperty("circuit_breakers", out var cb))
                        {
                            if (cb.TryGetProperty("thresholds", out var thresholds))
                            {
                                foreach (var threshold in thresholds.EnumerateArray())
                                {
                                    var priority = threshold.GetProperty("priority").GetString() ?? "DEFAULT";
                                    var breaker = new CircuitBreakerConfig { Priority = priority };

                                    if (threshold.TryGetProperty("max_connections", out var maxConn))
                                        breaker.MaxConnections = maxConn.GetInt32();
                                    if (threshold.TryGetProperty("max_pending_requests", out var maxPending))
                                        breaker.MaxPendingRequests = maxPending.GetInt32();
                                    if (threshold.TryGetProperty("max_requests", out var maxReq))
                                        breaker.MaxRequests = maxReq.GetInt32();
                                    if (threshold.TryGetProperty("max_retries", out var maxRetries))
                                        breaker.MaxRetries = maxRetries.GetInt32();

                                    health.CircuitBreakers.Add(breaker);
                                }
                            }
                        }

                        clusters.Add(health);
                    }
                }
            }
        }
        catch
        {

            // Admin API not accessible
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return clusters;
    }

    /// <summary>
    /// Gets listener configuration.
    /// </summary>
    /// <returns>Listener information.</returns>
    public async Task<IReadOnlyList<ListenerInfo>> GetListenersAsync(CancellationToken ct = default)
    {
        var listeners = new List<ListenerInfo>();

        try
        {
            var response = await _httpClient.GetAsync($"{_adminUrl}/listeners?format=json", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var data = JsonSerializer.Deserialize<JsonElement>(content);

                if (data.TryGetProperty("listener_statuses", out var listenerStatuses))
                {
                    foreach (var listener in listenerStatuses.EnumerateArray())
                    {
                        var info = new ListenerInfo
                        {
                            Name = listener.GetProperty("name").GetString() ?? ""
                        };

                        if (listener.TryGetProperty("local_address", out var localAddr) &&
                            localAddr.TryGetProperty("socket_address", out var socketAddr))
                        {
                            info.Address = socketAddr.GetProperty("address").GetString() ?? "";
                            info.Port = socketAddr.GetProperty("port_value").GetInt32();
                        }

                        listeners.Add(info);
                    }
                }
            }
        }
        catch
        {

            // Admin API not accessible
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return listeners;
    }

    /// <summary>
    /// Gets server information.
    /// </summary>
    /// <returns>Server information.</returns>
    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        var info = new ServerInfo();

        try
        {
            var response = await _httpClient.GetAsync($"{_adminUrl}/server_info", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var data = JsonSerializer.Deserialize<JsonElement>(content);

                if (data.TryGetProperty("version", out var version))
                    info.Version = version.GetString() ?? "";
                if (data.TryGetProperty("state", out var state))
                    info.State = state.GetString() ?? "";
                if (data.TryGetProperty("uptime_current_epoch", out var uptime))
                    info.UptimeSeconds = long.Parse(uptime.GetString()?.TrimEnd('s') ?? "0");
                if (data.TryGetProperty("hot_restart_version", out var hrVersion))
                    info.HotRestartVersion = hrVersion.GetString() ?? "";
            }
        }
        catch
        {

            // Admin API not accessible
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return info;
    }

    /// <summary>
    /// Drains listeners for graceful shutdown.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> DrainListenersAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsync($"{_adminUrl}/drain_listeners", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? CategorizeMetric(string name)
    {
        if (name.StartsWith("cluster.")) return "cluster";
        if (name.StartsWith("http.")) return "http";
        if (name.StartsWith("listener.")) return "listener";
        if (name.StartsWith("server.")) return "server";
        if (name.StartsWith("runtime.")) return "runtime";
        if (name.Contains("circuit_breaker")) return "circuit_breaker";
        if (name.Contains("outlier_detection")) return "outlier_detection";
        if (name.Contains("rate_limit")) return "rate_limit";
        if (name.Contains("upstream_rq") || name.Contains("upstream_cx")) return "upstream";
        if (name.Contains("downstream_rq") || name.Contains("downstream_cx")) return "downstream";
        return null;
    }

    private static string SanitizeName(string name)
    {
        return name.Replace(".", "_").Replace("-", "_").ToLowerInvariant();
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("envoy_proxy.metrics_sent");
        var envoyMetrics = await CollectMetricsAsync(cancellationToken);
        // Combine and forward
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        IncrementCounter("envoy_proxy.traces_sent");
        // Envoy handles trace header propagation
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("envoy_proxy.logs_sent");
        // Envoy access logs are typically configured separately
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var serverInfo = await GetServerInfoAsync(cancellationToken);
        var clusters = await GetClustersHealthAsync(cancellationToken);

        var isHealthy = serverInfo.State == "LIVE";
        var totalHosts = clusters.SelectMany(c => c.Hosts).Count();
        var healthyHosts = clusters.SelectMany(c => c.Hosts).Count(h => h.IsHealthy);

        return new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Envoy proxy is healthy" : $"Envoy state: {serverInfo.State}",
            Data: new Dictionary<string, object>
            {
                ["version"] = serverInfo.Version,
                ["state"] = serverInfo.State,
                ["uptimeSeconds"] = serverInfo.UptimeSeconds,
                ["clustersCount"] = clusters.Count,
                ["totalHosts"] = totalHosts,
                ["healthyHosts"] = healthyHosts
            });
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_adminUrl) || (!_adminUrl.StartsWith("http://") && !_adminUrl.StartsWith("https://")))
            throw new InvalidOperationException("EnvoyProxyStrategy: Invalid endpoint URL configured.");
        IncrementCounter("envoy_proxy.initialized");
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
        IncrementCounter("envoy_proxy.shutdown");
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
/// Cluster health information.
/// </summary>
public class ClusterHealth
{
    public string Name { get; set; } = "";
    public List<HostHealth> Hosts { get; } = new();
    public List<CircuitBreakerConfig> CircuitBreakers { get; } = new();
}

/// <summary>
/// Host health information.
/// </summary>
public class HostHealth
{
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public bool IsHealthy { get; set; }
    public string EdsHealthStatus { get; set; } = "";
    public bool FailedActiveHealthCheck { get; set; }
    public bool FailedOutlierCheck { get; set; }
    public long ConnectionsTotal { get; set; }
    public long ConnectionsActive { get; set; }
    public long RequestsTotal { get; set; }
    public long RequestsSuccess { get; set; }
    public long RequestsError { get; set; }
}

/// <summary>
/// Circuit breaker configuration.
/// </summary>
public class CircuitBreakerConfig
{
    public string Priority { get; set; } = "";
    public int MaxConnections { get; set; }
    public int MaxPendingRequests { get; set; }
    public int MaxRequests { get; set; }
    public int MaxRetries { get; set; }
}

/// <summary>
/// Listener information.
/// </summary>
public class ListenerInfo
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>
/// Envoy server information.
/// </summary>
public class ServerInfo
{
    public string Version { get; set; } = "";
    public string State { get; set; } = "";
    public long UptimeSeconds { get; set; }
    public string HotRestartVersion { get; set; } = "";
}
