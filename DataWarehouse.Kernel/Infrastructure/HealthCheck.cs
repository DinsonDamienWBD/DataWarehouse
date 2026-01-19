using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Kernel.Infrastructure
{
    /// <summary>
    /// Basic health check system for the kernel.
    /// Supports liveness, readiness, and component health checks.
    /// Essential for Kubernetes, load balancers, and orchestration systems.
    /// </summary>
    public class BasicHealthCheckManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, IHealthCheck> _checks = new();
        private readonly ConcurrentDictionary<string, HealthCheckResult> _lastResults = new();
        private readonly IKernelContext _context;
        private readonly HealthCheckConfig _config;
        private readonly Timer _backgroundCheckTimer;
        private readonly CancellationTokenSource _shutdownCts = new();
        private volatile bool _isShuttingDown;
        private DateTime _startTime;

        /// <summary>
        /// Indicates if the system is shutting down.
        /// </summary>
        public bool IsShuttingDown => _isShuttingDown;

        /// <summary>
        /// Time since the kernel started.
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;

        public BasicHealthCheckManager(IKernelContext context, HealthCheckConfig? config = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _config = config ?? new HealthCheckConfig();
            _startTime = DateTime.UtcNow;

            // Register built-in checks
            RegisterBuiltInChecks();

            // Start background health checks
            _backgroundCheckTimer = new Timer(
                RunBackgroundChecks,
                null,
                _config.BackgroundCheckInterval,
                _config.BackgroundCheckInterval);
        }

        #region Health Check Registration

        /// <summary>
        /// Registers a health check.
        /// </summary>
        public void Register(string name, IHealthCheck check)
        {
            _checks[name] = check;
            _context.LogInfo($"[Health] Registered health check: {name}");
        }

        /// <summary>
        /// Registers a simple health check function.
        /// </summary>
        public void Register(string name, Func<CancellationToken, Task<HealthCheckResult>> checkFunc)
        {
            Register(name, new DelegateHealthCheck(checkFunc));
        }

        /// <summary>
        /// Unregisters a health check.
        /// </summary>
        public void Unregister(string name)
        {
            _checks.TryRemove(name, out _);
            _lastResults.TryRemove(name, out _);
        }

        private void RegisterBuiltInChecks()
        {
            // Memory check
            Register("memory", new DelegateHealthCheck(async ct =>
            {
                var gcInfo = GC.GetGCMemoryInfo();
                var memoryLoad = (double)gcInfo.MemoryLoadBytes / gcInfo.HighMemoryLoadThresholdBytes;

                await Task.CompletedTask;

                if (memoryLoad > 0.95)
                {
                    return HealthCheckResult.Unhealthy("Critical memory pressure",
                        new Dictionary<string, object>
                        {
                            ["MemoryLoad"] = memoryLoad,
                            ["MemoryBytes"] = gcInfo.MemoryLoadBytes,
                            ["Threshold"] = gcInfo.HighMemoryLoadThresholdBytes
                        });
                }

                if (memoryLoad > 0.80)
                {
                    return HealthCheckResult.Degraded("High memory usage",
                        new Dictionary<string, object>
                        {
                            ["MemoryLoad"] = memoryLoad,
                            ["MemoryBytes"] = gcInfo.MemoryLoadBytes
                        });
                }

                return HealthCheckResult.Healthy("Memory OK",
                    new Dictionary<string, object>
                    {
                        ["MemoryLoad"] = memoryLoad,
                        ["MemoryBytes"] = gcInfo.MemoryLoadBytes
                    });
            }));

            // Thread pool check
            Register("threadpool", new DelegateHealthCheck(async ct =>
            {
                ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
                ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxCompletionPortThreads);

                await Task.CompletedTask;

                var workerUtilization = 1.0 - ((double)workerThreads / maxWorkerThreads);

                if (workerUtilization > 0.95)
                {
                    return HealthCheckResult.Unhealthy("Thread pool exhausted",
                        new Dictionary<string, object>
                        {
                            ["WorkerUtilization"] = workerUtilization,
                            ["AvailableWorkers"] = workerThreads,
                            ["MaxWorkers"] = maxWorkerThreads
                        });
                }

                if (workerUtilization > 0.80)
                {
                    return HealthCheckResult.Degraded("High thread pool usage",
                        new Dictionary<string, object>
                        {
                            ["WorkerUtilization"] = workerUtilization,
                            ["AvailableWorkers"] = workerThreads
                        });
                }

                return HealthCheckResult.Healthy("Thread pool OK",
                    new Dictionary<string, object>
                    {
                        ["AvailableWorkers"] = workerThreads,
                        ["AvailableCompletionPort"] = completionPortThreads
                    });
            }));

            // GC pressure check
            Register("gc", new DelegateHealthCheck(async ct =>
            {
                await Task.CompletedTask;

                var gen0 = GC.CollectionCount(0);
                var gen1 = GC.CollectionCount(1);
                var gen2 = GC.CollectionCount(2);
                var totalMemory = GC.GetTotalMemory(false);

                return HealthCheckResult.Healthy("GC OK",
                    new Dictionary<string, object>
                    {
                        ["Gen0Collections"] = gen0,
                        ["Gen1Collections"] = gen1,
                        ["Gen2Collections"] = gen2,
                        ["TotalMemory"] = totalMemory
                    });
            }));
        }

        #endregion

        #region Health Check Execution

        /// <summary>
        /// Checks liveness (is the process running and responsive?).
        /// </summary>
        public Task<HealthReport> CheckLivenessAsync(CancellationToken ct = default)
        {
            // Liveness is simple - if we can respond, we're alive
            return Task.FromResult(new HealthReport
            {
                Status = _isShuttingDown ? HealthStatus.Unhealthy : HealthStatus.Healthy,
                TotalDuration = TimeSpan.Zero,
                Entries = new Dictionary<string, HealthCheckResult>
                {
                    ["liveness"] = _isShuttingDown
                        ? HealthCheckResult.Unhealthy("Shutting down")
                        : HealthCheckResult.Healthy("Alive")
                }
            });
        }

        /// <summary>
        /// Checks readiness (is the system ready to accept work?).
        /// </summary>
        public async Task<HealthReport> CheckReadinessAsync(CancellationToken ct = default)
        {
            if (_isShuttingDown)
            {
                return new HealthReport
                {
                    Status = HealthStatus.Unhealthy,
                    TotalDuration = TimeSpan.Zero,
                    Entries = new Dictionary<string, HealthCheckResult>
                    {
                        ["readiness"] = HealthCheckResult.Unhealthy("Shutting down")
                    }
                };
            }

            // Run all checks that affect readiness
            return await RunChecksAsync(_config.ReadinessChecks, ct);
        }

        /// <summary>
        /// Runs all health checks and returns a comprehensive report.
        /// </summary>
        public async Task<HealthReport> CheckHealthAsync(CancellationToken ct = default)
        {
            return await RunChecksAsync(_checks.Keys.ToArray(), ct);
        }

        /// <summary>
        /// Runs specific health checks.
        /// </summary>
        public async Task<HealthReport> RunChecksAsync(string[] checkNames, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var results = new Dictionary<string, HealthCheckResult>();

            var tasks = checkNames
                .Where(name => _checks.ContainsKey(name))
                .Select(async name =>
                {
                    var checkSw = Stopwatch.StartNew();
                    HealthCheckResult result;

                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(_config.CheckTimeout);

                        result = await _checks[name].CheckAsync(cts.Token);
                        result.Duration = checkSw.Elapsed;
                    }
                    catch (OperationCanceledException)
                    {
                        result = HealthCheckResult.Unhealthy("Check timed out");
                        result.Duration = checkSw.Elapsed;
                    }
                    catch (Exception ex)
                    {
                        result = HealthCheckResult.Unhealthy($"Check failed: {ex.Message}");
                        result.Duration = checkSw.Elapsed;
                        result.Exception = ex;
                    }

                    _lastResults[name] = result;
                    return (name, result);
                });

            var allResults = await Task.WhenAll(tasks);

            foreach (var (name, result) in allResults)
            {
                results[name] = result;
            }

            var overallStatus = DetermineOverallStatus(results.Values);

            return new HealthReport
            {
                Status = overallStatus,
                TotalDuration = sw.Elapsed,
                Entries = results
            };
        }

        private HealthStatus DetermineOverallStatus(IEnumerable<HealthCheckResult> results)
        {
            var statuses = results.Select(r => r.Status).ToList();

            if (statuses.Any(s => s == HealthStatus.Unhealthy))
                return HealthStatus.Unhealthy;

            if (statuses.Any(s => s == HealthStatus.Degraded))
                return HealthStatus.Degraded;

            return HealthStatus.Healthy;
        }

        private void RunBackgroundChecks(object? state)
        {
            if (_isShuttingDown) return;

            try
            {
                var report = CheckHealthAsync(_shutdownCts.Token).GetAwaiter().GetResult();

                if (report.Status == HealthStatus.Unhealthy)
                {
                    _context.LogWarning($"[Health] System unhealthy: {string.Join(", ", report.Entries.Where(e => e.Value.Status == HealthStatus.Unhealthy).Select(e => e.Key))}");
                }
            }
            catch (Exception ex)
            {
                _context.LogError("[Health] Background health check failed", ex);
            }
        }

        #endregion

        #region Graceful Shutdown

        /// <summary>
        /// Initiates graceful shutdown.
        /// </summary>
        public async Task ShutdownAsync(TimeSpan timeout)
        {
            if (_isShuttingDown) return;

            _isShuttingDown = true;
            _context.LogInfo("[Shutdown] Initiating graceful shutdown...");

            // Stop accepting new work
            _shutdownCts.Cancel();
            _backgroundCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);

            // Wait for in-flight operations to complete
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                // Check if all operations are complete
                var activeOps = GetActiveOperations();
                if (activeOps == 0)
                {
                    _context.LogInfo("[Shutdown] All operations completed");
                    break;
                }

                _context.LogDebug($"[Shutdown] Waiting for {activeOps} operations to complete...");
                await Task.Delay(100);
            }

            _context.LogInfo("[Shutdown] Graceful shutdown complete");
        }

        /// <summary>
        /// Gets the count of active operations (override in kernel).
        /// </summary>
        protected virtual int GetActiveOperations()
        {
            // Base implementation - override to track actual operations
            return 0;
        }

        /// <summary>
        /// Registers a shutdown handler.
        /// </summary>
        public void OnShutdown(Action handler)
        {
            _shutdownCts.Token.Register(handler);
        }

        #endregion

        #region Status Endpoints

        /// <summary>
        /// Gets a simple status object suitable for HTTP responses.
        /// </summary>
        public async Task<object> GetStatusAsync(CancellationToken ct = default)
        {
            var report = await CheckHealthAsync(ct);

            return new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                uptime = Uptime.ToString(),
                uptimeSeconds = Uptime.TotalSeconds,
                checks = report.Entries.ToDictionary(
                    e => e.Key,
                    e => new
                    {
                        status = e.Value.Status.ToString().ToLowerInvariant(),
                        description = e.Value.Description,
                        durationMs = e.Value.Duration.TotalMilliseconds,
                        data = e.Value.Data
                    })
            };
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            _backgroundCheckTimer.Dispose();
            _shutdownCts.Dispose();
        }

        #endregion
    }

    #region Health Check Types

    /// <summary>
    /// Interface for health checks.
    /// </summary>
    public interface IHealthCheck
    {
        Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Health check result.
    /// </summary>
    public class HealthCheckResult
    {
        public HealthStatus Status { get; set; }
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public TimeSpan Duration { get; set; }
        public Exception? Exception { get; set; }

        public static HealthCheckResult Healthy(string description = "Healthy", Dictionary<string, object>? data = null)
            => new() { Status = HealthStatus.Healthy, Description = description, Data = data ?? new() };

        public static HealthCheckResult Degraded(string description, Dictionary<string, object>? data = null)
            => new() { Status = HealthStatus.Degraded, Description = description, Data = data ?? new() };

        public static HealthCheckResult Unhealthy(string description, Dictionary<string, object>? data = null)
            => new() { Status = HealthStatus.Unhealthy, Description = description, Data = data ?? new() };
    }

    /// <summary>
    /// Health status enumeration.
    /// </summary>
    public enum HealthStatus
    {
        Healthy,
        Degraded,
        Unhealthy
    }

    /// <summary>
    /// Complete health report.
    /// </summary>
    public class HealthReport
    {
        public HealthStatus Status { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<string, HealthCheckResult> Entries { get; set; } = new();
    }

    /// <summary>
    /// Delegate-based health check.
    /// </summary>
    public class DelegateHealthCheck : IHealthCheck
    {
        private readonly Func<CancellationToken, Task<HealthCheckResult>> _checkFunc;

        public DelegateHealthCheck(Func<CancellationToken, Task<HealthCheckResult>> checkFunc)
        {
            _checkFunc = checkFunc;
        }

        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            return _checkFunc(ct);
        }
    }

    /// <summary>
    /// Configuration for health checks.
    /// </summary>
    public class HealthCheckConfig
    {
        public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan BackgroundCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
        public string[] ReadinessChecks { get; set; } = new[] { "memory", "threadpool" };
    }

    #endregion
}
