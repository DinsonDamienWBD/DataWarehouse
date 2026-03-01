# Plugin: UltimateResilience
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateResilience

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/UltimateResiliencePlugin.cs
```csharp
public sealed class UltimateResiliencePlugin : ResiliencePluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StrategyRegistry<IResilienceStrategy> Registry;;
    public UltimateResiliencePlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.execute",
                DisplayName = $"{Name} - Execute with Resilience",
                Description = "Execute operations with resilience protection",
                Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "resilience",
                    "execution",
                    "protection"
                }
            }
        };
        // Auto-generate capabilities from strategy registry
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "resilience",
                "strategy",
                strategy.Category.ToLowerInvariant()
            };
            if (strategy.Characteristics.ProvidesFaultTolerance)
                tags.Add("fault-tolerance");
            if (strategy.Characteristics.ProvidesLoadManagement)
                tags.Add("load-management");
            if (strategy.Characteristics.SupportsAdaptiveBehavior)
                tags.Add("adaptive");
            if (strategy.Characteristics.SupportsDistributedCoordination)
                tags.Add("distributed");
            capabilities.Add(new RegisteredCapability { CapabilityId = $"{Id}.strategy.{strategy.StrategyId}", DisplayName = strategy.StrategyName, Description = strategy.Characteristics.Description, Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience, SubCategory = strategy.Category, PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category, ["providesFaultTolerance"] = strategy.Characteristics.ProvidesFaultTolerance, ["providesLoadManagement"] = strategy.Characteristics.ProvidesLoadManagement, ["supportsAdaptive"] = strategy.Characteristics.SupportsAdaptiveBehavior }, SemanticDescription = $"Apply {strategy.StrategyName} resilience pattern" });
        }

        return capabilities;
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
    protected override Dictionary<string, object> GetConfigurationState();
    protected override KnowledgeObject? BuildStatisticsKnowledge();
    public override async Task<T> ExecuteWithResilienceAsync<T>(Func<CancellationToken, Task<T>> action, string policyName, CancellationToken ct);
    public override Task<ResilienceHealthInfo> GetResilienceHealthAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/ResilienceStrategyBase.cs
```csharp
public interface IResilienceStrategy
{
}
    string StrategyId { get; }
    string StrategyName { get; }
    string Category { get; }
    ResilienceCharacteristics Characteristics { get; }
    Task<ResilienceResult<T>> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context = null, CancellationToken cancellationToken = default);;
    Task<ResilienceResult> ExecuteAsync(Func<CancellationToken, Task> operation, ResilienceContext? context = null, CancellationToken cancellationToken = default);;
    ResilienceStatistics GetStatistics();;
    void Reset();;
}
```
```csharp
public sealed record ResilienceCharacteristics
{
}
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public bool ProvidesFaultTolerance { get; init; }
    public bool ProvidesLoadManagement { get; init; }
    public bool SupportsAdaptiveBehavior { get; init; }
    public bool SupportsDistributedCoordination { get; init; }
    public double TypicalLatencyOverheadMs { get; init; }
    public string MemoryFootprint { get; init; };
}
```
```csharp
public class ResilienceResult
{
}
    public bool Success { get; init; }
    public Exception? Exception { get; init; }
    public int Attempts { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public bool UsedFallback { get; init; }
    public bool CircuitBreakerOpen { get; init; }
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public sealed class ResilienceResult<T> : ResilienceResult
{
}
    public T? Value { get; init; }
}
```
```csharp
public sealed class ResilienceContext
{
}
    public string OperationId { get; init; };
    public string? OperationName { get; init; }
    public Dictionary<string, object> Data { get; init; };
    public string? CorrelationId { get; init; }
    public int Priority { get; init; };
    public string[] Tags { get; init; };
}
```
```csharp
public sealed class ResilienceStatistics
{
}
    public long TotalExecutions { get; set; }
    public long SuccessfulExecutions { get; set; }
    public long FailedExecutions { get; set; }
    public long Timeouts { get; set; }
    public long RetryAttempts { get; set; }
    public long CircuitBreakerRejections { get; set; }
    public long FallbackInvocations { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public TimeSpan AverageExecutionTime { get; set; }
    public TimeSpan P99ExecutionTime { get; set; }
    public string? CurrentState { get; set; }
}
```
```csharp
public abstract class ResilienceStrategyBase : StrategyBase, IResilienceStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string StrategyName { get; }
    public override string Name;;
    public abstract string Category { get; }
    public new abstract ResilienceCharacteristics Characteristics { get; }
    public virtual async Task<ResilienceResult<T>> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context = null, CancellationToken cancellationToken = default);
    public virtual async Task<ResilienceResult> ExecuteAsync(Func<CancellationToken, Task> operation, ResilienceContext? context = null, CancellationToken cancellationToken = default);
    protected abstract Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);;
    public virtual ResilienceStatistics GetStatistics();
    public virtual void Reset();
    protected virtual string? GetCurrentState();;
    protected void RecordTimeout();;
    protected void RecordRetry();;
    protected void RecordCircuitBreakerRejection();;
    protected void RecordFallback();;
    public virtual KnowledgeObject GetStrategyKnowledge();
    public virtual RegisteredCapability GetStrategyCapability();
    protected virtual string GetStrategyDescription();;
    protected virtual Dictionary<string, object> GetKnowledgePayload();;
    protected virtual string[] GetKnowledgeTags();;
    protected virtual Dictionary<string, object> GetCapabilityMetadata();;
    protected virtual string GetSemanticDescription();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Scaling/AdaptiveResilienceThresholds.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Adaptive resilience thresholds from observed metrics")]
