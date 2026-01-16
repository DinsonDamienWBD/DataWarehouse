namespace DataWarehouse.SDK.Contracts
{
    #region Resilience

    /// <summary>
    /// Resilience policy for protecting operations against failures.
    /// Implements circuit breaker, retry, and timeout patterns.
    /// </summary>
    public interface IResiliencePolicy
    {
        /// <summary>
        /// Unique identifier for this policy.
        /// </summary>
        string PolicyId { get; }

        /// <summary>
        /// Current state of the circuit breaker.
        /// </summary>
        CircuitState State { get; }

        /// <summary>
        /// Executes an action with resilience protection.
        /// </summary>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default);

        /// <summary>
        /// Executes an action with resilience protection (no return value).
        /// </summary>
        Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default);

        /// <summary>
        /// Manually resets the circuit breaker to closed state.
        /// </summary>
        void Reset();

        /// <summary>
        /// Gets statistics about this policy's execution.
        /// </summary>
        ResilienceStatistics GetStatistics();
    }

    /// <summary>
    /// Circuit breaker states.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>Circuit is closed, requests flow normally.</summary>
        Closed,
        /// <summary>Circuit is open, requests fail fast.</summary>
        Open,
        /// <summary>Circuit is testing if the system has recovered.</summary>
        HalfOpen
    }

    /// <summary>
    /// Statistics for a resilience policy.
    /// </summary>
    public class ResilienceStatistics
    {
        public long TotalExecutions { get; init; }
        public long SuccessfulExecutions { get; init; }
        public long FailedExecutions { get; init; }
        public long TimeoutExecutions { get; init; }
        public long CircuitBreakerRejections { get; init; }
        public long RetryAttempts { get; init; }
        public DateTime? LastFailure { get; init; }
        public DateTime? LastSuccess { get; init; }
        public TimeSpan AverageExecutionTime { get; init; }
    }

    /// <summary>
    /// Configuration for a resilience policy.
    /// </summary>
    public class ResiliencePolicyConfig
    {
        /// <summary>Timeout for individual operations.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Number of failures before opening the circuit.</summary>
        public int FailureThreshold { get; init; } = 5;

        /// <summary>Time window for counting failures.</summary>
        public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(1);

        /// <summary>How long to wait before trying again when circuit is open.</summary>
        public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Maximum number of retry attempts.</summary>
        public int MaxRetries { get; init; } = 3;

        /// <summary>Base delay between retries (exponential backoff applied).</summary>
        public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Maximum delay between retries.</summary>
        public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Manages resilience policies for different operation types.
    /// </summary>
    public interface IResiliencePolicyManager
    {
        /// <summary>Gets or creates a policy for the specified key.</summary>
        IResiliencePolicy GetPolicy(string policyKey);

        /// <summary>Registers a custom policy configuration.</summary>
        void RegisterPolicy(string policyKey, ResiliencePolicyConfig config);

        /// <summary>Gets all registered policy keys.</summary>
        IEnumerable<string> GetPolicyKeys();

        /// <summary>Resets all circuits.</summary>
        void ResetAll();
    }

    #endregion

    #region Memory Pressure

    /// <summary>
    /// Monitors memory pressure and signals when throttling is needed.
    /// </summary>
    public interface IMemoryPressureMonitor
    {
        /// <summary>Current memory pressure level.</summary>
        MemoryPressureLevel CurrentLevel { get; }

        /// <summary>Whether requests should be throttled.</summary>
        bool ShouldThrottle { get; }

        /// <summary>Current memory usage statistics.</summary>
        MemoryStatistics GetStatistics();

        /// <summary>Event raised when pressure level changes.</summary>
        event Action<MemoryPressureLevel>? OnPressureChanged;

        /// <summary>Requests plugins to release resources.</summary>
        void RequestResourceRelease();
    }

    /// <summary>
    /// Memory pressure levels.
    /// </summary>
    public enum MemoryPressureLevel
    {
        /// <summary>Normal operation, plenty of memory available.</summary>
        Normal,
        /// <summary>Memory usage elevated, consider releasing caches.</summary>
        Elevated,
        /// <summary>Memory pressure high, throttle new requests.</summary>
        High,
        /// <summary>Critical memory pressure, reject non-essential operations.</summary>
        Critical
    }

    /// <summary>
    /// Memory usage statistics.
    /// </summary>
    public class MemoryStatistics
    {
        public long TotalMemoryBytes { get; init; }
        public long UsedMemoryBytes { get; init; }
        public long AvailableMemoryBytes { get; init; }
        public double UsagePercent { get; init; }
        public long GCTotalMemory { get; init; }
        public int Gen0Collections { get; init; }
        public int Gen1Collections { get; init; }
        public int Gen2Collections { get; init; }
        public DateTime Timestamp { get; init; }
    }

    #endregion

    #region Health Check

    /// <summary>
    /// Health check interface for components to report their health status.
    /// </summary>
    public interface IHealthCheck
    {
        /// <summary>Name of this health check.</summary>
        string Name { get; }

        /// <summary>Tags for categorizing this health check.</summary>
        string[] Tags { get; }

        /// <summary>Performs the health check.</summary>
        Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Result of a health check.
    /// </summary>
    public class HealthCheckResult
    {
        /// <summary>Health status.</summary>
        public HealthStatus Status { get; init; }

        /// <summary>Human-readable description.</summary>
        public string? Message { get; init; }

        /// <summary>How long the check took.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Additional diagnostic data.</summary>
        public Dictionary<string, object> Data { get; init; } = new();

        /// <summary>Exception if the check failed.</summary>
        public Exception? Exception { get; init; }

        public static HealthCheckResult Healthy(string? message = null, Dictionary<string, object>? data = null)
            => new() { Status = HealthStatus.Healthy, Message = message, Data = data ?? new() };

        public static HealthCheckResult Degraded(string message, Dictionary<string, object>? data = null)
            => new() { Status = HealthStatus.Degraded, Message = message, Data = data ?? new() };

        public static HealthCheckResult Unhealthy(string message, Exception? exception = null, Dictionary<string, object>? data = null)
            => new() { Status = HealthStatus.Unhealthy, Message = message, Exception = exception, Data = data ?? new() };
    }

    /// <summary>
    /// Health status values.
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>Component is fully healthy.</summary>
        Healthy,
        /// <summary>Component is functional but degraded.</summary>
        Degraded,
        /// <summary>Component is unhealthy and may not function correctly.</summary>
        Unhealthy
    }

    /// <summary>
    /// Aggregated health report for the entire system.
    /// </summary>
    public class HealthReport
    {
        /// <summary>Overall system status (worst of all components).</summary>
        public HealthStatus Status { get; init; }

        /// <summary>Total time to run all health checks.</summary>
        public TimeSpan TotalDuration { get; init; }

        /// <summary>Individual check results.</summary>
        public Dictionary<string, HealthCheckResult> Entries { get; init; } = new();

        /// <summary>When this report was generated.</summary>
        public DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Aggregates health checks from all components.
    /// </summary>
    public interface IHealthCheckAggregator
    {
        /// <summary>Registers a health check.</summary>
        void Register(IHealthCheck healthCheck);

        /// <summary>Unregisters a health check.</summary>
        void Unregister(string name);

        /// <summary>Runs all health checks and returns aggregated report.</summary>
        Task<HealthReport> CheckHealthAsync(CancellationToken ct = default);

        /// <summary>Runs health checks matching the specified tags.</summary>
        Task<HealthReport> CheckHealthAsync(string[] tags, CancellationToken ct = default);

        /// <summary>Gets liveness status (is the system running?).</summary>
        Task<bool> IsLiveAsync(CancellationToken ct = default);

        /// <summary>Gets readiness status (is the system ready to accept requests?).</summary>
        Task<bool> IsReadyAsync(CancellationToken ct = default);
    }

    #endregion

    #region Metrics

    /// <summary>
    /// Collects and stores metrics for observability.
    /// </summary>
    public interface IMetricsCollector
    {
        /// <summary>Increments a counter metric.</summary>
        void IncrementCounter(string name, long value = 1, params string[] tags);

        /// <summary>Records a gauge value.</summary>
        void RecordGauge(string name, double value, params string[] tags);

        /// <summary>Records a histogram value (for latency, sizes, etc.).</summary>
        void RecordHistogram(string name, double value, params string[] tags);

        /// <summary>Starts a timer that records duration when disposed.</summary>
        IDisposable StartTimer(string name, params string[] tags);

        /// <summary>Gets a snapshot of all metrics.</summary>
        MetricsSnapshot GetSnapshot();

        /// <summary>Resets all metrics.</summary>
        void Reset();
    }

    /// <summary>
    /// Snapshot of all collected metrics.
    /// </summary>
    public class MetricsSnapshot
    {
        public DateTime Timestamp { get; init; }
        public Dictionary<string, CounterMetric> Counters { get; init; } = new();
        public Dictionary<string, GaugeMetric> Gauges { get; init; } = new();
        public Dictionary<string, HistogramMetric> Histograms { get; init; } = new();
    }

    public class CounterMetric
    {
        public string Name { get; init; } = string.Empty;
        public long Value { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
    }

    public class GaugeMetric
    {
        public string Name { get; init; } = string.Empty;
        public double Value { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
    }

    public class HistogramMetric
    {
        public string Name { get; init; } = string.Empty;
        public long Count { get; init; }
        public double Sum { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
        public double Mean => Count > 0 ? Sum / Count : 0;
        public double P50 { get; init; }
        public double P95 { get; init; }
        public double P99 { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Interface for plugins that export metrics to external systems.
    /// </summary>
    public interface IMetricsExporter : IPlugin
    {
        /// <summary>Exports the current metrics snapshot.</summary>
        Task ExportAsync(MetricsSnapshot snapshot, CancellationToken ct = default);

        /// <summary>Export interval.</summary>
        TimeSpan ExportInterval { get; }
    }

    #endregion

    #region Rate Limiting

    /// <summary>
    /// Rate limiter interface.
    /// </summary>
    public interface IRateLimiter
    {
        /// <summary>Attempts to acquire a permit.</summary>
        Task<RateLimitResult> AcquireAsync(string key, int permits = 1, CancellationToken ct = default);

        /// <summary>Gets current rate limit status for a key.</summary>
        RateLimitStatus GetStatus(string key);

        /// <summary>Resets rate limit for a key.</summary>
        void Reset(string key);
    }

    /// <summary>
    /// Result of a rate limit acquisition attempt.
    /// </summary>
    public class RateLimitResult
    {
        public bool IsAllowed { get; init; }
        public int RemainingPermits { get; init; }
        public TimeSpan? RetryAfter { get; init; }
        public string? Reason { get; init; }

        public static RateLimitResult Allowed(int remaining) => new() { IsAllowed = true, RemainingPermits = remaining };
        public static RateLimitResult Denied(TimeSpan retryAfter, string reason) => new() { IsAllowed = false, RetryAfter = retryAfter, Reason = reason };
    }

    /// <summary>
    /// Current rate limit status.
    /// </summary>
    public class RateLimitStatus
    {
        public string Key { get; init; } = string.Empty;
        public int CurrentPermits { get; init; }
        public int MaxPermits { get; init; }
        public DateTime WindowStart { get; init; }
        public TimeSpan WindowDuration { get; init; }
    }

    /// <summary>
    /// Configuration for rate limiting.
    /// </summary>
    public class RateLimitConfig
    {
        public int PermitsPerWindow { get; init; } = 100;
        public TimeSpan WindowDuration { get; init; } = TimeSpan.FromMinutes(1);
        public int BurstLimit { get; init; } = 10;
    }

    #endregion

    #region Transaction

    /// <summary>
    /// Transaction scope for coordinating multi-step operations.
    /// </summary>
    public interface ITransactionScope : IAsyncDisposable
    {
        /// <summary>Unique transaction identifier.</summary>
        string TransactionId { get; }

        /// <summary>Transaction state.</summary>
        TransactionState State { get; }

        /// <summary>When the transaction was started.</summary>
        DateTime StartedAt { get; }

        /// <summary>Commits the transaction.</summary>
        Task CommitAsync(CancellationToken ct = default);

        /// <summary>Rolls back the transaction.</summary>
        Task RollbackAsync(CancellationToken ct = default);

        /// <summary>Registers a compensation action for rollback.</summary>
        void RegisterCompensation(Func<Task> compensationAction);

        /// <summary>Registers a resource in this transaction.</summary>
        void EnlistResource(string resourceId, object resource);
    }

    /// <summary>
    /// Transaction states.
    /// </summary>
    public enum TransactionState
    {
        Active,
        Committing,
        Committed,
        RollingBack,
        RolledBack,
        Failed
    }

    /// <summary>
    /// Factory for creating transaction scopes.
    /// </summary>
    public interface ITransactionManager
    {
        /// <summary>Creates a new transaction scope.</summary>
        ITransactionScope BeginTransaction(TransactionOptions? options = null);

        /// <summary>Gets the current ambient transaction, if any.</summary>
        ITransactionScope? Current { get; }
    }

    /// <summary>
    /// Options for creating a transaction.
    /// </summary>
    public class TransactionOptions
    {
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);
        public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;
    }

    public enum TransactionIsolationLevel
    {
        ReadUncommitted,
        ReadCommitted,
        RepeatableRead,
        Serializable
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Notifies when configuration changes.
    /// </summary>
    public interface IConfigurationChangeNotifier
    {
        /// <summary>Event raised when configuration changes.</summary>
        event Action<ConfigurationChangeEvent>? OnConfigurationChanged;

        /// <summary>Triggers a configuration reload.</summary>
        Task ReloadAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Configuration change event.
    /// </summary>
    public class ConfigurationChangeEvent
    {
        public string Section { get; init; } = string.Empty;
        public Dictionary<string, object?> OldValues { get; init; } = new();
        public Dictionary<string, object?> NewValues { get; init; } = new();
        public DateTime Timestamp { get; init; }
    }

    #endregion

    #region Plugin Reload

    /// <summary>
    /// Manages hot plugin reloading.
    /// </summary>
    public interface IPluginReloader
    {
        /// <summary>Reloads a specific plugin.</summary>
        Task<PluginReloadResult> ReloadPluginAsync(string pluginId, CancellationToken ct = default);

        /// <summary>Reloads all plugins.</summary>
        Task<PluginReloadResult[]> ReloadAllAsync(CancellationToken ct = default);

        /// <summary>Event raised before a plugin is reloaded.</summary>
        event Action<PluginReloadEvent>? OnPluginReloading;

        /// <summary>Event raised after a plugin is reloaded.</summary>
        event Action<PluginReloadEvent>? OnPluginReloaded;
    }

    /// <summary>
    /// Result of a plugin reload operation.
    /// </summary>
    public class PluginReloadResult
    {
        public string PluginId { get; init; } = string.Empty;
        public bool Success { get; init; }
        public string? PreviousVersion { get; init; }
        public string? NewVersion { get; init; }
        public string? Error { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Plugin reload event.
    /// </summary>
    public class PluginReloadEvent
    {
        public string PluginId { get; init; } = string.Empty;
        public string? Version { get; init; }
        public PluginReloadPhase Phase { get; init; }
        public DateTime Timestamp { get; init; }
    }

    public enum PluginReloadPhase
    {
        Starting,
        Unloading,
        Loading,
        Completed,
        Failed
    }

    #endregion

    #region Distributed Tracing

    /// <summary>
    /// Provides distributed tracing capabilities with correlation IDs.
    /// </summary>
    public interface IDistributedTracing
    {
        /// <summary>Gets the current trace context.</summary>
        TraceContext? Current { get; }

        /// <summary>Starts a new trace with a fresh correlation ID.</summary>
        ITraceScope StartTrace(string operationName, Dictionary<string, string>? baggage = null);

        /// <summary>Starts a child span within the current trace.</summary>
        ITraceScope StartSpan(string operationName, Dictionary<string, string>? baggage = null);

        /// <summary>Continues an existing trace from external correlation ID.</summary>
        ITraceScope ContinueTrace(string correlationId, string operationName, Dictionary<string, string>? baggage = null);

        /// <summary>Extracts trace context from headers/metadata.</summary>
        TraceContext? ExtractContext(IDictionary<string, string> carrier);

        /// <summary>Injects trace context into headers/metadata for propagation.</summary>
        void InjectContext(IDictionary<string, string> carrier);
    }

    /// <summary>
    /// Trace context containing correlation information.
    /// </summary>
    public class TraceContext
    {
        /// <summary>Unique correlation ID for the entire trace.</summary>
        public string CorrelationId { get; init; } = string.Empty;

        /// <summary>Span ID for this specific operation.</summary>
        public string SpanId { get; init; } = string.Empty;

        /// <summary>Parent span ID if this is a child span.</summary>
        public string? ParentSpanId { get; init; }

        /// <summary>Operation name being traced.</summary>
        public string OperationName { get; init; } = string.Empty;

        /// <summary>When the span started.</summary>
        public DateTime StartTime { get; init; }

        /// <summary>Baggage items propagated across service boundaries.</summary>
        public Dictionary<string, string> Baggage { get; init; } = new();

        /// <summary>Tags/attributes for this span.</summary>
        public Dictionary<string, object> Tags { get; init; } = new();
    }

    /// <summary>
    /// A trace scope that represents a span of work.
    /// </summary>
    public interface ITraceScope : IDisposable
    {
        /// <summary>Gets the trace context.</summary>
        TraceContext Context { get; }

        /// <summary>Adds a tag to this span.</summary>
        void SetTag(string key, object value);

        /// <summary>Adds baggage that propagates to child spans.</summary>
        void SetBaggage(string key, string value);

        /// <summary>Logs an event within this span.</summary>
        void LogEvent(string eventName, Dictionary<string, object>? fields = null);

        /// <summary>Marks the span as failed with an exception.</summary>
        void SetError(Exception exception);

        /// <summary>Sets the span status.</summary>
        void SetStatus(TraceStatus status, string? message = null);
    }

    /// <summary>
    /// Trace status values.
    /// </summary>
    public enum TraceStatus
    {
        Ok,
        Error,
        Cancelled
    }

    #endregion

    #region Kernel Limits Configuration

    /// <summary>
    /// Configurable limits for kernel components.
    /// </summary>
    public class KernelLimitsConfig
    {
        /// <summary>Maximum audit entries per URI before oldest are removed.</summary>
        public int MaxAuditEntriesPerUri { get; set; } = 10000;

        /// <summary>Maximum histogram sample values for percentile calculation.</summary>
        public int MaxHistogramSampleValues { get; set; } = 10000;

        /// <summary>Maximum items in in-memory storage.</summary>
        public int MaxInMemoryStorageItems { get; set; } = 10000;

        /// <summary>Maximum message bus pending messages.</summary>
        public int MaxPendingMessages { get; set; } = 100000;

        /// <summary>Maximum version history entries per URI.</summary>
        public int MaxVersionHistoryPerUri { get; set; } = 100;

        /// <summary>Maximum concurrent indexing jobs.</summary>
        public int MaxConcurrentIndexingJobs { get; set; } = 10;

        /// <summary>Default singleton instance with default values.</summary>
        public static KernelLimitsConfig Default { get; } = new();
    }

    #endregion

    #region Reliable Message Publishing

    /// <summary>
    /// Result of a message publish operation.
    /// </summary>
    public class PublishResult
    {
        /// <summary>Whether the message was successfully published.</summary>
        public bool Success { get; init; }

        /// <summary>Unique message ID for tracking.</summary>
        public string MessageId { get; init; } = string.Empty;

        /// <summary>Number of subscribers that received the message.</summary>
        public int SubscribersNotified { get; init; }

        /// <summary>Error message if publish failed.</summary>
        public string? Error { get; init; }

        /// <summary>Time taken to publish.</summary>
        public TimeSpan Duration { get; init; }

        public static PublishResult Ok(string messageId, int subscribers, TimeSpan duration) =>
            new() { Success = true, MessageId = messageId, SubscribersNotified = subscribers, Duration = duration };

        public static PublishResult Failed(string error) =>
            new() { Success = false, Error = error };
    }

    /// <summary>
    /// Options for reliable message publishing.
    /// </summary>
    public class PublishOptions
    {
        /// <summary>Timeout for publish operation.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Whether to wait for subscriber acknowledgement.</summary>
        public bool RequireAcknowledgement { get; init; } = false;

        /// <summary>Correlation ID for distributed tracing.</summary>
        public string? CorrelationId { get; init; }

        /// <summary>Priority level for the message.</summary>
        public MessagePriority Priority { get; init; } = MessagePriority.Normal;

        /// <summary>Default options.</summary>
        public static PublishOptions Default { get; } = new();
    }

    /// <summary>
    /// Message priority levels.
    /// </summary>
    public enum MessagePriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }

    #endregion
}
