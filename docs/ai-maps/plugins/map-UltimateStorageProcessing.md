# Plugin: UltimateStorageProcessing
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateStorageProcessing

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/UltimateStorageProcessingPlugin.cs
```csharp
public sealed class UltimateStorageProcessingPlugin : DataWarehouse.SDK.Contracts.Hierarchy.StoragePluginBase, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    internal StorageProcessingStrategyRegistryInternal Registry;;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.storageprocessing",
                DisplayName = "Ultimate Storage Processing",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.Compute,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = [..SemanticTags]
            }
        };
        // Per AD-05 (Phase 25b): capability registration is now plugin-level responsibility.
        foreach (var strategy in _registry.GetAllStrategies())
        {
            if (strategy is StorageProcessingStrategyBase baseStrategy)
            {
                capabilities.Add(new RegisteredCapability { CapabilityId = $"storageprocessing.{baseStrategy.StrategyId}", DisplayName = baseStrategy.Name, Description = baseStrategy.Description, Category = SDK.Contracts.CapabilityCategory.Custom, SubCategory = "StorageProcessing", PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = new[] { "storage-processing", baseStrategy.StrategyId }, SemanticDescription = baseStrategy.Description });
            }
        }

        return capabilities.AsReadOnly();
    }
}
    public UltimateStorageProcessingPlugin();
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override Task OnStopCoreAsync();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    public IStorageProcessingStrategy? GetStrategy(string strategyId);
    public async Task<ProcessingResult> ProcessAsync(string strategyId, ProcessingQuery query, CancellationToken ct = default);
    protected override void Dispose(bool disposing);
    public override Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);;
    public override Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);;
    public override Task DeleteAsync(string key, CancellationToken ct = default);;
    public override Task<bool> ExistsAsync(string key, CancellationToken ct = default);;
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public override Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);;
    public override Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/StorageProcessingStrategyRegistryInternal.cs
```csharp
internal sealed class StorageProcessingStrategyRegistryInternal
{
}
    public int Count;;
    public int DiscoverStrategies(params Assembly[] assemblies);
    public bool Register(StorageProcessingStrategyBase strategy);
    public IStorageProcessingStrategy? GetStrategy(string strategyId);
    public bool TryGetStrategy(string strategyId, out IStorageProcessingStrategy? strategy);
    public IReadOnlyCollection<IStorageProcessingStrategy> GetStrategiesByCategory(string category);
    public IReadOnlyCollection<IStorageProcessingStrategy> GetAllStrategies();
    public IReadOnlyCollection<string> GetAllStrategyIds();
    public IReadOnlyCollection<string> GetAllCategories();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Infrastructure/SharedCacheManager.cs