public sealed class AdaptiveResilienceThresholds : IDisposable
{
}
    public AdaptiveResilienceThresholds(ResilienceScalingManager manager, AdaptiveThresholdOptions? options = null);
    public void ComputeAndApplyThresholds();
    public void Reconfigure(AdaptiveThresholdOptions newOptions);
    internal StrategyAdaptiveState? GetAdaptiveState(string strategyId);
    public void Dispose();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Per-strategy adaptive state with EMA smoothing")]
internal sealed class StrategyAdaptiveState
{
}
    internal double EmaErrorRate { get; private set; }
    internal double EmaP99Latency { get; private set; }
    internal double PreviousEmaP99Latency { get; set; }
    internal double EmaRecoveryTimeMs { get; private set; }
    internal int CurrentFailureThreshold { get; set; }
    internal TimeSpan CurrentBreakDuration { get; set; }
    internal int CurrentMaxConcurrency { get; set; }
    public StrategyAdaptiveState(AdaptiveThresholdOptions opts);
    public void RecordErrorRate(double errorRate);
    public void RecordLatency(double latencyMs);
    public void RecordRecoveryTime(double recoveryMs);
    public double GetWindowedErrorRate();
    public double GetWindowedP99Latency();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Adaptive threshold configuration")]
public sealed record AdaptiveThresholdOptions
{
}
    public int AdaptationIntervalMs { get; init; };
    public int MinSamplesBeforeAdaptation { get; init; };
    public double EmaAlpha { get; init; };
    public double HealthyErrorRateThreshold { get; init; };
    public double CriticalErrorRateThreshold { get; init; };
    public double HealthyThresholdIncreaseFactor { get; init; };
    public double CriticalThresholdDecreaseFactor { get; init; };
    public int MinFailureThreshold { get; init; };
    public int MaxFailureThreshold { get; init; };
    public int DefaultFailureThreshold { get; init; };
    public double FastRecoveryThresholdMs { get; init; };
    public double SlowRecoveryThresholdMs { get; init; };
    public double BreakDurationRecoveryMultiplier { get; init; };
    public double MinBreakDurationMs { get; init; };
    public double MaxBreakDurationMs { get; init; };
    public double DefaultBreakDurationMs { get; init; };
    public double LatencyDropThreshold { get; init; };
    public double LatencyRiseThreshold { get; init; };
    public double ConcurrencyIncreaseFactor { get; init; };
    public double ConcurrencyDecreaseFactor { get; init; };
    public int MinConcurrency { get; init; };
    public int MaxConcurrency { get; init; };
    public int DefaultMaxConcurrency { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Scaling/ResilienceScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Resilience scaling manager with fixed ExecuteWithResilienceAsync")]
public sealed class ResilienceScalingManager : IScalableSubsystem, IDisposable
{
}
    public const string CircuitBreakerStateTopic = "dw.resilience.circuit-breaker.state";
    internal AdaptiveResilienceThresholds? AdaptiveThresholds { get; set; }
    public ResilienceScalingManager(IFederatedMessageBus? federatedBus = null, ScalingLimits? initialLimits = null);
    public async Task<T> ExecuteWithResilienceAsync<T>(Func<CancellationToken, Task<T>> action, string strategyId, CircuitBreakerOptions? circuitBreakerOptions = null, BulkheadOptions? bulkheadOptions = null, RetryOptions? retryOptions = null, CancellationToken ct = default);
    public void ConfigureStrategy(string strategyId, CircuitBreakerOptions? circuitBreakerOptions = null, BulkheadOptions? bulkheadOptions = null, RetryOptions? retryOptions = null);
    internal void ApplyAdaptiveThresholds(string strategyId, int newFailureThreshold, TimeSpan newBreakDuration, int newMaxConcurrency);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits
{
    get
    {
        lock (_configLock)
        {
            return _currentLimits;
        }
    }
}
    public BackpressureState CurrentBackpressureState
{
    get
    {
        // Compute backpressure from bulkhead utilization
        int totalCurrent = 0;
        int totalMax = 0;
        foreach (var kvp in _bulkheads)
        {
            var stats = kvp.Value.GetStatistics();
            totalCurrent += stats.CurrentConcurrency;
            totalMax += stats.MaxConcurrency;
        }

        if (totalMax == 0)
            return BackpressureState.Normal;
        double utilization = (double)totalCurrent / totalMax;
        return utilization switch
        {
            >= 0.80 => BackpressureState.Critical,
            >= 0.50 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }
}
    internal StrategyMetrics? GetStrategyMetrics(string strategyId);
    internal IReadOnlyCollection<string> TrackedStrategies;;
    public void Dispose();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Retry options for resilience scaling")]
public sealed record RetryOptions
{
}
    public int MaxRetries { get; init; };
    public double BaseDelayMs { get; init; };
    public double MaxDelayMs { get; init; };
    public RetryBackoffType BackoffType { get; init; };
    public bool UseJitter { get; init; };
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-03: Per-strategy metrics with circular buffer")]
internal sealed class StrategyMetrics
{
}
    public void RecordSuccess();
    public void RecordFailure(TimeSpan latency);
    public void RecordRetry();
    public void RecordCircuitBreakerTrip();
    public void RecordBulkheadRejection();
    public void RecordLatency(TimeSpan latency);
    public TimeSpan GetP50Latency();;
    public TimeSpan GetP95Latency();;
    public TimeSpan GetP99Latency();;
    public double GetSuccessRate();
    public double GetRetryRate();
    public double GetErrorRate();
    public long TotalExecutions;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/LoadBalancing/LoadBalancingStrategies.cs
```csharp
public sealed class LoadBalancerEndpoint
{
}
    public required string EndpointId { get; init; }
    public string? Name { get; init; }
    public required string Address { get; init; }
    public int Weight { get; set; };
    public bool IsHealthy { get; set; };
    public int ActiveConnections { get => _activeConnections; set => _activeConnections = value; }
    public int IncrementConnections();;
    public int DecrementConnections();;
    public TimeSpan LastResponseTime { get; set; }
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public abstract class LoadBalancingStrategyBase : ResilienceStrategyBase
{
}
    protected readonly List<LoadBalancerEndpoint> _endpoints = new();
    protected readonly object _endpointsLock = new();
    public override string Category;;
    public virtual void AddEndpoint(LoadBalancerEndpoint endpoint);
    public virtual bool RemoveEndpoint(string endpointId);
    public virtual IReadOnlyList<LoadBalancerEndpoint> GetEndpoints();
    protected virtual IReadOnlyList<LoadBalancerEndpoint> GetHealthyEndpoints();
    public abstract LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);;
}
```
```csharp
public sealed class RoundRobinLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class WeightedRoundRobinLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override void AddEndpoint(LoadBalancerEndpoint endpoint);
    public override bool RemoveEndpoint(string endpointId);
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class LeastConnectionsLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class RandomLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class IpHashLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class ConsistentHashingLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public ConsistentHashingLoadBalancingStrategy() : this(virtualNodes: 150);
    public ConsistentHashingLoadBalancingStrategy(int virtualNodes);
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override void AddEndpoint(LoadBalancerEndpoint endpoint);
    public override bool RemoveEndpoint(string endpointId);
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class LeastResponseTimeLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class PowerOfTwoChoicesLoadBalancingStrategy : LoadBalancingStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ResilienceCharacteristics Characteristics { get; };
    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/Timeout/TimeoutStrategies.cs
```csharp
public sealed class SimpleTimeoutStrategy : ResilienceStrategyBase
{
}
    public SimpleTimeoutStrategy() : this(timeout: TimeSpan.FromSeconds(30));
    public SimpleTimeoutStrategy(TimeSpan timeout);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class CascadingTimeoutStrategy : ResilienceStrategyBase
{
}
    public CascadingTimeoutStrategy() : this(outerTimeout: TimeSpan.FromSeconds(60), innerTimeout: TimeSpan.FromSeconds(10), perStepTimeout: TimeSpan.FromSeconds(5));
    public CascadingTimeoutStrategy(TimeSpan outerTimeout, TimeSpan innerTimeout, TimeSpan perStepTimeout);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public TimeSpan GetRemainingTime(ResilienceContext? context);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class AdaptiveTimeoutStrategy : ResilienceStrategyBase
{
}
    public AdaptiveTimeoutStrategy() : this(baseTimeout: TimeSpan.FromSeconds(10), minTimeout: TimeSpan.FromSeconds(1), maxTimeout: TimeSpan.FromSeconds(60), percentile: 0.99, multiplier: 1.5);
    public AdaptiveTimeoutStrategy(TimeSpan baseTimeout, TimeSpan minTimeout, TimeSpan maxTimeout, double percentile, double multiplier);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public TimeSpan CurrentTimeout
{
    get
    {
        lock (_adaptLock)
        {
            return _currentTimeout;
        }
    }
}
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class PessimisticTimeoutStrategy : ResilienceStrategyBase
{
}
    public PessimisticTimeoutStrategy() : this(timeout: TimeSpan.FromSeconds(30));
    public PessimisticTimeoutStrategy(TimeSpan timeout);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class OptimisticTimeoutStrategy : ResilienceStrategyBase
{
}
    public OptimisticTimeoutStrategy() : this(hardTimeout: TimeSpan.FromSeconds(60), softTimeout: TimeSpan.FromSeconds(30));
    public OptimisticTimeoutStrategy(TimeSpan hardTimeout, TimeSpan softTimeout);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class PerAttemptTimeoutStrategy : ResilienceStrategyBase
{
}
    public PerAttemptTimeoutStrategy() : this(attemptTimeout: TimeSpan.FromSeconds(10), totalTimeout: TimeSpan.FromSeconds(60));
    public PerAttemptTimeoutStrategy(TimeSpan attemptTimeout, TimeSpan totalTimeout);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public TimeSpan GetRemainingTotal(DateTimeOffset operationStart);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class TimeoutRejectedException : Exception
{
}
    public TimeoutRejectedException(string message) : base(message);
    public TimeoutRejectedException(string message, Exception innerException) : base(message, innerException);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/ChaosEngineering/ChaosEngineeringStrategies.cs
```csharp
public sealed class FaultInjectionStrategy : ResilienceStrategyBase
{
}
    public FaultInjectionStrategy() : this(faultRate: 0.1, exceptionTypes: new[] { typeof(Exception) });
    public FaultInjectionStrategy(double faultRate, Type[] exceptionTypes, string[]? faultMessages = null);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public double FaultRate;;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class LatencyInjectionStrategy : ResilienceStrategyBase
{
}
    public LatencyInjectionStrategy() : this(injectionRate: 0.1, minLatency: TimeSpan.FromMilliseconds(100), maxLatency: TimeSpan.FromSeconds(2));
    public LatencyInjectionStrategy(double injectionRate, TimeSpan minLatency, TimeSpan maxLatency);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public double InjectionRate;;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ProcessTerminationStrategy : ResilienceStrategyBase
{
}
    public ProcessTerminationStrategy() : this(terminationRate: 0.01, onTermination: null);
    public ProcessTerminationStrategy(double terminationRate, Action? onTermination = null);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool Enabled { get => _enabled; set => _enabled = value; }
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ResourceExhaustionStrategy : ResilienceStrategyBase
{
}
    public ResourceExhaustionStrategy() : this(exhaustionRate: 0.05, resourceTypes: new[] { "memory", "cpu", "connections", "disk" });
    public ResourceExhaustionStrategy(double exhaustionRate, string[] resourceTypes);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool Enabled { get => _enabled; set => _enabled = value; }
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class NetworkPartitionStrategy : ResilienceStrategyBase
{
}
    public NetworkPartitionStrategy() : this(partitionRate: 0.02, partitionDuration: TimeSpan.FromSeconds(30));
    public NetworkPartitionStrategy(double partitionRate, TimeSpan partitionDuration);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public bool IsPartitioned
{
    get
    {
        lock (_lock)
        {
            return DateTimeOffset.UtcNow < _partitionEnd;
        }
    }
}
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public void EndPartition();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ChaosMonkeyStrategy : ResilienceStrategyBase
{
}
    public ChaosMonkeyStrategy() : this(overallRate: 0.1);
    public ChaosMonkeyStrategy(double overallRate);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public ChaosMonkeyStrategy AddStrategy(string name, ResilienceStrategyBase strategy, double weight);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();
}
```
```csharp
public sealed class ChaosTerminationException : Exception
{
}
    public ChaosTerminationException(string message) : base(message);
}
```
```csharp
public sealed class ChaosNetworkPartitionException : Exception
{
}
    public ChaosNetworkPartitionException(string message) : base(message);
}
```
```csharp
public sealed class IOException : Exception
{
}
    public IOException(string message) : base(message);
}
```
```csharp
internal static class TimeSpanExtensions
{
}
    public static double TotalMs(this TimeSpan ts);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/RateLimiting/RateLimitingStrategies.cs
```csharp
public sealed class TokenBucketRateLimitingStrategy : ResilienceStrategyBase
{
}
    public TokenBucketRateLimitingStrategy() : this(bucketCapacity: 100, refillRate: 10, refillInterval: TimeSpan.FromSeconds(1));
    public TokenBucketRateLimitingStrategy(double bucketCapacity, double refillRate, TimeSpan refillInterval);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public double AvailableTokens
{
    get
    {
        lock (_lock)
        {
            RefillTokens();
            return _tokens;
        }
    }
}
    public bool TryAcquire(double tokens = 1);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class LeakyBucketRateLimitingStrategy : ResilienceStrategyBase
{
}
    public LeakyBucketRateLimitingStrategy() : this(bucketCapacity: 100, leakInterval: TimeSpan.FromMilliseconds(100));
    public LeakyBucketRateLimitingStrategy(int bucketCapacity, TimeSpan leakInterval);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int QueueLength;;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
    protected override void Dispose(bool disposing);
}
```
```csharp
public sealed class SlidingWindowRateLimitingStrategy : ResilienceStrategyBase
{
}
    public SlidingWindowRateLimitingStrategy() : this(maxRequests: 100, windowDuration: TimeSpan.FromMinutes(1));
    public SlidingWindowRateLimitingStrategy(int maxRequests, TimeSpan windowDuration);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int CurrentRequestCount
{
    get
    {
        lock (_lock)
        {
            PruneOldRequests();
            return _requests.Count;
        }
    }
}
    public bool TryAcquire();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class FixedWindowRateLimitingStrategy : ResilienceStrategyBase
{
}
    public FixedWindowRateLimitingStrategy() : this(maxRequests: 100, windowDuration: TimeSpan.FromMinutes(1));
    public FixedWindowRateLimitingStrategy(int maxRequests, TimeSpan windowDuration);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool TryAcquire();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class AdaptiveRateLimitingStrategy : ResilienceStrategyBase
{
}
    public AdaptiveRateLimitingStrategy() : this(baseLimit: 100, minLimit: 10, maxLimit: 1000, adaptationInterval: TimeSpan.FromSeconds(10), historyWindow: TimeSpan.FromMinutes(1), targetSuccessRate: 0.95, targetLatency: TimeSpan.FromMilliseconds(500));
    public AdaptiveRateLimitingStrategy(double baseLimit, double minLimit, double maxLimit, TimeSpan adaptationInterval, TimeSpan historyWindow, double targetSuccessRate, TimeSpan targetLatency);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public double CurrentLimit
{
    get
    {
        lock (_lock)
        {
            return _currentLimit;
        }
    }
}
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ConcurrencyLimiterStrategy : ResilienceStrategyBase
{
}
    public ConcurrencyLimiterStrategy() : this(maxConcurrency: 10);
    public ConcurrencyLimiterStrategy(int maxConcurrency);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int AvailablePermits;;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class RateLimitExceededException : Exception
{
}
    public RateLimitExceededException(string message) : base(message);
    public RateLimitExceededException(string message, Exception innerException) : base(message, innerException);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/Fallback/FallbackStrategies.cs
```csharp
public sealed class CacheFallbackStrategy<TResult> : ResilienceStrategyBase
{
}
    public CacheFallbackStrategy() : this(cacheTtl: TimeSpan.FromMinutes(5), keySelector: ctx => ctx?.OperationName ?? "default");
    public CacheFallbackStrategy(TimeSpan cacheTtl, Func<ResilienceContext?, string> keySelector);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int CacheSize;;
    public void CacheValue(string key, TResult value);
    public void PruneCache();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class DefaultValueFallbackStrategy<TResult> : ResilienceStrategyBase
{
}
    public DefaultValueFallbackStrategy(TResult defaultValue) : this(defaultValue, shouldFallback: null);
    public DefaultValueFallbackStrategy(TResult defaultValue, Func<Exception, bool>? shouldFallback);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class DegradedServiceFallbackStrategy<TResult> : ResilienceStrategyBase
{
}
    public DegradedServiceFallbackStrategy(Func<ResilienceContext?, CancellationToken, Task<TResult>> degradedOperation) : this(degradedOperation, shouldDegrade: null);
    public DegradedServiceFallbackStrategy(Func<ResilienceContext?, CancellationToken, Task<TResult>> degradedOperation, Func<Exception, bool>? shouldDegrade);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class FailoverFallbackStrategy : ResilienceStrategyBase
{
}
    public FailoverFallbackStrategy(params Func<CancellationToken, Task<object>>[] operations);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int PrimaryIndex
{
    get
    {
        lock (_failoverLock)
        {
            return _primaryIndex;
        }
    }
}
    public void SetPrimary(int index);
    public void AddService(Func<CancellationToken, Task<object>> operation);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class CircuitBreakerFallbackStrategy<TResult> : ResilienceStrategyBase
{
}
    public CircuitBreakerFallbackStrategy(TResult fallbackValue) : this(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30), fallbackValue, fallbackOperation: null);
    public CircuitBreakerFallbackStrategy(int failureThreshold, TimeSpan openDuration, TResult fallbackValue, Func<ResilienceContext?, CancellationToken, Task<TResult>>? fallbackOperation = null);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ConditionalFallbackStrategy<TResult> : ResilienceStrategyBase
{
}
    public ConditionalFallbackStrategy(Func<Exception, ResilienceContext?, CancellationToken, Task<TResult>>? defaultFallback = null);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public ConditionalFallbackStrategy<TResult> AddHandler(Func<Exception, bool> predicate, Func<Exception, ResilienceContext?, CancellationToken, Task<TResult>> fallback);
    public ConditionalFallbackStrategy<TResult> AddHandler<TException>(Func<TException, ResilienceContext?, CancellationToken, Task<TResult>> fallback)
    where TException : Exception;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/RetryPolicies/RetryStrategies.cs
```csharp
public sealed class ExponentialBackoffRetryStrategy : ResilienceStrategyBase
{
}
    public ExponentialBackoffRetryStrategy() : this(maxRetries: 3, initialDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30), multiplier: 2.0);
    public ExponentialBackoffRetryStrategy(int maxRetries, TimeSpan initialDelay, TimeSpan maxDelay, double multiplier, params Type[] retryableExceptions);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class JitteredExponentialBackoffStrategy : ResilienceStrategyBase
{
}
    public JitteredExponentialBackoffStrategy() : this(maxRetries: 3, initialDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30), multiplier: 2.0, jitterFactor: 0.5);
    public JitteredExponentialBackoffStrategy(int maxRetries, TimeSpan initialDelay, TimeSpan maxDelay, double multiplier, double jitterFactor);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class FixedDelayRetryStrategy : ResilienceStrategyBase
{
}
    public FixedDelayRetryStrategy() : this(maxRetries: 3, delay: TimeSpan.FromSeconds(1));
    public FixedDelayRetryStrategy(int maxRetries, TimeSpan delay);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class ImmediateRetryStrategy : ResilienceStrategyBase
{
}
    public ImmediateRetryStrategy() : this(maxRetries: 3);
    public ImmediateRetryStrategy(int maxRetries);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class RetryWithFallbackStrategy<TResult> : ResilienceStrategyBase
{
}
    public RetryWithFallbackStrategy(Func<CancellationToken, Task<TResult>> fallback) : this(maxRetries: 3, delay: TimeSpan.FromSeconds(1), fallback);
    public RetryWithFallbackStrategy(int maxRetries, TimeSpan delay, Func<CancellationToken, Task<TResult>> fallback);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class LinearBackoffRetryStrategy : ResilienceStrategyBase
{
}
    public LinearBackoffRetryStrategy() : this(maxRetries: 5, initialDelay: TimeSpan.FromMilliseconds(500), delayIncrement: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(10));
    public LinearBackoffRetryStrategy(int maxRetries, TimeSpan initialDelay, TimeSpan delayIncrement, TimeSpan maxDelay);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class DecorrelatedJitterRetryStrategy : ResilienceStrategyBase
{
}
    public DecorrelatedJitterRetryStrategy() : this(maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30));
    public DecorrelatedJitterRetryStrategy(int maxRetries, TimeSpan baseDelay, TimeSpan maxDelay);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```
```csharp
public sealed class AdaptiveRetryStrategy : ResilienceStrategyBase
{
}
    public AdaptiveRetryStrategy() : this(baseMaxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30));
    public AdaptiveRetryStrategy(int baseMaxRetries, TimeSpan baseDelay, TimeSpan maxDelay);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/Consensus/ConsensusStrategies.cs
