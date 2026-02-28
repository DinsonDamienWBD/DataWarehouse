# Plugin: UltimateCompute
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateCompute

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/UltimateComputePlugin.cs
```csharp
public sealed class UltimateComputePlugin : ComputePluginBase, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string RuntimeType;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    internal ComputeRuntimeStrategyRegistry Registry;;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.compute",
                DisplayName = "Ultimate Compute",
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
            if (strategy is ComputeRuntimeStrategyBase baseStrategy)
            {
                capabilities.Add(new RegisteredCapability { CapabilityId = $"compute.{baseStrategy.StrategyId}", DisplayName = baseStrategy.Name, Description = baseStrategy.Description, Category = SDK.Contracts.CapabilityCategory.Custom, SubCategory = "Compute", PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = new[] { "compute", baseStrategy.StrategyId }, SemanticDescription = baseStrategy.Description });
            }
        }

        return capabilities.AsReadOnly();
    }
}
    public UltimateComputePlugin();
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override Task OnStopCoreAsync();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    public IComputeRuntimeStrategy? GetStrategy(string strategyId);
    public async Task<ComputeResult> ExecuteAsync(string strategyId, ComputeTask task, CancellationToken ct = default);
    protected override void Dispose(bool disposing);
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/ComputeRuntimeStrategyRegistry.cs
```csharp
internal sealed class ComputeRuntimeStrategyRegistry
{
}
    public int Count;;
    public int DiscoverStrategies(params Assembly[] assemblies);
    public bool Register(ComputeRuntimeStrategyBase strategy);
    public IComputeRuntimeStrategy? GetStrategy(ComputeRuntime runtime);
    public IComputeRuntimeStrategy? GetStrategy(string strategyId);
    public bool TryGetStrategy(string strategyId, out IComputeRuntimeStrategy? strategy);
    public IReadOnlyCollection<IComputeRuntimeStrategy> GetStrategiesByRuntime(ComputeRuntime runtime);
    public IReadOnlyCollection<IComputeRuntimeStrategy> GetAllStrategies();
    public IReadOnlyCollection<string> GetAllStrategyIds();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/ComputeRuntimeStrategyBase.cs
```csharp
internal abstract class ComputeRuntimeStrategyBase : StrategyBase, IComputeRuntimeStrategy
{
#endregion
}
    public abstract override string StrategyId { get; }
    public abstract string StrategyName { get; }
    public override string Name;;
    public abstract ComputeRuntime Runtime { get; }
    public abstract ComputeCapabilities Capabilities { get; }
    public abstract IReadOnlyList<ComputeRuntime> SupportedRuntimes { get; }
    public abstract Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);;
    public new virtual Task InitializeAsync(CancellationToken cancellationToken = default);
    public virtual Task DisposeAsync(CancellationToken cancellationToken = default);
    protected record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed);;
    protected async Task<ProcessResult> RunProcessAsync(string executable, string arguments, string? stdin = null, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    protected async Task<bool> IsToolAvailableAsync(string executable, string versionFlag = "--version", CancellationToken cancellationToken = default);
    protected async Task<ComputeResult> MeasureExecutionAsync(string taskId, Func<Task<(byte[] output, string? logs)>> executeFunc, CancellationToken cancellationToken = default);
    protected static void ValidateTask(ComputeTask task);
    protected static TimeSpan GetEffectiveTimeout(ComputeTask task, TimeSpan? defaultTimeout = null);
    protected static long GetMaxMemoryBytes(ComputeTask task, long defaultBytes = 256 * 1024 * 1024);
    protected static byte[] EncodeOutput(string text);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/SelfEmulatingComputeStrategy.cs
```csharp
internal sealed class SelfEmulatingComputeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override string Description;;
    public override bool IsProductionReady;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Infrastructure/DataLocalityPlacement.cs
