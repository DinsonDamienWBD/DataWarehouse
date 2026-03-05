# Plugin: UltimateDataManagement
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataManagement

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/UltimateDataManagementPlugin.cs
```csharp
public sealed class UltimateDataManagementPlugin : DataManagementPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public DataManagementStrategyRegistry Registry;;
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool AutoOptimizationEnabled { get => _autoOptimizationEnabled; set => _autoOptimizationEnabled = value; }
    public UltimateDataManagementPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            // Main plugin capability
            new()
            {
                CapabilityId = "data-management",
                DisplayName = "Ultimate Data Management",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.DataManagement,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = SemanticTags
            }
        };
        // Add strategy-based capabilities
        foreach (var strategy in _registry.GetAll())
        {
            var tags = new List<string>
            {
                "data-management",
                strategy.Category.ToString().ToLowerInvariant()
            };
            tags.AddRange(strategy.Tags);
            capabilities.Add(new() { CapabilityId = $"data-management.{strategy.StrategyId}", DisplayName = strategy.DisplayName, Description = strategy.SemanticDescription, Category = SDK.Contracts.CapabilityCategory.DataManagement, SubCategory = strategy.Category.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Metadata = new Dictionary<string, object> { ["category"] = strategy.Category.ToString(), ["supportsAsync"] = strategy.Capabilities.SupportsAsync, ["supportsBatch"] = strategy.Capabilities.SupportsBatch, ["supportsDistributed"] = strategy.Capabilities.SupportsDistributed, ["supportsTransactions"] = strategy.Capabilities.SupportsTransactions, ["supportsTTL"] = strategy.Capabilities.SupportsTTL }, SemanticDescription = strategy.SemanticDescription });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override async Task OnMessageAsync(PluginMessage message);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class PolicyDto
{
}
    public string PolicyId { get; set; };
    public string Name { get; set; };
    public DateTime CreatedAt { get; set; }
}
```
```csharp
private sealed class DataManagementPolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public DateTime CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/DataManagementStrategyBase.cs
```csharp
public sealed record DataManagementCapabilities
{
}
    public required bool SupportsAsync { get; init; }
    public required bool SupportsBatch { get; init; }
    public required bool SupportsDistributed { get; init; }
    public required bool SupportsTransactions { get; init; }
    public required bool SupportsTTL { get; init; }
    public long MaxThroughput { get; init; };
    public double TypicalLatencyMs { get; init; };
}
```
```csharp
public sealed class DataManagementStatistics
{
}
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    public long TotalDeletes { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long TotalBytesRead { get; set; }
    public long TotalBytesWritten { get; set; }
    public double TotalTimeMs { get; set; }
    public long TotalFailures { get; set; }
    public double CacheHitRatio;;
    public double AverageLatencyMs
{
    get
    {
        var total = TotalReads + TotalWrites + TotalDeletes;
        return total > 0 ? TotalTimeMs / total : 0;
    }
}
}
```
```csharp
public interface IDataManagementStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    DataManagementCategory Category { get; }
    DataManagementCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
    DataManagementStatistics GetStatistics();;
    void ResetStatistics();;
    Task InitializeAsync(CancellationToken ct = default);;
    Task DisposeAsync();;
}
```
```csharp
public abstract class DataManagementStrategyBase : StrategyBase, IDataManagementStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract DataManagementCategory Category { get; }
    public abstract DataManagementCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected new bool IsInitialized;;
    public DataManagementStatistics GetStatistics();
    public void ResetStatistics();
    public new virtual async Task InitializeAsync(CancellationToken ct = default);
    public new virtual async Task DisposeAsync();
    protected virtual Task InitializeCoreAsync(CancellationToken ct);;
    protected virtual Task DisposeCoreAsync();;
    protected void RecordRead(long bytesRead, double timeMs, bool hit = false, bool miss = false);
    protected void RecordWrite(long bytesWritten, double timeMs);
    protected void RecordDelete(double timeMs);
    protected void RecordFailure();
    protected new void ThrowIfNotInitialized();
}
```
```csharp
public sealed class DataManagementStrategyRegistry
{
}
    public void Register(IDataManagementStrategy strategy);
    public bool Unregister(string strategyId);
    public IDataManagementStrategy? Get(string strategyId);
    public IReadOnlyCollection<IDataManagementStrategy> GetAll();
    public IReadOnlyCollection<IDataManagementStrategy> GetByCategory(DataManagementCategory category);
    public int Count;;
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/FanOut/IFanOutStrategy.cs
```csharp
public sealed class FanOutStrategyResult
{
}
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<WriteDestinationType, WriteDestinationResult> DestinationResults { get; init; };
    public TimeSpan Duration { get; init; }
    public int SuccessCount;;
    public int FailureCount;;
}
```
```csharp
public interface IFanOutStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    string SemanticDescription { get; }
    bool IsLocked { get; }
    FanOutSuccessCriteria SuccessCriteria { get; }
    TimeSpan NonRequiredTimeout { get; }
    bool AllowChildOverride { get; }
    IReadOnlySet<WriteDestinationType> EnabledDestinations { get; }
    IReadOnlySet<WriteDestinationType> RequiredDestinations { get; }
    Task<FanOutStrategyResult> ExecuteAsync(string objectId, IndexableContent content, IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations, CancellationToken ct = default);;
    StrategyValidationResult ValidateDestinations(IEnumerable<WriteDestinationType> availableDestinations);;
}
```
```csharp
public sealed class StrategyValidationResult
{
}
    public bool IsValid { get; init; }
    public IReadOnlyList<WriteDestinationType> MissingDestinations { get; init; };
    public IReadOnlyList<string> Warnings { get; init; };
    public IReadOnlyList<string> Errors { get; init; };
}
```
```csharp
public abstract class FanOutStrategyBase : StrategyBase, IFanOutStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract string SemanticDescription { get; }
    public abstract bool IsLocked { get; }
    public virtual FanOutSuccessCriteria SuccessCriteria { get; protected set; };
    public virtual TimeSpan NonRequiredTimeout { get; protected set; };
    public virtual bool AllowChildOverride { get; protected set; };
    public abstract IReadOnlySet<WriteDestinationType> EnabledDestinations { get; }
    public abstract IReadOnlySet<WriteDestinationType> RequiredDestinations { get; }
    public virtual async Task<FanOutStrategyResult> ExecuteAsync(string objectId, IndexableContent content, IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations, CancellationToken ct = default);
    public virtual StrategyValidationResult ValidateDestinations(IEnumerable<WriteDestinationType> availableDestinations);
    protected virtual bool EvaluateSuccess(Dictionary<WriteDestinationType, WriteDestinationResult> results);
    protected virtual string GetFailureMessage(Dictionary<WriteDestinationType, WriteDestinationResult> results);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/FanOut/WriteDestinations.cs
```csharp
public sealed class PrimaryStorageDestination : WriteDestinationPluginBase
{
}
    public PrimaryStorageDestination(IMessageBus? messageBus = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
public sealed class MetadataStorageDestination : WriteDestinationPluginBase
{
}
    public MetadataStorageDestination(IMessageBus? messageBus = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
public sealed class TextIndexDestination : WriteDestinationPluginBase
{
}
    public TextIndexDestination(IIndexingStrategy indexStrategy);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
public sealed class VectorStoreDestination : WriteDestinationPluginBase
{
}
    public VectorStoreDestination(IIndexingStrategy indexStrategy, IMessageBus? messageBus = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
public sealed class CacheDestination : WriteDestinationPluginBase
{
}
    public CacheDestination(ICachingStrategy cacheStrategy, TimeSpan? defaultTTL = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/FanOut/TamperProofDestinations.cs
```csharp
public sealed class BlockchainAnchorDestination : WriteDestinationPluginBase
{
}
    public BlockchainAnchorDestination(IMessageBus? messageBus = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
private sealed class BlockchainAnchorData
{
}
    public string ObjectId { get; init; };
    public string ContentHash { get; init; };
    public DateTimeOffset Timestamp { get; init; }
    public long Size { get; init; }
    public string ContentType { get; init; };
}
```
```csharp
public sealed class WormStorageDestination : WriteDestinationPluginBase
{
}
    public WormStorageDestination(IMessageBus? messageBus = null, TimeSpan? retentionPeriod = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
private sealed class WormRecord
{
}
    public string ObjectId { get; init; };
    public string Filename { get; init; };
    public string ContentType { get; init; };
    public long Size { get; init; }
    public DateTimeOffset FinalizedAt { get; init; }
    public DateTimeOffset RetentionUntil { get; init; }
    public string IntegrityHash { get; init; };
}
```
```csharp
private sealed class WormFinalizeResponse
{
}
    public bool Success { get; init; }
    public string? WormId { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed class AuditLogDestination : WriteDestinationPluginBase
{
}
    public AuditLogDestination(IMessageBus? messageBus = null);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override WriteDestinationType DestinationType;;
    public override bool IsRequired;;
    public override int Priority;;
    public override async Task<WriteDestinationResult> WriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
}
```
```csharp
private sealed class AuditLogEntry
{
}
    public string EntryId { get; init; };
    public string ObjectId { get; init; };
    public string Operation { get; init; };
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, object> Details { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/FanOut/DataWarehouseWriteFanOutOrchestrator.cs
```csharp
public sealed class DataWarehouseWriteFanOutOrchestrator : WriteFanOutOrchestratorPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public FanOutDeploymentMode DeploymentMode;;
    public bool IsLocked;;
    public IFanOutStrategy ActiveStrategy;;
    public string SemanticDescription;;
    public DataWarehouseWriteFanOutOrchestrator();
    public new void SetMessageBus(IMessageBus? messageBus);
    public bool ConfigureAsTamperProof();
    public bool ConfigureAsStandard(StandardFanOutConfiguration? configuration = null);
    public bool ConfigureWithCustomStrategy(IFanOutStrategy strategy);
    public bool ApplyConfigurationOverride(StandardFanOutConfiguration configuration);
    public void RegisterStrategy(IFanOutStrategy strategy);
    public IReadOnlyDictionary<string, IFanOutStrategy> GetStrategies();
    public void RegisterContentProcessor(IContentProcessor processor);
    public override async Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
    protected override async Task<Dictionary<ContentProcessingType, ContentProcessingResult>> ProcessContentAsync(Stream data, Manifest manifest, FanOutWriteOptions options, CancellationToken ct);
    protected override IndexableContent CreateIndexableContent(string objectId, Manifest manifest, Dictionary<ContentProcessingType, ContentProcessingResult> processingResults);
    public IReadOnlyDictionary<string, long> GetWriteStats();
    public async Task<FanOutStrategyResult> ExecuteStrategyWriteAsync(string objectId, IndexableContent content, CancellationToken ct = default);
    protected override Dictionary<string, object> GetMetadata();
}
```
```csharp
private sealed class ContentProcessingResponse
{
}
    public string? ExtractedText { get; init; }
    public float[]? Embeddings { get; init; }
    public string? Summary { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public ContentClassification? Classification { get; init; }
    public IReadOnlyList<ExtractedEntity>? Entities { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/FanOut/TamperProofFanOutStrategy.cs
```csharp
public sealed class TamperProofFanOutStrategy : FanOutStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override bool IsLocked;;
    public override bool AllowChildOverride;;
    public override IReadOnlySet<WriteDestinationType> EnabledDestinations;;
    public override IReadOnlySet<WriteDestinationType> RequiredDestinations;;
    public TamperProofFanOutStrategy(IMessageBus? messageBus = null);
    public override async Task<FanOutStrategyResult> ExecuteAsync(string objectId, IndexableContent content, IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations, CancellationToken ct = default);
}
```
```csharp
private sealed class BlockchainAnchorData
{
}
    public string AnchorId { get; init; };
    public string ObjectId { get; init; };
    public string ContentHash { get; init; };
    public string PreviousHash { get; init; };
    public DateTimeOffset Timestamp { get; init; }
    public long Nonce { get; init; }
    public long Size { get; init; }
    public string ContentType { get; init; };
    public string ToJson();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/FanOut/StandardFanOutStrategy.cs
```csharp
public sealed class StandardFanOutConfiguration
{
}
    public bool EnablePrimaryStorage { get; set; };
    public bool EnableMetadataStorage { get; set; };
    public bool EnableFullTextIndex { get; set; };
    public bool EnableVectorIndex { get; set; };
    public bool EnableCaching { get; set; };
    public FanOutSuccessCriteria SuccessCriteria { get; set; };
    public TimeSpan NonRequiredTimeout { get; set; };
    public bool AllowChildOverride { get; set; };
    public StandardFanOutConfiguration MergeWith(StandardFanOutConfiguration? childOverride);
}
```
```csharp
public sealed class StandardFanOutStrategy : FanOutStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override bool IsLocked;;
    public override IReadOnlySet<WriteDestinationType> EnabledDestinations;;
    public override IReadOnlySet<WriteDestinationType> RequiredDestinations;;
    public StandardFanOutConfiguration Configuration;;
    public StandardFanOutStrategy() : this(new StandardFanOutConfiguration(), null);
    public StandardFanOutStrategy(StandardFanOutConfiguration configuration, IMessageBus? messageBus = null);
    public StandardFanOutStrategy WithMessageBus(IMessageBus messageBus);
    public StandardFanOutStrategy WithOverride(StandardFanOutConfiguration? childOverride);
    public override async Task<FanOutStrategyResult> ExecuteAsync(string objectId, IndexableContent content, IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations, CancellationToken ct = default);
}
```
```csharp
public sealed class CustomFanOutStrategy : FanOutStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override string SemanticDescription;;
    public override bool IsLocked { get; }
    public override IReadOnlySet<WriteDestinationType> EnabledDestinations;;
    public override IReadOnlySet<WriteDestinationType> RequiredDestinations;;
    public CustomFanOutStrategy(string strategyId, string displayName, IEnumerable<WriteDestinationType> enabledDestinations, IEnumerable<WriteDestinationType> requiredDestinations, Dictionary<WriteDestinationType, string>? topicMappings = null, FanOutSuccessCriteria successCriteria = FanOutSuccessCriteria.AllRequired, bool isLocked = false, IMessageBus? messageBus = null);
    public override async Task<FanOutStrategyResult> ExecuteAsync(string objectId, IndexableContent content, IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations, CancellationToken ct = default);
    public sealed class Builder;
}
```
```csharp
public sealed class Builder
{
}
    public Builder WithId(string id);
    public Builder WithName(string name);
    public Builder WithMessageBus(IMessageBus bus);
    public Builder AddDestination(WriteDestinationType type, bool required = false, string? customTopic = null);
    public Builder WithSuccessCriteria(FanOutSuccessCriteria criteria);
    public Builder WithTimeout(TimeSpan timeout);
    public Builder Lock();
    public Builder AllowOverride();
    public CustomFanOutStrategy Build();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/InlineDeduplicationStrategy.cs
```csharp
public sealed class InlineDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public InlineDeduplicationStrategy() : this(65536);
    public InlineDeduplicationStrategy(int bufferSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public byte[]? GetData(string objectId);
    public bool RemoveData(string objectId);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/SemanticDeduplicationStrategy.cs
```csharp
public sealed class SemanticDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public SemanticDeduplicationStrategy() : this(0.95, 384, null);
    public SemanticDeduplicationStrategy(double similarityThreshold, int embeddingDimension, Func<byte[], CancellationToken, Task<float[]>>? embeddingProvider);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double SimilarityThreshold;;
    public int SemanticGroupCount;;
    public int EmbeddingCount;;
    public void SetEmbeddingProvider(Func<byte[], CancellationToken, Task<float[]>> provider);
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public IReadOnlyList<(string ObjectId, double Similarity)> FindSimilar(string objectId, double threshold = 0.9);
    public IReadOnlyList<string> GetGroupMembers(string groupId);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class EmbeddingEntry
{
}
    public required string ObjectId { get; init; }
    public required string Hash { get; init; }
    public required float[] Embedding { get; init; }
    public string? SemanticGroupId { get; set; }
    public required bool IsPrimary { get; init; }
    public required long Size { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed class SemanticGroup
{
}
    public required string GroupId { get; init; }
    public required string PrimaryObjectId { get; init; }
    public required List<string> Members { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/DeltaCompressionDeduplicationStrategy.cs
```csharp
public sealed class DeltaCompressionDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public DeltaCompressionDeduplicationStrategy() : this(0.5, 10, 8192);
    public DeltaCompressionDeduplicationStrategy(double deltaThreshold, int maxChainLength, int chunkSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double DeltaThreshold;;
    public int MaxChainLength;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public async Task<byte[]?> GetVersionDataAsync(string versionId, CancellationToken ct = default);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class ChunkRef
{
}
    public required string Hash { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
}
```
```csharp
private sealed class VersionEntry
{
}
    public required string VersionId { get; init; }
    public required bool IsBase { get; init; }
    public string? BaseVersionId { get; init; }
    public required List<ChunkRef> ChunkRefs { get; init; }
    public byte[]? DeltaData { get; init; }
    public required long TotalSize { get; init; }
    public required string Hash { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed class VersionChain
{
}
    public required string ChainId { get; init; }
    public required List<string> Versions { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/GlobalDeduplicationStrategy.cs
```csharp
public sealed class GlobalDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public GlobalDeduplicationStrategy() : this(3, 100);
    public GlobalDeduplicationStrategy(int replicationFactor, int virtualNodes);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void AddVolume(string volumeId, string volumePath, long capacityBytes);
    public void AddNode(string nodeId, string endpoint);
    public void RemoveNode(string nodeId);
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public GlobalDeduplicationStats GetGlobalStats();
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class VolumeInfo
{
}
    public required string VolumeId { get; init; }
    public required string VolumePath { get; init; }
    public required long CapacityBytes { get; init; }
    public long UsedBytes;
    public required DateTime AddedAt { get; init; }
}
```
```csharp
private sealed class NodeInfo
{
}
    public required string NodeId { get; init; }
    public required string Endpoint { get; init; }
    public bool IsHealthy { get; set; }
    public required DateTime AddedAt { get; init; }
    public DateTime LastHeartbeat { get; set; };
}
```
```csharp
private sealed class GlobalHashEntry
{
}
    public required string ObjectId { get; init; }
    public required string PrimaryLocation { get; init; }
    public required List<string> ReplicaLocations { get; init; }
    public required long Size { get; init; }
    public int ReferenceCount;
    public required HashSet<string> AccessingNodes { get; init; }
    public DateTime LastAccessedAt { get; set; };
}
```
```csharp
public sealed class GlobalDeduplicationStats
{
}
    public int TotalNodes { get; init; }
    public int HealthyNodes { get; init; }
    public int TotalVolumes { get; init; }
    public int TotalUniqueObjects { get; init; }
    public long TotalDataBytes { get; init; }
    public long TotalReferences { get; init; }
    public int ReplicationFactor { get; init; }
    public long SpaceSavings;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/SubFileDeduplicationStrategy.cs
```csharp
public sealed class SubFileDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public SubFileDeduplicationStrategy() : this(8192, true);
    public SubFileDeduplicationStrategy(int chunkSize, bool autoGc);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public long TotalStoredBytes;;
    public long TotalLogicalBytes;;
    public double DeduplicationRatio;;
    public int UniqueChunkCount;;
    public int FileCount;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public async Task<Stream?> ReconstructAsync(string objectId, CancellationToken ct = default);
    public bool RemoveFile(string objectId);
    public async Task<int> CollectGarbageAsync(CancellationToken ct = default);
    public ChunkStatistics GetChunkStatistics();
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class ChunkData
{
}
    public required string Hash { get; init; }
    public required byte[] Data { get; init; }
    public required int Size { get; init; }
    public int ReferenceCount;
    public bool MarkedForDeletion;
    public required DateTime CreatedAt { get; init; }
}
```
```csharp
private sealed class ChunkRef
{
}
    public required string Hash { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
}
```
```csharp
private sealed class FileManifest
{
}
    public required string ObjectId { get; init; }
    public required List<ChunkRef> Chunks { get; init; }
    public required long TotalSize { get; init; }
    public required int ChunkSize { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed class ChunkStatistics
{
}
    public int TotalChunks { get; init; }
    public long TotalSize { get; init; }
    public double AverageSize { get; init; }
    public int MinSize { get; init; }
    public int MaxSize { get; init; }
    public long TotalReferences { get; init; }
    public double AverageReferences { get; init; }
    public int OrphanedChunks { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/FixedBlockDeduplicationStrategy.cs
```csharp
public sealed class FixedBlockDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public FixedBlockDeduplicationStrategy() : this(8192);
    public FixedBlockDeduplicationStrategy(int blockSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int BlockSize;;
    public int UniqueBlockCount;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public async Task<Stream?> ReconstructAsync(string objectId, CancellationToken ct = default);
    public bool RemoveObject(string objectId);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class ObjectBlockMap
{
}
    public required string ObjectId { get; init; }
    public required List<string> BlockHashes { get; init; }
    public required int BlockSize { get; init; }
    public required long TotalSize { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/FileLevelDeduplicationStrategy.cs
```csharp
public sealed class FileLevelDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public FileLevelDeduplicationStrategy() : this(true);
    public FileLevelDeduplicationStrategy(bool storeInMemory);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int UniqueFileCount;;
    public int TotalObjectCount;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public byte[]? GetByHash(byte[] hash);
    public byte[]? GetByObjectId(string objectId);
    public byte[]? GetHashForObject(string objectId);
    public bool RemoveObject(string objectId);
    public bool ObjectExists(string objectId);
    public int GetReferenceCount(byte[] hash);
    public IReadOnlyList<string> GetObjectsForHash(byte[] hash);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/PostProcessDeduplicationStrategy.cs
```csharp
public sealed class PostProcessDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public PostProcessDeduplicationStrategy() : this(100, TimeSpan.FromSeconds(30));
    public PostProcessDeduplicationStrategy(int batchSize, TimeSpan batchInterval);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int PendingCount;;
    public bool IsProcessing;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class PendingData
{
}
    public required string ObjectId { get; init; }
    public required byte[] Hash { get; init; }
    public required string HashString { get; init; }
    public required long Size { get; init; }
    public required DateTime QueuedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/DeduplicationStrategyBase.cs
```csharp
public sealed class DeduplicationResult
{
}
    public required bool Success { get; init; }
    public bool IsDuplicate { get; init; }
    public byte[]? Hash { get; init; }
    public string? ReferenceId { get; init; }
    public long OriginalSize { get; init; }
    public long StoredSize { get; init; }
    public int ChunksProcessed { get; init; }
    public int DuplicateChunks { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public static DeduplicationResult Unique(byte[] hash, long originalSize, long storedSize, int chunks, int duplicateChunks, TimeSpan duration);;
    public static DeduplicationResult Duplicate(byte[] hash, string referenceId, long originalSize, TimeSpan duration);;
    public static DeduplicationResult Failed(string error);;
}
```
```csharp
public sealed class DeduplicationContext
{
}
    public string ObjectId { get; init; };
    public string? ContentType { get; init; }
    public string? TenantId { get; init; }
    public bool GlobalDedup { get; init; };
    public int MinChunkSize { get; init; };
    public int MaxChunkSize { get; init; };
    public int AverageChunkSize { get; init; };
    public int FixedBlockSize { get; init; };
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class DeduplicationStats
{
}
    public long TotalBytesProcessed { get; set; }
    public long BytesSaved { get; set; }
    public long UniqueChunks { get; set; }
    public long DuplicateChunks { get; set; }
    public long UniqueObjects { get; set; }
    public long DuplicateObjects { get; set; }
    public double DeduplicationRatio;;
    public double SpaceSavingsPercent;;
}
```
```csharp
public sealed class ChunkReference
{
}
    public required byte[] Hash { get; init; }
    public required int Size { get; init; }
    public required long Offset { get; init; }
    public int ReferenceCount { get; set; };
    public string? StorageId { get; init; }
}
```
```csharp
public interface IDeduplicationStrategy : IDataManagementStrategy
{
}
    Task<DeduplicationResult> DeduplicateAsync(Stream data, DeduplicationContext context, CancellationToken ct);;
    Task<bool> IsDuplicateAsync(byte[] hash, CancellationToken ct);;
    Task<DeduplicationStats> GetStatsAsync(CancellationToken ct);;
}
```
```csharp
public abstract class DeduplicationStrategyBase : DataManagementStrategyBase, IDeduplicationStrategy
{
}
    protected readonly BoundedDictionary<string, HashEntry> HashIndex = new BoundedDictionary<string, HashEntry>(1000);
    protected readonly BoundedDictionary<string, ChunkEntry> ChunkIndex = new BoundedDictionary<string, ChunkEntry>(1000);
    public override DataManagementCategory Category;;
    public async Task<DeduplicationResult> DeduplicateAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public async Task<bool> IsDuplicateAsync(byte[] hash, CancellationToken ct);
    public Task<DeduplicationStats> GetStatsAsync(CancellationToken ct);
    protected abstract Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);;
    protected virtual Task<bool> IsDuplicateCoreAsync(byte[] hash, CancellationToken ct);
    protected void UpdateStats(DeduplicationResult result);
    protected static byte[] ComputeHash(byte[] data);
    protected static async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct);
    protected static string HashToString(byte[] hash);;
    protected static byte[] StringToHash(string hashString);;
    protected sealed class HashEntry;
    protected sealed class ChunkEntry;
}
```
```csharp
protected sealed class HashEntry
{
}
    public required string ObjectId { get; init; }
    public required long Size { get; init; }
    public int ReferenceCount = 1;
    public DateTime CreatedAt { get; init; };
    public DateTime LastAccessedAt { get; set; };
    public void IncrementRef();;
    public int DecrementRef();;
}
```
```csharp
protected sealed class ChunkEntry
{
}
    public required string StorageId { get; init; }
    public required int Size { get; init; }
    public int ReferenceCount = 1;
    public DateTime CreatedAt { get; init; };
    public void IncrementRef();;
    public int DecrementRef();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/VariableBlockDeduplicationStrategy.cs
```csharp
public sealed class VariableBlockDeduplicationStrategy : DeduplicationStrategyBase
{
}
    public VariableBlockDeduplicationStrategy() : this(2048, 65536, 8192);
    public VariableBlockDeduplicationStrategy(int minChunkSize, int maxChunkSize, int avgChunkSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int MinChunkSize;;
    public int MaxChunkSize;;
    public int AverageChunkSize;;
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    public async Task<Stream?> ReconstructAsync(string objectId, CancellationToken ct = default);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed record DataChunk
{
}
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
}
```
```csharp
private sealed class ChunkInfo
{
}
    public required string Hash { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
}
```
```csharp
private sealed class ObjectChunkMap
{
}
    public required string ObjectId { get; init; }
    public required List<ChunkInfo> Chunks { get; init; }
    public required long TotalSize { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Deduplication/ContentAwareChunkingStrategy.cs
```csharp
public sealed class ContentAwareChunkingStrategy : DeduplicationStrategyBase
{
}
    public ContentAwareChunkingStrategy();
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterChunker(string contentType, IContentChunker chunker);
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(Stream data, DeduplicationContext context, CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class ContentChunkInfo
{
}
    public required string Hash { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public required string ChunkType { get; init; }
}
```
```csharp
private sealed class ObjectContentMap
{
}
    public required string ObjectId { get; init; }
    public required string ContentType { get; init; }
    public required List<ContentChunkInfo> Chunks { get; init; }
    public required long TotalSize { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```
```csharp
public interface IContentChunker
{
}
    List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);;
}
```
```csharp
public sealed class ContentChunk
{
}
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public required string ChunkType { get; init; }
}
```
```csharp
internal sealed class DefaultContentChunker : IContentChunker
{
}
    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}
```
```csharp
internal sealed class TextContentChunker : IContentChunker
{
}
    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}
```
```csharp
internal sealed class StructuredDataChunker : IContentChunker
{
}
    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}
```
```csharp
internal sealed class TabularDataChunker : IContentChunker
{
}
    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}
```
```csharp
internal sealed class BinaryFormatChunker : IContentChunker
{
}
    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}
```
```csharp
internal sealed class ArchiveChunker : IContentChunker
{
}
    public List<ContentChunk> Chunk(byte[] data, DeduplicationContext context);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/WriteBehindCacheStrategy.cs
```csharp
internal sealed class WriteBehindOperation
{
}
    public required string Key { get; init; }
    public required WriteBehindOperationType Type { get; init; }
    public byte[]? Value { get; init; }
    public DateTime QueuedAt { get; init; };
    public int RetryCount { get; set; }
}
```
```csharp
public sealed class WriteBehindConfig
{
}
    public long CacheMaxSize { get; init; };
    public TimeSpan DefaultTTL { get; init; };
    public int MaxQueueSize { get; init; };
    public int FlushBatchSize { get; init; };
    public TimeSpan FlushInterval { get; init; };
    public int MaxRetries { get; init; };
    public TimeSpan RetryDelay { get; init; };
    public int WorkerCount { get; init; };
}
```
```csharp
public sealed class WriteBehindCacheStrategy : CachingStrategyBase
{
}
    public WriteBehindCacheStrategy() : this(new InMemoryBackingStore(), new WriteBehindConfig());
    public WriteBehindCacheStrategy(ICacheBackingStore backingStore, WriteBehindConfig? config = null);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public long QueuedOperations;;
    public long FailedOperations;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override async Task ClearCoreAsync(CancellationToken ct);
    public async Task FlushAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/CachingStrategyBase.cs
```csharp
public sealed class CacheOptions
{
}
    public TimeSpan? TTL { get; init; }
    public TimeSpan? SlidingExpiration { get; init; }
    public CachePriority Priority { get; init; };
    public string[]? Tags { get; init; }
}
```
```csharp
public sealed class CacheResult<T>
{
}
    public bool Found { get; init; }
    public T? Value { get; init; }
    public TimeSpan? ExpiresIn { get; init; }
    public static CacheResult<T> Hit(T value, TimeSpan? expiresIn = null);;
    public static CacheResult<T> Miss();;
}
```
```csharp
public interface ICachingStrategy : IDataManagementStrategy
{
}
    Task<CacheResult<byte[]>> GetAsync(string key, CancellationToken ct = default);;
    Task SetAsync(string key, byte[] value, CacheOptions? options = null, CancellationToken ct = default);;
    Task<bool> RemoveAsync(string key, CancellationToken ct = default);;
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);;
    Task<byte[]> GetOrSetAsync(string key, Func<CancellationToken, Task<byte[]>> factory, CacheOptions? options = null, CancellationToken ct = default);;
    Task InvalidateByTagsAsync(string[] tags, CancellationToken ct = default);;
    Task ClearAsync(CancellationToken ct = default);;
    long GetCurrentSize();;
    long GetEntryCount();;
}
```
```csharp
public abstract class CachingStrategyBase : DataManagementStrategyBase, ICachingStrategy
{
}
    public override DataManagementCategory Category;;
    public async Task<CacheResult<byte[]>> GetAsync(string key, CancellationToken ct = default);
    public async Task SetAsync(string key, byte[] value, CacheOptions? options = null, CancellationToken ct = default);
    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default);
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    public async Task<byte[]> GetOrSetAsync(string key, Func<CancellationToken, Task<byte[]>> factory, CacheOptions? options = null, CancellationToken ct = default);
    public async Task InvalidateByTagsAsync(string[] tags, CancellationToken ct = default);
    public async Task ClearAsync(CancellationToken ct = default);
    public abstract long GetCurrentSize();;
    public abstract long GetEntryCount();;
    protected abstract Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);;
    protected abstract Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);;
    protected abstract Task<bool> RemoveCoreAsync(string key, CancellationToken ct);;
    protected abstract Task<bool> ExistsCoreAsync(string key, CancellationToken ct);;
    protected virtual Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);;
    protected abstract Task ClearCoreAsync(CancellationToken ct);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/DistributedCacheStrategy.cs
```csharp
public sealed class DistributedCacheConfig
{
}
    public DistributedCacheBackend Backend { get; init; };
    public required string ConnectionString { get; init; }
    public CacheSerializationFormat SerializationFormat { get; init; };
    public int PoolSize { get; init; };
    public TimeSpan ConnectionTimeout { get; init; };
    public TimeSpan OperationTimeout { get; init; };
    public string? KeyPrefix { get; init; }
    public bool ClusterMode { get; init; }
    public string[]? ClusterEndpoints { get; init; }
    public bool UseSsl { get; init; }
    public string? Password { get; init; }
}
```
```csharp
internal sealed class CacheConnection : IDisposable
{
}
    public CacheConnection(string host, int port, TimeSpan timeout);
    public bool IsConnected;;
    public async Task<byte[]?> SendCommandAsync(byte[] command, CancellationToken ct);
    public void Dispose();
}
```
```csharp
internal sealed class ConnectionPool : IDisposable
{
}
    public ConnectionPool(string host, int port, int maxSize, TimeSpan timeout);
    public async Task<CacheConnection?> GetConnectionAsync(CancellationToken ct);
    public void ReturnConnection(CacheConnection connection);
    public void Dispose();
}
```
```csharp
public sealed class DistributedCacheStrategy : CachingStrategyBase
{
}
    public DistributedCacheStrategy(DistributedCacheConfig config);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override async Task ClearCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/InMemoryCacheStrategy.cs
