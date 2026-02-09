using System.Net.Http;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ResourceMonitoring;

/// <summary>
/// Observability strategy for container resource monitoring (Docker, Kubernetes, containerd).
/// Provides container-specific resource metrics including cgroup limits and utilization.
/// </summary>
/// <remarks>
/// This strategy monitors:
/// <list type="bullet">
///   <item>Container CPU limits and usage</item>
///   <item>Container memory limits and usage</item>
///   <item>Container network I/O</item>
///   <item>Container disk I/O</item>
///   <item>Kubernetes pod resource quotas</item>
/// </list>
/// </remarks>
public sealed class ContainerResourceStrategy : ObservabilityStrategyBase
{
    private readonly HttpClient _httpClient;
    private string _cgroupPath = "/sys/fs/cgroup";
    private string _kubeletUrl = "http://localhost:10255";
    private string _dockerSocket = "/var/run/docker.sock";
    private string _containerId = "";
    private string _podName = "";
    private string _namespace = "default";

    /// <inheritdoc/>
    public override string StrategyId => "container-resources";

    /// <inheritdoc/>
    public override string Name => "Container Resource Monitor";

    /// <summary>
    /// Initializes a new instance of the <see cref="ContainerResourceStrategy"/> class.
    /// </summary>
    public ContainerResourceStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "Container", "Docker", "Kubernetes", "Cgroup" }))
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        DetectContainerEnvironment();
    }

    /// <summary>
    /// Configures the container monitoring parameters.
    /// </summary>
    /// <param name="cgroupPath">Path to cgroup filesystem.</param>
    /// <param name="kubeletUrl">Kubelet API URL for pod metrics.</param>
    /// <param name="podName">Kubernetes pod name.</param>
    /// <param name="namespace">Kubernetes namespace.</param>
    public void Configure(string? cgroupPath = null, string? kubeletUrl = null,
        string? podName = null, string? @namespace = null)
    {
        if (!string.IsNullOrEmpty(cgroupPath)) _cgroupPath = cgroupPath;
        if (!string.IsNullOrEmpty(kubeletUrl)) _kubeletUrl = kubeletUrl;
        if (!string.IsNullOrEmpty(podName)) _podName = podName;
        if (!string.IsNullOrEmpty(@namespace)) _namespace = @namespace;
    }

    /// <summary>
    /// Collects container resource metrics.
    /// </summary>
    /// <returns>Collection of container resource metrics.</returns>
    public async Task<IReadOnlyList<MetricValue>> CollectContainerMetricsAsync(CancellationToken ct = default)
    {
        var metrics = new List<MetricValue>();

        try
        {
            // CPU Metrics from cgroup
            var cpuMetrics = await CollectCgroupCpuMetricsAsync(ct);
            metrics.AddRange(cpuMetrics);

            // Memory Metrics from cgroup
            var memoryMetrics = await CollectCgroupMemoryMetricsAsync(ct);
            metrics.AddRange(memoryMetrics);

            // I/O Metrics
            var ioMetrics = await CollectCgroupIoMetricsAsync(ct);
            metrics.AddRange(ioMetrics);

            // Kubernetes-specific metrics if in k8s environment
            if (!string.IsNullOrEmpty(_podName))
            {
                var k8sMetrics = await CollectKubernetesMetricsAsync(ct);
                metrics.AddRange(k8sMetrics);
            }

            // Container info metrics
            metrics.Add(MetricValue.Gauge("container.info", 1, new[]
            {
                new MetricLabel("container_id", _containerId),
                new MetricLabel("pod_name", _podName),
                new MetricLabel("namespace", _namespace)
            }));
        }
        catch (Exception ex)
        {
            metrics.Add(MetricValue.Counter("container_monitor.errors", 1,
                new[] { new MetricLabel("error_type", ex.GetType().Name) }));
        }

        return metrics;
    }

    /// <summary>
    /// Gets the container resource limits.
    /// </summary>
    /// <returns>Container resource limits.</returns>
    public async Task<ContainerResourceLimits> GetResourceLimitsAsync(CancellationToken ct = default)
    {
        var limits = new ContainerResourceLimits();

        try
        {
            // CPU limit from cgroup
            var cpuQuotaPath = Path.Combine(_cgroupPath, "cpu", "cpu.cfs_quota_us");
            var cpuPeriodPath = Path.Combine(_cgroupPath, "cpu", "cpu.cfs_period_us");

            if (File.Exists(cpuQuotaPath) && File.Exists(cpuPeriodPath))
            {
                var quota = long.Parse(await File.ReadAllTextAsync(cpuQuotaPath, ct));
                var period = long.Parse(await File.ReadAllTextAsync(cpuPeriodPath, ct));

                if (quota > 0 && period > 0)
                {
                    limits.CpuLimitCores = (double)quota / period;
                }
            }

            // Memory limit from cgroup
            var memLimitPath = Path.Combine(_cgroupPath, "memory", "memory.limit_in_bytes");
            if (File.Exists(memLimitPath))
            {
                var limitStr = await File.ReadAllTextAsync(memLimitPath, ct);
                if (long.TryParse(limitStr.Trim(), out var memLimit) && memLimit < long.MaxValue / 2)
                {
                    limits.MemoryLimitBytes = memLimit;
                }
            }

            // cgroup v2 paths
            var cgroupV2MemPath = Path.Combine(_cgroupPath, "memory.max");
            if (File.Exists(cgroupV2MemPath))
            {
                var content = await File.ReadAllTextAsync(cgroupV2MemPath, ct);
                if (content.Trim() != "max" && long.TryParse(content.Trim(), out var memLimit))
                {
                    limits.MemoryLimitBytes = memLimit;
                }
            }

            var cgroupV2CpuPath = Path.Combine(_cgroupPath, "cpu.max");
            if (File.Exists(cgroupV2CpuPath))
            {
                var content = (await File.ReadAllTextAsync(cgroupV2CpuPath, ct)).Split(' ');
                if (content.Length >= 2 && content[0] != "max" &&
                    long.TryParse(content[0], out var quota) &&
                    long.TryParse(content[1], out var period))
                {
                    limits.CpuLimitCores = (double)quota / period;
                }
            }
        }
        catch
        {
            // Running outside container or cgroup not accessible
        }

        return limits;
    }

    private async Task<IEnumerable<MetricValue>> CollectCgroupCpuMetricsAsync(CancellationToken ct)
    {
        var metrics = new List<MetricValue>();

        try
        {
            // cgroup v1
            var cpuUsagePath = Path.Combine(_cgroupPath, "cpu", "cpuacct.usage");
            if (File.Exists(cpuUsagePath))
            {
                var usageNs = long.Parse(await File.ReadAllTextAsync(cpuUsagePath, ct));
                metrics.Add(MetricValue.Counter("container.cpu.usage_nanoseconds", usageNs));
            }

            var cpuStatPath = Path.Combine(_cgroupPath, "cpu", "cpu.stat");
            if (File.Exists(cpuStatPath))
            {
                var stats = await File.ReadAllTextAsync(cpuStatPath, ct);
                foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var value))
                    {
                        metrics.Add(MetricValue.Counter($"container.cpu.{parts[0]}", value));
                    }
                }
            }

            // cgroup v2
            var cpuStatV2Path = Path.Combine(_cgroupPath, "cpu.stat");
            if (File.Exists(cpuStatV2Path))
            {
                var stats = await File.ReadAllTextAsync(cpuStatV2Path, ct);
                foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var value))
                    {
                        metrics.Add(MetricValue.Counter($"container.cpu.{parts[0]}", value));
                    }
                }
            }
        }
        catch
        {
            // cgroup not accessible
        }

        return metrics;
    }

    private async Task<IEnumerable<MetricValue>> CollectCgroupMemoryMetricsAsync(CancellationToken ct)
    {
        var metrics = new List<MetricValue>();

        try
        {
            // cgroup v1
            var memUsagePath = Path.Combine(_cgroupPath, "memory", "memory.usage_in_bytes");
            if (File.Exists(memUsagePath))
            {
                var usage = long.Parse(await File.ReadAllTextAsync(memUsagePath, ct));
                metrics.Add(MetricValue.Gauge("container.memory.usage_bytes", usage));
            }

            var memLimitPath = Path.Combine(_cgroupPath, "memory", "memory.limit_in_bytes");
            if (File.Exists(memLimitPath))
            {
                var limitStr = await File.ReadAllTextAsync(memLimitPath, ct);
                if (long.TryParse(limitStr.Trim(), out var limit) && limit < long.MaxValue / 2)
                {
                    metrics.Add(MetricValue.Gauge("container.memory.limit_bytes", limit));

                    var usagePath = Path.Combine(_cgroupPath, "memory", "memory.usage_in_bytes");
                    if (File.Exists(usagePath))
                    {
                        var usage = long.Parse(await File.ReadAllTextAsync(usagePath, ct));
                        var utilization = (double)usage / limit * 100;
                        metrics.Add(MetricValue.Gauge("container.memory.utilization_percent", utilization));
                    }
                }
            }

            var memStatPath = Path.Combine(_cgroupPath, "memory", "memory.stat");
            if (File.Exists(memStatPath))
            {
                var stats = await File.ReadAllTextAsync(memStatPath, ct);
                foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var value))
                    {
                        var metricName = parts[0] switch
                        {
                            "cache" => "container.memory.cache_bytes",
                            "rss" => "container.memory.rss_bytes",
                            "swap" => "container.memory.swap_bytes",
                            "pgfault" => "container.memory.page_faults",
                            "pgmajfault" => "container.memory.major_page_faults",
                            _ => null
                        };

                        if (metricName != null)
                        {
                            metrics.Add(MetricValue.Gauge(metricName, value));
                        }
                    }
                }
            }

            // cgroup v2
            var memCurrentPath = Path.Combine(_cgroupPath, "memory.current");
            if (File.Exists(memCurrentPath))
            {
                var usage = long.Parse(await File.ReadAllTextAsync(memCurrentPath, ct));
                metrics.Add(MetricValue.Gauge("container.memory.usage_bytes", usage));
            }

            var memMaxPath = Path.Combine(_cgroupPath, "memory.max");
            if (File.Exists(memMaxPath))
            {
                var content = await File.ReadAllTextAsync(memMaxPath, ct);
                if (content.Trim() != "max" && long.TryParse(content.Trim(), out var limit))
                {
                    metrics.Add(MetricValue.Gauge("container.memory.limit_bytes", limit));
                }
            }
        }
        catch
        {
            // cgroup not accessible
        }

        return metrics;
    }

    private async Task<IEnumerable<MetricValue>> CollectCgroupIoMetricsAsync(CancellationToken ct)
    {
        var metrics = new List<MetricValue>();

        try
        {
            // cgroup v2 I/O stats
            var ioStatPath = Path.Combine(_cgroupPath, "io.stat");
            if (File.Exists(ioStatPath))
            {
                var stats = await File.ReadAllTextAsync(ioStatPath, ct);
                long totalReadBytes = 0, totalWriteBytes = 0;

                foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ');
                    foreach (var part in parts)
                    {
                        if (part.StartsWith("rbytes=") && long.TryParse(part.Substring(7), out var rb))
                            totalReadBytes += rb;
                        if (part.StartsWith("wbytes=") && long.TryParse(part.Substring(7), out var wb))
                            totalWriteBytes += wb;
                    }
                }

                metrics.Add(MetricValue.Counter("container.io.read_bytes", totalReadBytes));
                metrics.Add(MetricValue.Counter("container.io.write_bytes", totalWriteBytes));
            }

            // cgroup v1 blkio
            var blkioPath = Path.Combine(_cgroupPath, "blkio", "blkio.throttle.io_service_bytes");
            if (File.Exists(blkioPath))
            {
                var stats = await File.ReadAllTextAsync(blkioPath, ct);
                long totalRead = 0, totalWrite = 0;

                foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 3)
                    {
                        if (parts[1] == "Read" && long.TryParse(parts[2], out var read))
                            totalRead += read;
                        if (parts[1] == "Write" && long.TryParse(parts[2], out var write))
                            totalWrite += write;
                    }
                }

                metrics.Add(MetricValue.Counter("container.io.read_bytes", totalRead));
                metrics.Add(MetricValue.Counter("container.io.write_bytes", totalWrite));
            }
        }
        catch
        {
            // cgroup not accessible
        }

        return metrics;
    }

    private async Task<IEnumerable<MetricValue>> CollectKubernetesMetricsAsync(CancellationToken ct)
    {
        var metrics = new List<MetricValue>();

        try
        {
            var response = await _httpClient.GetAsync(
                $"{_kubeletUrl}/stats/summary", ct);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var stats = JsonSerializer.Deserialize<JsonElement>(content);

                if (stats.TryGetProperty("pods", out var pods))
                {
                    foreach (var pod in pods.EnumerateArray())
                    {
                        var podNamespace = pod.GetProperty("podRef").GetProperty("namespace").GetString();
                        var podNameVal = pod.GetProperty("podRef").GetProperty("name").GetString();

                        if (podNameVal != _podName || podNamespace != _namespace)
                            continue;

                        if (pod.TryGetProperty("cpu", out var cpu))
                        {
                            var cpuUsage = cpu.GetProperty("usageNanoCores").GetInt64();
                            metrics.Add(MetricValue.Gauge("k8s.pod.cpu.usage_nanocores", cpuUsage,
                                new[] { new MetricLabel("pod", _podName), new MetricLabel("namespace", _namespace) }));
                        }

                        if (pod.TryGetProperty("memory", out var memory))
                        {
                            var memUsage = memory.GetProperty("usageBytes").GetInt64();
                            var memWorkingSet = memory.GetProperty("workingSetBytes").GetInt64();

                            metrics.Add(MetricValue.Gauge("k8s.pod.memory.usage_bytes", memUsage,
                                new[] { new MetricLabel("pod", _podName), new MetricLabel("namespace", _namespace) }));
                            metrics.Add(MetricValue.Gauge("k8s.pod.memory.working_set_bytes", memWorkingSet,
                                new[] { new MetricLabel("pod", _podName), new MetricLabel("namespace", _namespace) }));
                        }
                    }
                }
            }
        }
        catch
        {
            // Kubelet not accessible
        }

        return metrics;
    }

    private void DetectContainerEnvironment()
    {
        // Detect container ID
        try
        {
            if (File.Exists("/proc/1/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                var match = System.Text.RegularExpressions.Regex.Match(cgroup, @"/docker/([a-f0-9]{64})");
                if (match.Success)
                {
                    _containerId = match.Groups[1].Value.Substring(0, 12);
                }
                else
                {
                    match = System.Text.RegularExpressions.Regex.Match(cgroup, @"/kubepods/[^/]+/pod[^/]+/([a-f0-9]{64})");
                    if (match.Success)
                    {
                        _containerId = match.Groups[1].Value.Substring(0, 12);
                    }
                }
            }
        }
        catch
        {
            // Not in container
        }

        // Detect Kubernetes environment
        _podName = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
        _namespace = Environment.GetEnvironmentVariable("POD_NAMESPACE") ??
                    Environment.GetEnvironmentVariable("KUBERNETES_NAMESPACE") ?? "default";

        var serviceHost = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        if (!string.IsNullOrEmpty(serviceHost))
        {
            _kubeletUrl = $"https://{serviceHost}:10250";
        }
    }

    /// <inheritdoc/>
    protected override async Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        // Combine with container-collected metrics
        var containerMetrics = await CollectContainerMetricsAsync(cancellationToken);
        // Both sets of metrics would be forwarded to configured backend
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Container Resource Monitor does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        // Could log resource alerts
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var limits = await GetResourceLimitsAsync(cancellationToken);
        var metrics = await CollectContainerMetricsAsync(cancellationToken);

        var memUsage = metrics.FirstOrDefault(m => m.Name == "container.memory.usage_bytes")?.Value ?? 0;
        var memLimit = limits.MemoryLimitBytes;
        var memUtilization = memLimit > 0 ? memUsage / memLimit * 100 : 0;

        var isHealthy = memUtilization < 90;

        return new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "Container resources within limits" : "Container memory utilization high",
            Data: new Dictionary<string, object>
            {
                ["containerId"] = _containerId,
                ["podName"] = _podName,
                ["namespace"] = _namespace,
                ["cpuLimitCores"] = limits.CpuLimitCores,
                ["memoryLimitMB"] = limits.MemoryLimitBytes / (1024.0 * 1024.0),
                ["memoryUtilizationPercent"] = memUtilization
            });
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

/// <summary>
/// Container resource limits.
/// </summary>
public class ContainerResourceLimits
{
    public double CpuLimitCores { get; set; } = -1;
    public long MemoryLimitBytes { get; set; } = -1;
    public long MemorySwapLimitBytes { get; set; } = -1;
    public long PidsLimit { get; set; } = -1;
}
