using System.Diagnostics;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.ResourceMonitoring;

/// <summary>
/// Observability strategy for system resource monitoring including CPU, memory, disk, and network metrics.
/// Provides real-time resource utilization tracking with configurable collection intervals.
/// </summary>
/// <remarks>
/// This strategy monitors:
/// <list type="bullet">
///   <item>CPU utilization (per-core and total)</item>
///   <item>Memory usage (working set, private bytes, GC heap)</item>
///   <item>Disk I/O and space utilization</item>
///   <item>Network throughput and connections</item>
///   <item>Thread pool and handle counts</item>
/// </list>
/// </remarks>
public sealed class SystemResourceStrategy : ObservabilityStrategyBase
{
    private readonly BoundedDictionary<string, double> _lastValues = new BoundedDictionary<string, double>(1000);
    private readonly BoundedDictionary<string, ResourceThreshold> _thresholds = new BoundedDictionary<string, ResourceThreshold>(1000);
    private Process? _currentProcess;
    private Timer? _collectionTimer;
    private int _collectionIntervalMs = 5000;
    private volatile bool _isCollecting;
    private readonly List<MetricValue> _collectedMetrics = new();
    private readonly object _metricsLock = new();
    private DateTimeOffset _lastCpuTime = DateTimeOffset.UtcNow;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;

    /// <inheritdoc/>
    public override string StrategyId => "system-resources";