```csharp
public sealed class InMemoryCacheStrategy : CachingStrategyBase
{
}
    public InMemoryCacheStrategy() : this(256 * 1024 * 1024);
    public InMemoryCacheStrategy(long maxSizeBytes);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    protected override Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override Task ClearCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class CacheEntry
{
}
    public byte[] Value { get; }
    public DateTime? AbsoluteExpiration { get; set; }
    public TimeSpan? SlidingExpiration { get; }
    public DateTime LastAccess { get; set; }
    public CachePriority Priority { get; }
    public string[]? Tags { get; }
    public CacheEntry(byte[] value, CacheOptions options);
    public bool IsExpired;;
    public TimeSpan? GetTimeToExpiration();
    public void Touch();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/ReadThroughCacheStrategy.cs
```csharp
public sealed class ReadThroughCacheConfig
{
}
    public TimeSpan DefaultTTL { get; init; };
    public long MaxCacheSize { get; init; };
    public bool EnableWriteThrough { get; init; }
    public bool RefreshOnAccess { get; init; };
    public double BackgroundRefreshThreshold { get; init; };
    public int MaxConcurrentLoads { get; init; };
    public TimeSpan LoaderTimeout { get; init; };
}
```
```csharp
internal sealed class ReadThroughEntry
{
}
    public byte[]? Value { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime LastAccess { get; set; }
    public DateTime LoadedAt { get; set; }
    public bool IsLoading { get; set; }
    public TaskCompletionSource<byte[]?>? LoadingTask { get; set; }
    public CachePriority Priority { get; set; }
    public string[]? Tags { get; set; }
}
```
```csharp
public sealed class ReadThroughCacheStrategy : CachingStrategyBase
{
}
    public ReadThroughCacheStrategy() : this(new ReadThroughCacheConfig());
    public ReadThroughCacheStrategy(ReadThroughCacheConfig config);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    public void SetDataLoader(DataLoaderDelegate loader);
    public void SetWriteBackHandler(WriteBackDelegate writeBack);
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override Task ClearCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/PredictiveCacheStrategy.cs
```csharp
public sealed class AccessPrediction
{
}
    public required string Key { get; init; }
    public double Probability { get; init; }
    public TimeSpan? TimeUntilAccess { get; init; }
    public double Confidence { get; init; }
}
```
```csharp
internal sealed class AccessPatternRecord
{
}
    public required string Key { get; init; }
    public DateTime AccessTime { get; init; }
    public string? PreviousKey { get; init; }
    public string? UserId { get; init; }
}
```
```csharp
public sealed class PredictiveCacheStrategy : CachingStrategyBase
{
}
    public PredictiveCacheStrategy() : this(256 * 1024 * 1024, 10000, 0.3);
    public PredictiveCacheStrategy(long maxSizeBytes, int maxHistorySize, double prefetchThreshold);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    public void SetMessageBus(IMessageBus messageBus);
    public void SetPrefetchLoader(DataLoaderDelegate loader);
    public IReadOnlyList<AccessPrediction> GetAccessPredictions(string currentKey, int maxPredictions = 5);
    protected override Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override Task ClearCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class CacheEntry
{
}
    public byte[] Value { get; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime LastAccess { get; set; }
    public CachePriority Priority { get; }
    public string[]? Tags { get; }
    public int AccessCount { get; set; }
    public bool IsPrefetched { get; set; }
    public CacheEntry(byte[] value, CacheOptions options);
    public bool IsExpired;;
    public TimeSpan? GetTimeToExpiration();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/HybridCacheStrategy.cs
```csharp
public sealed class HybridCacheConfig
{
}
    public long L1MaxSize { get; init; };
    public long L2MaxSize { get; init; };
    public int L1ThresholdSize { get; init; };
    public int PromotionThreshold { get; init; };
    public TimeSpan L1DefaultTTL { get; init; };
    public TimeSpan L2DefaultTTL { get; init; };
}
```
```csharp
public sealed class HybridCacheStrategy : CachingStrategyBase
{
}
    public HybridCacheStrategy() : this(new HybridCacheConfig());
    public HybridCacheStrategy(HybridCacheConfig config);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    public long GetL1Size();;
    public long GetL2Size();;
    public long GetL1EntryCount();;
    public long GetL2EntryCount();;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override async Task ClearCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/GeoDistributedCacheStrategy.cs
```csharp
public sealed class CacheRegion
{
}
    public required string RegionId { get; init; }
    public required string DisplayName { get; init; }
    public required string Endpoint { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public bool IsHealthy { get; set; };
    public DateTime LastHealthCheck { get; set; }
    public double AverageLatencyMs { get; set; }
    public bool IsLocal { get; init; }
}
```
```csharp
public sealed class InvalidationEvent
{
}
    public required string EventId { get; init; }
    public required string[] Keys { get; init; }
    public string[]? Tags { get; init; }
    public required string OriginRegion { get; init; }
    public DateTime Timestamp { get; init; };
}
```
```csharp
public sealed class GeoDistributedCacheConfig
{
}
    public required string LocalRegionId { get; init; }
    public long MaxLocalCacheSize { get; init; };
    public TimeSpan DefaultTTL { get; init; };
    public bool ReplicateWrites { get; init; };
    public bool PreferLocal { get; init; };
    public TimeSpan HealthCheckInterval { get; init; };
    public TimeSpan InvalidationDelay { get; init; };
}
```
```csharp
public sealed class GeoDistributedCacheStrategy : CachingStrategyBase
{
}
    public GeoDistributedCacheStrategy(GeoDistributedCacheConfig config);
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    public void AddRegion(CacheRegion region);
    public IReadOnlyList<CacheRegion> GetRegions();;
    public IReadOnlyList<CacheRegion> GetHealthyRegions();;
    public CacheRegion? GetNearestRegion(double latitude, double longitude);
    public void BroadcastInvalidation(string[] keys, string[]? tags = null);
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override Task ClearCoreAsync(CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class CacheEntry
{
}
    public byte[] Value { get; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime LastAccess { get => new DateTime(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc); set => Interlocked.Exchange(ref _lastAccessTicks, value.Ticks); }
    public CachePriority Priority { get; }
    public string[]? Tags { get; }
    public string OriginRegion { get; init; }
    public long Version { get; set; }
    public CacheEntry(byte[] value, CacheOptions options, string originRegion);
    public bool IsExpired;;
    public TimeSpan? GetTimeToExpiration();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Caching/WriteThruCacheStrategy.cs
```csharp
public interface ICacheBackingStore
{
}
    Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);;
    Task WriteAsync(string key, byte[] value, CancellationToken ct = default);;
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);;
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);;
}
```
```csharp
public sealed class InMemoryBackingStore : ICacheBackingStore
{
}
    public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);
    public Task WriteAsync(string key, byte[] value, CancellationToken ct = default);
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}
```
```csharp
public sealed class WriteThruCacheStrategy : CachingStrategyBase
{
}
    public WriteThruCacheStrategy() : this(new InMemoryBackingStore());
    public WriteThruCacheStrategy(ICacheBackingStore backingStore, long cacheMaxSize = 128 * 1024 * 1024, TimeSpan? defaultTTL = null);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetCurrentSize();;
    public override long GetEntryCount();;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct);
    protected override async Task ClearCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/ManualTieringStrategy.cs
```csharp
public sealed class ManualTieringStrategy : TieringStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<bool> AssignTierAsync(string objectId, StorageTier targetTier, string? assignedBy = null, string? reason = null, int priority = 0, CancellationToken ct = default);
    public Task<int> BulkAssignAsync(IEnumerable<(string ObjectId, StorageTier TargetTier, string? Reason)> assignments, string? assignedBy = null, CancellationToken ct = default);
    public Task<bool> RemoveAssignmentAsync(string objectId, string? removedBy = null, CancellationToken ct = default);
    public Task<bool> LockTierAsync(string objectId, StorageTier lockedTier, TimeSpan duration, string? lockedBy = null, string? reason = null, CancellationToken ct = default);
    public Task<bool> UnlockTierAsync(string objectId, string? unlockedBy = null, CancellationToken ct = default);
    public bool IsTierLocked(string objectId);
    public StorageTier? GetManualAssignment(string objectId);
    public IReadOnlyList<(DateTime Timestamp, string Operation, string ObjectId, string? Details)> GetAuditLog(int limit = 100);
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
private sealed class TierAssignment
{
}
    public required string ObjectId { get; init; }
    public StorageTier TargetTier { get; init; }
    public string? AssignedBy { get; init; }
    public DateTime AssignedAt { get; init; }
    public string? Reason { get; init; }
    public int Priority { get; init; }
}
```
```csharp
private sealed class TierLock
{
}
    public required string ObjectId { get; init; }
    public StorageTier LockedTier { get; init; }
    public DateTime LockedUntil { get; init; }
    public string? LockedBy { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
private sealed class TierAuditEntry
{
}
    public DateTime Timestamp { get; init; }
    public required string Operation { get; init; }
    public required string ObjectId { get; init; }
    public StorageTier? FromTier { get; init; }
    public StorageTier? ToTier { get; init; }
    public string? PerformedBy { get; init; }
    public string? Reason { get; init; }
    public bool Success { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/PerformanceTieringStrategy.cs
```csharp
public sealed class PerformanceTieringStrategy : TieringStrategyBase
{
}
    public sealed class PerformanceRequirements;
    public sealed class PerformanceConfig;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PerformanceConfig Config { get => _config; set => _config = value ?? PerformanceConfig.Default; }
    public void SetPerformanceRequirements(PerformanceRequirements requirements);
    public bool RemovePerformanceRequirements(string classification);
    public IReadOnlyList<PerformanceRequirements> GetPerformanceRequirements();
    public void RecordPerformance(string objectId, double latencyMs, long bytesTransferred = 0);
    public void ReportSlaViolation(string objectId);
    public (double AvgLatencyMs, double P99LatencyMs, double ThroughputMbps, int Violations)? GetMetrics(string objectId);
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
public sealed class PerformanceRequirements
{
}
    public required string Classification { get; init; }
    public double MaxLatencyMs { get; init; }
    public double MinThroughputMbps { get; init; }
    public double TargetAvailability { get; init; };
    public int Priority { get; init; }
    public bool AllowDemotion { get; init; };
}
```
```csharp
private sealed class PerformanceMetrics
{
}
    public required string ObjectId { get; init; }
    public double AverageLatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public double ThroughputMbps { get; set; }
    public int AccessCount { get; set; }
    public int SlaViolations { get; set; }
    public DateTime LastUpdated { get; set; }
    public List<double> RecentLatencies { get; };
}
```
```csharp
public sealed class PerformanceConfig
{
}
    public Dictionary<StorageTier, double> TierLatencyMs { get; init; };
    public Dictionary<StorageTier, double> TierThroughputMbps { get; init; };
    public int MetricsWindowSize { get; init; };
    public int PromotionAccessThreshold { get; init; };
    public int SlaViolationThreshold { get; init; };
    public static PerformanceConfig Default;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/HybridTieringStrategy.cs
```csharp
public sealed class HybridTieringStrategy : TieringStrategyBase
{
}
    public enum ConflictResolutionMode;
    public sealed class AggregatedRecommendation;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ConflictResolutionMode ConflictMode { get => _conflictMode; set => _conflictMode = value; }
    public void RegisterStrategy(ITieringStrategy strategy, double weight = 1.0, int priority = 0);
    public bool UnregisterStrategy(string strategyId);
    public IReadOnlyList<(string StrategyId, double Weight, int Priority, bool IsEnabled)> GetRegisteredStrategies();
    public bool SetStrategyWeight(string strategyId, double weight);
    public bool SetStrategyPriority(string strategyId, int priority);
    public bool SetStrategyEnabled(string strategyId, bool enabled);
    public bool ExcludeClassificationFromStrategy(string strategyId, string classification);
    public async Task<AggregatedRecommendation> EvaluateAllAsync(DataObject data, CancellationToken ct = default);
    protected override async Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
private sealed class StrategyRegistration
{
}
    public required ITieringStrategy Strategy { get; init; }
    public double Weight { get; set; };
    public int Priority { get; set; }
    public bool IsEnabled { get; set; };
    public HashSet<string> ExcludedClassifications { get; };
}
```
```csharp
public sealed class AggregatedRecommendation
{
}
    public StorageTier RecommendedTier { get; init; }
    public double AggregatedConfidence { get; init; }
    public IReadOnlyDictionary<string, TierRecommendation> StrategyRecommendations { get; init; };
    public int AgreementCount { get; init; }
    public int TotalStrategies { get; init; }
    public double AgreementRatio;;
    public ConflictResolutionMode ResolutionMode { get; init; }
    public string Reasoning { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/SizeTieringStrategy.cs
```csharp
public sealed class SizeTieringStrategy : TieringStrategyBase
{
}
    public sealed class SizeTierConfig;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SizeTierConfig Config { get => _config; set => _config = value ?? SizeTierConfig.Default; }
    public void ExcludeContentType(string contentType);
    public void IncludeContentType(string contentType);
    public IReadOnlyList<string> GetExcludedContentTypes();
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
public sealed class SizeTierConfig
{
}
    public long HotMaxSize { get; init; };
    public long WarmMaxSize { get; init; };
    public long ColdMaxSize { get; init; };
    public long ArchiveMaxSize { get; init; };
    public int AccessOverrideThreshold { get; init; };
    public bool ConsiderAccessPatterns { get; init; };
    public static SizeTierConfig Default;;
    public static SizeTierConfig Aggressive;;
    public static SizeTierConfig Conservative;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/BlockLevelTieringStrategy.cs
