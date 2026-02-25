# Plugin: UltimateReplication
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateReplication

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/UltimateReplicationPlugin.cs
```csharp
public sealed class UltimateReplicationPlugin : DataWarehouse.SDK.Contracts.Hierarchy.ReplicationPluginBase
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public UltimateReplicationPlugin();
    public IReadOnlyCollection<string> GetRegisteredStrategies();;
    public EnhancedReplicationStrategyBase? GetStrategy(string name);;
    public void SetActiveStrategy(string strategyName);
    public EnhancedReplicationStrategyBase? GetActiveStrategy();;
    public EnhancedReplicationStrategyBase? SelectBestStrategy(ConsistencyModel? preferredConsistency = null, long? maxLagMs = null, bool requireMultiMaster = false, bool requireGeoAware = false);
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override async Task OnStopCoreAsync();
    public override async Task OnMessageAsync(PluginMessage message);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.replicate",
                DisplayName = $"{Name} - Replicate",
                Description = "Replicate data using selected strategy with AI-enhanced conflict prediction and routing",
                Category = SDK.Contracts.CapabilityCategory.Storage,
                SubCategory = "Replication",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "replication",
                    "data-sync",
                    "ai-enhanced",
                    "conflict-resolution"
                },
                SemanticDescription = "Advanced replication with 8 strategies, vector clocks, conflict resolution, and Intelligence-powered optimization"
            }
        };
        foreach (var(name, strategy)in _registry.GetAll())
        {
            var chars = strategy.Characteristics;
            var tags = new List<string>
            {
                "replication",
                "strategy",
                name.ToLowerInvariant()
            };
            if (chars.Capabilities.SupportsMultiMaster)
                tags.Add("multi-master");
            if (chars.Capabilities.IsGeoAware)
                tags.Add("geo-aware");
            if (chars.SupportsVectorClocks)
                tags.Add("vector-clock");
            if (chars.SupportsStreaming)
                tags.Add("streaming");
            if (chars.SupportsAutoConflictResolution)
                tags.Add("auto-conflict-resolution");
            var consistencyTags = chars.ConsistencyModel switch
            {
                ConsistencyModel.Eventual => "eventual-consistency",
                ConsistencyModel.Strong => "strong-consistency",
                ConsistencyModel.Causal => "causal-consistency",
                ConsistencyModel.BoundedStaleness => "bounded-staleness",
                ConsistencyModel.SessionConsistent => "session-consistency",
                ConsistencyModel.ReadYourWrites => "read-your-writes",
                ConsistencyModel.MonotonicReads => "monotonic-reads",
                ConsistencyModel.MonotonicWrites => "monotonic-writes",
                _ => "consistency"
            };
            tags.Add(consistencyTags);
            capabilities.Add(new RegisteredCapability { CapabilityId = $"{Id}.strategy.{name.ToLowerInvariant()}", DisplayName = $"{name} Replication", Description = chars.Description, Category = SDK.Contracts.CapabilityCategory.Storage, SubCategory = "Replication", PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = tags.ToArray(), Priority = chars.Capabilities.SupportsMultiMaster ? 60 : 50, Metadata = new Dictionary<string, object> { ["strategyId"] = name, ["consistencyModel"] = chars.ConsistencyModel.ToString(), ["typicalLagMs"] = chars.TypicalLagMs, ["consistencySlaMs"] = chars.ConsistencySlaMs, ["supportsMultiMaster"] = chars.Capabilities.SupportsMultiMaster, ["isGeoAware"] = chars.Capabilities.IsGeoAware, ["supportsVectorClocks"] = chars.SupportsVectorClocks, ["supportsAutoConflictResolution"] = chars.SupportsAutoConflictResolution, ["supportsDeltaSync"] = chars.SupportsDeltaSync, ["supportsStreaming"] = chars.SupportsStreaming, ["conflictResolutionMethods"] = chars.Capabilities.ConflictResolutionMethods.Select(m => m.ToString()).ToArray() }, SemanticDescription = $"Replicate data using {name} strategy with {chars.ConsistencyModel} consistency. " + $"Typical lag: {chars.TypicalLagMs}ms. " + $"Supports: {(chars.Capabilities.SupportsMultiMaster ? "multi-master, " : "")}" + $"{(chars.Capabilities.IsGeoAware ? "geo-aware, " : "")}" + $"{(chars.SupportsVectorClocks ? "vector-clocks, " : "")}" + $"{(chars.SupportsAutoConflictResolution ? "auto-conflict-resolution" : "manual-conflict-resolution")}" });
        }

        return capabilities;
    }
}
    public override async Task<Dictionary<string, object>> ReplicateAsync(string key, string[] targetNodes, CancellationToken ct = default);
    public override Task<Dictionary<string, object>> GetSyncStatusAsync(string key, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/ReplicationStrategyRegistry.cs
```csharp
public sealed class ReplicationStrategyRegistry
{
}
    public IReadOnlyCollection<string> RegisteredStrategies;;
    public int Count;;
    public void Register(EnhancedReplicationStrategyBase strategy);
    public bool Unregister(string strategyName);
    public EnhancedReplicationStrategyBase? Get(string strategyName);
    public IEnumerable<KeyValuePair<string, EnhancedReplicationStrategyBase>> GetAll();
    public IEnumerable<EnhancedReplicationStrategyBase> GetByConsistencyModel(ConsistencyModel model);
    public IEnumerable<EnhancedReplicationStrategyBase> GetByCapability(string capability);
    public EnhancedReplicationStrategyBase? SelectBestStrategy(ConsistencyModel? preferredConsistency = null, long? maxLagMs = null, bool requireMultiMaster = false, bool requireGeoAware = false);
    public int DiscoverFromAssembly(Assembly? assembly = null);
    public IEnumerable<StrategySummary> GetSummary();
}
```
```csharp
public sealed class StrategySummary
{
}
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ConsistencyModel ConsistencyModel { get; init; }
    public required long TypicalLagMs { get; init; }
    public required bool SupportsMultiMaster { get; init; }
    public required bool IsGeoAware { get; init; }
    public required bool SupportsAutoConflictResolution { get; init; }
    public required ConflictResolutionMethod[] ConflictResolutionMethods { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/ReplicationStrategyBase.cs
```csharp
public sealed record ReplicationCharacteristics
{
}
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required ConsistencyModel ConsistencyModel { get; init; }
    public required ReplicationCapabilities Capabilities { get; init; }
    public bool SupportsAutoConflictResolution { get; init; }
    public bool SupportsVectorClocks { get; init; }
    public bool SupportsDeltaSync { get; init; }
    public bool SupportsStreaming { get; init; }
    public long TypicalLagMs { get; init; }
    public long ConsistencySlaMs { get; init; }
}
```
```csharp
public sealed class EnhancedVectorClock
{
}
    public IReadOnlyDictionary<string, long> Entries;;
    public EnhancedVectorClock();
    public EnhancedVectorClock(IReadOnlyDictionary<string, long> entries);
    public long this[string nodeId] => _clock.GetValueOrDefault(nodeId, 0);;
    public void Increment(string nodeId);
    public EnhancedVectorClock IncrementInPlace(string nodeId);
    public void Merge(EnhancedVectorClock other);
    public bool HappensBefore(EnhancedVectorClock other);
    public bool IsConcurrentWith(EnhancedVectorClock other);
    public EnhancedVectorClock Clone();
    public string ToJson();;
    public static EnhancedVectorClock FromJson(string json);
    public override string ToString();
}
```
```csharp
public sealed class EnhancedReplicationConflict
{
}
    public required string DataId { get; init; }
    public required EnhancedVectorClock LocalVersion { get; init; }
    public required EnhancedVectorClock RemoteVersion { get; init; }
    public required ReadOnlyMemory<byte> LocalData { get; init; }
    public required ReadOnlyMemory<byte> RemoteData { get; init; }
    public required string LocalNodeId { get; init; }
    public required string RemoteNodeId { get; init; }
    public DateTimeOffset DetectedAt { get; init; };
    public IReadOnlyDictionary<string, string>? LocalMetadata { get; init; }
    public IReadOnlyDictionary<string, string>? RemoteMetadata { get; init; }
}
```
```csharp
public sealed class ReplicationLagTracker
{
}
    public void RecordLag(string nodeId, TimeSpan lag);
    public TimeSpan GetCurrentLag(string nodeId);
    public (TimeSpan Current, TimeSpan Max, TimeSpan Min, TimeSpan Avg) GetLagStats(string nodeId);
    public IEnumerable<string> GetNodesExceedingThreshold(TimeSpan threshold);
}
```
```csharp
private sealed class LagEntry
{
}
    public TimeSpan CurrentLag { get; set; }
    public TimeSpan MaxLag { get; set; }
    public TimeSpan MinLag { get; set; };
    public TimeSpan AverageLag { get; set; }
    public int SampleCount { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed class AntiEntropyProtocol
{
}
    public AntiEntropyProtocol(TimeSpan? syncInterval = null);
    public void UpdateNodeVersion(string nodeId, EnhancedVectorClock version);
    public IEnumerable<string> GetNodesNeedingSync(EnhancedVectorClock localVersion);
    public IEnumerable<string> SelectGossipTargets(int count);
    public string ComputeMerkleRoot(IEnumerable<(string Key, byte[] Data)> items);
}
```
```csharp
public abstract class EnhancedReplicationStrategyBase : ReplicationStrategyBase
{
}
    protected EnhancedVectorClock VectorClock { get; };
    protected ReplicationLagTracker LagTracker { get; };
    protected AntiEntropyProtocol AntiEntropy { get; }
    protected string LocalNodeId { get; }
    protected ConflictResolutionMethod ConflictResolution { get; set; };
    protected string? PluginId { get; private set; }
    public new abstract ReplicationCharacteristics Characteristics { get; }
    protected EnhancedReplicationStrategyBase(string? nodeId = null, TimeSpan? antiEntropyInterval = null);
    public void ConfigureIntelligence(IMessageBus messageBus, string pluginId);
    protected void IncrementLocalClock();
    protected void MergeRemoteClock(EnhancedVectorClock remoteClock);
    public virtual EnhancedReplicationConflict? DetectConflictEnhanced(EnhancedVectorClock localVersion, EnhancedVectorClock remoteVersion, ReadOnlyMemory<byte> localData, ReadOnlyMemory<byte> remoteData, string remoteNodeId);
    public virtual async Task<(ReadOnlyMemory<byte> ResolvedData, EnhancedVectorClock ResolvedVersion)> ResolveConflictEnhancedAsync(EnhancedReplicationConflict conflict, CancellationToken cancellationToken = default);
    protected virtual Task<(ReadOnlyMemory<byte>, EnhancedVectorClock)> ResolveByCrdtAsync(EnhancedReplicationConflict conflict, EnhancedVectorClock mergedClock, CancellationToken ct);
    protected virtual Task<(ReadOnlyMemory<byte>, EnhancedVectorClock)> ResolveBytMergeAsync(EnhancedReplicationConflict conflict, EnhancedVectorClock mergedClock, CancellationToken ct);
    protected void RecordReplicationLag(string targetNodeId, TimeSpan lag);
    protected IEnumerable<string> GetNodesNeedingAntiEntropy();
    protected void UpdateRemoteNodeVersion(string nodeId, EnhancedVectorClock version);
    protected async Task<double?> RequestConflictPredictionAsync(string sourceNode, IEnumerable<string> targetNodes, Dictionary<string, object>? dataPattern = null, CancellationToken ct = default);
    protected async Task<ConsistencyModel?> RequestOptimalConsistencyAsync(string dataType, string accessPattern, long latencyRequirements, CancellationToken ct = default);
    protected async Task ReportLagToIntelligenceAsync(string sourceNode, string targetNode, long lagMs, CancellationToken ct = default);
    protected async Task<string?> RequestRoutingDecisionAsync(IEnumerable<string> availableReplicas, Dictionary<string, long> replicaLag, Dictionary<string, double> replicaLoad, string operationType = "read", CancellationToken ct = default);
    protected async Task ReportConflictResolutionAsync(string conflictId, ConflictResolutionMethod resolutionMethod, bool wasSuccessful, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/ReplicationTopics.cs
```csharp
public static class ReplicationTopics
{
}
    public const string Prefix = "replication.ultimate";
    public const string Replicate = $"{Prefix}.replicate";
    public const string Sync = $"{Prefix}.sync";
    public const string AntiEntropy = $"{Prefix}.anti-entropy";
    public const string Status = $"{Prefix}.status";
    public const string SelectStrategy = $"{Prefix}.select";
    public const string ListStrategies = $"{Prefix}.strategies.list";
    public const string StrategyInfo = $"{Prefix}.strategies.info";
    public const string RecommendStrategy = $"{Prefix}.strategies.recommend";
    public const string Conflict = $"{Prefix}.conflict";
    public const string ConflictResolved = $"{Prefix}.conflict.resolved";
    public const string ConflictResolve = $"{Prefix}.conflict.resolve";
    public const string Lag = $"{Prefix}.lag";
    public const string LagRequest = $"{Prefix}.lag.request";
    public const string LagAlert = $"{Prefix}.lag.alert";
    public const string Health = $"{Prefix}.health";
    public const string Metrics = $"{Prefix}.metrics";
    public const string NodeStatus = $"{Prefix}.node.status";
    public const string PredictConflict = $"{Prefix}.predict.conflict";
    public const string PredictConflictResponse = $"{Prefix}.predict.conflict.response";
    public const string OptimizeConsistency = $"{Prefix}.optimize.consistency";
    public const string OptimizeConsistencyResponse = $"{Prefix}.optimize.consistency.response";
    public const string RouteRequest = $"{Prefix}.route.request";
    public const string RouteRequestResponse = $"{Prefix}.route.request.response";
    public const string LagFeedback = $"{Prefix}.lag.feedback";
    public const string ConflictFeedback = $"{Prefix}.conflict.feedback";
    public const string OptimizePerformance = $"{Prefix}.optimize.performance";
    public const string OptimizePerformanceResponse = $"{Prefix}.optimize.performance.response";
    public const string AllEvents = $"{Prefix}.*";
    public const string AllConflicts = $"{Prefix}.conflict.*";
    public const string AllIntelligenceRequests = $"{Prefix}.predict.*";
    public const string AllStrategyTopics = $"{Prefix}.strategies.*";
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/StorageIntegrationFeature.cs
```csharp
public sealed class StorageIntegrationFeature : IDisposable
{
}
    public StorageIntegrationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus);
    public long TotalStorageReads;;
    public long TotalStorageWrites;;
    public long TotalBytesWritten;;
    public async Task<StorageWriteResult> WriteReplicaAsync(string key, ReadOnlyMemory<byte> data, string backendId, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<StorageReadResult> ReadReplicaAsync(string key, string backendId, CancellationToken ct = default);
    public async Task<ConsistencyCheckResult> VerifyConsistencyAsync(string key, IReadOnlyList<string> backendIds, CancellationToken ct = default);
    public void RegisterBackend(string backendId, string displayName, long capacityBytes);
    public StorageReplicaMapping? GetReplicaMapping(string key);
    public IReadOnlyDictionary<string, StorageBackendInfo> GetBackends();
    public void Dispose();
}
```
```csharp
public sealed class StorageBackendInfo
{
}
    public required string BackendId { get; init; }
    public required string DisplayName { get; init; }
    public long TotalCapacityBytes { get; set; }
    public long UsedCapacityBytes { get; set; }
    public long AvailableCapacityBytes;;
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed class StorageReplicaMapping
{
}
    public required string Key { get; init; }
    public required BoundedDictionary<string, ReplicaInfo> Backends { get; init; }
}
```
```csharp
public sealed class ReplicaInfo
{
}
    public required string BackendId { get; init; }
    public required string DataHash { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastUpdated { get; init; }
}
```
```csharp
public sealed class StorageWriteResult
{
}
    public required string Key { get; init; }
    public required string BackendId { get; init; }
    public required bool Success { get; init; }
    public required long BytesWritten { get; init; }
    public required string DataHash { get; init; }
    public required DateTimeOffset WrittenAt { get; init; }
}
```
```csharp
public sealed class StorageReadResult
{
}
    public required string Key { get; init; }
    public required string BackendId { get; init; }
    public required bool Success { get; init; }
    public required byte[] Data { get; init; }
    public required string DataHash { get; init; }
    public required DateTimeOffset ReadAt { get; init; }
}
```
```csharp
public sealed class ConsistencyCheckResult
{
}
    public required string Key { get; init; }
    public required bool IsConsistent { get; init; }
    public required Dictionary<string, string> BackendHashes { get; init; }
    public required int UniqueHashCount { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/ReplicationLagMonitoringFeature.cs
```csharp
public sealed class ReplicationLagMonitoringFeature : IDisposable
{
}
    public ReplicationLagMonitoringFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, long warningThresholdMs = 1000, long criticalThresholdMs = 5000, long emergencyThresholdMs = 30000, int maxHistorySamples = 1000);
    public long TotalMeasurements;;
    public long WarningAlerts;;
    public long CriticalAlerts;;
    public async Task<LagAlertLevel> RecordLagMeasurementAsync(string sourceNode, string targetNode, long lagMs, CancellationToken ct = default);
    public IReadOnlyDictionary<string, LagNodeStatus> GetAllNodeStatus();
    public LagNodeStatus? GetNodeStatus(string sourceNode, string targetNode);
    public IReadOnlyList<LagNodeStatus> GetNodesInAlert();
    public IReadOnlyList<LagSample> GetLagHistory(string sourceNode, string targetNode);
    public void Dispose();
}
```
```csharp
public sealed class LagNodeStatus
{
}
    public required string NodeKey { get; init; }
    public required string SourceNode { get; init; }
    public required string TargetNode { get; init; }
    public long CurrentLagMs { get; set; }
    public long MaxLagMs { get; set; }
    public long MinLagMs { get; set; };
    public double AverageLagMs { get; set; }
    public long SampleCount { get; set; }
    public LagAlertLevel CurrentAlertLevel { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public sealed class LagSample
{
}
    public required long LagMs { get; init; }
    public required DateTimeOffset SampledAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/SmartConflictResolutionFeature.cs
```csharp
public sealed class SmartConflictResolutionFeature : IDisposable
{
}
    public SmartConflictResolutionFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, TimeSpan? intelligenceTimeout = null);
    public long TotalConflictsProcessed;;
    public long SemanticResolutions;;
    public long StructuralResolutions;;
    public long LwwFallbackResolutions;;
    public async Task<SmartResolutionResult> ResolveConflictAsync(EnhancedReplicationConflict conflict, CancellationToken ct = default);
    public IReadOnlyDictionary<string, ConflictResolutionRecord> GetResolutionHistory();
    public void Dispose();
}
```
```csharp
public sealed class SmartResolutionResult
{
}
    public required string ConflictId { get; init; }
    public required byte[] ResolvedData { get; init; }
    public required SmartResolutionMethod Method { get; init; }
    public required double Confidence { get; init; }
    public required string WinningSource { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed class ConflictResolutionRecord
{
}
    public required string ConflictId { get; init; }
    public required SmartResolutionMethod Method { get; init; }
    public required double Confidence { get; init; }
    public required string WinningSource { get; init; }
    public required DateTimeOffset ResolvedAt { get; init; }
    public required string LocalNodeId { get; init; }
    public required string RemoteNodeId { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/IntelligenceIntegrationFeature.cs
```csharp
public sealed class IntelligenceIntegrationFeature : IDisposable
{
}
    public IntelligenceIntegrationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, TimeSpan? intelligenceTimeout = null, int maxLagDataPoints = 500);
    public long TotalPredictions;;
    public long AiPredictions;;
    public long FallbackPredictions;;
    public async Task<ConflictPrediction> PredictConflictsAsync(string sourceNode, IReadOnlyList<string> targetNodes, string activeStrategyName, double writeRatePerSecond, CancellationToken ct = default);
    public async Task<StrategyRecommendation> RecommendStrategyAsync(string dataType, string accessPattern, long maxLatencyMs, bool requireMultiMaster, CancellationToken ct = default);
    public LagPrediction PredictLag(string nodeKey, int predictMinutesAhead = 5);
    public void RecordLagDataPoint(string nodeKey, long lagMs);
    public AnomalyDetectionResult DetectLagAnomaly(string nodeKey, long currentLagMs, double stdDevThreshold = 2.0);
    public IReadOnlyDictionary<string, PredictionRecord> GetPredictionHistory();
    public void Dispose();
}
```
```csharp
public sealed class ConflictPrediction
{
}
    public required string SourceNode { get; init; }
    public required double ConflictProbability { get; init; }
    public required string Method { get; init; }
    public required double Confidence { get; init; }
    public required string[] RecommendedActions { get; init; }
    public required DateTimeOffset PredictedAt { get; init; }
}
```
```csharp
public sealed class StrategyRecommendation
{
}
    public required string RecommendedStrategy { get; init; }
    public required double Confidence { get; init; }
    public required string Method { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed class LagPrediction
{
}
    public required string NodeKey { get; init; }
    public required long PredictedLagMs { get; init; }
    public required double Confidence { get; init; }
    public required string Method { get; init; }
    public required DateTimeOffset PredictionTime { get; init; }
    public string Trend { get; init; };
}
```
```csharp
public sealed class AnomalyDetectionResult
{
}
    public required string NodeKey { get; init; }
    public required long CurrentLagMs { get; init; }
    public required bool IsAnomaly { get; init; }
    public required double Confidence { get; init; }
    public double ZScore { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public required string Reason { get; init; }
}
```
```csharp
public sealed class LagDataPoint
{
}
    public required string NodeKey { get; init; }
    public required long LagMs { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed class PredictionRecord
{
}
    public required string Key { get; init; }
    public required string SourceNode { get; init; }
    public required double Probability { get; init; }
    public required string Method { get; init; }
    public required double Confidence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/GeoWormReplicationFeature.cs
```csharp
public sealed class GeoWormReplicationFeature : IDisposable
{
}
    public GeoWormReplicationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, TimeSpan? operationTimeout = null);
    public long TotalWormReplications;;
    public long ComplianceModeWrites;;
    public long EnterpriseModeWrites;;
    public long GeofenceRejections;;
    public async Task<GeoWormReplicationResult> ReplicateToWormAsync(WormReplicationRequest request, GeoWormOptions options, CancellationToken ct = default);
    public void RegisterRegion(GeoWormRegion region);
    public IReadOnlyDictionary<string, GeoWormRegion> GetRegions();
    public IReadOnlyDictionary<string, WormReplicationRecord> GetReplicationRecords();
    public void Dispose();
}
```
```csharp
public sealed class WormReplicationRequest
{
}
    public required string DataId { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required string DataClassification { get; init; }
}
```
```csharp
public sealed class GeoWormOptions
{
}
    public required WormMode WormMode { get; init; }
    public required IReadOnlyList<string> TargetRegions { get; init; }
    public required TimeSpan DefaultRetentionPeriod { get; init; }
    public Dictionary<string, TimeSpan> RetentionPeriods { get; init; };
    public required IReadOnlyList<string> ComplianceFrameworks { get; init; }
}
```
```csharp
public sealed class GeoWormRegion
{
}
    public required string RegionId { get; init; }
    public required string Name { get; init; }
    public required string Continent { get; init; }
    public required string Country { get; init; }
    public required bool WormCapable { get; init; }
    public required string[] ComplianceFrameworks { get; init; }
}
```
```csharp
public sealed class WormRegionResult
{
}
    public required string RegionId { get; init; }
    public required bool Success { get; init; }
    public required WormReplicationPhase Phase { get; init; }
    public required string Reason { get; init; }
    public required long DurationMs { get; init; }
    public WormMode? WormMode { get; init; }
    public TimeSpan? RetentionPeriod { get; init; }
}
```
```csharp
public sealed class GeoWormReplicationResult
{
}
    public required string DataId { get; init; }
    public required bool OverallSuccess { get; init; }
    public required string[] SuccessfulRegions { get; init; }
    public required string[] FailedRegions { get; init; }
    public required Dictionary<string, WormRegionResult> RegionResults { get; init; }
    public required long TotalDurationMs { get; init; }
    public required WormMode WormMode { get; init; }
}
```
```csharp
public sealed class WormReplicationRecord
{
}
    public required string DataId { get; init; }
    public required string DataHash { get; init; }
    public required long DataSizeBytes { get; init; }
    public required WormMode WormMode { get; init; }
    public required Dictionary<string, WormRegionResult> RegionResults { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
```
```csharp
public sealed class GeofenceCheckResult
{
}
    public required string RegionId { get; init; }
    public required bool IsCompliant { get; init; }
    public string? RejectionReason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/PartialReplicationFeature.cs
```csharp
public sealed class PartialReplicationFeature : IDisposable
{
}
    public PartialReplicationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus);
    public long TotalEvaluated;;
    public long TotalIncluded;;
    public long TotalExcluded;;
    public ReplicationFilter CreateFilter(string filterId, IReadOnlyList<FilterRule> rules, FilterCombineMode combineMode = FilterCombineMode.And);
    public FilterSubscription CreateSubscription(string subscriptionId, string filterId, string targetNode);
    public bool ShouldReplicate(string targetNode, string dataKey, IReadOnlySet<string>? tags = null, long dataSizeBytes = 0, TimeSpan? dataAge = null);
    public IReadOnlyDictionary<string, ReplicationFilter> GetFilters();
    public IReadOnlyDictionary<string, FilterSubscription> GetSubscriptions();
    public bool RemoveFilter(string filterId);
    public void Dispose();
}
```
```csharp
public sealed class FilterRule
{
}
    public required FilterRuleType Type { get; init; }
    public IReadOnlyList<string> Values { get; init; };
    public string? Pattern { get; init; }
    public long SizeThreshold { get; init; }
    public TimeSpan AgeThreshold { get; init; }
    public bool Negate { get; init; }
}
```
```csharp
public sealed class ReplicationFilter
{
}
    public required string FilterId { get; init; }
    public required List<FilterRule> Rules { get; init; }
    public required FilterCombineMode CombineMode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; set; }
}
```
```csharp
public sealed class FilterSubscription
{
}
    public required string SubscriptionId { get; init; }
    public required string FilterId { get; init; }
    public required string TargetNode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/GeoDistributedShardingFeature.cs