```csharp
public sealed class RaftConsensusStrategy : ResilienceStrategyBase
{
}
    public RaftConsensusStrategy() : this(nodeId: Guid.NewGuid().ToString("N")[..8], clusterNodes: new List<string>(), electionTimeout: TimeSpan.FromMilliseconds(Random.Shared.Next(150, 300)), heartbeatInterval: TimeSpan.FromMilliseconds(50));
    public RaftConsensusStrategy(string nodeId, List<string> clusterNodes, TimeSpan electionTimeout, TimeSpan heartbeatInterval);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public RaftState State;;
    public long CurrentTerm;;
    public string? LeaderId;;
    public bool IsLeader;;
    public void AddNode(string nodeId);
    public Task<bool> StartElectionAsync(CancellationToken cancellationToken = default);
    public Task<bool> AppendCommandAsync(object command, CancellationToken cancellationToken = default);
    public void ReceiveHeartbeat(string leaderId, long term);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class PaxosConsensusStrategy : ResilienceStrategyBase
{
}
    public PaxosConsensusStrategy() : this(nodeId: Guid.NewGuid().ToString("N")[..8], quorumSize: 3);
    public PaxosConsensusStrategy(string nodeId, int quorumSize);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public Task<(bool success, object? value)> ProposeAsync(object value, CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class PbftConsensusStrategy : ResilienceStrategyBase
{
}
    public PbftConsensusStrategy() : this(nodeId: Guid.NewGuid().ToString("N")[..8], totalNodes: 4, faultyNodes: 1);
    public PbftConsensusStrategy(string nodeId, int totalNodes, int faultyNodes);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public Task<bool> ExecuteConsensusAsync(object request, CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ZabConsensusStrategy : ResilienceStrategyBase
{
}
    public ZabConsensusStrategy() : this(nodeId: Guid.NewGuid().ToString("N")[..8], quorumSize: 3);
    public ZabConsensusStrategy(string nodeId, int quorumSize);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public long Zxid;;
    public long Epoch;;
    public Task<bool> BroadcastAsync(object proposal, CancellationToken cancellationToken = default);
    public void BecomeLeader();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ViewstampedReplicationStrategy : ResilienceStrategyBase
{
}
    public ViewstampedReplicationStrategy() : this(nodeId: Guid.NewGuid().ToString("N")[..8], replicaCount: 3);
    public ViewstampedReplicationStrategy(string nodeId, int replicaCount);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public Task<bool> ProcessRequestAsync(object request, CancellationToken cancellationToken = default);
    public void BecomePrimary();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/DisasterRecovery/DisasterRecoveryStrategies.cs
```csharp
public sealed class RecoveryPoint
{
}
    public string Id { get; init; };
    public DateTimeOffset Timestamp { get; init; };
    public string? Description { get; init; }
    public string Type { get; init; };
    public Dictionary<string, object> Metadata { get; init; };
    public bool IsValid { get; init; };
    public long? SizeBytes { get; init; }
}
```
```csharp
public sealed class DisasterRecoveryResult
{
}
    public bool Success { get; init; }
    public DisasterRecoveryMode Mode { get; init; }
    public string? Description { get; init; }
    public TimeSpan Duration { get; init; }
    public RecoveryPoint? RecoveryPoint { get; init; }
    public Exception? Exception { get; init; }
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public sealed class GeoReplicationFailoverStrategy : ResilienceStrategyBase
{
}
    public GeoReplicationFailoverStrategy() : this(healthCheckInterval: TimeSpan.FromSeconds(30), failureThreshold: 3);
    public GeoReplicationFailoverStrategy(TimeSpan healthCheckInterval, int failureThreshold);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public DisasterRecoveryMode Mode;;
    public string? ActiveRegionId;;
    public GeoReplicationFailoverStrategy AddRegion(string regionId, string endpoint, int priority);
    public async Task<DisasterRecoveryResult> FailoverAsync(CancellationToken cancellationToken = default);
    public async Task<DisasterRecoveryResult> FailbackAsync(CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class PointInTimeRecoveryStrategy : ResilienceStrategyBase
{
}
    public PointInTimeRecoveryStrategy() : this(maxRecoveryPoints: 100, retentionPeriod: TimeSpan.FromDays(7));
    public PointInTimeRecoveryStrategy(int maxRecoveryPoints, TimeSpan retentionPeriod);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public IReadOnlyList<RecoveryPoint> RecoveryPoints;;
    public RecoveryPoint? LastCheckpoint;;
    public RecoveryPoint CreateCheckpoint(string? description = null, Dictionary<string, object>? metadata = null);
    public RecoveryPoint CreateFullBackup(string? description = null, long? sizeBytes = null);
    public IReadOnlyList<RecoveryPoint> FindRecoveryPoints(DateTimeOffset start, DateTimeOffset end);
    public RecoveryPoint? FindClosestRecoveryPoint(DateTimeOffset targetTime);
    public async Task<DisasterRecoveryResult> RestoreToPointAsync(string recoveryPointId, CancellationToken cancellationToken = default);
    public async Task<DisasterRecoveryResult> RestoreToTimeAsync(DateTimeOffset targetTime, CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class MultiRegionDisasterRecoveryStrategy : ResilienceStrategyBase
{
}
    public MultiRegionDisasterRecoveryStrategy() : this(activeActive: false, syncInterval: TimeSpan.FromSeconds(10));
    public MultiRegionDisasterRecoveryStrategy(bool activeActive, TimeSpan syncInterval);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public DisasterRecoveryMode Mode;;
    public string? PrimaryRegionId;;
    public bool IsActiveActive;;
    public MultiRegionDisasterRecoveryStrategy AddRegion(string regionId, string endpoint, bool isPrimary = false);
    public void UpdateRegionHealth(string regionId, bool isHealthy, long? replicationLagMs = null);
    public async Task<DisasterRecoveryResult> PromoteRegionAsync(string regionId, CancellationToken cancellationToken = default);
    public Dictionary<string, object> GetReplicationStatus();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
private sealed class RegionState
{
}
    public string RegionId { get; init; };
    public string Endpoint { get; init; };
    public bool IsPrimary { get; set; }
    public bool IsHealthy { get; set; };
    public DateTimeOffset LastSyncTime { get; set; };
    public long ReplicationLagMs { get; set; }
}
```
```csharp
public sealed class StateCheckpointStrategy : ResilienceStrategyBase
{
}
    public StateCheckpointStrategy() : this(maxCheckpoints: 50, stateSerializer: null, stateRestorer: null);
    public StateCheckpointStrategy(int maxCheckpoints, Func<CancellationToken, Task<byte[]>>? stateSerializer, Func<byte[], CancellationToken, Task>? stateRestorer);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int CheckpointCount;;
    public async Task<RecoveryPoint> CreateCheckpointAsync(byte[]? stateData = null, CancellationToken cancellationToken = default);
    public async Task<DisasterRecoveryResult> RestoreCheckpointAsync(string checkpointId, CancellationToken cancellationToken = default);
    public (string? checkpointId, DateTimeOffset? timestamp) GetLatestCheckpoint();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    public override void Reset();
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class DataCenterFailoverStrategy : ResilienceStrategyBase
{
}
    public DataCenterFailoverStrategy() : this(failoverTimeout: TimeSpan.FromMinutes(5));
    public DataCenterFailoverStrategy(TimeSpan failoverTimeout);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public string? ActiveDataCenterId;;
    public DisasterRecoveryMode Mode;;
    public DataCenterFailoverStrategy AddDataCenter(string id, string name, string location, int priority, Dictionary<string, object>? capabilities = null);
    public void UpdateHealth(string dataCenterId, bool isHealthy);
    public async Task<DisasterRecoveryResult> FailoverAsync(CancellationToken cancellationToken = default);
    public IReadOnlyList<Dictionary<string, object>> GetDataCenterStatus();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
private sealed class DataCenterInfo
{
}
    public string Id { get; init; };
    public string Name { get; init; };
    public string Location { get; init; };
    public int Priority { get; init; }
    public bool IsHealthy { get; set; };
    public DateTimeOffset LastHealthCheck { get; set; };
    public Dictionary<string, object> Capabilities { get; init; };
}
```
```csharp
public sealed class BackupCoordinationStrategy : ResilienceStrategyBase
{
}
    public BackupCoordinationStrategy() : this(backupInterval: TimeSpan.FromHours(1), retainBackups: 24);
    public BackupCoordinationStrategy(TimeSpan backupInterval, int retainBackups);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public DateTimeOffset LastBackupTime;;
    public int BackupCount;;
    public string StartBackup(string type = "full");
    public void CompleteBackup(string jobId, long? sizeBytes = null, string? error = null);
    public bool IsBackupDue();
    public IReadOnlyList<Dictionary<string, object>> GetBackupStatus();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