```csharp
public sealed class BlockLevelTieringStrategy : TieringStrategyBase
{
#endregion
}
    public BlockLevelTieringStrategy();
    public sealed class BlockHeatmap;
    public sealed class BlockRange;
    public sealed class DatabaseFileInfo;
    public enum DatabaseFileType;
    public sealed class BlockTierConfig;
    public sealed class BlockInfo;
    public sealed class BlockTierRecommendation;
    public sealed class BlockSplitResult;
    public sealed class BlockReassemblyResult;
    public sealed class CostOptimizationAnalysis;
    public sealed class BlockMoveRecommendation;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public BlockTierConfig Config { get => _config; set => _config = value ?? BlockTierConfig.Default; }
    public void InitializeBlockMap(string objectId, long totalSize, StorageTier initialTier = StorageTier.Hot, string? filePath = null);
    public void RecordBlockAccess(string objectId, long offset, long length, double? latencyMs = null);
    public BlockHeatmap GenerateHeatmap(string objectId);
    public BlockHeatmap? GetHeatmap(string objectId);
    public IEnumerable<(int BlockIndex, double Heat)> QueryBlocksByHeat(string objectId, double minHeat, double maxHeat);
    public async Task<BlockSplitResult> SplitIntoBlocksAsync(string objectId, Stream sourceStream, StorageTier initialTier = StorageTier.Hot, CancellationToken ct = default);
    public async Task<BlockSplitResult> SplitWithContentDefinedChunkingAsync(string objectId, Stream sourceStream, StorageTier initialTier = StorageTier.Hot, CancellationToken ct = default);
    public async Task<BlockReassemblyResult> ReassembleBlocksAsync(string objectId, long offset, long length, Func<int, StorageTier, CancellationToken, Task<byte[]>> blockFetcher, CancellationToken ct = default);
    public IEnumerable<(int BlockIndex, StorageTier Tier, string Location)> GetBlockLocations(string objectId, long offset, long length);
    public Task<bool> MoveBlockAsync(string objectId, int blockIndex, StorageTier targetTier, CancellationToken ct = default);
    public async Task<Dictionary<int, bool>> MoveBlocksAsync(string objectId, IEnumerable<(int BlockIndex, StorageTier TargetTier)> moves, CancellationToken ct = default);
    public async Task<int> ApplyRecommendationsAsync(string objectId, CancellationToken ct = default);
    public int[] GetPrefetchPredictions(string objectId, int currentBlock);
    public async Task<int> PrestageBlocksAsync(string objectId, int[] blockIndexes, CancellationToken ct = default);
    public IReadOnlyList<BlockInfo> GetBlockInfo(string objectId);
    public IEnumerable<(string ObjectId, int BlockIndex)> GetBlocksOnTier(StorageTier tier);
    public Dictionary<StorageTier, (long BlockCount, long TotalBytes)> GetTierDistribution();
    public CostOptimizationAnalysis AnalyzeCostOptimization(string objectId);
    public Dictionary<int, StorageTier> GetOptimalPlacement(string objectId, decimal maxMonthlyCostDollars, double minPerformanceScore);
    public static bool DetectDatabaseFile(string? filePath);
    public static DatabaseFileType DetectDatabaseType(string? filePath);
    public DatabaseFileInfo? GetDatabaseFileInfo(string objectId);
    public BlockTierRecommendation GetDatabaseAwareRecommendations(string objectId);
    public async Task<int> RebalanceBlocksAsync(string objectId, int[] blockIndexes, CancellationToken ct = default);
    public async Task<int> FullRebalanceAsync(string objectId, CancellationToken ct = default);
    public int GetPendingRebalanceCount();;
    public BlockTierRecommendation GetBlockRecommendations(string objectId);
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
private sealed class ObjectBlockMap
{
}
    public required string ObjectId { get; init; }
    public string? FilePath { get; init; }
    public long TotalSize { get; init; }
    public int BlockSize { get; init; }
    public int BlockCount { get; init; }
    public StorageTier[] BlockTiers { get; init; };
    public string[] BlockLocations { get; init; };
    public byte[][] BlockHashes { get; init; };
    public DateTime LastUpdated { get; set; }
    public bool IsDatabaseFile { get; set; }
    public DatabaseFileType DatabaseType { get; set; }
}
```
```csharp
private sealed class BlockAccessStats
{
}
    public required string ObjectId { get; init; }
    public int[] BlockAccessCounts { get; set; };
    public DateTime[] LastAccessTimes { get; set; };
    public int[] AccessesLast24Hours { get; set; };
    public int[] AccessesLast7Days { get; set; };
    public long[] TotalBytesRead { get; set; };
    public double[] AverageLatencyMs { get; set; };
    public int[][] AccessSequences { get; set; };
    public DateTime LastUpdated { get; set; }
}
```
```csharp
public sealed class BlockHeatmap
{
}
    public required string ObjectId { get; init; }
    public double[] HeatValues { get; set; };
    public StorageTier[] RecommendedTiers { get; set; };
    public List<BlockRange> HotRegions { get; set; };
    public List<BlockRange> ColdRegions { get; set; };
    public DateTime GeneratedAt { get; set; }
    public double OverallHeatScore { get; set; }
}
```
```csharp
public sealed class BlockRange
{
}
    public int StartBlock { get; init; }
    public int EndBlock { get; init; }
    public double AverageHeat { get; init; }
    public int BlockCount;;
}
```
```csharp
private sealed class PrefetchPrediction
{
}
    public required string ObjectId { get; init; }
    public int[] PredictedNextBlocks { get; set; };
    public double[] PredictionConfidence { get; set; };
    public Dictionary<int, int[]> SequentialPatterns { get; set; };
    public DateTime LastUpdated { get; set; }
}
```
```csharp
public sealed class DatabaseFileInfo
{
}
    public required string ObjectId { get; init; }
    public DatabaseFileType Type { get; init; }
    public int[] HeaderBlocks { get; set; };
    public int[] IndexBlocks { get; set; };
    public int[] DataBlocks { get; set; };
    public int[] FreeSpaceBlocks { get; set; };
    public int PageSize { get; init; }
    public DateTime LastAnalyzed { get; set; }
}
```
```csharp
private sealed class RebalanceEvent
{
}
    public required string ObjectId { get; init; }
    public RebalanceReason Reason { get; init; }
    public int[] AffectedBlocks { get; set; };
    public DateTime Timestamp { get; init; };
    public double Priority { get; init; }
}
```
```csharp
public sealed class BlockTierConfig
{
}
    public int DefaultBlockSize { get; init; };
    public long MinObjectSizeForBlocking { get; init; };
    public int HotBlockAccessThreshold { get; init; };
    public int ColdBlockInactiveDays { get; init; };
    public double HotBlockPercentageThreshold { get; init; };
    public double HeatDecayFactor { get; init; };
    public int PrefetchLookahead { get; init; };
    public double PrefetchMinConfidence { get; init; };
    public Dictionary<StorageTier, decimal> TierCosts { get; init; };
    public Dictionary<StorageTier, double> TierLatencyMultipliers { get; init; };
    public int RebalanceIntervalMinutes { get; init; };
    public int MaxBlocksPerRebalance { get; init; };
    public static BlockTierConfig Default;;
}
```
```csharp
public sealed class BlockInfo
{
}
    public int BlockIndex { get; init; }
    public long Offset { get; init; }
    public int Size { get; init; }
    public StorageTier Tier { get; init; }
    public int AccessCount { get; init; }
    public DateTime LastAccess { get; init; }
    public string? Location { get; init; }
    public double HeatValue { get; init; }
    public byte[]? ContentHash { get; init; }
}
```
```csharp
public sealed class BlockTierRecommendation
{
}
    public required string ObjectId { get; init; }
    public IReadOnlyList<int> BlocksToPromote { get; init; };
    public IReadOnlyList<int> BlocksToDemote { get; init; };
    public Dictionary<StorageTier, int> BlockDistribution { get; init; };
    public bool RecommendWholeObjectMove { get; init; }
    public string Reasoning { get; init; };
    public decimal EstimatedMonthlySavings { get; init; }
    public double PerformanceImpactScore { get; init; }
}
```
```csharp
public sealed class BlockSplitResult
{
}
    public required string ObjectId { get; init; }
    public int BlockCount { get; init; }
    public int BlockSize { get; init; }
    public IReadOnlyList<BlockInfo> Blocks { get; init; };
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed class BlockReassemblyResult
{
}
    public required string ObjectId { get; init; }
    public Dictionary<StorageTier, int> TierFetchCounts { get; init; };
    public long TotalBytes { get; init; }
    public double TotalLatencyMs { get; init; }
    public int[] PrefetchedBlocks { get; init; };
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public sealed class CostOptimizationAnalysis
{
}
    public required string ObjectId { get; init; }
    public decimal CurrentMonthlyCost { get; init; }
    public decimal OptimizedMonthlyCost { get; init; }
    public decimal MonthlySavings;;
    public double SavingsPercentage;;
    public double CurrentPerformanceScore { get; init; }
    public double OptimizedPerformanceScore { get; init; }
    public double PerformanceTradeoffPercent { get; init; }
    public List<BlockMoveRecommendation> RecommendedMoves { get; init; };
    public DateTime AnalyzedAt { get; init; };
}
```
```csharp
public sealed class BlockMoveRecommendation
{
}
    public int BlockIndex { get; init; }
    public StorageTier CurrentTier { get; init; }
    public StorageTier TargetTier { get; init; }
    public decimal CostSavings { get; init; }
    public double LatencyImpact { get; init; }
    public double Priority { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/CostOptimizedTieringStrategy.cs
```csharp
public sealed class CostOptimizedTieringStrategy : TieringStrategyBase
{
}
    public sealed class CostModel;
    public sealed class CostAnalysis;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public CostModel CurrentCostModel { get => _costModel; set => _costModel = value ?? CostModel.Default; }
    public CostAnalysis AnalyzeCosts(DataObject data);
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
public sealed class CostModel
{
}
    public Dictionary<StorageTier, decimal> StorageCostPerGbMonth { get; init; };
    public Dictionary<StorageTier, decimal> ReadCostPer1000Ops { get; init; };
    public Dictionary<StorageTier, decimal> WriteCostPer1000Ops { get; init; };
    public Dictionary<StorageTier, decimal> RetrievalCostPerGb { get; init; };
    public Dictionary<StorageTier, int> MinRetentionDays { get; init; };
    public decimal TransitionCostPerGb { get; init; };
    public static CostModel Default;;
    public static CostModel AwsS3;;
}
```
```csharp
public sealed class CostAnalysis
{
}
    public required string ObjectId { get; init; }
    public StorageTier CurrentTier { get; init; }
    public decimal CurrentMonthlyCost { get; init; }
    public StorageTier OptimalTier { get; init; }
    public decimal OptimalMonthlyCost { get; init; }
    public decimal TransitionCost { get; init; }
    public double BreakEvenMonths { get; init; }
    public decimal MonthlySavings;;
    public decimal AnnualSavings;;
    public bool IsTransitionRecommended { get; init; }
    public Dictionary<string, decimal> CostBreakdown { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/AccessFrequencyTieringStrategy.cs
```csharp
public sealed class AccessFrequencyTieringStrategy : TieringStrategyBase
{
}
    public sealed class TierThresholds;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TierThresholds Thresholds { get => _thresholds; set => _thresholds = value ?? TierThresholds.Default; }
    public void RecordAccess(string objectId, DateTime? timestamp = null);
    public (int Last24Hours, int Last7Days, int Last30Days, DateTime? LastAccess) GetAccessStats(string objectId);
    public void ClearCooldown(string objectId);
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
private sealed class AccessTracking
{
}
    public required string ObjectId { get; init; }
    public int AccessesLast24Hours { get; set; }
    public int AccessesLast7Days { get; set; }
    public int AccessesLast30Days { get; set; }
    public DateTime LastAccess { get; set; }
    public DateTime LastUpdated { get; set; }
}
```
```csharp
private sealed class TierTransition
{
}
    public required string ObjectId { get; init; }
    public StorageTier FromTier { get; init; }
    public StorageTier ToTier { get; init; }
    public DateTime TransitionedAt { get; init; }
}
```
```csharp
public sealed class TierThresholds
{
}
    public int HotDailyAccessMin { get; init; };
    public int WarmWeeklyAccessMin { get; init; };
    public int ColdMonthlyAccessMin { get; init; };
    public int GlacierInactiveDays { get; init; };
    public static TierThresholds Default;;
    public static TierThresholds Aggressive;;
    public static TierThresholds Conservative;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/PolicyBasedTieringStrategy.cs
```csharp
public sealed class PolicyBasedTieringStrategy : TieringStrategyBase
{
}
    public sealed class TieringRule;
    public sealed class RuleCondition;
    public enum ConditionProperty;
    public enum ConditionOperator;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public PolicyBasedTieringStrategy();
    public void AddRule(TieringRule rule);
    public bool RemoveRule(string ruleId);
    public IReadOnlyList<TieringRule> GetRules();
    public bool SetRuleEnabled(string ruleId, bool enabled);
    public (long Evaluated, long Matched) GetRuleStats();
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
public sealed class TieringRule
{
}
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int Priority { get; init; }
    public bool IsEnabled { get; init; };
    public required IReadOnlyList<RuleCondition> Conditions { get; init; }
    public StorageTier TargetTier { get; init; }
    public bool StopOnMatch { get; init; };
    public string? ContentTypeFilter { get; init; }
    public string? ClassificationFilter { get; init; }
    public DateTime CreatedAt { get; init; };
}
```
```csharp
public sealed class RuleCondition
{
}
    public required ConditionProperty Property { get; init; }
    public required ConditionOperator Operator { get; init; }
    public required object Value { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/TieringStrategyBase.cs
```csharp
public sealed class DataObject
{
}
    public required string ObjectId { get; init; }
    public StorageTier CurrentTier { get; init; };
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime LastModifiedAt { get; init; };
    public DateTime LastAccessedAt { get; init; };
    public long AccessCount { get; init; }
    public int AccessesLast24Hours { get; init; }
    public int AccessesLast7Days { get; init; }
    public int AccessesLast30Days { get; init; }
    public string? ContentType { get; init; }
    public string? Classification { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public TimeSpan Age;;
    public TimeSpan TimeSinceLastAccess;;
    public TimeSpan TimeSinceLastModified;;
}
```
```csharp
public record TierRecommendation(string ObjectId, StorageTier CurrentTier, StorageTier RecommendedTier, string Reason, double Confidence)
{
}
    public bool RequiresMove;;
    public bool IsDemotion;;
    public bool IsPromotion;;
    public double Priority { get; init; }
    public decimal? EstimatedMonthlySavings { get; init; }
}
```
```csharp
public sealed class TierMoveResult
{
}
    public required bool Success { get; init; }
    public required string ObjectId { get; init; }
    public StorageTier SourceTier { get; init; }
    public StorageTier TargetTier { get; init; }
    public TimeSpan Duration { get; init; }
    public long BytesTransferred { get; init; }
    public string? ErrorMessage { get; init; }
    public static TierMoveResult Succeeded(string objectId, StorageTier source, StorageTier target, TimeSpan duration, long bytes);;
    public static TierMoveResult Failed(string objectId, StorageTier source, StorageTier target, string error);;
}
```
```csharp
public sealed class TieringStats
{
}
    public long TotalObjectsEvaluated { get; set; }
    public long TotalObjectsMoved { get; set; }
    public long TotalBytesMoved { get; set; }
    public Dictionary<StorageTier, long> ObjectsPerTier { get; set; };
    public Dictionary<StorageTier, long> BytesPerTier { get; set; };
    public long PendingRecommendations { get; set; }
    public long FailedMoves { get; set; }
    public decimal TotalEstimatedMonthlySavings { get; set; }
    public DateTime? LastEvaluationAt { get; set; }
    public double AverageEvaluationTimeMs { get; set; }
}
```
```csharp
public interface ITieringStrategy : IDataManagementStrategy
{
}
    Task<TierRecommendation> EvaluateAsync(DataObject data, CancellationToken ct = default);;
    Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default);;
    Task<IEnumerable<TierRecommendation>> BatchEvaluateAsync(IEnumerable<DataObject> data, CancellationToken ct = default);;
    Task<TieringStats> GetStatsAsync(CancellationToken ct = default);;
}
```
```csharp
public abstract class TieringStrategyBase : DataManagementStrategyBase, ITieringStrategy
{
}
    protected IStorageTierHandler? TierHandler { get; set; }
    public override DataManagementCategory Category;;
    public async Task<TierRecommendation> EvaluateAsync(DataObject data, CancellationToken ct = default);
    public async Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default);
    public virtual async Task<IEnumerable<TierRecommendation>> BatchEvaluateAsync(IEnumerable<DataObject> data, CancellationToken ct = default);
    public Task<TieringStats> GetStatsAsync(CancellationToken ct = default);
    protected abstract Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);;
    protected virtual async Task<TierMoveResult> MoveCoreAsync(string objectId, StorageTier targetTier, CancellationToken ct);
    protected TierRecommendation NoChange(DataObject data, string reason, double confidence = 1.0);;
    protected TierRecommendation Promote(DataObject data, StorageTier targetTier, string reason, double confidence, double priority = 0.5);;
    protected TierRecommendation Demote(DataObject data, StorageTier targetTier, string reason, double confidence, double priority = 0.5, decimal? savings = null);;
    protected void RegisterObject(DataObject data);
    protected TierRecommendation? GetPendingRecommendation(string objectId);;
    protected static decimal EstimateMonthlySavings(long sizeBytes, StorageTier sourceTier, StorageTier targetTier);
}
```
```csharp
public interface IStorageTierHandler
{
}
    Task<StorageTier> GetCurrentTierAsync(string objectId, CancellationToken ct = default);;
    Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default);;
    Task<DataObject?> GetObjectMetadataAsync(string objectId, CancellationToken ct = default);;
}
```
```csharp
public sealed class InMemoryStorageTierHandler : IStorageTierHandler
{
}
    public void RegisterObject(DataObject obj);
    public Task<StorageTier> GetCurrentTierAsync(string objectId, CancellationToken ct = default);
    public Task<TierMoveResult> MoveToTierAsync(string objectId, StorageTier targetTier, CancellationToken ct = default);
    public Task<DataObject?> GetObjectMetadataAsync(string objectId, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/PredictiveTieringStrategy.cs
```csharp
public sealed class PredictiveTieringStrategy : TieringStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double MinConfidenceThreshold { get => _minConfidenceThreshold; set => _minConfidenceThreshold = Math.Clamp(value, 0.0, 1.0); }
    public void RecordAccess(string objectId, string accessType, DateTime? timestamp = null);
    public double GetPredictionAccuracy();
    public void ReportActualAccess(string objectId, int actualAccesses);
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
private sealed class AccessHistory
{
}
    public required string ObjectId { get; init; }
    public List<AccessEvent> Events { get; };
    public double[] DailyAccessCounts { get; set; };
    public double[] WeekdayPattern { get; set; };
    public double[] HourlyPattern { get; set; };
    public DateTime LastUpdated { get; set; }
}
```
```csharp
private sealed class AccessEvent
{
}
    public DateTime Timestamp { get; init; }
    public AccessType Type { get; init; }
}
```
```csharp
private sealed class PredictionResult
{
}
    public required string ObjectId { get; init; }
    public DateTime PredictedAt { get; init; }
    public double[] PredictedDailyAccesses { get; init; };
    public double Confidence { get; init; }
    public AccessTrend Trend { get; init; }
    public StorageTier RecommendedTier { get; init; }
    public string Reasoning { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Tiering/AgeTieringStrategy.cs
```csharp
public sealed class AgeTieringStrategy : TieringStrategyBase
{
}
    public sealed class AgeTierConfig;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AgeTierConfig Config { get => _config; set => _config = value ?? AgeTierConfig.Default; }
    public void ExcludeClassification(string classification);
    public void IncludeClassification(string classification);
    public void ExcludeObject(string objectId);
    public void IncludeObject(string objectId);
    public (IReadOnlyList<string> Classifications, IReadOnlyList<string> ObjectIds) GetExclusions();
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```
```csharp
public sealed class AgeTierConfig
{
}
    public TimeSpan HotToWarmAge { get; init; };
    public TimeSpan WarmToColdAge { get; init; };
    public TimeSpan ColdToArchiveAge { get; init; };
    public TimeSpan ArchiveToGlacierAge { get; init; };
    public bool UseLastModifiedDate { get; init; };
    public TimeSpan GracePeriod { get; init; };
    public static AgeTierConfig Default;;
    public static AgeTierConfig Aggressive;;
    public static AgeTierConfig Conservative;;
    public static AgeTierConfig ModifiedDateBased;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/AutoShardingStrategy.cs
```csharp
public sealed class AutoShardingConfig
{
}
    public int MinShards { get; init; };
    public int MaxShards { get; init; };
    public long SplitThresholdObjects { get; init; };
    public long SplitThresholdBytes { get; init; };
    public long MergeThresholdObjects { get; init; };
    public long MergeThresholdBytes { get; init; };
    public double RebalanceThresholdPercent { get; init; };
    public TimeSpan CheckInterval { get; init; };
    public TimeSpan OperationCooldown { get; init; };
}
```
```csharp
public sealed class AutoShardingOperationRecord
{
}
    public required string OperationId { get; init; }
    public AutoShardingOperation OperationType { get; init; }
    public required IReadOnlyList<string> InvolvedShards { get; init; }
    public DateTime StartedAt { get; init; };
    public DateTime? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long ObjectsMoved { get; set; }
    public long BytesMoved { get; set; }
}
```
```csharp
public sealed class AutoShardingStrategy : ShardingStrategyBase
{
}
    public AutoShardingStrategy() : this(new AutoShardingConfig());
    public AutoShardingStrategy(AutoShardingConfig config, int cacheMaxSize = 100000);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public AutoShardingConfig Configuration;;
    public bool AutoOperationsEnabled { get => _autoOperationsEnabled; set => _autoOperationsEnabled = value; }
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public async Task<IReadOnlyList<ShardInfo>> SplitShardAsync(string shardId, CancellationToken ct = default);
    public async Task<ShardInfo> MergeShardsAsync(string shardId1, string shardId2, CancellationToken ct = default);
    public IReadOnlyList<AutoShardingOperationRecord> GetOperationHistory(int limit = 100);
    public IReadOnlyList<ShardInfo> GetShardsNeedingSplit();
    public IReadOnlyList<ShardInfo> GetShardsNeedingMerge();
    public AutoShardingHealth GetHealth();
    public void UpdateShardMetricsAndCheck(string shardId, long objectCount, long sizeBytes);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class AutoShardingHealth
{
}
    public int TotalShards { get; init; }
    public int OnlineShards { get; init; }
    public long TotalObjects { get; init; }
    public long TotalSizeBytes { get; init; }
    public double AverageObjectsPerShard { get; init; }
    public long AverageSizePerShard { get; init; }
    public double MaxDeviationPercent { get; init; }
    public int ShardsNeedingSplit { get; init; }
    public int ShardsNeedingMerge { get; init; }
    public int RecentOperations { get; init; }
    public bool IsHealthy { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/TimeShardingStrategy.cs
```csharp
public sealed class TimePartition
{
}
    public required string PartitionId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public required string ShardId { get; init; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; init; };
    public long ObjectCount { get; set; }
    public long SizeBytes { get; set; }
}
```
```csharp
public sealed class TimeShardingStrategy : ShardingStrategyBase
{
}
    public TimeShardingStrategy() : this(TimePartitionGranularity.Day);
    public TimeShardingStrategy(TimePartitionGranularity granularity, int rollingPartitionCount = 30, int archiveAfterDays = 90, int cacheMaxSize = 100000);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimePartitionGranularity Granularity;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public Task<ShardInfo> GetShardForTimestampAsync(DateTime timestamp, CancellationToken ct = default);
    public Task<IReadOnlyList<ShardInfo>> GetShardsForTimeRangeAsync(DateTime startTime, DateTime endTime, bool includeArchived = false, CancellationToken ct = default);
    public IReadOnlyList<TimePartition> GetAllPartitions(bool includeArchived = false);
    public TimePartition GetCurrentPartition();
    public Task<bool> ArchivePartitionAsync(string partitionId, CancellationToken ct = default);
    public void SetTimestampKeyPattern(string regexPattern);
    public static string CreateTimeKey(DateTime timestamp, string key);
    public static string CreateTimeKey(string key);
    public void UpdatePartitionMetrics(string partitionId, long objectDelta, long sizeDelta);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/TenantShardingStrategy.cs
```csharp
public sealed class TenantConfig
{
}
    public required string TenantId { get; init; }
    public string? DisplayName { get; init; }
    public TenantIsolationMode IsolationMode { get; init; };
    public string? AssignedShardId { get; set; }
    public DateTime CreatedAt { get; init; };
    public long ObjectCount { get; set; }
    public long StorageSizeBytes { get; set; }
    public string Tier { get; init; };
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class TenantMigrationStatus
{
}
    public required string TenantId { get; init; }
    public required string SourceShardId { get; init; }
    public required string TargetShardId { get; init; }
    public TenantMigrationState State { get; set; };
    public int ProgressPercent { get; set; }
    public DateTime StartedAt { get; init; };
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
```
```csharp
public sealed class TenantShardingStrategy : ShardingStrategyBase
{
}
    public TenantShardingStrategy() : this(TenantIsolationMode.Hybrid);
    public TenantShardingStrategy(TenantIsolationMode defaultIsolationMode, int maxTenantsPerSharedShard = 100, long dedicatedShardThreshold = 1_000_000, int cacheMaxSize = 100000);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public async Task<TenantConfig> RegisterTenantAsync(string tenantId, string? displayName = null, TenantIsolationMode? isolationMode = null, string tier = "standard", CancellationToken ct = default);
    public TenantConfig? GetTenant(string tenantId);
    public IReadOnlyCollection<TenantConfig> GetAllTenants();
    public IReadOnlyList<TenantConfig> GetTenantsOnShard(string shardId);
    public async Task<TenantMigrationStatus> MigrateTenantAsync(string tenantId, string targetShardId, CancellationToken ct = default);
    public TenantMigrationStatus? GetMigrationStatus(string tenantId);
    public void UpdateTenantMetrics(string tenantId, long objectCount, long sizeBytes);
    public static string GetTenantPrefixedKey(string tenantId, string key);
    public async Task<bool> RemoveTenantAsync(string tenantId, bool deleteData = false, CancellationToken ct = default);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/ConsistentHashShardingStrategy.cs
```csharp
public sealed class ConsistentHashShardingStrategy : ShardingStrategyBase
{
}
    public ConsistentHashShardingStrategy() : this(150, 100000);
    public ConsistentHashShardingStrategy(int virtualNodesPerShard, int cacheMaxSize = 100000);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    protected override Task<ShardInfo> AddShardCoreAsync(string shardId, string physicalLocation, CancellationToken ct);
    protected override Task<bool> RemoveShardCoreAsync(string shardId, bool migrateData, CancellationToken ct);
    public Task<IReadOnlyList<ShardInfo>> GetReplicaShardsAsync(string key, int replicaCount, CancellationToken ct = default);
    public RingInfo GetRingInfo();
    public IReadOnlyList<RingPosition> VisualizeRingAroundKey(string key, int windowSize = 10);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class RingInfo
{
}
    public int TotalVirtualNodes { get; init; }
    public required IReadOnlyDictionary<string, int> VirtualNodesPerShard { get; init; }
    public required IReadOnlyDictionary<uint, string> RingPositions { get; init; }
}
```
```csharp
public sealed class RingPosition
{
}
    public uint Position { get; init; }
    public required string ShardId { get; init; }
    public bool IsKeyPosition { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/VirtualShardingStrategy.cs
```csharp
public sealed class VirtualShard
{
}
    public required string VirtualShardId { get; init; }
    public required string PhysicalShardId { get; set; }
    public HashSet<string> Aliases { get; init; };
    public DateTime CreatedAt { get; init; };
    public DateTime LastMappingChange { get; set; };
    public long Version { get; set; };
    public bool IsActive { get; set; };
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class VirtualShardMigrationPlan
{
}
    public required string VirtualShardId { get; init; }
    public required string SourcePhysicalShardId { get; init; }
    public required string TargetPhysicalShardId { get; init; }
    public bool CopyData { get; init; };
    public bool VerifyAfterMigration { get; init; };
    public TimeSpan? EstimatedDuration { get; init; }
}
```
```csharp
public sealed class VirtualShardingStrategy : ShardingStrategyBase
{
}
    public VirtualShardingStrategy() : this(64, 100000);
    public VirtualShardingStrategy(int virtualShardCount, int cacheMaxSize = 100000);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public string GetVirtualShardId(string key);
    public VirtualShard? GetVirtualShard(string virtualShardId);
    public IReadOnlyList<VirtualShard> GetAllVirtualShards(bool includeInactive = false);
    public IReadOnlyList<VirtualShard> GetVirtualShardsOnPhysical(string physicalShardId);
    public Task<bool> RemapVirtualShardAsync(string virtualShardId, string newPhysicalShardId, CancellationToken ct = default);
    public VirtualShardMigrationPlan CreateMigrationPlan(string virtualShardId, string targetPhysicalShardId);
    public void AddAlias(string alias, string virtualShardId);
    public bool RemoveAlias(string alias);
    public IReadOnlySet<string> GetAliases(string virtualShardId);
    public bool DeactivateVirtualShard(string virtualShardId);
    public bool ReactivateVirtualShard(string virtualShardId);
    public VirtualMappingStats GetMappingStats();
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class VirtualMappingStats
{
}
    public int TotalVirtualShards { get; init; }
    public int ActiveVirtualShards { get; init; }
    public int TotalAliases { get; init; }
    public required IReadOnlyDictionary<string, int> VirtualShardsPerPhysical { get; init; }
    public double AverageVirtualPerPhysical { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/DirectoryShardingStrategy.cs
```csharp
public sealed class DirectoryEntry
{
}
    public required string Key { get; init; }
    public required string ShardId { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime LastAccessedAt { get; set; };
    public long AccessCount { get; set; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class DirectoryShardingStrategy : ShardingStrategyBase
{
}
    public DirectoryShardingStrategy() : this(null, 100000);
    public DirectoryShardingStrategy(string? persistencePath, int cacheMaxSize = 100000, int persistenceIntervalSeconds = 60);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public Task<DirectoryEntry> RegisterMappingAsync(string key, string shardId, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken ct = default);
    public Task<int> RegisterMappingsBatchAsync(IReadOnlyDictionary<string, string> mappings, CancellationToken ct = default);
    public Task<bool> UpdateMappingAsync(string key, string newShardId, CancellationToken ct = default);
    public Task<bool> RemoveMappingAsync(string key, CancellationToken ct = default);
    public DirectoryEntry? GetEntry(string key);
    public IReadOnlyCollection<DirectoryEntry> GetAllEntries();
    public IReadOnlyCollection<DirectoryEntry> GetEntriesForShard(string shardId);
    public void SetDefaultShard(string shardId);
    public async Task PersistDirectoryAsync(CancellationToken ct = default);
    public async Task LoadDirectoryAsync(CancellationToken ct = default);
    protected override async Task DisposeCoreAsync();
}
```
```csharp
private sealed class DirectoryPersistenceData
{
}
    public List<DirectoryEntry> Entries { get; set; };
    public string DefaultShardId { get; set; };
    public DateTime PersistedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/GeoShardingStrategy.cs
```csharp
public sealed class GeoLocation
{
}
    public required string CountryCode { get; init; }
    public GeoRegion Region { get; init; }
    public string? SubRegion { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public DataResidencyLevel ResidencyRequirement { get; init; };
}
```
```csharp
public sealed class GeoShardConfig
{
}
    public required string ShardId { get; init; }
    public GeoRegion Region { get; init; }
    public HashSet<string> CountryCodes { get; init; };
    public required GeoLocation Location { get; init; }
    public bool SupportsResidencyCompliance { get; init; }
    public int Priority { get; init; };
}
```
```csharp
public sealed class GeoShardingStrategy : ShardingStrategyBase
{
}
    public GeoShardingStrategy() : this(100000);
    public GeoShardingStrategy(int cacheMaxSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public Task<ShardInfo> AddGeoShardAsync(GeoShardConfig config, string physicalLocation, CancellationToken ct = default);
    public void RegisterKeyLocation(string key, GeoLocation location);
    public void RegisterKeysLocation(IEnumerable<string> keys, GeoLocation location);
    public void SetDefaultLocation(GeoLocation location);
    public Task<ShardInfo> GetShardForLocationAsync(GeoLocation location, CancellationToken ct = default);
    public IReadOnlyList<ShardInfo> GetShardsInRegion(GeoRegion region);
    public IReadOnlyList<ShardInfo> GetResidencyCompliantShards(string countryCode);
    public static double CalculateDistance(GeoLocation loc1, GeoLocation loc2);
    public static GeoRegion GetRegionForCountry(string countryCode);
    public static GeoRegion GetRegionForCountryOrDefault(string countryCode, GeoRegion defaultRegion);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/RangeShardingStrategy.cs
```csharp
public sealed class KeyRange : IComparable<KeyRange>
{
}
    public string Start { get; }
    public string? End { get; }
    public string ShardId { get; }
    public KeyRange(string start, string? end, string shardId);
    public bool Contains(string key);
    public int CompareTo(KeyRange? other);
}
```
```csharp
public sealed class RangeShardingStrategy : ShardingStrategyBase
{
}
    public RangeShardingStrategy() : this(100000);
    public RangeShardingStrategy(int cacheMaxSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public Task<ShardInfo> AddRangeAsync(string start, string? end, string shardId, string physicalLocation, CancellationToken ct = default);
    public Task<ShardInfo> SplitRangeAtKeyAsync(string shardId, string splitKey, string newShardId, string newPhysicalLocation, CancellationToken ct = default);
    public Task<bool> MergeRangesAsync(string shardId1, string shardId2, CancellationToken ct = default);
    public Task<IReadOnlyList<ShardInfo>> GetShardsForRangeAsync(string startKey, string endKey, CancellationToken ct = default);
    public IReadOnlyList<KeyRange> GetAllRanges();
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/ShardingStrategyBase.cs
```csharp
public record ShardInfo(string ShardId, string PhysicalLocation, ShardStatus Status, long ObjectCount, long SizeBytes)
{
}
    public DateTime CreatedAt { get; init; };
    public DateTime LastModifiedAt { get; init; };
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class RebalanceOptions
{
}
    public bool AllowDataMovement { get; init; };
    public double MaxDataMovementPercent { get; init; };
    public double TargetBalanceRatio { get; init; };
    public bool Async { get; init; };
    public TimeSpan? MaxDuration { get; init; }
    public bool SkipBusyShards { get; init; };
}
```
```csharp
public sealed class ShardingStats
{
}
    public int TotalShards { get; init; }
    public int OnlineShards { get; init; }
    public int OfflineShards { get; init; }
    public long TotalObjects { get; init; }
    public long TotalSizeBytes { get; init; }
    public double AverageObjectsPerShard;;
    public double AverageSizePerShard;;
    public double BalanceRatio { get; init; }
    public long TotalRoutingOperations { get; init; }
    public long TotalRebalanceOperations { get; init; }
    public double AverageRoutingLatencyMs { get; init; }
}
```
```csharp
public sealed class ShardRoutingResult
{
}
    public required bool Success { get; init; }
    public ShardInfo? Shard { get; init; }
    public string? ErrorMessage { get; init; }
    public double RoutingTimeMs { get; init; }
    public static ShardRoutingResult Ok(ShardInfo shard, double routingTimeMs);;
    public static ShardRoutingResult Failed(string error);;
}
```
```csharp
public interface IShardingStrategy : IDataManagementStrategy
{
}
    Task<ShardInfo> GetShardAsync(string key, CancellationToken ct = default);;
    Task<IEnumerable<ShardInfo>> GetAllShardsAsync(CancellationToken ct = default);;
    Task<bool> RebalanceAsync(RebalanceOptions options, CancellationToken ct = default);;
    Task<ShardingStats> GetStatsAsync(CancellationToken ct = default);;
    Task<ShardInfo> AddShardAsync(string shardId, string physicalLocation, CancellationToken ct = default);;
    Task<bool> RemoveShardAsync(string shardId, bool migrateData = true, CancellationToken ct = default);;
    Task<bool> UpdateShardStatusAsync(string shardId, ShardStatus status, CancellationToken ct = default);;
}
```
```csharp
public abstract class ShardingStrategyBase : DataManagementStrategyBase, IShardingStrategy
{
}
    protected readonly BoundedDictionary<string, ShardInfo> ShardRegistry = new BoundedDictionary<string, ShardInfo>(1000);
    protected readonly object ShardLock = new();
    public override DataManagementCategory Category;;
    public async Task<ShardInfo> GetShardAsync(string key, CancellationToken ct = default);
    public async Task<IEnumerable<ShardInfo>> GetAllShardsAsync(CancellationToken ct = default);
    public async Task<bool> RebalanceAsync(RebalanceOptions options, CancellationToken ct = default);
    public async Task<ShardingStats> GetStatsAsync(CancellationToken ct = default);
    public async Task<ShardInfo> AddShardAsync(string shardId, string physicalLocation, CancellationToken ct = default);
    public async Task<bool> RemoveShardAsync(string shardId, bool migrateData = true, CancellationToken ct = default);
    public async Task<bool> UpdateShardStatusAsync(string shardId, ShardStatus status, CancellationToken ct = default);
    protected abstract Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);;
    protected virtual Task<IEnumerable<ShardInfo>> GetAllShardsCoreAsync(CancellationToken ct);
    protected abstract Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);;
    protected virtual Task<ShardInfo> AddShardCoreAsync(string shardId, string physicalLocation, CancellationToken ct);
    protected virtual async Task<bool> RemoveShardCoreAsync(string shardId, bool migrateData, CancellationToken ct);
    protected virtual Task<bool> UpdateShardStatusCoreAsync(string shardId, ShardStatus status, CancellationToken ct);
    protected virtual Task MigrateDataFromShardAsync(string shardId, CancellationToken ct);
    protected virtual void OnShardAdded(ShardInfo shard);
    protected virtual void OnShardRemoved(ShardInfo shard);
    protected void UpdateShardMetrics(string shardId, long objectCount, long sizeBytes);
    protected void IncrementShardMetrics(string shardId, long objectDelta, long sizeDelta);
    protected ShardInfo? GetOnlineShard(string shardId);
    protected IReadOnlyList<ShardInfo> GetOnlineShards();
    protected static uint ComputeHash(string key);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/HashShardingStrategy.cs
