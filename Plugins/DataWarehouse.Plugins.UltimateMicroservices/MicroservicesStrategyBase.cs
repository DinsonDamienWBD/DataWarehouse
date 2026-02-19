using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateMicroservices;

#region Enums and Core Types

/// <summary>
/// Microservices strategy category.
/// </summary>
public enum MicroservicesCategory
{
    /// <summary>Service discovery and registry.</summary>
    ServiceDiscovery,
    /// <summary>Inter-service communication.</summary>
    Communication,
    /// <summary>Load balancing.</summary>
    LoadBalancing,
    /// <summary>Circuit breaker and resilience.</summary>
    CircuitBreaker,
    /// <summary>API gateway.</summary>
    ApiGateway,
    /// <summary>Service orchestration.</summary>
    Orchestration,
    /// <summary>Distributed monitoring.</summary>
    Monitoring,
    /// <summary>Security and authentication.</summary>
    Security
}

/// <summary>
/// Communication protocol for microservices.
/// </summary>
public enum CommunicationProtocol
{
    /// <summary>HTTP/REST.</summary>
    Http,
    /// <summary>gRPC.</summary>
    Grpc,
    /// <summary>Message queue.</summary>
    MessageQueue,
    /// <summary>Event streaming.</summary>
    EventStream,
    /// <summary>WebSocket.</summary>
    WebSocket,
    /// <summary>GraphQL.</summary>
    GraphQL
}

/// <summary>
/// Service health status.
/// </summary>
public enum ServiceHealthStatus
{
    /// <summary>Healthy and operational.</summary>
    Healthy,
    /// <summary>Degraded performance.</summary>
    Degraded,
    /// <summary>Unhealthy.</summary>
    Unhealthy,
    /// <summary>Unknown status.</summary>
    Unknown
}

#endregion

#region Configuration Types

/// <summary>
/// Microservice configuration.
/// </summary>
public sealed record MicroserviceConfig
{
    /// <summary>Service unique identifier.</summary>
    public required string ServiceId { get; init; }
    /// <summary>Service name.</summary>
    public required string ServiceName { get; init; }
    /// <summary>Service version.</summary>
    public string Version { get; init; } = "1.0.0";
    /// <summary>Service endpoint.</summary>
    public required string Endpoint { get; init; }
    /// <summary>Health check endpoint.</summary>
    public string? HealthCheckEndpoint { get; init; }
    /// <summary>Communication protocols supported.</summary>
    public IReadOnlyList<CommunicationProtocol> SupportedProtocols { get; init; } = Array.Empty<CommunicationProtocol>();
    /// <summary>Service dependencies.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    /// <summary>Service metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Service instance runtime state.
/// </summary>
public sealed class ServiceInstance
{
    public required string InstanceId { get; init; }
    public required string ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public required string Endpoint { get; init; }
    public ServiceHealthStatus HealthStatus { get; set; } = ServiceHealthStatus.Healthy;
    public DateTimeOffset LastHealthCheck { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public int CurrentLoad { get; set; }
    public Dictionary<string, object> Metrics { get; init; } = new();
}

/// <summary>
/// Service request context.
/// </summary>
public sealed record ServiceRequest
{
    /// <summary>Request ID for tracing.</summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    /// <summary>Source service.</summary>
    public required string SourceService { get; init; }
    /// <summary>Target service.</summary>
    public required string TargetService { get; init; }
    /// <summary>Request method/operation.</summary>
    public required string Operation { get; init; }
    /// <summary>Request payload.</summary>
    public object? Payload { get; init; }
    /// <summary>Request headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();
    /// <summary>Request timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Service response.
/// </summary>
public sealed record ServiceResponse
{
    /// <summary>Request ID.</summary>
    public required string RequestId { get; init; }
    /// <summary>Success flag.</summary>
    public bool Success { get; init; }
    /// <summary>Response payload.</summary>
    public object? Payload { get; init; }
    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Response status code.</summary>
    public int StatusCode { get; init; }
    /// <summary>Duration in milliseconds.</summary>
    public double DurationMs { get; init; }
    /// <summary>Response timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

#endregion

#region Strategy Capabilities

/// <summary>
/// Capabilities of a microservices strategy.
/// </summary>
public sealed record MicroservicesStrategyCapabilities
{
    /// <summary>Supports service discovery.</summary>
    public bool SupportsServiceDiscovery { get; init; }
    /// <summary>Supports health checking.</summary>
    public bool SupportsHealthCheck { get; init; }
    /// <summary>Supports load balancing.</summary>
    public bool SupportsLoadBalancing { get; init; }
    /// <summary>Supports circuit breaking.</summary>
    public bool SupportsCircuitBreaker { get; init; }
    /// <summary>Supports distributed tracing.</summary>
    public bool SupportsDistributedTracing { get; init; }
    /// <summary>Supports retry policies.</summary>
    public bool SupportsRetry { get; init; }
    /// <summary>Supports rate limiting.</summary>
    public bool SupportsRateLimiting { get; init; }
    /// <summary>Typical latency overhead in ms.</summary>
    public double TypicalLatencyOverheadMs { get; init; } = 5.0;
}

#endregion

#region Strategy Base

/// <summary>
/// Abstract base class for microservices strategies.
/// </summary>
public abstract class MicroservicesStrategyBase
{
    private readonly ConcurrentDictionary<string, long> _operationCounts = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private long _totalOperations;
    private long _successfulOperations;
    private long _failedOperations;
    private bool _initialized;
    private DateTime? _healthCacheExpiry;
    private bool? _cachedHealthy;