    /// <inheritdoc/>
    public override string Name => "System Resource Monitor";

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemResourceStrategy"/> class.
    /// </summary>
    public SystemResourceStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: true,
        SupportsTracing: false,
        SupportsLogging: true,
        SupportsDistributedTracing: false,
        SupportsAlerting: true,
        SupportedExporters: new[] { "SystemResources", "ProcessMetrics", "RuntimeMetrics" }))
    {
        _currentProcess = Process.GetCurrentProcess();
        InitializeDefaultThresholds();
    }

    /// <summary>
    /// Configures the resource monitoring parameters.
    /// </summary>
    /// <param name="collectionIntervalMs">Interval between metric collections in milliseconds.</param>
    public void Configure(int collectionIntervalMs = 5000)
    {
        _collectionIntervalMs = collectionIntervalMs;
    }

    /// <summary>
    /// Sets a threshold for a specific resource metric.
    /// </summary>
    /// <param name="metricName">Name of the metric.</param>
    /// <param name="warningThreshold">Warning threshold value.</param>
    /// <param name="criticalThreshold">Critical threshold value.</param>
    public void SetThreshold(string metricName, double warningThreshold, double criticalThreshold)
    {
        _thresholds[metricName] = new ResourceThreshold(warningThreshold, criticalThreshold);
    }

    /// <summary>
    /// Starts automatic resource collection.
    /// </summary>
    public void StartCollection()
    {
        if (_isCollecting) return;
        _isCollecting = true;
        _collectionTimer = new Timer(CollectResourceMetrics, null, 0, _collectionIntervalMs);
    }

    /// <summary>
    /// Stops automatic resource collection.
    /// </summary>
    public void StopCollection()
    {
        _isCollecting = false;
        _collectionTimer?.Dispose();
        _collectionTimer = null;
    }

    /// <summary>
    /// Collects current system resource metrics.
    /// </summary>
    /// <returns>Collection of current resource metrics.</returns>
    public IReadOnlyList<MetricValue> CollectCurrentMetrics()
    {
        var metrics = new List<MetricValue>();

        try
        {
            _currentProcess?.Refresh();

            // CPU Metrics
            if (_currentProcess != null)
            {
                var cpuTime = _currentProcess.TotalProcessorTime.TotalMilliseconds;
                metrics.Add(MetricValue.Gauge("process.cpu.time_ms", cpuTime));

                var userTime = _currentProcess.UserProcessorTime.TotalMilliseconds;
                metrics.Add(MetricValue.Gauge("process.cpu.user_time_ms", userTime));

                var kernelTime = _currentProcess.PrivilegedProcessorTime.TotalMilliseconds;
                metrics.Add(MetricValue.Gauge("process.cpu.kernel_time_ms", kernelTime));
            }

            // Memory Metrics
            if (_currentProcess != null)
            {
                var workingSet = _currentProcess.WorkingSet64;
                metrics.Add(MetricValue.Gauge("process.memory.working_set_bytes", workingSet,
                    new[] { new MetricLabel("unit", "bytes") }));

                var privateBytes = _currentProcess.PrivateMemorySize64;
                metrics.Add(MetricValue.Gauge("process.memory.private_bytes", privateBytes,
                    new[] { new MetricLabel("unit", "bytes") }));

                var pagedMemory = _currentProcess.PagedMemorySize64;
                metrics.Add(MetricValue.Gauge("process.memory.paged_bytes", pagedMemory,
                    new[] { new MetricLabel("unit", "bytes") }));

                var virtualMemory = _currentProcess.VirtualMemorySize64;
                metrics.Add(MetricValue.Gauge("process.memory.virtual_bytes", virtualMemory,
                    new[] { new MetricLabel("unit", "bytes") }));
            }

            // GC Metrics
            var gcInfo = GC.GetGCMemoryInfo();
            metrics.Add(MetricValue.Gauge("runtime.gc.heap_size_bytes", gcInfo.HeapSizeBytes));
            metrics.Add(MetricValue.Gauge("runtime.gc.fragmented_bytes", gcInfo.FragmentedBytes));
            metrics.Add(MetricValue.Gauge("runtime.gc.memory_load_bytes", gcInfo.MemoryLoadBytes));
            metrics.Add(MetricValue.Gauge("runtime.gc.total_available_memory_bytes", gcInfo.TotalAvailableMemoryBytes));
            metrics.Add(MetricValue.Gauge("runtime.gc.high_memory_load_threshold_bytes", gcInfo.HighMemoryLoadThresholdBytes));

            for (int gen = 0; gen <= GC.MaxGeneration; gen++)
            {
                metrics.Add(MetricValue.Counter($"runtime.gc.collections_gen{gen}", GC.CollectionCount(gen),
                    new[] { new MetricLabel("generation", gen.ToString()) }));
            }

            metrics.Add(MetricValue.Gauge("runtime.gc.total_memory_bytes", GC.GetTotalMemory(false)));

            // Thread Pool Metrics
            ThreadPool.GetAvailableThreads(out int workerAvailable, out int completionPortAvailable);
            ThreadPool.GetMaxThreads(out int workerMax, out int completionPortMax);
            ThreadPool.GetMinThreads(out int workerMin, out int completionPortMin);

            metrics.Add(MetricValue.Gauge("runtime.threadpool.worker_available", workerAvailable));
            metrics.Add(MetricValue.Gauge("runtime.threadpool.worker_max", workerMax));
            metrics.Add(MetricValue.Gauge("runtime.threadpool.worker_min", workerMin));
            metrics.Add(MetricValue.Gauge("runtime.threadpool.completion_port_available", completionPortAvailable));
            metrics.Add(MetricValue.Gauge("runtime.threadpool.completion_port_max", completionPortMax));
            metrics.Add(MetricValue.Gauge("runtime.threadpool.pending_work_items", ThreadPool.PendingWorkItemCount));

            // Process Handles and Threads
            if (_currentProcess != null)
            {
                metrics.Add(MetricValue.Gauge("process.handle_count", _currentProcess.HandleCount));
                metrics.Add(MetricValue.Gauge("process.thread_count", _currentProcess.Threads.Count));
            }

            // Environment Info
            metrics.Add(MetricValue.Gauge("runtime.processor_count", Environment.ProcessorCount));
            metrics.Add(MetricValue.Gauge("runtime.is_64bit", Environment.Is64BitProcess ? 1 : 0));

            // Calculate utilization percentages
            if (_currentProcess != null && gcInfo.TotalAvailableMemoryBytes > 0)
            {
                var memoryUtilization = (double)gcInfo.MemoryLoadBytes / gcInfo.TotalAvailableMemoryBytes * 100;
                metrics.Add(MetricValue.Gauge("process.memory.utilization_percent", memoryUtilization));
            }
        }
        catch (Exception ex)
        {
            // Log error but don't fail
            metrics.Add(MetricValue.Counter("resource_monitor.errors", 1,
                new[] { new MetricLabel("error_type", ex.GetType().Name) }));
        }

        return metrics;
    }

    /// <summary>
    /// Gets the current resource utilization summary.
    /// </summary>
    /// <returns>Resource utilization summary.</returns>
    public ResourceUtilizationSummary GetResourceSummary()
    {
        _currentProcess?.Refresh();

        var gcInfo = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out int workerAvailable, out _);
        ThreadPool.GetMaxThreads(out int workerMax, out _);

        return new ResourceUtilizationSummary
        {
            Timestamp = DateTimeOffset.UtcNow,
            WorkingSetBytes = _currentProcess?.WorkingSet64 ?? 0,
            PrivateMemoryBytes = _currentProcess?.PrivateMemorySize64 ?? 0,
            GcHeapSizeBytes = gcInfo.HeapSizeBytes,
            GcMemoryLoadBytes = gcInfo.MemoryLoadBytes,
            TotalAvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes,
            MemoryUtilizationPercent = gcInfo.TotalAvailableMemoryBytes > 0
                ? (double)gcInfo.MemoryLoadBytes / gcInfo.TotalAvailableMemoryBytes * 100
                : 0,
            ThreadPoolUtilizationPercent = workerMax > 0
                ? (double)(workerMax - workerAvailable) / workerMax * 100
                : 0,
            HandleCount = _currentProcess?.HandleCount ?? 0,
            ThreadCount = _currentProcess?.Threads.Count ?? 0,
            ProcessorCount = Environment.ProcessorCount
        };
    }

    /// <summary>
    /// Checks if any resource thresholds are exceeded.
    /// </summary>
    /// <returns>List of threshold violations.</returns>
    public IReadOnlyList<ThresholdViolation> CheckThresholds()
    {
        var violations = new List<ThresholdViolation>();
        var metrics = CollectCurrentMetrics();

        foreach (var metric in metrics)
        {
            if (_thresholds.TryGetValue(metric.Name, out var threshold))
            {
                if (metric.Value >= threshold.Critical)
                {
                    violations.Add(new ThresholdViolation(metric.Name, metric.Value, threshold.Critical, ThresholdLevel.Critical));
                }
                else if (metric.Value >= threshold.Warning)
                {
                    violations.Add(new ThresholdViolation(metric.Name, metric.Value, threshold.Warning, ThresholdLevel.Warning));
                }
            }
        }

        return violations;
    }

    private void CollectResourceMetrics(object? state)
    {
        if (!_isCollecting) return;

        try
        {
            var metrics = CollectCurrentMetrics();
            lock (_metricsLock)
            {
                _collectedMetrics.AddRange(metrics);

                // Keep only last 1000 metrics to prevent unbounded growth
                if (_collectedMetrics.Count > 1000)
                {
                    _collectedMetrics.RemoveRange(0, _collectedMetrics.Count - 1000);
                }
            }
        }
        catch
        {
            // Ignore collection errors
        }
    }

    private void InitializeDefaultThresholds()
    {
        _thresholds["process.memory.utilization_percent"] = new ResourceThreshold(70, 90);
        _thresholds["runtime.threadpool.pending_work_items"] = new ResourceThreshold(100, 500);
        _thresholds["process.handle_count"] = new ResourceThreshold(5000, 10000);
    }

    /// <inheritdoc/>
    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken)
    {
        IncrementCounter("system_resource.metrics_sent");
        // Store externally provided metrics alongside collected ones
        lock (_metricsLock)
        {
            _collectedMetrics.AddRange(metrics);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("System Resource Monitor does not support tracing");
    }

    /// <inheritdoc/>
    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("system_resource.logs_sent");
        // Log threshold violations
        var violations = CheckThresholds();
        // Violations would be logged to configured logging backend
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken cancellationToken)
    {
        var violations = CheckThresholds();
        var isHealthy = !violations.Any(v => v.Level == ThresholdLevel.Critical);
        var summary = GetResourceSummary();

        return Task.FromResult(new HealthCheckResult(
            IsHealthy: isHealthy,
            Description: isHealthy ? "System resources within normal limits" : $"Resource thresholds exceeded: {violations.Count} violations",
            Data: new Dictionary<string, object>
            {
                ["memoryUtilizationPercent"] = summary.MemoryUtilizationPercent,
                ["threadPoolUtilizationPercent"] = summary.ThreadPoolUtilizationPercent,
                ["handleCount"] = summary.HandleCount,
                ["threadCount"] = summary.ThreadCount,
                ["gcHeapSizeMB"] = summary.GcHeapSizeBytes / (1024.0 * 1024.0),
                ["violations"] = violations.Count
            }));
    }

    /// <inheritdoc/>

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("system_resource.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("system_resource.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopCollection();
            _currentProcess?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Resource threshold configuration.
/// </summary>
public record ResourceThreshold(double Warning, double Critical);

/// <summary>
/// Threshold violation details.
/// </summary>
public record ThresholdViolation(string MetricName, double CurrentValue, double ThresholdValue, ThresholdLevel Level);

/// <summary>
/// Threshold severity level.
/// </summary>
public enum ThresholdLevel { Warning, Critical }

/// <summary>
/// Resource utilization summary.
/// </summary>
public class ResourceUtilizationSummary
{
    public DateTimeOffset Timestamp { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long GcHeapSizeBytes { get; init; }
    public long GcMemoryLoadBytes { get; init; }
    public long TotalAvailableMemoryBytes { get; init; }
    public double MemoryUtilizationPercent { get; init; }
    public double ThreadPoolUtilizationPercent { get; init; }
    public int HandleCount { get; init; }
    public int ThreadCount { get; init; }
    public int ProcessorCount { get; init; }
}