```csharp
public sealed class HashShardingStrategy : ShardingStrategyBase
{
}
    public HashShardingStrategy() : this(16);
    public HashShardingStrategy(int initialShardCount, int cacheMaxSize = 100000);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public string GetShardIdForKey(string key);
    public Task<IReadOnlyDictionary<string, ShardInfo>> GetShardsForKeysAsync(IEnumerable<string> keys, CancellationToken ct = default);
    public IReadOnlyDictionary<string, List<string>> GroupKeysByShard(IEnumerable<string> keys);
    protected override void OnShardAdded(ShardInfo shard);
    protected override void OnShardRemoved(ShardInfo shard);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Sharding/CompositeShardingStrategy.cs
```csharp
public sealed class ShardKeyComponent
{
}
    public required string Name { get; init; }
    public int Order { get; init; }
    public double Weight { get; init; };
    public Func<string, string>? Extractor { get; init; }
}
```
```csharp
public sealed class CompositeShardKey
{
}
    public required IReadOnlyDictionary<string, string> Components { get; init; }
    public static CompositeShardKey Create(params (string Name, string Value)[] components);
    public override string ToString();
}
```
```csharp
public sealed class CompositeShardingStrategy : ShardingStrategyBase
{
}
    public CompositeShardingStrategy() : this(100000);
    public CompositeShardingStrategy(int cacheMaxSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);
    public void AddKeyComponent(ShardKeyComponent component);
    public void ConfigurePattern(string pattern);
    public void SetDelimiters(string keyDelimiter = ":", string componentDelimiter = "=");
    public Task<ShardInfo> GetShardForCompositeKeyAsync(CompositeShardKey compositeKey, CancellationToken ct = default);
    public void RegisterCompositeMapping(CompositeShardKey compositeKey, string shardId);
    public IReadOnlyDictionary<string, string> GetAllMappings();
    public string CreateCompositeKeyString(params (string Name, string Value)[] components);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/DataPurgingStrategy.cs
```csharp
public sealed class LegalHold
{
}
    public required string HoldId { get; init; }
    public required string Name { get; init; }
    public string? Reason { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime? ExpiresAt { get; init; }
    public HashSet<string> ObjectIds { get; init; };
    public PolicyScope? Scope { get; init; }
    public bool IsActive;;
}
```
```csharp
public sealed class PurgeResult
{
}
    public required string ObjectId { get; init; }
    public required bool Success { get; init; }
    public PurgingMethod Method { get; init; }
    public int PassesPerformed { get; init; }
    public long BytesPurged { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Verified { get; init; }
    public string? ErrorMessage { get; init; }
    public List<string>? CascadedObjects { get; init; }
    public string? AuditTrailId { get; init; }
}
```
```csharp
public sealed class PurgeAuditRecord
{
}
    public required string AuditId { get; init; }
    public required string ObjectId { get; init; }
    public string? ObjectPath { get; init; }
    public long ObjectSize { get; init; }
    public PurgingMethod Method { get; init; }
    public string? InitiatedBy { get; init; }
    public DateTime InitiatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public string? PolicyId { get; init; }
    public string? ComplianceStandard { get; init; }
    public string? VerificationHash { get; init; }
    public List<string>? CascadedDeletions { get; init; }
}
```
```csharp
public sealed class ComplianceVerification
{
}
    public required bool IsCompliant { get; init; }
    public List<string> Violations { get; init; };
    public List<string> Warnings { get; init; };
    public List<string> ApplicableRegulations { get; init; };
    public List<string> BlockingHolds { get; init; };
}
```
```csharp
public sealed class DataPurgingStrategy : LifecycleStrategyBase
{
}
    public PurgingMethod DefaultMethod { get; set; };
    public bool EnableCascadingDeletions { get; set; };
    public bool VerifyAfterPurge { get; set; };
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task DisposeCoreAsync();
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);
    public async Task<PurgeResult> PurgeAsync(string objectId, PurgingMethod? method = null, string? reason = null, string? initiatedBy = null, CancellationToken ct = default);
    public void CreateHold(LegalHold hold);
    public bool RemoveHold(string holdId);
    public bool AddToHold(string holdId, string objectId);
    public bool RemoveFromHold(string holdId, string objectId);
    public Task<ComplianceVerification> VerifyComplianceAsync(LifecycleDataObject data, CancellationToken ct = default);
    public IEnumerable<PurgeAuditRecord> GetAuditTrail(string objectId);
    public IEnumerable<PurgeAuditRecord> GetAllAuditRecords(DateTime? startDate = null, DateTime? endDate = null);
    public void RegisterRelationship(string objectId, params string[] relatedObjectIds);
    public async Task<IEnumerable<PurgeResult>> BatchPurgeAsync(IEnumerable<string> objectIds, PurgingMethod? method = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/LifecycleStrategyBase.cs
```csharp
public sealed class LifecycleDecision
{
}
    public required LifecycleAction Action { get; init; }
    public required string Reason { get; init; }
    public double Priority { get; init; };
    public double Confidence { get; init; };
    public DateTime? ScheduledAt { get; init; }
    public DateTime? NextEvaluationDate { get; init; }
    public string? TargetLocation { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }
    public string? PolicyName { get; init; }
    public static LifecycleDecision NoAction(string reason, DateTime? nextEvaluation = null);;
    public static LifecycleDecision Archive(string reason, string? target = null, double priority = 0.5);;
    public static LifecycleDecision Delete(string reason, double priority = 0.5);;
    public static LifecycleDecision Tier(string reason, string targetTier, double priority = 0.5);;
    public static LifecycleDecision Notify(string reason, Dictionary<string, object>? notifyParams = null);;
    public static LifecycleDecision Purge(string reason, string method, double priority = 0.5);;
    public static LifecycleDecision Expire(string reason, bool softDelete = true);;
    public static LifecycleDecision Migrate(string reason, string targetLocation, double priority = 0.5);;
}
```
```csharp
public sealed class LifecyclePolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int Priority { get; init; };
    public bool Enabled { get; init; };
    public required List<PolicyCondition> Conditions { get; init; }
    public required LifecycleAction Action { get; init; }
    public Dictionary<string, object>? ActionParameters { get; init; }
    public string? CronSchedule { get; init; }
    public PolicyScope? Scope { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime? LastModifiedAt { get; init; }
    public List<string>? ConflictsWith { get; init; }
}
```
```csharp
public sealed class PolicyCondition
{
}
    public required string Field { get; init; }
    public required ConditionOperator Operator { get; init; }
    public required object Value { get; init; }
    public LogicalOperator LogicalOperator { get; init; };
}
```
```csharp
public sealed class PolicyScope
{
}
    public string[]? TenantIds { get; init; }
    public string[]? PathPrefixes { get; init; }
    public string[]? ContentTypes { get; init; }
    public ClassificationLabel[]? Classifications { get; init; }
    public string[]? Tags { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
}
```
```csharp
public sealed class LifecycleStats
{
}
    public long TotalEvaluated { get; set; }
    public Dictionary<LifecycleAction, long> ActionCounts { get; set; };
    public long TotalBytesProcessed { get; set; }
    public long TotalBytesFreed { get; set; }
    public long TotalBytesArchived { get; set; }
    public long TotalBytesMigrated { get; set; }
    public long PoliciesExecuted { get; set; }
    public long FailedOperations { get; set; }
    public DateTime? LastEvaluationTime { get; set; }
    public double AverageEvaluationTimeMs { get; set; }
    public long PendingActions { get; set; }
    public long ObjectsOnHold { get; set; }
}
```
```csharp
public sealed class LifecycleDataObject
{
}
    public required string ObjectId { get; init; }
    public string? Path { get; init; }
    public string? ContentType { get; init; }
    public long Size { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? LastModifiedAt { get; init; }
    public DateTime? LastAccessedAt { get; init; }
    public int Version { get; init; };
    public bool IsLatestVersion { get; init; };
    public string? TenantId { get; init; }
    public string[]? Tags { get; init; }
    public ClassificationLabel Classification { get; init; };
    public string? StorageTier { get; init; }
    public string? StorageLocation { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool IsOnHold { get; init; }
    public string? HoldId { get; init; }
    public bool IsArchived { get; init; }
    public bool IsSoftDeleted { get; init; }
    public DateTime? SoftDeletedAt { get; init; }
    public string? ContentHash { get; init; }
    public bool IsEncrypted { get; init; }
    public string? EncryptionKeyId { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public string[]? RelatedObjectIds { get; init; }
    public TimeSpan Age;;
    public TimeSpan? TimeSinceLastAccess;;
    public bool IsExpired;;
    public TimeSpan? TimeUntilExpiration;;
}
```
```csharp
public interface ILifecycleStrategy : IDataManagementStrategy
{
}
    Task<LifecycleDecision> EvaluateAsync(LifecycleDataObject data, CancellationToken ct);;
    Task<int> ExecutePolicyAsync(LifecyclePolicy policy, CancellationToken ct);;
    Task<LifecycleStats> GetStatsAsync(CancellationToken ct);;
}
```
```csharp
public abstract class LifecycleStrategyBase : DataManagementStrategyBase, ILifecycleStrategy
{
}
    protected readonly BoundedDictionary<string, LifecycleDataObject> TrackedObjects = new BoundedDictionary<string, LifecycleDataObject>(1000);
    protected readonly BoundedDictionary<string, LifecyclePolicy> Policies = new BoundedDictionary<string, LifecyclePolicy>(1000);
    protected readonly BoundedDictionary<string, LifecycleDecision> PendingActions = new BoundedDictionary<string, LifecycleDecision>(1000);
    public override DataManagementCategory Category;;
    public async Task<LifecycleDecision> EvaluateAsync(LifecycleDataObject data, CancellationToken ct);
    public async Task<int> ExecutePolicyAsync(LifecyclePolicy policy, CancellationToken ct);
    public Task<LifecycleStats> GetStatsAsync(CancellationToken ct);
    protected abstract Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);;
    protected virtual async Task<int> ExecutePolicyCoreAsync(LifecyclePolicy policy, CancellationToken ct);
    protected virtual IEnumerable<LifecycleDataObject> GetObjectsInScope(PolicyScope? scope);
    protected virtual bool EvaluatePolicyConditions(LifecycleDataObject obj, List<PolicyCondition> conditions);
    protected virtual bool EvaluateSingleCondition(LifecycleDataObject obj, PolicyCondition condition);
    protected virtual object? GetFieldValue(LifecycleDataObject obj, string field);
    protected static int CompareValues(object? a, object? b);
    protected virtual Task<int> ApplyDecisionAsync(LifecycleDataObject obj, LifecycleDecision decision, CancellationToken ct);
    protected void UpdateEvaluationStats(LifecycleDecision decision, double timeMs);
    public void TrackObject(LifecycleDataObject obj);
    public bool UntrackObject(string objectId);
    public void RegisterPolicy(LifecyclePolicy policy);
    public bool UnregisterPolicy(string policyId);
    public IEnumerable<LifecyclePolicy> GetPolicies();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/DataMigrationStrategy.cs
```csharp
public sealed class MigrationJob
{
}
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public required string SourceProvider { get; init; }
    public string? SourceLocation { get; init; }
    public required string TargetProvider { get; init; }
    public string? TargetLocation { get; init; }
    public List<string>? ObjectIds { get; init; }
    public PolicyScope? Scope { get; init; }
    public MigrationFormat TargetFormat { get; init; };
    public bool EnableCompression { get; init; }
    public bool VerifyAfterMigration { get; init; };
    public bool DeleteSourceOnSuccess { get; init; }
    public int MaxParallelism { get; init; };
    public int RetryCount { get; init; };
    public int Priority { get; init; };
    public DateTime CreatedAt { get; init; };
    public DateTime? ScheduledAt { get; init; }
}
```
```csharp
public sealed class MigrationProgress
{
}
    public required string JobId { get; init; }
    public MigrationStatus Status { get; set; };
    public long TotalObjects { get; set; }
    public long CompletedObjects { get; set; }
    public long FailedObjects { get; set; }
    public long SkippedObjects { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? LastError { get; set; }
    public string? LastCheckpoint { get; set; }
    public double PercentComplete;;
    public bool CanResume;;
}
```
```csharp
public sealed class MigrationResult
{
}
    public required string ObjectId { get; init; }
    public required bool Success { get; init; }
    public string? SourceLocation { get; init; }
    public string? TargetLocation { get; init; }
    public long BytesTransferred { get; init; }
    public TimeSpan Duration { get; init; }
    public bool FormatConverted { get; init; }
    public MigrationFormat? NewFormat { get; init; }
    public bool? Verified { get; init; }
    public string? ErrorMessage { get; init; }
    public int RetryCount { get; init; }
}
```
```csharp
public sealed class DataMigrationStrategy : LifecycleStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task DisposeCoreAsync();
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);
    public async Task<string> StartMigrationAsync(MigrationJob job, CancellationToken ct = default);
    public MigrationProgress? GetProgress(string jobId);
    public bool PauseMigration(string jobId);
    public async Task<bool> ResumeMigrationAsync(string jobId, CancellationToken ct = default);
    public bool CancelMigration(string jobId);
    public async Task<int> RollbackMigrationAsync(string jobId, CancellationToken ct = default);
    public IReadOnlyList<MigrationResult> GetResults(string jobId);
    public IEnumerable<MigrationJob> GetAllJobs();
    public async Task<MigrationResult> MigrateSingleObjectAsync(string objectId, string targetProvider, string? targetLocation = null, MigrationFormat format = MigrationFormat.Original, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/LifecyclePolicyEngineStrategy.cs
```csharp
public sealed class PolicyValidationResult
{
}
    public required bool IsValid { get; init; }
    public List<string> Errors { get; init; };
    public List<string> Warnings { get; init; };
    public List<string> ConflictingPolicies { get; init; };
    public static PolicyValidationResult Valid();;
    public static PolicyValidationResult Invalid(params string[] errors);;
}
```
```csharp
public sealed class PolicyExecutionResult
{
}
    public required string PolicyId { get; init; }
    public required bool Success { get; init; }
    public int ObjectsAffected { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime ExecutedAt { get; init; };
    public string? ErrorMessage { get; init; }
    public DateTime? NextScheduledExecution { get; init; }
}
```
```csharp
public sealed class PolicyScheduleEntry
{
}
    public required string PolicyId { get; init; }
    public required string CronExpression { get; init; }
    public DateTime NextExecution { get; set; }
    public DateTime? LastExecution { get; set; }
    public bool IsActive { get; set; };
}
```
```csharp
public sealed class LifecyclePolicyEngineStrategy : LifecycleStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);
    protected override async Task<int> ExecutePolicyCoreAsync(LifecyclePolicy policy, CancellationToken ct);
    public PolicyValidationResult ValidatePolicy(LifecyclePolicy policy);
    public PolicyValidationResult RegisterPolicyWithValidation(LifecyclePolicy policy, bool force = false);
    public PolicyExecutionResult? GetLastExecutionResult(string policyId);
    public IEnumerable<PolicyScheduleEntry> GetScheduledPolicies();
    public async Task<int> ExecuteAllPoliciesAsync(CancellationToken ct = default);
    public IReadOnlyList<string> GetConflictingPolicies(string policyId);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/DataExpirationStrategy.cs
```csharp
public sealed class ExpirationEvent
{
}
    public required string EventId { get; init; }
    public required string ObjectId { get; init; }
    public required ExpirationEventType EventType { get; init; }
    public DateTime OccurredAt { get; init; };
    public DateTime? OriginalExpirationDate { get; init; }
    public DateTime? NewExpirationDate { get; init; }
    public int? DaysUntilExpiration { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}
```
```csharp
public sealed class ExpirationPolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public required TimeSpan DefaultTtl { get; init; }
    public TimeSpan GracePeriod { get; init; };
    public bool SoftDeleteFirst { get; init; };
    public int[] WarningThresholds { get; init; };
    public int MaxExtensions { get; init; };
    public TimeSpan ExtensionDuration { get; init; };
    public PolicyScope? Scope { get; init; }
    public bool Enabled { get; init; };
    public int BatchSize { get; init; };
    public List<string>? NotificationChannels { get; init; }
}
```
```csharp
public sealed class ExpirationTracker
{
}
    public required string ObjectId { get; init; }
    public DateTime ExpiresAt { get; set; }
    public DateTime OriginalExpiresAt { get; init; }
    public int ExtensionsGranted { get; set; }
    public bool InGracePeriod { get; set; }
    public DateTime? GracePeriodStartedAt { get; set; }
    public bool IsSoftDeleted { get; set; }
    public DateTime? SoftDeletedAt { get; set; }
    public HashSet<int> WarningsSent { get; init; };
    public string? PolicyId { get; init; }
    public bool IsExpired;;
    public TimeSpan? TimeUntilExpiration;;
    public int? DaysUntilExpiration;;
}
```
```csharp
public sealed class ExpirationResult
{
}
    public required string ObjectId { get; init; }
    public required ExpirationEventType ActionTaken { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime? NextExpirationDate { get; init; }
}
```
```csharp
public sealed class BulkExpirationResult
{
}
    public int TotalProcessed { get; set; }
    public int SoftDeleted { get; set; }
    public int HardDeleted { get; set; }
    public int EnteredGracePeriod { get; set; }
    public int WarningsSent { get; set; }
    public int Failures { get; set; }
    public int Skipped { get; set; }
    public TimeSpan Duration { get; set; }
    public List<ExpirationResult> Results { get; init; };
}
```
```csharp
public sealed class DataExpirationStrategy : LifecycleStrategyBase
{
}
    public ExpirationPolicy? DefaultPolicy { get; set; }
    public TimeSpan ProcessingInterval { get; set; };
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);
    public void SetExpiration(string objectId, DateTime expiresAt, string? policyId = null);
    public void SetTtl(string objectId, TimeSpan ttl, string? policyId = null);
    public bool ExtendExpiration(string objectId, TimeSpan? extension = null);
    public bool CancelExpiration(string objectId);
    public async Task<BulkExpirationResult> ProcessExpiredObjectsAsync(CancellationToken ct = default);
    public void RegisterPolicy(ExpirationPolicy policy);
    public void SubscribeToEvents(Action<ExpirationEvent> handler);
    public ExpirationTracker? GetTracker(string objectId);
    public IEnumerable<ExpirationTracker> GetExpiringWithin(TimeSpan within);
    public IEnumerable<ExpirationTracker> GetExpiredObjects();
    public Dictionary<string, object> GetExpirationStats();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/DataArchivalStrategy.cs
```csharp
public sealed class ArchiveMetadata
{
}
    public required string ObjectId { get; init; }
    public required string ArchiveId { get; init; }
    public string? OriginalPath { get; init; }
    public string? OriginalContentType { get; init; }
    public long OriginalSize { get; init; }
    public long ArchivedSize { get; init; }
    public string? OriginalHash { get; init; }
    public ArchiveTier Tier { get; init; }
    public ArchiveCompression Compression { get; init; }
    public ArchiveEncryption Encryption { get; init; }
    public string? EncryptionKeyId { get; init; }
    public DateTime ArchivedAt { get; init; };
    public DateTime OriginalCreatedAt { get; init; }
    public DateTime? OriginalLastModifiedAt { get; init; }
    public string[]? OriginalTags { get; init; }
    public ClassificationLabel OriginalClassification { get; init; }
    public TimeSpan? RetentionPeriod { get; init; }
    public Dictionary<string, object>? PreservedMetadata { get; init; }
    public string? ArchiveLocation { get; init; }
}
```
```csharp
public sealed class ArchiveRetrievalRequest
{
}
    public required string ArchiveId { get; init; }
    public RetrievalTier Tier { get; init; };
    public string? TargetLocation { get; init; }
    public TimeSpan RestoreDuration { get; init; };
    public string? CallbackUrl { get; init; }
}
```
```csharp
public sealed class ArchiveRetrievalStatus
{
}
    public required string ArchiveId { get; init; }
    public required string RequestId { get; init; }
    public RetrievalStatus Status { get; set; }
    public DateTime? EstimatedCompletion { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? TargetLocation { get; set; }
    public string? ErrorMessage { get; set; }
    public long BytesRestored { get; set; }
}
```
```csharp
public sealed class DataArchivalStrategy : LifecycleStrategyBase
{
}
    public ArchiveCompression DefaultCompression { get; set; };
    public ArchiveEncryption DefaultEncryption { get; set; };
    public ArchiveTier DefaultTier { get; set; };
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task DisposeCoreAsync();
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);
    public async Task<ArchiveMetadata> ArchiveAsync(LifecycleDataObject data, ArchiveTier? tier = null, ArchiveCompression? compression = null, ArchiveEncryption? encryption = null, TimeSpan? retentionPeriod = null, CancellationToken ct = default);
    public async Task<ArchiveRetrievalStatus> InitiateRetrievalAsync(ArchiveRetrievalRequest request, CancellationToken ct = default);
    public ArchiveRetrievalStatus? GetRetrievalStatus(string requestId);
    public ArchiveMetadata? GetArchiveMetadata(string archiveId);
    public IEnumerable<ArchiveMetadata> GetArchivesForObject(string objectId);
    public TimeSpan GetEstimatedRestoreTime(ArchiveTier tier, RetrievalTier retrievalTier = RetrievalTier.Standard);
    public Dictionary<string, object> GetArchivalStats();
    public async Task<IEnumerable<ArchiveMetadata>> BatchArchiveAsync(IEnumerable<LifecycleDataObject> objects, ArchiveTier? tier = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Lifecycle/DataClassificationStrategy.cs
```csharp
public sealed class ClassificationRule
{
}
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required ClassificationLabel Label { get; init; }
    public int Priority { get; init; };
    public List<string>? ContentPatterns { get; init; }
    public Dictionary<string, string>? MetadataPatterns { get; init; }
    public List<string>? PathPatterns { get; init; }
    public List<string>? ContentTypes { get; init; }
    public List<string>? IndicativeTags { get; init; }
    public double MinConfidence { get; init; };
    public bool Enabled { get; init; };
}
```
```csharp
public sealed class ClassificationResult
{
}
    public required string ObjectId { get; init; }
    public ClassificationLabel PreviousLabel { get; init; }
    public required ClassificationLabel NewLabel { get; init; }
    public double Confidence { get; init; }
    public List<string> MatchedRules { get; init; };
    public List<string> DetectedPatterns { get; init; };
    public bool Changed;;
    public bool UsedMlClassification { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}
```
```csharp
public sealed class DataClassificationStrategy : LifecycleStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);
    public async Task<ClassificationResult> ClassifyAsync(LifecycleDataObject data, CancellationToken ct);
    public void RegisterRule(ClassificationRule rule);
    public bool RemoveRule(string ruleId);
    public IEnumerable<ClassificationRule> GetRules();
    public IReadOnlyDictionary<ClassificationLabel, long> GetLabelDistribution();
    public async Task<IEnumerable<ClassificationResult>> BatchClassifyAsync(IEnumerable<LifecycleDataObject> objects, CancellationToken ct = default);
    public ClassificationResult? GetCachedClassification(string objectId);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Branching/BranchingStrategyBase.cs
```csharp
public sealed class DataBlock
{
}
    public required string BlockId { get; init; }
    public required string ContentHash { get; init; }
    public byte[]? Data { get; set; }
    public string? ParentBlockId { get; init; }
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; };
    public int ReferenceCount { get => _referenceCount; set => _referenceCount = value; }
    public int IncrementReferenceCount();;
    public int DecrementReferenceCount();;
    public bool IsCoWReference;;
}
```
```csharp
public sealed class DataBranch
{
}
    public required string BranchId { get; init; }
    public required string Name { get; init; }
    public required string ObjectId { get; init; }
    public string? ParentBranchId { get; init; }
    public string? BranchPoint { get; init; }
    public BranchStatus Status { get; set; };
    public DateTime CreatedAt { get; init; };
    public DateTime ModifiedAt { get; set; };
    public string? CreatedBy { get; init; }
    public string? Description { get; init; }
    public HashSet<string> BlockIds { get; };
    public Dictionary<string, string> Tags { get; };
    public Dictionary<string, object> Metadata { get; };
    public long CommitCount { get; set; }
}
```
```csharp
public sealed record BranchInfo
{
}
    public required string BranchId { get; init; }
    public required string Name { get; init; }
    public required string ObjectId { get; init; }
    public string? ParentBranch { get; init; }
    public BranchStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
    public string? CreatedBy { get; init; }
    public long SizeBytes { get; init; }
    public int BlockCount { get; init; }
    public long CommitCount { get; init; }
    public bool IsDefault { get; init; }
}
```
```csharp
public sealed class MergeConflict
{
}
    public required string ConflictId { get; init; }
    public ConflictType Type { get; init; }
    public required string BlockId { get; init; }
    public byte[]? SourceData { get; init; }
    public byte[]? TargetData { get; init; }
    public byte[]? AncestorData { get; init; }
    public byte[]? ResolvedData { get; set; }
    public ConflictResolutionStrategy? Resolution { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public bool IsResolved;;
}
```
```csharp
public sealed class MergeResult
{
}
    public required bool Success { get; init; }
    public bool HasConflicts { get; init; }
    public List<MergeConflict> Conflicts { get; init; };
    public string? MergeCommitId { get; init; }
    public string? Message { get; init; }
    public int BlocksMerged { get; init; }
    public double MergeTimeMs { get; init; }
}
```
```csharp
public sealed class BranchDiff
{
}
    public required string SourceBranchId { get; init; }
    public required string TargetBranchId { get; init; }
    public List<string> AddedBlocks { get; init; };
    public List<string> RemovedBlocks { get; init; };
    public List<string> ModifiedBlocks { get; init; };
    public long SizeDifference { get; init; }
    public bool IsIdentical;;
    public string? CommonAncestor { get; init; }
    public string? Summary { get; init; }
}
```
```csharp
public sealed class PullRequest
{
}
    public required string PullRequestId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string SourceBranchId { get; init; }
    public required string TargetBranchId { get; init; }
    public PullRequestStatus Status { get; set; };
    public required string CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime UpdatedAt { get; set; };
    public List<string> Reviewers { get; init; };
    public List<PullRequestApproval> Approvals { get; };
    public List<PullRequestComment> Comments { get; };
    public List<string> Labels { get; init; };
    public MergeResult? MergeResult { get; set; }
}
```
```csharp
public sealed record PullRequestApproval
{
}
    public required string ApprovedBy { get; init; }
    public DateTime ApprovedAt { get; init; };
    public string? Comment { get; init; }
}
```
```csharp
public sealed record PullRequestComment
{
}
    public required string CommentId { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }
    public DateTime CreatedAt { get; init; };
    public string? BlockId { get; init; }
}
```
```csharp
public sealed record BranchPermissionEntry
{
}
    public required string PrincipalId { get; init; }
    public bool IsRole { get; init; }
    public BranchPermission Permissions { get; init; }
    public DateTime GrantedAt { get; init; };
    public string? GrantedBy { get; init; }
}
```
```csharp
public sealed class BranchTreeNode
{
}
    public required BranchInfo Branch { get; init; }
    public List<BranchTreeNode> Children { get; init; };
    public int Depth { get; init; }
    public string? TreePrefix { get; init; }
}
```
```csharp
public sealed record GarbageCollectionStats
{
}
    public long BlocksScanned { get; init; }
    public long OrphanedBlocks { get; init; }
    public long BytesReclaimed { get; init; }
    public int BranchesCleaned { get; init; }
    public double DurationMs { get; init; }
    public DateTime CompletedAt { get; init; };
    public List<string> Errors { get; init; };
}
```
```csharp
public sealed class GarbageCollectionOptions
{
}
    public TimeSpan MinimumAge { get; init; };
    public bool DryRun { get; init; }
    public int MaxBlocksPerRun { get; init; };
    public bool Compact { get; init; };
    public bool RunAsync { get; init; };
}
```
```csharp
public sealed class CreateBranchOptions
{
}
    public string? Description { get; init; }
    public string? CreatedBy { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
    public bool InheritPermissions { get; init; };
}
```
```csharp
public interface IBranchingStrategy : IDataManagementStrategy
{
}
    Task<BranchInfo> CreateBranchAsync(string objectId, string branchName, string? fromBranch = null, string? fromVersion = null, CreateBranchOptions? options = null, CancellationToken ct = default);;
    Task<BranchInfo?> GetBranchAsync(string objectId, string branchName, CancellationToken ct = default);;
    Task<IEnumerable<BranchInfo>> ListBranchesAsync(string objectId, bool includeDeleted = false, CancellationToken ct = default);;
    Task<bool> DeleteBranchAsync(string objectId, string branchName, CancellationToken ct = default);;
    Task<string> WriteBlockAsync(string objectId, string branchName, byte[] data, CancellationToken ct = default);;
    Task<byte[]?> ReadBlockAsync(string objectId, string branchName, string blockId, CancellationToken ct = default);;
    Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsAsync(string objectId, CancellationToken ct = default);;
    Task<BranchDiff> DiffBranchesAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);;
    Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);;
    Task<MergeResult> ThreeWayMergeAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct = default);;
    Task<IEnumerable<MergeConflict>> GetConflictsAsync(string objectId, string mergeId, CancellationToken ct = default);;
    Task<bool> ResolveConflictAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution = null, string? resolvedBy = null, CancellationToken ct = default);;
    Task<int> AutoResolveConflictsAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct = default);;
    Task<BranchTreeNode> GetBranchTreeAsync(string objectId, CancellationToken ct = default);;
    Task<string> GetBranchTreeTextAsync(string objectId, CancellationToken ct = default);;
    Task<PullRequest> CreatePullRequestAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description = null, CancellationToken ct = default);;
    Task<PullRequest?> GetPullRequestAsync(string objectId, string pullRequestId, CancellationToken ct = default);;
    Task<IEnumerable<PullRequest>> ListPullRequestsAsync(string objectId, PullRequestStatus? status = null, CancellationToken ct = default);;
    Task<bool> ApprovePullRequestAsync(string objectId, string pullRequestId, string approver, string? comment = null, CancellationToken ct = default);;
    Task<MergeResult> MergePullRequestAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct = default);;
    Task<bool> ClosePullRequestAsync(string objectId, string pullRequestId, string closedBy, string? reason = null, CancellationToken ct = default);;
    Task<bool> SetBranchPermissionsAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole = false, string? grantedBy = null, CancellationToken ct = default);;
    Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsAsync(string objectId, string branchName, CancellationToken ct = default);;
    Task<bool> HasPermissionAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct = default);;
    Task<bool> RemovePermissionsAsync(string objectId, string branchName, string principalId, CancellationToken ct = default);;
    Task<GarbageCollectionStats> RunGarbageCollectionAsync(string objectId, GarbageCollectionOptions? options = null, CancellationToken ct = default);;
    Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageAsync(string objectId, CancellationToken ct = default);;
}
```
```csharp
public abstract class BranchingStrategyBase : DataManagementStrategyBase, IBranchingStrategy
{
#endregion
}
    protected const string DefaultBranchName = "main";
    public override DataManagementCategory Category;;
    public async Task<BranchInfo> CreateBranchAsync(string objectId, string branchName, string? fromBranch = null, string? fromVersion = null, CreateBranchOptions? options = null, CancellationToken ct = default);
    public async Task<BranchInfo?> GetBranchAsync(string objectId, string branchName, CancellationToken ct = default);
    public async Task<IEnumerable<BranchInfo>> ListBranchesAsync(string objectId, bool includeDeleted = false, CancellationToken ct = default);
    public async Task<bool> DeleteBranchAsync(string objectId, string branchName, CancellationToken ct = default);
    public async Task<string> WriteBlockAsync(string objectId, string branchName, byte[] data, CancellationToken ct = default);
    public async Task<byte[]?> ReadBlockAsync(string objectId, string branchName, string blockId, CancellationToken ct = default);
    public async Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsAsync(string objectId, CancellationToken ct = default);
    public async Task<BranchDiff> DiffBranchesAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);
    public async Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);
    public async Task<MergeResult> ThreeWayMergeAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct = default);
    public async Task<IEnumerable<MergeConflict>> GetConflictsAsync(string objectId, string mergeId, CancellationToken ct = default);
    public async Task<bool> ResolveConflictAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution = null, string? resolvedBy = null, CancellationToken ct = default);
    public async Task<int> AutoResolveConflictsAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct = default);
    public async Task<BranchTreeNode> GetBranchTreeAsync(string objectId, CancellationToken ct = default);
    public async Task<string> GetBranchTreeTextAsync(string objectId, CancellationToken ct = default);
    public async Task<PullRequest> CreatePullRequestAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description = null, CancellationToken ct = default);
    public async Task<PullRequest?> GetPullRequestAsync(string objectId, string pullRequestId, CancellationToken ct = default);
    public async Task<IEnumerable<PullRequest>> ListPullRequestsAsync(string objectId, PullRequestStatus? status = null, CancellationToken ct = default);
    public async Task<bool> ApprovePullRequestAsync(string objectId, string pullRequestId, string approver, string? comment = null, CancellationToken ct = default);
    public async Task<MergeResult> MergePullRequestAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct = default);
    public async Task<bool> ClosePullRequestAsync(string objectId, string pullRequestId, string closedBy, string? reason = null, CancellationToken ct = default);
    public async Task<bool> SetBranchPermissionsAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole = false, string? grantedBy = null, CancellationToken ct = default);
    public async Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsAsync(string objectId, string branchName, CancellationToken ct = default);
    public async Task<bool> HasPermissionAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct = default);
    public async Task<bool> RemovePermissionsAsync(string objectId, string branchName, string principalId, CancellationToken ct = default);
    public async Task<GarbageCollectionStats> RunGarbageCollectionAsync(string objectId, GarbageCollectionOptions? options = null, CancellationToken ct = default);
    public async Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageAsync(string objectId, CancellationToken ct = default);
    protected abstract Task<BranchInfo> CreateBranchCoreAsync(string objectId, string branchName, string fromBranch, string? fromVersion, CreateBranchOptions options, CancellationToken ct);;
    protected abstract Task<BranchInfo?> GetBranchCoreAsync(string objectId, string branchName, CancellationToken ct);;
    protected abstract Task<IEnumerable<BranchInfo>> ListBranchesCoreAsync(string objectId, bool includeDeleted, CancellationToken ct);;
    protected abstract Task<bool> DeleteBranchCoreAsync(string objectId, string branchName, CancellationToken ct);;
    protected abstract Task<string> WriteBlockCoreAsync(string objectId, string branchName, byte[] data, CancellationToken ct);;
    protected abstract Task<byte[]?> ReadBlockCoreAsync(string objectId, string branchName, string blockId, CancellationToken ct);;
    protected abstract Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsCoreAsync(string objectId, CancellationToken ct);;
    protected abstract Task<BranchDiff> DiffBranchesCoreAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct);;
    protected abstract Task<MergeResult> MergeBranchCoreAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct);;
    protected abstract Task<MergeResult> ThreeWayMergeCoreAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct);;
    protected abstract Task<IEnumerable<MergeConflict>> GetConflictsCoreAsync(string objectId, string mergeId, CancellationToken ct);;
    protected abstract Task<bool> ResolveConflictCoreAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution, string? resolvedBy, CancellationToken ct);;
    protected abstract Task<int> AutoResolveConflictsCoreAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct);;
    protected abstract Task<BranchTreeNode> GetBranchTreeCoreAsync(string objectId, CancellationToken ct);;
    protected abstract Task<string> GetBranchTreeTextCoreAsync(string objectId, CancellationToken ct);;
    protected abstract Task<PullRequest> CreatePullRequestCoreAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description, CancellationToken ct);;
    protected abstract Task<PullRequest?> GetPullRequestCoreAsync(string objectId, string pullRequestId, CancellationToken ct);;
    protected abstract Task<IEnumerable<PullRequest>> ListPullRequestsCoreAsync(string objectId, PullRequestStatus? status, CancellationToken ct);;
    protected abstract Task<bool> ApprovePullRequestCoreAsync(string objectId, string pullRequestId, string approver, string? comment, CancellationToken ct);;
    protected abstract Task<MergeResult> MergePullRequestCoreAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct);;
    protected abstract Task<bool> ClosePullRequestCoreAsync(string objectId, string pullRequestId, string closedBy, string? reason, CancellationToken ct);;
    protected abstract Task<bool> SetBranchPermissionsCoreAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole, string? grantedBy, CancellationToken ct);;
    protected abstract Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsCoreAsync(string objectId, string branchName, CancellationToken ct);;
    protected abstract Task<bool> HasPermissionCoreAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct);;
    protected abstract Task<bool> RemovePermissionsCoreAsync(string objectId, string branchName, string principalId, CancellationToken ct);;
    protected abstract Task<GarbageCollectionStats> RunGarbageCollectionCoreAsync(string objectId, GarbageCollectionOptions options, CancellationToken ct);;
    protected abstract Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageCoreAsync(string objectId, CancellationToken ct);;
    protected static string ComputeHash(byte[] data);
    protected static string GenerateId();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Branching/GitForDataBranchingStrategy.cs
