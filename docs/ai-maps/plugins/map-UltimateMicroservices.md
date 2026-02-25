# Plugin: UltimateMicroservices
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateMicroservices

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/UltimateMicroservicesPlugin.cs
```csharp
public sealed class UltimateMicroservicesPlugin : PlatformPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string PlatformDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public IReadOnlyDictionary<MicroservicesCategory, IReadOnlyList<MicroservicesStrategyBase>> StrategiesByCategory
{
    get
    {
        return _strategies.Values.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => (IReadOnlyList<MicroservicesStrategyBase>)g.ToList());
    }
}
    public UltimateMicroservicesPlugin();
    public void RegisterStrategy(MicroservicesStrategyBase strategy);
    public MicroservicesStrategyBase? GetStrategy(string strategyId);
    public IReadOnlyList<MicroservicesStrategyBase> GetStrategiesForCategory(MicroservicesCategory category);;
    public void RegisterService(MicroserviceConfig config);
    public ServiceInstance? GetService(string serviceId);
    public IReadOnlyList<ServiceInstance> ListServices();;
    public void RecordRequest(ServiceRequest request, bool success, double durationMs);
    public ServiceStatistics GetServiceStatistics(string serviceId);
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
                CapabilityId = $"{Id}.orchestration",
                DisplayName = "Microservices Orchestration",
                Description = "Orchestrate microservices architecture patterns",
                Category = SDK.Contracts.CapabilityCategory.Infrastructure,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = ["microservices", "orchestration", "distributed"]
            }
        };
        foreach (var strategy in _strategies.Values)
        {
            capabilities.Add(strategy.GetCapability());
        }

        return capabilities;
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    protected override Task OnStartCoreAsync(CancellationToken ct);;
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    public override Task OnMessageAsync(PluginMessage message);
    protected override void Dispose(bool disposing);
}
```
```csharp
public sealed record ServiceStatistics
{
}
    public required string ServiceId { get; init; }
    public long TotalRequests { get; init; }
    public int UniqueCallers { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/MicroservicesStrategyBase.cs
```csharp
public sealed record MicroserviceConfig
{
}
    public required string ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public string Version { get; init; };
    public required string Endpoint { get; init; }
    public string? HealthCheckEndpoint { get; init; }
    public IReadOnlyList<CommunicationProtocol> SupportedProtocols { get; init; };
    public IReadOnlyList<string> Dependencies { get; init; };
    public Dictionary<string, string> Metadata { get; init; };
}
```
```csharp
public sealed class ServiceInstance
{
}
    public required string InstanceId { get; init; }
    public required string ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public required string Endpoint { get; init; }
    public ServiceHealthStatus HealthStatus { get; set; };
    public DateTimeOffset LastHealthCheck { get; set; };
    public DateTimeOffset RegisteredAt { get; init; };
    public int CurrentLoad { get; set; }
    public Dictionary<string, object> Metrics { get; init; };
}
```
```csharp
public sealed record ServiceRequest
{
}
    public string RequestId { get; init; };
    public required string SourceService { get; init; }
    public required string TargetService { get; init; }
    public required string Operation { get; init; }
    public object? Payload { get; init; }
    public Dictionary<string, string> Headers { get; init; };
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
public sealed record ServiceResponse
{
}
    public required string RequestId { get; init; }
    public bool Success { get; init; }
    public object? Payload { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; }
    public double DurationMs { get; init; }
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
public sealed record MicroservicesStrategyCapabilities
{
}
    public bool SupportsServiceDiscovery { get; init; }
    public bool SupportsHealthCheck { get; init; }
    public bool SupportsLoadBalancing { get; init; }
    public bool SupportsCircuitBreaker { get; init; }
    public bool SupportsDistributedTracing { get; init; }
    public bool SupportsRetry { get; init; }
    public bool SupportsRateLimiting { get; init; }
    public double TypicalLatencyOverheadMs { get; init; };
}
```
```csharp
public abstract class MicroservicesStrategyBase
{
}
    public abstract string StrategyId { get; }
    public abstract string DisplayName { get; }
    public abstract MicroservicesCategory Category { get; }
    public abstract MicroservicesStrategyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    public bool IsInitialized;;
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default);
    public virtual Task ShutdownAsync(CancellationToken cancellationToken = default);
    public bool IsHealthy();
    protected void IncrementCounter(string name);
    public IReadOnlyDictionary<string, long> GetCounters();;
    protected void RecordOperation(string operationType = "default", bool success = true);
    public IReadOnlyDictionary<string, long> GetOperationStats();;
    public virtual KnowledgeObject GetKnowledge();
    public virtual RegisteredCapability GetCapability();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/Security/SecurityStrategies.cs
```csharp
public sealed class OAuth2SecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class JwtSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MtlsSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ServiceMeshSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApiKeySecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SamlSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class OpenIdConnectSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RbacSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SpiffeSpireSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class VaultSecretsSecurityStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/Communication/CommunicationStrategies.cs
```csharp
public sealed class RestHttpCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GrpcCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GraphQlCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MessageQueueCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EventStreamingCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class WebSocketCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApacheThriftCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AmqpCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RedisPubSubCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class NatsCommunicationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/ServiceDiscovery/ServiceDiscoveryStrategies.cs
```csharp
public sealed class ConsulServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<ServiceInstance?> DiscoverServiceAsync(string serviceName, CancellationToken ct = default);
    public Task RegisterServiceAsync(ServiceInstance instance, CancellationToken ct = default);
    public Task DeregisterServiceAsync(string instanceId, CancellationToken ct = default);
}
```
```csharp
public sealed class EurekaServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<List<ServiceInstance>> GetInstancesAsync(string serviceName, CancellationToken ct = default);
}
```
```csharp
public sealed class ZookeeperServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EtcdServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DnsServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class KubernetesServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AwsCloudMapServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AzureServiceFabricDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class NacosServiceDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class IstioServiceMeshDiscoveryStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/Monitoring/MonitoringStrategies.cs
```csharp
public sealed class PrometheusMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GrafanaMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class JaegerTracingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ZipkinTracingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ElkStackMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DatadogMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class NewRelicMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AppDynamicsMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LightstepMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class OpenTelemetryMonitoringStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/ApiGateway/ApiGatewayStrategies.cs
```csharp
public sealed class KongApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class NginxApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EnvoyApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AwsApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AzureApiManagementStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GcpApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TykApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApisixApiGatewayStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/LoadBalancing/LoadBalancingStrategies.cs
```csharp
public sealed class RoundRobinLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LeastConnectionsLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class WeightedLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class IpHashLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ConsistentHashingLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RandomLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ResponseTimeLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ResourceBasedLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GeographicLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PowerOfTwoChoicesLoadBalancingStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/CircuitBreaker/CircuitBreakerStrategies.cs
```csharp
public sealed class HystrixCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class Resilience4jCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PollyCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class BulkheadIsolationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TimeoutCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AdaptiveCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class HalfOpenCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FailFastCircuitBreakerStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateMicroservices/Strategies/Orchestration/OrchestrationStrategies.cs
```csharp
public sealed class KubernetesOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DockerSwarmOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class NomadOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MesosOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EcsOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AksOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GkeOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ServiceFabricOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class OpenShiftOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RancherOrchestrationStrategy : MicroservicesStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override MicroservicesCategory Category;;
    public override MicroservicesStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
