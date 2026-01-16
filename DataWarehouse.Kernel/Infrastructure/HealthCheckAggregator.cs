using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Kernel.Infrastructure
{
    /// <summary>
    /// Aggregates health checks from all registered components.
    /// Provides liveness and readiness endpoints for orchestrators.
    /// </summary>
    public sealed class HealthCheckAggregator : IHealthCheckAggregator
    {
        private readonly ConcurrentDictionary<string, IHealthCheck> _healthChecks = new();
        private readonly TimeSpan _cacheDuration;
        private readonly object _cacheLock = new();

        private HealthReport? _cachedReport;
        private DateTime _cacheExpiry = DateTime.MinValue;

        public HealthCheckAggregator(TimeSpan? cacheDuration = null)
        {
            _cacheDuration = cacheDuration ?? TimeSpan.FromSeconds(5);
        }

        public void Register(IHealthCheck healthCheck)
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

        public async Task<HealthReport> CheckHealthAsync(CancellationToken ct = default)
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
            var entries = new Dictionary<string, HealthCheckResult>();
            var overallStatus = HealthStatus.Healthy;

            // Run all health checks in parallel
            var tasks = _healthChecks.Select(async kvp =>
            {
                var checkSw = Stopwatch.StartNew();
                try
                {
                    var result = await kvp.Value.CheckHealthAsync(ct);
                    checkSw.Stop();

                    return (kvp.Key, Result: result with { Duration = checkSw.Elapsed });
                }
                catch (Exception ex)
                {
                    checkSw.Stop();
                    return (kvp.Key, Result: HealthCheckResult.Unhealthy(
                        $"Health check threw exception: {ex.Message}",
                        ex,
                        new Dictionary<string, object> { ["ExceptionType"] = ex.GetType().Name }
                    ) with { Duration = checkSw.Elapsed });
                }
            });

            var results = await Task.WhenAll(tasks);

            foreach (var (name, result) in results)
            {
                entries[name] = result;

                // Determine overall status (worst wins)
                if (result.Status == HealthStatus.Unhealthy)
                {
                    overallStatus = HealthStatus.Unhealthy;
                }
                else if (result.Status == HealthStatus.Degraded && overallStatus != HealthStatus.Unhealthy)
                {
                    overallStatus = HealthStatus.Degraded;
                }
            }

            sw.Stop();

            var report = new HealthReport
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

        public async Task<HealthReport> CheckHealthAsync(string[] tags, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var entries = new Dictionary<string, HealthCheckResult>();
            var overallStatus = HealthStatus.Healthy;
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
                    return (kvp.Key, Result: result with { Duration = checkSw.Elapsed });
                }
                catch (Exception ex)
                {
                    checkSw.Stop();
                    return (kvp.Key, Result: HealthCheckResult.Unhealthy(
                        $"Health check threw exception: {ex.Message}", ex
                    ) with { Duration = checkSw.Elapsed });
                }
            });

            var results = await Task.WhenAll(tasks);

            foreach (var (name, result) in results)
            {
                entries[name] = result;

                if (result.Status == HealthStatus.Unhealthy)
                {
                    overallStatus = HealthStatus.Unhealthy;
                }
                else if (result.Status == HealthStatus.Degraded && overallStatus != HealthStatus.Unhealthy)
                {
                    overallStatus = HealthStatus.Degraded;
                }
            }

            sw.Stop();

            return new HealthReport
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

            return report.Status != HealthStatus.Unhealthy;
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

            return report.Status == HealthStatus.Healthy;
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
    public sealed class KernelHealthCheck : IHealthCheck
    {
        private readonly IMemoryPressureMonitor _memoryMonitor;
        private readonly Func<int> _getPluginCount;
        private readonly Func<bool> _isRunning;

        public string Name => "kernel";
        public string[] Tags => ["liveness", "readiness", "kernel"];

        public KernelHealthCheck(
            IMemoryPressureMonitor memoryMonitor,
            Func<int> getPluginCount,
            Func<bool> isRunning)
        {
            _memoryMonitor = memoryMonitor;
            _getPluginCount = getPluginCount;
            _isRunning = isRunning;
        }

        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            var data = new Dictionary<string, object>();

            // Check if kernel is running
            if (!_isRunning())
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
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

            if (memoryLevel == MemoryPressureLevel.Critical)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Critical memory pressure: {memoryStats.UsagePercent:F1}%",
                    data: data
                ));
            }

            if (memoryLevel == MemoryPressureLevel.High)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
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
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Thread pool exhaustion: {workerUtilization:P0} utilized",
                    data: data
                ));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Kernel healthy. Memory: {memoryStats.UsagePercent:F1}%, Plugins: {_getPluginCount()}",
                data: data
            ));
        }
    }
}