```csharp
internal sealed class DataLocalityPlacement
{
}
    public const double SameNodeScore = 1.0;
    public const double SameRackScore = 0.7;
    public const double SameDatacenterScore = 0.4;
    public const double RemoteScore = 0.1;
    public double LocalityWeight { get; set; };
    public double CapabilityWeight { get; set; };
    public IReadOnlyList<PlacementScore> FindOptimalRuntime(ComputeTask task, IReadOnlyList<IComputeRuntimeStrategy> strategies, DataLocation? dataLocation = null, IReadOnlyDictionary<string, DataLocation>? runtimeLocations = null);
    public static double ScoreLocality(DataLocation dataLocation, DataLocation runtimeLocation);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Infrastructure/JobScheduler.cs
```csharp
internal sealed class JobScheduler : IDisposable
{
}
    public JobScheduler(int maxConcurrency = 0);
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
    public int MaxConcurrency;;
    public async Task<ComputeResult> ScheduleAsync(ComputeTask task, IComputeRuntimeStrategy strategy, int priority = 5, CancellationToken cancellationToken = default);
    public bool Cancel(string taskId);
    public JobStatus? GetStatus(string taskId);
    public JobInfo? GetJobInfo(string taskId);
    public void Dispose();
}
```
```csharp
private sealed class ScheduledJob
{
}
    public required string TaskId { get; init; }
    public required ComputeTask ComputeTask { get; init; }
    public required IComputeRuntimeStrategy Strategy { get; init; }
    public required int Priority { get; init; }
    public JobStatus Status { get; set; }
    public required DateTime ScheduledAt { get; init; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public required TaskCompletionSource<ComputeResult> CompletionSource { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Scaling/WasmScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Per-module WASM configuration")]
public sealed record WasmModuleConfig(string ModuleId, int MaxPages = 256, int MaxPoolSize = 10, bool PersistState = false)
{
}
    public const int MinPages = 1;
    public const int MaxPagesLimit = 65_536;
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: Warm WASM instance for pool reuse")]
public sealed record WasmInstance(string ModuleId, string InstanceId, DateTime CreatedAtUtc)
{
}
    public bool IsReady { get; set; };
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-10: WASM scaling manager with dynamic limits and instance pooling")]
public sealed class WasmScalingManager : IScalableSubsystem, IDisposable
{
}
    public WasmScalingManager(IPersistentBackingStore? backingStore = null, ScalingLimits? limits = null);
    public WasmModuleConfig GetModuleConfig(string moduleId);
    public async Task SetModuleConfigAsync(WasmModuleConfig config, CancellationToken ct = default);
    public int GetEffectiveMaxPages(string moduleId);
    public async Task AcquireExecutionSlotAsync(CancellationToken ct = default);
    public void ReleaseExecutionSlot();
    public int MaxConcurrentExecutions;;
    public WasmInstance? AcquireWarmInstance(string moduleId);
    public void ReturnWarmInstance(WasmInstance instance);
    public async Task<bool> PersistModuleStateAsync(string moduleId, byte[] linearMemory, CancellationToken ct = default);
    public async Task<byte[]?> RestoreModuleStateAsync(string moduleId, CancellationToken ct = default);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState
{
    get
    {
        long pending = Interlocked.Read(ref _pendingExecutions);
        int maxQueue = _currentLimits.MaxQueueDepth;
        if (pending <= 0)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.5)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.8)
            return BackpressureState.Warning;
        if (pending < maxQueue)
            return BackpressureState.Critical;
        return BackpressureState.Shedding;
    }
}
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/FirecrackerStrategy.cs
```csharp
internal sealed class FirecrackerStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/KataContainersStrategy.cs
```csharp
internal sealed class KataContainersStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/ContainerdStrategy.cs
```csharp
internal sealed class ContainerdStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/RunscStrategy.cs
```csharp
internal sealed class RunscStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/PodmanStrategy.cs
```csharp
internal sealed class PodmanStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/YoukiStrategy.cs
```csharp
internal sealed class YoukiStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Container/GvisorStrategy.cs
```csharp
internal sealed class GvisorStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/WasmLanguageEcosystemStrategy.cs
```csharp
internal sealed class WasmLanguageEcosystemStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override ComputeCapabilities Capabilities;;
    internal record EcosystemReport(int TotalLanguages, int Tier1Count, int Tier2Count, int Tier3Count, IReadOnlyList<LanguageSummary> Languages);;
    internal record LanguageSummary(string Language, string Tier, WasiSupportLevel WasiSupport, PerformanceTier Performance, WasmBinarySize BinarySize, string CompileCommand, string Notes);;
    public EcosystemReport GetEcosystemReport();
    public string GenerateEcosystemSummary();
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/WasmLanguageBenchmark.cs
```csharp
internal sealed class WasmLanguageBenchmarkStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override ComputeCapabilities Capabilities;;
    internal record BenchmarkWorkload(string Name, string Description, byte[] WasmBytes, string ExpectedOutputPattern);;
    internal record BenchmarkResult(string Language, string Tier, TimeSpan ExecutionTime, long PeakMemoryBytes, long BinarySizeBytes, bool Success, string? Error);;
    public async Task<IReadOnlyList<BenchmarkResult>> RunBenchmarksAsync(IEnumerable<WasmLanguageStrategyBase> strategies, CancellationToken ct);
    public string GenerateBenchmarkReport(IReadOnlyList<BenchmarkResult> results);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/WasmLanguageSdkDocumentation.cs
```csharp
internal sealed class WasmLanguageSdkDocumentation
{
#endregion
}
    public static string GetBindingDocumentation(string language);
    public static string GetAllBindingDocumentation();
    public static IReadOnlyDictionary<string, string> GetHostFunctionDefinitions();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/WasmLanguageStrategyBase.cs
