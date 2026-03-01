# Plugin: UltimateDataLineage
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataLineage

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/LineageStrategyBase.cs
```csharp
public sealed record LineageNode
{
}
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string NodeType { get; init; }
    public string? SourceSystem { get; init; }
    public string? Path { get; init; }
    public Dictionary<string, string>? Schema { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime ModifiedAt { get; init; };
    public Dictionary<string, object>? Metadata { get; init; }
    public string[]? Tags { get; init; }
}
```
```csharp
public sealed record LineageEdge
{
}
    public required string EdgeId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required string EdgeType { get; init; }
    public string? TransformationDetails { get; init; }
    public string? TransformationCode { get; init; }
    public DateTime CreatedAt { get; init; };
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed record LineageGraph
{
}
    public required string RootNodeId { get; init; }
    public required IReadOnlyList<LineageNode> Nodes { get; init; }
    public required IReadOnlyList<LineageEdge> Edges { get; init; }
    public DateTime GeneratedAt { get; init; };
    public int Depth { get; init; }
    public int UpstreamCount { get; init; }
    public int DownstreamCount { get; init; }
}
```
```csharp
public sealed record ImpactAnalysisResult
{
}
    public required string SourceNodeId { get; init; }
    public required string ChangeType { get; init; }
    public required IReadOnlyList<string> DirectlyImpacted { get; init; }
    public required IReadOnlyList<string> IndirectlyImpacted { get; init; }
    public int ImpactScore { get; init; }
    public DateTime AnalyzedAt { get; init; };
    public IReadOnlyList<string>? Recommendations { get; init; }
}
```
```csharp
public sealed record ProvenanceRecord
{
}
    public required string RecordId { get; init; }
    public required string DataObjectId { get; init; }
    public required string Operation { get; init; }
    public required string Actor { get; init; }
    public DateTime Timestamp { get; init; };
    public IReadOnlyList<string>? SourceObjects { get; init; }
    public string? Transformation { get; init; }
    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed record LineageStrategyCapabilities
{
}
    public required bool SupportsUpstream { get; init; }
    public required bool SupportsDownstream { get; init; }
    public required bool SupportsTransformations { get; init; }
    public required bool SupportsSchemaEvolution { get; init; }
    public required bool SupportsImpactAnalysis { get; init; }
    public required bool SupportsVisualization { get; init; }
    public required bool SupportsRealTime { get; init; }
}
```
```csharp
public interface ILineageStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    LineageCategory Category { get; }
    LineageStrategyCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
    Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);;
    Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);;
    Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);;
    Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default);;
    Task InitializeAsync(CancellationToken ct = default);;
    Task DisposeAsync();;
}
```
```csharp
public abstract class LineageStrategyBase : StrategyBase, ILineageStrategy
{
}
    protected readonly BoundedDictionary<string, LineageNode> _nodes = new BoundedDictionary<string, LineageNode>(1000);
    protected readonly BoundedDictionary<string, LineageEdge> _edges = new BoundedDictionary<string, LineageEdge>(1000);
    protected readonly BoundedDictionary<string, List<ProvenanceRecord>> _provenance = new BoundedDictionary<string, List<ProvenanceRecord>>(1000);
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract LineageCategory Category { get; }
    public abstract LineageStrategyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected virtual Task InitializeCoreAsync(CancellationToken ct);;
    protected virtual Task DisposeCoreAsync();;
    public virtual Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public IReadOnlyList<ProvenanceRecord> GetProvenance(string dataObjectId);
    public virtual Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public virtual Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public virtual Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default);
    public IReadOnlyDictionary<string, long> GetCounters();;
    protected void AddNode(LineageNode node);;
    protected void AddEdge(LineageEdge edge);
}
```
```csharp
public sealed class LineageStrategyRegistry
{
}
    public void Register(ILineageStrategy strategy);
    public bool Unregister(string strategyId);;
    public ILineageStrategy? Get(string strategyId);;
    public IReadOnlyCollection<ILineageStrategy> GetAll();;
    public IReadOnlyCollection<ILineageStrategy> GetByCategory(LineageCategory category);;
    public int Count;;
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/UltimateDataLineagePlugin.cs
```csharp
public sealed class UltimateDataLineagePlugin : DataManagementPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public LineageStrategyRegistry Registry;;
    public string DefaultStrategy { get => _defaultStrategy; set => _defaultStrategy = value; }
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public bool RealTimeTrackingEnabled { get => _realTimeTrackingEnabled; set => _realTimeTrackingEnabled = value; }
    public UltimateDataLineagePlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Composition/ProvenanceCertificateService.cs
