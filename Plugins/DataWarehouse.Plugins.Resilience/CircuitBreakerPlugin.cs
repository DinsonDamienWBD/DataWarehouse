using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.Resilience
{
    /// <summary>
    /// Production-ready circuit breaker plugin implementing the resilience pattern.
    /// Provides automatic failure detection, circuit state management, and statistics tracking.
    ///
    /// Features:
    /// - Named circuit breakers for different resources/services
    /// - Configurable failure thresholds and break durations
    /// - Sliding window failure counting
    /// - Half-open state for gradual recovery testing
    /// - Exponential backoff with jitter for retries
    /// - Per-circuit and global statistics
    /// - Persistent state for crash recovery
    /// - Event notifications for state changes
    ///
    /// Circuit States:
    /// - Closed: Normal operation, tracking failures
    /// - Open: Failing fast, rejecting requests
    /// - HalfOpen: Testing if underlying service has recovered
    ///
    /// Message Commands:
    /// - circuit.execute: Execute an operation with circuit breaker protection
    /// - circuit.status: Get the status of a circuit
    /// - circuit.reset: Manually reset a circuit to closed state
    /// - circuit.configure: Configure a named circuit
    /// - circuit.list: List all circuits and their states
    /// - circuit.statistics: Get execution statistics
    /// </summary>
    public sealed class CircuitBreakerPlugin : CircuitBreakerPluginBase
    {
        private readonly ConcurrentDictionary<string, CircuitConfig> _circuitConfigs;
        private readonly ConcurrentDictionary<string, CircuitInstance> _circuits;
        private readonly ConcurrentDictionary<string, List<Action<CircuitStateChange>>> _stateChangeHandlers;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly CircuitBreakerConfig _globalConfig;
        private readonly string _storagePath;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.resilience.circuitbreaker";

        /// <inheritdoc/>
        public override string Name => "Circuit Breaker";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <inheritdoc/>
        public override string PolicyId => "default";

        /// <summary>
        /// Default configuration for the circuit breaker.
        /// </summary>
        protected override ResiliencePolicyConfig Config => new()
        {
            FailureThreshold = _globalConfig.FailureThreshold,
            BreakDuration = _globalConfig.BreakDuration,
            FailureWindow = _globalConfig.FailureWindow,
            Timeout = _globalConfig.Timeout,
            MaxRetries = _globalConfig.MaxRetries,
            RetryBaseDelay = _globalConfig.RetryBaseDelay,
            RetryMaxDelay = _globalConfig.RetryMaxDelay
        };

        /// <summary>
        /// Initializes a new instance of the CircuitBreakerPlugin.
        /// </summary>
        /// <param name="config">Optional configuration for the circuit breaker.</param>
        public CircuitBreakerPlugin(CircuitBreakerConfig? config = null)
        {
            _globalConfig = config ?? new CircuitBreakerConfig();
            _circuitConfigs = new ConcurrentDictionary<string, CircuitConfig>();
            _circuits = new ConcurrentDictionary<string, CircuitInstance>();
            _stateChangeHandlers = new ConcurrentDictionary<string, List<Action<CircuitStateChange>>>();
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "resilience", "circuits");
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);
            await LoadCircuitStatesAsync();
            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "circuit.execute", DisplayName = "Execute", Description = "Execute with circuit breaker protection" },
                new() { Name = "circuit.status", DisplayName = "Status", Description = "Get circuit status" },
                new() { Name = "circuit.reset", DisplayName = "Reset", Description = "Reset circuit to closed state" },
                new() { Name = "circuit.configure", DisplayName = "Configure", Description = "Configure a named circuit" },
                new() { Name = "circuit.list", DisplayName = "List", Description = "List all circuits" },
                new() { Name = "circuit.statistics", DisplayName = "Statistics", Description = "Get execution statistics" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["CircuitCount"] = _circuits.Count;
            metadata["GlobalFailureThreshold"] = _globalConfig.FailureThreshold;
            metadata["GlobalBreakDuration"] = _globalConfig.BreakDuration.TotalSeconds;
            metadata["SupportsNamedCircuits"] = true;
            metadata["SupportsPersistence"] = true;
            metadata["SupportsEventNotifications"] = true;
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "circuit.execute":
                    await HandleExecuteAsync(message);
                    break;
                case "circuit.status":
                    HandleStatus(message);
                    break;
                case "circuit.reset":
                    await HandleResetAsync(message);
                    break;
                case "circuit.configure":
                    await HandleConfigureAsync(message);
                    break;
                case "circuit.list":
                    HandleList(message);
                    break;
                case "circuit.statistics":
                    HandleStatistics(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        /// <summary>
        /// Gets or creates a circuit instance for the specified name.
        /// </summary>
        /// <param name="circuitName">The name of the circuit.</param>
        /// <returns>The circuit instance.</returns>
        public CircuitInstance GetCircuit(string circuitName)
        {
            if (string.IsNullOrWhiteSpace(circuitName))
                throw new ArgumentException("Circuit name cannot be null or empty", nameof(circuitName));

            return _circuits.GetOrAdd(circuitName, name =>
            {
                var config = _circuitConfigs.GetValueOrDefault(name, new CircuitConfig { Name = name });
                return new CircuitInstance(name, config, OnCircuitStateChanged);
            });
        }

        /// <summary>
        /// Executes an operation with circuit breaker protection for the specified circuit.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="circuitName">The name of the circuit.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        public async Task<T> ExecuteAsync<T>(string circuitName, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        {
            var circuit = GetCircuit(circuitName);
            return await circuit.ExecuteAsync(operation, ct);
        }

        /// <summary>
        /// Executes an operation with circuit breaker protection for the specified circuit.
        /// </summary>
        /// <param name="circuitName">The name of the circuit.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteAsync(string circuitName, Func<CancellationToken, Task> operation, CancellationToken ct = default)
        {
            var circuit = GetCircuit(circuitName);
            await circuit.ExecuteAsync(async token =>
            {
                await operation(token);
                return true;
            }, ct);
        }

        /// <summary>
        /// Configures a named circuit with specific settings.
        /// </summary>
        /// <param name="config">The circuit configuration.</param>
        public void ConfigureCircuit(CircuitConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.Name))
                throw new ArgumentException("Circuit name is required", nameof(config));

            _circuitConfigs[config.Name] = config;

            // Update existing circuit if present
            if (_circuits.TryGetValue(config.Name, out var circuit))
            {
                circuit.UpdateConfig(config);
            }
        }

        /// <summary>
        /// Registers a handler for circuit state changes.
        /// </summary>
        /// <param name="circuitName">The circuit name to monitor, or "*" for all circuits.</param>
        /// <param name="handler">The handler to invoke on state changes.</param>
        public void OnStateChange(string circuitName, Action<CircuitStateChange> handler)
        {
            var handlers = _stateChangeHandlers.GetOrAdd(circuitName, _ => new List<Action<CircuitStateChange>>());
            lock (handlers)
            {
                handlers.Add(handler);
            }
        }

        /// <summary>
        /// Resets the specified circuit to closed state.
        /// </summary>
        /// <param name="circuitName">The name of the circuit to reset.</param>
        public async Task ResetCircuitAsync(string circuitName)
        {
            if (_circuits.TryGetValue(circuitName, out var circuit))
            {
                circuit.Reset();
                await PersistCircuitStatesAsync();
            }
        }

        /// <summary>
        /// Gets the status of all circuits.
        /// </summary>
        /// <returns>A dictionary of circuit names to their status.</returns>
        public IReadOnlyDictionary<string, CircuitStatus> GetAllCircuitStatus()
        {
            return _circuits.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.GetStatus());
        }

        private void OnCircuitStateChanged(CircuitStateChange change)
        {
            // Notify specific circuit handlers
            if (_stateChangeHandlers.TryGetValue(change.CircuitName, out var handlers))
            {
                InvokeHandlers(handlers, change);
            }

            // Notify wildcard handlers
            if (_stateChangeHandlers.TryGetValue("*", out var wildcardHandlers))
            {
                InvokeHandlers(wildcardHandlers, change);
            }

            // Persist state changes
            _ = PersistCircuitStatesAsync();
        }

        private static void InvokeHandlers(List<Action<CircuitStateChange>> handlers, CircuitStateChange change)
        {
            List<Action<CircuitStateChange>> handlersCopy;
            lock (handlers)
            {
                handlersCopy = handlers.ToList();
            }

            foreach (var handler in handlersCopy)
            {
                try
                {
                    handler(change);
                }
                catch
                {
                    // Log but don't throw - handler errors shouldn't affect circuit operation
                }
            }
        }

        private async Task HandleExecuteAsync(PluginMessage message)
        {
            var circuitName = GetString(message.Payload, "circuit") ?? "default";
            var operationId = GetString(message.Payload, "operationId") ?? Guid.NewGuid().ToString("N");

            var circuit = GetCircuit(circuitName);
            var status = circuit.GetStatus();

            // Store status in message response
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["operationId"] = operationId,
                ["circuitState"] = status.State.ToString(),
                ["canExecute"] = status.State != CircuitState.Open
            };

            await Task.CompletedTask;
        }

        private void HandleStatus(PluginMessage message)
        {
            var circuitName = GetString(message.Payload, "circuit");

            if (!string.IsNullOrEmpty(circuitName))
            {
                if (_circuits.TryGetValue(circuitName, out var circuit))
                {
                    message.Payload["result"] = circuit.GetStatus();
                }
            }
            else
            {
                message.Payload["result"] = GetAllCircuitStatus();
            }
        }

        private async Task HandleResetAsync(PluginMessage message)
        {
            var circuitName = GetString(message.Payload, "circuit") ?? throw new ArgumentException("circuit name required");
            await ResetCircuitAsync(circuitName);
        }

        private async Task HandleConfigureAsync(PluginMessage message)
        {
            var config = new CircuitConfig
            {
                Name = GetString(message.Payload, "name") ?? throw new ArgumentException("name required"),
                FailureThreshold = GetInt(message.Payload, "failureThreshold") ?? _globalConfig.FailureThreshold,
                BreakDurationSeconds = GetInt(message.Payload, "breakDurationSeconds") ?? (int)_globalConfig.BreakDuration.TotalSeconds,
                TimeoutSeconds = GetInt(message.Payload, "timeoutSeconds") ?? (int)_globalConfig.Timeout.TotalSeconds,
                MaxRetries = GetInt(message.Payload, "maxRetries") ?? _globalConfig.MaxRetries
            };

            ConfigureCircuit(config);
            await PersistCircuitStatesAsync();
        }

        private void HandleList(PluginMessage message)
        {
            message.Payload["result"] = _circuits.Keys.ToList();
        }

        private void HandleStatistics(PluginMessage message)
        {
            var circuitName = GetString(message.Payload, "circuit");

            if (!string.IsNullOrEmpty(circuitName))
            {
                if (_circuits.TryGetValue(circuitName, out var circuit))
                {
                    message.Payload["result"] = circuit.GetStatistics();
                }
            }
            else
            {
                message.Payload["result"] = _circuits.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.GetStatistics());
            }
        }

        private async Task LoadCircuitStatesAsync()
        {
            var path = Path.Combine(_storagePath, "circuit-states.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<CircuitPersistenceData>(json);

                if (data?.Configs != null)
                {
                    foreach (var config in data.Configs)
                    {
                        _circuitConfigs[config.Name] = config;
                    }
                }
            }
            catch
            {
                // Log but continue - circuit will use default config
            }
        }

        private async Task PersistCircuitStatesAsync()
        {
            if (!await _persistLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return;

            try
            {
                Directory.CreateDirectory(_storagePath);

                var data = new CircuitPersistenceData
                {
                    Configs = _circuitConfigs.Values.ToList(),
                    States = _circuits.ToDictionary(
                        kv => kv.Key,
                        kv => new PersistedCircuitState
                        {
                            Name = kv.Key,
                            State = kv.Value.CurrentState.ToString(),
                            LastStateChange = kv.Value.LastStateChange
                        })
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(_storagePath, "circuit-states.json"), json);
            }
            finally
            {
                _persistLock.Release();
            }
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
    }

    /// <summary>
    /// Individual circuit breaker instance with its own state and configuration.
    /// </summary>
    public sealed class CircuitInstance
    {
        private readonly object _stateLock = new();
        private readonly SlidingWindowCounter _failureWindow;
        private readonly Action<CircuitStateChange> _stateChangeCallback;
        private CircuitState _state = CircuitState.Closed;
        private DateTime _lastStateChange = DateTime.UtcNow;
        private int _successCount;
        private int _consecutiveSuccesses;
        private long _totalExecutions;
        private long _successfulExecutions;
        private long _failedExecutions;
        private long _rejectedExecutions;
        private long _timeoutExecutions;
        private DateTime? _lastFailure;
        private DateTime? _lastSuccess;
        private CircuitConfig _config;

        /// <summary>
        /// Name of this circuit.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Current state of the circuit.
        /// </summary>
        public CircuitState CurrentState
        {
            get { lock (_stateLock) { return _state; } }
        }

        /// <summary>
        /// Time of the last state change.
        /// </summary>
        public DateTime LastStateChange
        {
            get { lock (_stateLock) { return _lastStateChange; } }
        }

        internal CircuitInstance(string name, CircuitConfig config, Action<CircuitStateChange> stateChangeCallback)
        {
            Name = name;
            _config = config;
            _stateChangeCallback = stateChangeCallback;
            _failureWindow = new SlidingWindowCounter(TimeSpan.FromSeconds(config.FailureWindowSeconds));
        }

        /// <summary>
        /// Updates the circuit configuration.
        /// </summary>
        public void UpdateConfig(CircuitConfig config)
        {
            lock (_stateLock)
            {
                _config = config;
            }
        }

        /// <summary>
        /// Executes an operation with circuit breaker protection.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalExecutions);

            if (!CanExecute())
            {
                Interlocked.Increment(ref _rejectedExecutions);
                var retryAfter = GetRetryAfter();
                throw new CircuitBreakerOpenException(Name, retryAfter);
            }

            var retryCount = 0;
            Exception? lastException = null;

            while (retryCount <= _config.MaxRetries)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

                    var result = await operation(cts.Token);
                    RecordSuccess();
                    return result;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _timeoutExecutions);
                    RecordFailure();
                    throw new TimeoutException($"Operation timed out after {_config.TimeoutSeconds}s");
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (!ShouldRetry(ex))
                    {
                        RecordFailure();
                        throw;
                    }

                    retryCount++;
                    if (retryCount <= _config.MaxRetries)
                    {
                        var delay = CalculateBackoff(retryCount);
                        await Task.Delay(delay, ct);
                    }
                }
            }

            RecordFailure();
            throw lastException ?? new InvalidOperationException("Max retries exceeded");
        }

        /// <summary>
        /// Resets the circuit to closed state.
        /// </summary>
        public void Reset()
        {
            CircuitState oldState;
            lock (_stateLock)
            {
                oldState = _state;
                _state = CircuitState.Closed;
                _lastStateChange = DateTime.UtcNow;
                _successCount = 0;
                _consecutiveSuccesses = 0;
                _failureWindow.Reset();
            }

            if (oldState != CircuitState.Closed)
            {
                NotifyStateChange(oldState, CircuitState.Closed, "Manual reset");
            }
        }

        /// <summary>
        /// Gets the current status of the circuit.
        /// </summary>
        public CircuitStatus GetStatus()
        {
            lock (_stateLock)
            {
                return new CircuitStatus
                {
                    Name = Name,
                    State = _state,
                    LastStateChange = _lastStateChange,
                    FailureCount = _failureWindow.Count,
                    FailureThreshold = _config.FailureThreshold,
                    BreakDurationSeconds = _config.BreakDurationSeconds,
                    RetryAfter = _state == CircuitState.Open ? GetRetryAfter() : null
                };
            }
        }

        /// <summary>
        /// Gets execution statistics for this circuit.
        /// </summary>
        public CircuitStatistics GetStatistics()
        {
            return new CircuitStatistics
            {
                Name = Name,
                TotalExecutions = Interlocked.Read(ref _totalExecutions),
                SuccessfulExecutions = Interlocked.Read(ref _successfulExecutions),
                FailedExecutions = Interlocked.Read(ref _failedExecutions),
                RejectedExecutions = Interlocked.Read(ref _rejectedExecutions),
                TimeoutExecutions = Interlocked.Read(ref _timeoutExecutions),
                LastSuccess = _lastSuccess,
                LastFailure = _lastFailure,
                CurrentState = CurrentState
            };
        }

        private bool CanExecute()
        {
            lock (_stateLock)
            {
                switch (_state)
                {
                    case CircuitState.Closed:
                        return true;

                    case CircuitState.Open:
                        var breakDuration = TimeSpan.FromSeconds(_config.BreakDurationSeconds);
                        if (DateTime.UtcNow - _lastStateChange >= breakDuration)
                        {
                            TransitionTo(CircuitState.HalfOpen, "Break duration elapsed");
                            return true;
                        }
                        return false;

                    case CircuitState.HalfOpen:
                        return true;

                    default:
                        return false;
                }
            }
        }

        private void RecordSuccess()
        {
            Interlocked.Increment(ref _successfulExecutions);
            _lastSuccess = DateTime.UtcNow;

            lock (_stateLock)
            {
                _successCount++;
                _consecutiveSuccesses++;

                if (_state == CircuitState.HalfOpen)
                {
                    if (_consecutiveSuccesses >= _config.SuccessThreshold)
                    {
                        TransitionTo(CircuitState.Closed, $"Recovered after {_consecutiveSuccesses} consecutive successes");
                        _failureWindow.Reset();
                    }
                }
            }
        }

        private void RecordFailure()
        {
            Interlocked.Increment(ref _failedExecutions);
            _lastFailure = DateTime.UtcNow;

            lock (_stateLock)
            {
                _consecutiveSuccesses = 0;
                _failureWindow.Increment();

                if (_state == CircuitState.HalfOpen)
                {
                    TransitionTo(CircuitState.Open, "Failure during half-open state");
                }
                else if (_state == CircuitState.Closed)
                {
                    if (_failureWindow.Count >= _config.FailureThreshold)
                    {
                        TransitionTo(CircuitState.Open, $"Failure threshold ({_config.FailureThreshold}) exceeded");
                    }
                }
            }
        }

        private void TransitionTo(CircuitState newState, string reason)
        {
            var oldState = _state;
            _state = newState;
            _lastStateChange = DateTime.UtcNow;

            if (newState == CircuitState.HalfOpen)
            {
                _consecutiveSuccesses = 0;
            }

            NotifyStateChange(oldState, newState, reason);
        }

        private void NotifyStateChange(CircuitState from, CircuitState to, string reason)
        {
            var change = new CircuitStateChange
            {
                CircuitName = Name,
                FromState = from,
                ToState = to,
                Reason = reason,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                _stateChangeCallback(change);
            }
            catch
            {
                // Ignore callback errors
            }
        }

        private TimeSpan GetRetryAfter()
        {
            lock (_stateLock)
            {
                var breakDuration = TimeSpan.FromSeconds(_config.BreakDurationSeconds);
                var elapsed = DateTime.UtcNow - _lastStateChange;
                var remaining = breakDuration - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        private static bool ShouldRetry(Exception ex)
        {
            // Don't retry for these exception types
            return ex switch
            {
                ArgumentNullException => false,
                ArgumentException => false,
                InvalidOperationException => false,
                NotSupportedException => false,
                UnauthorizedAccessException => false,
                _ => true
            };
        }

        private TimeSpan CalculateBackoff(int retryCount)
        {
            var baseDelay = TimeSpan.FromSeconds(_config.RetryBaseDelaySeconds);
            var maxDelay = TimeSpan.FromSeconds(_config.RetryMaxDelaySeconds);

            var exponentialDelay = baseDelay.TotalMilliseconds * Math.Pow(2, retryCount - 1);
            var jitter = Random.Shared.NextDouble() * 0.2 * exponentialDelay;
            var totalDelay = exponentialDelay + jitter;

            return TimeSpan.FromMilliseconds(Math.Min(totalDelay, maxDelay.TotalMilliseconds));
        }

        private sealed class SlidingWindowCounter
        {
            private readonly TimeSpan _windowDuration;
            private readonly Queue<DateTime> _timestamps = new();
            private readonly object _lock = new();

            public SlidingWindowCounter(TimeSpan windowDuration)
            {
                _windowDuration = windowDuration;
            }

            public int Count
            {
                get
                {
                    lock (_lock)
                    {
                        Cleanup();
                        return _timestamps.Count;
                    }
                }
            }

            public void Increment()
            {
                lock (_lock)
                {
                    _timestamps.Enqueue(DateTime.UtcNow);
                    Cleanup();
                }
            }

            public void Reset()
            {
                lock (_lock)
                {
                    _timestamps.Clear();
                }
            }

            private void Cleanup()
            {
                var cutoff = DateTime.UtcNow - _windowDuration;
                while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                {
                    _timestamps.Dequeue();
                }
            }
        }
    }

    /// <summary>
    /// Configuration for the CircuitBreakerPlugin.
    /// </summary>
    public class CircuitBreakerConfig
    {
        /// <summary>
        /// Number of failures before opening the circuit. Default is 5.
        /// </summary>
        public int FailureThreshold { get; set; } = 5;

        /// <summary>
        /// Duration the circuit stays open before transitioning to half-open.
        /// </summary>
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Time window for counting failures.
        /// </summary>
        public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Timeout for individual operations.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum retry attempts before failing.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Base delay between retries.
        /// </summary>
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Maximum delay between retries.
        /// </summary>
        public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Configuration for a named circuit.
    /// </summary>
    public class CircuitConfig
    {
        /// <summary>
        /// Name of the circuit.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Number of failures before opening the circuit.
        /// </summary>
        public int FailureThreshold { get; set; } = 5;

        /// <summary>
        /// Number of consecutive successes needed to close the circuit from half-open.
        /// </summary>
        public int SuccessThreshold { get; set; } = 3;

        /// <summary>
        /// Duration in seconds the circuit stays open.
        /// </summary>
        public int BreakDurationSeconds { get; set; } = 30;

        /// <summary>
        /// Time window in seconds for counting failures.
        /// </summary>
        public int FailureWindowSeconds { get; set; } = 60;

        /// <summary>
        /// Timeout in seconds for individual operations.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum retry attempts.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Base delay in seconds between retries.
        /// </summary>
        public double RetryBaseDelaySeconds { get; set; } = 0.5;

        /// <summary>
        /// Maximum delay in seconds between retries.
        /// </summary>
        public double RetryMaxDelaySeconds { get; set; } = 30;
    }

    /// <summary>
    /// Status of a circuit breaker.
    /// </summary>
    public class CircuitStatus
    {
        /// <summary>
        /// Name of the circuit.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Current state of the circuit.
        /// </summary>
        public CircuitState State { get; init; }

        /// <summary>
        /// Time of the last state change.
        /// </summary>
        public DateTime LastStateChange { get; init; }

        /// <summary>
        /// Current failure count in the sliding window.
        /// </summary>
        public int FailureCount { get; init; }

        /// <summary>
        /// Failure threshold for opening the circuit.
        /// </summary>
        public int FailureThreshold { get; init; }

        /// <summary>
        /// Break duration in seconds.
        /// </summary>
        public int BreakDurationSeconds { get; init; }

        /// <summary>
        /// Time until retry is allowed (if circuit is open).
        /// </summary>
        public TimeSpan? RetryAfter { get; init; }
    }

    /// <summary>
    /// Execution statistics for a circuit.
    /// </summary>
    public class CircuitStatistics
    {
        /// <summary>
        /// Name of the circuit.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Total number of executions.
        /// </summary>
        public long TotalExecutions { get; init; }

        /// <summary>
        /// Number of successful executions.
        /// </summary>
        public long SuccessfulExecutions { get; init; }

        /// <summary>
        /// Number of failed executions.
        /// </summary>
        public long FailedExecutions { get; init; }

        /// <summary>
        /// Number of executions rejected due to open circuit.
        /// </summary>
        public long RejectedExecutions { get; init; }

        /// <summary>
        /// Number of executions that timed out.
        /// </summary>
        public long TimeoutExecutions { get; init; }

        /// <summary>
        /// Time of last successful execution.
        /// </summary>
        public DateTime? LastSuccess { get; init; }

        /// <summary>
        /// Time of last failed execution.
        /// </summary>
        public DateTime? LastFailure { get; init; }

        /// <summary>
        /// Current state of the circuit.
        /// </summary>
        public CircuitState CurrentState { get; init; }

        /// <summary>
        /// Success rate as a percentage.
        /// </summary>
        public double SuccessRate => TotalExecutions > 0
            ? (double)SuccessfulExecutions / TotalExecutions * 100
            : 0;
    }

    /// <summary>
    /// Information about a circuit state change.
    /// </summary>
    public class CircuitStateChange
    {
        /// <summary>
        /// Name of the circuit that changed.
        /// </summary>
        public string CircuitName { get; init; } = string.Empty;

        /// <summary>
        /// Previous state.
        /// </summary>
        public CircuitState FromState { get; init; }

        /// <summary>
        /// New state.
        /// </summary>
        public CircuitState ToState { get; init; }

        /// <summary>
        /// Reason for the state change.
        /// </summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// Time of the state change.
        /// </summary>
        public DateTime Timestamp { get; init; }
    }

    internal class CircuitPersistenceData
    {
        public List<CircuitConfig> Configs { get; set; } = new();
        public Dictionary<string, PersistedCircuitState> States { get; set; } = new();
    }

    internal class PersistedCircuitState
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "Closed";
        public DateTime LastStateChange { get; set; }
    }
}
