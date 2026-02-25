# Plugin: UltimateDataLake
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataLake

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/UltimateDataLakePlugin.cs
```csharp
public sealed class UltimateDataLakePlugin : DataManagementPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StrategyRegistry<IDataLakeStrategy> Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public UltimateDataLakePlugin();
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
                CapabilityId = "datalake",
                DisplayName = "Ultimate Data Lake",
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
                "datalake",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            capabilities.Add(new() { CapabilityId = $"datalake.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.DataManagement, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category.ToString(), ["supportsAsync"] = strategy.Capabilities.SupportsAsync, ["supportsBatch"] = strategy.Capabilities.SupportsBatch, ["supportsStreaming"] = strategy.Capabilities.SupportsStreaming, ["supportsAcid"] = strategy.Capabilities.SupportsAcid, ["supportsTimeTravel"] = strategy.Capabilities.SupportsTimeTravel }, SemanticDescription = strategy.SemanticDescription });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);;
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStopCoreAsync();
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Architecture/DataLakeArchitectureStrategies.cs
```csharp
public sealed class LambdaArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class KappaArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DeltaLakeArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class IcebergArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class HudiArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LakehouseArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataMeshArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataFabricArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Catalog/DataCatalogStrategies.cs
```csharp
public sealed class MetadataCatalogStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataDiscoveryStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class BusinessGlossaryStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TagManagementStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class HiveMetastoreStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AwsGlueCatalogStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class UnityCatalogStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Governance/DataLakeGovernanceStrategies.cs
```csharp
public sealed class DataQualityRulesStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProfilingStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataRetentionPolicyStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PiiDetectionStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataStewardshipStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ComplianceAutomationStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataContractStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Integration/LakeWarehouseIntegrationStrategies.cs
```csharp
public sealed class MaterializedViewStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LakeWarehouseSyncStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class QueryFederationStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ExternalTableStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataVirtualizationStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class IncrementalLoadStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ReverseEtlStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Lineage/DataLineageStrategies.cs
```csharp
public sealed class ColumnLevelLineageStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TableLevelLineageStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SqlParserLineageStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SparkLineageStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ImpactAnalysisStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RootCauseAnalysisStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class OpenLineageStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Schema/SchemaOnReadStrategies.cs
```csharp
public sealed class DynamicSchemaInferenceStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaEvolutionStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaMergeStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaValidationStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaRegistryIntegrationStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PolyglotSchemaStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Security/DataLakeSecurityStrategies.cs
```csharp
public sealed class RowLevelSecurityStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ColumnLevelSecurityStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataMaskingStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EncryptionAtRestStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RbacStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AbacStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AuditLoggingStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLake/Strategies/Zones/DataLakeZoneStrategies.cs
```csharp
public sealed class MedallionArchitectureStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RawZoneStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CuratedZoneStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ConsumptionZoneStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SandboxZoneStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ZonePromotionStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ArchiveZoneStrategy : DataLakeStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataLakeCategory Category;;
    public override DataLakeCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
