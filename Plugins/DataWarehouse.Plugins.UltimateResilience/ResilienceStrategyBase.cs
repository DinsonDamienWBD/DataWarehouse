using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateResilience;

/// <summary>
/// Defines a resilience strategy that can be applied to operations.
/// </summary>
public interface IResilienceStrategy
{
    /// <summary>
    /// Unique identifier for this strategy.
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Human-readable name of this strategy.
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Category of this strategy (e.g., CircuitBreaker, Retry, LoadBalancing).
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Gets the characteristics of this resilience strategy.
    /// </summary>
    ResilienceCharacteristics Characteristics { get; }

    /// <summary>
    /// Executes an operation with this resilience strategy applied.
    /// </summary>
    Task<ResilienceResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation with this resilience strategy applied (no return value).
    /// </summary>
    Task<ResilienceResult> ExecuteAsync(
        Func<CancellationToken, Task> operation,
        ResilienceContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current statistics for this strategy.
    /// </summary>
    ResilienceStatistics GetStatistics();

    /// <summary>
    /// Resets the strategy state (e.g., circuit breaker reset).
    /// </summary>
    void Reset();
}

/// <summary>
/// Describes the characteristics of a resilience strategy.
/// </summary>
public sealed record ResilienceCharacteristics
{
    /// <summary>Strategy name.</summary>
    public required string StrategyName { get; init; }

    /// <summary>Strategy description.</summary>
    public required string Description { get; init; }

    /// <summary>Category of the strategy.</summary>
    public required string Category { get; init; }

    /// <summary>Whether this strategy provides fault tolerance.</summary>
    public bool ProvidesFaultTolerance { get; init; }

    /// <summary>Whether this strategy provides load management.</summary>
    public bool ProvidesLoadManagement { get; init; }

    /// <summary>Whether this strategy supports adaptive behavior.</summary>
    public bool SupportsAdaptiveBehavior { get; init; }

    /// <summary>Whether this strategy supports distributed coordination.</summary>
    public bool SupportsDistributedCoordination { get; init; }

    /// <summary>Typical latency overhead in milliseconds.</summary>
    public double TypicalLatencyOverheadMs { get; init; }

    /// <summary>Memory footprint category (Low, Medium, High).</summary>
    public string MemoryFootprint { get; init; } = "Low";
}

/// <summary>
/// Result of a resilience-protected operation.
/// </summary>
public class ResilienceResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Exception if the operation failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Number of attempts made.</summary>
    public int Attempts { get; init; }

    /// <summary>Total execution time including retries.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Whether a fallback was used.</summary>
    public bool UsedFallback { get; init; }

    /// <summary>Whether the circuit breaker was open.</summary>
    public bool CircuitBreakerOpen { get; init; }

    /// <summary>Additional metadata about the execution.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Result of a resilience-protected operation with a return value.
/// </summary>
public sealed class ResilienceResult<T> : ResilienceResult
{
    /// <summary>The result value if successful.</summary>
    public T? Value { get; init; }
}

/// <summary>
/// Context for a resilience-protected operation.
/// </summary>
public sealed class ResilienceContext
{
    /// <summary>Unique identifier for this operation.</summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Operation name for logging/metrics.</summary>
    public string? OperationName { get; init; }

    /// <summary>Additional context data.</summary>
    public Dictionary<string, object> Data { get; init; } = new();

    /// <summary>Correlation ID for distributed tracing.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Priority level (higher = more important).</summary>
    public int Priority { get; init; } = 0;

    /// <summary>Tags for categorization.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Statistics about resilience strategy execution.
/// </summary>
public sealed class ResilienceStatistics
{
    /// <summary>Total number of executions.</summary>
    public long TotalExecutions { get; set; }

    /// <summary>Number of successful executions.</summary>
    public long SuccessfulExecutions { get; set; }

    /// <summary>Number of failed executions.</summary>
    public long FailedExecutions { get; set; }

    /// <summary>Number of timeouts.</summary>
    public long Timeouts { get; set; }

    /// <summary>Number of retries performed.</summary>
    public long RetryAttempts { get; set; }

    /// <summary>Number of circuit breaker rejections.</summary>
    public long CircuitBreakerRejections { get; set; }

    /// <summary>Number of fallback invocations.</summary>
    public long FallbackInvocations { get; set; }

    /// <summary>Last failure timestamp.</summary>
    public DateTimeOffset? LastFailure { get; set; }

    /// <summary>Last success timestamp.</summary>
    public DateTimeOffset? LastSuccess { get; set; }