```csharp
public sealed class GitForDataBranchingStrategy : BranchingStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task<BranchInfo> CreateBranchCoreAsync(string objectId, string branchName, string fromBranch, string? fromVersion, CreateBranchOptions options, CancellationToken ct);
    protected override Task<BranchInfo?> GetBranchCoreAsync(string objectId, string branchName, CancellationToken ct);
    protected override Task<IEnumerable<BranchInfo>> ListBranchesCoreAsync(string objectId, bool includeDeleted, CancellationToken ct);
    protected override Task<bool> DeleteBranchCoreAsync(string objectId, string branchName, CancellationToken ct);
    protected override Task<string> WriteBlockCoreAsync(string objectId, string branchName, byte[] data, CancellationToken ct);
    protected override Task<byte[]?> ReadBlockCoreAsync(string objectId, string branchName, string blockId, CancellationToken ct);
    protected override Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsCoreAsync(string objectId, CancellationToken ct);
    protected override Task<BranchDiff> DiffBranchesCoreAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct);
    protected override Task<MergeResult> MergeBranchCoreAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct);
    protected override Task<MergeResult> ThreeWayMergeCoreAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct);
    protected override Task<IEnumerable<MergeConflict>> GetConflictsCoreAsync(string objectId, string mergeId, CancellationToken ct);
    protected override Task<bool> ResolveConflictCoreAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution, string? resolvedBy, CancellationToken ct);
    protected override Task<int> AutoResolveConflictsCoreAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct);
    protected override Task<BranchTreeNode> GetBranchTreeCoreAsync(string objectId, CancellationToken ct);
    protected override Task<string> GetBranchTreeTextCoreAsync(string objectId, CancellationToken ct);
    protected override Task<PullRequest> CreatePullRequestCoreAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description, CancellationToken ct);
    protected override Task<PullRequest?> GetPullRequestCoreAsync(string objectId, string pullRequestId, CancellationToken ct);
    protected override Task<IEnumerable<PullRequest>> ListPullRequestsCoreAsync(string objectId, PullRequestStatus? status, CancellationToken ct);
    protected override Task<bool> ApprovePullRequestCoreAsync(string objectId, string pullRequestId, string approver, string? comment, CancellationToken ct);
    protected override async Task<MergeResult> MergePullRequestCoreAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct);
    protected override Task<bool> ClosePullRequestCoreAsync(string objectId, string pullRequestId, string closedBy, string? reason, CancellationToken ct);
    protected override Task<bool> SetBranchPermissionsCoreAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole, string? grantedBy, CancellationToken ct);
    protected override Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsCoreAsync(string objectId, string branchName, CancellationToken ct);
    protected override Task<bool> HasPermissionCoreAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct);
    protected override Task<bool> RemovePermissionsCoreAsync(string objectId, string branchName, string principalId, CancellationToken ct);
    protected override Task<GarbageCollectionStats> RunGarbageCollectionCoreAsync(string objectId, GarbageCollectionOptions options, CancellationToken ct);
    protected override Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class ObjectStore
{
}
    public BoundedDictionary<string, DataBranch> Branches { get; };
    public BoundedDictionary<string, DataBlock> BlocksByHash { get; };
    public BoundedDictionary<string, string> BlockIdToHash { get; };
    public BoundedDictionary<string, PullRequest> PullRequests { get; };
    public BoundedDictionary<string, BoundedDictionary<string, BranchPermissionEntry>> Permissions { get; };
    public BoundedDictionary<string, List<MergeConflict>> PendingConflicts { get; };
    public BoundedDictionary<string, DateTime> DeletedBranches { get; };
    public ObjectStore(string objectId);
    public object Lock;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/SpatialIndexStrategy.cs
```csharp
public sealed class SpatialIndexStrategy : IndexingStrategyBase
{
}
    public enum GeometryType;
    public readonly struct Point2D : IEquatable<Point2D>;
    public readonly struct BoundingBox;
    public sealed class Geometry;
    public SpatialIndexStrategy() : this(maxEntriesPerNode: 10, minEntriesPerNode: 4);
    public SpatialIndexStrategy(int maxEntriesPerNode = 10, int minEntriesPerNode = 4);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    public List<IndexSearchResult> SearchBoundingBox(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchNearestNeighbors(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchWithinDistance(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchContains(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchIntersects(Dictionary<string, string> @params, IndexSearchOptions options);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
public readonly struct Point2D : IEquatable<Point2D>
{
}
    public double X { get; }
    public double Y { get; }
    public Point2D(double x, double y);
    public double DistanceTo(Point2D other);
    public double HaversineDistanceTo(Point2D other);
    public bool Equals(Point2D other);;
    public override bool Equals(object? obj);;
    public override int GetHashCode();;
    public static bool operator ==(Point2D left, Point2D right) => left.Equals(right);;
    public static bool operator !=(Point2D left, Point2D right) => !left.Equals(right);;
}
```
```csharp
public readonly struct BoundingBox
{
}
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public BoundingBox(double minX, double minY, double maxX, double maxY);
    public static BoundingBox FromPoint(Point2D point);;
    public static BoundingBox FromPoints(IEnumerable<Point2D> points);
    public double Area;;
    public Point2D Center;;
    public bool Intersects(BoundingBox other);;
    public bool Contains(BoundingBox other);;
    public bool Contains(Point2D point);;
    public BoundingBox Union(BoundingBox other);;
    public double DistanceTo(Point2D point);
}
```
```csharp
public sealed class Geometry
{
}
    public required GeometryType Type { get; init; }
    public required Point2D[] Coordinates { get; init; }
    public Point2D[][]? Rings { get; init; }
    public BoundingBox BoundingBox { get; init; }
    public static Geometry CreatePoint(double x, double y);
    public static Geometry CreateLineString(IEnumerable<Point2D> points);
    public static Geometry CreatePolygon(IEnumerable<Point2D> exteriorRing, IEnumerable<Point2D[]>? holes = null);
    public bool ContainsPoint(Point2D point);
    public bool Intersects(Geometry other);
}
```
```csharp
private sealed class SpatialDocument
{
}
    public required string ObjectId { get; init; }
    public required Geometry Geometry { get; init; }
    public required Dictionary<string, object>? Metadata { get; init; }
    public required string? GeoJson { get; init; }
    public DateTime IndexedAt { get; init; };
}
```
```csharp
private sealed class RTreeNode
{
}
    public BoundingBox Bounds { get; set; }
    public List<RTreeEntry> Entries { get; };
    public List<RTreeNode> Children { get; };
    public bool IsLeaf;;
}
```
```csharp
private sealed class RTreeEntry
{
}
    public required string ObjectId { get; init; }
    public required BoundingBox Bounds { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/CompositeIndexStrategy.cs
```csharp
public sealed class CompositeIndexStrategy : IndexingStrategyBase
{
}
    public sealed class CompositeIndex;
    public sealed class IndexField;
    public enum SortOrder;
    public sealed class CompositeKey : IComparable<CompositeKey>;
    public sealed class IndexStatistics;
    public CompositeIndexStrategy() : this(new[] { "_id", "_type" });
    public CompositeIndexStrategy(IEnumerable<string> defaultIndexFields);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    public CompositeIndex CreateIndex(string name, List<IndexField> fields, bool isUnique = false, bool isCovering = false);
    public CompositeIndex CreateIndex(string name, params string[] fieldNames);
    public bool DropIndex(string name);
    public IReadOnlyList<string> GetIndexNames();;
    public IndexStatistics? GetIndexStatistics(string indexName);
    public IReadOnlyDictionary<string, IndexStatistics> GetAllStatistics();
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    public List<IndexSearchResult> SearchWithIntersection(IEnumerable<string> indexNames, Dictionary<string, string> queryParams, IndexSearchOptions options);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
    public override Task OptimizeAsync(CancellationToken ct = default);
}
```
```csharp
private sealed class IndexedDocument
{
}
    public required string ObjectId { get; init; }
    public required Dictionary<string, object?> FieldValues { get; init; }
    public required string? TextContent { get; init; }
    public DateTime IndexedAt { get; init; };
}
```
```csharp
public sealed class CompositeIndex
{
}
    public required string Name { get; init; }
    public required List<IndexField> Fields { get; init; }
    public bool IsUnique { get; init; }
    public bool IsCovering { get; init; }
    internal SortedDictionary<CompositeKey, HashSet<string>> Tree { get; };
    internal object TreeLock { get; };
}
```
```csharp
public sealed class IndexField
{
}
    public required string Name { get; init; }
    public SortOrder Order { get; init; };
    public bool IndexNulls { get; init; };
}
```
```csharp
public sealed class CompositeKey : IComparable<CompositeKey>
{
}
    public object? [] Values { get; }
    public SortOrder[] SortOrders { get; }
    public CompositeKey(object? [] values, SortOrder[]? sortOrders = null);
    public int CompareTo(CompositeKey? other);
    public bool IsPrefixOf(CompositeKey other);
    public bool MatchesPrefix(CompositeKey prefix);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public override string ToString();
}
```
```csharp
public sealed class IndexStatistics
{
}
    public required string IndexName { get; init; }
    public long EntryCount { get; set; }
    public long UniqueKeyCount { get; set; }
    public double AverageEntriesPerKey;;
    public double Selectivity;;
    public long QueryCount { get => _queryCount; set => _queryCount = value; }
    public long ScanCount { get => _scanCount; set => _scanCount = value; }
    public double AverageScanEfficiency;;
    public DateTime LastUpdated { get; set; };
    public void IncrementQueryCount();;
    public void AddScanCount(long value);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/IndexingStrategyBase.cs
```csharp
public sealed class IndexResult
{
}
    public required bool Success { get; init; }
    public int DocumentCount { get; init; }
    public long TokenCount { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public static IndexResult Ok(int documentCount, long tokenCount, TimeSpan duration);;
    public static IndexResult Failed(string error);;
}
```
```csharp
public sealed class IndexSearchResult
{
}
    public required string ObjectId { get; init; }
    public double Score { get; init; }
    public string? Snippet { get; init; }
    public IReadOnlyList<(int Start, int End)>? Highlights { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class IndexSearchOptions
{
}
    public int MaxResults { get; init; };
    public double MinScore { get; init; };
    public bool IncludeSnippets { get; init; };
    public bool IncludeHighlights { get; init; };
    public Dictionary<string, object>? Filters { get; init; }
}
```
```csharp
public interface IIndexingStrategy : IDataManagementStrategy
{
}
    Task<IndexResult> IndexAsync(string objectId, IndexableContent content, CancellationToken ct = default);;
    Task<IndexResult> IndexBatchAsync(IEnumerable<(string ObjectId, IndexableContent Content)> batch, CancellationToken ct = default);;
    Task<IReadOnlyList<IndexSearchResult>> SearchAsync(string query, IndexSearchOptions? options = null, CancellationToken ct = default);;
    Task<bool> RemoveAsync(string objectId, CancellationToken ct = default);;
    Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);;
    long GetDocumentCount();;
    long GetIndexSize();;
    Task OptimizeAsync(CancellationToken ct = default);;
    Task ClearAsync(CancellationToken ct = default);;
}
```
```csharp
public abstract class IndexingStrategyBase : DataManagementStrategyBase, IIndexingStrategy
{
}
    public override DataManagementCategory Category;;
    public async Task<IndexResult> IndexAsync(string objectId, IndexableContent content, CancellationToken ct = default);
    public async Task<IndexResult> IndexBatchAsync(IEnumerable<(string ObjectId, IndexableContent Content)> batch, CancellationToken ct = default);
    public async Task<IReadOnlyList<IndexSearchResult>> SearchAsync(string query, IndexSearchOptions? options = null, CancellationToken ct = default);
    public async Task<bool> RemoveAsync(string objectId, CancellationToken ct = default);
    public abstract Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);;
    public abstract long GetDocumentCount();;
    public abstract long GetIndexSize();;
    public virtual Task OptimizeAsync(CancellationToken ct = default);;
    public abstract Task ClearAsync(CancellationToken ct = default);;
    protected abstract Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);;
    protected virtual async Task<IndexResult> IndexBatchCoreAsync(IEnumerable<(string ObjectId, IndexableContent Content)> batch, CancellationToken ct);
    protected abstract Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);;
    protected abstract Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);;
    protected static IEnumerable<string> Tokenize(string text);
    protected static double CalculateTfIdf(int termFrequency, int documentCount, int documentsWithTerm);
    protected static string CreateSnippet(string text, IEnumerable<string> matchingTerms, int contextLength = 50);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/SpatialAnchorStrategy.cs
