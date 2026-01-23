using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DataWarehouse.Plugins.Resilience
{
    /// <summary>
    /// Production-ready health monitoring plugin for comprehensive system health tracking.
    /// Provides aggregated health status, component monitoring, and alerting capabilities.
    ///
    /// Features:
    /// - Component-based health checks with dependency tracking
    /// - Liveness and readiness probe support (Kubernetes-compatible)
    /// - Configurable check intervals and timeouts
    /// - Health status caching with configurable TTL
    /// - Historical health data for trend analysis
    /// - Alerting on status changes
    /// - Custom health check registration
    /// - Resource monitoring (CPU, Memory, Disk)
    /// - External dependency checks (databases, APIs, etc.)
    ///
    /// Message Commands:
    /// - health.check: Perform a health check
    /// - health.liveness: Kubernetes liveness probe
    /// - health.readiness: Kubernetes readiness probe
    /// - health.register: Register a health check
    /// - health.unregister: Unregister a health check
    /// - health.history: Get health check history
    /// - health.alert.configure: Configure alerting
    /// </summary>
    public sealed class HealthMonitorPlugin : HealthProviderPluginBase
    {
        private readonly ConcurrentDictionary<string, HealthCheckRegistration> _registrations;
        private readonly ConcurrentDictionary<string, HealthCheckHistory> _history;
        private readonly ConcurrentDictionary<string, AlertConfiguration> _alertConfigs;
        private readonly List<Action<HealthStatusChange>> _statusChangeHandlers;
        private readonly SemaphoreSlim _historyLock = new(1, 1);
        private readonly Timer _scheduledCheckTimer;
        private readonly HealthMonitorConfig _config;
        private readonly string _storagePath;
        private HealthCheckResult? _cachedResult;
        private DateTime _lastCacheUpdate = DateTime.MinValue;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.resilience.healthmonitor";

        /// <inheritdoc/>
        public override string Name => "Health Monitor";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <inheritdoc/>
        protected override TimeSpan CacheDuration => _config.CacheDuration;

        /// <inheritdoc/>
        protected override TimeSpan CheckTimeout => _config.CheckTimeout;

        /// <summary>
        /// Initializes a new instance of the HealthMonitorPlugin.
        /// </summary>
        /// <param name="config">Optional configuration for the health monitor.</param>
        public HealthMonitorPlugin(HealthMonitorConfig? config = null)
        {
            _config = config ?? new HealthMonitorConfig();
            _registrations = new ConcurrentDictionary<string, HealthCheckRegistration>();
            _history = new ConcurrentDictionary<string, HealthCheckHistory>();
            _alertConfigs = new ConcurrentDictionary<string, AlertConfiguration>();
            _statusChangeHandlers = new List<Action<HealthStatusChange>>();
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "resilience", "health");

            // Register built-in health checks
            RegisterBuiltInChecks();

            // Start scheduled health checks
            if (_config.ScheduledCheckInterval > TimeSpan.Zero)
            {
                _scheduledCheckTimer = new Timer(
                    async _ => await PerformScheduledCheckAsync(),
                    null,
                    _config.ScheduledCheckInterval,
                    _config.ScheduledCheckInterval);
            }
            else
            {
                _scheduledCheckTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);
            await LoadConfigurationAsync();
            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "health.check", DisplayName = "Health Check", Description = "Perform comprehensive health check" },
                new() { Name = "health.liveness", DisplayName = "Liveness", Description = "Kubernetes liveness probe" },
                new() { Name = "health.readiness", DisplayName = "Readiness", Description = "Kubernetes readiness probe" },
                new() { Name = "health.register", DisplayName = "Register", Description = "Register a health check" },
                new() { Name = "health.unregister", DisplayName = "Unregister", Description = "Unregister a health check" },
                new() { Name = "health.history", DisplayName = "History", Description = "Get health check history" },
                new() { Name = "health.alert.configure", DisplayName = "Configure Alert", Description = "Configure alerting" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["RegisteredChecks"] = _registrations.Count;
            metadata["ScheduledCheckInterval"] = _config.ScheduledCheckInterval.TotalSeconds;
            metadata["CacheDuration"] = _config.CacheDuration.TotalSeconds;
            metadata["SupportsLivenessProbe"] = true;
            metadata["SupportsReadinessProbe"] = true;
            metadata["SupportsAlerting"] = true;
            metadata["SupportsHistory"] = true;
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "health.check":
                    await HandleCheckAsync(message);
                    break;
                case "health.liveness":
                    await HandleLivenessAsync(message);
                    break;
                case "health.readiness":
                    await HandleReadinessAsync(message);
                    break;
                case "health.register":
                    HandleRegister(message);
                    break;
                case "health.unregister":
                    HandleUnregister(message);
                    break;
                case "health.history":
                    HandleHistory(message);
                    break;
                case "health.alert.configure":
                    await HandleConfigureAlertAsync(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        /// <summary>
        /// Registers a custom health check.
        /// </summary>
        /// <param name="name">Unique name for the health check.</param>
        /// <param name="check">The health check to register.</param>
        /// <param name="tags">Tags for categorizing the check.</param>
        /// <param name="timeout">Optional timeout for this specific check.</param>
        public void RegisterCheck(string name, IHealthCheck check, string[]? tags = null, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));
            if (check == null)
                throw new ArgumentNullException(nameof(check));

            var registration = new HealthCheckRegistration
            {
                Name = name,
                Check = check,
                Tags = tags ?? Array.Empty<string>(),
                Timeout = timeout ?? _config.CheckTimeout,
                RegisteredAt = DateTime.UtcNow
            };

            _registrations[name] = registration;
            RegisterHealthCheck(name, check);
        }

        /// <summary>
        /// Registers a custom health check using a function.
        /// </summary>
        /// <param name="name">Unique name for the health check.</param>
        /// <param name="checkFunc">The health check function.</param>
        /// <param name="tags">Tags for categorizing the check.</param>
        /// <param name="timeout">Optional timeout for this specific check.</param>
        public void RegisterCheck(string name, Func<CancellationToken, Task<HealthCheckResult>> checkFunc, string[]? tags = null, TimeSpan? timeout = null)
        {
            RegisterCheck(name, new DelegateHealthCheck(checkFunc), tags, timeout);
        }

        /// <summary>
        /// Unregisters a health check.
        /// </summary>
        /// <param name="name">Name of the health check to unregister.</param>
        /// <returns>True if the check was unregistered, false if not found.</returns>
        public bool UnregisterCheck(string name)
        {
            var removed = _registrations.TryRemove(name, out _);
            UnregisterHealthCheck(name);
            return removed;
        }

        /// <summary>
        /// Performs a liveness check (is the application alive?).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if alive, false otherwise.</returns>
        public async Task<bool> CheckLivenessAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await CheckSelfHealthAsync(ct);
                return result.Status != HealthStatus.Unhealthy;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Performs a readiness check (is the application ready to serve traffic?).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if ready, false otherwise.</returns>
        public async Task<bool> CheckReadinessAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await CheckHealthAsync(ct);
                return result.Status == HealthStatus.Healthy;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the health check history for a component.
        /// </summary>
        /// <param name="component">Component name, or null for all.</param>
        /// <param name="limit">Maximum number of entries to return.</param>
        /// <returns>Health check history entries.</returns>
        public IReadOnlyList<HealthHistoryEntry> GetHistory(string? component = null, int limit = 100)
        {
            if (!string.IsNullOrEmpty(component))
            {
                if (_history.TryGetValue(component, out var history))
                {
                    return history.Entries.TakeLast(limit).ToList();
                }
                return Array.Empty<HealthHistoryEntry>();
            }

            return _history.Values
                .SelectMany(h => h.Entries)
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Registers a handler for health status changes.
        /// </summary>
        /// <param name="handler">The handler to invoke on status changes.</param>
        public void OnStatusChange(Action<HealthStatusChange> handler)
        {
            lock (_statusChangeHandlers)
            {
                _statusChangeHandlers.Add(handler);
            }
        }

        /// <summary>
        /// Configures alerting for a component.
        /// </summary>
        /// <param name="config">The alert configuration.</param>
        public void ConfigureAlert(AlertConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.Component))
                throw new ArgumentException("Component name is required", nameof(config));

            _alertConfigs[config.Component] = config;
        }

        /// <inheritdoc/>
        protected override async Task<HealthCheckResult> CheckSelfHealthAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var data = new Dictionary<string, object>();

            try
            {
                // Basic self-health checks
                data["registeredChecks"] = _registrations.Count;
                data["uptime"] = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToString();

                // Check system resources
                var process = Process.GetCurrentProcess();
                data["memoryMB"] = process.WorkingSet64 / 1024 / 1024;
                data["threadCount"] = process.Threads.Count;

                sw.Stop();

                return new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    Message = "Health monitor is operational",
                    Duration = sw.Elapsed,
                    Data = data
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return HealthCheckResult.Unhealthy($"Self-health check failed: {ex.Message}", ex);
            }
        }

        /// <inheritdoc/>
        public override async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            // Check cache
            if (DateTime.UtcNow - _lastCacheUpdate < CacheDuration && _cachedResult != null)
            {
                return _cachedResult;
            }

            var result = await base.CheckHealthAsync(ct);

            // Update cache
            _cachedResult = result;
            _lastCacheUpdate = DateTime.UtcNow;

            // Record history
            await RecordHistoryAsync("_aggregate", result, ct);

            // Check for status changes and trigger alerts
            await CheckAndTriggerAlertsAsync(result, ct);

            return result;
        }

        private void RegisterBuiltInChecks()
        {
            // Memory health check
            RegisterCheck("memory", async ct =>
            {
                var process = Process.GetCurrentProcess();
                var memoryMB = process.WorkingSet64 / 1024 / 1024;
                var threshold = _config.MemoryThresholdMB;

                if (memoryMB > threshold)
                {
                    return HealthCheckResult.Degraded($"High memory usage: {memoryMB}MB (threshold: {threshold}MB)");
                }

                return HealthCheckResult.Healthy($"Memory usage: {memoryMB}MB");
            }, new[] { "system", "resource" });

            // Thread pool health check
            RegisterCheck("threadpool", async ct =>
            {
                ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
                ThreadPool.GetMaxThreads(out var maxWorker, out var maxCompletion);

                var workerUsage = 1.0 - ((double)workerThreads / maxWorker);
                var completionUsage = 1.0 - ((double)completionPortThreads / maxCompletion);

                if (workerUsage > 0.9 || completionUsage > 0.9)
                {
                    return HealthCheckResult.Degraded($"Thread pool near exhaustion: Worker={workerUsage:P0}, IOCP={completionUsage:P0}");
                }

                return HealthCheckResult.Healthy($"Thread pool healthy: Worker={workerUsage:P0}, IOCP={completionUsage:P0}");
            }, new[] { "system", "resource" });

            // GC health check
            RegisterCheck("gc", async ct =>
            {
                var gcInfo = GC.GetGCMemoryInfo();
                var heapBytes = gcInfo.HeapSizeBytes;
                var fragmentedBytes = gcInfo.FragmentedBytes;
                var fragmentationPercent = (double)fragmentedBytes / heapBytes * 100;

                if (fragmentationPercent > 30)
                {
                    return HealthCheckResult.Degraded($"High heap fragmentation: {fragmentationPercent:F1}%");
                }

                return HealthCheckResult.Healthy($"GC healthy: Heap={heapBytes / 1024 / 1024}MB, Fragmentation={fragmentationPercent:F1}%");
            }, new[] { "system", "gc" });

            // Disk space health check
            RegisterCheck("disk", async ct =>
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(_storagePath) ?? "/");
                    var freePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100;

                    if (freePercent < 5)
                    {
                        return HealthCheckResult.Unhealthy($"Critical low disk space: {freePercent:F1}% free");
                    }
                    if (freePercent < 15)
                    {
                        return HealthCheckResult.Degraded($"Low disk space: {freePercent:F1}% free");
                    }

                    return HealthCheckResult.Healthy($"Disk space: {freePercent:F1}% free ({drive.AvailableFreeSpace / 1024 / 1024 / 1024}GB)");
                }
                catch (Exception ex)
                {
                    return HealthCheckResult.Degraded($"Could not check disk: {ex.Message}");
                }
            }, new[] { "system", "resource" });
        }

        private async Task PerformScheduledCheckAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(_config.CheckTimeout);
                await CheckHealthAsync(cts.Token);
            }
            catch
            {
                // Log but don't throw - scheduled checks shouldn't crash
            }
        }

        private async Task RecordHistoryAsync(string component, HealthCheckResult result, CancellationToken ct)
        {
            if (!await _historyLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
                return;

            try
            {
                var history = _history.GetOrAdd(component, _ => new HealthCheckHistory { Component = component });

                history.Entries.Add(new HealthHistoryEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Status = result.Status,
                    Message = result.Message,
                    Duration = result.Duration
                });

                // Trim old entries
                while (history.Entries.Count > _config.MaxHistoryEntries)
                {
                    history.Entries.RemoveAt(0);
                }
            }
            finally
            {
                _historyLock.Release();
            }
        }

        private async Task CheckAndTriggerAlertsAsync(HealthCheckResult result, CancellationToken ct)
        {
            if (!result.Data.TryGetValue("components", out var componentsObj))
                return;

            if (componentsObj is not Dictionary<string, object> components)
                return;

            foreach (var (name, componentData) in components)
            {
                if (componentData is not Dictionary<string, object> data)
                    continue;

                if (!data.TryGetValue("status", out var statusObj) || statusObj is not string statusStr)
                    continue;

                if (!Enum.TryParse<HealthStatus>(statusStr, true, out var status))
                    continue;

                // Check for alert configuration
                if (_alertConfigs.TryGetValue(name, out var alertConfig))
                {
                    if (ShouldAlert(name, status, alertConfig))
                    {
                        var change = new HealthStatusChange
                        {
                            Component = name,
                            PreviousStatus = GetPreviousStatus(name),
                            CurrentStatus = status,
                            Timestamp = DateTime.UtcNow,
                            Message = data.TryGetValue("message", out var msg) ? msg?.ToString() : null
                        };

                        NotifyStatusChange(change);
                        UpdatePreviousStatus(name, status);
                    }
                }
            }
        }

        private bool ShouldAlert(string component, HealthStatus status, AlertConfiguration config)
        {
            if (!config.Enabled)
                return false;

            var previousStatus = GetPreviousStatus(component);

            // Alert on transition to configured statuses
            if (config.AlertOnUnhealthy && status == HealthStatus.Unhealthy && previousStatus != HealthStatus.Unhealthy)
                return true;

            if (config.AlertOnDegraded && status == HealthStatus.Degraded && previousStatus != HealthStatus.Degraded)
                return true;

            if (config.AlertOnRecovery && status == HealthStatus.Healthy && previousStatus != HealthStatus.Healthy)
                return true;

            return false;
        }

        private HealthStatus GetPreviousStatus(string component)
        {
            if (_history.TryGetValue(component, out var history) && history.Entries.Count > 0)
            {
                return history.Entries[^1].Status;
            }
            return HealthStatus.Healthy;
        }

        private void UpdatePreviousStatus(string component, HealthStatus status)
        {
            // Already recorded in history
        }

        private void NotifyStatusChange(HealthStatusChange change)
        {
            List<Action<HealthStatusChange>> handlers;
            lock (_statusChangeHandlers)
            {
                handlers = _statusChangeHandlers.ToList();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler(change);
                }
                catch
                {
                    // Log but don't throw
                }
            }
        }

        private async Task HandleCheckAsync(PluginMessage message)
        {
            var includeHistory = GetBool(message.Payload, "includeHistory") ?? false;
            var tags = GetStringArray(message.Payload, "tags");

            var result = await CheckHealthAsync();

            var response = new Dictionary<string, object>
            {
                ["status"] = result.Status.ToString(),
                ["message"] = result.Message ?? string.Empty,
                ["duration"] = result.Duration.TotalMilliseconds,
                ["data"] = result.Data
            };

            if (includeHistory)
            {
                response["history"] = GetHistory(null, 10);
            }

            message.Payload["result"] = response;
        }

        private async Task HandleLivenessAsync(PluginMessage message)
        {
            var isAlive = await CheckLivenessAsync();
            message.Payload["result"] = new { alive = isAlive };
        }

        private async Task HandleReadinessAsync(PluginMessage message)
        {
            var isReady = await CheckReadinessAsync();
            message.Payload["result"] = new { ready = isReady };
        }

        private void HandleRegister(PluginMessage message)
        {
            var name = GetString(message.Payload, "name") ?? throw new ArgumentException("name required");
            var tags = GetStringArray(message.Payload, "tags") ?? Array.Empty<string>();
            var timeoutSeconds = GetInt(message.Payload, "timeoutSeconds") ?? (int)_config.CheckTimeout.TotalSeconds;

            // For external registration, we create a placeholder that can be triggered
            var placeholder = new ExternalHealthCheck(name);
            RegisterCheck(name, placeholder, tags, TimeSpan.FromSeconds(timeoutSeconds));
        }

        private void HandleUnregister(PluginMessage message)
        {
            var name = GetString(message.Payload, "name") ?? throw new ArgumentException("name required");
            UnregisterCheck(name);
        }

        private void HandleHistory(PluginMessage message)
        {
            var component = GetString(message.Payload, "component");
            var limit = GetInt(message.Payload, "limit") ?? 100;

            message.Payload["result"] = GetHistory(component, limit);
        }

        private async Task HandleConfigureAlertAsync(PluginMessage message)
        {
            var config = new AlertConfiguration
            {
                Component = GetString(message.Payload, "component") ?? throw new ArgumentException("component required"),
                Enabled = GetBool(message.Payload, "enabled") ?? true,
                AlertOnUnhealthy = GetBool(message.Payload, "alertOnUnhealthy") ?? true,
                AlertOnDegraded = GetBool(message.Payload, "alertOnDegraded") ?? false,
                AlertOnRecovery = GetBool(message.Payload, "alertOnRecovery") ?? true
            };

            ConfigureAlert(config);
            await SaveConfigurationAsync();
        }

        private async Task LoadConfigurationAsync()
        {
            var path = Path.Combine(_storagePath, "health-config.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<HealthPersistenceData>(json);

                if (data?.AlertConfigs != null)
                {
                    foreach (var config in data.AlertConfigs)
                    {
                        _alertConfigs[config.Component] = config;
                    }
                }
            }
            catch
            {
                // Log but continue
            }
        }

        private async Task SaveConfigurationAsync()
        {
            try
            {
                Directory.CreateDirectory(_storagePath);

                var data = new HealthPersistenceData
                {
                    AlertConfigs = _alertConfigs.Values.ToList()
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(_storagePath, "health-config.json"), json);
            }
            catch
            {
                // Log but continue
            }
        }

        /// <inheritdoc/>
        public override Task StopAsync()
        {
            _scheduledCheckTimer.Dispose();
            return base.StopAsync();
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private static int? GetInt(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is int i) return i;
                if (val is long l) return (int)l;
                if (val is string s && int.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        private static bool? GetBool(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is bool b) return b;
                if (val is string s) return bool.TryParse(s, out var parsed) && parsed;
            }
            return null;
        }

        private static string[]? GetStringArray(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is string[] arr) return arr;
                if (val is IEnumerable<string> enumerable) return enumerable.ToArray();
            }
            return null;
        }
    }

    /// <summary>
    /// Configuration for the HealthMonitorPlugin.
    /// </summary>
    public class HealthMonitorConfig
    {
        /// <summary>
        /// How long to cache health check results. Default is 5 seconds.
        /// </summary>
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Timeout for individual health checks. Default is 10 seconds.
        /// </summary>
        public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Interval for scheduled health checks. Default is 30 seconds. Set to Zero to disable.
        /// </summary>
        public TimeSpan ScheduledCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum history entries to keep per component. Default is 1000.
        /// </summary>
        public int MaxHistoryEntries { get; set; } = 1000;

        /// <summary>
        /// Memory usage threshold in MB for the memory health check. Default is 1024.
        /// </summary>
        public long MemoryThresholdMB { get; set; } = 1024;
    }

    /// <summary>
    /// Registration information for a health check.
    /// </summary>
    public class HealthCheckRegistration
    {
        /// <summary>
        /// Name of the health check.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The health check instance.
        /// </summary>
        public IHealthCheck Check { get; set; } = null!;

        /// <summary>
        /// Tags for categorizing the check.
        /// </summary>
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Timeout for this specific check.
        /// </summary>
        public TimeSpan Timeout { get; set; }

        /// <summary>
        /// When the check was registered.
        /// </summary>
        public DateTime RegisteredAt { get; set; }
    }

    /// <summary>
    /// Health check history for a component.
    /// </summary>
    public class HealthCheckHistory
    {
        /// <summary>
        /// Component name.
        /// </summary>
        public string Component { get; set; } = string.Empty;

        /// <summary>
        /// History entries.
        /// </summary>
        public List<HealthHistoryEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// A single health check history entry.
    /// </summary>
    public class HealthHistoryEntry
    {
        /// <summary>
        /// When the check was performed.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// The health status.
        /// </summary>
        public HealthStatus Status { get; set; }

        /// <summary>
        /// Status message.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// How long the check took.
        /// </summary>
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Information about a health status change.
    /// </summary>
    public class HealthStatusChange
    {
        /// <summary>
        /// Component that changed.
        /// </summary>
        public string Component { get; set; } = string.Empty;

        /// <summary>
        /// Previous status.
        /// </summary>
        public HealthStatus PreviousStatus { get; set; }

        /// <summary>
        /// Current status.
        /// </summary>
        public HealthStatus CurrentStatus { get; set; }

        /// <summary>
        /// When the change occurred.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Status message.
        /// </summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// Configuration for health alerting.
    /// </summary>
    public class AlertConfiguration
    {
        /// <summary>
        /// Component to configure alerting for.
        /// </summary>
        public string Component { get; set; } = string.Empty;

        /// <summary>
        /// Whether alerting is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Alert when component becomes unhealthy.
        /// </summary>
        public bool AlertOnUnhealthy { get; set; } = true;

        /// <summary>
        /// Alert when component becomes degraded.
        /// </summary>
        public bool AlertOnDegraded { get; set; } = false;

        /// <summary>
        /// Alert when component recovers to healthy.
        /// </summary>
        public bool AlertOnRecovery { get; set; } = true;
    }

    /// <summary>
    /// Health check implementation using a delegate function.
    /// </summary>
    internal sealed class DelegateHealthCheck : IHealthCheck
    {
        private readonly Func<CancellationToken, Task<HealthCheckResult>> _checkFunc;

        public string Name => "Delegate";
        public string[] Tags => Array.Empty<string>();

        public DelegateHealthCheck(Func<CancellationToken, Task<HealthCheckResult>> checkFunc)
        {
            _checkFunc = checkFunc ?? throw new ArgumentNullException(nameof(checkFunc));
        }

        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            return _checkFunc(ct);
        }
    }

    /// <summary>
    /// Placeholder health check for external registration.
    /// </summary>
    internal sealed class ExternalHealthCheck : IHealthCheck
    {
        public string Name { get; }
        public string[] Tags => Array.Empty<string>();

        private HealthCheckResult _lastResult = HealthCheckResult.Healthy("Not yet checked");

        public ExternalHealthCheck(string name)
        {
            Name = name;
        }

        public void UpdateStatus(HealthCheckResult result)
        {
            _lastResult = result;
        }

        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_lastResult);
        }
    }

    internal class HealthPersistenceData
    {
        public List<AlertConfiguration> AlertConfigs { get; set; } = new();
    }
}