    /// <summary>Average execution time.</summary>
    public TimeSpan AverageExecutionTime { get; set; }

    /// <summary>P99 execution time.</summary>
    public TimeSpan P99ExecutionTime { get; set; }

    /// <summary>Current state (e.g., circuit state).</summary>
    public string? CurrentState { get; set; }
}

/// <summary>
/// Abstract base class for resilience strategies providing common infrastructure.
/// Inherits lifecycle, counters, health caching, and dispose from StrategyBase.
/// </summary>
public abstract class ResilienceStrategyBase : StrategyBase, IResilienceStrategy
{
    private readonly ConcurrentQueue<TimeSpan> _executionTimes = new();
    private long _totalExecutions;
    private long _successfulExecutions;
    private long _failedExecutions;
    private long _timeouts;
    private long _retryAttempts;
    private long _circuitBreakerRejections;
    private long _fallbackInvocations;
    // Stored as ticks (0 = never) for atomic read/write via Interlocked
    private long _lastFailureTicks;
    private long _lastSuccessTicks;
    private DateTimeOffset? _lastFailure => _lastFailureTicks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(_lastFailureTicks, TimeSpan.Zero);
    private DateTimeOffset? _lastSuccess => _lastSuccessTicks == 0 ? (DateTimeOffset?)null : new DateTimeOffset(_lastSuccessTicks, TimeSpan.Zero);

    /// <inheritdoc/>
    public abstract override string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public override string Name => StrategyName;

    /// <inheritdoc/>
    public abstract string Category { get; }

    /// <inheritdoc/>
    public new abstract ResilienceCharacteristics Characteristics { get; }

    /// <inheritdoc/>
    public virtual async Task<ResilienceResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _totalExecutions);

