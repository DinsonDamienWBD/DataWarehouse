using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for monitoring system health and metrics.
/// </summary>
public interface ISystemHealthService
{
    /// <summary>
    /// Gets current system health status.
    /// </summary>
    SystemHealth GetCurrentHealth();

    /// <summary>
    /// Gets historical metrics.
    /// </summary>
    IEnumerable<MetricSnapshot> GetMetricHistory(string metricName, TimeSpan duration);

    /// <summary>
    /// Gets all current metrics.
    /// </summary>
    Dictionary<string, object> GetAllMetrics();

    /// <summary>
    /// Event raised when health status changes.
    /// </summary>
    event EventHandler<HealthChangedEventArgs>? HealthChanged;

    /// <summary>
    /// Event raised when new metrics are recorded.
    /// </summary>
    event EventHandler<MetricSnapshot>? MetricRecorded;
}

/// <summary>
/// Current system health status.
/// </summary>
public class SystemHealth
{
    public HealthStatus Status { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double CpuUsagePercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public double MemoryUsagePercent => MemoryTotalBytes > 0 ? (double)MemoryUsedBytes / MemoryTotalBytes * 100 : 0;
    public long DiskUsedBytes { get; set; }
    public long DiskTotalBytes { get; set; }
    public double DiskUsagePercent => DiskTotalBytes > 0 ? (double)DiskUsedBytes / DiskTotalBytes * 100 : 0;
    public int ActiveConnections { get; set; }
    public int ActivePlugins { get; set; }
    public int HealthyPlugins { get; set; }
    public int UnhealthyPlugins { get; set; }
    public long RequestsPerMinute { get; set; }
    public double AverageLatencyMs { get; set; }
    public long ErrorsPerMinute { get; set; }
    public TimeSpan Uptime { get; set; }
    public List<HealthIssue> Issues { get; set; } = new();
}

public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
    Unknown
}