```csharp
public sealed class SpatialAnchorStrategy : IndexingStrategyBase, ISpatialAnchorStrategy
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public SpatialAnchorCapabilities AnchorCapabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public async Task<SpatialAnchor> CreateAnchorAsync(string objectId, GpsCoordinate gpsPosition, VisualFeatureSignature? visualFeatures, int expirationDays, CancellationToken ct = default);
    public async Task<SpatialAnchor?> RetrieveAnchorAsync(string anchorId, CancellationToken ct = default);
    public async Task<ProximityVerificationResult> VerifyProximityAsync(string anchorId, GpsCoordinate currentPosition, double maxDistanceMeters, bool requireVisualMatch = false, CancellationToken ct = default);
    public async Task<IEnumerable<SpatialAnchor>> FindNearbyAnchorsAsync(GpsCoordinate position, double radiusMeters, int maxResults = 50, CancellationToken ct = default);
    public async Task<bool> DeleteAnchorAsync(string anchorId, CancellationToken ct = default);
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/MetadataIndexStrategy.cs
```csharp
public sealed class MetadataIndexStrategy : IndexingStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    protected override async Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class IndexedMetadata
{
}
    public required string ObjectId { get; init; }
    public required string? Filename { get; init; }
    public required Dictionary<string, object>? Metadata { get; init; }
    public DateTime IndexedAt { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/FullTextIndexStrategy.cs
```csharp
public sealed partial class FullTextIndexStrategy : IndexingStrategyBase
{
}
    public FullTextIndexStrategy() : this(null, true);
    public FullTextIndexStrategy(HashSet<string>? stopWords = null, bool enableStemming = true);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    public override Task OptimizeAsync(CancellationToken ct = default);
    protected override async Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class IndexedDocument
{
}
    public required string ObjectId { get; init; }
    public required string? Filename { get; init; }
    public required string? TextContent { get; init; }
    public required int TokenCount { get; init; }
    public required Dictionary<string, int> TermFrequencies { get; init; }
    public required Dictionary<string, object>? Metadata { get; init; }
    public DateTime IndexedAt { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/GraphIndexStrategy.cs
```csharp
public sealed class GraphIndexStrategy : IndexingStrategyBase
{
}
    public sealed class GraphNode;
    public sealed class GraphEdge;
    public sealed class GraphPath;
    public enum TraversalDirection;
    public GraphIndexStrategy() : this(maxTraversalDepth: 10);
    public GraphIndexStrategy(int maxTraversalDepth = 10);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public int NodeCount;;
    public int EdgeCount;;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    public List<IndexSearchResult> SearchNodes(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchEdges(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchNeighbors(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<(GraphNode Node, GraphEdge Edge)> GetNeighbors(string nodeId, TraversalDirection direction = TraversalDirection.Both, string? edgeTypeFilter = null);
    public List<IndexSearchResult> SearchShortestPath(Dictionary<string, string> @params, IndexSearchOptions options);
    public GraphPath? FindShortestPath(string fromId, string toId, string? edgeTypeFilter = null, int? maxDepth = null);
    public List<IndexSearchResult> SearchAllPaths(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<GraphPath> FindAllPaths(string fromId, string toId, string? edgeTypeFilter = null, int? maxDepth = null);
    public List<IndexSearchResult> SearchTraversal(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<(GraphNode Node, int Depth)> TraverseBfs(string startId, TraversalDirection direction = TraversalDirection.Both, string? edgeTypeFilter = null, int? maxDepth = null);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
public sealed class GraphNode
{
}
    public required string Id { get; init; }
    public required HashSet<string> Labels { get; init; }
    public required Dictionary<string, object> Properties { get; init; }
    public DateTime IndexedAt { get; init; };
}
```
```csharp
public sealed class GraphEdge
{
}
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required string Type { get; init; }
    public required Dictionary<string, object> Properties { get; init; }
    public double Weight { get; init; };
    public bool Bidirectional { get; init; }
    public DateTime IndexedAt { get; init; };
}
```
```csharp
public sealed class GraphPath
{
}
    public required List<string> NodeIds { get; init; }
    public required List<string> EdgeIds { get; init; }
    public double TotalWeight { get; init; }
    public int Length;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/TemporalIndexStrategy.cs
```csharp
public sealed class TemporalIndexStrategy : IndexingStrategyBase
{
}
    public enum TimeBucket;
    public sealed class RetentionPolicy;
    public sealed class AggregatedBucket;
    public readonly struct TimeRange;
    public TemporalIndexStrategy() : this(new RetentionPolicy());
    public TemporalIndexStrategy(RetentionPolicy retentionPolicy);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    public override Task OptimizeAsync(CancellationToken ct = default);
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    public List<IndexSearchResult> SearchTimeRange(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchPointInTime(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchAggregated(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchLatest(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<IndexSearchResult> SearchEarliest(Dictionary<string, string> @params, IndexSearchOptions options);
    public List<AggregatedBucket> GetAggregations(TimeBucket bucket, DateTime start, DateTime end, string? seriesFilter = null);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class RetentionPolicy
{
}
    public TimeSpan RawDataRetention { get; init; };
    public TimeSpan MinuteAggregateRetention { get; init; };
    public TimeSpan HourlyAggregateRetention { get; init; };
    public TimeSpan DailyAggregateRetention { get; init; };
    public bool EnableAutoCleanup { get; init; };
    public TimeSpan CleanupInterval { get; init; };
}
```
```csharp
public sealed class AggregatedBucket
{
}
    public DateTime BucketStart { get; init; }
    public DateTime BucketEnd { get; init; }
    public long Count { get; set; }
    public double Sum { get; set; }
    public double Min { get; set; };
    public double Max { get; set; };
    public double First { get; set; }
    public double Last { get; set; }
    public DateTime FirstTimestamp { get; set; }
    public DateTime LastTimestamp { get; set; }
    public double Average;;
    public void AddDataPoint(double value, DateTime timestamp);
}
```
```csharp
private sealed class TemporalDocument
{
}
    public required string ObjectId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required double? Value { get; init; }
    public required string? SeriesId { get; init; }
    public required Dictionary<string, object>? Metadata { get; init; }
    public required string? TextContent { get; init; }
    public DateTime IndexedAt { get; init; };
}
```
```csharp
public readonly struct TimeRange
{
}
    public DateTime Start { get; }
    public DateTime End { get; }
    public bool StartInclusive { get; }
    public bool EndInclusive { get; }
    public TimeRange(DateTime start, DateTime end, bool startInclusive = true, bool endInclusive = true);
    public static TimeRange Last(TimeSpan duration);;
    public static TimeRange ForDay(DateTime date);;
    public static TimeRange ForMonth(int year, int month);;
    public bool Contains(DateTime timestamp);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/SemanticIndexStrategy.cs
```csharp
public sealed class SemanticIndexStrategy : IndexingStrategyBase
{
}
    public SemanticIndexStrategy(int embeddingDimension = 384);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    public void SetMessageBus(IMessageBus? messageBus);
    protected override async Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override async Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class VectorDocument
{
}
    public required string ObjectId { get; init; }
    public required string? Filename { get; init; }
    public required string? TextContent { get; init; }
    public required float[] Embedding { get; init; }
    public float[][]? ChunkEmbeddings { get; init; }
    public required Dictionary<string, object>? Metadata { get; init; }
    public DateTime IndexedAt { get; init; };
}
```
```csharp
private sealed class EmbeddingResponse
{
}
    public float[]? Embedding { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Indexing/TemporalConsistencyStrategy.cs
```csharp
public sealed class TemporalSnapshot
{
}
    public required string SnapshotId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required long SequenceNumber { get; init; }
    public SnapshotIsolationLevel IsolationLevel { get; init; }
    public string? SessionId { get; init; }
    public DateTime ExpiresAt { get; init; }
    public bool IsValid;;
    public HashSet<string> VisibleTransactions { get; init; };
}
```
```csharp
public sealed class TemporalTransaction : IAsyncDisposable
{
}
    public string TransactionId { get; }
    public TemporalSnapshot Snapshot { get; }
    public DateTime StartTime { get; }
    public TemporalConsistencyLevel ConsistencyLevel { get; }
    public bool IsActive;;
    public TimeSpan Timeout { get; init; };
    internal TemporalTransaction(TemporalConsistencyStrategy strategy, string transactionId, TemporalSnapshot snapshot, TemporalConsistencyLevel consistencyLevel);
    public void RecordRead(string objectId);
    public void RecordWrite(string objectId, byte[] data, DateTime timestamp);
    public async Task<bool> CommitAsync(CancellationToken ct = default);
    public void Abort();
    public async ValueTask DisposeAsync();
}
```
```csharp
public sealed class TemporalWriteOperation
{
}
    public required string ObjectId { get; init; }
    public required byte[] Data { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string TransactionId { get; init; }
    public DateTime? CommitTimestamp { get; set; }
}
```
```csharp
public sealed class ConsistencyCheckResult
{
}
    public bool IsConsistent { get; init; }
    public List<ConsistencyViolation> Violations { get; init; };
    public DateTime CheckedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public TemporalConsistencyLevel ConsistencyLevel { get; init; }
}
```
```csharp
public sealed class ConsistencyViolation
{
}
    public required ConsistencyViolationType Type { get; init; }
    public required string ObjectId { get; init; }
    public required string Description { get; init; }
    public DateTime? TimestampStart { get; init; }
    public DateTime? TimestampEnd { get; init; }
    public ConsistencyViolationSeverity Severity { get; init; }
}
```
```csharp
public sealed class TemporalConsistencyStrategy : IndexingStrategyBase
{
#endregion
}
    public TemporalConsistencyStrategy(ILogger? logger = null);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeSpan BoundedStalenessWindow { get => _boundedStalenessWindow; set => _boundedStalenessWindow = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(5); }
    public TimeSpan SnapshotLifetime { get => _snapshotLifetime; set => _snapshotLifetime = value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(30); }
    public Task<TemporalSnapshot> CreateSnapshotAsync(SnapshotIsolationLevel isolationLevel = SnapshotIsolationLevel.Snapshot, string? sessionId = null, CancellationToken ct = default);
    public async Task<TemporalTransaction> BeginTransactionAsync(TemporalConsistencyLevel consistencyLevel = TemporalConsistencyLevel.Strong, string? sessionId = null, CancellationToken ct = default);
    public Task<byte[]?> ReadAtSnapshotAsync(string objectId, TemporalSnapshot snapshot, CancellationToken ct = default);
    public bool CheckBoundedStaleness(string objectId, DateTime readTimestamp);
    public Task<ConsistencyCheckResult> VerifyConsistencyAsync(IEnumerable<string>? objectIds = null, TemporalConsistencyLevel consistencyLevel = TemporalConsistencyLevel.Strong, CancellationToken ct = default);
    public void RecordSessionWrite(string sessionId, DateTime writeTimestamp);
    public bool CheckSessionConsistency(string sessionId, DateTime readTimestamp);
    internal async Task<bool> CommitTransactionAsync(TemporalTransaction transaction, List<TemporalWriteOperation> writes, HashSet<string> reads, CancellationToken ct);
    public new Dictionary<string, object> GetStatistics();
    public override long GetDocumentCount();;
    public override long GetIndexSize();
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default);
    public override Task ClearAsync(CancellationToken ct = default);
    public override Task OptimizeAsync(CancellationToken ct = default);
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct);
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(string query, IndexSearchOptions options, CancellationToken ct);
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Fabric/FabricStrategies.cs
```csharp
public sealed class StarTopologyStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MeshTopologyStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FederatedTopologyStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ViewVirtualizationStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MaterializedVirtualizationStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CachedVirtualizationStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataMeshIntegrationStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DataProductIntegrationStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SemanticLayerStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class FabricDataCatalogStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class LineageTrackingStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class UnifiedAccessStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class AIEnhancedFabricStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/InactivityBasedRetentionStrategy.cs
```csharp
public sealed class InactivityBasedRetentionStrategy : RetentionStrategyBase
{
}
    public InactivityBasedRetentionStrategy() : this(TimeSpan.FromDays(90), TimeSpan.FromDays(7));
    public InactivityBasedRetentionStrategy(TimeSpan inactivityThreshold, TimeSpan warningPeriod, bool trackReads = true, bool trackWrites = true);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeSpan InactivityThreshold;;
    public TimeSpan WarningPeriod;;
    public void SetContentTypeThreshold(string contentType, TimeSpan threshold);
    public void RecordRead(string objectId);
    public void RecordWrite(string objectId);
    public AccessRecord? GetAccessRecord(string objectId);
    public IReadOnlyList<string> GetExpiringObjects(TimeSpan within);
    public AccessPatternStats GetAccessStats();
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    protected override Task DisposeCoreAsync();
}
```
```csharp
public sealed class AccessRecord
{
}
    public required string ObjectId { get; init; }
    public string? ContentType { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? LastReadAt
{
    get
    {
        var t = Interlocked.Read(ref _lastReadAtTicks);
        return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
    }

    set
    {
        Interlocked.Exchange(ref _lastReadAtTicks, value?.Ticks ?? 0L);
    }
}
    public DateTime? LastWriteAt
{
    get
    {
        var t = Interlocked.Read(ref _lastWriteAtTicks);
        return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
    }

    set
    {
        Interlocked.Exchange(ref _lastWriteAtTicks, value?.Ticks ?? 0L);
    }
}
    public DateTime LastAccessAt
{
    get
    {
        return new DateTime(Interlocked.Read(ref _lastAccessAtTicks), DateTimeKind.Utc);
    }

    set
    {
        Interlocked.Exchange(ref _lastAccessAtTicks, value.Ticks);
    }
}
    public long ReadCount;
    public long WriteCount;
    public long TotalAccessCount;;
    public TimeSpan InactivityDuration;;
}
```
```csharp
public sealed class AccessPatternStats
{
}
    public int TotalObjects { get; init; }
    public int ActiveObjects { get; init; }
    public int InactiveObjects { get; init; }
    public int StaleObjects { get; init; }
    public double AverageReadsPerObject { get; init; }
    public double AverageWritesPerObject { get; init; }
    public List<string>? MostAccessedObjects { get; init; }
    public List<string>? LeastAccessedObjects { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/CascadingRetentionStrategy.cs
```csharp
public sealed class CascadingRetentionStrategy : RetentionStrategyBase
{
}
    public CascadingRetentionStrategy() : this(7, 4, 12, 7);
    public CascadingRetentionStrategy(int dailyCount, int weeklyCount, int monthlyCount, int yearlyCount);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public (int Daily, int Weekly, int Monthly, int Yearly) TierCounts;;
    public void AssignTier(string objectId, RetentionTier tier, int tierPosition, DateTime backupTime);
    public RetentionTierInfo? GetTierAssignment(string objectId);
    public bool PromoteToTier(string objectId, RetentionTier newTier);
    public IReadOnlyList<RetentionTierInfo> GetObjectsInTier(RetentionTier tier);
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    protected override async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct);
}
```
```csharp
public sealed record RetentionTierInfo
{
}
    public required string ObjectId { get; init; }
    public required RetentionTier Tier { get; init; }
    public required int TierPosition { get; init; }
    public required DateTime BackupTime { get; init; }
    public required DateTime AssignedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/SizeBasedRetentionStrategy.cs
```csharp
public sealed class SizeBasedRetentionStrategy : RetentionStrategyBase
{
}
    public SizeBasedRetentionStrategy() : this(100L * 1024 * 1024 * 1024);
    public SizeBasedRetentionStrategy(long globalQuotaBytes, double warningThreshold = 0.8, double criticalThreshold = 0.95, EvictionPolicy evictionPolicy = EvictionPolicy.LeastRecentlyUsed);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public long GlobalQuotaBytes;;
    public long CurrentUsageBytes;;
    public double UsageRatio;;
    public void SetTenantQuota(string tenantId, long quotaBytes);
    public TenantQuota? GetTenantQuota(string tenantId);
    public void RecordUsage(string objectId, string? tenantId, long sizeBytes);
    public void ReleaseUsage(string? tenantId, long sizeBytes);
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    protected override async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct);
}
```
```csharp
public sealed class TenantQuota
{
}
    public required string TenantId { get; init; }
    public required long QuotaBytes { get; init; }
    public long UsedBytes;
    public double UsageRatio;;
    public long RemainingBytes;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/RetentionStrategyBase.cs
```csharp
public sealed class RetentionDecision
{
}
    public required RetentionAction Action { get; init; }
    public required string Reason { get; init; }
    public DateTime? NextEvaluationDate { get; init; }
    public string? PolicyName { get; init; }
    public double? Confidence { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public static RetentionDecision Retain(string reason, DateTime? nextEvaluation = null);;
    public static RetentionDecision Delete(string reason);;
    public static RetentionDecision Archive(string reason);;
    public static RetentionDecision HoldForLegal(string reason, string holdId);;
}
```
```csharp
public sealed class RetentionScope
{
}
    public string? TenantId { get; init; }
    public string? PathPrefix { get; init; }
    public string[]? ContentTypes { get; init; }
    public string[]? Tags { get; init; }
    public TimeSpan? MinAge { get; init; }
    public int? MaxItems { get; init; }
    public bool DryRun { get; init; };
}
```
```csharp
public sealed class RetentionStats
{
}
    public long TotalEvaluated { get; set; }
    public long Retained { get; set; }
    public long Deleted { get; set; }
    public long Archived { get; set; }
    public long LegalHold { get; set; }
    public long BytesFreed { get; set; }
    public long BytesArchived { get; set; }
    public DateTime? LastEvaluationTime { get; set; }
    public TimeSpan? LastEvaluationDuration { get; set; }
}
```
```csharp
public sealed class DataObject
{
}
    public required string ObjectId { get; init; }
    public string? Path { get; init; }
    public string? ContentType { get; init; }
    public long Size { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? LastModifiedAt { get; init; }
    public DateTime? LastAccessedAt { get; init; }
    public int Version { get; init; };
    public bool IsLatestVersion { get; init; };
    public string? TenantId { get; init; }
    public string[]? Tags { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public TimeSpan Age;;
    public TimeSpan? TimeSinceLastAccess;;
}
```
```csharp
public interface IRetentionStrategy : IDataManagementStrategy
{
}
    Task<RetentionDecision> EvaluateAsync(DataObject data, CancellationToken ct);;
    Task<int> ApplyRetentionAsync(RetentionScope scope, CancellationToken ct);;
    Task<RetentionStats> GetStatsAsync(CancellationToken ct);;
}
```
```csharp
public abstract class RetentionStrategyBase : DataManagementStrategyBase, IRetentionStrategy
{
}
    protected readonly BoundedDictionary<string, DataObject> TrackedObjects = new BoundedDictionary<string, DataObject>(1000);
    public override DataManagementCategory Category;;
    public async Task<RetentionDecision> EvaluateAsync(DataObject data, CancellationToken ct);
    public async Task<int> ApplyRetentionAsync(RetentionScope scope, CancellationToken ct);
    public Task<RetentionStats> GetStatsAsync(CancellationToken ct);
    protected abstract Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);;
    protected virtual async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct);
    protected virtual IEnumerable<DataObject> GetObjectsInScope(RetentionScope scope);
    protected virtual int ApplyDecision(DataObject obj, RetentionDecision decision);
    protected void UpdateEvaluationStats(RetentionDecision decision);
    public void TrackObject(DataObject obj);
    public bool UntrackObject(string objectId);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/TimeBasedRetentionStrategy.cs
```csharp
public sealed class TimeBasedRetentionStrategy : RetentionStrategyBase
{
}
    public TimeBasedRetentionStrategy() : this(TimeSpan.FromDays(365), TimeSpan.FromDays(30));
    public TimeBasedRetentionStrategy(TimeSpan retentionPeriod, TimeSpan gracePeriod, bool archiveBeforeDelete = false, TimeSpan? archiveAfter = null);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeSpan RetentionPeriod;;
    public TimeSpan GracePeriod;;
    public void SetContentTypeRetention(string contentType, TimeSpan period);
    public void SetTagRetention(string tag, TimeSpan period);
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/LegalHoldStrategy.cs
```csharp
public sealed class LegalHoldStrategy : RetentionStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int ActiveHoldCount;;
    public int HeldObjectCount;;
    public LegalHold CreateHold(string holdId, string matterName, string description, string createdBy);
    public int ApplyHoldToObjects(string holdId, IEnumerable<string> objectIds, string appliedBy);
    public void ApplyHoldToCustodian(string holdId, string custodianId, string appliedBy);
    public void ReleaseHold(string holdId, string releasedBy, string reason);
    public bool IsObjectUnderHold(string objectId);
    public IReadOnlyList<string> GetObjectHolds(string objectId);
    public bool IsCustodianUnderHold(string custodianId);
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    public IReadOnlyList<HoldAuditEntry> GetAuditLog(string? holdId = null, int limit = 100);
    public LegalHold? GetHold(string holdId);
    public IReadOnlyList<LegalHold> GetActiveHolds();
}
```
```csharp
public sealed class LegalHold
{
}
    internal readonly object ListLock = new();
    public required string HoldId { get; init; }
    public required string MatterName { get; init; }
    public string? Description { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public bool IsActive { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }
    public required List<string> HeldObjects { get; init; }
    public required List<string> Custodians { get; init; }
}
```
```csharp
public sealed class HoldAuditEntry
{
}
    public required DateTime Timestamp { get; init; }
    public required string HoldId { get; init; }
    public required HoldAction Action { get; init; }
    public required string PerformedBy { get; init; }
    public required string Details { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/VersionRetentionStrategy.cs
```csharp
public sealed class VersionRetentionStrategy : RetentionStrategyBase
{
}
    public VersionRetentionStrategy() : this(10, 1, null);
    public VersionRetentionStrategy(int maxVersions, int minVersions, TimeSpan? maxVersionAge);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int MaxVersions;;
    public int MinVersions;;
    public void RegisterVersion(string baseObjectId, string versionId, int version, DateTime createdAt, long size);
    public IReadOnlyList<VersionInfo>? GetVersionHistory(string baseObjectId);
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    protected override async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct);
}
```
```csharp
private sealed class ObjectVersionHistory
{
}
    public required string BaseObjectId { get; init; }
    public required List<VersionInfo> Versions { get; set; }
}
```
```csharp
public sealed class VersionInfo
{
}
    public required string VersionId { get; init; }
    public required int VersionNumber { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required long Size { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/PolicyBasedRetentionStrategy.cs
```csharp
public sealed class PolicyBasedRetentionStrategy : RetentionStrategyBase
{
}
    public PolicyBasedRetentionStrategy();
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterPolicy(CompliancePolicy policy);
    public void AssignPolicy(PolicyAssignment assignment);
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    public IReadOnlyList<PolicyAuditEntry> GetAuditLog(int limit = 100);
}
```
```csharp
public sealed class CompliancePolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public TimeSpan? MinRetention { get; init; }
    public TimeSpan? MaxRetention { get; init; }
    public bool RequiresConsent { get; init; }
    public bool AllowsDeletion { get; init; };
    public bool RequiresDeletionOnRequest { get; init; }
    public string[]? ApplicableRegions { get; init; }
    public string[]? DataCategories { get; init; }
    public int Priority { get; init; }
}
```
```csharp
public sealed class PolicyAssignment
{
}
    public required string PolicyId { get; init; }
    public string? PathPattern { get; init; }
    public string[]? ContentTypes { get; init; }
    public string? TenantId { get; init; }
}
```
```csharp
public sealed class PolicyAuditEntry
{
}
    public required DateTime Timestamp { get; init; }
    public required string ObjectId { get; init; }
    public required string PolicyId { get; init; }
    public required string PolicyName { get; init; }
    public required RetentionAction Decision { get; init; }
    public required string Reason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Retention/SmartRetentionStrategy.cs
