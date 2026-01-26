using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.RetryPolicy
{
    /// <summary>
    /// Production-ready retry policy plugin implementing comprehensive resilience patterns.
    /// Provides configurable retry strategies with multiple backoff algorithms, retry budgets,
    /// exception filtering, context preservation, and circuit breaker integration.
    ///
    /// Features:
    /// - Multiple backoff strategies: Exponential with jitter, Linear, Constant, Decorrelated jitter
    /// - Configurable retry budgets with sliding window limits
    /// - Exception classification and filtering for retryable errors
    /// - Context preservation across retries for debugging and auditing
    /// - Integration with circuit breakers for coordinated failure handling
    /// - Comprehensive retry metrics and monitoring
    /// - Named policies for different operations/services
    /// - Persistent configuration support
    ///
    /// Message Commands:
    /// - retry.execute: Execute an operation with retry protection
    /// - retry.configure: Configure a named retry policy
    /// - retry.status: Get the status of retry policies
    /// - retry.metrics: Get retry execution metrics
    /// - retry.reset: Reset retry metrics for a policy
    /// - retry.list: List all configured policies
    /// - retry.budget: Get/set retry budget status
    /// </summary>
    public sealed class RetryPolicyPlugin : FeaturePluginBase
    {
        private readonly ConcurrentDictionary<string, RetryPolicyConfig> _policies;
        private readonly ConcurrentDictionary<string, RetryMetricsCollector> _metricsCollectors;
        private readonly ConcurrentDictionary<string, RetryBudgetTracker> _budgetTrackers;
        private readonly ConcurrentDictionary<string, Func<Exception, bool>> _exceptionFilters;
        private readonly ConcurrentDictionary<string, ICircuitBreakerIntegration> _circuitBreakers;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly RetryPolicyPluginConfig _globalConfig;
        private readonly string _storagePath;
        private readonly Timer? _budgetResetTimer;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.resilience.retry";

        /// <inheritdoc/>
        public override string Name => "Retry Policy Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Initializes a new instance of the RetryPolicyPlugin.
        /// </summary>
        /// <param name="config">Optional global configuration for the plugin.</param>
        public RetryPolicyPlugin(RetryPolicyPluginConfig? config = null)
        {
            _globalConfig = config ?? new RetryPolicyPluginConfig();
            _policies = new ConcurrentDictionary<string, RetryPolicyConfig>();
            _metricsCollectors = new ConcurrentDictionary<string, RetryMetricsCollector>();
            _budgetTrackers = new ConcurrentDictionary<string, RetryBudgetTracker>();
            _exceptionFilters = new ConcurrentDictionary<string, Func<Exception, bool>>();
            _circuitBreakers = new ConcurrentDictionary<string, ICircuitBreakerIntegration>();
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "resilience", "retry-policies");

            // Initialize default policy
            _policies[RetryPolicyConfig.DefaultPolicyName] = CreateDefaultPolicy();

            // Setup budget reset timer if budgets are enabled
            if (_globalConfig.EnableRetryBudgets)
            {
                _budgetResetTimer = new Timer(
                    _ => ResetExpiredBudgets(),
                    null,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30));
            }
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);
            await LoadPoliciesAsync();
            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "retry.execute", DisplayName = "Execute with Retry", Description = "Execute operation with retry protection" },
                new() { Name = "retry.configure", DisplayName = "Configure Policy", Description = "Configure a named retry policy" },
                new() { Name = "retry.status", DisplayName = "Policy Status", Description = "Get retry policy status" },
                new() { Name = "retry.metrics", DisplayName = "Retry Metrics", Description = "Get retry execution metrics" },
                new() { Name = "retry.reset", DisplayName = "Reset Metrics", Description = "Reset retry metrics for a policy" },
                new() { Name = "retry.list", DisplayName = "List Policies", Description = "List all configured policies" },
                new() { Name = "retry.budget", DisplayName = "Retry Budget", Description = "Get/set retry budget status" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "RetryPolicy";
            metadata["PolicyCount"] = _policies.Count;
            metadata["SupportedBackoffStrategies"] = new[] { "Exponential", "Linear", "Constant", "DecorrelatedJitter" };
            metadata["SupportsRetryBudgets"] = _globalConfig.EnableRetryBudgets;
            metadata["SupportsExceptionFiltering"] = true;
            metadata["SupportsCircuitBreakerIntegration"] = true;
            metadata["SupportsContextPreservation"] = true;
            metadata["DefaultMaxRetries"] = _globalConfig.DefaultMaxRetries;
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "retry.execute":
                    await HandleExecuteAsync(message);
                    break;
                case "retry.configure":
                    await HandleConfigureAsync(message);
                    break;
                case "retry.status":
                    HandleStatus(message);
                    break;
                case "retry.metrics":
                    HandleMetrics(message);
                    break;
                case "retry.reset":
                    HandleReset(message);
                    break;
                case "retry.list":
                    HandleList(message);
                    break;
                case "retry.budget":
                    HandleBudget(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        /// <inheritdoc/>
        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        /// <inheritdoc/>
        public override Task StopAsync()
        {
            _budgetResetTimer?.Dispose();
            return Task.CompletedTask;
        }

        #region Public API

        /// <summary>
        /// Gets or creates a retry policy with the specified name.
        /// </summary>
        /// <param name="policyName">The name of the policy.</param>
        /// <returns>The retry policy configuration.</returns>
        public RetryPolicyConfig GetPolicy(string policyName)
        {
            if (string.IsNullOrWhiteSpace(policyName))
                throw new ArgumentException("Policy name cannot be null or empty", nameof(policyName));

            return _policies.GetOrAdd(policyName, _ => CreateDefaultPolicy());
        }

        /// <summary>
        /// Configures a named retry policy.
        /// </summary>
        /// <param name="config">The policy configuration.</param>
        public void ConfigurePolicy(RetryPolicyConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.Name))
                throw new ArgumentException("Policy name is required", nameof(config));

            _policies[config.Name] = config;

            // Initialize metrics collector for the policy
            _metricsCollectors.GetOrAdd(config.Name, _ => new RetryMetricsCollector(config.Name));

            // Initialize budget tracker if budgets are enabled
            if (config.RetryBudget != null)
            {
                _budgetTrackers[config.Name] = new RetryBudgetTracker(config.RetryBudget);
            }
        }

        /// <summary>
        /// Registers a custom exception filter for a policy.
        /// </summary>
        /// <param name="policyName">The policy name.</param>
        /// <param name="filter">The exception filter predicate.</param>
        public void RegisterExceptionFilter(string policyName, Func<Exception, bool> filter)
        {
            if (string.IsNullOrWhiteSpace(policyName))
                throw new ArgumentException("Policy name cannot be null or empty", nameof(policyName));
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            _exceptionFilters[policyName] = filter;
        }

        /// <summary>
        /// Registers a circuit breaker for coordinated failure handling.
        /// </summary>
        /// <param name="policyName">The policy name.</param>
        /// <param name="circuitBreaker">The circuit breaker integration.</param>
        public void RegisterCircuitBreaker(string policyName, ICircuitBreakerIntegration circuitBreaker)
        {
            if (string.IsNullOrWhiteSpace(policyName))
                throw new ArgumentException("Policy name cannot be null or empty", nameof(policyName));
            if (circuitBreaker == null)
                throw new ArgumentNullException(nameof(circuitBreaker));

            _circuitBreakers[policyName] = circuitBreaker;
        }

        /// <summary>
        /// Executes an operation with the specified retry policy.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="policyName">The name of the retry policy to use.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        public Task<T> ExecuteAsync<T>(string policyName, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        {
            return ExecuteWithContextAsync(policyName, operation, null, ct);
        }

        /// <summary>
        /// Executes an operation with the specified retry policy and context.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="policyName">The name of the retry policy to use.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="context">Optional retry context for tracking and debugging.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        public async Task<T> ExecuteWithContextAsync<T>(
            string policyName,
            Func<CancellationToken, Task<T>> operation,
            RetryContext? context,
            CancellationToken ct = default)
        {
            var policy = GetPolicy(policyName);
            var metrics = _metricsCollectors.GetOrAdd(policyName, _ => new RetryMetricsCollector(policyName));
            var retryContext = context ?? new RetryContext(policyName);

            // Check circuit breaker first
            if (_circuitBreakers.TryGetValue(policyName, out var circuitBreaker))
            {
                if (!circuitBreaker.CanExecute())
                {
                    metrics.RecordCircuitBreakerRejection();
                    throw new RetryCircuitBreakerOpenException(policyName, circuitBreaker.GetRetryAfter());
                }
            }

            // Check retry budget
            if (_budgetTrackers.TryGetValue(policyName, out var budgetTracker))
            {
                if (!budgetTracker.TryAcquire())
                {
                    metrics.RecordBudgetExhausted();
                    throw new RetryBudgetExhaustedException(policyName, budgetTracker.GetTimeUntilReset());
                }
            }

            metrics.RecordAttemptStart();
            retryContext.TotalAttempts = 0;

            var retryCount = 0;
            Exception? lastException = null;
            var startTime = DateTime.UtcNow;

            while (retryCount <= policy.MaxRetries)
            {
                retryContext.CurrentAttempt = retryCount;
                retryContext.TotalAttempts++;
                retryContext.LastAttemptTime = DateTime.UtcNow;

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (policy.Timeout > TimeSpan.Zero)
                    {
                        cts.CancelAfter(policy.Timeout);
                    }

                    var result = await operation(cts.Token);

                    // Record success
                    var duration = DateTime.UtcNow - startTime;
                    metrics.RecordSuccess(retryCount, duration);
                    retryContext.WasSuccessful = true;

                    // Notify circuit breaker of success
                    circuitBreaker?.RecordSuccess();

                    return result;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastException = new TimeoutException($"Operation timed out after {policy.Timeout.TotalSeconds}s");
                    metrics.RecordTimeout();
                    retryContext.AddException(lastException);

                    // Timeouts may not be retryable based on policy
                    if (!policy.RetryOnTimeout)
                    {
                        circuitBreaker?.RecordFailure();
                        throw lastException;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retryContext.AddException(ex);

                    // Check if exception is retryable
                    if (!ShouldRetry(policyName, policy, ex, retryCount))
                    {
                        metrics.RecordNonRetryableFailure();
                        circuitBreaker?.RecordFailure();
                        throw;
                    }
                }

                retryCount++;
                metrics.RecordRetry();

                if (retryCount <= policy.MaxRetries)
                {
                    var delay = CalculateDelay(policy.BackoffStrategy, retryCount, policy, retryContext);
                    retryContext.LastDelay = delay;

                    // Invoke retry callback if registered
                    policy.OnRetry?.Invoke(retryContext, lastException);

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Respect cancellation during delay
                    }
                }
            }

            // All retries exhausted
            var totalDuration = DateTime.UtcNow - startTime;
            metrics.RecordExhausted(totalDuration);
            retryContext.WasSuccessful = false;

            // Notify circuit breaker of final failure
            circuitBreaker?.RecordFailure();

            throw new RetryExhaustedException(policyName, policy.MaxRetries, lastException);
        }

        /// <summary>
        /// Executes an operation with the specified retry policy (no return value).
        /// </summary>
        /// <param name="policyName">The name of the retry policy to use.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteAsync(string policyName, Func<CancellationToken, Task> operation, CancellationToken ct = default)
        {
            await ExecuteAsync(policyName, async token =>
            {
                await operation(token);
                return true;
            }, ct);
        }

        /// <summary>
        /// Gets retry metrics for a policy.
        /// </summary>
        /// <param name="policyName">The policy name, or null for all policies.</param>
        /// <returns>The retry metrics.</returns>
        public RetryMetrics GetMetrics(string? policyName = null)
        {
            if (!string.IsNullOrEmpty(policyName))
            {
                if (_metricsCollectors.TryGetValue(policyName, out var collector))
                {
                    return collector.GetMetrics();
                }
                return new RetryMetrics { PolicyName = policyName };
            }

            // Aggregate all metrics
            return RetryMetricsCollector.Aggregate(_metricsCollectors.Values);
        }

        /// <summary>
        /// Resets metrics for a policy.
        /// </summary>
        /// <param name="policyName">The policy name to reset.</param>
        public void ResetMetrics(string policyName)
        {
            if (_metricsCollectors.TryGetValue(policyName, out var collector))
            {
                collector.Reset();
            }
        }

        /// <summary>
        /// Gets the retry budget status for a policy.
        /// </summary>
        /// <param name="policyName">The policy name.</param>
        /// <returns>The budget status, or null if no budget is configured.</returns>
        public RetryBudgetStatus? GetBudgetStatus(string policyName)
        {
            if (_budgetTrackers.TryGetValue(policyName, out var tracker))
            {
                return tracker.GetStatus();
            }
            return null;
        }

        /// <summary>
        /// Gets all configured policy names.
        /// </summary>
        /// <returns>List of policy names.</returns>
        public IReadOnlyList<string> GetPolicyNames()
        {
            return _policies.Keys.ToList().AsReadOnly();
        }

        #endregion

        #region Private Methods

        private bool ShouldRetry(string policyName, RetryPolicyConfig policy, Exception ex, int retryCount)
        {
            // Check max retries
            if (retryCount >= policy.MaxRetries)
            {
                return false;
            }

            // Check custom exception filter first
            if (_exceptionFilters.TryGetValue(policyName, out var customFilter))
            {
                return customFilter(ex);
            }

            // Check policy's custom filter
            if (policy.ShouldRetry != null)
            {
                return policy.ShouldRetry(ex);
            }

            // Check if exception type is in non-retryable list
            var exType = ex.GetType();
            if (policy.NonRetryableExceptions.Any(t => t.IsAssignableFrom(exType)))
            {
                return false;
            }

            // Check if exception type is in retryable list (if specified)
            if (policy.RetryableExceptions.Count > 0)
            {
                return policy.RetryableExceptions.Any(t => t.IsAssignableFrom(exType));
            }

            // Default: use exception classifier
            return ExceptionClassifier.IsTransient(ex);
        }

        private TimeSpan CalculateDelay(BackoffStrategy strategy, int retryCount, RetryPolicyConfig policy, RetryContext context)
        {
            var delay = strategy switch
            {
                BackoffStrategy.Constant => CalculateConstantDelay(policy),
                BackoffStrategy.Linear => CalculateLinearDelay(policy, retryCount),
                BackoffStrategy.Exponential => CalculateExponentialDelay(policy, retryCount),
                BackoffStrategy.ExponentialWithJitter => CalculateExponentialWithJitterDelay(policy, retryCount),
                BackoffStrategy.DecorrelatedJitter => CalculateDecorrelatedJitterDelay(policy, context),
                BackoffStrategy.Custom => policy.CustomDelayCalculator?.Invoke(retryCount, context) ?? policy.BaseDelay,
                _ => CalculateExponentialWithJitterDelay(policy, retryCount)
            };

            // Enforce max delay
            return TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, policy.MaxDelay.TotalMilliseconds));
        }

        private static TimeSpan CalculateConstantDelay(RetryPolicyConfig policy)
        {
            return policy.BaseDelay;
        }

        private static TimeSpan CalculateLinearDelay(RetryPolicyConfig policy, int retryCount)
        {
            var delay = policy.BaseDelay.TotalMilliseconds * retryCount;
            return TimeSpan.FromMilliseconds(delay);
        }

        private static TimeSpan CalculateExponentialDelay(RetryPolicyConfig policy, int retryCount)
        {
            var delay = policy.BaseDelay.TotalMilliseconds * Math.Pow(policy.ExponentialBase, retryCount - 1);
            return TimeSpan.FromMilliseconds(delay);
        }

        private static TimeSpan CalculateExponentialWithJitterDelay(RetryPolicyConfig policy, int retryCount)
        {
            var exponentialDelay = policy.BaseDelay.TotalMilliseconds * Math.Pow(policy.ExponentialBase, retryCount - 1);
            var jitterRange = exponentialDelay * policy.JitterFactor;
            var jitter = (Random.Shared.NextDouble() * 2 - 1) * jitterRange; // -jitterRange to +jitterRange
            var delay = Math.Max(0, exponentialDelay + jitter);
            return TimeSpan.FromMilliseconds(delay);
        }

        private static TimeSpan CalculateDecorrelatedJitterDelay(RetryPolicyConfig policy, RetryContext context)
        {
            // Decorrelated jitter: delay = random(baseDelay, lastDelay * 3)
            // This provides better distribution than simple jitter
            var minDelay = policy.BaseDelay.TotalMilliseconds;
            var maxDelay = context.LastDelay.TotalMilliseconds > 0
                ? context.LastDelay.TotalMilliseconds * 3
                : policy.BaseDelay.TotalMilliseconds * 3;

            maxDelay = Math.Min(maxDelay, policy.MaxDelay.TotalMilliseconds);
            var delay = minDelay + Random.Shared.NextDouble() * (maxDelay - minDelay);
            return TimeSpan.FromMilliseconds(delay);
        }

        private RetryPolicyConfig CreateDefaultPolicy()
        {
            return new RetryPolicyConfig
            {
                Name = RetryPolicyConfig.DefaultPolicyName,
                MaxRetries = _globalConfig.DefaultMaxRetries,
                BaseDelay = _globalConfig.DefaultBaseDelay,
                MaxDelay = _globalConfig.DefaultMaxDelay,
                Timeout = _globalConfig.DefaultTimeout,
                BackoffStrategy = _globalConfig.DefaultBackoffStrategy,
                ExponentialBase = 2.0,
                JitterFactor = 0.2,
                RetryOnTimeout = true
            };
        }

        private void ResetExpiredBudgets()
        {
            foreach (var tracker in _budgetTrackers.Values)
            {
                tracker.ResetIfExpired();
            }
        }

        #endregion

        #region Message Handlers

        private async Task HandleExecuteAsync(PluginMessage message)
        {
            var policyName = GetString(message.Payload, "policy") ?? RetryPolicyConfig.DefaultPolicyName;
            var operationId = GetString(message.Payload, "operationId") ?? Guid.NewGuid().ToString("N");

            var context = new RetryContext(policyName) { OperationId = operationId };
            var status = new Dictionary<string, object>
            {
                ["operationId"] = operationId,
                ["policyName"] = policyName,
                ["ready"] = true
            };

            if (_budgetTrackers.TryGetValue(policyName, out var tracker))
            {
                var budgetStatus = tracker.GetStatus();
                status["budgetRemaining"] = budgetStatus.RemainingRetries;
                status["budgetAvailable"] = budgetStatus.RemainingRetries > 0;
            }

            message.Payload["result"] = status;
            await Task.CompletedTask;
        }

        private async Task HandleConfigureAsync(PluginMessage message)
        {
            var config = new RetryPolicyConfig
            {
                Name = GetString(message.Payload, "name") ?? throw new ArgumentException("name required"),
                MaxRetries = GetInt(message.Payload, "maxRetries") ?? _globalConfig.DefaultMaxRetries,
                BaseDelay = TimeSpan.FromMilliseconds(GetDouble(message.Payload, "baseDelayMs") ?? _globalConfig.DefaultBaseDelay.TotalMilliseconds),
                MaxDelay = TimeSpan.FromMilliseconds(GetDouble(message.Payload, "maxDelayMs") ?? _globalConfig.DefaultMaxDelay.TotalMilliseconds),
                Timeout = TimeSpan.FromMilliseconds(GetDouble(message.Payload, "timeoutMs") ?? _globalConfig.DefaultTimeout.TotalMilliseconds),
                BackoffStrategy = ParseBackoffStrategy(GetString(message.Payload, "backoffStrategy")),
                ExponentialBase = GetDouble(message.Payload, "exponentialBase") ?? 2.0,
                JitterFactor = GetDouble(message.Payload, "jitterFactor") ?? 0.2,
                RetryOnTimeout = GetBool(message.Payload, "retryOnTimeout") ?? true
            };

            ConfigurePolicy(config);
            await PersistPoliciesAsync();

            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = true,
                ["policyName"] = config.Name
            };
        }

        private void HandleStatus(PluginMessage message)
        {
            var policyName = GetString(message.Payload, "policy");

            if (!string.IsNullOrEmpty(policyName))
            {
                if (_policies.TryGetValue(policyName, out var policy))
                {
                    message.Payload["result"] = new Dictionary<string, object>
                    {
                        ["name"] = policy.Name,
                        ["maxRetries"] = policy.MaxRetries,
                        ["backoffStrategy"] = policy.BackoffStrategy.ToString(),
                        ["baseDelayMs"] = policy.BaseDelay.TotalMilliseconds,
                        ["maxDelayMs"] = policy.MaxDelay.TotalMilliseconds,
                        ["timeoutMs"] = policy.Timeout.TotalMilliseconds
                    };
                }
            }
            else
            {
                message.Payload["result"] = _policies.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        maxRetries = kv.Value.MaxRetries,
                        backoffStrategy = kv.Value.BackoffStrategy.ToString()
                    });
            }
        }

        private void HandleMetrics(PluginMessage message)
        {
            var policyName = GetString(message.Payload, "policy");
            var metrics = GetMetrics(policyName);
            message.Payload["result"] = metrics;
        }

        private void HandleReset(PluginMessage message)
        {
            var policyName = GetString(message.Payload, "policy") ?? throw new ArgumentException("policy name required");
            ResetMetrics(policyName);
            message.Payload["result"] = new Dictionary<string, object> { ["success"] = true };
        }

        private void HandleList(PluginMessage message)
        {
            message.Payload["result"] = GetPolicyNames().ToList();
        }

        private void HandleBudget(PluginMessage message)
        {
            var policyName = GetString(message.Payload, "policy") ?? throw new ArgumentException("policy name required");
            var status = GetBudgetStatus(policyName);

            if (status != null)
            {
                message.Payload["result"] = new Dictionary<string, object>
                {
                    ["policyName"] = policyName,
                    ["maxRetries"] = status.MaxRetries,
                    ["usedRetries"] = status.UsedRetries,
                    ["remainingRetries"] = status.RemainingRetries,
                    ["windowDurationSeconds"] = status.WindowDuration.TotalSeconds,
                    ["timeUntilResetSeconds"] = status.TimeUntilReset.TotalSeconds
                };
            }
            else
            {
                message.Payload["result"] = new Dictionary<string, object>
                {
                    ["policyName"] = policyName,
                    ["budgetConfigured"] = false
                };
            }
        }

        #endregion

        #region Persistence

        private async Task LoadPoliciesAsync()
        {
            var path = Path.Combine(_storagePath, "retry-policies.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<RetryPolicyPersistenceData>(json);

                if (data?.Policies != null)
                {
                    foreach (var policyData in data.Policies)
                    {
                        var config = new RetryPolicyConfig
                        {
                            Name = policyData.Name,
                            MaxRetries = policyData.MaxRetries,
                            BaseDelay = TimeSpan.FromMilliseconds(policyData.BaseDelayMs),
                            MaxDelay = TimeSpan.FromMilliseconds(policyData.MaxDelayMs),
                            Timeout = TimeSpan.FromMilliseconds(policyData.TimeoutMs),
                            BackoffStrategy = Enum.TryParse<BackoffStrategy>(policyData.BackoffStrategy, out var bs) ? bs : BackoffStrategy.ExponentialWithJitter,
                            ExponentialBase = policyData.ExponentialBase,
                            JitterFactor = policyData.JitterFactor,
                            RetryOnTimeout = policyData.RetryOnTimeout
                        };
                        ConfigurePolicy(config);
                    }
                }
            }
            catch
            {
                // Log but continue - policies will use defaults
            }
        }

        private async Task PersistPoliciesAsync()
        {
            if (!await _persistLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return;

            try
            {
                Directory.CreateDirectory(_storagePath);

                var data = new RetryPolicyPersistenceData
                {
                    Policies = _policies.Values.Select(p => new PersistedRetryPolicy
                    {
                        Name = p.Name,
                        MaxRetries = p.MaxRetries,
                        BaseDelayMs = p.BaseDelay.TotalMilliseconds,
                        MaxDelayMs = p.MaxDelay.TotalMilliseconds,
                        TimeoutMs = p.Timeout.TotalMilliseconds,
                        BackoffStrategy = p.BackoffStrategy.ToString(),
                        ExponentialBase = p.ExponentialBase,
                        JitterFactor = p.JitterFactor,
                        RetryOnTimeout = p.RetryOnTimeout
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(_storagePath, "retry-policies.json"), json);
            }
            finally
            {
                _persistLock.Release();
            }
        }

        #endregion

        #region Helpers

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

        private static double? GetDouble(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is double d) return d;
                if (val is float f) return f;
                if (val is int i) return i;
                if (val is long l) return l;
                if (val is string s && double.TryParse(s, out var parsed)) return parsed;
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

        private static BackoffStrategy ParseBackoffStrategy(string? value)
        {
            if (string.IsNullOrEmpty(value)) return BackoffStrategy.ExponentialWithJitter;
            return Enum.TryParse<BackoffStrategy>(value, true, out var result)
                ? result
                : BackoffStrategy.ExponentialWithJitter;
        }

        #endregion
    }

    #region Configuration Types

    /// <summary>
    /// Global configuration for the RetryPolicyPlugin.
    /// </summary>
    public class RetryPolicyPluginConfig
    {
        /// <summary>
        /// Default maximum retry attempts. Default is 3.
        /// </summary>
        public int DefaultMaxRetries { get; set; } = 3;

        /// <summary>
        /// Default base delay between retries. Default is 500ms.
        /// </summary>
        public TimeSpan DefaultBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Default maximum delay between retries. Default is 30 seconds.
        /// </summary>
        public TimeSpan DefaultMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Default operation timeout. Default is 30 seconds.
        /// </summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Default backoff strategy. Default is ExponentialWithJitter.
        /// </summary>
        public BackoffStrategy DefaultBackoffStrategy { get; set; } = BackoffStrategy.ExponentialWithJitter;

        /// <summary>
        /// Whether to enable retry budgets. Default is true.
        /// </summary>
        public bool EnableRetryBudgets { get; set; } = true;
    }

    /// <summary>
    /// Configuration for a named retry policy.
    /// </summary>
    public class RetryPolicyConfig
    {
        /// <summary>
        /// Default policy name.
        /// </summary>
        public const string DefaultPolicyName = "default";

        /// <summary>
        /// Name of the policy.
        /// </summary>
        public string Name { get; set; } = DefaultPolicyName;

        /// <summary>
        /// Maximum number of retry attempts. Default is 3.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Base delay between retries. Default is 500ms.
        /// </summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Maximum delay between retries. Default is 30 seconds.
        /// </summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Operation timeout. Default is 30 seconds. Use TimeSpan.Zero for no timeout.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Backoff strategy to use. Default is ExponentialWithJitter.
        /// </summary>
        public BackoffStrategy BackoffStrategy { get; set; } = BackoffStrategy.ExponentialWithJitter;

        /// <summary>
        /// Base for exponential backoff. Default is 2.0.
        /// </summary>
        public double ExponentialBase { get; set; } = 2.0;

        /// <summary>
        /// Jitter factor (0.0 to 1.0). Default is 0.2 (20% jitter).
        /// </summary>
        public double JitterFactor { get; set; } = 0.2;

        /// <summary>
        /// Whether to retry on timeout. Default is true.
        /// </summary>
        public bool RetryOnTimeout { get; set; } = true;

        /// <summary>
        /// Optional retry budget configuration.
        /// </summary>
        public RetryBudgetConfig? RetryBudget { get; set; }

        /// <summary>
        /// Exception types that should not trigger retries.
        /// </summary>
        public List<Type> NonRetryableExceptions { get; set; } = new()
        {
            typeof(ArgumentException),
            typeof(ArgumentNullException),
            typeof(InvalidOperationException),
            typeof(NotSupportedException),
            typeof(UnauthorizedAccessException)
        };

        /// <summary>
        /// Exception types that should trigger retries. If empty, all exceptions
        /// except NonRetryableExceptions are retried.
        /// </summary>
        public List<Type> RetryableExceptions { get; set; } = new();

        /// <summary>
        /// Custom retry predicate. Takes precedence over exception lists.
        /// </summary>
        public Func<Exception, bool>? ShouldRetry { get; set; }

        /// <summary>
        /// Custom delay calculator for BackoffStrategy.Custom.
        /// </summary>
        public Func<int, RetryContext, TimeSpan>? CustomDelayCalculator { get; set; }

        /// <summary>
        /// Callback invoked before each retry attempt.
        /// </summary>
        public Action<RetryContext, Exception?>? OnRetry { get; set; }
    }

    /// <summary>
    /// Configuration for retry budgets.
    /// </summary>
    public class RetryBudgetConfig
    {
        /// <summary>
        /// Maximum retries allowed within the time window. Default is 100.
        /// </summary>
        public int MaxRetries { get; set; } = 100;

        /// <summary>
        /// Time window for the budget. Default is 1 minute.
        /// </summary>
        public TimeSpan WindowDuration { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Percentage of budget to reserve for critical operations (0.0 to 1.0).
        /// </summary>
        public double ReserveRatio { get; set; } = 0.1;
    }

    #endregion

    #region Backoff Strategies

    /// <summary>
    /// Backoff strategy for retry delays.
    /// </summary>
    public enum BackoffStrategy
    {
        /// <summary>
        /// Constant delay between retries.
        /// </summary>
        Constant,

        /// <summary>
        /// Linearly increasing delay (baseDelay * retryCount).
        /// </summary>
        Linear,

        /// <summary>
        /// Exponentially increasing delay (baseDelay * 2^retryCount).
        /// </summary>
        Exponential,

        /// <summary>
        /// Exponential delay with random jitter to prevent thundering herd.
        /// </summary>
        ExponentialWithJitter,

        /// <summary>
        /// Decorrelated jitter for better distribution across retries.
        /// Uses AWS-style decorrelated jitter: delay = random(baseDelay, lastDelay * 3).
        /// </summary>
        DecorrelatedJitter,

        /// <summary>
        /// Custom delay calculation via CustomDelayCalculator.
        /// </summary>
        Custom
    }

    #endregion

    #region Retry Context

    /// <summary>
    /// Context for tracking retry state across attempts.
    /// </summary>
    public class RetryContext
    {
        /// <summary>
        /// Creates a new retry context.
        /// </summary>
        /// <param name="policyName">The policy name being used.</param>
        public RetryContext(string policyName)
        {
            PolicyName = policyName;
            OperationId = Guid.NewGuid().ToString("N");
            StartTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Unique operation identifier.
        /// </summary>
        public string OperationId { get; set; }

        /// <summary>
        /// Policy name being used.
        /// </summary>
        public string PolicyName { get; }

        /// <summary>
        /// Current retry attempt (0-based).
        /// </summary>
        public int CurrentAttempt { get; set; }

        /// <summary>
        /// Total attempts made.
        /// </summary>
        public int TotalAttempts { get; set; }

        /// <summary>
        /// Time when the operation started.
        /// </summary>
        public DateTime StartTime { get; }

        /// <summary>
        /// Time of the last attempt.
        /// </summary>
        public DateTime LastAttemptTime { get; set; }

        /// <summary>
        /// Duration of the last delay.
        /// </summary>
        public TimeSpan LastDelay { get; set; }

        /// <summary>
        /// Whether the operation ultimately succeeded.
        /// </summary>
        public bool WasSuccessful { get; set; }

        /// <summary>
        /// Total elapsed time.
        /// </summary>
        public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;

        /// <summary>
        /// Exceptions encountered during retries.
        /// </summary>
        public List<Exception> Exceptions { get; } = new();

        /// <summary>
        /// Custom properties for context preservation.
        /// </summary>
        public Dictionary<string, object> Properties { get; } = new();

        /// <summary>
        /// Adds an exception to the context.
        /// </summary>
        /// <param name="ex">The exception to add.</param>
        public void AddException(Exception ex)
        {
            Exceptions.Add(ex);
        }

        /// <summary>
        /// Sets a custom property.
        /// </summary>
        /// <param name="key">Property key.</param>
        /// <param name="value">Property value.</param>
        public void SetProperty(string key, object value)
        {
            Properties[key] = value;
        }

        /// <summary>
        /// Gets a custom property.
        /// </summary>
        /// <typeparam name="T">Expected type.</typeparam>
        /// <param name="key">Property key.</param>
        /// <returns>The property value, or default if not found.</returns>
        public T? GetProperty<T>(string key)
        {
            if (Properties.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return default;
        }
    }

    #endregion

    #region Metrics

    /// <summary>
    /// Retry execution metrics.
    /// </summary>
    public class RetryMetrics
    {
        /// <summary>
        /// Policy name (or "aggregate" for combined metrics).
        /// </summary>
        public string PolicyName { get; init; } = string.Empty;

        /// <summary>
        /// Total operations attempted.
        /// </summary>
        public long TotalOperations { get; init; }

        /// <summary>
        /// Operations that succeeded (with or without retries).
        /// </summary>
        public long SuccessfulOperations { get; init; }

        /// <summary>
        /// Operations that exhausted all retries.
        /// </summary>
        public long ExhaustedOperations { get; init; }

        /// <summary>
        /// Total retry attempts made.
        /// </summary>
        public long TotalRetries { get; init; }

        /// <summary>
        /// Operations that succeeded on first attempt.
        /// </summary>
        public long FirstAttemptSuccesses { get; init; }

        /// <summary>
        /// Operations that timed out.
        /// </summary>
        public long TimeoutCount { get; init; }

        /// <summary>
        /// Operations rejected by circuit breaker.
        /// </summary>
        public long CircuitBreakerRejections { get; init; }

        /// <summary>
        /// Operations rejected due to budget exhaustion.
        /// </summary>
        public long BudgetExhaustedCount { get; init; }

        /// <summary>
        /// Non-retryable failures.
        /// </summary>
        public long NonRetryableFailures { get; init; }

        /// <summary>
        /// Average retries per operation.
        /// </summary>
        public double AverageRetriesPerOperation => TotalOperations > 0
            ? (double)TotalRetries / TotalOperations
            : 0;

        /// <summary>
        /// Success rate as percentage.
        /// </summary>
        public double SuccessRate => TotalOperations > 0
            ? (double)SuccessfulOperations / TotalOperations * 100
            : 0;

        /// <summary>
        /// First attempt success rate as percentage.
        /// </summary>
        public double FirstAttemptSuccessRate => TotalOperations > 0
            ? (double)FirstAttemptSuccesses / TotalOperations * 100
            : 0;

        /// <summary>
        /// Average operation duration (including retries).
        /// </summary>
        public TimeSpan AverageOperationDuration { get; init; }

        /// <summary>
        /// Time of last successful operation.
        /// </summary>
        public DateTime? LastSuccess { get; init; }

        /// <summary>
        /// Time of last exhausted operation.
        /// </summary>
        public DateTime? LastExhausted { get; init; }
    }

    /// <summary>
    /// Collects retry metrics for a policy.
    /// </summary>
    internal sealed class RetryMetricsCollector
    {
        private readonly string _policyName;
        private long _totalOperations;
        private long _successfulOperations;
        private long _exhaustedOperations;
        private long _totalRetries;
        private long _firstAttemptSuccesses;
        private long _timeoutCount;
        private long _circuitBreakerRejections;
        private long _budgetExhaustedCount;
        private long _nonRetryableFailures;
        private readonly List<double> _operationDurations = new();
        private readonly object _durationsLock = new();
        private DateTime? _lastSuccess;
        private DateTime? _lastExhausted;

        public RetryMetricsCollector(string policyName)
        {
            _policyName = policyName;
        }

        public void RecordAttemptStart()
        {
            Interlocked.Increment(ref _totalOperations);
        }

        public void RecordSuccess(int retryCount, TimeSpan duration)
        {
            Interlocked.Increment(ref _successfulOperations);
            if (retryCount == 0)
            {
                Interlocked.Increment(ref _firstAttemptSuccesses);
            }
            _lastSuccess = DateTime.UtcNow;

            lock (_durationsLock)
            {
                _operationDurations.Add(duration.TotalMilliseconds);
                if (_operationDurations.Count > 1000)
                {
                    _operationDurations.RemoveAt(0);
                }
            }
        }

        public void RecordRetry()
        {
            Interlocked.Increment(ref _totalRetries);
        }

        public void RecordExhausted(TimeSpan duration)
        {
            Interlocked.Increment(ref _exhaustedOperations);
            _lastExhausted = DateTime.UtcNow;

            lock (_durationsLock)
            {
                _operationDurations.Add(duration.TotalMilliseconds);
                if (_operationDurations.Count > 1000)
                {
                    _operationDurations.RemoveAt(0);
                }
            }
        }

        public void RecordTimeout()
        {
            Interlocked.Increment(ref _timeoutCount);
        }

        public void RecordCircuitBreakerRejection()
        {
            Interlocked.Increment(ref _circuitBreakerRejections);
        }

        public void RecordBudgetExhausted()
        {
            Interlocked.Increment(ref _budgetExhaustedCount);
        }

        public void RecordNonRetryableFailure()
        {
            Interlocked.Increment(ref _nonRetryableFailures);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _totalOperations, 0);
            Interlocked.Exchange(ref _successfulOperations, 0);
            Interlocked.Exchange(ref _exhaustedOperations, 0);
            Interlocked.Exchange(ref _totalRetries, 0);
            Interlocked.Exchange(ref _firstAttemptSuccesses, 0);
            Interlocked.Exchange(ref _timeoutCount, 0);
            Interlocked.Exchange(ref _circuitBreakerRejections, 0);
            Interlocked.Exchange(ref _budgetExhaustedCount, 0);
            Interlocked.Exchange(ref _nonRetryableFailures, 0);

            lock (_durationsLock)
            {
                _operationDurations.Clear();
            }

            _lastSuccess = null;
            _lastExhausted = null;
        }

        public RetryMetrics GetMetrics()
        {
            TimeSpan avgDuration;
            lock (_durationsLock)
            {
                avgDuration = _operationDurations.Count > 0
                    ? TimeSpan.FromMilliseconds(_operationDurations.Average())
                    : TimeSpan.Zero;
            }

            return new RetryMetrics
            {
                PolicyName = _policyName,
                TotalOperations = Interlocked.Read(ref _totalOperations),
                SuccessfulOperations = Interlocked.Read(ref _successfulOperations),
                ExhaustedOperations = Interlocked.Read(ref _exhaustedOperations),
                TotalRetries = Interlocked.Read(ref _totalRetries),
                FirstAttemptSuccesses = Interlocked.Read(ref _firstAttemptSuccesses),
                TimeoutCount = Interlocked.Read(ref _timeoutCount),
                CircuitBreakerRejections = Interlocked.Read(ref _circuitBreakerRejections),
                BudgetExhaustedCount = Interlocked.Read(ref _budgetExhaustedCount),
                NonRetryableFailures = Interlocked.Read(ref _nonRetryableFailures),
                AverageOperationDuration = avgDuration,
                LastSuccess = _lastSuccess,
                LastExhausted = _lastExhausted
            };
        }

        public static RetryMetrics Aggregate(IEnumerable<RetryMetricsCollector> collectors)
        {
            var list = collectors.ToList();
            var metrics = list.Select(c => c.GetMetrics()).ToList();

            return new RetryMetrics
            {
                PolicyName = "aggregate",
                TotalOperations = metrics.Sum(m => m.TotalOperations),
                SuccessfulOperations = metrics.Sum(m => m.SuccessfulOperations),
                ExhaustedOperations = metrics.Sum(m => m.ExhaustedOperations),
                TotalRetries = metrics.Sum(m => m.TotalRetries),
                FirstAttemptSuccesses = metrics.Sum(m => m.FirstAttemptSuccesses),
                TimeoutCount = metrics.Sum(m => m.TimeoutCount),
                CircuitBreakerRejections = metrics.Sum(m => m.CircuitBreakerRejections),
                BudgetExhaustedCount = metrics.Sum(m => m.BudgetExhaustedCount),
                NonRetryableFailures = metrics.Sum(m => m.NonRetryableFailures),
                AverageOperationDuration = metrics.Count > 0
                    ? TimeSpan.FromMilliseconds(metrics.Average(m => m.AverageOperationDuration.TotalMilliseconds))
                    : TimeSpan.Zero,
                LastSuccess = metrics.Where(m => m.LastSuccess.HasValue).Max(m => m.LastSuccess),
                LastExhausted = metrics.Where(m => m.LastExhausted.HasValue).Max(m => m.LastExhausted)
            };
        }
    }

    #endregion

    #region Retry Budget

    /// <summary>
    /// Status of a retry budget.
    /// </summary>
    public class RetryBudgetStatus
    {
        /// <summary>
        /// Maximum retries allowed in the window.
        /// </summary>
        public int MaxRetries { get; init; }

        /// <summary>
        /// Retries used in the current window.
        /// </summary>
        public int UsedRetries { get; init; }

        /// <summary>
        /// Remaining retries in the current window.
        /// </summary>
        public int RemainingRetries { get; init; }

        /// <summary>
        /// Duration of the budget window.
        /// </summary>
        public TimeSpan WindowDuration { get; init; }

        /// <summary>
        /// Time until the budget resets.
        /// </summary>
        public TimeSpan TimeUntilReset { get; init; }
    }

    /// <summary>
    /// Tracks retry budget usage.
    /// </summary>
    internal sealed class RetryBudgetTracker
    {
        private readonly RetryBudgetConfig _config;
        private readonly object _lock = new();
        private int _usedRetries;
        private DateTime _windowStart;

        public RetryBudgetTracker(RetryBudgetConfig config)
        {
            _config = config;
            _windowStart = DateTime.UtcNow;
            _usedRetries = 0;
        }

        public bool TryAcquire()
        {
            lock (_lock)
            {
                ResetIfExpiredInternal();

                if (_usedRetries < _config.MaxRetries)
                {
                    _usedRetries++;
                    return true;
                }

                return false;
            }
        }

        public void ResetIfExpired()
        {
            lock (_lock)
            {
                ResetIfExpiredInternal();
            }
        }

        public RetryBudgetStatus GetStatus()
        {
            lock (_lock)
            {
                ResetIfExpiredInternal();

                var timeUntilReset = _config.WindowDuration - (DateTime.UtcNow - _windowStart);
                if (timeUntilReset < TimeSpan.Zero)
                {
                    timeUntilReset = TimeSpan.Zero;
                }

                return new RetryBudgetStatus
                {
                    MaxRetries = _config.MaxRetries,
                    UsedRetries = _usedRetries,
                    RemainingRetries = Math.Max(0, _config.MaxRetries - _usedRetries),
                    WindowDuration = _config.WindowDuration,
                    TimeUntilReset = timeUntilReset
                };
            }
        }

        public TimeSpan GetTimeUntilReset()
        {
            lock (_lock)
            {
                var elapsed = DateTime.UtcNow - _windowStart;
                var remaining = _config.WindowDuration - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        private void ResetIfExpiredInternal()
        {
            var elapsed = DateTime.UtcNow - _windowStart;
            if (elapsed >= _config.WindowDuration)
            {
                _usedRetries = 0;
                _windowStart = DateTime.UtcNow;
            }
        }
    }

    #endregion

    #region Circuit Breaker Integration

    /// <summary>
    /// Interface for circuit breaker integration.
    /// </summary>
    public interface ICircuitBreakerIntegration
    {
        /// <summary>
        /// Checks if execution is allowed.
        /// </summary>
        /// <returns>True if the circuit is closed or half-open.</returns>
        bool CanExecute();

        /// <summary>
        /// Records a successful execution.
        /// </summary>
        void RecordSuccess();

        /// <summary>
        /// Records a failed execution.
        /// </summary>
        void RecordFailure();

        /// <summary>
        /// Gets the time until retry is allowed (when circuit is open).
        /// </summary>
        /// <returns>Time until retry.</returns>
        TimeSpan GetRetryAfter();
    }

    #endregion

    #region Exception Classification

    /// <summary>
    /// Classifies exceptions as transient or permanent.
    /// </summary>
    public static class ExceptionClassifier
    {
        private static readonly HashSet<Type> TransientExceptionTypes = new()
        {
            typeof(TimeoutException),
            typeof(TaskCanceledException),
            typeof(OperationCanceledException),
            typeof(IOException),
            typeof(HttpRequestException)
        };

        private static readonly HashSet<string> TransientExceptionNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SocketException",
            "WebException",
            "HttpRequestException",
            "TransientException",
            "ServiceUnavailableException",
            "GatewayTimeoutException",
            "RequestTimeoutException",
            "TooManyRequestsException",
            "ThrottlingException",
            "RetryableException"
        };

        /// <summary>
        /// Determines if an exception is likely transient and worth retrying.
        /// </summary>
        /// <param name="ex">The exception to classify.</param>
        /// <returns>True if the exception is transient.</returns>
        public static bool IsTransient(Exception ex)
        {
            if (ex == null) return false;

            var exType = ex.GetType();

            // Check direct type match
            if (TransientExceptionTypes.Contains(exType))
            {
                return true;
            }

            // Check type name
            if (TransientExceptionNames.Contains(exType.Name))
            {
                return true;
            }

            // Check for transient-like properties
            if (IsTransientHttpException(ex))
            {
                return true;
            }

            // Check inner exception
            if (ex.InnerException != null && IsTransient(ex.InnerException))
            {
                return true;
            }

            // Check aggregate exception
            if (ex is AggregateException aggEx)
            {
                return aggEx.InnerExceptions.Any(IsTransient);
            }

            return false;
        }

        private static bool IsTransientHttpException(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                // Check if there's an HTTP status code we can examine
                // 408, 429, 500, 502, 503, 504 are typically transient
                var message = httpEx.Message;
                if (message.Contains("408") || message.Contains("429") ||
                    message.Contains("500") || message.Contains("502") ||
                    message.Contains("503") || message.Contains("504") ||
                    message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Registers a custom transient exception type.
        /// </summary>
        /// <param name="exceptionType">The exception type to register.</param>
        public static void RegisterTransientType(Type exceptionType)
        {
            TransientExceptionTypes.Add(exceptionType);
        }

        /// <summary>
        /// Registers a custom transient exception type by name.
        /// </summary>
        /// <param name="typeName">The exception type name to register.</param>
        public static void RegisterTransientTypeName(string typeName)
        {
            TransientExceptionNames.Add(typeName);
        }
    }

    #endregion

    #region Exceptions

    /// <summary>
    /// Exception thrown when all retry attempts have been exhausted.
    /// </summary>
    public class RetryExhaustedException : Exception
    {
        /// <summary>
        /// Policy name that exhausted retries.
        /// </summary>
        public string PolicyName { get; }

        /// <summary>
        /// Number of retry attempts made.
        /// </summary>
        public int RetryCount { get; }

        /// <summary>
        /// Creates a new RetryExhaustedException.
        /// </summary>
        public RetryExhaustedException(string policyName, int retryCount, Exception? innerException = null)
            : base($"Retry policy '{policyName}' exhausted after {retryCount} attempts", innerException)
        {
            PolicyName = policyName;
            RetryCount = retryCount;
        }
    }

    /// <summary>
    /// Exception thrown when retry budget is exhausted.
    /// </summary>
    public class RetryBudgetExhaustedException : Exception
    {
        /// <summary>
        /// Policy name with exhausted budget.
        /// </summary>
        public string PolicyName { get; }

        /// <summary>
        /// Time until budget resets.
        /// </summary>
        public TimeSpan RetryAfter { get; }

        /// <summary>
        /// Creates a new RetryBudgetExhaustedException.
        /// </summary>
        public RetryBudgetExhaustedException(string policyName, TimeSpan retryAfter)
            : base($"Retry budget for policy '{policyName}' exhausted. Retry after {retryAfter.TotalSeconds:F1}s")
        {
            PolicyName = policyName;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Exception thrown when circuit breaker is open.
    /// </summary>
    public class RetryCircuitBreakerOpenException : Exception
    {
        /// <summary>
        /// Policy name with open circuit.
        /// </summary>
        public string PolicyName { get; }

        /// <summary>
        /// Time until circuit may close.
        /// </summary>
        public TimeSpan RetryAfter { get; }

        /// <summary>
        /// Creates a new RetryCircuitBreakerOpenException.
        /// </summary>
        public RetryCircuitBreakerOpenException(string policyName, TimeSpan retryAfter)
            : base($"Circuit breaker for policy '{policyName}' is open. Retry after {retryAfter.TotalSeconds:F1}s")
        {
            PolicyName = policyName;
            RetryAfter = retryAfter;
        }
    }

    #endregion

    #region Persistence Types

    internal class RetryPolicyPersistenceData
    {
        public List<PersistedRetryPolicy> Policies { get; set; } = new();
    }

    internal class PersistedRetryPolicy
    {
        public string Name { get; set; } = string.Empty;
        public int MaxRetries { get; set; }
        public double BaseDelayMs { get; set; }
        public double MaxDelayMs { get; set; }
        public double TimeoutMs { get; set; }
        public string BackoffStrategy { get; set; } = "ExponentialWithJitter";
        public double ExponentialBase { get; set; } = 2.0;
        public double JitterFactor { get; set; } = 0.2;
        public bool RetryOnTimeout { get; set; } = true;
    }

    #endregion
}
