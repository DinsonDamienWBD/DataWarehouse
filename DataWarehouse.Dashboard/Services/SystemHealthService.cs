using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for monitoring system health and metrics.
/// </summary>
public interface ISystemHealthService
{
    /// <summary>
    /// Gets current system health status asynchronously.
    /// </summary>
    Task<SystemHealthStatus> GetSystemHealthAsync();

    /// <summary>
    /// Gets current system metrics.
    /// </summary>
    SystemMetrics GetCurrentMetrics();

    /// <summary>
    /// Gets historical metrics.
    /// </summary>
    IEnumerable<SystemMetrics> GetMetricsHistory(TimeSpan duration, TimeSpan resolution);

    /// <summary>
    /// Gets system alerts.
    /// </summary>
    IEnumerable<SystemAlert> GetAlerts(bool activeOnly = true);

    /// <summary>
    /// Acknowledges an alert.
    /// </summary>
    Task<bool> AcknowledgeAlertAsync(string alertId, string acknowledgedBy);

    /// <summary>
    /// Clears an alert.
    /// </summary>
    Task<bool> ClearAlertAsync(string alertId);

    /// <summary>
    /// Event raised when health status changes.
    /// </summary>
    event EventHandler<HealthChangedEventArgs>? HealthChanged;

    /// <summary>
    /// Event raised when new metrics are recorded.
    /// </summary>
    event EventHandler<SystemMetrics>? MetricRecorded;
}