```csharp
[SdkCompatibility("3.0.0")]
public sealed class ProvenanceCertificateService : IDisposable
{
}
    public ProvenanceCertificateService(IMessageBus messageBus, ILogger? logger = null);
    public void StartListening();
    public async Task<ProvenanceCertificate> IssueCertificateAsync(string objectId, CancellationToken ct = default);
    public async Task<CertificateVerificationResult> VerifyCertificateAsync(ProvenanceCertificate certificate, CancellationToken ct = default);
    public Task<IReadOnlyList<ProvenanceCertificate>> GetCertificateHistoryAsync(string objectId, CancellationToken ct = default);
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Strategies/LineageStrategies.cs
```csharp
public sealed class InMemoryGraphStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default);
}
```
```csharp
public sealed class SqlTransformationStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class EtlPipelineStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ApiConsumptionStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ReportConsumptionStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Strategies/ActiveLineageStrategies.cs
```csharp
internal sealed class SelfTrackingDataStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default);
}
```
```csharp
internal sealed class RealTimeLineageCaptureStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
}
```
```csharp
internal sealed class LineageInferenceStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterSchema(string nodeId, Dictionary<string, string> schema);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
}
```
```csharp
internal sealed class ImpactAnalysisEngineStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterDependency(string sourceId, string targetId);
    public void SetCriticality(string nodeId, double score);
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default);
}
```
```csharp
internal sealed class LineageVisualizationStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public string ExportDot(string rootNodeId);
    public string ExportMermaid(string rootNodeId);
    public string ExportJson(string rootNodeId);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Strategies/ConsciousnessLineageStrategies.cs
```csharp
internal sealed class MultiHopLineageBfsStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public void RegisterScore(string objectId, ConsciousnessScore? score);
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    public Task<List<ConsciousnessLineageNode>> TraverseBfsAsync(string objectId, int maxDepth = 10, bool upstream = false, CancellationToken ct = default);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
}
```
```csharp
internal sealed class ConsciousnessAwareLineageStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ConsciousnessAwareLineageStrategy(MultiHopLineageBfsStrategy bfsStrategy);
    public async Task<ConsciousnessImpactResult> AnalyzeImpactAsync(string objectId, CancellationToken ct = default);
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);;
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);;
}
```
```csharp
internal sealed class ConsciousnessImpactPropagationStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public ConsciousnessImpactPropagationStrategy(MultiHopLineageBfsStrategy bfsStrategy);
    public IReadOnlyList<LineagePropagationEvent> PropagationHistory;;
    public async Task<List<LineagePropagationEvent>> PropagateAsync(string sourceObjectId, double oldScore, double newScore, CancellationToken ct = default);
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Strategies/LineageEnhancedStrategies.cs
```csharp
public sealed class LineageVersioningStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LineageSnapshot CreateSnapshot(string dataObjectId, SimpleLineageGraph graph, string? description = null);
    public LineageSnapshot? GetAtTime(string dataObjectId, DateTimeOffset timestamp);
    public IReadOnlyList<LineageSnapshot> GetHistory(string dataObjectId);;
}
```
```csharp
public sealed class EnhancedBlastRadiusStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public BlastRadiusResult CalculateBlastRadius(string sourceNodeId, SimpleLineageGraph graph, int maxDepth = 10);
}
```
```csharp
public sealed class LineageDiffStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public LineageDiffResult ComputeDiff(SimpleLineageGraph before, SimpleLineageGraph after);
}
```
```csharp
public sealed class CrossSystemLineageStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SystemRegistration RegisterSystem(string systemId, string name, string endpoint, string? busTopicPrefix = null);
    public CrossSystemEdge CreateCrossEdge(string sourceSystemId, string sourceNodeId, string targetSystemId, string targetNodeId, string? transformationType = null);
    public IReadOnlyList<CrossSystemEdge> GetCrossEdges(string sourceSystemId, string sourceNodeId);
    public IReadOnlyList<SystemRegistration> GetSystems();;
}
```
```csharp
public sealed record LineageSnapshot
{
}
    public required string SnapshotId { get; init; }
    public required string DataObjectId { get; init; }
    public int Version { get; init; }
    public required SimpleLineageGraph Graph { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Description { get; init; }
}
```
```csharp
public sealed record SimpleLineageGraph
{
}
    public List<SimpleLineageNode> Nodes { get; init; };
    public List<SimpleLineageEdge> Edges { get; init; };
}
```
```csharp
public sealed record SimpleLineageNode
{
}
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; };
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public sealed record SimpleLineageEdge
{
}
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public string? TransformationType { get; init; }
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public sealed record BlastRadiusResult
{
}
    public required string SourceNodeId { get; init; }
    public int TotalAffected { get; init; }
    public int MaxDepth { get; init; }
    public List<AffectedNode> AffectedNodes { get; init; };
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
}
```
```csharp
public sealed record AffectedNode
{
}
    public required string NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? NodeType { get; init; }
    public int Distance { get; init; }
    public double Criticality { get; init; }
    public string[] PropagationPath { get; init; };
}
```
```csharp
public sealed record LineageDiffResult
{
}
    public List<SimpleLineageNode> AddedNodes { get; init; };
    public List<SimpleLineageNode> RemovedNodes { get; init; };
    public List<NodeModification> ModifiedNodes { get; init; };
    public List<SimpleLineageEdge> AddedEdges { get; init; };
    public List<SimpleLineageEdge> RemovedEdges { get; init; };
    public int TotalChanges { get; init; }
}
```
```csharp
public sealed record NodeModification
{
}
    public required string NodeId { get; init; }
    public required SimpleLineageNode Before { get; init; }
    public required SimpleLineageNode After { get; init; }
}
```
```csharp
public sealed record SystemRegistration
{
}
    public required string SystemId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public string? BusTopicPrefix { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public bool IsActive { get; init; }
}
```
```csharp
public sealed record CrossSystemEdge
{
}
    public required string EdgeId { get; init; }
    public required string SourceSystemId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetSystemId { get; init; }
    public required string TargetNodeId { get; init; }
    public string? TransformationType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Strategies/AdvancedLineageStrategies.cs
```csharp
public sealed class BlastRadiusStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class DagVisualizationStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class CryptoProvenanceStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
}
```
```csharp
public sealed class AuditTrailStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class GdprLineageStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class MlPipelineLineageStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class SchemaEvolutionStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```
```csharp
public sealed class ExternalSourceStrategy : LineageStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override LineageCategory Category;;
    public override LineageStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Scaling/LineageScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Paginated result for lineage queries")]