```csharp
public sealed class SmartRetentionStrategy : RetentionStrategyBase
{
}
    public SmartRetentionStrategy() : this(0.7, 0.3, null);
    public SmartRetentionStrategy(double retainThreshold, double deleteThreshold, Func<ObjectFeatures, CancellationToken, Task<double>>? mlPredictor);
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public FeatureWeights Weights;;
    public void RecordFeatures(string objectId, ObjectFeatures features);
    public void RecordBusinessValueSignal(string objectId, double signal);
    public void RecordAccess(string objectId, AccessType accessType);
    protected override async Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);
    public SmartRetentionMetrics GetMetrics();
}
```
```csharp
public sealed class ObjectFeatures
{
}
    public required string ObjectId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? LastAccessedAt { get; set; }
    public required long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public double BusinessValue { get; set; }
    public double UserImportanceRating { get; set; }
    public required List<AccessEvent> AccessHistory { get; init; }
    public required System.Collections.Concurrent.ConcurrentBag<ValueSignal> BusinessValueSignals { get; init; }
}
```
```csharp
public sealed class AccessEvent
{
}
    public required DateTime Timestamp { get; init; }
    public required AccessType Type { get; init; }
}
```
```csharp
public sealed class ValueSignal
{
}
    public required DateTime Timestamp { get; init; }
    public required double Value { get; init; }
}
```
```csharp
public sealed class FeatureWeights
{
}
    public double RecencyWeight { get; set; }
    public double FrequencyWeight { get; set; }
    public double SizeWeight { get; set; }
    public double BusinessValueWeight { get; set; }
    public double ContentTypeWeight { get; set; }
    public double UserImportanceWeight { get; set; }
}
```
```csharp
public sealed class SmartRetentionMetrics
{
}
    public int TotalObjectsTracked { get; init; }
    public int ObjectsWithAccessHistory { get; init; }
    public int ObjectsWithBusinessValue { get; init; }
    public double AverageBusinessValue { get; init; }
    public int PredictionCacheSize { get; init; }
    public double PredictionCacheHitRate { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/CopyOnWriteVersioningStrategy.cs
```csharp
public sealed class CopyOnWriteVersioningStrategy : VersioningStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int BlockSize { get => _blockSize; set => _blockSize = value > 0 ? value : DefaultBlockSize; }
    public int UniqueBlockCount;;
    public long TotalBlockPoolBytes;;
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
    public int RunGarbageCollection();
}
```
```csharp
private sealed class SharedBlock
{
}
    public required string BlockHash { get; init; }
    public required byte[] Data { get; init; }
    public int ReferenceCount
{
    get
    {
        lock (_refLock)
            return _referenceCount;
    }
}
    public void AddReference();
    public bool RemoveReference();
}
```
```csharp
private sealed class CoWVersionEntry
{
}
    public required VersionInfo Info { get; init; }
    public required List<string> BlockHashes { get; init; }
    public required long TotalSize { get; init; }
}
```
```csharp
private sealed class ObjectCoWStore
{
}
    public (VersionInfo Info, List<string> OldBlockHashes) CreateVersion(string objectId, List<string> blockHashes, long totalSize, string contentHash, VersionMetadata metadata);
    public List<string>? GetVersionBlockHashes(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public (bool Success, List<string> BlockHashes) DeleteVersion(string versionId);
    public VersionInfo? GetCurrentVersion();
    public (VersionInfo? Info, List<string>? BlockHashes) GetVersionForRestore(string versionId);
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount(bool includeDeleted = false);
    public HashSet<string> GetAllActiveBlockHashes();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/BranchingVersioningStrategy.cs
```csharp
public sealed class BranchingVersioningStrategy : VersioningStrategyBase
{
}
    public sealed class BranchInfo;
    public sealed class MergeResult;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<BranchInfo> CreateBranchAsync(string objectId, string branchName, string? fromBranch = null, string? fromVersion = null, CancellationToken ct = default);
    public Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);
    public Task<bool> DeleteBranchAsync(string objectId, string branchName, CancellationToken ct = default);
    public Task<IEnumerable<BranchInfo>> ListBranchesAsync(string objectId, bool includeDeleted = false, CancellationToken ct = default);
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class Branch
{
}
    public required string Name { get; init; }
    public required string ObjectId { get; init; }
    public string? ParentBranch { get; init; }
    public string? BranchPoint { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsDeleted { get; set; }
    public List<VersionEntry> Versions { get; };
    public string? CurrentVersionId { get; set; }
}
```
```csharp
private sealed class VersionEntry
{
}
    public required VersionInfo Info { get; init; }
    public required byte[] Data { get; init; }
}
```
```csharp
private sealed class BranchStore
{
}
    public BranchStore(string objectId);
    public VersionInfo CreateVersion(string objectId, byte[] data, VersionMetadata metadata);
    public Branch CreateBranch(string branchName, string? fromBranch = null, string? fromVersion = null);
    public MergeResult MergeBranch(string sourceBranch, string targetBranch);
    public bool DeleteBranch(string branchName);
    public IEnumerable<BranchInfo> ListBranches(bool includeDeleted = false);
    public byte[]? GetVersionData(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public VersionInfo? GetCurrentVersion(string? branchName = null);
    public bool DeleteVersion(string versionId);
    public VersionInfo? RestoreVersion(string objectId, string versionId, string? toBranch = null);
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount(string? branchName = null);
}
```
```csharp
public sealed class BranchInfo
{
}
    public required string Name { get; init; }
    public required string ObjectId { get; init; }
    public string? ParentBranch { get; init; }
    public string? BranchPoint { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CurrentVersionId { get; init; }
    public int VersionCount { get; init; }
    public bool IsDeleted { get; init; }
}
```
```csharp
public sealed class MergeResult
{
}
    public bool Success { get; init; }
    public bool HasConflicts { get; init; }
    public string? MergeVersionId { get; init; }
    public string? Message { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/DeltaVersioningStrategy.cs
```csharp
public sealed class DeltaVersioningStrategy : VersioningStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int MaxDeltaChainLength { get => _maxDeltaChainLength; set => _maxDeltaChainLength = value > 0 ? value : DefaultMaxDeltaChainLength; }
    public int BlockSize { get => _blockSize; set => _blockSize = value > 0 ? value : DefaultBlockSize; }
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class DeltaVersionEntry
{
}
    public required VersionInfo Info { get; init; }
    public required bool IsBase { get; init; }
    public byte[]? BaseData { get; init; }
    public byte[]? DeltaData { get; init; }
    public string? ParentVersionId { get; init; }
    public int ChainDepth { get; init; }
}
```
```csharp
private sealed class ObjectDeltaStore
{
}
    public VersionInfo CreateBaseVersion(string objectId, byte[] data, VersionMetadata metadata);
    public VersionInfo CreateDeltaVersion(string objectId, byte[] data, byte[] deltaData, string parentVersionId, int chainDepth, VersionMetadata metadata);
    public DeltaVersionEntry? GetCurrentEntry();
    public DeltaVersionEntry? GetEntry(string versionId);
    public List<DeltaVersionEntry> GetDeltaChain(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public bool DeleteVersion(string versionId);
    public VersionInfo? GetCurrentVersion();
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount(bool includeDeleted = false);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/TimePointVersioningStrategy.cs
```csharp
public sealed class TimePointVersioningStrategy : VersioningStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public int SnapshotIntervalMs { get => _snapshotIntervalMs; set => _snapshotIntervalMs = value > 0 ? value : DefaultSnapshotIntervalMs; }
    public int RetentionDays { get => _retentionDays; set => _retentionDays = value > 0 ? value : DefaultRetentionDays; }
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
    public Task<VersionInfo?> GetVersionAtTimeAsync(string objectId, DateTime pointInTime, CancellationToken ct = default);
    public Task<Stream?> GetDataAtTimeAsync(string objectId, DateTime pointInTime, CancellationToken ct = default);
    public Task<IEnumerable<VersionInfo>> GetVersionsInRangeAsync(string objectId, DateTime from, DateTime to, CancellationToken ct = default);
    public int ApplyRetentionPolicy(string objectId, int? retentionDays = null);
    public int ApplyRetentionPolicyAll();
    public (int TotalEntries, int Snapshots, int Deltas) GetWalStats(string objectId);
}
```
```csharp
private sealed class WalEntry
{
}
    public required DateTime Timestamp { get; init; }
    public required WalOperationType Operation { get; init; }
    public byte[]? DeltaData { get; init; }
    public byte[]? SnapshotData { get; init; }
    public required string ContentHash { get; init; }
    public required long SizeBytes { get; init; }
}
```
```csharp
private sealed class TimePointVersionEntry
{
}
    public required VersionInfo Info { get; init; }
    public required DateTime PreciseTimestamp { get; init; }
    public required byte[] Data { get; init; }
    public required bool IsSnapshot { get; init; }
}
```
```csharp
private sealed class ObjectTimePointStore
{
}
    public (VersionInfo Info, bool CreatedSnapshot) CreateVersion(string objectId, byte[] data, VersionMetadata metadata, int snapshotIntervalMs);
    public TimePointVersionEntry? GetVersionAtTime(DateTime pointInTime);
    public IEnumerable<TimePointVersionEntry> GetVersionsInRange(DateTime from, DateTime to);
    public byte[]? GetVersionData(string versionId);
    public TimePointVersionEntry? GetEntry(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public bool DeleteVersion(string versionId);
    public int ApplyRetentionPolicy(int retentionDays);
    public VersionInfo? GetCurrentVersion();
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount(bool includeDeleted = false);
    public (int TotalEntries, int Snapshots, int Deltas) GetWalStats();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/TaggingVersioningStrategy.cs
```csharp
public sealed class TaggingVersioningStrategy : VersioningStrategyBase
{
}
    public sealed record TagInfo;
    public sealed class TagCreateOptions;
    public sealed class TagListOptions;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<TagInfo> CreateTagAsync(string objectId, string tagName, string versionId, TagCreateOptions? options = null, CancellationToken ct = default);
    public Task<TagInfo?> GetTagAsync(string objectId, string tagName, CancellationToken ct = default);
    public Task<Stream> GetVersionByTagAsync(string objectId, string tagName, CancellationToken ct = default);
    public Task<bool> DeleteTagAsync(string objectId, string tagName, CancellationToken ct = default);
    public Task<IEnumerable<TagInfo>> ListTagsAsync(string objectId, TagListOptions? options = null, CancellationToken ct = default);
    public Task<IEnumerable<TagInfo>> GetTagsForVersionAsync(string objectId, string versionId, CancellationToken ct = default);
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
public sealed record TagInfo
{
}
    public required string Name { get; init; }
    public required string ObjectId { get; init; }
    public required string VersionId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Author { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public bool IsDeleted { get; init; }
}
```
```csharp
public sealed class TagCreateOptions
{
}
    public string? Author { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public bool Overwrite { get; init; }
}
```
```csharp
public sealed class TagListOptions
{
}
    public string? Pattern { get; init; }
    public int MaxResults { get; init; };
    public bool IncludeDeleted { get; init; }
    public bool Descending { get; init; }
}
```
```csharp
private sealed class TagStore
{
}
    public VersionInfo CreateVersion(string objectId, byte[] data, VersionMetadata metadata);
    public TagInfo CreateTag(string objectId, string tagName, string versionId, TagCreateOptions options);
    public TagInfo? GetTag(string tagName);
    public (byte[]? data, VersionInfo? info) GetVersionByTag(string tagName);
    public bool DeleteTag(string tagName);
    public IEnumerable<TagInfo> ListTags(TagListOptions options);
    public IEnumerable<TagInfo> GetTagsForVersion(string versionId);
    public byte[]? GetVersionData(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public VersionInfo? GetCurrentVersion();
    public bool DeleteVersion(string versionId);
    public VersionInfo? RestoreVersion(string objectId, string versionId);
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount();
}
```
```csharp
private sealed class VersionEntry
{
}
    public required VersionInfo Info { get; init; }
    public required byte[] Data { get; init; }
}
```
```csharp
private sealed class TagEntry
{
}
    public required TagInfo Info { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/VersioningStrategyBase.cs
```csharp
public sealed record VersionMetadata
{
}
    public string? Author { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, string>? Properties { get; init; }
    public string? ParentVersionId { get; init; }
    public string? Branch { get; init; }
    public string[]? Tags { get; init; }
}
```
```csharp
public sealed record VersionInfo
{
}
    public required string VersionId { get; init; }
    public required string ObjectId { get; init; }
    public long VersionNumber { get; init; }
    public required string ContentHash { get; init; }
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public VersionMetadata? Metadata { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsDeleted { get; init; }
}
```
```csharp
public sealed class VersionDiff
{
}
    public required string FromVersionId { get; init; }
    public required string ToVersionId { get; init; }
    public long SizeDifference { get; init; }
    public bool IsIdentical { get; init; }
    public byte[]? DeltaBytes { get; init; }
    public string? Summary { get; init; }
}
```
```csharp
public sealed class VersionListOptions
{
}
    public int MaxResults { get; init; };
    public bool IncludeDeleted { get; init; }
    public string? Branch { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}
```
```csharp
public interface IVersioningStrategy : IDataManagementStrategy
{
}
    Task<VersionInfo> CreateVersionAsync(string objectId, Stream data, VersionMetadata? metadata, CancellationToken ct = default);;
    Task<Stream> GetVersionAsync(string objectId, string versionId, CancellationToken ct = default);;
    Task<IEnumerable<VersionInfo>> ListVersionsAsync(string objectId, VersionListOptions? options = null, CancellationToken ct = default);;
    Task<bool> DeleteVersionAsync(string objectId, string versionId, CancellationToken ct = default);;
    Task<VersionInfo?> GetCurrentVersionAsync(string objectId, CancellationToken ct = default);;
    Task<VersionInfo> RestoreVersionAsync(string objectId, string versionId, CancellationToken ct = default);;
    Task<VersionDiff> DiffVersionsAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct = default);;
    Task<long> GetVersionCountAsync(string objectId, CancellationToken ct = default);;
}
```
```csharp
public abstract class VersioningStrategyBase : DataManagementStrategyBase, IVersioningStrategy
{
}
    public override DataManagementCategory Category;;
    public async Task<VersionInfo> CreateVersionAsync(string objectId, Stream data, VersionMetadata? metadata, CancellationToken ct = default);
    public async Task<Stream> GetVersionAsync(string objectId, string versionId, CancellationToken ct = default);
    public async Task<IEnumerable<VersionInfo>> ListVersionsAsync(string objectId, VersionListOptions? options = null, CancellationToken ct = default);
    public async Task<bool> DeleteVersionAsync(string objectId, string versionId, CancellationToken ct = default);
    public async Task<VersionInfo?> GetCurrentVersionAsync(string objectId, CancellationToken ct = default);
    public async Task<VersionInfo> RestoreVersionAsync(string objectId, string versionId, CancellationToken ct = default);
    public async Task<VersionDiff> DiffVersionsAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct = default);
    public async Task<long> GetVersionCountAsync(string objectId, CancellationToken ct = default);
    protected abstract Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);;
    protected abstract Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);;
    protected abstract Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);;
    protected abstract Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);;
    protected abstract Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);;
    protected abstract Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);;
    protected abstract Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);;
    protected abstract Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);;
    protected static string ComputeHash(Stream stream);
    protected static string ComputeHash(byte[] data);
    protected static string GenerateVersionId();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/SemanticVersioningStrategy.cs
```csharp
public sealed class SemanticVersioningStrategy : VersioningStrategyBase
{
}
    public sealed class SemVer : IComparable<SemVer>;
    public enum ChangeType;
    public sealed class VersionConstraint;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
    public IEnumerable<(VersionInfo Info, SemVer Version)> FindMatchingVersions(string objectId, string constraint);
    public static bool AreCompatible(string version1, string version2);
}
```
```csharp
public sealed class SemVer : IComparable<SemVer>
{
}
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }
    public SemVer(int major, int minor, int patch, string? prerelease = null, string? buildMetadata = null);
    public static SemVer Parse(string version);
    public static bool TryParse(string version, out SemVer? result);
    public SemVer IncrementMajor();;
    public SemVer IncrementMinor();;
    public SemVer IncrementPatch();;
    public int CompareTo(SemVer? other);
    public override string ToString();
    public override bool Equals(object? obj);;
    public override int GetHashCode();;
    public static bool operator <(SemVer? left, SemVer? right) => left is null ? right is not null : left.CompareTo(right) < 0;;
    public static bool operator>(SemVer? left, SemVer? right) => left is not null && left.CompareTo(right) > 0;;
    public static bool operator <=(SemVer? left, SemVer? right) => left is null || left.CompareTo(right) <= 0;;
    public static bool operator >=(SemVer? left, SemVer? right) => left is null ? right is null : left.CompareTo(right) >= 0;;
    public static bool operator ==(SemVer? left, SemVer? right) => ReferenceEquals(left, right) || (left is not null && left.Equals(right));;
    public static bool operator !=(SemVer? left, SemVer? right) => !(left == right);;
}
```
```csharp
public sealed class VersionConstraint
{
}
    public static VersionConstraint Parse(string constraint);
    public bool Satisfies(SemVer version);;
    public override string ToString();;
}
```
```csharp
private sealed class SemVerEntry
{
}
    public required VersionInfo Info { get; init; }
    public required SemVer SemanticVersion { get; init; }
    public required byte[] Data { get; init; }
    public required string[] DataStructureKeys { get; init; }
}
```
```csharp
private sealed class ObjectSemVerStore
{
}
    public VersionInfo CreateVersion(string objectId, byte[] data, SemVer semVer, string[] structureKeys, VersionMetadata metadata);
    public SemVer GetCurrentSemVer();
    public SemVerEntry? GetEntry(string versionId);
    public SemVerEntry? GetCurrentEntry();
    public byte[]? GetVersionData(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public IEnumerable<(VersionInfo Info, SemVer Version)> FindMatchingVersions(VersionConstraint constraint);
    public bool DeleteVersion(string versionId);
    public VersionInfo? GetCurrentVersion();
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount(bool includeDeleted = false);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/LinearVersioningStrategy.cs
```csharp
public sealed class LinearVersioningStrategy : VersioningStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
private sealed class ObjectVersionStore
{
}
    public sealed class VersionEntry;
    public VersionInfo CreateVersion(string objectId, byte[] data, VersionMetadata metadata);
    public byte[]? GetVersionData(string versionId);
    public IEnumerable<VersionInfo> ListVersions(VersionListOptions options);
    public bool DeleteVersion(string versionId);
    public VersionInfo? GetCurrentVersion();
    public VersionInfo? RestoreVersion(string objectId, string versionId);
    public VersionInfo? GetVersionInfo(string versionId);
    public long GetVersionCount(bool includeDeleted = false);
}
```
```csharp
public sealed class VersionEntry
{
}
    public required VersionInfo Info { get; init; }
    public required byte[] Data { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/Versioning/BiTemporalVersioningStrategy.cs
```csharp
public sealed class BiTemporalVersioningStrategy : VersioningStrategyBase
{
#endregion
}
    public sealed class BiTemporalFact;
    public sealed class BiTemporalQueryResult;
    public sealed class BiTemporalInsertOptions;
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public TimeSpan DefaultValidTimeDuration { get => _defaultValidTimeDuration; set => _defaultValidTimeDuration = value > TimeSpan.Zero ? value : TimeSpan.FromDays(36500); }
    public async Task<BiTemporalFact> InsertBiTemporalAsync(string objectId, Stream data, VersionMetadata? metadata, BiTemporalInsertOptions? options = null, CancellationToken ct = default);
    public Task<BiTemporalQueryResult> QueryBiTemporalAsync(string objectId, DateTime validTime, DateTime transactionTime, CancellationToken ct = default);
    public Task<BiTemporalFact?> GetCurrentStateAsync(string objectId, CancellationToken ct = default);
    public Task<BiTemporalFact?> GetStateAtValidTimeAsync(string objectId, DateTime validTime, CancellationToken ct = default);
    public Task<BiTemporalFact?> GetStateAsKnownAtAsync(string objectId, DateTime transactionTime, CancellationToken ct = default);
    public Task<IReadOnlyList<BiTemporalFact>> GetTimelineAsync(string objectId, bool includeSuperseded = false, CancellationToken ct = default);
    public async Task<BiTemporalFact> CorrectHistoricalDataAsync(string objectId, Stream data, DateTime validTimeStart, DateTime validTimeEnd, string correctionReason, VersionMetadata? metadata = null, CancellationToken ct = default);
    public int ApplyRetentionPolicy(TimeSpan transactionTimeRetention);
    protected override async Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);
    protected override Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);
    protected override Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override async Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);
    protected override async Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);
    protected override Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);
    protected override Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);
}
```
```csharp
public sealed class BiTemporalFact
{
}
    public required string FactId { get; init; }
    public required string ObjectId { get; init; }
    public required DateTime ValidTimeStart { get; init; }
    public required DateTime ValidTimeEnd { get; init; }
    public required DateTime TransactionTimeStart { get; init; }
    public DateTime TransactionTimeEnd { get; set; };
    public required byte[] Data { get; init; }
    public required string ContentHash { get; init; }
    public long SizeBytes { get; init; }
    public VersionMetadata? Metadata { get; init; }
    public bool IsCurrentTransaction;;
    public bool IsCurrentlyValid
{
    get
    {
        var now = DateTime.UtcNow;
        return ValidTimeStart <= now && ValidTimeEnd > now;
    }
}
    public bool WasValidAt(DateTime validTime);;
    public bool WasKnownAt(DateTime transactionTime);;
    public bool MatchesBiTemporal(DateTime validTime, DateTime transactionTime);;
}
```
```csharp
public sealed class BiTemporalQueryResult
{
}
    public required IReadOnlyList<BiTemporalFact> Facts { get; init; }
    public DateTime QueryValidTime { get; init; }
    public DateTime QueryTransactionTime { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public bool HasResults;;
    public BiTemporalFact? MostRecentFact;;
}
```
```csharp
public sealed class BiTemporalInsertOptions
{
}
    public DateTime? ValidTimeStart { get; init; }
    public DateTime? ValidTimeEnd { get; init; }
    public bool AutoCloseOverlaps { get; init; };
    public bool IsCorrection { get; init; }
    public string? CorrectionReason { get; init; }
}
```
```csharp
private sealed class BiTemporalObjectStore
{
}
    public BiTemporalFact Insert(string objectId, byte[] data, VersionMetadata? metadata, BiTemporalInsertOptions options);
    public BiTemporalQueryResult Query(DateTime validTime, DateTime transactionTime);
    public BiTemporalFact? GetCurrentState();
    public BiTemporalFact? GetStateAtValidTime(DateTime validTime);
    public BiTemporalFact? GetStateAsKnownAt(DateTime transactionTime);
    public IReadOnlyList<BiTemporalFact> GetTimeline(bool includeSuperseded = false);
    public BiTemporalFact? GetFact(string factId);
    public bool DeleteFact(string factId);
    public long GetActiveFactCount();
    public long GetTotalFactCount();
    public int ApplyRetention(TimeSpan transactionTimeRetention);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/SemanticDeduplicationStrategy.cs
```csharp
public sealed class DeduplicationResult
{
}
    public required bool HasDuplicates { get; init; }
    public bool HasSemanticDuplicates { get; init; }
    public bool HasExactDuplicates { get; init; }
    public IReadOnlyList<(string ObjectId, double Similarity, bool IsExact)> Duplicates { get; init; };
    public string? BestMatchId { get; init; }
    public double BestMatchSimilarity { get; init; }
    public bool UsedAi { get; init; }
    public TimeSpan Duration { get; init; }
}
```
```csharp
public sealed class ContentSignature
{
}
    public required string ObjectId { get; init; }
    public required string ContentHash { get; init; }
    public ContentAnalysisType ContentType { get; init; }
    public float[]? Embedding { get; init; }
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; };
    public HashSet<string>? ExtractedTokens { get; init; }
}
```
```csharp
public sealed class SemanticDeduplicationStrategy : AiEnhancedStrategyBase
{
}
    public SemanticDeduplicationStrategy() : this(0.95, 0.85);
    public SemanticDeduplicationStrategy(double semanticThreshold, double nearDuplicateThreshold);
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public double SemanticThreshold;;
    public double NearDuplicateThreshold;;
    public async Task<ContentSignature> RegisterContentAsync(string objectId, byte[] content, string? textContent = null, ContentAnalysisType contentType = ContentAnalysisType.Binary, CancellationToken ct = default);
    public async Task<DeduplicationResult> CheckForDuplicatesAsync(byte[] content, string? textContent = null, string? excludeObjectId = null, CancellationToken ct = default);
    public async Task<IReadOnlyList<IReadOnlyList<string>>> FindAllDuplicateGroupsAsync(CancellationToken ct = default);
    public bool RemoveContent(string objectId);
    public Dictionary<string, object> GetContentStatistics();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/IntentBasedDataManagementStrategy.cs
```csharp
public sealed class DataGoal
{
}
    public required string GoalId { get; init; }
    public required DataGoalType Type { get; init; }
    public int Priority { get; init; };
    public double? TargetValue { get; init; }
    public string? TargetUnit { get; init; }
    public Dictionary<string, object>? Constraints { get; init; }
    public DateTime CreatedAt { get; init; };
    public string? Description { get; init; }
}
```
```csharp
public sealed class GoalAction
{
}
    public required string ActionType { get; init; }
    public required string Description { get; init; }
    public double ExpectedImpact { get; init; }
    public decimal? EstimatedCost { get; init; }
    public TimeSpan? EstimatedDuration { get; init; }
    public Dictionary<string, object>? Parameters { get; init; }
    public double RiskLevel { get; init; }
}
```
```csharp
public sealed class GoalSatisfaction
{
}
    public required DataGoal Goal { get; init; }
    public double SatisfactionLevel { get; init; }
    public double? CurrentValue { get; init; }
    public double? GapToTarget { get; init; }
    public int Trend { get; init; }
    public IReadOnlyList<GoalAction> RecommendedActions { get; init; };
    public DateTime MeasuredAt { get; init; };
    public bool IsSatisfied;;
}
```
```csharp
public sealed class IntentBasedDataManagementStrategy : AiEnhancedStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void SetGoal(DataGoal goal);
    public async Task<DataGoal?> ParseGoalAsync(string description, CancellationToken ct = default);
    public DataGoal? GetGoal(string goalId);
    public IReadOnlyList<DataGoal> GetAllGoals();
    public bool RemoveGoal(string goalId);
    public void ReportMetric(string goalId, double value);
    public async Task<GoalSatisfaction?> MeasureSatisfactionAsync(string goalId, CancellationToken ct = default);
    public async Task<IReadOnlyDictionary<string, GoalSatisfaction>> GetOverallSatisfactionAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<(DataGoal Goal, GoalAction Action)>> GetRecommendedActionsAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/CarbonAwareDataManagementStrategy.cs
```csharp
public sealed class CarbonIntensity
{
}
    public required string RegionId { get; init; }
    public required string RegionName { get; init; }
    public double CurrentIntensity { get; init; }
    public double AverageIntensity { get; init; }
    public double RenewablePercentage { get; init; }
    public EnergySource PrimarySource { get; init; }
    public double? PredictedNextHourIntensity { get; init; }
    public DateTime Timestamp { get; init; };
    public (DateTime Start, DateTime End)? LowCarbonWindow { get; init; }
}
```
```csharp
public sealed class CarbonFootprint
{
}
    public double GramsCO2e { get; init; }
    public double EnergyKwh { get; init; }
    public string? Region { get; init; }
    public double CarbonIntensityUsed { get; init; }
    public double RenewableOffsetGrams { get; init; }
    public double NetGramsCO2e;;
}
```
```csharp
public sealed class ScheduledOperation
{
}
    public required string OperationId { get; init; }
    public required string OperationType { get; init; }
    public string[]? ObjectIds { get; init; }
    public double EstimatedEnergyKwh { get; init; }
    public string? PreferredRegion { get; init; }
    public TimeSpan MaxDelay { get; init; }
    public DateTime? ScheduledTime { get; set; }
    public string? ScheduleReason { get; set; }
    public CarbonFootprint? EstimatedFootprint { get; set; }
    public DateTime QueuedAt { get; init; };
}
```
```csharp
public sealed class CarbonAwareDataManagementStrategy : AiEnhancedStrategyBase
{
}
    public CarbonAwareDataManagementStrategy();
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void UpdateRegionIntensity(CarbonIntensity intensity);
    public CarbonIntensity? GetRegionIntensity(string regionId);
    public IReadOnlyList<CarbonIntensity> GetRegionsByIntensity();
    public CarbonIntensity? GetGreenestRegion();
    public CarbonFootprint CalculateFootprint(string operationType, long dataSizeBytes, string regionId);
    public void RecordOperationFootprint(string operationId, CarbonFootprint footprint);
    public async Task<ScheduledOperation> ScheduleOperationAsync(ScheduledOperation operation, CancellationToken ct = default);
    public IReadOnlyList<ScheduledOperation> GetPendingOperations();
    public IReadOnlyList<ScheduledOperation> GetReadyOperations();
    public bool RemoveOperation(string operationId);
    public Dictionary<string, object> GetCarbonStatistics();
    public Dictionary<string, object> GenerateSustainabilityReport();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/SelfOrganizingDataStrategy.cs
```csharp
public sealed class CategoryAssignment
{
}
    public required string PrimaryCategory { get; init; }
    public IReadOnlyList<string> SecondaryCategories { get; init; };
    public double Confidence { get; init; }
    public bool IsAiGenerated { get; init; }
}
```
```csharp
public sealed class TagSuggestion
{
}
    public required string Tag { get; init; }
    public double Relevance { get; init; }
    public string Source { get; init; };
}
```
```csharp
public sealed class StructureSuggestion
{
}
    public required string SuggestedPath { get; init; }
    public double Confidence { get; init; }
    public string? Reasoning { get; init; }
    public IReadOnlyList<string> Alternatives { get; init; };
}
```
```csharp
public sealed class OrganizationResult
{
}
    public required string ObjectId { get; init; }
    public CategoryAssignment? Category { get; init; }
    public IReadOnlyList<TagSuggestion> SuggestedTags { get; init; };
    public StructureSuggestion? Structure { get; init; }
    public IReadOnlyList<string> RelatedObjects { get; init; };
    public bool UsedAi { get; init; }
    public TimeSpan Duration { get; init; }
}
```
```csharp
public sealed class OrganizableContent
{
}
    public required string ObjectId { get; init; }
    public string? Filename { get; init; }
    public string? TextContent { get; init; }
    public string? ContentType { get; init; }
    public string[]? ExistingTags { get; init; }
    public string? ExistingCategory { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class SelfOrganizingDataStrategy : AiEnhancedStrategyBase
{
}
    public SelfOrganizingDataStrategy() : this(GetDefaultCategories(), TimeSpan.FromHours(24));
    public SelfOrganizingDataStrategy(string[] categories, TimeSpan cacheTtl);
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public IReadOnlyList<string> Categories;;
    public async Task<OrganizationResult> OrganizeAsync(OrganizableContent content, bool forceRefresh = false, CancellationToken ct = default);
    public async Task<IReadOnlyList<TagSuggestion>> GetTagSuggestionsAsync(string textContent, int maxTags = 10, CancellationToken ct = default);
    public async Task<CategoryAssignment> CategorizeAsync(OrganizableContent content, CancellationToken ct = default);
    public async Task<StructureSuggestion> GetStructureSuggestionAsync(OrganizableContent content, CancellationToken ct = default);
    public async Task<IReadOnlyList<(string ObjectId, double Similarity)>> FindRelatedAsync(OrganizableContent content, int maxResults = 10, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/CostAwareDataPlacementStrategy.cs
```csharp
public sealed class StoragePricing
{
}
    public required string TierId { get; init; }
    public required string Name { get; init; }
    public required decimal StorageCostPerGBMonth { get; init; }
    public decimal ReadCostPer10K { get; init; }
    public decimal WriteCostPer10K { get; init; }
    public decimal RetrievalCostPerGB { get; init; }
    public int MinStorageDays { get; init; }
    public decimal EarlyDeletionCostPerGB { get; init; }
    public double TypicalLatencyMs { get; init; }
}
```
```csharp
public sealed class CostAnalysis
{
}
    public required string ObjectId { get; init; }
    public required string CurrentTier { get; init; }
    public decimal CurrentMonthlyCost { get; init; }
    public string? RecommendedTier { get; init; }
    public decimal? ProjectedMonthlyCost { get; init; }
    public decimal MonthlySavings { get; init; }
    public decimal AnnualSavings { get; init; }
    public decimal MigrationCost { get; init; }
    public double? PaybackMonths { get; init; }
    public double Confidence { get; init; }
    public bool UsedAi { get; init; }
}
```
```csharp
public sealed class BudgetConstraint
{
}
    public required decimal MonthlyBudget { get; init; }
    public decimal CurrentSpend { get; init; }
    public bool IsHardLimit { get; init; }
    public decimal WarningThreshold { get; init; };
    public decimal AlertThreshold { get; init; };
}
```
```csharp
public sealed class CostAwareDataPlacementStrategy : AiEnhancedStrategyBase
{
}
    public CostAwareDataPlacementStrategy();
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterTier(StoragePricing tier);
    public IReadOnlyList<StoragePricing> GetTiers();;
    public void SetBudget(BudgetConstraint budget);
    public (decimal Budget, decimal CurrentSpend, double UtilizationPercent, string Status)? GetBudgetStatus();
    public void RecordObjectMetrics(string objectId, string currentTier, long sizeBytes, long readOps = 0, long writeOps = 0);
    public async Task<CostAnalysis?> AnalyzeCostAsync(string objectId, bool forceRefresh = false, CancellationToken ct = default);
    public async Task<IReadOnlyList<CostAnalysis>> GetOptimizationRecommendationsAsync(decimal minSavings = 0.01m, CancellationToken ct = default);
    public async Task<(decimal MonthlySavings, decimal AnnualSavings)> GetTotalPotentialSavingsAsync(CancellationToken ct = default);
    public decimal GetCurrentMonthlyCost();
    public bool WouldExceedBudget(string tierId, long sizeBytes, long estimatedMonthlyReadOps, long estimatedMonthlyWriteOps);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/PredictiveDataLifecycleStrategy.cs
```csharp
public sealed class LifecyclePrediction
{
}
    public required string ObjectId { get; init; }
    public ImportanceLevel Importance { get; init; }
    public LifecycleAction RecommendedAction { get; init; }
    public double Confidence { get; init; }
    public double FutureAccessProbability { get; init; }
    public TimeSpan? PredictedNextAccess { get; init; }
    public TimeSpan? RecommendedRetention { get; init; }
    public string? Reasoning { get; init; }
    public double RiskScore { get; init; }
    public decimal? EstimatedCostImpact { get; init; }
    public bool IsAiGenerated { get; init; }
    public DateTime GeneratedAt { get; init; };
}
```
```csharp
public sealed class DataObjectInfo
{
}
    public required string ObjectId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastAccessedAt { get; init; }
    public DateTime? LastModifiedAt { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public StorageTier CurrentTier { get; init; }
    public long AccessCount { get; init; }
    public bool HasComplianceRequirements { get; init; }
    public TimeSpan? MinimumRetention { get; init; }
    public string? OwnerId { get; init; }
    public string[]? Tags { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class PredictiveDataLifecycleStrategy : AiEnhancedStrategyBase
{
}
    public PredictiveDataLifecycleStrategy() : this(TimeSpan.FromHours(6));
    public PredictiveDataLifecycleStrategy(TimeSpan predictionCacheTtl);
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterObject(DataObjectInfo info);
    public void RecordAccess(string objectId, bool isWrite = false);
    public async Task<LifecyclePrediction?> GetPredictionAsync(string objectId, bool forceRefresh = false, CancellationToken ct = default);
    public async Task<IReadOnlyDictionary<string, LifecyclePrediction>> GetAllPredictionsAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<LifecyclePrediction>> GetActionableItemsAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<LifecyclePrediction>> GetByImportanceAsync(ImportanceLevel minImportance, CancellationToken ct = default);
    public async Task<IReadOnlyList<LifecyclePrediction>> GetDeletionCandidatesAsync(double minConfidence = 0.8, CancellationToken ct = default);
    public bool RemoveObject(string objectId);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/GravityAwarePlacementIntegration.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity message bus wiring")]
public sealed class GravityAwarePlacementIntegration
{
}
    public GravityAwarePlacementIntegration(IPlacementOptimizer? optimizer = null, GravityScoringWeights? weights = null);
    public GravityScoringWeights Weights;;
    public async Task<TieringRecommendation> ComputeTieringRecommendationAsync(string objectKey, CancellationToken ct = default);
    public async Task<IReadOnlyList<TieringRecommendation>> ComputeBatchTieringAsync(IReadOnlyList<string> objectKeys, CancellationToken ct = default);
    public async Task<bool> ShouldProtectFromDeletionAsync(string objectKey, double protectionThreshold = 0.7, CancellationToken ct = default);
    public async Task<double> ComputeMigrationUrgencyAsync(string objectKey, CancellationToken ct = default);
}
```
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-gravity message bus wiring")]
public sealed record TieringRecommendation
{
}
    public string ObjectKey { get; init; };
    public double GravityScore { get; init; }
    public double AccessFrequency { get; init; }
    public TieringAction RecommendedAction { get; init; }
    public string Reason { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/AiEnhancedStrategyBase.cs
```csharp
public sealed class AiOperationResult
{
}
    public required bool Success { get; init; }
    public bool UsedAi { get; init; }
    public bool UsedFallback { get; init; }
    public double Confidence { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public static AiOperationResult OkWithAi(double confidence, TimeSpan duration, Dictionary<string, object>? data = null);;
    public static AiOperationResult OkWithFallback(TimeSpan duration, Dictionary<string, object>? data = null);;
    public static AiOperationResult Failed(string error, TimeSpan duration);;
}
```
```csharp
public sealed class AiEnhancedStatistics
{
}
    public long TotalOperations { get; set; }
    public long AiOperations { get; set; }
    public long FallbackOperations { get; set; }
    public long FailedOperations { get; set; }
    public double AverageConfidence { get; set; }
    public double AverageTimeMs { get; set; }
    public double TotalTimeMs { get; set; }
}
```
```csharp
public interface IAiEnhancedStrategy : IDataManagementStrategy
{
}
    AiEnhancedCategory AiCategory { get; }
    bool IsAiAvailable { get; }
    IntelligenceCapabilities RequiredCapabilities { get; }
    AiEnhancedStatistics GetAiStatistics();;
}
```
```csharp
public abstract class AiEnhancedStrategyBase : DataManagementStrategyBase, IAiEnhancedStrategy
{
}
    protected new IMessageBus? MessageBus { get; set; }
    protected IntelligenceContext DefaultContext { get; set; };
    public override DataManagementCategory Category;;
    public abstract AiEnhancedCategory AiCategory { get; }
    public bool IsAiAvailable;;
    public abstract IntelligenceCapabilities RequiredCapabilities { get; }
    protected bool HasRequiredCapabilities();
    protected bool HasCapability(IntelligenceCapabilities capability);
    public AiEnhancedStatistics GetAiStatistics();
    protected void RecordAiOperation(bool usedAi, bool failed, double confidence, double timeMs);
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected virtual Task InitializeAiCoreAsync(CancellationToken ct);;
    protected override Task DisposeCoreAsync();
    protected async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default);
    protected async Task<MessageResponse?> SendAiRequestAsync(string topic, Dictionary<string, object> payload, TimeSpan? timeout = null, CancellationToken ct = default);
    protected async Task<float[][]?> RequestEmbeddingsAsync(string[] texts, IntelligenceContext? context = null, CancellationToken ct = default);
    protected async Task<(string Category, double Confidence)[]?> RequestClassificationAsync(string text, string[] categories, bool multiLabel = false, IntelligenceContext? context = null, CancellationToken ct = default);
    protected async Task<(object? Prediction, double Confidence, Dictionary<string, object> Metadata)?> RequestPredictionAsync(string predictionType, Dictionary<string, object> inputData, IntelligenceContext? context = null, CancellationToken ct = default);
    protected async Task<(bool ContainsPII, (string Type, string Value, double Confidence)[] Items)?> RequestPIIDetectionAsync(string text, IntelligenceContext? context = null, CancellationToken ct = default);
    protected async Task<(string Text, string Type, double Confidence)[]?> RequestEntityExtractionAsync(string text, string[]? entityTypes = null, IntelligenceContext? context = null, CancellationToken ct = default);
    protected async Task<string?> RequestCompletionAsync(string prompt, string? systemMessage = null, IntelligenceContext? context = null, CancellationToken ct = default);
    protected async Task<double?> RequestSimilarityScoreAsync(float[] embedding1, float[] embedding2, CancellationToken ct = default);
    protected static double CalculateCosineSimilarity(float[] a, float[] b);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/ComplianceAwareLifecycleStrategy.cs