private sealed class BackupJob
{
}
    public string Id { get; init; };
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public string Status { get; set; };
    public string? Error { get; set; }
    public long? SizeBytes { get; set; }
    public string Type { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/Bulkhead/BulkheadStrategies.cs
```csharp
public sealed class ThreadPoolBulkheadStrategy : ResilienceStrategyBase
{
}
    public ThreadPoolBulkheadStrategy() : this(maxParallelism: 10, maxQueueLength: 100);
    public ThreadPoolBulkheadStrategy(int maxParallelism, int maxQueueLength);
    protected override void Dispose(bool disposing);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int ActiveExecutions;;
    public int QueuedItems;;
    public int AvailableSlots;;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class SemaphoreBulkheadStrategy : ResilienceStrategyBase
{
}
    public SemaphoreBulkheadStrategy() : this(maxParallelism: 10, waitTimeout: TimeSpan.FromSeconds(30));
    public SemaphoreBulkheadStrategy(int maxParallelism, TimeSpan waitTimeout);
    protected override void Dispose(bool disposing);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int ActiveExecutions;;
    public int AvailableSlots;;
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class PartitionBulkheadStrategy : ResilienceStrategyBase
{
}
    public PartitionBulkheadStrategy() : this(defaultPartitionSize: 10, new Dictionary<string, int>());
    public PartitionBulkheadStrategy(int defaultPartitionSize, Dictionary<string, int> partitionSizes);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public void ConfigurePartition(string partitionKey, int maxParallelism);
    public int GetActiveExecutions(string partitionKey);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();
}
```
```csharp
public sealed class PriorityBulkheadStrategy : ResilienceStrategyBase
{
}
    public PriorityBulkheadStrategy() : this(highPrioritySlots: 5, normalSlots: 10, priorityThreshold: 5);
    public PriorityBulkheadStrategy(int highPrioritySlots, int normalSlots, int priorityThreshold);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class AdaptiveBulkheadStrategy : ResilienceStrategyBase
{
}
    public AdaptiveBulkheadStrategy() : this(minCapacity: 5, maxCapacity: 50, baseCapacity: 20, targetLatency: TimeSpan.FromMilliseconds(500), adaptationInterval: TimeSpan.FromSeconds(10));
    public AdaptiveBulkheadStrategy(int minCapacity, int maxCapacity, int baseCapacity, TimeSpan targetLatency, TimeSpan adaptationInterval);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public int CurrentCapacity
{
    get
    {
        lock (_adaptLock)
        {
            return _currentCapacity;
        }
    }
}
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class BulkheadRejectedException : Exception
{
}
    public BulkheadRejectedException(string message) : base(message);
    public BulkheadRejectedException(string message, Exception innerException) : base(message, innerException);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/CircuitBreaker/CircuitBreakerStrategies.cs
```csharp
public sealed class StandardCircuitBreakerStrategy : ResilienceStrategyBase
{
}
    public StandardCircuitBreakerStrategy() : this(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30), halfOpenSuccessThreshold: 2);
    public StandardCircuitBreakerStrategy(int failureThreshold, TimeSpan openDuration, int halfOpenSuccessThreshold);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public CircuitBreakerState State
{
    get
    {
        lock (_stateLock)
        {
            CheckTransitionFromOpen();
            return _state;
        }
    }
}
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
    public override void Reset();
}
```
```csharp
public sealed class SlidingWindowCircuitBreakerStrategy : ResilienceStrategyBase
{
}
    public SlidingWindowCircuitBreakerStrategy() : this(windowDuration: TimeSpan.FromMinutes(1), failureRateThreshold: 0.5, minimumRequests: 10, openDuration: TimeSpan.FromSeconds(30));
    public SlidingWindowCircuitBreakerStrategy(TimeSpan windowDuration, double failureRateThreshold, int minimumRequests, TimeSpan openDuration);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public CircuitBreakerState State
{
    get
    {
        lock (_stateLock)
        {
            CheckTransitions();
            return _state;
        }
    }
}
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
    public override void Reset();
}
```
```csharp
public sealed class CountBasedCircuitBreakerStrategy : ResilienceStrategyBase
{
}
    public CountBasedCircuitBreakerStrategy() : this(failureThreshold: 5, successThreshold: 3, openDuration: TimeSpan.FromSeconds(30));
    public CountBasedCircuitBreakerStrategy(int failureThreshold, int successThreshold, TimeSpan openDuration);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
    public override void Reset();
}
```
```csharp
public sealed class TimeBasedCircuitBreakerStrategy : ResilienceStrategyBase
{
}
    public TimeBasedCircuitBreakerStrategy() : this(bucketDuration: TimeSpan.FromSeconds(10), bucketsToTrack: 6, failureRateThreshold: 0.5, minimumRequests: 10, openDuration: TimeSpan.FromSeconds(30));
    public TimeBasedCircuitBreakerStrategy(TimeSpan bucketDuration, int bucketsToTrack, double failureRateThreshold, int minimumRequests, TimeSpan openDuration);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
    public override void Reset();
}
```
```csharp
public sealed class GradualRecoveryCircuitBreakerStrategy : ResilienceStrategyBase
{
}
    public GradualRecoveryCircuitBreakerStrategy() : this(failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30), initialPermitRate: 0.1, permitRateIncrement: 0.1, successesPerIncrement: 3);
    public GradualRecoveryCircuitBreakerStrategy(int failureThreshold, TimeSpan openDuration, double initialPermitRate, double permitRateIncrement, int successesPerIncrement);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
    public override void Reset();
}
```
```csharp
public sealed class AdaptiveCircuitBreakerStrategy : ResilienceStrategyBase
{
}
    public AdaptiveCircuitBreakerStrategy() : this(baseFailureThreshold: 0.5, minFailureThreshold: 0.2, maxFailureThreshold: 0.8, baseOpenDuration: TimeSpan.FromSeconds(30), minOpenDuration: TimeSpan.FromSeconds(10), maxOpenDuration: TimeSpan.FromMinutes(2), historyWindow: TimeSpan.FromMinutes(5), minimumRequests: 20, latencyThreshold: TimeSpan.FromSeconds(5));
    public AdaptiveCircuitBreakerStrategy(double baseFailureThreshold, double minFailureThreshold, double maxFailureThreshold, TimeSpan baseOpenDuration, TimeSpan minOpenDuration, TimeSpan maxOpenDuration, TimeSpan historyWindow, int minimumRequests, TimeSpan latencyThreshold);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
    public override void Reset();
}
```
```csharp
public sealed class CircuitBreakerOpenException : Exception
{
}
    public CircuitBreakerOpenException(string message) : base(message);
    public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/HealthChecks/HealthCheckStrategies.cs