/// <summary>
/// System health status.
/// </summary>
public class SystemHealthStatus
{
    public HealthStatus OverallStatus { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<ComponentHealth> Components { get; set; } = new();
    public string? Message { get; set; }
}

/// <summary>
/// Individual component health.
/// </summary>
public class ComponentHealth
{
    public string Name { get; set; } = string.Empty;
    public HealthStatus Status { get; set; }
    public string? Message { get; set; }
    public TimeSpan? ResponseTime { get; set; }
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Health status enum.
/// </summary>
public enum HealthStatus
{
    Healthy,
    Degraded,
    Critical,
    Unknown
}

/// <summary>
/// System metrics snapshot.
/// </summary>
public class SystemMetrics
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double CpuUsagePercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public double MemoryUsagePercent => MemoryTotalBytes > 0 ? (double)MemoryUsedBytes / MemoryTotalBytes * 100 : 0;
    public long DiskUsedBytes { get; set; }
    public long DiskTotalBytes { get; set; }
    public double DiskUsagePercent => DiskTotalBytes > 0 ? (double)DiskUsedBytes / DiskTotalBytes * 100 : 0;
    public int ActiveConnections { get; set; }
    public double RequestsPerSecond { get; set; }
    public int ThreadCount { get; set; }
    public long UptimeSeconds { get; set; }
    public long ErrorCount { get; set; }
    public double AverageLatencyMs { get; set; }
}

/// <summary>
/// System alert.
/// </summary>
public class SystemAlert
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsAcknowledged { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// Alert severity levels.
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public class HealthChangedEventArgs : EventArgs
{
    public HealthStatus PreviousStatus { get; set; }
    public HealthStatus NewStatus { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Implementation of system health monitoring.
/// </summary>
public class SystemHealthService : ISystemHealthService
{
    private readonly ILogger<SystemHealthService> _logger;
    private readonly IPluginDiscoveryService _pluginService;
    private readonly ConcurrentQueue<SystemMetrics> _metricsHistory = new();
    private readonly BoundedDictionary<string, SystemAlert> _alerts = new BoundedDictionary<string, SystemAlert>(1000);
    private readonly DateTime _startTime = DateTime.UtcNow;
    private HealthStatus _lastStatus = HealthStatus.Unknown;
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private const int MaxMetricsHistory = 1440; // 24 hours at 1-minute intervals

    public event EventHandler<HealthChangedEventArgs>? HealthChanged;
    public event EventHandler<SystemMetrics>? MetricRecorded;

    public SystemHealthService(ILogger<SystemHealthService> logger, IPluginDiscoveryService pluginService)
    {
        _logger = logger;
        _pluginService = pluginService;
    }

    public async Task<SystemHealthStatus> GetSystemHealthAsync()
    {
        var status = new SystemHealthStatus
        {
            Timestamp = DateTime.UtcNow,
            Components = new List<ComponentHealth>()
        };

        try
        {
            // Check kernel component
            status.Components.Add(new ComponentHealth
            {
                Name = "Kernel",
                Status = HealthStatus.Healthy,
                Message = "Running",
                LastChecked = DateTime.UtcNow
            });

            // Check plugins
            var plugins = _pluginService.GetAllPlugins().ToList();
            var activePlugins = plugins.Count(p => p.IsActive);
            var healthyPlugins = plugins.Count(p => p.IsHealthy);
            var pluginStatus = activePlugins == healthyPlugins ? HealthStatus.Healthy :
                               healthyPlugins > 0 ? HealthStatus.Degraded : HealthStatus.Critical;

            status.Components.Add(new ComponentHealth
            {
                Name = "Plugins",
                Status = pluginStatus,
                Message = $"{healthyPlugins}/{activePlugins} healthy",
                LastChecked = DateTime.UtcNow
            });

            // Check memory
            var metrics = GetCurrentMetrics();
            var memoryStatus = metrics.MemoryUsagePercent < 80 ? HealthStatus.Healthy :
                               metrics.MemoryUsagePercent < 95 ? HealthStatus.Degraded : HealthStatus.Critical;

            status.Components.Add(new ComponentHealth
            {
                Name = "Memory",
                Status = memoryStatus,
                Message = $"{metrics.MemoryUsagePercent:F1}% used",
                LastChecked = DateTime.UtcNow
            });

            // Check disk
            var diskStatus = metrics.DiskUsagePercent < 80 ? HealthStatus.Healthy :
                             metrics.DiskUsagePercent < 95 ? HealthStatus.Degraded : HealthStatus.Critical;

            status.Components.Add(new ComponentHealth
            {
                Name = "Disk",
                Status = diskStatus,
                Message = $"{metrics.DiskUsagePercent:F1}% used",
                LastChecked = DateTime.UtcNow
            });

            // Check CPU
            var cpuStatus = metrics.CpuUsagePercent < 80 ? HealthStatus.Healthy :
                            metrics.CpuUsagePercent < 95 ? HealthStatus.Degraded : HealthStatus.Critical;

            status.Components.Add(new ComponentHealth
            {
                Name = "CPU",
                Status = cpuStatus,
                Message = $"{metrics.CpuUsagePercent:F1}% used",
                LastChecked = DateTime.UtcNow
            });

            // Determine overall status
            if (status.Components.Any(c => c.Status == HealthStatus.Critical))
                status.OverallStatus = HealthStatus.Critical;
            else if (status.Components.Any(c => c.Status == HealthStatus.Degraded))
                status.OverallStatus = HealthStatus.Degraded;
            else
                status.OverallStatus = HealthStatus.Healthy;

            // Generate alerts for issues
            await GenerateAlertsAsync(status, metrics);

            // Notify if status changed
            if (status.OverallStatus != _lastStatus)
            {
                HealthChanged?.Invoke(this, new HealthChangedEventArgs
                {
                    PreviousStatus = _lastStatus,
                    NewStatus = status.OverallStatus
                });
                _lastStatus = status.OverallStatus;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking system health");
            status.OverallStatus = HealthStatus.Unknown;
            status.Message = "Error checking health: " + ex.Message;
        }

        return status;
    }

    public SystemMetrics GetCurrentMetrics()
    {
        var metrics = new SystemMetrics
        {
            Timestamp = DateTime.UtcNow,
            UptimeSeconds = (long)(DateTime.UtcNow - _startTime).TotalSeconds,
            ThreadCount = Process.GetCurrentProcess().Threads.Count
        };

        try
        {
            _currentProcess.Refresh();
            metrics.MemoryUsedBytes = _currentProcess.WorkingSet64;
            metrics.MemoryTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            metrics.CpuUsagePercent = GetCpuUsage();

            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
            if (drives.Any())
            {
                metrics.DiskTotalBytes = drives.Sum(d => d.TotalSize);
                metrics.DiskUsedBytes = drives.Sum(d => d.TotalSize - d.AvailableFreeSpace);
            }

            // Store in history
            _metricsHistory.Enqueue(metrics);
            while (_metricsHistory.Count > MaxMetricsHistory && _metricsHistory.TryDequeue(out _)) { }

            MetricRecorded?.Invoke(this, metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting metrics");
        }

        return metrics;
    }

    public IEnumerable<SystemMetrics> GetMetricsHistory(TimeSpan duration, TimeSpan resolution)
    {
        var cutoff = DateTime.UtcNow - duration;
        var metrics = _metricsHistory.Where(m => m.Timestamp >= cutoff).OrderBy(m => m.Timestamp).ToList();

        if (resolution.TotalSeconds < 60 || metrics.Count < 2)
            return metrics;

        // Downsample to resolution
        var result = new List<SystemMetrics>();
        var bucketStart = metrics.First().Timestamp;

        while (bucketStart < DateTime.UtcNow)
        {
            var bucketEnd = bucketStart + resolution;
            var bucketMetrics = metrics.Where(m => m.Timestamp >= bucketStart && m.Timestamp < bucketEnd).ToList();

            if (bucketMetrics.Any())
            {
                result.Add(new SystemMetrics
                {
                    Timestamp = bucketStart,
                    CpuUsagePercent = bucketMetrics.Average(m => m.CpuUsagePercent),
                    MemoryUsedBytes = (long)bucketMetrics.Average(m => m.MemoryUsedBytes),
                    MemoryTotalBytes = bucketMetrics.First().MemoryTotalBytes,
                    DiskUsedBytes = (long)bucketMetrics.Average(m => m.DiskUsedBytes),
                    DiskTotalBytes = bucketMetrics.First().DiskTotalBytes,
                    ActiveConnections = (int)bucketMetrics.Average(m => m.ActiveConnections),
                    RequestsPerSecond = bucketMetrics.Average(m => m.RequestsPerSecond),
                    ThreadCount = (int)bucketMetrics.Average(m => m.ThreadCount),
                    UptimeSeconds = bucketMetrics.Last().UptimeSeconds
                });
            }

            bucketStart = bucketEnd;
        }

        return result;
    }

    public IEnumerable<SystemAlert> GetAlerts(bool activeOnly = true)
    {
        var alerts = _alerts.Values.OrderByDescending(a => a.Timestamp);
        return activeOnly ? alerts.Where(a => !a.IsAcknowledged) : alerts;
    }

    public Task<bool> AcknowledgeAlertAsync(string alertId, string acknowledgedBy)
    {
        if (_alerts.TryGetValue(alertId, out var alert))
        {
            alert.IsAcknowledged = true;
            alert.AcknowledgedBy = acknowledgedBy;
            alert.AcknowledgedAt = DateTime.UtcNow;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> ClearAlertAsync(string alertId)
    {
        return Task.FromResult(_alerts.TryRemove(alertId, out _));
    }

    private Task GenerateAlertsAsync(SystemHealthStatus status, SystemMetrics metrics)
    {
        // CPU alert
        if (metrics.CpuUsagePercent > 90)
        {
            AddOrUpdateAlert("cpu_critical", "CPU Critical", $"CPU usage at {metrics.CpuUsagePercent:F1}%", AlertSeverity.Critical);
        }
        else if (metrics.CpuUsagePercent > 80)
        {
            AddOrUpdateAlert("cpu_warning", "CPU Warning", $"CPU usage at {metrics.CpuUsagePercent:F1}%", AlertSeverity.Warning);
        }

        // Memory alert
        if (metrics.MemoryUsagePercent > 95)
        {
            AddOrUpdateAlert("memory_critical", "Memory Critical", $"Memory usage at {metrics.MemoryUsagePercent:F1}%", AlertSeverity.Critical);
        }
        else if (metrics.MemoryUsagePercent > 85)
        {
            AddOrUpdateAlert("memory_warning", "Memory Warning", $"Memory usage at {metrics.MemoryUsagePercent:F1}%", AlertSeverity.Warning);
        }

        // Disk alert
        if (metrics.DiskUsagePercent > 95)
        {
            AddOrUpdateAlert("disk_critical", "Disk Critical", $"Disk usage at {metrics.DiskUsagePercent:F1}%", AlertSeverity.Critical);
        }
        else if (metrics.DiskUsagePercent > 85)
        {
            AddOrUpdateAlert("disk_warning", "Disk Warning", $"Disk usage at {metrics.DiskUsagePercent:F1}%", AlertSeverity.Warning);
        }

        return Task.CompletedTask;
    }

    private void AddOrUpdateAlert(string id, string title, string message, AlertSeverity severity)
    {
        _alerts.AddOrUpdate(id,
            _ => new SystemAlert { Id = id, Title = title, Message = message, Severity = severity, Source = "System" },
            (_, existing) => { existing.Message = message; existing.Timestamp = DateTime.UtcNow; return existing; });
    }

    private double GetCpuUsage()
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = _currentProcess.TotalProcessorTime;
            Thread.Sleep(50);
            _currentProcess.Refresh();
            var endTime = DateTime.UtcNow;
            var endCpuUsage = _currentProcess.TotalProcessorTime;
            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            return Math.Min(100, cpuUsedMs / (Environment.ProcessorCount * totalMsPassed) * 100);
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
                await _healthService.GetSystemHealthAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health monitor");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