```csharp
public sealed class GeoDistributedShardingFeature : IDisposable
{
}
    public GeoDistributedShardingFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, int defaultDataShards = 4, int defaultParityShards = 2);
    public long TotalShardOperations;;
    public long TotalShardsCreated;;
    public long TotalParityShardsCreated;;
    public long TotalShardRebuilds;;
    public async Task<GeoShardDistributionResult> ShardAcrossContinentsAsync(GeoShardRequest request, GeoShardOptions options, CancellationToken ct = default);
    public async Task<ShardRebuildResult> RebuildShardsAsync(string dataId, IReadOnlyList<string> failedShardIds, CancellationToken ct = default);
    public void RegisterRegion(GeoShardRegion region);
    public IReadOnlyDictionary<string, ShardHealthRecord> GetShardHealth();
    public IReadOnlyDictionary<string, ShardDistributionRecord> GetDistributionRecords();
    public void Dispose();
}
```
```csharp
public sealed class GeoShardRequest
{
}
    public required string DataId { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required string DataClassification { get; init; }
}
```
```csharp
public sealed class GeoShardOptions
{
}
    public required int DataShards { get; init; }
    public required int ParityShards { get; init; }
    public required IReadOnlyList<string> AllowedRegions { get; init; }
    public bool PreferLocalContinent { get; init; };
}
```
```csharp
public sealed class GeoShardRegion
{
}
    public required string RegionId { get; init; }
    public required string Name { get; init; }
    public required string Continent { get; init; }
    public required double CapacityWeight { get; init; }
    public required int LatencyMs { get; init; }
    public required string[] ComplianceFrameworks { get; init; }
}
```
```csharp
public sealed class ShardPlacementResult
{
}
    public required string ShardId { get; init; }
    public required int ShardIndex { get; init; }
    public required bool IsDataShard { get; init; }
    public required string RegionId { get; init; }
    public required bool Success { get; init; }
    public required int SizeBytes { get; init; }
    public required string DataHash { get; init; }
}
```
```csharp
public sealed class GeoShardDistributionResult
{
}
    public required string DataId { get; init; }
    public required bool OverallSuccess { get; init; }
    public required int TotalShards { get; init; }
    public required int SuccessfulShards { get; init; }
    public required int DataShards { get; init; }
    public required int ParityShards { get; init; }
    public required Dictionary<string, ShardPlacementResult> ShardPlacements { get; init; }
    public required Dictionary<string, int> RegionDistribution { get; init; }
    public required long TotalDurationMs { get; init; }
}
```
```csharp
public sealed class ShardRebuildResult
{
}
    public required string DataId { get; init; }
    public required bool Success { get; init; }
    public required string Reason { get; init; }
    public required string[] RebuiltShards { get; init; }
}
```
```csharp
public sealed class ShardDistributionRecord
{
}
    public required string DataId { get; init; }
    public required int DataShardCount { get; init; }
    public required int ParityShardCount { get; init; }
    public required Dictionary<string, ShardPlacementResult> ShardPlacements { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed class ShardHealthRecord
{
}
    public required string ShardId { get; init; }
    public required string RegionId { get; init; }
    public required bool IsHealthy { get; init; }
    public required DateTimeOffset LastChecked { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/CrossCloudReplicationFeature.cs
```csharp
public sealed class CrossCloudReplicationFeature : IDisposable
{
}
    public CrossCloudReplicationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus);
    public long TotalCrossCloudReplications;;
    public long TotalBytesReplicated;;
    public async Task<CrossCloudReplicationResult> ReplicateAcrossCloudsAsync(string replicationId, ReadOnlyMemory<byte> data, string sourceProvider, IReadOnlyList<string> targetProviders, bool verifyIntegrity = true, CancellationToken ct = default);
    public void RegisterProvider(string providerId, string displayName, double egressCostPerGb, int latencyMs);
    public IReadOnlyDictionary<string, CloudProviderStatus> GetProviderStatuses();
    public void Dispose();
}
```
```csharp
public sealed class CloudProviderStatus
{
}
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public CloudHealth Health { get; set; }
    public required double EgressCostPerGb { get; init; }
    public required int EstimatedLatencyMs { get; init; }
    public DateTimeOffset LastHealthCheck { get; set; }
}
```
```csharp
public sealed class CloudProviderResult
{
}
    public required string ProviderId { get; init; }
    public required bool Success { get; init; }
    public required string Reason { get; init; }
    public required long DurationMs { get; init; }
    public double EgressCostEstimate { get; init; }
}
```
```csharp
public sealed class CrossCloudReplicationResult
{
}
    public required string ReplicationId { get; init; }
    public required bool OverallSuccess { get; init; }
    public required Dictionary<string, CloudProviderResult> ProviderResults { get; init; }
    public required long TotalDurationMs { get; init; }
    public required double TotalEgressCost { get; init; }
}
```
```csharp
public sealed class CrossCloudReplicationRecord
{
}
    public required string ReplicationId { get; init; }
    public required string SourceProvider { get; init; }
    public required string[] TargetProviders { get; init; }
    public required long DataSizeBytes { get; init; }
    public required string DataHash { get; init; }
    public required Dictionary<string, CloudProviderResult> ProviderResults { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/GlobalTransactionCoordinationFeature.cs
```csharp
public sealed class GlobalTransactionCoordinationFeature : IDisposable
{
}
    public GlobalTransactionCoordinationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, TimeSpan? prepareTimeout = null, TimeSpan? commitTimeout = null);
    public long TotalTransactions;;
    public long CommittedTransactions;;
    public long AbortedTransactions;;
    public async Task<TransactionResult> ExecuteTwoPhaseCommitAsync(string? transactionId, IReadOnlyList<string> participantNodes, ReadOnlyMemory<byte> data, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<TransactionResult> ExecuteThreePhaseCommitAsync(string? transactionId, IReadOnlyList<string> participantNodes, ReadOnlyMemory<byte> data, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public TransactionState? GetTransactionState(string transactionId);
    public IReadOnlyDictionary<string, TransactionLog> GetTransactionLog();
    public void Dispose();
}
```
```csharp
public sealed class TransactionState
{
}
    public required string TransactionId { get; init; }
    public required TransactionProtocol Protocol { get; init; }
    public TransactionPhase Phase { get; set; }
    public required List<string> ParticipantNodes { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required BoundedDictionary<string, ParticipantVote> Votes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
public sealed class TransactionResult
{
}
    public required string TransactionId { get; init; }
    public required TransactionProtocol Protocol { get; init; }
    public required TransactionOutcome Outcome { get; init; }
    public required Dictionary<string, ParticipantVote> ParticipantOutcomes { get; init; }
    public required TimeSpan Duration { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
```
```csharp
public sealed class TransactionLog
{
}
    public required string TransactionId { get; init; }
    public required string Action { get; init; }
    public required string[] Participants { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/BandwidthAwareSchedulingFeature.cs
```csharp
public sealed class BandwidthAwareSchedulingFeature : IDisposable
{
}
    public BandwidthAwareSchedulingFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, double highUtilizationThreshold = 0.7, double lowUtilizationThreshold = 0.3, long largeTransferThresholdBytes = 10 * 1024 * 1024);
    public long TotalScheduled;;
    public long TotalDeferred;;
    public long TotalImmediate;;
    public async Task<SchedulingDecision> ScheduleTransferAsync(string transferId, string sourceNode, string targetNode, long dataSizeBytes, TransferPriority priority = TransferPriority.Normal, CancellationToken ct = default);
    public void ReportBandwidth(string sourceNode, string targetNode, double utilizationPercent, double availableBandwidthMbps);
    public int DeferredQueueDepth;;
    public IReadOnlyDictionary<string, BandwidthSample> GetBandwidthStats();
    public void Dispose();
}
```
```csharp
public sealed class BandwidthSample
{
}
    public required string LinkKey { get; init; }
    public required double Utilization { get; init; }
    public required double AvailableBandwidthMbps { get; init; }
    public required DateTimeOffset SampledAt { get; init; }
}
```
```csharp
public sealed class ScheduledTransfer
{
}
    public required string TransferId { get; init; }
    public required string SourceNode { get; init; }
    public required string TargetNode { get; init; }
    public required long DataSizeBytes { get; init; }
    public required TransferPriority Priority { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required string LinkKey { get; init; }
}
```
```csharp
public sealed class SchedulingDecision
{
}
    public required string TransferId { get; init; }
    public required SchedulingMode Mode { get; init; }
    public required DateTimeOffset EstimatedStartTime { get; init; }
    public required long EstimatedDurationMs { get; init; }
    public double ThrottleRatio { get; init; };
    public required string Reason { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/RaidIntegrationFeature.cs
```csharp
public sealed class RaidIntegrationFeature : IDisposable
{
}
    public RaidIntegrationFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus);
    public long TotalParityChecks;;
    public long ParityChecksPassed;;
    public long TotalRebuildsInitiated;;
    public async Task<ParityCheckResult> VerifyParityAsync(string dataId, ReadOnlyMemory<byte> data, string? arrayId = null, CancellationToken ct = default);
    public async Task<RebuildResult> InitiateRebuildAsync(string dataId, string arrayId, IReadOnlyList<string> failedShards, CancellationToken ct = default);
    public IReadOnlyDictionary<string, RaidParityStatus> GetParityStatuses();
    public IReadOnlyDictionary<string, RebuildOperation> GetActiveRebuilds();
    public void Dispose();
}
```
```csharp
public sealed class ParityCheckResult
{
}
    public required string DataId { get; init; }
    public required bool ParityValid { get; init; }
    public required string ArrayId { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    public required string ParityHash { get; init; }
}
```
```csharp
public sealed class RaidParityStatus
{
}
    public required string DataId { get; init; }
    public required bool IsValid { get; init; }
    public DateTimeOffset LastChecked { get; set; }
    public required string ArrayId { get; init; }
}
```
```csharp
public sealed class RebuildOperation
{
}
    public required string OperationId { get; init; }
    public required string DataId { get; init; }
    public required string ArrayId { get; init; }
    public required string[] FailedShards { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public RebuildStatus Status { get; set; }
}
```
```csharp
public sealed class RebuildResult
{
}
    public required string OperationId { get; init; }
    public required bool Success { get; init; }
    public required string DataId { get; init; }
    public required string ArrayId { get; init; }
    public required string[] RebuiltShards { get; init; }
    public required long DurationMs { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Features/PriorityBasedQueueFeature.cs
```csharp
public sealed class PriorityBasedQueueFeature : IDisposable
{
}
    public PriorityBasedQueueFeature(ReplicationStrategyRegistry registry, IMessageBus messageBus, TimeSpan? starvationThreshold = null, int maxQueueDepth = 10000);
    public long TotalEnqueued;;
    public long TotalDequeued;;
    public long TotalPromoted;;
    public bool Enqueue(QueuedReplication item);
    public QueuedReplication? Dequeue();
    public IReadOnlyList<QueuedReplication> DequeueBatch(int count);
    public IReadOnlyDictionary<ReplicationPriority, int> GetQueueDepths();
    public int TotalQueueDepth;;
    public TimeSpan EstimateWaitTime(ReplicationPriority priority);
    public void Dispose();
}
```
```csharp
public sealed class QueuedReplication
{
}
    public required string ItemId { get; init; }
    public ReplicationPriority Priority { get; set; }
    public required string SourceNode { get; init; }
    public required string TargetNode { get; init; }
    public required long DataSizeBytes { get; init; }
    public string? StrategyName { get; init; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? DequeuedAt { get; set; }
    public int PromotionCount { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Scaling/ReplicationScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: Replication scaling manager with per-namespace strategy, WAL queue, streaming conflict")]
public sealed class ReplicationScalingManager : IScalableSubsystem, IDisposable
{
}
    public const string StrategyRoutingTopic = "dw.replication.strategy-routing";
    public const string ClusterMembershipTopic = "dw.cluster.membership.changed";
    public const int DefaultComparisonChunkSize = 64 * 1024;
    public static readonly TimeSpan DefaultWalRetention = TimeSpan.FromHours(48);
    public static readonly TimeSpan DefaultHealthCheckInterval = TimeSpan.FromSeconds(30);
    public const int MaxConsecutiveFailures = 3;
    public ReplicationScalingManager(IPersistentBackingStore? walStore = null, IFederatedMessageBus? messageBus = null, ScalingLimits? initialLimits = null, TimeSpan? walRetention = null, TimeSpan? healthCheckInterval = null);
    public string ResolveStrategy(string namespaceName);
    public void RegisterRoute(string globPattern, string strategyName);
    public bool RemoveRoute(string strategyName);
    public void SetDefaultStrategy(string strategyName);
    public int RouteCount;;
    public async Task<long> AppendToWalAsync(string key, string namespaceName, string operation, string payloadReference, CancellationToken ct = default);
    public async Task<IReadOnlyList<WalEntry>> ReadFromWalAsync(string replicaId, int maxEntries = 100, CancellationToken ct = default);
    public async Task AcknowledgeAsync(string replicaId, long sequence, CancellationToken ct = default);
    public async Task RestoreOffsetsAsync(CancellationToken ct = default);
    public async Task<int> PurgeExpiredWalEntriesAsync(CancellationToken ct = default);
    public long WalSize;;
    public bool StreamingCompare(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote);
    public bool DetectConflict(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote);
    public void RecordConflictResolved();
    public void RegisterReplica(string replicaId, string endpoint);
    public bool RemoveReplica(string replicaId);
    public void RecordHealthCheck(string replicaId, bool success);
    public IReadOnlyList<ReplicaInfo> GetActiveReplicas();
    public IReadOnlyList<ReplicaInfo> GetAllReplicas();
    public long GetReplicationLag(string replicaId);
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
        var walSize = Interlocked.Read(ref _walSequence);
        long minAcked = _replicaOffsets.Values.DefaultIfEmpty(0L).Min();
        long pendingEntries = walSize - minAcked;
        if (_currentLimits.MaxQueueDepth == 0)
            return BackpressureState.Normal;
        double utilization = (double)pendingEntries / _currentLimits.MaxQueueDepth;
        return utilization switch
        {
            >= 0.80 => BackpressureState.Critical,
            >= 0.50 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }
}
    public void Dispose();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: WAL entry for replication queue")]
public sealed class WalEntry
{
}
    public long Sequence { get; init; }
    public required string Key { get; init; }
    public required string Namespace { get; init; }
    public required string Operation { get; init; }
    public required string PayloadReference { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: Replica information for replication scaling")]
public sealed class ReplicaInfo
{
}
    public required string ReplicaId { get; init; }
    public required string Endpoint { get; init; }
    public bool IsActive { get; set; }
    public DateTimeOffset LastHealthCheck { get; set; }
    public int ConsecutiveFailures { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/DR/DisasterRecoveryStrategies.cs
```csharp
public sealed class DRSite
{
}
    public required string SiteId { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public DRSiteRole Role { get; set; };
    public DRSiteHealth Health { get; set; };
    public int RpoSeconds { get; set; };
    public int RtoSeconds { get; set; };
    public int LatencyMs { get; set; };
    public DateTimeOffset? LastSyncTime { get; set; }
}
```
```csharp
public sealed class AsyncDRStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetRPO(int seconds);
    public void AddSite(DRSite site);
    public long GetCurrentCheckpoint();;
    public long GetCheckpointLag(string siteId);
    public bool IsWithinRPO(string siteId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SyncDRStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetQuorum(int quorum);
    public void AddSite(DRSite site);
    public DRSite? GetPrimary();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ZeroRPOStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed record LogEntry(long Lsn, string Key, byte[] Data, DateTimeOffset Timestamp);;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(DRSite site);
    public long GetCurrentLsn();;
    public long GetSiteLsn(string siteId);
    public IEnumerable<LogEntry> GetPendingLogEntries(string siteId, int maxEntries = 100);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ActivePassiveStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed record FailoverEvent(string FromSite, string ToSite, DateTimeOffset Timestamp, FailoverStatus Status, string? Reason);;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetAutoFailover(bool enabled);
    public void SetHealthCheckInterval(int intervalMs);
    public void AddSite(DRSite site);
    public DRSite? GetActiveSite();
    public IEnumerable<DRSite> GetPassiveSites();
    public async Task<bool> FailoverAsync(string targetSiteId, string? reason = null, CancellationToken ct = default);
    public async Task CheckHealthAndFailoverAsync(CancellationToken ct = default);
    public IReadOnlyList<FailoverEvent> GetFailoverHistory();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class FailoverDRStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(DRSite site);
    public FailoverStatus GetFailoverStatus();;
    public DRSite? GetPrimary();
    public async Task<bool> InitiateFailoverAsync(string targetSiteId, CancellationToken ct = default);
    public async Task<bool> RollbackAsync(CancellationToken ct = default);
    public async Task<bool> PlannedSwitchoverAsync(string targetSiteId, CancellationToken ct = default);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Topology/TopologyStrategies.cs
```csharp
public sealed class TopologyNode
{
}
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public TopologyRole Role { get; set; };
    public string? ParentNodeId { get; set; }
    public List<string> ChildNodeIds { get; };
    public List<string> NeighborNodeIds { get; };
    public TopologyNodeHealth Health { get; set; };
    public int LatencyMs { get; set; };
    public double CapacityWeight { get; set; };
}
```
```csharp
public sealed class StarTopologyStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetHub(TopologyNode node);
    public void AddSpoke(TopologyNode node);
    public TopologyNode? GetHub();
    public IEnumerable<TopologyNode> GetSpokes();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MeshTopologyStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetMinConnections(int minConnections);
    public void AddNode(TopologyNode node);
    public IEnumerable<TopologyNode> GetNeighbors(string nodeId);
    public bool VerifyMeshConnectivity();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ChainTopologyStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddToChain(TopologyNode node);
    public TopologyNode? GetChainHead();
    public TopologyNode? GetChainTail();
    public int GetChainPosition(string nodeId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class TreeTopologyStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetRoot(TopologyNode node);
    public void AddChild(string parentNodeId, TopologyNode child);
    public void SetMaxFanOut(int maxFanOut);
    public int GetDepth(string nodeId);
    public IEnumerable<string> GetDescendants(string nodeId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class RingTopologyStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddToRing(TopologyNode node);
    public void SetBidirectional(bool enabled);
    public TopologyNode? GetNextNode(string nodeId);
    public TopologyNode? GetPreviousNode(string nodeId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class HierarchicalTopologyStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetMaxLevels(int maxLevels);
    public void AddNodeAtLevel(TopologyNode node, int level, string? parentNodeId = null);
    public IEnumerable<TopologyNode> GetNodesAtLevel(int level);
    public int GetNodeLevel(string nodeId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Synchronous/SynchronousReplicationStrategy.cs
```csharp
public sealed class SynchronousReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ConsistencyModel ConsistencyModel;;
    public override ReplicationCapabilities Capabilities { get; };
    public SynchronousReplicationStrategy();
    public SynchronousReplicationStrategy(int writeQuorum, TimeSpan? syncTimeout = null);
    public override ReplicationCharacteristics Characteristics { get; };
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override async Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
    protected override string GetStrategyDescription();;
    protected override string[] GetKnowledgeTags();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Core/CoreReplicationStrategies.cs
```csharp
internal static class DictionaryExtensions
{
}
    public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue>? dict, TKey key, TValue? defaultValue = default)
    where TKey : notnull;
}
```
```csharp
public sealed class GCounterCrdt
{
}
    public long Value;;
    public void Increment(string nodeId, long amount = 1);
    public void Merge(GCounterCrdt other);
    public string ToJson();;
    public static GCounterCrdt FromJson(string json);
}
```
```csharp
public sealed class PNCounterCrdt
{
}
    public long Value;;
    public void Increment(string nodeId, long amount = 1);;
    public void Decrement(string nodeId, long amount = 1);;
    public void Merge(PNCounterCrdt other);
    public string ToJson();;
    public static PNCounterCrdt FromJson(string json);
}
```
```csharp
public sealed class ORSetCrdt<T>
    where T : notnull
{
}
    public IEnumerable<T> Elements;;
    public void Add(T element, string tag);
    public void Remove(T element);
    public bool Contains(T element);;
    public void Merge(ORSetCrdt<T> other);
}
```
```csharp
public sealed class LWWRegisterCrdt<T>
{
}
    public T? Value;;
    public DateTimeOffset Timestamp;;
    public void Set(T value, DateTimeOffset? timestamp = null);
    public void Merge(LWWRegisterCrdt<T> other);
}
```
```csharp
public sealed class CrdtReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void CreateCrdt<T>(string key, T initialValue)
    where T : class;
    public T? GetCrdt<T>(string key)
    where T : class;
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    protected override Task<(ReadOnlyMemory<byte>, EnhancedVectorClock)> ResolveByCrdtAsync(EnhancedReplicationConflict conflict, EnhancedVectorClock mergedClock, CancellationToken ct);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override async Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MultiMasterStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetConflictResolution(ConflictResolutionMethod method);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class RealTimeSyncStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public async Task StartStreamAsync(string targetNodeId, CancellationToken cancellationToken = default);
    public void StopStream(string targetNodeId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DeltaSyncStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public byte[] ComputeDelta(byte[] oldData, byte[] newData);
    public byte[] ApplyDelta(byte[] oldData, byte[] delta);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public IReadOnlyList<(string VersionId, DateTimeOffset Timestamp)> GetVersionChain(string dataId);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class DeltaVersion
{
}
    public required string VersionId { get; init; }
    public required byte[] Delta { get; init; }
    public required EnhancedVectorClock Clock { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? ParentVersionId { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/AirGap/AirGapReplicationStrategies.cs
```csharp
public sealed class AirGapSite
{
}
    public required string SiteId { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public int PendingSyncCount { get; set; }
    public string? DataChecksum { get; set; }
}
```
```csharp
public sealed class SyncBatch
{
}
    public required string BatchId { get; init; }
    public required string SourceSiteId { get; init; }
    public required string TargetSiteId { get; init; }
    public List<SyncEntry> Entries { get; };
    public DateTimeOffset CreatedAt { get; init; };
    public bool IsApplied { get; set; }
    public string? Checksum { get; set; }
    public int SchemaVersion { get; init; };
}
```
```csharp
public sealed class SyncEntry
{
}
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public long Version { get; init; }
    public SyncOperation Operation { get; init; };
    public string? Provenance { get; init; }
}
```
```csharp
public sealed class BidirectionalMergeStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(AirGapSite site);
    public SyncBatch CreateExportBatch(string sourceSiteId, string targetSiteId, int maxEntries = 1000);
    public async Task<(int Merged, int Skipped, int Conflicts)> ImportBatchAsync(SyncBatch batch, CancellationToken ct = default);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ConflictAvoidanceStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(AirGapSite site, IEnumerable<string>? keyPrefixes = null);
    public void AssignKeyRange(string siteId, string keyPrefix);
    public string? GetKeyOwner(string key);
    public bool CanWrite(string siteId, string key);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SchemaEvolutionStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class SchemaDefinition;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void RegisterSchema(SchemaDefinition schema);
    public void RegisterMigration(int fromVersion, int toVersion, Func<byte[], byte[]> migrator);
    public int GetCurrentSchemaVersion();;
    public byte[] MigrateData(byte[] data, int fromVersion, int toVersion);
    public bool IsCompatible(int version1, int version2);
    public void AddSite(AirGapSite site);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SchemaDefinition
{
}
    public required int Version { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, string> Fields { get; init; };
    public DateTimeOffset CreatedAt { get; init; };
}
```
```csharp
public sealed class ZeroDataLossStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed record AuditEntry(long Sequence, string Key, string Operation, string DataHash, DateTimeOffset Timestamp, string SourceSite);;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(AirGapSite site);
    public string ComputeHash(byte[] data);
    public bool VerifyIntegrity(string key, byte[] data);
    public IEnumerable<AuditEntry> GetAuditTrail(string? key = null);
    public (int Total, int Valid, int Invalid) GetIntegrityReport();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ResumableMergeStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(AirGapSite site);
    public (long Checkpoint, int Processed, int Total, string Status) GetSyncState(string siteId);
    public bool PauseSync(string siteId);
    public async Task<bool> ResumeSyncAsync(string siteId, IEnumerable<SyncEntry> entries, CancellationToken ct = default);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class SyncState
{
}
    public string? BatchId { get; set; }
    public long LastCheckpoint { get; set; }
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public SyncStatus Status { get; set; };
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
}
```
```csharp
public sealed class IncrementalSyncStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(AirGapSite site);
    public IEnumerable<(string Key, byte[] Data, long Version)> GetChangesSince(long sinceVersion);
    public byte[] ComputeDelta(byte[] oldData, byte[] newData);
    public byte[] ApplyDelta(byte[] oldData, byte[] delta);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ProvenanceTrackingStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddSite(AirGapSite site);
    public IEnumerable<(string Site, string Op, DateTimeOffset Time)> GetLineage(string key);
    public string? GetOriginSite(string key);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class DataWithProvenance
{
}
    public required byte[] Data { get; init; }
    public required string OriginSite { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<ProvenanceEntry> Lineage { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Conflict/ConflictResolutionStrategies.cs
```csharp
public sealed class ResolvedConflict
{
}
    public required EnhancedReplicationConflict OriginalConflict { get; init; }
    public required ConflictResolutionMethod Method { get; init; }
    public required byte[] ResolvedData { get; init; }
    public required string WinningSource { get; init; }
    public DateTimeOffset ResolvedAt { get; init; };
    public string? Reason { get; init; }
}
```
```csharp
public interface ICrdtValue
{
}
    void Merge(ICrdtValue other);;
    byte[] ToBytes();;
}
```
```csharp
public sealed class LastWriteWinsStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetNodePriority(string nodeId, int priority);
    public IReadOnlyList<ResolvedConflict> GetConflictLog();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class VectorClockStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public IEnumerable<EnhancedReplicationConflict> GetPendingConflicts(string key);
    public void ResolveManually(string key, byte[] resolvedData);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MergeConflictStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void RegisterMerger(string dataType, Func<byte[], byte[], byte[]> merger);
    public byte[] MergeJson(byte[] local, byte[] remote);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CustomConflictStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetGlobalResolver(Func<byte[], byte[], (byte[] Winner, string Reason)> resolver);
    public void SetTypeResolver(string dataType, Func<byte[], byte[], (byte[] Winner, string Reason)> resolver);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CrdtConflictStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class VersionConflictStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public long GetVersion(string key);
    public bool TryConditionalWrite(string key, byte[] data, long expectedVersion, string nodeId, out long newVersion);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ThreeWayMergeStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public byte[] ThreeWayMerge(byte[] ancestor, byte[] local, byte[] remote);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class VersionedData
{
}
    public required byte[] Data { get; init; }
    public required EnhancedVectorClock Clock { get; init; }
    public string? AncestorId { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Specialized/SpecializedReplicationStrategies.cs
```csharp
public sealed class SelectiveReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddFilter(string filterName, Func<byte[], IDictionary<string, string>?, bool> filter);
    public bool RemoveFilter(string filterName);
    public void SetDefaultAllow(bool allow);
    public bool ShouldReplicate(byte[] data, IDictionary<string, string>? metadata);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class FilteredReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetTargetFilter(string targetId, Func<byte[], IDictionary<string, string>?, bool> filter);
    public bool RemoveTargetFilter(string targetId);
    public bool ShouldReplicateTo(string targetId, byte[] data, IDictionary<string, string>? metadata);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CompressionReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public enum CompressionAlgorithm;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetAlgorithm(CompressionAlgorithm algorithm);
    public void SetCompressionLevel(CompressionLevel level);
    public void SetMinSizeForCompression(int minSize);
    public byte[] Compress(byte[] data);
    public byte[] Decompress(byte[] compressedData);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class EncryptionReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public enum EncryptionAlgorithm;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetMasterKey(byte[] key);
    public byte[] GenerateMasterKey();
    public void SetAlgorithm(EncryptionAlgorithm algorithm);
    public void SetNodeKey(string nodeId, byte[] key);
    public (byte[] EncryptedData, byte[] IV) Encrypt(byte[] data, string? targetNodeId = null);
    public byte[] Decrypt(byte[] encryptedData, byte[] iv, string? sourceNodeId = null);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ThrottleReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class ThrottleConfig;
    public override ReplicationCharacteristics Characteristics { get; };
    public ThrottleReplicationStrategy();
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetGlobalBandwidth(int bytesPerSecond);
    public void SetGlobalConcurrency(int maxConcurrent);
    public void SetTargetThrottle(string targetId, ThrottleConfig config);
    public int CalculateTransferTime(int dataSize, string? targetId = null);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ThrottleConfig
{
}
    public int BytesPerSecond { get; init; };
    public int MaxConcurrentOps { get; init; };
    public TimeSpan MinInterval { get; init; };
}
```
```csharp
public sealed class PriorityReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public PriorityReplicationStrategy();
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetPriorityLevels(int levels);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
    public new void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/GeoReplication/PrimarySecondaryReplicationStrategy.cs
```csharp
public sealed class PrimarySecondaryReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ConsistencyModel ConsistencyModel;;
    public override ReplicationCapabilities Capabilities { get; };
    public PrimarySecondaryReplicationStrategy() : this(autoFailover: true);
    public PrimarySecondaryReplicationStrategy(bool autoFailover, TimeSpan? failoverTimeout = null);
    public override ReplicationCharacteristics Characteristics { get; };
    public void SetPrimary(string nodeId);
    public void AddSecondary(string nodeId);
    public void RemoveSecondary(string nodeId);
    public string? GetPrimary();;
    public IReadOnlyList<string> GetSecondaries();;
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override async Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
    public async Task<bool> PerformFailoverAsync(CancellationToken cancellationToken = default);
    protected override string GetStrategyDescription();;
    protected override string[] GetKnowledgeTags();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/ActiveActive/ActiveActiveStrategies.cs
```csharp
public sealed class ActiveNode
{
}
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public string? Region { get; init; }
    public NodeHealthStatus Health { get; set; };
    public int WriteCapacity { get; set; };
    public int ReadCapacity { get; set; };
    public double LoadPercent { get; set; }
    public int LatencyMs { get; set; };
    public int Priority { get; init; };
}
```
```csharp
public sealed class HotHotStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddNode(ActiveNode node);
    public bool RemoveNode(string nodeId);
    public void SetConflictResolution(ConflictResolutionMethod method);
    public string? RouteWrite();
    public string? RouteRead();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override async Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class NWayActiveStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetWriteQuorum(int quorum);
    public void SetReadQuorum(int quorum);
    public (int Write, int Read) GetQuorumConfig();;
    public void AddNode(ActiveNode node);
    public IReadOnlyCollection<ActiveNode> GetActiveNodes();
    public bool HasWriteQuorum();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public async Task<byte[]?> QuorumReadAsync(string dataId, CancellationToken ct = default);
    public async Task RunAntiEntropyAsync(CancellationToken ct = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MultiRegionWriteCoordinator
{
}
    public enum TransactionPhase;
    public sealed class TransactionState;
    public sealed class CompensatingAction;
    public TransactionState BeginTransaction(string transactionId, string[] regions, byte[] data);
    public bool Prepare(string transactionId);
    public bool Commit(string transactionId);
    public bool Abort(string transactionId);
    public TransactionState? GetTransaction(string transactionId);
    public IReadOnlyList<TransactionState> GetActiveTransactions();
}
```
```csharp
public sealed class TransactionState
{
}
    public required string TransactionId { get; init; }
    public TransactionPhase Phase { get; set; };
    public string[] ParticipantRegions { get; init; };
    public BoundedDictionary<string, bool> PrepareVotes { get; };
    public BoundedDictionary<string, bool> CommitAcks { get; };
    public DateTimeOffset StartedAt { get; init; };
    public DateTimeOffset? PreparedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public byte[]? Data { get; init; }
}
```
```csharp
public sealed class CompensatingAction
{
}
    public required string RegionId { get; init; }
    public required string ActionType { get; init; }
    public byte[]? RollbackData { get; init; }
    public DateTimeOffset RecordedAt { get; init; };
}
```
```csharp
public sealed class GeoDistributionManager
{
}
    public sealed class GeoRegion;
    public sealed class RegionHealthMetrics;
    public void SetRpoTarget(TimeSpan rpo);;
    public void SetRtoTarget(TimeSpan rto);;
    public void AddRegion(GeoRegion region);;
    public IReadOnlyList<GeoRegion> GetHealthyRegions();;
    public void UpdateHealth(string regionId, double latencyMs, bool success, double replicationLagMs);
    public bool TriggerFailover(string failedRegionId);
    public TimeSpan GetCurrentRpo();
    public (bool RpoMet, bool RtoMet) CheckTargets();
}
```
```csharp
public sealed class GeoRegion
{
}
    public required string RegionId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsPrimary { get; set; }
    public NodeHealthStatus Status { get; set; };
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int MaxWriteOpsPerSecond { get; init; };
    public int CurrentWriteOpsPerSecond { get; set; }
}
```
```csharp
public sealed class RegionHealthMetrics
{
}
    public double AvailabilityPercent { get; set; };
    public double AverageLatencyMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset LastHealthCheck { get; set; };
    public double ReplicationLagMs { get; set; }
}
```
```csharp
public sealed class GlobalActiveStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class GlobalRegion;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddRegion(GlobalRegion region);
    public void SetConflictFreeRouting(bool enabled);
    public string? RouteWriteConflictFree(string dataId);
    public GlobalRegion? GetNearestRegion(double clientLat, double clientLon);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public async Task SyncRegionsAsync(CancellationToken ct = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class GlobalRegion
{
}
    public required string RegionId { get; init; }
    public required string Name { get; init; }
    public required string Continent { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public List<ActiveNode> Nodes { get; };
    public NodeHealthStatus Health { get; set; };
    public int InterRegionLatencyMs { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Asynchronous/AsynchronousReplicationStrategy.cs
```csharp
public sealed class AsynchronousReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ConsistencyModel ConsistencyModel;;
    public override ReplicationCapabilities Capabilities { get; };
    public AsynchronousReplicationStrategy();
    public AsynchronousReplicationStrategy(int maxQueueSize, TimeSpan replicationInterval);
    public override ReplicationCharacteristics Characteristics { get; };
    public override Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override async Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
    public int GetQueueDepth();;
    public new void Dispose();
    protected override string GetStrategyDescription();;
    protected override string[] GetKnowledgeTags();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/AI/AiReplicationStrategies.cs
```csharp
public sealed class PredictionResult
{
}
    public required object Prediction { get; init; }
    public double Confidence { get; init; }
    public List<(object Value, double Confidence)>? Alternatives { get; init; }
    public string? Explanation { get; init; }
    public DateTimeOffset Timestamp { get; init; };
}
```
```csharp
public sealed class AccessPattern
{
}
    public required string Key { get; init; }
    public long ReadCount { get; set; }
    public long WriteCount { get; set; }
    public DateTimeOffset LastAccess { get; set; }
    public TimeSpan AverageInterval { get; set; }
    public int[] PeakHours { get; set; };
    public DateTimeOffset? PredictedNextAccess { get; set; }
}
```
```csharp
public sealed class PredictiveReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void Configure(double predictionThreshold, int historyWindowSize);
    public void RecordAccess(string key, string nodeId, bool isWrite);
    public PredictionResult PredictTargetNodes(string key);
    public PredictionResult PredictNextAccess(string key);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class SemanticReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void DefineRelationship(string key1, string key2);
    public void CategorizeData(string key, string category);
    public void SetCategoryAffinity(string category, IEnumerable<string> preferredNodes);
    public IEnumerable<string> GetRelatedKeys(string key);
    public IEnumerable<string> GetCategoryNodes(string category);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class AdaptiveReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class WorkloadMetrics;
    public sealed class AdaptiveConfig;
    public sealed class ReplicationConfig;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public AdaptiveConfig GetConfig();;
    public void RecordMetrics(string key, bool isWrite, TimeSpan latency, bool hadConflict);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class WorkloadMetrics
{
}
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    public double ReadWriteRatio;;
    public TimeSpan AverageLatency { get; set; }
    public double ConflictRate { get; set; }
    public int ActiveNodes { get; set; }
}
```
```csharp
public sealed class AdaptiveConfig
{
}
    public int ReplicaCount { get; set; };
    public ConsistencyModel ConsistencyLevel { get; set; };
    public int BatchSize { get; set; };
    public TimeSpan SyncInterval { get; set; };
    public bool EnableCompression { get; set; };
}
```
```csharp
public sealed class ReplicationConfig
{
}
    public int ReplicaCount { get; init; }
    public ConsistencyModel Consistency { get; init; }
}
```
```csharp
public sealed class IntelligentReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class NodeHealthHistory;
    public sealed class AnomalyEvent;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void RecordNodeHealth(string nodeId, double latencyMs, double errorRate);
    public void RecordAnomaly(string nodeId, string type, double severity, string? description = null);
    public IEnumerable<(string NodeId, double Probability, DateTimeOffset? PredictedTime)> GetFailurePredictions();
    public IEnumerable<string> GetHealthyNodes();
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class NodeHealthHistory
{
}
    public List<double> LatencyHistory { get; };
    public List<double> ErrorRateHistory { get; };
    public double AverageLatency { get; set; }
    public double LatencyStdDev { get; set; }
    public double PredictedFailureProbability { get; set; }
    public DateTimeOffset? PredictedFailureTime { get; set; }
}
```
```csharp
public sealed class AnomalyEvent
{
}
    public required string NodeId { get; init; }
    public required string Type { get; init; }
    public double Severity { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed class AutoTuneReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class TuningParameters;
    public sealed class TuningState;
    public sealed class TuningEpisode;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public TuningParameters GetParameters();;
    public TuningParameters Explore();
    public void UpdateFromReward(TuningParameters action, double reward);
    public double CalculateReward(double latencyMs, double throughput, double errorRate, double slaLatencyMs);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class TuningParameters
{
}
    public int BatchSize { get; set; };
    public int ParallelDegree { get; set; };
    public int RetryCount { get; set; };
    public TimeSpan Timeout { get; set; };
    public bool UseCompression { get; set; };
    public bool UseDeltaSync { get; set; };
    public double ExplorationRate { get; set; };
}
```
```csharp
public sealed class TuningState
{
}
    public TuningParameters Parameters { get; init; };
    public double RewardScore { get; set; }
}
```
```csharp
public sealed class TuningEpisode
{
}
    public required TuningParameters Action { get; init; }
    public double Reward { get; set; }
    public double Latency { get; set; }
    public double Throughput { get; set; }
    public double ErrorRate { get; set; }
}
```
```csharp
public sealed class PriorityBasedReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public enum DataPriority;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetPriority(string keyOrPattern, DataPriority priority);
    public DataPriority ResolvePriority(string key);
    public static TimeSpan GetTargetLag(DataPriority priority);;
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);;
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);;
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);;
}
```
```csharp
public sealed class CostOptimizedReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class RegionCost;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetBudget(double monthlyBudgetUsd);
    public void SetRegionCost(string regionId, RegionCost cost);
    public double GetRemainingBudget();
    public double EstimateCost(long dataSizeBytes, IEnumerable<string> targetRegions);
    public IReadOnlyList<string> SelectCostEffectiveTargets(IEnumerable<string> candidates, long dataSizeBytes, int minReplicas);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);;
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);;
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);;
}
```
```csharp
public sealed class RegionCost
{
}
    public double StorageCostPerGb { get; init; };
    public double EgressCostPerGb { get; init; };
    public double InterRegionCostPerGb { get; init; };
    public string StorageTier { get; init; };
}
```
```csharp
public sealed class ComplianceAwareReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class DataClassification;
    public sealed class RegionCompliance;
    public sealed class ComplianceViolation;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void RegisterClassification(string keyPattern, DataClassification classification);
    public void RegisterRegion(string regionId, RegionCompliance compliance);
    public IReadOnlyList<ComplianceViolation> GetViolations();
    public bool IsRegionCompliant(string regionId, DataClassification classification);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);;
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);;
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);;
}
```
```csharp
public sealed class DataClassification
{
}
    public required string Label { get; init; }
    public string[] Regulations { get; init; };
    public string[] AllowedRegions { get; init; };
    public string[] DeniedRegions { get; init; };
    public bool RequiresEncryptionAtRest { get; init; };
    public bool RequiresEncryptionInTransit { get; init; };
    public TimeSpan? MaxRetention { get; init; }
}
```
```csharp
public sealed class RegionCompliance
{
}
    public string[] SupportedRegulations { get; init; };
    public bool SupportsEncryptionAtRest { get; init; };
    public bool HasDataSovereignty { get; init; }
    public string CountryCode { get; init; };
}
```
```csharp
public sealed class ComplianceViolation
{
}
    public required string DataKey { get; init; }
    public required string Classification { get; init; }
    public required string Violation { get; init; }
    public required string AttemptedRegion { get; init; }
    public DateTimeOffset DetectedAt { get; init; };
}
```
```csharp
public sealed class LatencyOptimizedReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class LatencyMeasurement;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetSlaTarget(double targetMs);;
    public void RecordLatency(string fromNode, string toNode, double rttMs);
    public double GetEstimatedLatency(string fromNode, string toNode);
    public IReadOnlyList<(string NodeId, double EstimatedMs)> RankTargetsByLatency(string sourceNode, IEnumerable<string> targets);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);;
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);;
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);;
}
```
```csharp
public sealed class LatencyMeasurement
{
}
    public double AverageRttMs { get; set; }
    public double P99RttMs { get; set; }
    public int SampleCount { get; set; }
    public DateTimeOffset LastMeasured { get; set; }
    public int HopCount { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Cloud/CloudReplicationStrategies.cs
```csharp
public sealed class CloudProvider
{
}
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public required CloudProviderType Type { get; init; }
    public Dictionary<string, string> RegionalEndpoints { get; init; };
    public CloudProviderHealth Health { get; set; };
    public int WritePriority { get; init; };
    public int ReadPriority { get; init; };
    public bool IsReadOnly { get; init; }
    public int EstimatedLatencyMs { get; set; };
    public double EgressCostMultiplier { get; init; };
}
```
```csharp
public sealed class AwsReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddRegion(string regionCode, string endpoint, int priority = 100);
    public void SetTransferAcceleration(bool enabled);
    public void SetIntelligentTiering(bool enabled);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class AzureReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetConsistencyLevel(ConsistencyModel level);
    public void AddRegion(string regionName, bool isWriteRegion = false);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public async Task<byte[]?> ReadWithSessionTokenAsync(string documentId, string sessionToken, CancellationToken ct = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class GcpReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetExternalConsistency(bool enabled);
    public void AddRegion(string regionName, bool isLeader = false);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public async Task<byte[]?> StaleReadAsync(string key, DateTimeOffset readTimestamp, CancellationToken ct = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class HybridCloudStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddProvider(CloudProvider provider);
    public void SetDataSovereigntyRules(string dataClassification, IEnumerable<string> allowedProviders);
    public void SetWanOptimization(bool enabled, bool enableCompression = true);
    public IEnumerable<CloudProvider> GetAllowedProviders(string dataClassification);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class KubernetesReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class KubernetesNode;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddPod(KubernetesNode node);
    public void SetServiceMesh(bool enabled);
    public string? GetPrimaryPod();;
    public bool IsPodReady(string podName);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class KubernetesNode
{
}
    public required string PodName { get; init; }
    public required string Namespace { get; init; }
    public required string StatefulSetName { get; init; }
    public int Ordinal { get; init; }
    public bool IsReady { get; set; };
    public string? PersistentVolumeClaimName { get; init; }
}
```
```csharp
public sealed class EdgeReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class EdgeNode;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddEdgeNode(EdgeNode node);
    public void UpdateNodeStatus(string nodeId, bool isOnline);
    public int GetPendingSyncCount(string nodeId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public async Task SyncPendingAsync(string nodeId, CancellationToken ct = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class EdgeNode
{
}
    public required string NodeId { get; init; }
    public required string Location { get; init; }
    public bool IsOnline { get; set; };
    public int BandwidthKbps { get; set; };
    public int LatencyMs { get; set; };
    public DateTimeOffset LastSeen { get; set; };
    public long StorageCapacityBytes { get; set; };
    public long StorageUsedBytes { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Federation/FederationStrategies.cs
```csharp
public sealed class FederatedDataSource
{
}
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public required string SourceType { get; init; }
    public required string Endpoint { get; init; }
    public Dictionary<string, string> SchemaMappings { get; init; };
    public bool SupportsTransactions { get; init; }
    public int ReadPriority { get; init; };
    public int WritePriority { get; init; };
    public bool IsReadOnly { get; init; }
    public DataSourceHealth Health { get; set; };
}
```
```csharp
public sealed class UnifiedSchema
{
}
    public required string Name { get; init; }
    public Dictionary<string, UnifiedFieldType> Fields { get; init; };
    public Dictionary<string, SourceMapping> SourceMappings { get; init; };
}
```
```csharp
public sealed class SourceMapping
{
}
    public required string SourceField { get; init; }
    public string? Transform { get; init; }
}
```
```csharp
public sealed class FederationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddDataSource(FederatedDataSource source);
    public bool RemoveDataSource(string sourceId);
    public IReadOnlyCollection<FederatedDataSource> GetDataSources();;
    public void RegisterSchema(UnifiedSchema schema);
    public UnifiedSchema? GetSchema(string name);
    public string BeginTransaction(IEnumerable<string> participantSourceIds);
    public void AddTransactionWrite(string transactionId, string sourceId, byte[] data);
    public async Task<bool> CommitTransactionAsync(string transactionId, CancellationToken ct = default);
    public async Task AbortTransactionAsync(string transactionId, CancellationToken ct = default);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class DistributedTransaction
{
}
    public required string TransactionId { get; init; }
    public HashSet<string> ParticipantSources { get; };
    public TransactionState State { get; set; };
    public DateTimeOffset StartedAt { get; init; }
    public List<(string SourceId, byte[] Data)> Writes { get; };
}
```
```csharp
public sealed class FederatedQueryStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetReadPreference(ReadPreference preference);
    public ReadPreference GetReadPreference();;
    public void AddSource(FederatedDataSource source);
    public void MapShard(string shardKey, IEnumerable<string> sourceIds);
    public string? RouteQuery(string? shardKey = null);
    public async Task<IReadOnlyList<(string SourceId, byte[] Result)>> ScatterGatherAsync(IEnumerable<string> sourceIds, byte[] queryData, CancellationToken ct = default);
    public byte[] AggregateResults(IEnumerable<(string SourceId, byte[] Result)> results, string aggregationType = "concat");
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/Geo/GeoReplicationStrategies.cs
```csharp
public sealed class GeoRegion
{
}
    public required string RegionId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int Priority { get; init; };
    public bool IsWitness { get; init; }
    public int EstimatedLatencyMs { get; set; };
    public RegionHealth Health { get; set; };
}
```
```csharp
public sealed class GeoReplicationStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void AddRegion(GeoRegion region);
    public bool RemoveRegion(string regionId);
    public IReadOnlyCollection<GeoRegion> GetRegions();;
    public void SetPrimaryRegion(string regionId);
    public GeoRegion? GetPrimaryRegion();
    public void SelectPrimaryByLatency();
    public double CalculateDistance(string regionId1, string regionId2);
    public GeoRegion? RouteToNearest(string fromRegionId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public async Task CheckRegionHealthAsync(CancellationToken ct = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CrossRegionStrategy : EnhancedReplicationStrategyBase
{
}
    public CrossRegionStrategy(TimeSpan? boundedStaleness = null) : base();
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public TimeSpan BoundedStaleness;;
    public void SetAutoFailover(bool enabled);
    public void AddRegion(GeoRegion region);
    public GeoRegion? GetActiveRegion();
    public async Task<bool> FailoverAsync(string targetRegionId, CancellationToken ct = default);
    public async Task MonitorAndFailoverAsync(CancellationToken ct = default);
    public bool IsWithinStaleness(string regionId);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateReplication/Strategies/CDC/CdcStrategies.cs
```csharp
public sealed class CdcEvent
{
}
    public required string EventId { get; init; }
    public required string Source { get; init; }
    public required CdcOperation Operation { get; init; }
    public required string Key { get; init; }
    public byte[]? Before { get; init; }
    public byte[]? After { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public string? TransactionId { get; init; }
    public long Lsn { get; init; }
    public int SchemaVersion { get; init; };
}
```
```csharp
public sealed class KafkaConnectCdcStrategy : EnhancedReplicationStrategyBase
{
}
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void Configure(string bootstrapServers, int partitionCount);
    public void SetExactlyOnce(bool enabled);
    public string GetPartition(string key);
    public void Produce(CdcEvent cdcEvent);
    public IEnumerable<CdcEvent> Consume(string partition, int maxEvents = 100);
    public void CommitOffset(string partition, long offset);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class DebeziumCdcStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class ConnectorConfig;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void RegisterConnector(ConnectorConfig config);
    public async Task StartConnectorAsync(string connectorName, CancellationToken ct = default);
    public void StopConnector(string connectorName);
    public async Task PerformSnapshotAsync(string connectorName, CancellationToken ct = default);
    public void RegisterSchema(int version, string schemaJson);
    public string? GetSchema(int version);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class ConnectorConfig
{
}
    public required string Name { get; init; }
    public required string DatabaseType { get; init; }
    public required string ConnectionString { get; init; }
    public string[]? Tables { get; init; }
    public ConnectorStatus Status { get; set; };
    public long? CurrentLsn { get; set; }
    public DateTimeOffset? LastSnapshot { get; set; }
}
```
```csharp
public sealed class MaxwellCdcStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class MaxwellEvent;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void SetBootstrap(bool enabled);
    public void AddStream(string database, string table);
    public (string? File, long Position) GetBinlogPosition();
    public void SetBinlogPosition(string file, long position);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class MaxwellEvent
{
}
    public required string Database { get; init; }
    public required string Table { get; init; }
    public required string Type { get; init; }
    public required long Ts { get; init; }
    public long Xid { get; init; }
    public bool Commit { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public Dictionary<string, object>? Old { get; init; }
    public string? BinlogFile { get; init; }
    public long BinlogPosition { get; init; }
}
```
```csharp
public sealed class CanalCdcStrategy : EnhancedReplicationStrategyBase
{
}
    public sealed class CanalInstance;
    public sealed class CanalEntry;
    public override ReplicationCharacteristics Characteristics { get; };
    public override ReplicationCapabilities Capabilities;;
    public override ConsistencyModel ConsistencyModel;;
    public void CreateInstance(CanalInstance instance);
    public void StartInstance(string destination);
    public void StopInstance(string destination);
    public IEnumerable<CanalEntry> GetEntries(string destination, int batchSize = 100);
    public override async Task ReplicateAsync(string sourceNodeId, IEnumerable<string> targetNodeIds, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(ReplicationConflict conflict, CancellationToken cancellationToken = default);
    public override Task<bool> VerifyConsistencyAsync(IEnumerable<string> nodeIds, string dataId, CancellationToken cancellationToken = default);
    public override Task<TimeSpan> GetReplicationLagAsync(string sourceNodeId, string targetNodeId, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class CanalInstance
{
}
    public required string Destination { get; init; }
    public required string MasterAddress { get; init; }
    public string? StandbyAddress { get; init; }
    public string? Filter { get; init; }
    public bool IsRunning { get; set; }
    public long CurrentPosition { get; set; }
}
```
```csharp
public sealed class CanalEntry
{
}
    public required string LogfileName { get; init; }
    public required long LogfileOffset { get; init; }
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string EventType { get; init; }
    public long ExecuteTime { get; init; }
    public List<Dictionary<string, object>>? RowDataList { get; init; }
}
```