public sealed class LineagePagedResult<T>
{
}
    public IReadOnlyList<T> Items { get; }
    public int TotalCount { get; }
    public bool HasMore { get; }
    public LineagePagedResult(IReadOnlyList<T> items, int totalCount, bool hasMore);
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Lineage graph node")]
public sealed class LineageNode
{
}
    public string NodeId { get; }
    public int Depth { get; }
    public byte[]? Metadata { get; }
    public LineageNode(string nodeId, int depth, byte[]? metadata = null);
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Lineage scaling with persistent graph, partitioned caches, paginated BFS")]
public sealed class LineageScalingManager : IScalableSubsystem, IDisposable
{
}
    public const int DefaultPartitionCount = 16;
    public const int DefaultMaxNodesPerPartition = 50_000;
    public const int DefaultMaxTraversalDepth = 50;
    public const double HotPartitionThreshold = 0.90;
    public LineageScalingManager(IPersistentBackingStore? backingStore = null, ScalingLimits? initialLimits = null, int? partitionCount = null, int? maxTraversalDepth = null);
    public async Task PutNodeAsync(string nodeId, byte[] adjacencyData, CancellationToken ct = default);
    public async Task<byte[]?> GetNodeAsync(string nodeId, CancellationToken ct = default);
    public bool RemoveNode(string nodeId);
    public LineagePagedResult<LineageNode> TraceLineage(string nodeId, LineageDirection direction, int maxDepth, int offset, int limit);
    public int MaxTraversalDepth { get => _maxTraversalDepth; set => _maxTraversalDepth = Math.Max(1, value); }
    public int PartitionCount;;
    public long TotalNodeCount;;
    public int GetPartitionCapacity(int partitionIndex);
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
        long totalNodes = Interlocked.Read(ref _totalNodeCount);
        long maxCapacity = 0;
        for (int i = 0; i < _partitionCount; i++)
            maxCapacity += _partitionCapacities[i];
        if (maxCapacity == 0)
            return BackpressureState.Normal;
        double utilization = (double)totalNodes / maxCapacity;
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
```csharp
private sealed class EdgeList
{
}
    public List<string>? Targets { get; set; }
}
```
