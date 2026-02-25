# Plugin: UltimateDataCatalog
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataCatalog

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/DataCatalogStrategy.cs
```csharp
public sealed record DataCatalogCapabilities
{
}
    public required bool SupportsAsync { get; init; }
    public required bool SupportsBatch { get; init; }
    public required bool SupportsRealTime { get; init; }
    public required bool SupportsFederation { get; init; }
    public required bool SupportsVersioning { get; init; }
    public required bool SupportsMultiTenancy { get; init; }
    public long MaxEntries { get; init; }
}
```
```csharp
public interface IDataCatalogStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    DataCatalogCategory Category { get; }
    DataCatalogCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}
```
```csharp
public abstract class DataCatalogStrategyBase : StrategyBase, IDataCatalogStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract DataCatalogCategory Category { get; }
    public abstract DataCatalogCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/UltimateDataCatalogPlugin.cs
```csharp
public sealed class UltimateDataCatalogPlugin : DataManagementPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public StrategyRegistry<IDataCatalogStrategy> Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public UltimateDataCatalogPlugin();
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
                CapabilityId = "datacatalog",
                DisplayName = "Ultimate Data Catalog",
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
                "datacatalog",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            capabilities.Add(new() { CapabilityId = $"datacatalog.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.DataManagement, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category.ToString(), ["supportsAsync"] = strategy.Capabilities.SupportsAsync, ["supportsBatch"] = strategy.Capabilities.SupportsBatch, ["supportsRealTime"] = strategy.Capabilities.SupportsRealTime, ["supportsFederation"] = strategy.Capabilities.SupportsFederation, ["supportsVersioning"] = strategy.Capabilities.SupportsVersioning }, SemanticDescription = strategy.SemanticDescription });
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
```csharp
public sealed record CatalogAsset
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Source { get; init; }
    public string? Description { get; init; }
    public string? Owner { get; init; }
    public string[]? Tags { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}
```
```csharp
public sealed record CatalogRelationship
{
}
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed record BusinessTerm
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Definition { get; init; }
    public required string Domain { get; init; }
    public string[]? Synonyms { get; init; }
    public string[]? RelatedTerms { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Scaling/CatalogScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Paginated result for catalog and lineage queries")]
public sealed class PagedResult<T>
{
}
    public IReadOnlyList<T> Items { get; }
    public int TotalCount { get; }
    public bool HasMore { get; }
    public PagedResult(IReadOnlyList<T> items, int totalCount, bool hasMore);
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Catalog scaling with persistence, LRU cache, sharding, pagination")]
public sealed class CatalogScalingManager : IScalableSubsystem, IDisposable
{
}
    public const int DefaultMaxAssetsPerShard = 100_000;
    public const int DefaultMaxRelationships = 500_000;
    public const int DefaultMaxGlossaryTerms = 50_000;
    public const int MaxShardCount = 256;
    public const int AssetsPerShard = 100_000;
    public CatalogScalingManager(IPersistentBackingStore? backingStore = null, ScalingLimits? initialLimits = null);
    public async Task PutAssetAsync(string assetId, string namespacePrefix, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetAssetAsync(string assetId, string namespacePrefix, CancellationToken ct = default);
    public PagedResult<KeyValuePair<string, byte[]>> ListAssets(int offset, int limit);
    public async Task PutRelationshipAsync(string relationshipId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetRelationshipAsync(string relationshipId, CancellationToken ct = default);
    public PagedResult<KeyValuePair<string, byte[]>> ListRelationships(int offset, int limit);
    public async Task PutGlossaryTermAsync(string termId, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> GetGlossaryTermAsync(string termId, CancellationToken ct = default);
    public PagedResult<KeyValuePair<string, byte[]>> SearchGlossary(string query, int offset, int limit);
    public int ShardCount;;
    public long TotalAssetCount;;
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
        long totalAssets = Interlocked.Read(ref _totalAssetCount);
        long maxCapacity = (long)_shardCount * DefaultMaxAssetsPerShard;
        if (maxCapacity == 0)
            return BackpressureState.Normal;
        double utilization = (double)totalAssets / maxCapacity;
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

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/AccessControl/AccessControlStrategies.cs
```csharp
public sealed class RoleBasedAccessControlStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AttributeBasedAccessControlStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataAccessPolicySyncStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AccessRequestWorkflowStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataMaskingIntegrationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RowLevelSecurityStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SsoIntegrationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AccessAuditTrailStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataPrivacyComplianceStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TemporaryAccessGrantStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/AssetDiscovery/AssetDiscoveryStrategies.cs
```csharp
public sealed class AutomatedCrawlerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DatabaseSchemaScannerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FileSystemScannerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CloudStorageScannerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApiEndpointDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class StreamingSourceDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataLakeDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MlModelRegistryDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FeatureStoreDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class IncrementalChangeDetectionStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/AssetDiscovery/DarkDataDiscoveryStrategies.cs
```csharp
public sealed record DarkDataScanConfig(int MaxObjectsPerScan = 10000, int StaleDaysThreshold = 180, bool SkipAlreadyScored = true, string[]? IncludeStorageTiers = null, string[]? ExcludePatterns = null)
{
}
    public string[] EffectiveExcludePatterns;;
}
```
```csharp
public sealed class UntaggedObjectScanStrategy : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<List<DarkDataCandidate>> ScanAsync(IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects, DarkDataScanConfig config, string scanScope = "default", CancellationToken ct = default);
    public DateTime GetWatermark(string scanScope = "default");
}
```
```csharp
public sealed class OrphanedDataScanStrategy : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<List<DarkDataCandidate>> ScanAsync(IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects, IReadOnlySet<string> activePrincipalIds, IReadOnlySet<string> existingObjectIds, DarkDataScanConfig config, CancellationToken ct = default);
}
```
```csharp
public sealed class ShadowDataScanStrategy : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<List<DarkDataCandidate>> ScanAsync(IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects, IReadOnlySet<string> registeredLocations, DarkDataScanConfig config, CancellationToken ct = default);
}
```
```csharp
public sealed class DarkDataDiscoveryOrchestrator : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public UntaggedObjectScanStrategy UntaggedScanner;;
    public OrphanedDataScanStrategy OrphanedScanner;;
    public ShadowDataScanStrategy ShadowScanner;;
    public IReadOnlyDictionary<DateTime, DarkDataScanResult> ScanHistory;;
    public async Task<DarkDataScanResult> RunDiscoveryAsync(IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects, IReadOnlySet<string> activePrincipalIds, IReadOnlySet<string> existingObjectIds, IReadOnlySet<string> registeredLocations, IConsciousnessScorer scorer, DarkDataScanConfig config, CancellationToken ct = default);
    public async Task<DarkDataScanResult> RunIncrementalAsync(IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects, IReadOnlySet<string> activePrincipalIds, IReadOnlySet<string> existingObjectIds, IReadOnlySet<string> registeredLocations, IConsciousnessScorer scorer, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/AssetDiscovery/RetroactiveScoringStrategies.cs
```csharp
public sealed class RetroactiveBatchScoringStrategy : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public async Task<RescoringResult> ScoreBatchAsync(IReadOnlyList<(string objectId, byte[] data, Dictionary<string, object> metadata)> objects, IConsciousnessScorer scorer, CancellationToken ct = default, Action<int, int>? progressCallback = null);
}
```
```csharp
public sealed class IncrementalRescoringStrategy : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public async Task<RescoringResult> RescoreChangedAsync(IReadOnlyList<(string objectId, Dictionary<string, object> currentMetadata, Dictionary<string, object> previousMetadata)> objects, IConsciousnessScorer scorer, RescoringConfig? config = null, CancellationToken ct = default);
}
```
```csharp
public sealed class ScoreDecayRecalculationStrategy : ConsciousnessStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override ConsciousnessCategory Category;;
    public override ConsciousnessCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyCollection<(string ObjectId, ConsciousnessGrade OldGrade, ConsciousnessGrade NewGrade, DateTime DecayedAt)> GradeBoundaryEvents;;
    public async Task<RescoringResult> RecalculateDecayAsync(IReadOnlyList<ConsciousnessScore> existingScores, IConsciousnessScorer scorer, RescoringConfig? config = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/CatalogApi/CatalogApiStrategies.cs
```csharp
public sealed class RestApiStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GraphQlApiStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PythonSdkStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class JavaSdkStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CliToolStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class WebhookEventsStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EventStreamingStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class BulkImportExportStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApiGatewayIntegrationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ThirdPartyCatalogIntegrationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/CatalogUI/CatalogUIStrategies.cs
```csharp
public sealed class AssetBrowserStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaViewerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LineageVisualizerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProfileDashboardStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SearchInterfaceStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataQualityScorecardStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CatalogAdminConsoleStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GlossaryEditorStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CatalogMobileAppStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EmbeddedWidgetStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/DataRelationships/DataRelationshipsStrategies.cs
```csharp
public sealed class DataLineageGraphStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ForeignKeyRelationshipStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class InferredRelationshipStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class KnowledgeGraphStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataDomainMappingStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApplicationDependencyStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class PipelineDependencyStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CrossDatasetJoinStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class BusinessProcessMappingStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataContractStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/Documentation/DocumentationStrategies.cs
```csharp
public sealed class BusinessGlossaryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataDictionaryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AutomatedDocumentationGeneratorStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataQualityDocumentationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class UsageDocumentationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataOwnershipDocumentationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CollaborativeAnnotationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataClassificationStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DocumentationTemplateStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DocumentationExportStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/LivingCatalog/LivingCatalogStrategies.cs
```csharp
public sealed class SelfLearningCatalogStrategy : DataCatalogStrategyBase
{
}
    internal record CatalogEntry(string AssetId, string Name, string Description, Dictionary<string, string> Metadata, DateTimeOffset LastUpdated, int Version);;
    internal record FeedbackRecord(string FeedbackId, string AssetId, string FieldName, string OriginalValue, string CorrectedValue, DateTimeOffset Timestamp);;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterAsset(string assetId, string name, string description, Dictionary<string, string>? metadata);
    public void RecordFeedback(string assetId, string fieldName, string originalValue, string correctedValue);
    public void ApplyLearning(string assetId);
    public double GetConfidence(string assetId);
}
```
```csharp
public sealed class AutoTaggingStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlySet<string> GenerateTags(string assetId, string name, string description, IReadOnlyList<string> columnNames);
    public IReadOnlySet<string> GetTags(string assetId);
}
```
```csharp
public sealed class RelationshipDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public record DiscoveredRelationship(string SourceAssetId, string TargetAssetId, string SourceColumn, string TargetColumn, double Confidence, string RelationshipType);;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterColumns(string assetId, IReadOnlyList<string> columnNames);
    public IReadOnlyList<DiscoveredRelationship> DiscoverRelationships(string assetId);
}
```
```csharp
public sealed class SchemaEvolutionTrackerStrategy : DataCatalogStrategyBase
{
}
    public record SchemaVersion(int Version, Dictionary<string, string> Columns, DateTimeOffset CapturedAt);;
    public record SchemaDiff(int FromVersion, int ToVersion, IReadOnlyList<string> AddedColumns, IReadOnlyList<string> RemovedColumns, IReadOnlyList<string> TypeChangedColumns);;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordSchema(string assetId, Dictionary<string, string> columns);
    public IReadOnlyList<SchemaVersion> GetHistory(string assetId);
    public SchemaDiff? ComputeDiff(string assetId, int fromVersion, int toVersion);
}
```
```csharp
public sealed class UsagePatternLearnerStrategy : DataCatalogStrategyBase
{
}
    public sealed class UsageProfile;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordAccess(string assetId, string actorId, string operationType);
    public void RecordCoAccess(string assetId1, string assetId2);
    public IReadOnlyList<string> GetPopularAssets(int topK);
    public IReadOnlyList<string> GetRecommendations(string assetId, int topK);
    public UsageProfile? GetUsageProfile(string assetId);
}
```
```csharp
public sealed class UsageProfile
{
}
    public long AccessCount;
    public long QueryCount;
    public DateTimeOffset LastAccessed;
    public DateTimeOffset FirstAccessed;
    public BoundedDictionary<string, int> AccessorCounts { get; };
    public BoundedDictionary<string, int> OperationCounts { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/Marketplace/MarketplaceStrategies.cs
```csharp
public sealed class DataMarketplaceStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataListingStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/SchemaRegistry/SchemaRegistryStrategies.cs
```csharp
public sealed class CentralizedSchemaRegistryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaVersionControlStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaCompatibilityCheckerStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AvroSchemaRegistryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ProtobufSchemaRegistryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class JsonSchemaRegistryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaInferenceEngineStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaMigrationGeneratorStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CrossPlatformSchemaTranslatorStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaGovernancePolicyStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/SearchDiscovery/SearchDiscoveryStrategies.cs
```csharp
public sealed class FullTextSearchStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SemanticSearchStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FacetedSearchStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ColumnSearchStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class TagBasedDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SimilarityBasedDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class RecommendationEngineStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LineageBasedDiscoveryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class NaturalLanguageQueryStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SavedSearchStrategy : DataCatalogStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataCatalogCategory Category;;
    public override DataCatalogCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
