# Plugin: UltimateDataMesh
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataMesh

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/UltimateDataMeshPlugin.cs
```csharp
public sealed class UltimateDataMeshPlugin : DataManagementPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StrategyRegistry<IDataMeshStrategy> Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public UltimateDataMeshPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = "datamesh",
                DisplayName = "Ultimate Data Mesh",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.DataManagement,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = SemanticTags
            }
        };
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "datamesh",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            capabilities.Add(new() { CapabilityId = $"datamesh.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.DataManagement, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category.ToString(), ["supportsAsync"] = strategy.Capabilities.SupportsAsync, ["supportsRealTime"] = strategy.Capabilities.SupportsRealTime, ["supportsBatch"] = strategy.Capabilities.SupportsBatch, ["supportsEventDriven"] = strategy.Capabilities.SupportsEventDriven, ["supportsMultiTenancy"] = strategy.Capabilities.SupportsMultiTenancy, ["supportsFederation"] = strategy.Capabilities.SupportsFederation }, SemanticDescription = strategy.SemanticDescription });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);;
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Scaling/DataMeshScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: DataMesh scaling with persistent stores, bounded caches, cross-domain federation")]
public sealed class DataMeshScalingManager : IScalableSubsystem, IDisposable
{
}
    public const int DefaultMaxDomains = 1_000;
    public const int DefaultMaxProducts = 10_000;
    public const int DefaultMaxConsumers = 50_000;
    public const int DefaultMaxShares = 10_000;
    public const int DefaultMaxPolicies = 5_000;
    public const string FederationTopic = "dw.mesh.federation.events";
    public DataMeshScalingManager(IPersistentBackingStore? backingStore = null, IMessageBus? messageBus = null, ScalingLimits? initialLimits = null);
    public async Task PutDomainAsync(string domainId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetDomainAsync(string domainId, CancellationToken ct = default);
    public async Task PutProductAsync(string productId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetProductAsync(string productId, CancellationToken ct = default);
    public async Task PutConsumerAsync(string consumerId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetConsumerAsync(string consumerId, CancellationToken ct = default);
    public async Task PutShareAsync(string shareId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetShareAsync(string shareId, CancellationToken ct = default);
    public async Task PutMeshPolicyAsync(string policyId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetMeshPolicyAsync(string policyId, CancellationToken ct = default);
    public long FederationEventsPublished;;
    public long FederationEventsReceived;;
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
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
        int totalEntries = _domains.Count + _products.Count + _consumers.Count + _shares.Count + _meshPolicies.Count;
        int maxCapacity = DefaultMaxDomains + DefaultMaxProducts + DefaultMaxConsumers + DefaultMaxShares + DefaultMaxPolicies;
        if (maxCapacity == 0)
            return BackpressureState.Normal;
        double utilization = (double)totalEntries / maxCapacity;
        return utilization switch
        {
            >= 0.85 => BackpressureState.Critical,
            >= 0.50 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }
}
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/SelfServe/SelfServeStrategies.cs
```csharp
public sealed class SelfServiceDataPlatformStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class InfrastructureAsCodeStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductTemplateStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AutomatedPipelineStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SelfServiceAnalyticsStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApiPortalStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ResourceQuotaStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/DomainDiscovery/DomainDiscoveryStrategies.cs
```csharp
public sealed class DataCatalogDiscoveryStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SemanticSearchStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataLineageDiscoveryStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DomainMarketplaceStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class KnowledgeGraphStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AutoDiscoveryStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProfilingDiscoveryStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/CrossDomainSharing/CrossDomainSharingStrategies.cs
```csharp
public sealed class DataSharingAgreementStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataFederationStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataContractSharingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EventBasedSharingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApiGatewaySharingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataVirtualizationStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataMeshBridgeStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/DataProduct/DataProductStrategies.cs
```csharp
public sealed class DataProductDefinitionStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductSlaStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductVersioningStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductQualityStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductDocumentationStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductConsumptionStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductFeedbackStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/MeshSecurity/MeshSecurityStrategies.cs
```csharp
public sealed class ZeroTrustSecurityStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MeshRbacStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MeshAbacStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataEncryptionStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataMaskingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AuditLoggingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class IdentityFederationStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ThreatDetectionStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/FederatedGovernance/FederatedGovernanceStrategies.cs
```csharp
public sealed class FederatedPolicyManagementStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ComputationalGovernanceStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GlobalStandardsStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class InteroperabilityStandardsStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ComplianceFrameworkStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataClassificationGovernanceStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GovernanceCouncilStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/MeshObservability/MeshObservabilityStrategies.cs
```csharp
public sealed class MeshMetricsStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DistributedTracingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataQualityMonitoringStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class UsageAnalyticsStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MeshAlertingStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MeshDashboardStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LogAggregationStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataMesh/Strategies/DomainOwnership/DomainOwnershipStrategies.cs
```csharp
public sealed class DomainTeamAutonomyStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DomainBoundedContextStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DomainLifecycleStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DomainOwnershipTransferStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DomainTeamStructureStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DomainDataSovereigntyStrategy : DataMeshStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataMeshCategory Category;;
    public override DataMeshCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