```csharp
public sealed class ComplianceAnalysis
{
}
    public required string ObjectId { get; init; }
    public SensitivityLevel SensitivityLevel { get; init; }
    public IReadOnlyList<ComplianceFramework> ApplicableFrameworks { get; init; };
    public bool ContainsPII { get; init; }
    public IReadOnlyList<string> PIITypes { get; init; };
    public TimeSpan? RequiredRetention { get; init; }
    public string? DisposalMethod { get; init; }
    public IReadOnlyList<string> RequiredControls { get; init; };
    public IReadOnlyList<ComplianceViolation> Violations { get; init; };
    public double ComplianceScore { get; init; }
    public double Confidence { get; init; }
    public bool UsedAi { get; init; }
    public DateTime AnalyzedAt { get; init; };
}
```
```csharp
public sealed class ComplianceViolation
{
}
    public required string ViolationType { get; init; }
    public required string Description { get; init; }
    public double Severity { get; init; }
    public ComplianceFramework? Framework { get; init; }
    public string? Remediation { get; init; }
}
```
```csharp
public sealed class RetentionPolicy
{
}
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public ComplianceFramework[] Frameworks { get; init; };
    public TimeSpan MinRetention { get; init; }
    public TimeSpan? MaxRetention { get; init; }
    public string DisposalMethod { get; init; };
    public bool AllowsLegalHold { get; init; };
    public string[]? ApplicableContentTypes { get; init; }
    public SensitivityLevel[]? ApplicableSensitivityLevels { get; init; }
}
```
```csharp
public sealed class ComplianceAwareLifecycleStrategy : AiEnhancedStrategyBase
{
}
    public ComplianceAwareLifecycleStrategy() : this(TimeSpan.FromHours(24));
    public ComplianceAwareLifecycleStrategy(TimeSpan cacheTtl);
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterPolicy(RetentionPolicy policy);
    public IReadOnlyList<RetentionPolicy> GetPolicies();;
    public async Task<ComplianceAnalysis> AnalyzeAsync(string objectId, string? textContent, string? contentType = null, Dictionary<string, object>? metadata = null, bool forceRefresh = false, CancellationToken ct = default);
    public RetentionPolicy? GetApplicablePolicy(ComplianceAnalysis analysis);
    public async Task<(bool CanDelete, string Reason)> CanDeleteAsync(string objectId, DateTime createdAt, CancellationToken ct = default);
    public IReadOnlyList<(string ObjectId, string Reason)> GetObjectsRequiringDeletion(IReadOnlyDictionary<string, DateTime> objectCreationDates);
    public Dictionary<string, object> GenerateComplianceReport();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/AiEnhanced/AiDataOrchestratorStrategy.cs
```csharp
public sealed class PlacementRecommendation
{
}
    public required StorageTier RecommendedTier { get; init; }
    public double Confidence { get; init; }
    public double PredictedAccessFrequency { get; init; }
    public TimeSpan? PredictedNextAccess { get; init; }
    public string? Reasoning { get; init; }
    public bool RecommendsCrossTierOptimization { get; init; }
    public int SuggestedReplicationFactor { get; init; };
}
```
```csharp
public sealed class AccessPattern
{
}
    public required string ObjectId { get; init; }
    public long ReadCount { get; init; }
    public long WriteCount { get; init; }
    public DateTime? LastAccess { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public DateTime CreatedAt { get; init; }
    public StorageTier CurrentTier { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed class AiDataOrchestratorStrategy : AiEnhancedStrategyBase
{
}
    public AiDataOrchestratorStrategy() : this(TimeSpan.FromDays(7));
    public AiDataOrchestratorStrategy(TimeSpan observationWindow);
    public override string StrategyId;;
    public override string DisplayName;;
    public override AiEnhancedCategory AiCategory;;
    public override IntelligenceCapabilities RequiredCapabilities;;
    public override DataManagementCapabilities Capabilities { get; };
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RecordAccess(string objectId, bool isWrite, long sizeBytes, string? contentType = null, StorageTier currentTier = StorageTier.Hot, Dictionary<string, object>? metadata = null);
    public async Task<PlacementRecommendation?> GetPlacementRecommendationAsync(string objectId, bool forceRefresh = false, CancellationToken ct = default);
    public async Task<IReadOnlyDictionary<string, PlacementRecommendation>> AnalyzeAllAsync(CancellationToken ct = default);
    public async Task<IReadOnlyList<(string ObjectId, StorageTier CurrentTier, StorageTier RecommendedTier)>> GetMigrationCandidatesAsync(CancellationToken ct = default);
    public void PruneOldData();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataManagement/Strategies/EventSourcing/EventSourcingStrategies.cs
```csharp
public sealed record StoredEvent
{
}
    public required string EventId { get; init; }
    public required string StreamId { get; init; }
    public required string EventType { get; init; }
    public int SchemaVersion { get; init; };
    public long StreamPosition { get; init; }
    public long GlobalPosition { get; init; }
    public required byte[] Data { get; init; }
    public byte[]? Metadata { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public string? ActorId { get; init; }
    public string? ContentHash { get; init; }
    public long? ExpectedVersion { get; init; }
}
```
```csharp
public sealed record EventMetadata
{
}
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public string? ActorId { get; init; }
    public string? TenantId { get; init; }
    public string? SourceSystem { get; init; }
    public Dictionary<string, object>? CustomHeaders { get; init; }
}
```
```csharp
public sealed record AppendResult
{
}
    public bool Success { get; init; }
    public long NextExpectedVersion { get; init; }
    public long GlobalPosition { get; init; }
    public string? Error { get; init; }
    public int EventsAppended { get; init; }
}
```
```csharp
public sealed record ReadOptions
{
}
    public long FromPosition { get; init; };
    public int MaxCount { get; init; };
    public bool ReadForward { get; init; };
    public bool ResolveLinkTos { get; init; };
    public string[]? EventTypeFilter { get; init; }
    public DateTimeOffset? FromTimestamp { get; init; }
    public DateTimeOffset? ToTimestamp { get; init; }
}
```
```csharp
public sealed record AggregateSnapshot
{
}
    public required string AggregateId { get; init; }
    public required string AggregateType { get; init; }
    public required byte[] State { get; init; }
    public long Version { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public string? ContentHash { get; init; }
    public long EventCount { get; init; }
}
```
```csharp
public sealed record EventSchema
{
}
    public required string EventType { get; init; }
    public required int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
    public bool IsDeprecated { get; init; }
    public string? UpgradeScript { get; init; }
    public string? DowngradeScript { get; init; }
}
```
```csharp
public sealed record SchemaCompatibility
{
}
    public bool IsCompatible { get; init; }
    public CompatibilityLevel Level { get; init; }
    public string[] BreakingChanges { get; init; };
    public string[] Warnings { get; init; };
}
```
```csharp
public sealed record Command
{
}
    public required string CommandId { get; init; }
    public required string CommandType { get; init; }
    public required string AggregateId { get; init; }
    public required byte[] Payload { get; init; }
    public EventMetadata? Metadata { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public long? ExpectedVersion { get; init; }
}
```
```csharp
public sealed record CommandResult
{
}
    public bool Success { get; init; }
    public string? Error { get; init; }
    public StoredEvent[]? Events { get; init; }
    public long NewVersion { get; init; }
}
```
```csharp
public sealed record ProjectionDefinition
{
}
    public required string ProjectionId { get; init; }
    public required string ProjectionName { get; init; }
    public required string[] SubscribedEventTypes { get; init; }
    public bool IsEnabled { get; init; };
    public long CheckpointPosition { get; init; }
    public DateTimeOffset LastProcessedAt { get; init; }
    public ProjectionStatus Status { get; init; };
}
```
```csharp
public abstract class AggregateRoot
{
}
    public string Id { get; protected set; };
    public long Version { get; protected set; };
    public IReadOnlyList<StoredEvent> UncommittedEvents;;
    public void ClearUncommittedEvents();;
    protected void ApplyChange(object @event);
    public void LoadFromHistory(IEnumerable<StoredEvent> history);
    protected abstract void ApplyEvent(object @event);;
    protected abstract object DeserializeEvent(StoredEvent storedEvent);;
}
```
```csharp
public sealed class EventStoreStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<AppendResult> AppendToStreamAsync(string streamId, IEnumerable<StoredEvent> events, long? expectedVersion = null, CancellationToken ct = default);
    public Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(string streamId, ReadOptions? options = null, CancellationToken ct = default);
    public Task<IReadOnlyList<StoredEvent>> ReadAllAsync(ReadOptions? options = null, CancellationToken ct = default);
    public Task<long> GetStreamVersionAsync(string streamId, CancellationToken ct = default);
    public IDisposable Subscribe(string streamId, Action<StoredEvent> handler);
    public IDisposable SubscribeToAll(Action<StoredEvent> handler);
}
```
```csharp
private sealed class SubscriptionHandle : IDisposable
{
}
    public SubscriptionHandle(Action unsubscribe);;
    public void Dispose();;
}
```
```csharp
public sealed class EventStreamingStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public async Task<StreamingSubscription> CreateSubscriptionAsync(string subscriptionId, string streamId, long fromPosition = 0, StreamingOptions? options = null, CancellationToken ct = default);
    public Task<ConsumerGroupMembership> JoinConsumerGroupAsync(string groupId, string consumerId, string[] subscribedStreams, CancellationToken ct = default);
    public async Task<bool> PushEventAsync(StoredEvent @event, CancellationToken ct = default);
    public async IAsyncEnumerable<StoredEvent> CatchupAsync(string streamId, long fromPosition, [EnumeratorCancellation] CancellationToken ct = default);
    public Task AcknowledgeAsync(string subscriptionId, long position, CancellationToken ct = default);
}
```
```csharp
public sealed record StreamingOptions
{
}
    public int BufferSize { get; init; };
    public TimeSpan AckTimeout { get; init; };
    public int MaxRetries { get; init; };
    public bool AutoAck { get; init; };
}
```
```csharp
public sealed record StreamingSubscription
{
}
    public required string SubscriptionId { get; init; }
    public required string StreamId { get; init; }
    public long FromPosition { get; init; }
    public SubscriptionStatus Status { get; init; }
}
```
```csharp
internal sealed class StreamingConsumer
{
}
    public required string SubscriptionId { get; init; }
    public required string StreamId { get; init; }
    public long CurrentPosition { get; set; }
    public required StreamingOptions Options { get; init; }
    public ConsumerStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAckAt { get; set; }
    public long EventsDelivered { get; set; }
}
```
```csharp
internal sealed class ConsumerGroup
{
}
    public required string GroupId { get; init; }
    public required List<string> SubscribedStreams { get; init; }
    public required BoundedDictionary<string, ConsumerMember> Members { get; init; }
}
```
```csharp
internal sealed class ConsumerMember
{
}
    public required string ConsumerId { get; init; }
    public DateTimeOffset JoinedAt { get; init; }
    public ConsumerStatus Status { get; set; }
    public int[] AssignedPartitions { get; set; };
    public long EventsDelivered { get; set; }
}
```
```csharp
public sealed record ConsumerGroupMembership
{
}
    public required string GroupId { get; init; }
    public required string ConsumerId { get; init; }
    public int[] AssignedPartitions { get; init; };
    public MembershipStatus Status { get; init; }
}
```
```csharp
public sealed class EventReplayStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<ReplaySession> StartReplayAsync(ReplayRequest request, CancellationToken ct = default);
    public async IAsyncEnumerable<ReplayedEvent> ReplayEventsAsync(string sessionId, IEnumerable<StoredEvent> events, [EnumeratorCancellation] CancellationToken ct = default);
    public async Task<ReplayResult> ReplayToPointInTimeAsync(string streamId, DateTimeOffset targetTime, Func<StoredEvent, Task> handler, IEnumerable<StoredEvent> events, CancellationToken ct = default);
    public async Task<WhatIfResult> WhatIfAnalysisAsync(string streamId, IEnumerable<StoredEvent> events, Func<StoredEvent, StoredEvent?> modifier, Func<StoredEvent, Task<object?>> stateBuilder, CancellationToken ct = default);
    public Task PauseReplayAsync(string sessionId);
    public Task ResumeReplayAsync(string sessionId);
    public Task StopReplayAsync(string sessionId);
}
```
```csharp
public sealed record ReplayRequest
{
}
    public required string StreamId { get; init; }
    public long FromPosition { get; init; };
    public long? ToPosition { get; init; }
    public double Speed { get; init; };
    public long[]? Breakpoints { get; init; }
}
```
```csharp
public sealed class ReplaySession
{
}
    public required string SessionId { get; init; }
    public required string StreamId { get; init; }
    public long FromPosition { get; init; }
    public long? ToPosition { get; init; }
    public long CurrentPosition { get; set; }
    public double Speed { get; init; }
    public ReplayStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? PausedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long EventsReplayed { get; set; }
    public List<long> Breakpoints { get; init; };
}
```
```csharp
public sealed record ReplayedEvent
{
}
    public required StoredEvent Event { get; init; }
    public DateTimeOffset ReplayTimestamp { get; init; }
    public double ElapsedMs { get; init; }
    public long SessionPosition { get; init; }
}
```
```csharp
public sealed record ReplayResult
{
}
    public bool Success { get; init; }
    public int EventsReplayed { get; init; }
    public long FinalPosition { get; init; }
    public double DurationMs { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record WhatIfResult
{
}
    public int OriginalEventCount { get; init; }
    public int ModifiedEventCount { get; init; }
    public int FilteredEventCount { get; init; }
    public List<string> StateDifferences { get; init; };
}
```
```csharp
public sealed class SnapshotStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SnapshotResult> SaveSnapshotAsync(string aggregateId, string aggregateType, byte[] state, long version, long eventCount, CancellationToken ct = default);
    public Task<AggregateSnapshot?> LoadSnapshotAsync(string aggregateId, CancellationToken ct = default);
    public Task<bool> ShouldSnapshotAsync(string aggregateId, string aggregateType, long currentVersion, long eventsSinceSnapshot, TimeSpan timeSinceSnapshot, CancellationToken ct = default);
    public Task SetPolicyAsync(string aggregateType, SnapshotPolicy policy, CancellationToken ct = default);
    public Task<int> CleanupSnapshotsAsync(TimeSpan retentionPeriod, CancellationToken ct = default);
}
```
```csharp
public sealed record SnapshotPolicy
{
}
    public int EventCountThreshold { get; init; };
    public TimeSpan? TimeThreshold { get; init; }
    public bool EnableAdaptive { get; init; };
    public double AdaptiveEventRateThreshold { get; init; };
    public bool EnableCompression { get; init; };
    public TimeSpan RetentionPeriod { get; init; };
    public int MaxSnapshotsPerAggregate { get; init; };
}
```
```csharp
public sealed record SnapshotResult
{
}
    public bool Success { get; init; }
    public long SnapshotVersion { get; init; }
    public long SizeBytes { get; init; }
    public double CompressionRatio { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed class EventVersioningStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task RegisterSchemaAsync(EventSchema schema, CancellationToken ct = default);
    public Task RegisterUpcasterAsync(string eventType, int fromVersion, int toVersion, Func<byte[], int, byte[]> upcaster, CancellationToken ct = default);
    public Task RegisterDowncasterAsync(string eventType, int fromVersion, int toVersion, Func<byte[], int, byte[]> downcaster, CancellationToken ct = default);
    public Task<VersionedEventData> UpcastToLatestAsync(string eventType, byte[] data, int currentVersion, CancellationToken ct = default);
    public Task<VersionedEventData> DowncastToVersionAsync(string eventType, byte[] data, int currentVersion, int targetVersion, CancellationToken ct = default);
    public Task<int> GetLatestVersionAsync(string eventType, CancellationToken ct = default);
    public Task<IReadOnlyList<EventSchema>> GetSchemaHistoryAsync(string eventType, CancellationToken ct = default);
}
```
```csharp
public sealed record VersionedEventData
{
}
    public required byte[] Data { get; init; }
    public int Version { get; init; }
    public bool WasTransformed { get; init; }
    public string[]? Transformations { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed class SchemaEvolutionStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task<SchemaRegistrationResult> RegisterSchemaAsync(string subject, string schemaDefinition, CompatibilityMode compatibilityMode = CompatibilityMode.Backward, CancellationToken ct = default);
    public Task<SchemaCompatibility> CheckCompatibilityAsync(string subject, string newSchemaDefinition, CancellationToken ct = default);
    public Task<SchemaMigrationPath> GenerateMigrationPathAsync(string subject, int fromVersion, int toVersion, CancellationToken ct = default);
    public Task<InferredSchema> InferSchemaAsync(byte[] sampleData, CancellationToken ct = default);
    public Task<bool> DeprecateVersionAsync(string subject, int version, CancellationToken ct = default);
}
```
```csharp
internal sealed class SchemaRegistry
{
}
    public required string Subject { get; init; }
    public Dictionary<int, RegisteredSchema> Schemas { get; };
    public int LatestVersion { get; set; }
}
```
```csharp
internal sealed class RegisteredSchema
{
}
    public required string Subject { get; init; }
    public required int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public CompatibilityMode CompatibilityMode { get; init; }
    public bool IsDeprecated { get; set; }
    public DateTimeOffset? DeprecatedAt { get; set; }
}
```
```csharp
public sealed record SchemaRegistrationResult
{
}
    public bool Success { get; init; }
    public string? SchemaId { get; init; }
    public int Version { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record SchemaMigrationPath
{
}
    public bool Success { get; init; }
    public string? Subject { get; init; }
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public MigrationStep[] Steps { get; init; };
    public string? Error { get; init; }
}
```
```csharp
public sealed record MigrationStep
{
}
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public MigrationDirection Direction { get; init; }
    public MigrationComplexity EstimatedComplexity { get; init; }
}
```
```csharp
public sealed record InferredSchema
{
}
    public bool Success { get; init; }
    public string? SchemaType { get; init; }
    public Dictionary<string, InferredProperty>? Properties { get; init; }
    public DateTimeOffset InferredAt { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record InferredProperty
{
}
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool IsNullable { get; init; }
}
```
```csharp
public sealed class CqrsStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task RegisterCommandHandlerAsync(string commandType, Func<Command, CancellationToken, Task<CommandResult>> handler, CancellationToken ct = default);
    public Task RegisterValidationAsync(string commandType, CommandValidation validation, CancellationToken ct = default);
    public async Task<CommandResult> DispatchAsync(Command command, CancellationToken ct = default);
    public Task EnqueueAsync(Command command, CancellationToken ct = default);
    public async Task<int> ProcessQueueAsync(int maxCommands = 100, CancellationToken ct = default);
    public Task UpdateReadModelAsync<T>(string modelId, T model, CancellationToken ct = default)
    where T : class;
    public Task<T?> GetReadModelAsync<T>(string modelId, CancellationToken ct = default)
    where T : class;
    public Task<IReadOnlyList<T>> QueryReadModelsAsync<T>(Func<T, bool> predicate, CancellationToken ct = default)
    where T : class;
}
```
```csharp
public sealed class CommandValidation
{
}
    public required Func<Command, CancellationToken, Task<ValidationResult>> ValidateAsync { get; init; }
}
```
```csharp
public sealed record ValidationResult
{
}
    public bool IsValid { get; init; }
    public string[] Errors { get; init; };
}
```
```csharp
public sealed class EventAggregationStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task DefineAggregationAsync(AggregationDefinition definition, CancellationToken ct = default);
    public async Task<AggregationResult> AggregateAsync(string aggregationId, IEnumerable<StoredEvent> events, CancellationToken ct = default);
    public async Task<bool> UpdateMaterializedViewAsync<T>(string viewId, IEnumerable<StoredEvent> events, Func<T?, StoredEvent, T> folder, CancellationToken ct = default)
    where T : class;
    public Task<T?> GetMaterializedViewAsync<T>(string viewId, CancellationToken ct = default)
    where T : class;
    public Task<CompactionResult> CompactEventsAsync(IEnumerable<StoredEvent> events, Func<StoredEvent, string> keySelector, CancellationToken ct = default);
}
```
```csharp
public sealed record AggregationDefinition
{
}
    public required string AggregationId { get; init; }
    public required string[] EventTypes { get; init; }
    public required Func<IEnumerable<StoredEvent>, CancellationToken, Task<byte[]>> Aggregator { get; init; }
    public required string OutputEventType { get; init; }
    public string? OutputStreamId { get; init; }
    public TimeSpan? WindowSize { get; init; }
    public bool EmitOnWindow { get; init; };
}
```
```csharp
public sealed record AggregationResult
{
}
    public bool Success { get; init; }
    public int InputEventCount { get; init; }
    public int OutputEventCount { get; init; }
    public StoredEvent[] AggregatedEvents { get; init; };
    public string? Error { get; init; }
}
```
```csharp
public sealed record CompactionResult
{
}
    public bool Success { get; init; }
    public int OriginalCount { get; init; }
    public int CompactedCount { get; init; }
    public double ReductionRatio { get; init; }
    public StoredEvent[] CompactedEvents { get; init; };
}
```
```csharp
public sealed class ProjectionStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task DefineProjectionAsync(ProjectionDefinition definition, Func<StoredEvent, object?, CancellationToken, Task<object?>> handler, CancellationToken ct = default);
    public async Task<ProjectionResult> ProjectAsync(string projectionId, IEnumerable<StoredEvent> events, CancellationToken ct = default);
    public async Task<ProjectionResult> RebuildAsync(string projectionId, IEnumerable<StoredEvent> allEvents, CancellationToken ct = default);
    public Task<ProjectionState?> GetProjectionStateAsync(string projectionId, CancellationToken ct = default);
    public Task PauseProjectionAsync(string projectionId, CancellationToken ct = default);
    public Task ResumeProjectionAsync(string projectionId, CancellationToken ct = default);
    public Task<IReadOnlyList<ProjectionState>> GetAllProjectionStatesAsync(CancellationToken ct = default);
}
```
```csharp
public sealed class ProjectionState
{
}
    public required string ProjectionId { get; init; }
    public ProjectionStatus Status { get; set; }
    public long CheckpointPosition { get; set; }
    public DateTimeOffset LastProcessedAt { get; set; }
    public object? CurrentState { get; set; }
    public long EventsProcessed { get; set; }
    public int ErrorCount { get; set; }
}
```
```csharp
public sealed record ProjectionResult
{
}
    public bool Success { get; init; }
    public int EventsProcessed { get; init; }
    public long CheckpointPosition { get; init; }
    public string[] Errors { get; init; };
    public string? Error { get; init; }
}
```
```csharp
public sealed class DddEventSourcingStrategy : DataManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override DataManagementCategory Category;;
    public override DataManagementCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public Task RegisterBoundedContextAsync(BoundedContextDefinition context, CancellationToken ct = default);
    public Task RegisterDomainEventHandlerAsync<TEvent>(Func<TEvent, CancellationToken, Task> handler, CancellationToken ct = default)
    where TEvent : class;
    public async Task<SaveAggregateResult> SaveAggregateAsync<T>(T aggregate, EventStoreStrategy eventStore, CancellationToken ct = default)
    where T : AggregateRoot;
    public async Task<T?> LoadAggregateAsync<T>(string aggregateId, EventStoreStrategy eventStore, SnapshotStrategy? snapshotStore = null, CancellationToken ct = default)
    where T : AggregateRoot, new();
    public Task<SagaState> StartSagaAsync(SagaDefinition definition, object initialData, CancellationToken ct = default);
    public async Task<SagaStepResult> AdvanceSagaAsync(string sagaId, object eventData, CancellationToken ct = default);
    public async Task<SagaCompensationResult> CompensateSagaAsync(string sagaId, string reason, CancellationToken ct = default);
    public async Task<TranslatedEvent?> TranslateEventAsync(StoredEvent sourceEvent, string sourceContext, string targetContext, CancellationToken ct = default);
}
```
```csharp
public sealed record BoundedContextDefinition
{
}
    public required string ContextId { get; init; }
    public required string ContextName { get; init; }
    public string[] OwnedAggregates { get; init; };
    public string[] PublishedEvents { get; init; };
    public string[] ConsumedEvents { get; init; };
}
```
```csharp
public sealed record SagaDefinition
{
}
    public required string SagaType { get; init; }
    public required SagaStep[] Steps { get; init; }
}
```
```csharp
public sealed record SagaStep
{
}
    public required string StepName { get; init; }
    public required string CommandType { get; init; }
    public string? CompensationCommandType { get; init; }
    public TimeSpan? Timeout { get; init; }
}
```
```csharp
public sealed class SagaState
{
}
    public required string SagaId { get; init; }
    public required string SagaType { get; init; }
    public int CurrentStep { get; set; }
    public SagaStatus Status { get; set; }
    public object? Data { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public string? CompensationReason { get; set; }
}
```
```csharp
public sealed record SaveAggregateResult
{
}
    public bool Success { get; init; }
    public int EventsStored { get; init; }
    public long NewVersion { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record SagaStepResult
{
}
    public bool Success { get; init; }
    public int CurrentStep { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record SagaCompensationResult
{
}
    public bool Success { get; init; }
    public int StepsCompensated { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed record TranslatedEvent
{
}
    public required StoredEvent OriginalEvent { get; init; }
    public required string TranslatedEventType { get; init; }
    public required byte[] TranslatedData { get; init; }
    public required string SourceContext { get; init; }
    public required string TargetContext { get; init; }
}
```