```csharp
public sealed class HealthCheckResult
{
}
    public HealthStatus Status { get; init; }
    public string? Description { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public Exception? Exception { get; init; }
    public Dictionary<string, object> Data { get; init; };
    public Dictionary<string, HealthCheckResult> Components { get; init; };
}
```
```csharp
public sealed class LivenessHealthCheckStrategy : ResilienceStrategyBase
{
}
    public LivenessHealthCheckStrategy();
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public HealthCheckResult? LastResult;;
    public LivenessHealthCheckStrategy AddCheck(Func<CancellationToken, Task<bool>> check);
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class ReadinessHealthCheckStrategy : ResilienceStrategyBase
{
}
    public ReadinessHealthCheckStrategy();
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public HealthCheckResult? LastResult;;
    public ReadinessHealthCheckStrategy AddCheck(string name, Func<CancellationToken, Task<HealthCheckResult>> check);
    public ReadinessHealthCheckStrategy AddCheck(string name, Func<CancellationToken, Task<bool>> check);
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();
}
```
```csharp
public sealed class StartupProbeHealthCheckStrategy : ResilienceStrategyBase
{
}
    public StartupProbeHealthCheckStrategy() : this(startupCheck: _ => Task.FromResult(true), timeout: TimeSpan.FromMinutes(5), checkInterval: TimeSpan.FromSeconds(10), maxAttempts: 30);
    public StartupProbeHealthCheckStrategy(Func<CancellationToken, Task<bool>> startupCheck, TimeSpan timeout, TimeSpan checkInterval, int maxAttempts);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public bool StartupComplete;;
    public async Task<HealthCheckResult> WaitForStartupAsync(CancellationToken cancellationToken = default);
    public void MarkStartupComplete();
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();;
}
```
```csharp
public sealed class DeepHealthCheckStrategy : ResilienceStrategyBase
{
}
    public DeepHealthCheckStrategy() : this(cacheValidity: TimeSpan.FromSeconds(30));
    public DeepHealthCheckStrategy(TimeSpan cacheValidity);
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Category;;
    public override ResilienceCharacteristics Characteristics { get; };
    public HealthCheckResult? LastResult;;
    public DeepHealthCheckStrategy AddDependency(string name, Func<CancellationToken, Task<HealthCheckResult>> check, bool critical = true);
    public DeepHealthCheckStrategy AddDependency(string name, Func<CancellationToken, Task<bool>> check, bool critical = true);
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation, ResilienceContext? context, CancellationToken cancellationToken);
    protected override string? GetCurrentState();
}
```
```csharp
public sealed class HealthCheckFailedException : Exception
{
}
    public HealthCheckFailedException(string message) : base(message);
    public HealthCheckFailedException(string message, Exception innerException) : base(message, innerException);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResilience/Strategies/ChaosEngineering/ChaosVaccination/ChaosInjectionStrategies.cs
```csharp
public abstract class ChaosVaccinationStrategyBase
{
}
    public abstract string Name { get; }
    public string Category;;
    public abstract Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);;
}
```
```csharp
public sealed class VaccinationRunStrategy : ChaosVaccinationStrategyBase
{
}
    public override string Name;;
    public VaccinationRunStrategy(int failureThreshold = 3, int cooldownSeconds = 60);
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
    public string CurrentState;;
    public int ConsecutiveFailures;;
}
```
```csharp
public sealed class ImmuneAutoRemediationStrategy : ChaosVaccinationStrategyBase
{
}
    public override string Name;;
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}
```
```csharp
public sealed class BlastRadiusGuardStrategy : ChaosVaccinationStrategyBase
{
}
    public override string Name;;
    public BlastRadiusGuardStrategy(string[] targetPlugins, int maxDurationMs = 120_000);
    public override async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}
```