public class HealthIssue
{
    public string Component { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public IssueSeverity Severity { get; set; }
    public DateTime DetectedAt { get; set; }
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public class MetricSnapshot
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

public class HealthChangedEventArgs : EventArgs
{
    public HealthStatus PreviousStatus { get; set; }
    public HealthStatus NewStatus { get; set; }
    public List<HealthIssue> Issues { get; set; } = new();
}

/// <summary>
/// Implementation of system health monitoring.
/// </summary>
public class SystemHealthService : ISystemHealthService
{
    private readonly ILogger<SystemHealthService> _logger;
    private readonly IPluginDiscoveryService _pluginService;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<MetricSnapshot>> _metricHistory = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private HealthStatus _lastStatus = HealthStatus.Unknown;
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private const int MaxHistoryPerMetric = 1000;

    public event EventHandler<HealthChangedEventArgs>? HealthChanged;
    public event EventHandler<MetricSnapshot>? MetricRecorded;

    public SystemHealthService(ILogger<SystemHealthService> logger, IPluginDiscoveryService pluginService)
    {
        _logger = logger;
        _pluginService = pluginService;
    }

    public SystemHealth GetCurrentHealth()
    {
        var health = new SystemHealth
        {
            Timestamp = DateTime.UtcNow,
            Uptime = DateTime.UtcNow - _startTime
        };

        try
        {
            // CPU and Memory
            _currentProcess.Refresh();
            health.MemoryUsedBytes = _currentProcess.WorkingSet64;
            health.MemoryTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            health.CpuUsagePercent = GetCpuUsage();

            // Disk (root drive)
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
            if (drives.Any())
            {
                health.DiskTotalBytes = drives.Sum(d => d.TotalSize);
                health.DiskUsedBytes = drives.Sum(d => d.TotalSize - d.AvailableFreeSpace);
            }

            // Plugin health
            var plugins = _pluginService.GetAllPlugins().ToList();
            health.ActivePlugins = plugins.Count(p => p.IsActive);
            health.HealthyPlugins = plugins.Count(p => p.IsHealthy);
            health.UnhealthyPlugins = plugins.Count(p => !p.IsHealthy);

            // Determine overall status
            health.Status = DetermineStatus(health);

            // Check for issues
            health.Issues = DetectIssues(health);

            // Notify if status changed
            if (health.Status != _lastStatus)
            {
                var args = new HealthChangedEventArgs
                {
                    PreviousStatus = _lastStatus,
                    NewStatus = health.Status,
                    Issues = health.Issues
                };
                _lastStatus = health.Status;
                HealthChanged?.Invoke(this, args);
            }

            // Record metrics
            RecordMetric("cpu_usage", health.CpuUsagePercent);
            RecordMetric("memory_usage", health.MemoryUsagePercent);
            RecordMetric("disk_usage", health.DiskUsagePercent);
            RecordMetric("active_plugins", health.ActivePlugins);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting health metrics");
            health.Status = HealthStatus.Unknown;
        }

        return health;
    }

    public IEnumerable<MetricSnapshot> GetMetricHistory(string metricName, TimeSpan duration)
    {
        if (!_metricHistory.TryGetValue(metricName, out var queue))
            return Enumerable.Empty<MetricSnapshot>();

        var cutoff = DateTime.UtcNow - duration;
        return queue.Where(m => m.Timestamp >= cutoff).OrderBy(m => m.Timestamp);
    }

    public Dictionary<string, object> GetAllMetrics()
    {
        var metrics = new Dictionary<string, object>();

        foreach (var (name, queue) in _metricHistory)
        {
            if (queue.TryPeek(out var latest))
            {
                metrics[name] = latest.Value;
            }
        }

        return metrics;
    }

    private void RecordMetric(string name, double value, Dictionary<string, string>? tags = null)
    {
        var snapshot = new MetricSnapshot
        {
            Name = name,
            Value = value,
            Timestamp = DateTime.UtcNow,
            Tags = tags ?? new()
        };

        var queue = _metricHistory.GetOrAdd(name, _ => new ConcurrentQueue<MetricSnapshot>());
        queue.Enqueue(snapshot);

        // Trim old entries
        while (queue.Count > MaxHistoryPerMetric && queue.TryDequeue(out _)) { }

        MetricRecorded?.Invoke(this, snapshot);
    }

    private static HealthStatus DetermineStatus(SystemHealth health)
    {
        if (health.UnhealthyPlugins > 0 || health.CpuUsagePercent > 90 || health.MemoryUsagePercent > 95)
            return HealthStatus.Unhealthy;

        if (health.CpuUsagePercent > 80 || health.MemoryUsagePercent > 85 || health.DiskUsagePercent > 90)
            return HealthStatus.Degraded;

        return HealthStatus.Healthy;
    }

    private static List<HealthIssue> DetectIssues(SystemHealth health)
    {
        var issues = new List<HealthIssue>();

        if (health.CpuUsagePercent > 90)
        {
            issues.Add(new HealthIssue
            {
                Component = "CPU",
                Message = $"CPU usage is critically high ({health.CpuUsagePercent:F1}%)",
                Severity = IssueSeverity.Critical,
                DetectedAt = DateTime.UtcNow
            });
        }
        else if (health.CpuUsagePercent > 80)
        {
            issues.Add(new HealthIssue
            {
                Component = "CPU",
                Message = $"CPU usage is high ({health.CpuUsagePercent:F1}%)",
                Severity = IssueSeverity.Warning,
                DetectedAt = DateTime.UtcNow
            });
        }

        if (health.MemoryUsagePercent > 95)
        {
            issues.Add(new HealthIssue
            {
                Component = "Memory",
                Message = $"Memory usage is critically high ({health.MemoryUsagePercent:F1}%)",
                Severity = IssueSeverity.Critical,
                DetectedAt = DateTime.UtcNow
            });
        }
        else if (health.MemoryUsagePercent > 85)
        {
            issues.Add(new HealthIssue
            {
                Component = "Memory",
                Message = $"Memory usage is high ({health.MemoryUsagePercent:F1}%)",
                Severity = IssueSeverity.Warning,
                DetectedAt = DateTime.UtcNow
            });
        }

        if (health.DiskUsagePercent > 95)
        {
            issues.Add(new HealthIssue
            {
                Component = "Disk",
                Message = $"Disk space critically low ({100 - health.DiskUsagePercent:F1}% free)",
                Severity = IssueSeverity.Critical,
                DetectedAt = DateTime.UtcNow
            });
        }

        if (health.UnhealthyPlugins > 0)
        {
            issues.Add(new HealthIssue
            {
                Component = "Plugins",
                Message = $"{health.UnhealthyPlugins} plugin(s) are unhealthy",
                Severity = IssueSeverity.Error,
                DetectedAt = DateTime.UtcNow
            });
        }

        return issues;
    }

    private double GetCpuUsage()
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = _currentProcess.TotalProcessorTime;

            Thread.Sleep(100); // Brief sample

            _currentProcess.Refresh();
            var endTime = DateTime.UtcNow;
            var endCpuUsage = _currentProcess.TotalProcessorTime;

            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;

            return cpuUsedMs / (Environment.ProcessorCount * totalMsPassed) * 100;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Background service for periodic health monitoring.
/// </summary>
public class HealthMonitorService : BackgroundService
{
    private readonly ISystemHealthService _healthService;
    private readonly ILogger<HealthMonitorService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    public HealthMonitorService(ISystemHealthService healthService, ILogger<HealthMonitorService> logger)
    {
        _healthService = healthService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _healthService.GetCurrentHealth();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health monitor");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
