using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Kernel.Infrastructure
{
    /// <summary>
    /// Configuration options for the HealthCheckManager.
    /// </summary>
    public sealed class HealthCheckManagerConfig
    {
        /// <summary>
        /// Interval between background health checks.
        /// </summary>
        public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Timeout for individual health checks.
        /// </summary>
        public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Memory usage percentage threshold for degraded status.
        /// </summary>
        public double MemoryDegradedThreshold { get; init; } = 0.80;

        /// <summary>
        /// Memory usage percentage threshold for unhealthy status.
        /// </summary>
        public double MemoryUnhealthyThreshold { get; init; } = 0.95;

        /// <summary>
        /// Thread pool utilization threshold for degraded status.
        /// </summary>
        public double ThreadPoolDegradedThreshold { get; init; } = 0.80;

        /// <summary>
        /// Thread pool utilization threshold for unhealthy status.
        /// </summary>
        public double ThreadPoolUnhealthyThreshold { get; init; } = 0.95;

        /// <summary>
        /// Whether to include plugin health checks in aggregation.
        /// </summary>
        public bool AggregatePluginHealth { get; init; } = true;

        /// <summary>
        /// Whether to fail fast if any component is unhealthy.
        /// </summary>
        public bool FailFastOnUnhealthy { get; init; } = false;
    }

    /// <summary>
    /// Event arguments for health state changes.
    /// </summary>
    public sealed class HealthChangedEventArgs : EventArgs
    {
        /// <summary>
        /// The previous health status.
        /// </summary>
        public HealthStatus PreviousStatus { get; init; }

        /// <summary>
        /// The new health status.
        /// </summary>
        public HealthStatus CurrentStatus { get; init; }

        /// <summary>
        /// The full health report at the time of the change.
        /// </summary>
        public HealthReport Report { get; init; } = null!;

        /// <summary>
        /// Timestamp when the change occurred.
        /// </summary>
        public DateTime Timestamp { get; init; }

        /// <summary>
        /// Names of components that changed status.
        /// </summary>
        public IReadOnlyList<string> ChangedComponents { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Production-ready health check manager that implements IHealthCheck from SDK.
    /// Provides comprehensive system health monitoring including memory, thread pool,
    /// and aggregated plugin health checks.
    ///
    /// Features:
    /// - Background health monitoring with configurable interval
    /// - Memory pressure monitoring using GC.GetGCMemoryInfo
    /// - Thread pool monitoring using ThreadPool.GetAvailableThreads
    /// - Plugin health aggregation for plugins implementing IHealthCheck
    /// - OnHealthChanged event for reactive health monitoring
    /// - Proper disposal and cancellation support
    /// </summary>
    public sealed class HealthCheckManager : IHealthCheck, IAsyncDisposable, IDisposable
    {
        private readonly HealthCheckManagerConfig _config;
        private readonly PluginRegistry _pluginRegistry;
        private readonly ConcurrentDictionary<string, HealthCheckResult> _lastResults = new();
        private readonly ConcurrentDictionary<string, HealthStatus> _previousComponentStatuses = new();
        private readonly SemaphoreSlim _checkLock = new(1, 1);
        private readonly object _stateLock = new();

        private Timer? _backgroundTimer;
        private CancellationTokenSource? _stoppingCts;
        private HealthStatus _lastOverallStatus = HealthStatus.Healthy;
        private HealthReport? _lastReport;
        private DateTime _startTime;
        private volatile bool _isRunning;
        private volatile bool _isDisposed;

        /// <summary>
        /// Event raised when overall health status changes.
        /// </summary>
        public event EventHandler<HealthChangedEventArgs>? OnHealthChanged;

        /// <summary>
        /// Gets the name of this health check.
        /// </summary>
        public string Name => "kernel-health-manager";

        /// <summary>
        /// Gets the tags for categorizing this health check.
        /// </summary>
        public string[] Tags => new[] { "kernel", "infrastructure", "liveness", "readiness" };

        /// <summary>
        /// Gets whether the background health monitoring is running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the time since the health check manager was started.
        /// </summary>
        public TimeSpan Uptime => _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero;

        /// <summary>
        /// Gets the last known health status.
        /// </summary>
        public HealthStatus LastStatus
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastOverallStatus;
                }
            }
        }

        /// <summary>
        /// Gets the most recent health report, if available.
        /// </summary>
        public HealthReport? LastReport
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastReport;
                }
            }
        }

        /// <summary>
        /// Creates a new instance of the HealthCheckManager.
        /// </summary>
        /// <param name="pluginRegistry">The plugin registry to monitor for plugin health.</param>
        /// <param name="config">Optional configuration. Uses defaults if not provided.</param>
        public HealthCheckManager(PluginRegistry pluginRegistry, HealthCheckManagerConfig? config = null)
        {
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            _config = config ?? new HealthCheckManagerConfig();
        }

        #region Lifecycle Management

        /// <summary>
        /// Starts background health monitoring.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to observe.</param>
        /// <returns>A task representing the start operation.</returns>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_isRunning)
            {
                return; // Already running
            }

            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return;
                }

                _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _startTime = DateTime.UtcNow;
                _isRunning = true;
            }

            // Run initial health check
            try
            {
                await CheckHealthInternalAsync(_stoppingCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation during startup
            }

            // Start background timer
            _backgroundTimer = new Timer(
                OnBackgroundCheck,
                null,
                _config.CheckInterval,
                _config.CheckInterval);
        }

        /// <summary>
        /// Stops background health monitoring.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to observe.</param>
        /// <returns>A task representing the stop operation.</returns>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_isRunning)
            {
                return;
            }

            lock (_stateLock)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
            }

            // Signal cancellation
            _stoppingCts?.Cancel();

            // Stop the timer
            if (_backgroundTimer != null)
            {
                await _backgroundTimer.DisposeAsync().ConfigureAwait(false);
                _backgroundTimer = null;
            }

            // Wait for any in-progress check to complete
            try
            {
                await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                _checkLock.Release();
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }

            // Cleanup
            _stoppingCts?.Dispose();
            _stoppingCts = null;
        }

        #endregion

        #region Health Check Implementation

        /// <summary>
        /// Performs a comprehensive health check of the system.
        /// </summary>
        /// <param name="ct">Cancellation token to observe.</param>
        /// <returns>The aggregated health check result.</returns>
        public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var report = await CheckHealthInternalAsync(ct).ConfigureAwait(false);

            return new HealthCheckResult
            {
                Status = report.Status,
                Message = GenerateHealthMessage(report),
                Duration = report.TotalDuration,
                Data = new Dictionary<string, object>
                {
                    ["uptime"] = Uptime.ToString(),
                    ["uptimeSeconds"] = Uptime.TotalSeconds,
                    ["componentCount"] = report.Entries.Count,
                    ["healthyCount"] = report.Entries.Count(e => e.Value.Status == HealthStatus.Healthy),
                    ["degradedCount"] = report.Entries.Count(e => e.Value.Status == HealthStatus.Degraded),
                    ["unhealthyCount"] = report.Entries.Count(e => e.Value.Status == HealthStatus.Unhealthy),
                    ["entries"] = report.Entries.ToDictionary(
                        e => e.Key,
                        e => (object)new { status = e.Value.Status.ToString(), message = e.Value.Message })
                }
            };
        }

        private async Task<HealthReport> CheckHealthInternalAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var entries = new Dictionary<string, HealthCheckResult>();

            if (!await _checkLock.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                return new HealthReport
                {
                    Status = HealthStatus.Degraded,
                    TotalDuration = sw.Elapsed,
                    Entries = new Dictionary<string, HealthCheckResult>
                    {
                        ["lock-timeout"] = HealthCheckResult.Degraded(
                            "Health check lock timeout - another check is in progress")
                    },
                    Timestamp = DateTime.UtcNow
                };
            }

            try
            {
                // Run all checks in parallel
                var tasks = new List<Task<(string Name, HealthCheckResult Result)>>
                {
                    CheckMemoryPressureAsync(ct),
                    CheckThreadPoolAsync(ct),
                    CheckPluginRegistryAsync(ct)
                };

                // Add plugin health checks if enabled
                if (_config.AggregatePluginHealth)
                {
                    tasks.AddRange(GetPluginHealthCheckTasks(ct));
                }

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);

                foreach (var (name, result) in results)
                {
                    entries[name] = result;
                    _lastResults[name] = result;
                }

                sw.Stop();

                var overallStatus = DetermineOverallStatus(entries.Values);

                var report = new HealthReport
                {
                    Status = overallStatus,
                    TotalDuration = sw.Elapsed,
                    Entries = entries,
                    Timestamp = DateTime.UtcNow
                };

                // Check for status change and raise event
                CheckAndRaiseStatusChange(report);

                lock (_stateLock)
                {
                    _lastReport = report;
                    _lastOverallStatus = overallStatus;
                }

                return report;
            }
            finally
            {
                _checkLock.Release();
            }
        }

        #endregion

        #region Component Health Checks

        private async Task<(string Name, HealthCheckResult Result)> CheckMemoryPressureAsync(CancellationToken ct)
        {
            var checkSw = Stopwatch.StartNew();

            try
            {
                await Task.Yield(); // Ensure async execution

                var gcInfo = GC.GetGCMemoryInfo();
                var totalMemory = GC.GetTotalMemory(false);
                var memoryLoadBytes = gcInfo.MemoryLoadBytes;
                var highMemoryThreshold = gcInfo.HighMemoryLoadThresholdBytes;

                // Calculate memory load percentage
                double memoryLoadPercent = highMemoryThreshold > 0
                    ? (double)memoryLoadBytes / highMemoryThreshold
                    : 0;

                // Get fragmentation info
                var fragmentedBytes = gcInfo.FragmentedBytes;
                var heapSize = gcInfo.HeapSizeBytes;

                var data = new Dictionary<string, object>
                {
                    ["memoryLoadPercent"] = memoryLoadPercent * 100,
                    ["memoryLoadBytes"] = memoryLoadBytes,
                    ["highMemoryThreshold"] = highMemoryThreshold,
                    ["totalManagedMemory"] = totalMemory,
                    ["heapSizeBytes"] = heapSize,
                    ["fragmentedBytes"] = fragmentedBytes,
                    ["gen0Collections"] = GC.CollectionCount(0),
                    ["gen1Collections"] = GC.CollectionCount(1),
                    ["gen2Collections"] = GC.CollectionCount(2),
                    ["gcLatencyMode"] = GCSettings.LatencyMode.ToString()
                };

                checkSw.Stop();

                if (memoryLoadPercent >= _config.MemoryUnhealthyThreshold)
                {
                    return ("memory-pressure", new HealthCheckResult
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = $"Critical memory pressure: {memoryLoadPercent:P1} of threshold",
                        Duration = checkSw.Elapsed,
                        Data = data
                    });
                }

                if (memoryLoadPercent >= _config.MemoryDegradedThreshold)
                {
                    return ("memory-pressure", new HealthCheckResult
                    {
                        Status = HealthStatus.Degraded,
                        Message = $"High memory usage: {memoryLoadPercent:P1} of threshold",
                        Duration = checkSw.Elapsed,
                        Data = data
                    });
                }

                return ("memory-pressure", new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    Message = $"Memory OK: {memoryLoadPercent:P1} of threshold",
                    Duration = checkSw.Elapsed,
                    Data = data
                });
            }
            catch (Exception ex)
            {
                checkSw.Stop();
                return ("memory-pressure", new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Memory check failed: {ex.Message}",
                    Duration = checkSw.Elapsed,
                    Exception = ex,
                    Data = new Dictionary<string, object> { ["error"] = ex.Message }
                });
            }
        }

        private async Task<(string Name, HealthCheckResult Result)> CheckThreadPoolAsync(CancellationToken ct)
        {
            var checkSw = Stopwatch.StartNew();

            try
            {
                await Task.Yield(); // Ensure async execution

                ThreadPool.GetAvailableThreads(out int availableWorkerThreads, out int availableCompletionThreads);
                ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxCompletionThreads);
                ThreadPool.GetMinThreads(out int minWorkerThreads, out int minCompletionThreads);

                // Calculate utilization (1 - available/max)
                double workerUtilization = maxWorkerThreads > 0
                    ? 1.0 - ((double)availableWorkerThreads / maxWorkerThreads)
                    : 0;

                double ioUtilization = maxCompletionThreads > 0
                    ? 1.0 - ((double)availableCompletionThreads / maxCompletionThreads)
                    : 0;

                // Get pending work item count
                var pendingWorkItems = ThreadPool.PendingWorkItemCount;
                var completedWorkItems = ThreadPool.CompletedWorkItemCount;

                var data = new Dictionary<string, object>
                {
                    ["availableWorkerThreads"] = availableWorkerThreads,
                    ["availableCompletionThreads"] = availableCompletionThreads,
                    ["maxWorkerThreads"] = maxWorkerThreads,
                    ["maxCompletionThreads"] = maxCompletionThreads,
                    ["minWorkerThreads"] = minWorkerThreads,
                    ["minCompletionThreads"] = minCompletionThreads,
                    ["workerUtilization"] = workerUtilization * 100,
                    ["ioUtilization"] = ioUtilization * 100,
                    ["pendingWorkItems"] = pendingWorkItems,
                    ["completedWorkItems"] = completedWorkItems,
                    ["threadCount"] = ThreadPool.ThreadCount
                };

                checkSw.Stop();

                // Use the higher utilization for status determination
                var maxUtilization = Math.Max(workerUtilization, ioUtilization);

                if (maxUtilization >= _config.ThreadPoolUnhealthyThreshold)
                {
                    return ("thread-pool", new HealthCheckResult
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = $"Thread pool exhausted: {maxUtilization:P1} utilized",
                        Duration = checkSw.Elapsed,
                        Data = data
                    });
                }

                if (maxUtilization >= _config.ThreadPoolDegradedThreshold)
                {
                    return ("thread-pool", new HealthCheckResult
                    {
                        Status = HealthStatus.Degraded,
                        Message = $"High thread pool usage: {maxUtilization:P1} utilized",
                        Duration = checkSw.Elapsed,
                        Data = data
                    });
                }

                return ("thread-pool", new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    Message = $"Thread pool OK: {maxUtilization:P1} utilized, {pendingWorkItems} pending",
                    Duration = checkSw.Elapsed,
                    Data = data
                });
            }
            catch (Exception ex)
            {
                checkSw.Stop();
                return ("thread-pool", new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Thread pool check failed: {ex.Message}",
                    Duration = checkSw.Elapsed,
                    Exception = ex,
                    Data = new Dictionary<string, object> { ["error"] = ex.Message }
                });
            }
        }

        private async Task<(string Name, HealthCheckResult Result)> CheckPluginRegistryAsync(CancellationToken ct)
        {
            var checkSw = Stopwatch.StartNew();

            try
            {
                await Task.Yield(); // Ensure async execution

                var allPlugins = _pluginRegistry.GetAll().ToList();
                var pluginCount = allPlugins.Count;
                var pluginIds = allPlugins.Select(p => p.Id).ToList();
                var categorySummary = _pluginRegistry.GetCategorySummary();

                var data = new Dictionary<string, object>
                {
                    ["pluginCount"] = pluginCount,
                    ["pluginIds"] = pluginIds,
                    ["categorySummary"] = categorySummary.ToDictionary(
                        k => k.Key.ToString(),
                        v => (object)v.Value),
                    ["operatingMode"] = _pluginRegistry.OperatingMode.ToString()
                };

                checkSw.Stop();

                if (pluginCount == 0)
                {
                    return ("plugin-registry", new HealthCheckResult
                    {
                        Status = HealthStatus.Degraded,
                        Message = "No plugins registered",
                        Duration = checkSw.Elapsed,
                        Data = data
                    });
                }

                return ("plugin-registry", new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    Message = $"Plugin registry OK: {pluginCount} plugins registered",
                    Duration = checkSw.Elapsed,
                    Data = data
                });
            }
            catch (Exception ex)
            {
                checkSw.Stop();
                return ("plugin-registry", new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Plugin registry check failed: {ex.Message}",
                    Duration = checkSw.Elapsed,
                    Exception = ex,
                    Data = new Dictionary<string, object> { ["error"] = ex.Message }
                });
            }
        }

        #endregion

        #region Plugin Health Aggregation

        private IEnumerable<Task<(string Name, HealthCheckResult Result)>> GetPluginHealthCheckTasks(CancellationToken ct)
        {
            var healthCheckPlugins = _pluginRegistry.GetAll()
                .OfType<IHealthCheck>()
                .ToList();

            foreach (var plugin in healthCheckPlugins)
            {
                yield return CheckPluginHealthAsync(plugin, ct);
            }
        }

        private async Task<(string Name, HealthCheckResult Result)> CheckPluginHealthAsync(
            IHealthCheck plugin,
            CancellationToken ct)
        {
            var name = $"plugin:{plugin.Name}";
            var checkSw = Stopwatch.StartNew();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.CheckTimeout);

                var result = await plugin.CheckHealthAsync(timeoutCts.Token).ConfigureAwait(false);

                checkSw.Stop();

                // Ensure duration is set
                return (name, new HealthCheckResult
                {
                    Status = result.Status,
                    Message = result.Message,
                    Duration = result.Duration > TimeSpan.Zero ? result.Duration : checkSw.Elapsed,
                    Data = result.Data,
                    Exception = result.Exception
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                checkSw.Stop();
                return (name, new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Plugin health check timed out after {_config.CheckTimeout.TotalSeconds}s",
                    Duration = checkSw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["timeout"] = _config.CheckTimeout.TotalSeconds,
                        ["pluginName"] = plugin.Name
                    }
                });
            }
            catch (Exception ex)
            {
                checkSw.Stop();
                return (name, new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Plugin health check failed: {ex.Message}",
                    Duration = checkSw.Elapsed,
                    Exception = ex,
                    Data = new Dictionary<string, object>
                    {
                        ["error"] = ex.Message,
                        ["pluginName"] = plugin.Name
                    }
                });
            }
        }

        #endregion

        #region Background Monitoring

        private void OnBackgroundCheck(object? state)
        {
            if (_isDisposed || !_isRunning)
            {
                return;
            }

            var cancellationToken = _stoppingCts?.Token ?? CancellationToken.None;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Fire-and-forget, but handle errors
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckHealthInternalAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    // Log but don't crash
                    System.Diagnostics.Debug.WriteLine(
                        $"[HealthCheckManager] Background check failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        #endregion

        #region Status Helpers

        private HealthStatus DetermineOverallStatus(IEnumerable<HealthCheckResult> results)
        {
            var resultList = results.ToList();

            if (_config.FailFastOnUnhealthy)
            {
                if (resultList.Any(r => r.Status == HealthStatus.Unhealthy))
                {
                    return HealthStatus.Unhealthy;
                }
            }

            // Count by status
            var unhealthyCount = resultList.Count(r => r.Status == HealthStatus.Unhealthy);
            var degradedCount = resultList.Count(r => r.Status == HealthStatus.Degraded);

            // If any component is unhealthy, system is unhealthy
            if (unhealthyCount > 0)
            {
                return HealthStatus.Unhealthy;
            }

            // If any component is degraded, system is degraded
            if (degradedCount > 0)
            {
                return HealthStatus.Degraded;
            }

            return HealthStatus.Healthy;
        }

        private void CheckAndRaiseStatusChange(HealthReport report)
        {
            HealthStatus previousOverall;
            var changedComponents = new List<string>();

            lock (_stateLock)
            {
                previousOverall = _lastOverallStatus;

                // Check each component for changes
                foreach (var entry in report.Entries)
                {
                    if (_previousComponentStatuses.TryGetValue(entry.Key, out var previousStatus))
                    {
                        if (previousStatus != entry.Value.Status)
                        {
                            changedComponents.Add(entry.Key);
                        }
                    }
                    else
                    {
                        // New component
                        changedComponents.Add(entry.Key);
                    }

                    _previousComponentStatuses[entry.Key] = entry.Value.Status;
                }
            }

            // Raise event if overall status changed
            if (previousOverall != report.Status || changedComponents.Count > 0)
            {
                var args = new HealthChangedEventArgs
                {
                    PreviousStatus = previousOverall,
                    CurrentStatus = report.Status,
                    Report = report,
                    Timestamp = DateTime.UtcNow,
                    ChangedComponents = changedComponents.AsReadOnly()
                };

                try
                {
                    OnHealthChanged?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    // Don't let event handlers crash the health check
                    System.Diagnostics.Debug.WriteLine(
                        $"[HealthCheckManager] OnHealthChanged handler threw: {ex.Message}");
                }
            }
        }

        private static string GenerateHealthMessage(HealthReport report)
        {
            var unhealthyComponents = report.Entries
                .Where(e => e.Value.Status == HealthStatus.Unhealthy)
                .Select(e => e.Key)
                .ToList();

            var degradedComponents = report.Entries
                .Where(e => e.Value.Status == HealthStatus.Degraded)
                .Select(e => e.Key)
                .ToList();

            return report.Status switch
            {
                HealthStatus.Healthy => $"All {report.Entries.Count} components healthy",
                HealthStatus.Degraded => $"Degraded: {string.Join(", ", degradedComponents)}",
                HealthStatus.Unhealthy => $"Unhealthy: {string.Join(", ", unhealthyComponents)}",
                _ => "Unknown health status"
            };
        }

        #endregion

        #region Disposal

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(HealthCheckManager));
            }
        }

        /// <summary>
        /// Disposes resources used by the health check manager.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isRunning = false;

            _stoppingCts?.Cancel();
            _backgroundTimer?.Dispose();
            _stoppingCts?.Dispose();
            _checkLock.Dispose();
        }

        /// <summary>
        /// Asynchronously disposes resources used by the health check manager.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isRunning = false;

            _stoppingCts?.Cancel();

            if (_backgroundTimer != null)
            {
                await _backgroundTimer.DisposeAsync().ConfigureAwait(false);
            }

            _stoppingCts?.Dispose();
            _checkLock.Dispose();
        }

        #endregion
    }
}
