using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure
{
    #region Self-Healing Plugin Framework

    /// <summary>
    /// Health status for self-healing components.
    /// </summary>
    public enum ComponentHealth
    {
        /// <summary>Component is operating normally.</summary>
        Healthy,
        /// <summary>Component is degraded but functional.</summary>
        Degraded,
        /// <summary>Component is unhealthy and needs intervention.</summary>
        Unhealthy,
        /// <summary>Component is recovering from a failure.</summary>
        Recovering,
        /// <summary>Component is dead and needs restart.</summary>
        Dead
    }

    /// <summary>
    /// Represents a healing action that can be taken to recover a component.
    /// </summary>
    public sealed class HealingAction
    {
        /// <summary>Unique identifier for this action.</summary>
        public string ActionId { get; init; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>Human-readable description of the action.</summary>
        public required string Description { get; init; }

        /// <summary>Priority (lower = higher priority).</summary>
        public int Priority { get; init; } = 100;

        /// <summary>Maximum number of retry attempts.</summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>Delay between retries.</summary>
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>The healing function to execute.</summary>
        public required Func<CancellationToken, Task<bool>> Execute { get; init; }
    }

    /// <summary>
    /// Result of a healing attempt.
    /// </summary>
    public sealed class HealingResult
    {
        /// <summary>Whether the healing was successful.</summary>
        public bool Success { get; init; }

        /// <summary>The action that was executed.</summary>
        public required HealingAction Action { get; init; }

        /// <summary>Number of attempts made.</summary>
        public int Attempts { get; init; }

        /// <summary>Time taken to heal.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>When the healing completed.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Interface for components that support self-healing.
    /// </summary>
    public interface ISelfHealingComponent
    {
        /// <summary>Gets the component identifier.</summary>
        string ComponentId { get; }

        /// <summary>Gets the current health status.</summary>
        ComponentHealth Health { get; }

        /// <summary>Gets available healing actions for this component.</summary>
        IEnumerable<HealingAction> GetHealingActions();

        /// <summary>Performs a health check.</summary>
        Task<ComponentHealth> CheckHealthAsync(CancellationToken ct = default);

        /// <summary>Event raised when health changes.</summary>
        event Action<ComponentHealth>? OnHealthChanged;
    }

    /// <summary>
    /// Self-healing orchestrator that monitors components and applies healing actions.
    /// Thread-safe implementation for production use.
    /// </summary>
    public sealed class SelfHealingOrchestrator : IDisposable
    {
        private readonly ConcurrentDictionary<string, ISelfHealingComponent> _components = new();
        private readonly ConcurrentDictionary<string, HealingHistory> _healingHistory = new();
        private readonly ConcurrentDictionary<string, ComponentHealth> _lastKnownHealth = new();
        private readonly SelfHealingOptions _options;
        private readonly Timer _healthCheckTimer;
        private readonly SemaphoreSlim _healingLock = new(1, 1);
        private readonly object _disposeLock = new();
        private volatile bool _disposed;
        private volatile bool _isHealing;

        /// <summary>
        /// Event raised when healing is triggered.
        /// </summary>
        public event Action<string, HealingAction>? OnHealingStarted;

        /// <summary>
        /// Event raised when healing completes.
        /// </summary>
        public event Action<string, HealingResult>? OnHealingCompleted;

        /// <summary>
        /// Event raised when component health changes.
        /// </summary>
        public event Action<string, ComponentHealth, ComponentHealth>? OnHealthChanged;

        /// <summary>
        /// Creates a new self-healing orchestrator.
        /// </summary>
        public SelfHealingOrchestrator(SelfHealingOptions? options = null)
        {
            _options = options ?? new SelfHealingOptions();
            _healthCheckTimer = new Timer(
                _ => _ = CheckAllComponentsAsync(),
                null,
                _options.HealthCheckInterval,
                _options.HealthCheckInterval);
        }

        /// <summary>
        /// Registers a component for self-healing monitoring.
        /// </summary>
        public void RegisterComponent(ISelfHealingComponent component)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(component);

            _components[component.ComponentId] = component;
            _lastKnownHealth[component.ComponentId] = ComponentHealth.Healthy;
            _healingHistory[component.ComponentId] = new HealingHistory();

            component.OnHealthChanged += health => HandleHealthChange(component.ComponentId, health);
        }

        /// <summary>
        /// Unregisters a component from monitoring.
        /// </summary>
        public void UnregisterComponent(string componentId)
        {
            ThrowIfDisposed();
            _components.TryRemove(componentId, out _);
            _lastKnownHealth.TryRemove(componentId, out _);
        }

        /// <summary>
        /// Gets the current health status of all components.
        /// </summary>
        public IReadOnlyDictionary<string, ComponentHealth> GetHealthStatus()
        {
            return new Dictionary<string, ComponentHealth>(_lastKnownHealth);
        }

        /// <summary>
        /// Gets the healing history for a component.
        /// </summary>
        public IEnumerable<HealingResult> GetHealingHistory(string componentId)
        {
            if (_healingHistory.TryGetValue(componentId, out var history))
            {
                return history.GetRecent(_options.MaxHistoryEntries);
            }
            return Enumerable.Empty<HealingResult>();
        }

        /// <summary>
        /// Manually triggers healing for a specific component.
        /// </summary>
        public async Task<HealingResult?> TriggerHealingAsync(
            string componentId,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (!_components.TryGetValue(componentId, out var component))
            {
                return null;
            }

            return await HealComponentAsync(component, ct);
        }

        private async Task CheckAllComponentsAsync()
        {
            if (_disposed || _isHealing)
            {
                return;
            }

            foreach (var (id, component) in _components)
            {
                try
                {
                    var health = await component.CheckHealthAsync();
                    HandleHealthChange(id, health);
                }
                catch (Exception)
                {
                    // If health check fails, assume unhealthy
                    HandleHealthChange(id, ComponentHealth.Unhealthy);
                }
            }
        }

        private void HandleHealthChange(string componentId, ComponentHealth newHealth)
        {
            if (!_lastKnownHealth.TryGetValue(componentId, out var oldHealth))
            {
                oldHealth = ComponentHealth.Healthy;
            }

            if (oldHealth != newHealth)
            {
                _lastKnownHealth[componentId] = newHealth;
                OnHealthChanged?.Invoke(componentId, oldHealth, newHealth);

                // Trigger healing for unhealthy components
                if (newHealth == ComponentHealth.Unhealthy || newHealth == ComponentHealth.Dead)
                {
                    _ = Task.Run(async () =>
                    {
                        if (_components.TryGetValue(componentId, out var component))
                        {
                            await HealComponentAsync(component, CancellationToken.None);
                        }
                    });
                }
            }
        }

        private async Task<HealingResult?> HealComponentAsync(
            ISelfHealingComponent component,
            CancellationToken ct)
        {
            if (!await _healingLock.WaitAsync(_options.HealingTimeout, ct))
            {
                return null; // Already healing
            }

            try
            {
                _isHealing = true;
                _lastKnownHealth[component.ComponentId] = ComponentHealth.Recovering;

                var actions = component.GetHealingActions()
                    .OrderBy(a => a.Priority)
                    .ToList();

                foreach (var action in actions)
                {
                    OnHealingStarted?.Invoke(component.ComponentId, action);

                    var result = await ExecuteHealingActionAsync(action, ct);
                    RecordHealingResult(component.ComponentId, result);
                    OnHealingCompleted?.Invoke(component.ComponentId, result);

                    if (result.Success)
                    {
                        _lastKnownHealth[component.ComponentId] = ComponentHealth.Healthy;
                        return result;
                    }
                }

                // All actions failed
                _lastKnownHealth[component.ComponentId] = ComponentHealth.Dead;
                return null;
            }
            finally
            {
                _isHealing = false;
                _healingLock.Release();
            }
        }

        private async Task<HealingResult> ExecuteHealingActionAsync(
            HealingAction action,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var attempts = 0;
            Exception? lastException = null;

            while (attempts < action.MaxRetries)
            {
                attempts++;
                try
                {
                    var success = await action.Execute(ct);
                    if (success)
                    {
                        return new HealingResult
                        {
                            Success = true,
                            Action = action,
                            Attempts = attempts,
                            Duration = stopwatch.Elapsed
                        };
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                if (attempts < action.MaxRetries)
                {
                    await Task.Delay(action.RetryDelay, ct);
                }
            }

            return new HealingResult
            {
                Success = false,
                Action = action,
                Attempts = attempts,
                Duration = stopwatch.Elapsed,
                ErrorMessage = lastException?.Message ?? "Healing action returned false"
            };
        }

        private void RecordHealingResult(string componentId, HealingResult result)
        {
            if (_healingHistory.TryGetValue(componentId, out var history))
            {
                history.Add(result);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SelfHealingOrchestrator));
            }
        }

        /// <summary>
        /// Disposes the orchestrator and stops monitoring.
        /// </summary>
        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _healthCheckTimer.Dispose();
                _healingLock.Dispose();
            }
        }

        private sealed class HealingHistory
        {
            private readonly ConcurrentQueue<HealingResult> _results = new();
            private const int MaxSize = 100;

            public void Add(HealingResult result)
            {
                _results.Enqueue(result);
                while (_results.Count > MaxSize)
                {
                    _results.TryDequeue(out _);
                }
            }

            public IEnumerable<HealingResult> GetRecent(int count)
            {
                return _results.TakeLast(count);
            }
        }
    }

    /// <summary>
    /// Configuration options for self-healing.
    /// </summary>
    public sealed class SelfHealingOptions
    {
        /// <summary>Interval between health checks.</summary>
        public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Maximum time to wait for healing to complete.</summary>
        public TimeSpan HealingTimeout { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>Maximum number of history entries to keep per component.</summary>
        public int MaxHistoryEntries { get; init; } = 50;

        /// <summary>Enable automatic healing.</summary>
        public bool AutoHealingEnabled { get; init; } = true;
    }

    #endregion

    #region Chaos Engineering Framework

    /// <summary>
    /// Types of chaos that can be injected.
    /// </summary>
    public enum ChaosType
    {
        /// <summary>Inject random latency.</summary>
        Latency,
        /// <summary>Inject random exceptions.</summary>
        Exception,
        /// <summary>Inject resource exhaustion.</summary>
        ResourceExhaustion,
        /// <summary>Inject network partition simulation.</summary>
        NetworkPartition,
        /// <summary>Inject disk I/O failures.</summary>
        DiskFailure,
        /// <summary>Inject memory pressure.</summary>
        MemoryPressure
    }

    /// <summary>
    /// Configuration for a chaos experiment.
    /// </summary>
    public sealed class ChaosExperiment
    {
        /// <summary>Unique identifier for this experiment.</summary>
        public string ExperimentId { get; init; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>Human-readable name.</summary>
        public required string Name { get; init; }

        /// <summary>Type of chaos to inject.</summary>
        public ChaosType Type { get; init; }

        /// <summary>Target component or operation pattern (regex).</summary>
        public string TargetPattern { get; init; } = ".*";

        /// <summary>Probability of chaos occurring (0.0 - 1.0).</summary>
        public double Probability { get; init; } = 0.1;

        /// <summary>Duration of chaos effect (for latency).</summary>
        public TimeSpan Duration { get; init; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Exception to throw (for exception chaos).</summary>
        public Type? ExceptionType { get; init; }

        /// <summary>When the experiment started.</summary>
        public DateTimeOffset? StartedAt { get; set; }

        /// <summary>When the experiment should end.</summary>
        public DateTimeOffset? EndsAt { get; set; }

        /// <summary>Whether the experiment is currently active.</summary>
        public bool IsActive => StartedAt.HasValue &&
            (!EndsAt.HasValue || DateTimeOffset.UtcNow < EndsAt.Value);
    }

    /// <summary>
    /// Result of a chaos injection.
    /// </summary>
    public sealed class ChaosInjectionResult
    {
        /// <summary>Whether chaos was injected.</summary>
        public bool WasInjected { get; init; }

        /// <summary>The experiment that caused the injection.</summary>
        public ChaosExperiment? Experiment { get; init; }

        /// <summary>Target that was affected.</summary>
        public string? Target { get; init; }

        /// <summary>When the injection occurred.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Chaos engineering framework for testing system resilience.
    /// IMPORTANT: Only enable in non-production environments!
    /// </summary>
    public sealed class ChaosEngine : IDisposable
    {
        private readonly ConcurrentDictionary<string, ChaosExperiment> _experiments = new();
        private readonly ConcurrentQueue<ChaosInjectionResult> _injectionLog = new();
        private readonly Random _random = new();
        private readonly object _disposeLock = new();
        private volatile bool _enabled;
        private volatile bool _disposed;
        private int _injectionCount;

        /// <summary>
        /// Gets or sets whether chaos engineering is enabled.
        /// WARNING: Should NEVER be enabled in production!
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                ThrowIfDisposed();
                _enabled = value;
            }
        }

        /// <summary>
        /// Gets the total number of chaos injections.
        /// </summary>
        public int TotalInjections => _injectionCount;

        /// <summary>
        /// Event raised when chaos is injected.
        /// </summary>
        public event Action<ChaosInjectionResult>? OnChaosInjected;

        /// <summary>
        /// Registers a chaos experiment.
        /// </summary>
        public void RegisterExperiment(ChaosExperiment experiment)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(experiment);
            _experiments[experiment.ExperimentId] = experiment;
        }

        /// <summary>
        /// Starts a registered experiment.
        /// </summary>
        public void StartExperiment(string experimentId, TimeSpan? duration = null)
        {
            ThrowIfDisposed();
            if (_experiments.TryGetValue(experimentId, out var experiment))
            {
                experiment.StartedAt = DateTimeOffset.UtcNow;
                experiment.EndsAt = duration.HasValue
                    ? DateTimeOffset.UtcNow + duration.Value
                    : null;
            }
        }

        /// <summary>
        /// Stops a running experiment.
        /// </summary>
        public void StopExperiment(string experimentId)
        {
            ThrowIfDisposed();
            if (_experiments.TryGetValue(experimentId, out var experiment))
            {
                experiment.EndsAt = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Stops all running experiments.
        /// </summary>
        public void StopAllExperiments()
        {
            ThrowIfDisposed();
            foreach (var experiment in _experiments.Values)
            {
                experiment.EndsAt = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Checks if chaos should be injected for a given target and applies it.
        /// </summary>
        /// <param name="target">The target operation or component name.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if operation should proceed, false if it should fail.</returns>
        public async Task<bool> MaybeInjectChaosAsync(string target, CancellationToken ct = default)
        {
            if (!_enabled)
            {
                return true;
            }

            foreach (var experiment in _experiments.Values.Where(e => e.IsActive))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(target, experiment.TargetPattern))
                {
                    continue;
                }

                if (_random.NextDouble() > experiment.Probability)
                {
                    continue;
                }

                // Inject chaos
                var result = new ChaosInjectionResult
                {
                    WasInjected = true,
                    Experiment = experiment,
                    Target = target
                };

                Interlocked.Increment(ref _injectionCount);
                RecordInjection(result);
                OnChaosInjected?.Invoke(result);

                switch (experiment.Type)
                {
                    case ChaosType.Latency:
                        await Task.Delay(experiment.Duration, ct);
                        return true;

                    case ChaosType.Exception:
                        var exType = experiment.ExceptionType ?? typeof(ChaosException);
                        var ex = (Exception)Activator.CreateInstance(
                            exType,
                            $"Chaos injection: {experiment.Name}")!;
                        throw ex;

                    case ChaosType.NetworkPartition:
                        throw new ChaosNetworkException(
                            $"Simulated network partition: {experiment.Name}");

                    case ChaosType.DiskFailure:
                        throw new ChaosIOException(
                            $"Simulated disk failure: {experiment.Name}");

                    case ChaosType.ResourceExhaustion:
                        // Simulate by adding latency and then throwing
                        await Task.Delay(experiment.Duration, ct);
                        throw new ChaosResourceException(
                            $"Simulated resource exhaustion: {experiment.Name}");

                    case ChaosType.MemoryPressure:
                        // Force GC to simulate memory pressure
                        GC.Collect(2, GCCollectionMode.Forced);
                        return true;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the recent injection log.
        /// </summary>
        public IEnumerable<ChaosInjectionResult> GetInjectionLog(int count = 100)
        {
            return _injectionLog.TakeLast(count);
        }

        /// <summary>
        /// Gets all registered experiments.
        /// </summary>
        public IEnumerable<ChaosExperiment> GetExperiments()
        {
            return _experiments.Values.ToList();
        }

        private void RecordInjection(ChaosInjectionResult result)
        {
            _injectionLog.Enqueue(result);
            while (_injectionLog.Count > 1000)
            {
                _injectionLog.TryDequeue(out _);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ChaosEngine));
            }
        }

        /// <summary>
        /// Disposes the chaos engine.
        /// </summary>
        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                StopAllExperiments();
            }
        }
    }

    /// <summary>
    /// Base exception for chaos injection.
    /// </summary>
    public class ChaosException : Exception
    {
        public ChaosException(string message) : base(message) { }
    }

    /// <summary>
    /// Chaos exception simulating network issues.
    /// </summary>
    public class ChaosNetworkException : ChaosException
    {
        public ChaosNetworkException(string message) : base(message) { }
    }

    /// <summary>
    /// Chaos exception simulating I/O failures.
    /// </summary>
    public class ChaosIOException : ChaosException
    {
        public ChaosIOException(string message) : base(message) { }
    }

    /// <summary>
    /// Chaos exception simulating resource exhaustion.
    /// </summary>
    public class ChaosResourceException : ChaosException
    {
        public ChaosResourceException(string message) : base(message) { }
    }

    #endregion

    #region Adaptive Rate Limiting

    /// <summary>
    /// Adaptive rate limiter that adjusts limits based on system load.
    /// Uses a sliding window algorithm with dynamic adjustment.
    /// </summary>
    public sealed class AdaptiveRateLimiter : IDisposable
    {
        private readonly ConcurrentDictionary<string, ClientState> _clientStates = new();
        private readonly AdaptiveRateLimiterOptions _options;
        private readonly Timer _adjustmentTimer;
        private readonly object _disposeLock = new();
        private volatile bool _disposed;
        private double _currentLoadFactor = 1.0;
        private long _totalRequests;
        private long _rejectedRequests;

        /// <summary>
        /// Event raised when a request is rate limited.
        /// </summary>
        public event Action<string, RateLimitResult>? OnRateLimited;

        /// <summary>
        /// Creates a new adaptive rate limiter.
        /// </summary>
        public AdaptiveRateLimiter(AdaptiveRateLimiterOptions? options = null)
        {
            _options = options ?? new AdaptiveRateLimiterOptions();
            _adjustmentTimer = new Timer(
                _ => AdjustLimits(),
                null,
                _options.AdjustmentInterval,
                _options.AdjustmentInterval);
        }

        /// <summary>
        /// Gets the current load factor (1.0 = normal, &lt;1.0 = under load).
        /// </summary>
        public double CurrentLoadFactor => _currentLoadFactor;

        /// <summary>
        /// Gets rate limiting statistics.
        /// </summary>
        public RateLimitStats GetStats()
        {
            return new RateLimitStats
            {
                TotalRequests = Interlocked.Read(ref _totalRequests),
                RejectedRequests = Interlocked.Read(ref _rejectedRequests),
                CurrentLoadFactor = _currentLoadFactor,
                ActiveClients = _clientStates.Count
            };
        }

        /// <summary>
        /// Attempts to acquire a rate limit token for a client.
        /// </summary>
        /// <param name="clientId">The client identifier.</param>
        /// <param name="weight">Weight of this request (default 1).</param>
        /// <returns>Result indicating if the request is allowed.</returns>
        public RateLimitResult TryAcquire(string clientId, int weight = 1)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref _totalRequests);

            var state = _clientStates.GetOrAdd(clientId, _ => new ClientState(_options.BaseRequestsPerSecond));
            var now = DateTimeOffset.UtcNow;

            lock (state.Lock)
            {
                // Clean old entries from sliding window
                var windowStart = now - _options.WindowSize;
                while (state.RequestTimes.TryPeek(out var oldest) && oldest < windowStart)
                {
                    state.RequestTimes.TryDequeue(out _);
                }

                // Calculate effective limit based on load factor and client reputation
                var effectiveLimit = (int)(state.BaseLimit * _currentLoadFactor * state.ReputationScore);
                var currentCount = state.RequestTimes.Count;

                if (currentCount + weight > effectiveLimit)
                {
                    Interlocked.Increment(ref _rejectedRequests);
                    var result = new RateLimitResult
                    {
                        Allowed = false,
                        ClientId = clientId,
                        CurrentCount = currentCount,
                        Limit = effectiveLimit,
                        RetryAfter = CalculateRetryAfter(state, effectiveLimit)
                    };
                    OnRateLimited?.Invoke(clientId, result);
                    return result;
                }

                // Record request
                for (var i = 0; i < weight; i++)
                {
                    state.RequestTimes.Enqueue(now);
                }

                return new RateLimitResult
                {
                    Allowed = true,
                    ClientId = clientId,
                    CurrentCount = currentCount + weight,
                    Limit = effectiveLimit,
                    Remaining = effectiveLimit - currentCount - weight
                };
            }
        }

        /// <summary>
        /// Updates the load factor based on system metrics.
        /// </summary>
        /// <param name="cpuUsage">Current CPU usage (0.0 - 1.0).</param>
        /// <param name="memoryUsage">Current memory usage (0.0 - 1.0).</param>
        /// <param name="latencyMs">Current average latency in milliseconds.</param>
        public void UpdateLoadMetrics(double cpuUsage, double memoryUsage, double latencyMs)
        {
            ThrowIfDisposed();

            // Calculate load factor based on metrics
            // Lower factor = more restrictive rate limiting
            var cpuFactor = 1.0 - Math.Min(1.0, cpuUsage);
            var memoryFactor = 1.0 - Math.Min(1.0, memoryUsage);
            var latencyFactor = Math.Max(0.1, 1.0 - (latencyMs / _options.MaxAcceptableLatencyMs));

            var newFactor = (cpuFactor * 0.4 + memoryFactor * 0.3 + latencyFactor * 0.3);
            newFactor = Math.Clamp(newFactor, _options.MinLoadFactor, _options.MaxLoadFactor);

            // Smooth the transition
            _currentLoadFactor = _currentLoadFactor * 0.8 + newFactor * 0.2;
        }

        /// <summary>
        /// Updates client reputation based on behavior.
        /// </summary>
        /// <param name="clientId">The client identifier.</param>
        /// <param name="delta">Reputation change (-1.0 to 1.0).</param>
        public void UpdateClientReputation(string clientId, double delta)
        {
            ThrowIfDisposed();

            if (_clientStates.TryGetValue(clientId, out var state))
            {
                lock (state.Lock)
                {
                    state.ReputationScore = Math.Clamp(
                        state.ReputationScore + delta,
                        0.1,
                        2.0);
                }
            }
        }

        private TimeSpan CalculateRetryAfter(ClientState state, int effectiveLimit)
        {
            if (state.RequestTimes.Count == 0)
            {
                return TimeSpan.Zero;
            }

            // Calculate when the oldest request will fall out of the window
            if (state.RequestTimes.TryPeek(out var oldest))
            {
                var retryAt = oldest + _options.WindowSize;
                var retryAfter = retryAt - DateTimeOffset.UtcNow;
                return retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMilliseconds(100);
            }

            return TimeSpan.FromSeconds(1);
        }

        private void AdjustLimits()
        {
            if (_disposed)
            {
                return;
            }

            // Clean up inactive clients
            var threshold = DateTimeOffset.UtcNow - _options.ClientInactivityTimeout;
            foreach (var (clientId, state) in _clientStates)
            {
                if (state.LastActivity < threshold)
                {
                    _clientStates.TryRemove(clientId, out _);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AdaptiveRateLimiter));
            }
        }

        /// <summary>
        /// Disposes the rate limiter.
        /// </summary>
        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _adjustmentTimer.Dispose();
            }
        }

        private sealed class ClientState
        {
            public readonly object Lock = new();
            public readonly ConcurrentQueue<DateTimeOffset> RequestTimes = new();
            public int BaseLimit;
            public double ReputationScore = 1.0;
            public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;

            public ClientState(int baseLimit)
            {
                BaseLimit = baseLimit;
            }
        }
    }

    /// <summary>
    /// Configuration for adaptive rate limiting.
    /// </summary>
    public sealed class AdaptiveRateLimiterOptions
    {
        /// <summary>Base requests per second per client.</summary>
        public int BaseRequestsPerSecond { get; init; } = 100;

        /// <summary>Sliding window size for rate calculation.</summary>
        public TimeSpan WindowSize { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>How often to adjust limits.</summary>
        public TimeSpan AdjustmentInterval { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Minimum load factor (most restrictive).</summary>
        public double MinLoadFactor { get; init; } = 0.1;

        /// <summary>Maximum load factor (least restrictive).</summary>
        public double MaxLoadFactor { get; init; } = 2.0;

        /// <summary>Maximum acceptable latency in milliseconds.</summary>
        public double MaxAcceptableLatencyMs { get; init; } = 500;

        /// <summary>Time after which inactive clients are cleaned up.</summary>
        public TimeSpan ClientInactivityTimeout { get; init; } = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Result of a rate limit check.
    /// </summary>
    public sealed class RateLimitResult
    {
        /// <summary>Whether the request is allowed.</summary>
        public bool Allowed { get; init; }

        /// <summary>The client identifier.</summary>
        public required string ClientId { get; init; }

        /// <summary>Current request count in window.</summary>
        public int CurrentCount { get; init; }

        /// <summary>Current limit.</summary>
        public int Limit { get; init; }

        /// <summary>Remaining requests in window (if allowed).</summary>
        public int Remaining { get; init; }

        /// <summary>Time to wait before retrying (if not allowed).</summary>
        public TimeSpan RetryAfter { get; init; }
    }

    /// <summary>
    /// Rate limiting statistics.
    /// </summary>
    public sealed class RateLimitStats
    {
        /// <summary>Total requests processed.</summary>
        public long TotalRequests { get; init; }

        /// <summary>Total requests rejected.</summary>
        public long RejectedRequests { get; init; }

        /// <summary>Rejection rate.</summary>
        public double RejectionRate => TotalRequests > 0 ? (double)RejectedRequests / TotalRequests : 0;

        /// <summary>Current load factor.</summary>
        public double CurrentLoadFactor { get; init; }

        /// <summary>Number of active clients.</summary>
        public int ActiveClients { get; init; }
    }

    #endregion

    #region Predictive Load Shedding

    /// <summary>
    /// Predictive load shedding that uses historical patterns to prevent overload.
    /// </summary>
    public sealed class PredictiveLoadShedder : IDisposable
    {
        private readonly PredictiveLoadShedderOptions _options;
        private readonly ConcurrentQueue<LoadSample> _loadHistory = new();
        private readonly Timer _samplingTimer;
        private readonly object _disposeLock = new();
        private volatile bool _disposed;
        private volatile bool _shedding;
        private double _currentLoad;
        private double _predictedLoad;
        private int _droppedRequests;

        /// <summary>
        /// Event raised when load shedding starts.
        /// </summary>
        public event Action<double>? OnSheddingStarted;

        /// <summary>
        /// Event raised when load shedding ends.
        /// </summary>
        public event Action? OnSheddingEnded;

        /// <summary>
        /// Event raised when a request is shed.
        /// </summary>
        public event Action<LoadSheddingResult>? OnRequestShed;

        /// <summary>
        /// Creates a new predictive load shedder.
        /// </summary>
        public PredictiveLoadShedder(PredictiveLoadShedderOptions? options = null)
        {
            _options = options ?? new PredictiveLoadShedderOptions();
            _samplingTimer = new Timer(
                _ => SampleLoad(),
                null,
                _options.SamplingInterval,
                _options.SamplingInterval);
        }

        /// <summary>
        /// Gets whether load shedding is currently active.
        /// </summary>
        public bool IsShedding => _shedding;

        /// <summary>
        /// Gets the current load estimate (0.0 - 1.0+).
        /// </summary>
        public double CurrentLoad => _currentLoad;

        /// <summary>
        /// Gets the predicted load for the near future.
        /// </summary>
        public double PredictedLoad => _predictedLoad;

        /// <summary>
        /// Gets the number of dropped requests.
        /// </summary>
        public int DroppedRequests => _droppedRequests;

        /// <summary>
        /// Records current system metrics for prediction.
        /// </summary>
        public void RecordMetrics(double cpuUsage, double memoryUsage, int activeRequests, int queueDepth)
        {
            ThrowIfDisposed();

            _currentLoad = CalculateLoad(cpuUsage, memoryUsage, activeRequests, queueDepth);
        }

        /// <summary>
        /// Checks if a request should be allowed or shed.
        /// </summary>
        /// <param name="priority">Request priority (higher = more important).</param>
        /// <returns>Result indicating if the request should proceed.</returns>
        public LoadSheddingResult ShouldAllowRequest(int priority = 0)
        {
            ThrowIfDisposed();

            // Always allow high-priority requests
            if (priority >= _options.CriticalPriorityThreshold)
            {
                return new LoadSheddingResult { Allowed = true, Priority = priority };
            }

            // Calculate drop probability based on predicted load
            if (_predictedLoad < _options.SheddingThreshold)
            {
                if (_shedding)
                {
                    _shedding = false;
                    OnSheddingEnded?.Invoke();
                }
                return new LoadSheddingResult { Allowed = true, Priority = priority };
            }

            if (!_shedding)
            {
                _shedding = true;
                OnSheddingStarted?.Invoke(_predictedLoad);
            }

            // Progressive shedding based on load and priority
            var excessLoad = _predictedLoad - _options.SheddingThreshold;
            var dropProbability = Math.Min(1.0, excessLoad / (1.0 - _options.SheddingThreshold));

            // Adjust for priority (higher priority = lower drop probability)
            var priorityFactor = 1.0 - (priority / (double)_options.CriticalPriorityThreshold);
            dropProbability *= priorityFactor;

            var random = Random.Shared.NextDouble();
            if (random < dropProbability)
            {
                Interlocked.Increment(ref _droppedRequests);
                var result = new LoadSheddingResult
                {
                    Allowed = false,
                    Priority = priority,
                    DropProbability = dropProbability,
                    CurrentLoad = _currentLoad,
                    PredictedLoad = _predictedLoad
                };
                OnRequestShed?.Invoke(result);
                return result;
            }

            return new LoadSheddingResult
            {
                Allowed = true,
                Priority = priority,
                DropProbability = dropProbability,
                CurrentLoad = _currentLoad,
                PredictedLoad = _predictedLoad
            };
        }

        private void SampleLoad()
        {
            if (_disposed)
            {
                return;
            }

            var sample = new LoadSample
            {
                Timestamp = DateTimeOffset.UtcNow,
                Load = _currentLoad
            };

            _loadHistory.Enqueue(sample);

            // Keep only recent samples
            while (_loadHistory.Count > _options.MaxSamples)
            {
                _loadHistory.TryDequeue(out _);
            }

            // Predict future load using exponential smoothing
            _predictedLoad = PredictLoad();
        }

        private double PredictLoad()
        {
            var samples = _loadHistory.ToArray();
            if (samples.Length == 0)
            {
                return _currentLoad;
            }

            // Simple exponential moving average with trend
            var alpha = _options.SmoothingFactor;
            double level = samples[0].Load;
            double trend = 0;

            foreach (var sample in samples)
            {
                var lastLevel = level;
                level = alpha * sample.Load + (1 - alpha) * (level + trend);
                trend = alpha * (level - lastLevel) + (1 - alpha) * trend;
            }

            // Predict ahead by extrapolating trend
            var prediction = level + trend * _options.PredictionHorizonSamples;
            return Math.Max(0, prediction);
        }

        private double CalculateLoad(double cpu, double memory, int activeRequests, int queueDepth)
        {
            // Weighted combination of metrics
            var cpuLoad = cpu * 0.4;
            var memLoad = memory * 0.2;
            var requestLoad = Math.Min(1.0, activeRequests / (double)_options.MaxConcurrentRequests) * 0.25;
            var queueLoad = Math.Min(1.0, queueDepth / (double)_options.MaxQueueDepth) * 0.15;

            return cpuLoad + memLoad + requestLoad + queueLoad;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PredictiveLoadShedder));
            }
        }

        /// <summary>
        /// Disposes the load shedder.
        /// </summary>
        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _samplingTimer.Dispose();
            }
        }

        private readonly struct LoadSample
        {
            public DateTimeOffset Timestamp { get; init; }
            public double Load { get; init; }
        }
    }

    /// <summary>
    /// Configuration for predictive load shedding.
    /// </summary>
    public sealed class PredictiveLoadShedderOptions
    {
        /// <summary>Load threshold at which to start shedding (0.0 - 1.0).</summary>
        public double SheddingThreshold { get; init; } = 0.8;

        /// <summary>Priority at which requests are never shed.</summary>
        public int CriticalPriorityThreshold { get; init; } = 100;

        /// <summary>How often to sample load.</summary>
        public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Maximum number of load samples to keep.</summary>
        public int MaxSamples { get; init; } = 100;

        /// <summary>Smoothing factor for exponential moving average (0.0 - 1.0).</summary>
        public double SmoothingFactor { get; init; } = 0.3;

        /// <summary>How many samples ahead to predict.</summary>
        public int PredictionHorizonSamples { get; init; } = 5;

        /// <summary>Maximum concurrent requests for load calculation.</summary>
        public int MaxConcurrentRequests { get; init; } = 1000;

        /// <summary>Maximum queue depth for load calculation.</summary>
        public int MaxQueueDepth { get; init; } = 500;
    }

    /// <summary>
    /// Result of a load shedding decision.
    /// </summary>
    public sealed class LoadSheddingResult
    {
        /// <summary>Whether the request is allowed.</summary>
        public bool Allowed { get; init; }

        /// <summary>The request priority.</summary>
        public int Priority { get; init; }

        /// <summary>Probability of being dropped.</summary>
        public double DropProbability { get; init; }

        /// <summary>Current system load.</summary>
        public double CurrentLoad { get; init; }

        /// <summary>Predicted system load.</summary>
        public double PredictedLoad { get; init; }
    }

    #endregion
}