```csharp
internal abstract class WasmLanguageStrategyBase : ComputeRuntimeStrategyBase
{
}
    public override ComputeRuntime Runtime;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public abstract WasmLanguageInfo LanguageInfo { get; }
    public override ComputeCapabilities Capabilities;;
    protected abstract ReadOnlySpan<byte> GetSampleWasmBytes();;
    protected virtual string ExpectedSampleOutput;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
    public async Task<WasmLanguageVerificationResult> VerifyLanguageAsync(CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Sandbox/NsjailStrategy.cs
```csharp
internal sealed class NsjailStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Sandbox/SeccompStrategy.cs
```csharp
internal sealed class SeccompStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Sandbox/LandlockStrategy.cs
```csharp
internal sealed class LandlockStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Sandbox/SeLinuxStrategy.cs
```csharp
internal sealed class SeLinuxStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Sandbox/BubbleWrapStrategy.cs
```csharp
internal sealed class BubbleWrapStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Sandbox/AppArmorStrategy.cs
```csharp
internal sealed class AppArmorStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Gpu/VulkanComputeStrategy.cs
```csharp
internal sealed class VulkanComputeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Gpu/TensorRtStrategy.cs
```csharp
internal sealed class TensorRtStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Gpu/CudaStrategy.cs
```csharp
internal sealed class CudaStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Gpu/OneApiStrategy.cs
```csharp
internal sealed class OneApiStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Gpu/OpenClStrategy.cs
```csharp
internal sealed class OpenClStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Gpu/MetalStrategy.cs
```csharp
internal sealed class MetalStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Enclave/NitroEnclavesStrategy.cs
```csharp
internal sealed class NitroEnclavesStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Enclave/TrustZoneStrategy.cs
```csharp
internal sealed class TrustZoneStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Enclave/SgxStrategy.cs
```csharp
internal sealed class SgxStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Enclave/ConfidentialVmStrategy.cs
```csharp
internal sealed class ConfidentialVmStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Enclave/SevStrategy.cs
```csharp
internal sealed class SevStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/CoreRuntimes/DockerExecutionStrategy.cs
```csharp
internal sealed class DockerExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public DockerExecutionStrategy() : this(SharedHttpClient);
    public DockerExecutionStrategy(HttpClient httpClient);
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/CoreRuntimes/ProcessExecutionStrategy.cs
```csharp
internal sealed class ProcessExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```
```csharp
internal sealed class ServerlessExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```
```csharp
internal sealed class ScriptExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```
```csharp
internal sealed class BatchExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```
```csharp
internal sealed class ParallelExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```
```csharp
internal sealed class GpuComputeAbstractionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/FlinkStrategy.cs
```csharp
internal sealed class FlinkStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/RayStrategy.cs
```csharp
internal sealed class RayStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/SparkStrategy.cs
```csharp
internal sealed class SparkStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/DaskStrategy.cs
```csharp
internal sealed class DaskStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/BeamStrategy.cs
```csharp
internal sealed class BeamStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/MapReduceStrategy.cs
```csharp
internal sealed class MapReduceStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Distributed/PrestoTrinoStrategy.cs
```csharp
internal sealed class PrestoTrinoStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/SelfOptimizingPipelineStrategy.cs
```csharp
internal sealed class SelfOptimizingPipelineStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/ComputeCostPredictionStrategy.cs
```csharp
internal sealed class ComputeCostPredictionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/CarbonAwareComputeStrategy.cs
```csharp
internal sealed class CarbonAwareComputeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/DataGravitySchedulerStrategy.cs
```csharp
internal sealed class DataGravitySchedulerStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/IncrementalComputeStrategy.cs
```csharp
internal sealed class IncrementalComputeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```
```csharp
private sealed class CachedSegment(string output, DateTime lastAccessed)
{
}
    public string Output { get; };
    public DateTime LastAccessed { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/SpeculativeExecutionStrategy.cs
```csharp
internal sealed class SpeculativeExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/AdaptiveRuntimeSelectionStrategy.cs
```csharp
internal sealed class AdaptiveRuntimeSelectionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/IndustryFirst/HybridComputeStrategy.cs
```csharp
internal sealed class HybridComputeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken = default);
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasmInterpreterStrategy.cs
```csharp
internal sealed class WasmInterpreterStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override string Description;;
    public override bool IsProductionReady;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasmerStrategy.cs
```csharp
internal sealed class WasmerStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasmEdgeStrategy.cs
```csharp
internal sealed class WasmEdgeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasiNnStrategy.cs
```csharp
internal sealed class WasiNnStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasmtimeStrategy.cs
```csharp
internal sealed class WasmtimeStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WazeroStrategy.cs
```csharp
internal sealed class WazeroStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasiStrategy.cs
```csharp
internal sealed class WasiStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/Wasm/WasmComponentStrategy.cs
```csharp
internal sealed class WasmComponentStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task InitializeAsync(CancellationToken cancellationToken = default);
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/ScatterGather/ScatterGatherStrategy.cs
```csharp
internal sealed class ScatterGatherStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/ScatterGather/PartitionedQueryStrategy.cs
```csharp
internal sealed class PartitionedQueryStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/ScatterGather/PipelinedExecutionStrategy.cs
```csharp
internal sealed class PipelinedExecutionStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/ScatterGather/ParallelAggregationStrategy.cs
```csharp
internal sealed class ParallelAggregationStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/ScatterGather/ShuffleStrategy.cs
```csharp
internal sealed class ShuffleStrategy : ComputeRuntimeStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ComputeRuntime Runtime;;
    public override ComputeCapabilities Capabilities;;
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes;;
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/ZigWasmLanguageStrategy.cs
```csharp
internal sealed class ZigWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/CppWasmLanguageStrategy.cs
```csharp
internal sealed class CppWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/AssemblyScriptWasmLanguageStrategy.cs
```csharp
internal sealed class AssemblyScriptWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/GoWasmLanguageStrategy.cs
```csharp
internal sealed class GoWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/DotNetWasmLanguageStrategy.cs
```csharp
internal sealed class DotNetWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/RustWasmLanguageStrategy.cs
```csharp
internal sealed class RustWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier1/CWasmLanguageStrategy.cs
```csharp
internal sealed class CWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/GrainWasmLanguageStrategy.cs
```csharp
internal sealed class GrainWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/KotlinWasmLanguageStrategy.cs
```csharp
internal sealed class KotlinWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/OCamlWasmLanguageStrategy.cs
```csharp
internal sealed class OCamlWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/JavaWasmLanguageStrategy.cs
```csharp
internal sealed class JavaWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/MoonBitWasmLanguageStrategy.cs
```csharp
internal sealed class MoonBitWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/SwiftWasmLanguageStrategy.cs
```csharp
internal sealed class SwiftWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/TypeScriptWasmLanguageStrategy.cs
```csharp
internal sealed class TypeScriptWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/RubyWasmLanguageStrategy.cs
```csharp
internal sealed class RubyWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/HaskellWasmLanguageStrategy.cs
```csharp
internal sealed class HaskellWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/PythonWasmLanguageStrategy.cs
```csharp
internal sealed class PythonWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/DartWasmLanguageStrategy.cs
```csharp
internal sealed class DartWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/PhpWasmLanguageStrategy.cs
```csharp
internal sealed class PhpWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/JavaScriptWasmLanguageStrategy.cs
```csharp
internal sealed class JavaScriptWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier2/LuaWasmLanguageStrategy.cs
```csharp
internal sealed class LuaWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/RWasmLanguageStrategy.cs
```csharp
internal sealed class RWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/ElixirWasmLanguageStrategy.cs
```csharp
internal sealed class ElixirWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/PrologWasmLanguageStrategy.cs
```csharp
internal sealed class PrologWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/AdaWasmLanguageStrategy.cs
```csharp
internal sealed class AdaWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/PerlWasmLanguageStrategy.cs
```csharp
internal sealed class PerlWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/NimWasmLanguageStrategy.cs
```csharp
internal sealed class NimWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/VWasmLanguageStrategy.cs
```csharp
internal sealed class VWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/ScalaWasmLanguageStrategy.cs
```csharp
internal sealed class ScalaWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/CrystalWasmLanguageStrategy.cs
```csharp
internal sealed class CrystalWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateCompute/Strategies/WasmLanguages/Tier3/FortranWasmLanguageStrategy.cs
```csharp
internal sealed class FortranWasmLanguageStrategy : WasmLanguageStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override WasmLanguageInfo LanguageInfo;;
    protected override ReadOnlySpan<byte> GetSampleWasmBytes();;
}
```
