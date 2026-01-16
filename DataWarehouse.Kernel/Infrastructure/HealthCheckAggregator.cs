using System.Collections.Concurrent;
using System.Diagnostics;
using SdkContracts = DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Kernel.Infrastructure
{
    /// <summary>
    /// Aggregates health checks from all registered components.
    /// Provides liveness and readiness endpoints for orchestrators.
    /// </summary>
    public sealed class HealthCheckAggregator : SdkContracts.IHealthCheckAggregator
    {
        private readonly ConcurrentDictionary<string, SdkContracts.IHealthCheck> _healthChecks = new();
        private readonly TimeSpan _cacheDuration;
        private readonly object _cacheLock = new();

        private SdkContracts.HealthReport? _cachedReport;
        private DateTime _cacheExpiry = DateTime.MinValue;

        public HealthCheckAggregator(TimeSpan? cacheDuration = null)
        {
            _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(5);
        }

        public void Register(SdkContracts.IHealthCheck healthCheck)
        {
            ArgumentNullException.ThrowIfNull(healthCheck);

            _healthChecks[healthCheck.Name] = healthCheck;
            InvalidateCache();
        }

        public void Unregister(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            _healthChecks.TryRemove(name, out _);
            InvalidateCache();
        }

        public async Task<SdkContracts.HealthReport> CheckHealthAsync(CancellationToken ct = default)
        {
            // Check cache
            lock (_cacheLock)
            {
                if (_cachedReport != null && DateTime.UtcNow < _cacheExpiry)
                {
                    return _cachedReport;
                }
            }

            var sw = Stopwatch.StartNew();
            var entries = new Dictionary<string, SdkContracts.HealthCheckResult>();
            var overallStatus = SdkContracts.HealthStatus.Healthy;

            // Run all health checks in parallel
            var tasks = _healthChecks.Select(async kvp =>
            {
                var checkSw = Stopwatch.StartNew();
                try
                {
                    var result = await kvp.Value.CheckHealthAsync(ct);
                    checkSw.Stop();

                    // Create a new result with duration set
                    return (kvp.Key, Result: new SdkContracts.HealthCheckResult
                    {
                        Status = result.Status,
                        Message = result.Message,
                        Duration = checkSw.Elapsed,
                        Data = result.Data,
                        Exception = result.Exception
                    });
                }
                catch (Exception ex)
                {
                    checkSw.Stop();
                    return (kvp.Key, Result: new SdkContracts.HealthCheckResult
                    {
                        Status = SdkContracts.HealthStatus.Unhealthy,
                        Message = $"Health check threw exception: {ex.Message}",
                        Exception = ex,
                        Duration = checkSw.Elapsed,
                        Data = new Dictionary<string, object> { ["ExceptionType"] = ex.GetType().Name }
                    });
                }
            });

            var results = await Task.WhenAll(tasks);

            foreach (var (name, result) in results)
            {
                entries[name] = result;

                // Determine overall status (worst wins)
                if (result.Status == SdkContracts.HealthStatus.Unhealthy)
                {
                    overallStatus = SdkContracts.HealthStatus.Unhealthy;
                }
                else if (result.Status == SdkContracts.HealthStatus.Degraded && overallStatus != SdkContracts.HealthStatus.Unhealthy)
                {
                    overallStatus = SdkContracts.HealthStatus.Degraded;
                }
            }

            sw.Stop();

            var report = new SdkContracts.HealthReport
            {
                Status = overallStatus,
                TotalDuration = sw.Elapsed,
                Entries = entries,
                Timestamp = DateTime.UtcNow
            };

            // Update cache
            lock (_cacheLock)
            {
                _cachedReport = report;
                _cacheExpiry = DateTime.UtcNow + _cacheDuration;
            }

            return report;
        }

        public async Task<SdkContracts.HealthReport> CheckHealthAsync(string[] tags, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var entries = new Dictionary<string, SdkContracts.HealthCheckResult>();
            var overallStatus = SdkContracts.HealthStatus.Healthy;
            var tagsSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);

            // Filter health checks by tags and run in parallel
            var filteredChecks = _healthChecks
                .Where(kvp => kvp.Value.Tags.Any(t => tagsSet.Contains(t)))
                .ToList();

            var tasks = filteredChecks.Select(async kvp =>
            {
                var checkSw = Stopwatch.StartNew();
                try
                {
                    var result = await kvp.Value.CheckHealthAsync(ct);
                    checkSw.Stop();
                    return (kvp.Key, Result: new SdkContracts.HealthCheckResult
                    {
                        Status = result.Status,
                        Message = result.Message,
                        Duration = checkSw.Elapsed,
                        Data = result.Data,
                        Exception = result.Exception
                    });
                }
                catch (Exception ex)
                {
                    checkSw.Stop();
                    return (kvp.Key, Result: new SdkContracts.HealthCheckResult
                    {
                        Status = SdkContracts.HealthStatus.Unhealthy,
                        Message = $"Health check threw exception: {ex.Message}",
                        Exception = ex,
                        Duration = checkSw.Elapsed
                    });
                }
            });

            var results = await Task.WhenAll(tasks);

            foreach (var (name, result) in results)
            {
                entries[name] = result;

                if (result.Status == SdkContracts.HealthStatus.Unhealthy)
                {
                    overallStatus = SdkContracts.HealthStatus.Unhealthy;
                }
                else if (result.Status == SdkContracts.HealthStatus.Degraded && overallStatus != SdkContracts.HealthStatus.Unhealthy)
                {
                    overallStatus = SdkContracts.HealthStatus.Degraded;
                }
            }

            sw.Stop();

            return new SdkContracts.HealthReport
            {
                Status = overallStatus,
                TotalDuration = sw.Elapsed,
                Entries = entries,
                Timestamp = DateTime.UtcNow
            };
        }

        public async Task<bool> IsLiveAsync(CancellationToken ct = default)
        {
            // Liveness: Is the system running?
            // Check only critical components tagged with "liveness"
            var report = await CheckHealthAsync(["liveness"], ct);

            // If no liveness checks registered, assume live
            if (report.Entries.Count == 0)
            {
                return true;
            }

            return report.Status != SdkContracts.HealthStatus.Unhealthy;
        }

        public async Task<bool> IsReadyAsync(CancellationToken ct = default)
        {
            // Readiness: Is the system ready to accept requests?
            // Check components tagged with "readiness"
            var report = await CheckHealthAsync(["readiness"], ct);

            // If no readiness checks registered, check all
            if (report.Entries.Count == 0)
            {
                report = await CheckHealthAsync(ct);
            }

            return report.Status == SdkContracts.HealthStatus.Healthy;
        }

        private void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedReport = null;
                _cacheExpiry = DateTime.MinValue;
            }
        }
    }

    /// <summary>
    /// Built-in kernel health check that monitors core kernel health.
    /// </summary>
    public sealed class KernelHealthCheck : SdkContracts.IHealthCheck
    {
        private readonly SdkContracts.IMemoryPressureMonitor _memoryMonitor;
        private readonly Func<int> _getPluginCount;
        private readonly Func<bool> _isRunning;

        public string Name => "kernel";
        public string[] Tags => ["liveness", "readiness", "kernel"];

        public KernelHealthCheck(
            SdkContracts.IMemoryPressureMonitor memoryMonitor,
            Func<int> getPluginCount,
            Func<bool> isRunning)
        {
            _memoryMonitor = memoryMonitor;
            _getPluginCount = getPluginCount;
            _isRunning = isRunning;
        }

        public Task<SdkContracts.HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            var data = new Dictionary<string, object>();

            // Check if kernel is running
            if (!_isRunning())
            {
                return Task.FromResult(SdkContracts.HealthCheckResult.Unhealthy(
                    "Kernel is not running",
                    data: data
                ));
            }

            // Check memory pressure
            var memoryStats = _memoryMonitor.GetStatistics();
            var memoryLevel = _memoryMonitor.CurrentLevel;

            data["memoryUsagePercent"] = memoryStats.UsagePercent;
            data["memoryPressureLevel"] = memoryLevel.ToString();
            data["gcTotalMemoryMB"] = memoryStats.GCTotalMemory / (1024 * 1024);
            data["pluginCount"] = _getPluginCount();
            data["gen2Collections"] = memoryStats.Gen2Collections;

            if (memoryLevel == SdkContracts.MemoryPressureLevel.Critical)
            {
                return Task.FromResult(SdkContracts.HealthCheckResult.Unhealthy(
                    $"Critical memory pressure: {memoryStats.UsagePercent:F1}%",
                    data: data
                ));
            }

            if (memoryLevel == SdkContracts.MemoryPressureLevel.High)
            {
                return Task.FromResult(SdkContracts.HealthCheckResult.Degraded(
                    $"High memory pressure: {memoryStats.UsagePercent:F1}%",
                    data: data
                ));
            }

            // Check thread pool
            ThreadPool.GetAvailableThreads(out var workerThreads, out var ioThreads);
            ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);

            data["availableWorkerThreads"] = workerThreads;
            data["availableIoThreads"] = ioThreads;

            var workerUtilization = 1.0 - ((double)workerThreads / maxWorker);
            if (workerUtilization > 0.9)
            {
                return Task.FromResult(SdkContracts.HealthCheckResult.Degraded(
                    $"Thread pool exhaustion: {workerUtilization:P0} utilized",
                    data: data
                ));
            }

            return Task.FromResult(SdkContracts.HealthCheckResult.Healthy(
                $"Kernel healthy. Memory: {memoryStats.UsagePercent:F1}%, Plugins: {_getPluginCount()}",
                data: data
            ));
        }
    }
}