    /// <summary>Strategy unique identifier.</summary>
    public abstract string StrategyId { get; }

    /// <summary>Strategy display name.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Strategy category.</summary>
    public abstract MicroservicesCategory Category { get; }

    /// <summary>Strategy capabilities.</summary>
    public abstract MicroservicesStrategyCapabilities Capabilities { get; }

    /// <summary>Semantic description for AI discovery.</summary>
    public abstract string SemanticDescription { get; }

    /// <summary>Tags for categorization.</summary>
    public abstract string[] Tags { get; }

    /// <summary>Gets whether this strategy has been initialized.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>Initializes the strategy. Idempotent.</summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        IncrementCounter("initialized");
        return Task.CompletedTask;
    }

    /// <summary>Shuts down the strategy gracefully.</summary>
    public virtual Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return Task.CompletedTask;
        _initialized = false;
        IncrementCounter("shutdown");
        return Task.CompletedTask;
    }

    /// <summary>Gets cached health status, refreshing every 60 seconds.</summary>
    public bool IsHealthy()
    {
        if (_cachedHealthy.HasValue && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
            return _cachedHealthy.Value;
        _cachedHealthy = _initialized;
        _healthCacheExpiry = DateTime.UtcNow.AddSeconds(60);
        return _cachedHealthy.Value;
    }

    /// <summary>Increments a named counter. Thread-safe.</summary>
    protected void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => Interlocked.Increment(ref current));
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => new Dictionary<string, long>(_counters);

    /// <summary>
    /// Records an operation for statistics.
    /// </summary>
    protected void RecordOperation(string operationType = "default", bool success = true)
    {
        Interlocked.Increment(ref _totalOperations);
        _operationCounts.AddOrUpdate(operationType, 1, (_, c) => c + 1);

        if (success)
            Interlocked.Increment(ref _successfulOperations);
        else
            Interlocked.Increment(ref _failedOperations);
    }

    /// <summary>
    /// Gets operation statistics.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetOperationStats() =>
        new Dictionary<string, long>(_operationCounts)
        {
            ["total"] = Interlocked.Read(ref _totalOperations),
            ["successful"] = Interlocked.Read(ref _successfulOperations),
            ["failed"] = Interlocked.Read(ref _failedOperations)
        };

    /// <summary>
    /// Gets knowledge object for AI integration.
    /// </summary>
    public virtual KnowledgeObject GetKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"microservices.strategy.{StrategyId}",
            Topic = "microservices.strategy",
            SourcePluginId = "com.datawarehouse.microservices.ultimate",
            SourcePluginName = DisplayName,
            KnowledgeType = "capability",
            Description = SemanticDescription,
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["displayName"] = DisplayName,
                ["category"] = Category.ToString(),
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["serviceDiscovery"] = Capabilities.SupportsServiceDiscovery,
                    ["healthCheck"] = Capabilities.SupportsHealthCheck,
                    ["loadBalancing"] = Capabilities.SupportsLoadBalancing,
                    ["circuitBreaker"] = Capabilities.SupportsCircuitBreaker,
                    ["distributedTracing"] = Capabilities.SupportsDistributedTracing,
                    ["retry"] = Capabilities.SupportsRetry,
                    ["rateLimiting"] = Capabilities.SupportsRateLimiting
                }
            },
            Tags = Tags
        };
    }

    /// <summary>
    /// Gets registered capability for the strategy.
    /// </summary>
    public virtual RegisteredCapability GetCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"microservices.strategy.{StrategyId}",
            DisplayName = DisplayName,
            Description = SemanticDescription,
            Category = SDK.Contracts.CapabilityCategory.Infrastructure,
            SubCategory = Category.ToString(),
            PluginId = "com.datawarehouse.microservices.ultimate",
            PluginName = "Ultimate Microservices",
            PluginVersion = "1.0.0",
            Tags = Tags,
            SemanticDescription = SemanticDescription
        };
    }
}

#endregion
