# Plugin: SemanticSync
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.SemanticSync

### File: Plugins/DataWarehouse.Plugins.SemanticSync/SemanticSyncPlugin.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
public sealed class SemanticSyncPlugin : OrchestrationPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string OrchestrationMode;;
    public override PluginCategory Category;;
    public IReadOnlyCollection<string> RegisteredStrategyNames;;
    public int StrategyCount;;
    public void RegisterStrategy(string name, StrategyBase strategy);
    public T? GetStrategy<T>(string name)
    where T : StrategyBase;
    public StrategyBase? GetStrategy(string name);
    public IReadOnlyDictionary<string, StrategyBase> GetAllStrategies();;
    public bool RemoveStrategy(string name);
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStopCoreAsync();
    protected override void Dispose(bool disposing);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Null AI provider for graceful degradation")]
internal sealed class NullAIProvider : IAIProvider
{
}
    public static readonly NullAIProvider Instance = new();
    public string ProviderId;;
    public string DisplayName;;
    public bool IsAvailable;;
    public AICapabilities Capabilities;;
    public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default);;
    public async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default);;
    public Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Orchestration/SyncPipeline.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync orchestration")]
internal sealed class SyncPipeline
{
}
    public SyncPipeline(ISemanticClassifier classifier, ISyncFidelityController fidelityController, ISummaryRouter router, ISemanticConflictResolver conflictResolver, EdgeInferenceCoordinator edgeInference);
    public async Task<SyncPipelineResult> ExecuteAsync(string dataId, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata, CancellationToken ct);
    public async Task<ConflictResolutionResult> ResolveConflictAsync(string dataId, ReadOnlyMemory<byte> localData, ReadOnlyMemory<byte> remoteData, IMessageBus? messageBus, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Orchestration/SemanticSyncOrchestrator.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync orchestration")]
internal sealed class SemanticSyncOrchestrator : IAsyncDisposable, IDisposable
{
}
    public SemanticSyncOrchestrator(SyncPipeline pipeline, BandwidthBudgetTracker? budgetTracker = null, int maxConcurrency = DefaultMaxConcurrency);
    public long TotalSyncs;;
    public long TotalBytesSaved;;
    public double AverageCompressionRatio
{
    get
    {
        long original = Interlocked.Read(ref _totalOriginalBytes);
        long payload = Interlocked.Read(ref _totalPayloadBytes);
        return original > 0 ? (double)payload / original : 1.0;
    }
}
    public int DeferredCount;;
    public void StartAsync();
    public async Task StopAsync();
    public async Task<SyncPipelineResult> SubmitSyncAsync(string dataId, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata, CancellationToken ct);
    public void Dispose();
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/EdgeInference/FederatedSyncLearner.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
internal sealed class FederatedSyncLearner
{
}
    public FederatedSyncLearner(LocalModelManager modelManager);
    public async Task SubmitLocalGradientAsync(SemanticImportance label, float[] embedding, IMessageBus? messageBus, CancellationToken ct);
    public async Task OnAggregatedModelReceivedAsync(ReadOnlyMemory<byte> aggregatedModel, CancellationToken ct);
    public Task<ReadOnlyMemory<byte>> ExportGradientsAsync(CancellationToken ct);
    public static string AggregatedModelTopic;;
    public static string GradientTopic;;
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/EdgeInference/LocalModelManager.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
internal sealed class LocalModelManager : IDisposable
{
}
    public SyncInferenceModel? GetCurrentModel();
    public Task LoadModelAsync(ReadOnlyMemory<byte> serializedModel, CancellationToken ct);
    public Task<ReadOnlyMemory<byte>> SerializeModelAsync(SyncInferenceModel model, CancellationToken ct);
    public void RollbackModel();
    public SyncInferenceModel CreateDefaultModel();
    public SyncInferenceModel UpdateCentroids(SyncInferenceModel model, SemanticImportance importance, float[] newEmbedding, double learningRate);
    public static int ExpectedDimensions;;
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/EdgeInference/EdgeInferenceCoordinator.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
public sealed class EdgeInferenceCoordinator : SemanticSyncStrategyBase
{
}
    internal EdgeInferenceCoordinator(LocalModelManager modelManager, ISemanticClassifier? cloudClassifier = null, IAIProvider? aiProvider = null);
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    public override bool SupportsLocalInference;;
    public async Task<EdgeInferenceResult> InferAsync(string dataId, ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Fidelity/AdaptiveFidelityController.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
public sealed class AdaptiveFidelityController : SemanticSyncStrategyBase, ISyncFidelityController
{
}
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    internal AdaptiveFidelityController(BandwidthBudgetTracker tracker, FidelityPolicyEngine policyEngine);
    public Task<SyncDecision> DecideFidelityAsync(SemanticClassification classification, FidelityBudget budget, CancellationToken ct = default);
    public Task<FidelityBudget> GetCurrentBudgetAsync(CancellationToken ct = default);
    public Task UpdateBandwidthAsync(long currentBandwidthBps, CancellationToken ct = default);
    public Task SetPolicyAsync(FidelityPolicy policy, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Fidelity/BandwidthBudgetTracker.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
internal sealed class BandwidthBudgetTracker
{
}
    public BandwidthBudgetTracker();
    public void UpdateBandwidth(long currentBps);
    public void RecordConsumption(SyncFidelity fidelity, long bytes);
    public void RecordSyncQueued();
    public void RecordSyncCompleted();
    public FidelityBudget GetCurrentBudget();
    public double GetRemainingCapacityPercent();
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Fidelity/FidelityPolicyEngine.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
internal sealed class FidelityPolicyEngine
{
}
    public FidelityPolicyEngine(FidelityPolicy policy);
    public SyncFidelity ApplyPolicy(SemanticClassification classification, SyncFidelity proposedFidelity);
    public SyncFidelity GetFidelityForBandwidth(long bandwidthBps);
    public bool ShouldDefer(SemanticClassification classification, FidelityBudget budget);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/ConflictResolution/ConflictClassificationEngine.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Sync fidelity control")]
internal sealed class ConflictClassificationEngine
{
}
    public ConflictType Classify(SemanticConflict conflict, double? similarityScore);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/ConflictResolution/EmbeddingSimilarityDetector.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Sync fidelity control")]
internal sealed class EmbeddingSimilarityDetector
{
}
    public EmbeddingSimilarityDetector(IAIProvider? aiProvider = null);
    public double? LastSimilarityScore { get; private set; }
    public async Task<SemanticConflict?> DetectAsync(string dataId, ReadOnlyMemory<byte> localData, ReadOnlyMemory<byte> remoteData, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/ConflictResolution/SemanticMergeResolver.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Sync fidelity control")]
public sealed class SemanticMergeResolver : SemanticSyncStrategyBase, ISemanticConflictResolver
{
}
    internal SemanticMergeResolver(EmbeddingSimilarityDetector detector, ConflictClassificationEngine classificationEngine);
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    public override bool SupportsLocalInference;;
    public ConflictResolverCapabilities Capabilities { get; };
    public Task<SemanticConflict?> DetectConflictAsync(string dataId, ReadOnlyMemory<byte> localData, ReadOnlyMemory<byte> remoteData, CancellationToken ct = default);
    public Task<ConflictType> ClassifyConflictAsync(SemanticConflict conflict, CancellationToken ct = default);
    public Task<ConflictResolutionResult> ResolveAsync(SemanticConflict conflict, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Classification/EmbeddingClassifier.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic classification")]
public sealed class EmbeddingClassifier : SemanticSyncStrategyBase, ISemanticClassifier
{
}
    public EmbeddingClassifier(IAIProvider aiProvider);
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    public override bool SupportsLocalInference;;
    public SemanticClassifierCapabilities Capabilities { get; };
    public async Task<SemanticClassification> ClassifyAsync(ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items, [EnumeratorCancellation] CancellationToken ct = default);
    public async Task<double> ComputeSemanticSimilarityAsync(ReadOnlyMemory<byte> data1, ReadOnlyMemory<byte> data2, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Classification/RuleBasedClassifier.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic classification")]
public sealed class RuleBasedClassifier : SemanticSyncStrategyBase, ISemanticClassifier
{
#endregion
}
    public RuleBasedClassifier();
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    public override bool SupportsLocalInference;;
    public SemanticClassifierCapabilities Capabilities { get; };
    public Task<SemanticClassification> ClassifyAsync(ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items, [EnumeratorCancellation] CancellationToken ct = default);
    public Task<double> ComputeSemanticSimilarityAsync(ReadOnlyMemory<byte> data1, ReadOnlyMemory<byte> data2, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Classification/HybridClassifier.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic classification")]
public sealed class HybridClassifier : SemanticSyncStrategyBase, ISemanticClassifier
{
}
    public HybridClassifier(ISemanticClassifier embeddingClassifier, ISemanticClassifier ruleBasedClassifier, double embeddingWeight = 0.7);
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    public override bool SupportsLocalInference;;
    public SemanticClassifierCapabilities Capabilities;;
    public async Task<SemanticClassification> ClassifyAsync(ReadOnlyMemory<byte> data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async IAsyncEnumerable<SemanticClassification> ClassifyBatchAsync(IAsyncEnumerable<(string DataId, ReadOnlyMemory<byte> Data)> items, [EnumeratorCancellation] CancellationToken ct = default);
    public async Task<double> ComputeSemanticSimilarityAsync(ReadOnlyMemory<byte> data1, ReadOnlyMemory<byte> data2, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Routing/BandwidthAwareSummaryRouter.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Summary routing")]
public sealed class BandwidthAwareSummaryRouter : SemanticSyncStrategyBase, ISummaryRouter
{
}
    public override string StrategyId;;
    public override string Name;;
    public override string Description;;
    public override string SemanticDomain;;
    public SummaryRouterCapabilities Capabilities { get; };
    internal BandwidthAwareSummaryRouter(SummaryGenerator summaryGenerator, FidelityDownsampler downsampler);
    public Task<SyncDecision> RouteAsync(string dataId, SemanticClassification classification, FidelityBudget budget, CancellationToken ct = default);
    public Task<DataSummary> GenerateSummaryAsync(string dataId, ReadOnlyMemory<byte> rawData, SyncFidelity targetFidelity, CancellationToken ct = default);
    public Task<ReadOnlyMemory<byte>> ReconstructFromSummaryAsync(DataSummary summary, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Routing/SummaryGenerator.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Summary routing")]
internal sealed class SummaryGenerator
{
}
    public SummaryGenerator(IAIProvider? aiProvider = null);
    public async Task<DataSummary> GenerateAsync(string dataId, ReadOnlyMemory<byte> rawData, SyncFidelity targetFidelity, CancellationToken ct = default);
    public async Task<ReadOnlyMemory<byte>> ReconstructAsync(DataSummary summary, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Routing/FidelityDownsampler.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 60: Summary routing")]
internal sealed class FidelityDownsampler
{
}
    internal static readonly IReadOnlyDictionary<SyncFidelity, double> TargetRatios = new Dictionary<SyncFidelity, double>
{
    [SyncFidelity.Full] = 1.00,
    [SyncFidelity.Detailed] = 0.80,
    [SyncFidelity.Standard] = 0.50,
    [SyncFidelity.Summarized] = 0.15,
    [SyncFidelity.Metadata] = 0.02
};
    public ReadOnlyMemory<byte> Downsample(ReadOnlyMemory<byte> data, SyncFidelity from, SyncFidelity to, IDictionary<string, string>? metadata = null);
}
```
