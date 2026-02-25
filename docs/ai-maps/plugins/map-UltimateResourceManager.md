# Plugin: UltimateResourceManager
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateResourceManager

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/ResourceStrategyBase.cs
```csharp
public sealed record ResourceMetrics
{
}
    public double CpuPercent { get; init; }
    public long MemoryBytes { get; init; }
    public long IopsRate { get; init; }
    public long IoBandwidth { get; init; }
    public double GpuPercent { get; init; }
    public long GpuMemoryBytes { get; init; }
    public long NetworkBandwidth { get; init; }
    public DateTime Timestamp { get; init; };
}
```
```csharp
public sealed record ResourceQuota
{
}
    public required string QuotaId { get; init; }
    public double MaxCpuCores { get; init; }
    public long MaxMemoryBytes { get; init; }
    public long MaxIops { get; init; }
    public long MaxIoBandwidth { get; init; }
    public double MaxGpuPercent { get; init; }
    public long MaxNetworkBandwidth { get; init; }
    public int Priority { get; init; };
    public bool HardLimit { get; init; };
}
```
```csharp
public sealed record ResourceRequest
{
}
    public string RequestId { get; init; };
    public required string RequesterId { get; init; }
    public double CpuCores { get; init; }
    public long MemoryBytes { get; init; }
    public long Iops { get; init; }
    public long IoBandwidth { get; init; }
    public double GpuPercent { get; init; }
    public int Priority { get; init; };
    public TimeSpan Timeout { get; init; };
}
```
```csharp
public sealed record ResourceAllocation
{
}
    public required string RequestId { get; init; }
    public bool Success { get; init; }
    public double AllocatedCpuCores { get; init; }
    public long AllocatedMemoryBytes { get; init; }
    public long AllocatedIops { get; init; }
    public long AllocatedIoBandwidth { get; init; }
    public double AllocatedGpuPercent { get; init; }
    public string? AllocationHandle { get; init; }
    public string? FailureReason { get; init; }
    public DateTime AllocatedAt { get; init; };
}
```
```csharp
public sealed record ResourceStrategyCapabilities
{
}
    public required bool SupportsCpu { get; init; }
    public required bool SupportsMemory { get; init; }
    public required bool SupportsIO { get; init; }
    public required bool SupportsGpu { get; init; }
    public required bool SupportsNetwork { get; init; }
    public required bool SupportsQuotas { get; init; }
    public required bool SupportsHierarchicalQuotas { get; init; }
    public required bool SupportsPreemption { get; init; }
}
```
```csharp
public interface IResourceStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    ResourceCategory Category { get; }
    ResourceStrategyCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
    Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);;
    Task<ResourceAllocation> AllocateAsync(ResourceRequest request, CancellationToken ct = default);;
    Task<bool> ReleaseAsync(string allocationHandle, CancellationToken ct = default);;
    Task<bool> ApplyQuotaAsync(ResourceQuota quota, CancellationToken ct = default);;
    Task InitializeAsync(CancellationToken ct = default);;
    Task DisposeAsync();;
}
```
```csharp
public abstract class ResourceStrategyBase : StrategyBase, IResourceStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract ResourceCategory Category { get; }
    public abstract ResourceStrategyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected IReadOnlyDictionary<string, ResourceAllocation> ActiveAllocations;;
    protected IReadOnlyDictionary<string, ResourceQuota> ActiveQuotas;;
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected virtual Task InitializeCoreAsync(CancellationToken ct);;
    protected virtual Task DisposeCoreAsync();;
    public abstract Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);;
    public virtual async Task<ResourceAllocation> AllocateAsync(ResourceRequest request, CancellationToken ct = default);
    public virtual async Task<bool> ReleaseAsync(string allocationHandle, CancellationToken ct = default);
    public virtual Task<bool> ApplyQuotaAsync(ResourceQuota quota, CancellationToken ct = default);
    protected abstract Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);;
    protected virtual Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);;
    protected virtual Task<bool> ApplyQuotaCoreAsync(ResourceQuota quota, CancellationToken ct);;
}
```
```csharp
public sealed class ResourceStrategyRegistry
{
}
    public void Register(IResourceStrategy strategy);
    public bool Unregister(string strategyId);;
    public IResourceStrategy? Get(string strategyId);;
    public IReadOnlyCollection<IResourceStrategy> GetAll();;
    public IReadOnlyCollection<IResourceStrategy> GetByCategory(ResourceCategory category);;
    public int Count;;
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/UltimateResourceManagerPlugin.cs
```csharp
public sealed class UltimateResourceManagerPlugin : InfrastructurePluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string InfrastructureDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public ResourceStrategyRegistry Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool FairSchedulingEnabled { get => _fairSchedulingEnabled; set => _fairSchedulingEnabled = value; }
    public bool PreemptionEnabled { get => _preemptionEnabled; set => _preemptionEnabled = value; }
    public UltimateResourceManagerPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/GpuStrategies.cs
```csharp
public sealed class TimeSlicingGpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class MigGpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class MpsGpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class VgpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/PowerStrategies.cs
```csharp
public sealed class DvfsCpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class CStateStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class IntelRaplStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class AmdRaplStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class CarbonAwareStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class BatteryAwareStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class ThermalThrottleStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class SuspendResumeStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/NetworkStrategies.cs
```csharp
public sealed class TokenBucketNetworkStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class QosNetworkStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class CompositeResourceStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class HierarchicalQuotaStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/IoStrategies.cs
```csharp
public sealed class DeadlineIoStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class TokenBucketIoStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class BandwidthLimitIoStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class PriorityIoStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/CpuStrategies.cs
```csharp
public sealed class FairShareCpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class PriorityCpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class AffinityCpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class RealTimeCpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class NumaCpuStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/MemoryStrategies.cs
```csharp
public sealed class CgroupMemoryStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class BalloonMemoryStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class PressureAwareMemoryStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```
```csharp
public sealed class HugePagesMemoryStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateResourceManager/Strategies/ContainerStrategies.cs
```csharp
public sealed class CgroupV2Strategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class DockerResourceStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class KubernetesResourceStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class PodmanResourceStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class WindowsJobObjectStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class WindowsContainerStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class ProcessGroupStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
```csharp
public sealed class NamespaceIsolationStrategy : ResourceStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ResourceCategory Category;;
    public override ResourceStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct);
}
```