        try
        {
            var result = await ExecuteCoreAsync(operation, context, cancellationToken);

            var duration = DateTimeOffset.UtcNow - startTime;
            RecordExecutionTime(duration);

            if (result.Success)
            {
                Interlocked.Increment(ref _successfulExecutions);
                Interlocked.Exchange(ref _lastSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
            else
            {
                Interlocked.Increment(ref _failedExecutions);
                Interlocked.Exchange(ref _lastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
            }

            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedExecutions);
            Interlocked.Exchange(ref _lastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    /// <inheritdoc/>
    public virtual async Task<ResilienceResult> ExecuteAsync(
        Func<CancellationToken, Task> operation,
        ResilienceContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(async ct =>
        {
            await operation(ct);
            return true;
        }, context, cancellationToken);

        return new ResilienceResult
        {
            Success = result.Success,
            Exception = result.Exception,
            Attempts = result.Attempts,
            TotalDuration = result.TotalDuration,
            UsedFallback = result.UsedFallback,
            CircuitBreakerOpen = result.CircuitBreakerOpen,
            Metadata = result.Metadata
        };
    }

    /// <summary>
    /// Core execution logic to be implemented by derived classes.
    /// </summary>
    protected abstract Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken);

    /// <inheritdoc/>
    public virtual ResilienceStatistics GetStatistics()
    {
        var avgTime = CalculateAverageExecutionTime();
        var p99Time = CalculateP99ExecutionTime();

        return new ResilienceStatistics
        {
            TotalExecutions = Interlocked.Read(ref _totalExecutions),
            SuccessfulExecutions = Interlocked.Read(ref _successfulExecutions),
            FailedExecutions = Interlocked.Read(ref _failedExecutions),
            Timeouts = Interlocked.Read(ref _timeouts),
            RetryAttempts = Interlocked.Read(ref _retryAttempts),
            CircuitBreakerRejections = Interlocked.Read(ref _circuitBreakerRejections),
            FallbackInvocations = Interlocked.Read(ref _fallbackInvocations),
            LastFailure = _lastFailure,
            LastSuccess = _lastSuccess,
            AverageExecutionTime = avgTime,
            P99ExecutionTime = p99Time,
            CurrentState = GetCurrentState()
        };
    }

    /// <inheritdoc/>
    public virtual void Reset()
    {
        Interlocked.Exchange(ref _totalExecutions, 0);
        Interlocked.Exchange(ref _successfulExecutions, 0);
        Interlocked.Exchange(ref _failedExecutions, 0);
        Interlocked.Exchange(ref _timeouts, 0);
        Interlocked.Exchange(ref _retryAttempts, 0);
        Interlocked.Exchange(ref _circuitBreakerRejections, 0);
        Interlocked.Exchange(ref _fallbackInvocations, 0);
        Interlocked.Exchange(ref _lastFailureTicks, 0);
        Interlocked.Exchange(ref _lastSuccessTicks, 0);

        while (_executionTimes.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Gets the current state description for statistics.
    /// </summary>
    protected virtual string? GetCurrentState() => null;

    /// <summary>
    /// Records a timeout occurrence.
    /// </summary>
    protected void RecordTimeout() => Interlocked.Increment(ref _timeouts);

    /// <summary>
    /// Records a retry attempt.
    /// </summary>
    protected void RecordRetry() => Interlocked.Increment(ref _retryAttempts);

    /// <summary>
    /// Records a circuit breaker rejection.
    /// </summary>
    protected void RecordCircuitBreakerRejection() => Interlocked.Increment(ref _circuitBreakerRejections);

    /// <summary>
    /// Records a fallback invocation.
    /// </summary>
    protected void RecordFallback() => Interlocked.Increment(ref _fallbackInvocations);

    private void RecordExecutionTime(TimeSpan duration)
    {
        _executionTimes.Enqueue(duration);

        // Keep only last 1000 samples
        while (_executionTimes.Count > 1000)
        {
            _executionTimes.TryDequeue(out _);
        }
    }

    private TimeSpan CalculateAverageExecutionTime()
    {
        var times = _executionTimes.ToArray();
        if (times.Length == 0) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(times.Average(t => t.TotalMilliseconds));
    }

    private TimeSpan CalculateP99ExecutionTime()
    {
        var times = _executionTimes.ToArray();
        if (times.Length == 0) return TimeSpan.Zero;
        var sorted = times.OrderBy(t => t.TotalMilliseconds).ToArray();
        var p99Index = (int)(sorted.Length * 0.99);
        return sorted[Math.Min(p99Index, sorted.Length - 1)];
    }

    /// <summary>
    /// Gets knowledge object for AI discovery.
    /// </summary>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"strategy.{StrategyId}",
            Topic = $"resilience.{Category.ToLowerInvariant()}",
            SourcePluginId = "sdk.resilience",
            SourcePluginName = StrategyName,
            KnowledgeType = "capability",
            Description = GetStrategyDescription(),
            Payload = GetKnowledgePayload(),
            Tags = GetKnowledgeTags()
        };
    }

    /// <summary>
    /// Gets the registered capability for this strategy.
    /// </summary>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"strategy.{StrategyId}",
            DisplayName = StrategyName,
            Description = GetStrategyDescription(),
            Category = CapabilityCategory.Infrastructure,
            SubCategory = Category,
            PluginId = "sdk.resilience",
            PluginName = StrategyName,
            PluginVersion = "1.0.0",
            Tags = GetKnowledgeTags(),
            Metadata = GetCapabilityMetadata(),
            SemanticDescription = GetSemanticDescription()
        };
    }

    /// <summary>
    /// Gets description for this strategy.
    /// </summary>
    protected virtual string GetStrategyDescription() =>
        Characteristics.Description;

    /// <summary>
    /// Gets knowledge payload.
    /// </summary>
    protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
    {
        ["category"] = Category,
        ["providesFaultTolerance"] = Characteristics.ProvidesFaultTolerance,
        ["providesLoadManagement"] = Characteristics.ProvidesLoadManagement,
        ["supportsAdaptiveBehavior"] = Characteristics.SupportsAdaptiveBehavior,
        ["supportsDistributedCoordination"] = Characteristics.SupportsDistributedCoordination,
        ["typicalLatencyOverheadMs"] = Characteristics.TypicalLatencyOverheadMs,
        ["memoryFootprint"] = Characteristics.MemoryFootprint
    };

    /// <summary>
    /// Gets knowledge tags.
    /// </summary>
    protected virtual string[] GetKnowledgeTags() => new[]
    {
        "resilience",
        "strategy",
        Category.ToLowerInvariant(),
        Characteristics.ProvidesFaultTolerance ? "fault-tolerance" : "load-management"
    };

    /// <summary>
    /// Gets capability metadata.
    /// </summary>
    protected virtual Dictionary<string, object> GetCapabilityMetadata() => new()
    {
        ["category"] = Category,
        ["providesFaultTolerance"] = Characteristics.ProvidesFaultTolerance
    };

    /// <summary>
    /// Gets semantic description for AI discovery.
    /// </summary>
    protected virtual string GetSemanticDescription() =>
        $"Use {StrategyName} for {Category} resilience pattern";
}