```csharp
internal sealed class SharedCacheManager : IDisposable
{
}
    public int Count;;
    public SharedCacheManager(TimeSpan? cleanupInterval = null);
    public IReadOnlyDictionary<string, object?>? TryGetCached(string key);
    public void SetCached(string key, IReadOnlyDictionary<string, object?> data, TimeSpan ttl);
    public bool Invalidate(string key);
    public int InvalidateByPrefix(string prefix);
    public IReadOnlyDictionary<string, object> GetStats();
    public void Clear();
    public void Dispose();
}
```
```csharp
private sealed class CachedEntry
{
}
    public required string Key { get; init; }
    public required IReadOnlyDictionary<string, object?> Data { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastAccessed { get; set; }
    public required TimeSpan TimeToLive { get; init; }
    public bool IsExpired;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Infrastructure/ProcessingJobScheduler.cs
```csharp
internal sealed class ProcessingJobScheduler : IDisposable
{
}
    public int MaxConcurrency { get; }
    public int PendingCount
{
    get
    {
        lock (_queueLock)
        {
            return _queue.Count;
        }
    }
}
    public int ActiveCount;;
    public ProcessingJobScheduler(int maxConcurrency = 0);
    public async Task<ProcessingResult> ScheduleAsync(IStorageProcessingStrategy strategy, ProcessingQuery query, int priority = 5, CancellationToken ct = default);
    public bool Cancel(string jobId);
    public int GetPendingCount();;
    public IReadOnlyDictionary<string, object> GetStats();
    public void Dispose();
}
```
```csharp
private sealed class ScheduledJob
{
}
    public required string JobId { get; init; }
    public required IStorageProcessingStrategy Strategy { get; init; }
    public required ProcessingQuery Query { get; init; }
    public required int Priority { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required TaskCompletionSource<ProcessingResult> Completion { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/CliProcessHelper.cs
```csharp
internal static class CliProcessHelper
{
}
    public static async Task<CliOutput> RunAsync(string fileName, string arguments, string? workingDirectory = null, int timeoutMs = 300_000, CancellationToken ct = default);
    public static ProcessingResult ToProcessingResult(CliOutput output, string sourcePath, string toolName, Dictionary<string, object?>? extraData = null);
    public static ProcessingResult ToolNotFound(string toolName, string source, Stopwatch sw);
    public static async IAsyncEnumerable<ProcessingResult> EnumerateProjectFiles(ProcessingQuery query, string[] extensions, Stopwatch sw, [EnumeratorCancellation] CancellationToken ct = default);
    public static Task<AggregationResult> AggregateProjectFiles(ProcessingQuery query, AggregationType aggregationType, string[] extensions, CancellationToken ct);
    public static T? GetOption<T>(ProcessingQuery query, string key);
}
```
```csharp
internal sealed record CliOutput
{
}
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required bool Success { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Document/JupyterExecuteStrategy.cs
```csharp
internal sealed class JupyterExecuteStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Document/LatexRenderStrategy.cs
```csharp
internal sealed class LatexRenderStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Document/MinificationStrategy.cs
```csharp
internal sealed class MinificationStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Document/MarkdownRenderStrategy.cs
```csharp
internal sealed class MarkdownRenderStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Document/SassCompileStrategy.cs
```csharp
internal sealed class SassCompileStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/GameAsset/LodGenerationStrategy.cs
```csharp
internal sealed class LodGenerationStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/GameAsset/AudioConversionStrategy.cs
```csharp
internal sealed class AudioConversionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/GameAsset/MeshOptimizationStrategy.cs
```csharp
internal sealed class MeshOptimizationStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/GameAsset/AssetBundlingStrategy.cs
```csharp
internal sealed class AssetBundlingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/GameAsset/TextureCompressionStrategy.cs
```csharp
internal sealed class TextureCompressionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/GameAsset/ShaderCompilationStrategy.cs
```csharp
internal sealed class ShaderCompilationStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/GradleBuildStrategy.cs
```csharp
internal sealed class GradleBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/NpmBuildStrategy.cs
```csharp
internal sealed class NpmBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/GoBuildStrategy.cs
```csharp
internal sealed class GoBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/BazelBuildStrategy.cs
```csharp
internal sealed class BazelBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/DockerBuildStrategy.cs
```csharp
internal sealed class DockerBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/TypeScriptBuildStrategy.cs
```csharp
internal sealed class TypeScriptBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/MavenBuildStrategy.cs
```csharp
internal sealed class MavenBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/DotNetBuildStrategy.cs
```csharp
internal sealed class DotNetBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Build/RustBuildStrategy.cs
```csharp
internal sealed class RustBuildStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Data/SchemaInferenceStrategy.cs
```csharp
internal sealed class SchemaInferenceStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```
```csharp
private sealed class FieldStats
{
}
    public int OccurrenceCount;
    public int NullCount;
    public int StringCount;
    public int NumberCount;
    public int IntCount;
    public int FloatCount;
    public int BoolCount;
    public int DateCount;
    public int ArrayCount;
    public bool IsNested;
    public readonly HashSet<string> DistinctValues = new(StringComparer.OrdinalIgnoreCase);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Data/VectorEmbeddingStrategy.cs
```csharp
internal sealed class VectorEmbeddingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Data/ParquetCompactionStrategy.cs
```csharp
internal sealed class ParquetCompactionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Data/DataValidationStrategy.cs
```csharp
internal sealed class DataValidationStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Data/IndexBuildingStrategy.cs
```csharp
internal sealed class IndexBuildingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Media/HlsPackagingStrategy.cs
```csharp
internal sealed class HlsPackagingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Media/WebPConversionStrategy.cs
```csharp
internal sealed class WebPConversionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Media/AvifConversionStrategy.cs
```csharp
internal sealed class AvifConversionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Media/ImageMagickStrategy.cs
```csharp
internal sealed class ImageMagickStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Media/DashPackagingStrategy.cs
```csharp
internal sealed class DashPackagingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Media/FfmpegTranscodeStrategy.cs
```csharp
internal sealed class FfmpegTranscodeStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Compression/TransparentCompressionStrategy.cs
```csharp
internal sealed class TransparentCompressionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Compression/OnStorageLz4Strategy.cs
```csharp
internal sealed class OnStorageLz4Strategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override async Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Compression/OnStorageBrotliStrategy.cs
```csharp
internal sealed class OnStorageBrotliStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```
```csharp
internal static class CompressionAggregationHelper
{
}
    public static Task<AggregationResult> AggregateFileSizes(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Compression/OnStorageSnappyStrategy.cs
```csharp
internal sealed class OnStorageSnappyStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Compression/OnStorageZstdStrategy.cs
```csharp
internal sealed class OnStorageZstdStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override async Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/Compression/ContentAwareCompressionStrategy.cs
```csharp
internal sealed class ContentAwareCompressionStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/IndustryFirst/IncrementalProcessingStrategy.cs
```csharp
internal sealed class IncrementalProcessingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/IndustryFirst/BuildCacheSharingStrategy.cs
```csharp
internal sealed class BuildCacheSharingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```
```csharp
private sealed class CacheEntry
{
}
    public required string Key { get; init; }
    public required string Hash { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset StoredAt { get; init; }
    public required string Namespace { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/IndustryFirst/DependencyAwareProcessingStrategy.cs
```csharp
internal sealed class DependencyAwareProcessingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/IndustryFirst/PredictiveProcessingStrategy.cs
```csharp
internal sealed class PredictiveProcessingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```
```csharp
private sealed class AccessStats
{
}
    public DateTime LastAccessed;
    public int AccessCount;
    public double EmaPrediction;
    public long Size;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/IndustryFirst/GpuAcceleratedProcessingStrategy.cs
```csharp
internal sealed class GpuAcceleratedProcessingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/Strategies/IndustryFirst/CostOptimizedProcessingStrategy.cs
```csharp
internal sealed class CostOptimizedProcessingStrategy : StorageProcessingStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageProcessingCapabilities Capabilities;;
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default);
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default);
}
```
```csharp
private sealed record StrategyEstimate(string Name, double CpuCost, double MemoryCost, double IoCost, double Quality)
{
}
    public double TotalCost;;
}
```
```csharp
private sealed class CostRecord
{
}
    public required string Strategy { get; init; }
    public required double PredictedCost { get; init; }
    public required double ActualCost { get; init; }
    public required long FileSize { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```
