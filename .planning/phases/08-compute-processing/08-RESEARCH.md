# Phase 8: Compute & Processing - Research

**Researched:** 2026-02-11
**Domain:** Compute orchestration, storage processing, database protocol/storage, data lifecycle management
**Confidence:** HIGH

## Summary

Phase 8 covers five plugins spanning compute orchestration and data processing capabilities. Three of the five plugins (T102 UltimateDatabaseProtocol, T103 UltimateDatabaseStorage, T104 UltimateDataManagement) are **already marked complete** in TODO.md with 50, 45, and 92 strategies respectively, all with existing plugin directories and substantial implementation files. The remaining two plugins (T111 UltimateCompute, T112 UltimateStorageProcessing) are **not started** -- neither plugin directory exists yet.

The SDK foundation for all five plugins is complete. `IComputeRuntimeStrategy`, `ComputeCapabilities`, `ComputeTypes`, `IPipelineComputeStrategy`, and related types exist in `SDK/Contracts/Compute/`. `IStorageProcessingStrategy`, `StorageProcessingCapabilities`, and related types exist in `SDK/Contracts/StorageProcessing/`. Both include base classes with Intelligence integration hooks (`PipelineComputeStrategyBase`, `StorageProcessingStrategyBase`).

**Primary recommendation:** For the three "complete" plugins (T102, T103, T104), the plans should verify existing implementations are production-ready (no stubs, proper error handling, XML docs, Rule 13 compliance). For the two unstarted plugins (T111, T112), the plans must create the plugin projects from scratch following the established Ultimate plugin pattern: csproj referencing only SDK, orchestrator plugin extending `IntelligenceAwarePluginBase`, strategy registry with auto-discovery, and individual strategies extending appropriate base classes.

## Standard Stack

### Core (SDK Contracts -- Already Implemented)

| Library/Contract | Location | Purpose | Status |
|------------------|----------|---------|--------|
| `IComputeRuntimeStrategy` | SDK/Contracts/Compute/IComputeRuntimeStrategy.cs | Compute runtime abstraction | [x] Complete |
| `ComputeCapabilities` | SDK/Contracts/Compute/ComputeCapabilities.cs | Runtime capability declaration | [x] Complete |
| `ComputeTask` / `ComputeResult` | SDK/Contracts/Compute/ComputeTypes.cs | Task/result records | [x] Complete |
| `ComputeRuntime` enum | SDK/Contracts/Compute/ComputeTypes.cs | 15 runtime types (WASM, Python, JS, etc.) | [x] Complete |
| `ResourceLimits` | SDK/Contracts/Compute/ComputeTypes.cs | Resource constraint records | [x] Complete |
| `IPipelineComputeStrategy` | SDK/Contracts/Compute/PipelineComputeStrategy.cs | Adaptive pipeline compute | [x] Complete |
| `PipelineComputeStrategyBase` | SDK/Contracts/Compute/PipelineComputeStrategy.cs | Base class with Intelligence integration | [x] Complete |
| `IDeferredComputeQueue` | SDK/Contracts/Compute/PipelineComputeStrategy.cs | Background processing queue | [x] Complete |
| `IComputeCapacityMonitor` | SDK/Contracts/Compute/PipelineComputeStrategy.cs | Capacity monitoring interface | [x] Complete |
| `IStorageProcessingStrategy` | SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs | Storage-side processing | [x] Complete |
| `StorageProcessingStrategyBase` | SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs | Base with Intelligence hooks | [x] Complete |
| `StorageProcessingCapabilities` | SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs | Processing capability flags | [x] Complete |
| `IntelligenceAwarePluginBase` | SDK/Contracts/IntelligenceAware/ | Base class for Ultimate plugins | [x] Complete |

### Existing Plugins (Complete per TODO.md)

| Plugin | Directory | Strategies | Status |
|--------|-----------|------------|--------|
| UltimateDatabaseProtocol | `Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/` | 50 | [x] Complete |
| UltimateDatabaseStorage | `Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/` | 45 | [x] Complete |
| UltimateDataManagement | `Plugins/DataWarehouse.Plugins.UltimateDataManagement/` | 92 | [x] Complete |

### Plugins To Create (Not Started)

| Plugin | Directory | Expected Strategies | Status |
|--------|-----------|---------------------|--------|
| UltimateCompute | `Plugins/DataWarehouse.Plugins.UltimateCompute/` (does NOT exist) | ~60+ | [ ] Not Started |
| UltimateStorageProcessing | `Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/` (does NOT exist) | ~45+ | [ ] Not Started |

### NuGet Dependencies (Established Pattern)

The existing plugins reference only the SDK project. External NuGet packages are used for real client libraries (e.g., Npgsql, MongoDB.Driver, StackExchange.Redis, etc.). For T111 UltimateCompute and T112 UltimateStorageProcessing, no external NuGet packages should be needed initially since strategies integrate with external runtimes via process invocation, CLI wrappers, or SDK APIs rather than embedding the runtimes directly.

## Architecture Patterns

### Recommended Project Structure (for T111 and T112)

Based on the established pattern from T102/T103/T104:

```
Plugins/DataWarehouse.Plugins.UltimateCompute/
├── DataWarehouse.Plugins.UltimateCompute.csproj
├── UltimateComputePlugin.cs                    # Orchestrator (extends IntelligenceAwarePluginBase)
├── ComputeRuntimeStrategyBase.cs               # Base for all compute strategies (extends IComputeRuntimeStrategy)
├── ComputeRuntimeStrategyRegistry.cs           # Auto-discovery registry
├── Infrastructure/
│   ├── JobScheduler.cs                         # Job scheduling
│   └── DataLocalityPlacement.cs                # Data-locality aware placement
├── Strategies/
│   ├── Wasm/
│   │   ├── WasmtimeStrategy.cs
│   │   ├── WasmerStrategy.cs
│   │   ├── WazeroStrategy.cs
│   │   ├── WasmEdgeStrategy.cs
│   │   ├── WasiStrategy.cs
│   │   ├── WasiNnStrategy.cs
│   │   └── WasmComponentStrategy.cs
│   ├── Container/
│   │   ├── GvisorStrategy.cs
│   │   ├── FirecrackerStrategy.cs
│   │   ├── KataContainersStrategy.cs
│   │   ├── ContainerdStrategy.cs
│   │   ├── PodmanStrategy.cs
│   │   ├── RunscStrategy.cs
│   │   └── YoukiStrategy.cs
│   ├── Sandbox/
│   │   ├── SeccompStrategy.cs
│   │   ├── LandlockStrategy.cs
│   │   ├── AppArmorStrategy.cs
│   │   ├── SeLinuxStrategy.cs
│   │   ├── BubbleWrapStrategy.cs
│   │   └── NsjailStrategy.cs
│   ├── Enclave/
│   │   ├── SgxStrategy.cs
│   │   ├── SevStrategy.cs
│   │   ├── TrustZoneStrategy.cs
│   │   ├── NitroEnclavesStrategy.cs
│   │   └── ConfidentialVmStrategy.cs
│   ├── Distributed/
│   │   ├── MapReduceStrategy.cs
│   │   ├── SparkStrategy.cs
│   │   ├── FlinkStrategy.cs
│   │   ├── BeamStrategy.cs
│   │   ├── DaskStrategy.cs
│   │   ├── RayStrategy.cs
│   │   └── PrestoTrinoStrategy.cs
│   ├── ScatterGather/
│   │   ├── ScatterGatherStrategy.cs
│   │   ├── PartitionedQueryStrategy.cs
│   │   ├── ParallelAggregationStrategy.cs
│   │   ├── PipelinedExecutionStrategy.cs
│   │   └── ShuffleStrategy.cs
│   ├── Gpu/
│   │   ├── CudaStrategy.cs
│   │   ├── OpenClStrategy.cs
│   │   ├── MetalStrategy.cs
│   │   ├── VulkanComputeStrategy.cs
│   │   ├── OneApiStrategy.cs
│   │   └── TensorRtStrategy.cs
│   └── IndustryFirst/
│       ├── DataGravitySchedulerStrategy.cs
│       ├── ComputeCostPredictionStrategy.cs
│       ├── AdaptiveRuntimeSelectionStrategy.cs
│       ├── SpeculativeExecutionStrategy.cs
│       ├── IncrementalComputeStrategy.cs
│       ├── HybridComputeStrategy.cs
│       ├── SelfOptimizingPipelineStrategy.cs
│       └── CarbonAwareComputeStrategy.cs
```

```
Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/
├── DataWarehouse.Plugins.UltimateStorageProcessing.csproj
├── UltimateStorageProcessingPlugin.cs           # Orchestrator
├── StorageProcessingStrategyRegistryInternal.cs  # Auto-discovery registry
├── Infrastructure/
│   ├── ProcessingJobScheduler.cs
│   └── SharedCacheManager.cs
├── Strategies/
│   ├── Compression/
│   │   ├── OnStorageZstdStrategy.cs
│   │   ├── OnStorageLz4Strategy.cs
│   │   ├── OnStorageBrotliStrategy.cs
│   │   ├── OnStorageSnappyStrategy.cs
│   │   ├── TransparentCompressionStrategy.cs
│   │   └── ContentAwareCompressionStrategy.cs
│   ├── Build/
│   │   ├── DotNetBuildStrategy.cs
│   │   ├── TypeScriptBuildStrategy.cs
│   │   ├── RustBuildStrategy.cs
│   │   ├── GoBuildStrategy.cs
│   │   ├── DockerBuildStrategy.cs
│   │   ├── BazelBuildStrategy.cs
│   │   ├── GradleBuildStrategy.cs
│   │   ├── MavenBuildStrategy.cs
│   │   └── NpmBuildStrategy.cs
│   ├── Document/
│   │   ├── MarkdownRenderStrategy.cs
│   │   ├── LatexRenderStrategy.cs
│   │   ├── JupyterExecuteStrategy.cs
│   │   ├── SassCompileStrategy.cs
│   │   └── MinificationStrategy.cs
│   ├── Media/
│   │   ├── FfmpegTranscodeStrategy.cs
│   │   ├── ImageMagickStrategy.cs
│   │   ├── WebPConversionStrategy.cs
│   │   ├── AvifConversionStrategy.cs
│   │   ├── HlsPackagingStrategy.cs
│   │   └── DashPackagingStrategy.cs
│   ├── GameAsset/
│   │   ├── TextureCompressionStrategy.cs
│   │   ├── MeshOptimizationStrategy.cs
│   │   ├── AudioConversionStrategy.cs
│   │   ├── ShaderCompilationStrategy.cs
│   │   ├── AssetBundlingStrategy.cs
│   │   └── LodGenerationStrategy.cs
│   ├── Data/
│   │   ├── ParquetCompactionStrategy.cs
│   │   ├── IndexBuildingStrategy.cs
│   │   ├── VectorEmbeddingStrategy.cs
│   │   ├── DataValidationStrategy.cs
│   │   └── SchemaInferenceStrategy.cs
│   └── IndustryFirst/
│       ├── BuildCacheSharingStrategy.cs
│       ├── IncrementalProcessingStrategy.cs
│       ├── PredictiveProcessingStrategy.cs
│       ├── GpuAcceleratedProcessingStrategy.cs
│       ├── CostOptimizedProcessingStrategy.cs
│       └── DependencyAwareProcessingStrategy.cs
```

### Pattern 1: Ultimate Plugin Orchestrator

**What:** The main plugin class that auto-discovers and manages all strategies.
**When to use:** Every Ultimate plugin follows this pattern.
**Source:** Verified from `UltimateDatabaseProtocolPlugin.cs`

```csharp
public sealed class UltimateComputePlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly ComputeRuntimeStrategyRegistry _registry;
    private bool _initialized;

    public override string Id => "com.datawarehouse.compute.ultimate";
    public override string Name => "Ultimate Compute";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    public string SemanticDescription => "...";
    public string[] SemanticTags => ["compute", "runtime", "wasm", ...];

    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());
        // Configure Intelligence for each strategy
        _initialized = true;
    }
}
```

### Pattern 2: Strategy Registry with Auto-Discovery

**What:** A registry that scans assemblies for strategy implementations and registers them.
**When to use:** Every Ultimate plugin uses this for strategy management.
**Source:** Verified from `DatabaseProtocolStrategyRegistry`

```csharp
public sealed class ComputeRuntimeStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IComputeRuntimeStrategy> _strategies = new();

    public void DiscoverStrategies(params Assembly[] assemblies)
    {
        var strategyType = typeof(IComputeRuntimeStrategy);
        foreach (var assembly in assemblies)
        {
            var types = assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is IComputeRuntimeStrategy strategy)
                    _strategies[strategy.Runtime.ToString()] = strategy;
            }
        }
    }
}
```

### Pattern 3: Strategy Base Class with Intelligence Integration

**What:** Abstract base class for strategies that provides Intelligence hooks, knowledge registration, and capability metadata.
**When to use:** Every strategy should extend the appropriate base class.
**Source:** Verified from `PipelineComputeStrategyBase` and `StorageProcessingStrategyBase` in SDK

The SDK already provides:
- `PipelineComputeStrategyBase` for adaptive pipeline compute strategies (Phase E)
- `StorageProcessingStrategyBase` for storage processing strategies (T112)

For T111 core compute runtime strategies, a custom `ComputeRuntimeStrategyBase` needs to be created in the plugin project (like `DatabaseProtocolStrategyBase` was created in T102).

### Pattern 4: csproj Only References SDK

**What:** Plugin csproj references only the SDK project, plus any needed NuGet packages for real library integrations.
**Source:** Verified from all existing Ultimate plugin csproj files

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\DataWarehouse.SDK\DataWarehouse.SDK.csproj" />
  </ItemGroup>
</Project>
```

### Anti-Patterns to Avoid

- **Direct plugin references:** Plugins NEVER reference other plugins. Use message bus for inter-plugin communication.
- **Simulations/Stubs/Mocks:** Rule 13 prohibits any placeholder implementations. Every strategy must have real production logic.
- **Implementing interfaces directly:** Always extend the base class (`ComputeRuntimeStrategyBase`, `StorageProcessingStrategyBase`, etc.), not implement `IComputeRuntimeStrategy` directly.
- **Missing XML documentation:** All public APIs must have XML documentation comments.
- **Empty catch blocks:** All exception handling must be meaningful.
- **Console logging:** Use `ILogger` or `IMessageBus` for telemetry.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Plugin base class | Custom plugin infrastructure | `IntelligenceAwarePluginBase` from SDK | Handles Intelligence discovery, handshake, capabilities |
| Strategy discovery | Manual strategy registration | `Assembly.GetTypes()` scanning pattern from registry | Consistent with all other Ultimate plugins |
| Compute type system | Custom enums/records for tasks | `ComputeTask`, `ComputeResult`, `ComputeRuntime`, `ResourceLimits` from SDK | Already defined and used by other consumers |
| Pipeline compute | Custom adaptive routing | `IPipelineComputeStrategy`, `PipelineComputeStrategyBase`, `AdaptiveRouterConfig` from SDK | Complete adaptive routing framework already in SDK |
| Storage processing | Custom query/filter types | `ProcessingQuery`, `FilterExpression`, `AggregationResult` from SDK | Full query pushdown framework already in SDK |
| Intelligence integration | Direct AI service calls | `IMessageBus` + Intelligence topics from SDK | Required pattern for graceful degradation |
| Buffer management | Manual byte array allocation | `System.Buffers.ArrayPool<byte>.Shared` | Used throughout codebase for efficiency |

**Key insight:** The SDK provides extensive infrastructure for both compute and storage processing. The plugin's job is to provide concrete strategy implementations, not to reinvent the orchestration framework.

## Common Pitfalls

### Pitfall 1: Confusing "Complete" Status with "Verified"

**What goes wrong:** TODO.md marks T102, T103, T104 as complete, but strategies may contain placeholder logic or incomplete error handling.
**Why it happens:** Bulk implementation phases may have created files that compile but don't meet Rule 13 (production-ready only).
**How to avoid:** Each verification plan must spot-check strategies for: real library usage (not stub returns), proper error handling, XML docs, resource disposal, thread safety.
**Warning signs:** Methods that return `Task.CompletedTask` or `throw new NotImplementedException()`, methods under 10 lines that should be complex.

### Pitfall 2: T111 Has Two Distinct Strategy Interfaces

**What goes wrong:** Implementing T111 as a single strategy type when it actually has two: `IComputeRuntimeStrategy` (core compute) and `IPipelineComputeStrategy` (adaptive pipeline compute).
**Why it happens:** The TODO groups both under T111 (Phase B = core runtimes, Phase E = pipeline compute), but they serve different purposes.
**How to avoid:** The plugin should register both types of strategies. Phase B strategies implement `IComputeRuntimeStrategy`. Phase E strategies implement `IPipelineComputeStrategy` (extending `PipelineComputeStrategyBase`).
**Warning signs:** Only one strategy interface implemented, Phase E strategies missing.

### Pitfall 3: UltimateCompute Plugin Directory Does Not Exist

**What goes wrong:** Plans assume the directory exists and try to modify existing files.
**Why it happens:** T111 Phase B says `111.B1.1 Create DataWarehouse.Plugins.UltimateCompute project [ ]` -- the project has never been created.
**How to avoid:** Plan 08-01 must create the entire project from scratch: directory, csproj, orchestrator, registry, base class, and all strategies.
**Warning signs:** File read errors, missing directory errors.

### Pitfall 4: UltimateStorageProcessing Also Does Not Exist

**What goes wrong:** Same as Pitfall 3 but for T112.
**Why it happens:** `112.B1.1 Create DataWarehouse.Plugins.UltimateStorageProcessing project [ ]` -- never created.
**How to avoid:** Plan 08-02 must create the entire project from scratch.

### Pitfall 5: Scope Explosion on T111

**What goes wrong:** Trying to implement all 60+ strategies in T111 when the Phase E adaptive pipeline compute alone has 30+ sub-tasks.
**Why it happens:** T111 is the largest single task (~140 sub-tasks across Phases A-F plus hardware acceleration).
**How to avoid:** Focus on the core structure (Phase B1-B4 setup and representative strategies from each category) and the most critical strategies. Not every single strategy needs to be implemented in this phase -- prioritize the orchestrator infrastructure and key representative strategies.
**Warning signs:** Plans with 100+ individual strategy tasks.

### Pitfall 6: Not Adding to Solution File

**What goes wrong:** New plugin projects compile but aren't included in `DataWarehouse.slnx`.
**Why it happens:** Forgetting to register new projects in the solution file.
**How to avoid:** Each plan that creates a new project must include a step to add it to the slnx.
**Warning signs:** `dotnet build` doesn't include the new project.

### Pitfall 7: StorageProcessing SDK Interface Mismatch

**What goes wrong:** The TODO.md describes `IStorageProcessingStrategy` with `ProcessingId`, `ProcessingDomain`, `ProcessAsync(ProcessingJob)`, and `ValidateCacheAsync()`. But the actual SDK interface has `StrategyId`, `Name`, `ProcessAsync(ProcessingQuery)`, `QueryAsync()`, `AggregateAsync()`, etc.
**Why it happens:** The TODO was written before/separately from the SDK implementation. The SDK has a different (more query-oriented) design.
**How to avoid:** Always follow the **actual SDK interface** (`StorageProcessingStrategyBase` in `SDK/Contracts/StorageProcessing/`), not the TODO description. The TODO's `ProcessingDomain` enum and `ProcessingJob` types do not exist in the SDK.
**Warning signs:** Compile errors from implementing types that don't exist in SDK.

## Code Examples

### Example 1: Compute Runtime Strategy Implementation

Based on the `IComputeRuntimeStrategy` interface from SDK:

```csharp
// Source: SDK/Contracts/Compute/IComputeRuntimeStrategy.cs
public sealed class WasmtimeStrategy : IComputeRuntimeStrategy
{
    public ComputeRuntime Runtime => ComputeRuntime.WASM;

    public ComputeCapabilities Capabilities => ComputeCapabilities.CreateWasmDefaults();

    public IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    public async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Real Wasmtime CLI integration
            // ...
            return ComputeResult.CreateSuccess(task.Id, outputData, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return ComputeResult.CreateCancelled(task.Id, sw.Elapsed);
        }
        catch (TimeoutException)
        {
            return ComputeResult.CreateTimeout(task.Id, task.Timeout ?? Capabilities.MaxExecutionTime!.Value);
        }
        catch (Exception ex)
        {
            return ComputeResult.CreateFailure(task.Id, ex.Message, ex.ToString(), sw.Elapsed);
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Verify wasmtime CLI is available
        // ...
        return Task.CompletedTask;
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default)
    {
        // Cleanup any cached modules
        return Task.CompletedTask;
    }
}
```

### Example 2: Storage Processing Strategy Implementation

Based on the actual `StorageProcessingStrategyBase` from SDK:

```csharp
// Source: SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs
public sealed class ParquetCompactionStrategy : StorageProcessingStrategyBase
{
    public override string StrategyId => "parquet-compaction";
    public override string Name => "Parquet File Compaction";

    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true,
        SupportsProjection = true,
        SupportsAggregation = true,
        SupportsSorting = true,
        SupportsGrouping = true,
        SupportsLimiting = true,
        MaxQueryComplexity = 7,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "in" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average }
    };

    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        // Real Parquet compaction logic using Apache Arrow/Parquet libraries
        // ...
    }

    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(
        ProcessingQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        // Real streaming query against Parquet files
        // ...
    }

    public override async Task<AggregationResult> AggregateAsync(
        ProcessingQuery query,
        AggregationType aggregationType,
        CancellationToken ct = default)
    {
        ValidateQuery(query);
        ValidateAggregation(aggregationType);
        // Real aggregation on Parquet columnar data
        // ...
    }
}
```

### Example 3: Plugin csproj Template

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>
    <Description>Ultimate Compute Plugin - 60+ compute runtime strategies</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\DataWarehouse.SDK\DataWarehouse.SDK.csproj" />
  </ItemGroup>
</Project>
```

## State of the Art

| Area | Current State | Impact on Planning |
|------|---------------|-------------------|
| T102 (DB Protocol) | 50 strategies, all marked [x] | Verify only, no new implementation |
| T103 (DB Storage) | 45 strategies, all marked [x] | Verify only, no new implementation |
| T104 (Data Management) | 92 strategies, all marked [x] | Verify only, no new implementation |
| T111 (Compute) | SDK foundation complete, plugin NOT created | Full implementation required |
| T112 (Storage Processing) | SDK foundation complete, plugin NOT created | Full implementation required |
| .NET version | net10.0 | All new projects must target net10.0 |
| Build state | Compiles with 0 errors, 1141 warnings | Clean baseline for new work |

### Key SDK Interfaces Summary

**T111 uses two distinct interfaces:**

1. `IComputeRuntimeStrategy` -- Core compute: `ExecuteAsync(ComputeTask)`, `InitializeAsync()`, `DisposeAsync()`. Has `ComputeRuntime` enum, `ComputeCapabilities` record, `ResourceLimits` record.

2. `IPipelineComputeStrategy` -- Adaptive pipeline: `EstimateThroughputAsync()`, `ProcessAsync(Stream, PipelineComputeContext)`, `ProcessDeferredAsync()`. Has `PipelineComputeCapabilities`, `ThroughputMetrics`, `AdaptiveRouterConfig`, `ComputeOutputMode` enum.

**T112 uses one interface:**

`IStorageProcessingStrategy` -- Storage-side processing: `ProcessAsync(ProcessingQuery)`, `QueryAsync(ProcessingQuery)`, `AggregateAsync()`, `IsQuerySupported()`, `EstimateQueryCostAsync()`. Has `StorageProcessingCapabilities`, `ProcessingQuery`, `FilterExpression`, `AggregationType` enum.

## Task-Specific Findings

### T111: UltimateCompute -- Detailed Status

**Phase A (SDK Foundation):** COMPLETE. All interfaces, records, enums in `SDK/Contracts/Compute/`.

**Phase B (Plugin Implementation):** NOT STARTED.
- 111.B1.1-B1.4: Project setup, orchestrator, scheduler, placement -- all [ ]
- 111.B2.1-B2.7: WASM runtimes (7 strategies) -- all [ ]
- 111.B3.1-B3.7: Container runtimes (7 strategies) -- all [ ]
- 111.B4.1-B4.6: Native/sandboxed runtimes (6 strategies) -- all [ ]
- 111.B5.1-B5.5: Secure enclaves (5 strategies) -- all [ ]
- 111.B6.1-B6.7: MapReduce & batch (7 strategies) -- all [ ]
- 111.B7.1-B7.5: Scatter-gather (5 strategies) -- all [ ]
- 111.B8.1-B8.6: GPU/accelerator (6 strategies) -- all [ ]
- 111.B9.1-B9.8: Industry-first innovations (8 strategies) -- all [ ]

**Phase C (Advanced Features):** NOT STARTED (111.C1-C8, all [ ])

**Phase D (Migration & Cleanup):** NOT STARTED (111.D1-D5, all [ ])

**Phase E (Adaptive Pipeline Compute):** NOT STARTED.
- 111.E1.1-E1.6: Adaptive router (6 sub-tasks) -- all [ ]
- 111.E2.1-E2.8: Live compute strategies (8) -- all [ ]
- 111.E3.1-E3.5: Deferred compute queue (5) -- all [ ]
- 111.E4.1-E4.5: Output modes (5) -- all [ ]
- 111.E5.1-E5.10: Domain-specific pipelines (10) -- all [ ]

**Phase F (Industry-First Pipeline):** NOT STARTED (111.F1-F10, all [ ])

**WASI sub-tasks (from T90):** Marked as [x] -> T111, meaning the design was moved to T111 but actual implementation is pending within the UltimateCompute plugin.

**Total scope:** ~140 sub-tasks. This is by far the largest plugin in this phase.

### T112: UltimateStorageProcessing -- Detailed Status

**Phase A (SDK Foundation):** COMPLETE (112.A1-A6, all [x]).

**Phase B (Plugin Implementation):** NOT STARTED.
- 112.B1.1-B1.4: Project setup (4 sub-tasks) -- all [ ]
- 112.B2.1-B2.6: On-storage compression (6) -- all [ ]
- 112.B3.1-B3.9: Build & compilation (9) -- all [ ]
- 112.B4.1-B4.5: Code/document processing (5) -- all [ ]
- 112.B5.1-B5.6: Media transcoding (6) -- all [ ]
- 112.B6.1-B6.6: Game asset processing (6) -- all [ ]
- 112.B7.1-B7.5: Data processing (5) -- all [ ]
- 112.B8.1-B8.6: Industry-first (6) -- all [ ]

**Phase C-D:** NOT STARTED.

**CRITICAL NOTE on T112 SDK mismatch:** The TODO describes `IStorageProcessingStrategy` as having `ProcessingId`, `ProcessingDomain` enum (`Compression, Build, Media, Asset, Transform`), `ProcessAsync(ProcessingJob)`, and `ValidateCacheAsync()`. However, the actual SDK defines it differently -- as a query pushdown interface with `ProcessAsync(ProcessingQuery)`, `QueryAsync()`, `AggregateAsync()`, `IsQuerySupported()`, etc. The TODO's vision of "on-storage compression, build, render, media" strategies does NOT match the query-oriented SDK interface.

**Recommended resolution:** Create a separate `IOnStorageProcessingStrategy` interface within the plugin (or a `StorageProcessingJobStrategy` base) that matches the TODO's intent, while the SDK's `IStorageProcessingStrategy` is used for query pushdown. Alternatively, adapt the strategies to work within the SDK's query model. The planner needs to decide this.

### T102: UltimateDatabaseProtocol -- Verification Scope

**Status:** TODO.md says [x] Complete - 50 strategies

**Files found:**
- `UltimateDatabaseProtocolPlugin.cs` -- Orchestrator (verified, ~515 lines, production quality)
- `DatabaseProtocolStrategyBase.cs` -- Base class (verified, ~1240 lines, extensive)
- `Infrastructure/` -- Registry and infrastructure
- `Strategies/` -- 12 subdirectories with strategy files covering: CloudDW, Driver, Drivers (empty), Embedded, Graph, Messaging, NewSQL, NoSQL, Relational, Search, Specialized, TimeSeries

**NuGet dependencies:** Npgsql 10.0.1, MySqlConnector 2.4.1, Microsoft.Data.SqlClient 6.1.4, Oracle.ManagedDataAccess.Core 23.26.100, MongoDB.Driver 3.6.0, StackExchange.Redis 2.10.14, CassandraCSharpDriver 3.22.0, Elastic.Clients.Elasticsearch 9.3.0, Neo4j.Driver 5.30.0, InfluxDB.Client 5.0.0

**Verification focus:** Ensure all 50 strategies are present and implement real wire protocols (not stubs), handle connection lifecycle properly, and have XML docs.

### T103: UltimateDatabaseStorage -- Verification Scope

**Status:** TODO.md says [x] Complete - 45 strategies

**Files found:**
- `UltimateDatabaseStoragePlugin.cs` -- Orchestrator
- `DatabaseStorageStrategyBase.cs` -- Base class
- `DatabaseStorageStrategyRegistry.cs` -- Registry
- `Strategies/` -- 13 subdirectories: Analytics, CloudNative, Embedded, Graph, KeyValue, NewSQL, NoSQL, Relational, Search, Spatial, Streaming, TimeSeries, WideColumn

**NuGet dependencies:** Extensive -- covers Npgsql, MySqlConnector, SqlClient, Oracle, MongoDB, StackExchange.Redis, CassandraCSharpDriver, Neo4j.Driver, InfluxDB.Client, Elastic.Clients.Elasticsearch, DuckDB.NET.Data, Google.Cloud.Bigtable, Microsoft.Azure.Cosmos, AWSSDK.DynamoDBv2, and many more.

**Verification focus:** Ensure strategies use real client libraries (the NuGet packages are present), handle CRUD operations, and manage connections properly.

### T104: UltimateDataManagement -- Verification Scope

**Status:** TODO.md says [x] Complete - 92 strategies

**Files found:**
- `UltimateDataManagementPlugin.cs` -- Orchestrator
- `DataManagementStrategyBase.cs` -- Base class
- `FanOut/` -- Fan-out write orchestration
- `Strategies/` -- 11 subdirectories: AiEnhanced (9 files), Branching (2), Caching (8), Deduplication (11), EventSourcing (1), Indexing (10), Lifecycle (7), Retention (7+), Sharding (11), Tiering (11), Versioning (9)

**NuGet dependencies:** Only the SDK reference (no external packages). This makes sense as data management operations work on the warehouse's own storage abstraction.

**Verification focus:** Ensure all 92 strategies are present with real logic (especially AI-enhanced ones which require graceful degradation when Intelligence unavailable), fan-out orchestration is properly implemented, and caching strategies handle thread safety.

## Open Questions

1. **T112 SDK Mismatch Resolution**
   - What we know: The TODO describes T112 as "on-storage compression, build, render, media" but the SDK interface is query-oriented (filter, project, aggregate).
   - What's unclear: Whether the planner should create new interfaces within the plugin, or adapt the existing SDK interface.
   - Recommendation: Create a `StorageProcessingJobBase` class within the T112 plugin project that provides the job/processing model described in TODO while still implementing the SDK's `IStorageProcessingStrategy` interface where applicable. This keeps SDK compatibility while enabling the file-processing strategies.

2. **T111 Scope Management**
   - What we know: T111 has ~140 sub-tasks across 6 phases.
   - What's unclear: How many strategies should be implemented in this phase vs. deferred.
   - Recommendation: Implement all Phase B strategies (core infrastructure + all runtime categories). Phase C-F (advanced features, adaptive pipeline) can be deferred to a later phase if needed. The orchestrator infrastructure (B1) and representative strategies from each category (B2-B9) are mandatory.

3. **WASI Sub-tasks Ownership**
   - What we know: TODO.md shows `90.X1.1-X1.4` and `90.X3.1-X3.5` marked as `[x] -> T111`, meaning they were moved from T90 to T111.
   - What's unclear: Whether these specific implementations exist somewhere or need to be created fresh.
   - Recommendation: These should be implemented as part of T111's WASM and Container strategy categories (B2, B3).

## Sources

### Primary (HIGH confidence)
- `SDK/Contracts/Compute/IComputeRuntimeStrategy.cs` -- Compute runtime interface
- `SDK/Contracts/Compute/ComputeCapabilities.cs` -- Capability records
- `SDK/Contracts/Compute/ComputeTypes.cs` -- Task/result types, ComputeRuntime enum
- `SDK/Contracts/Compute/PipelineComputeStrategy.cs` -- Full adaptive pipeline framework
- `SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs` -- Storage processing interface + base
- `SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs` -- Plugin base class
- `Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/` -- Complete reference implementation
- `Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/` -- Complete reference implementation
- `Plugins/DataWarehouse.Plugins.UltimateDataManagement/` -- Complete reference implementation
- `Metadata/TODO.md` lines 10000-12000 -- Task definitions for T102-T104, T111-T112
- `Metadata/CLAUDE.md` -- Architecture rules and patterns

### Secondary (MEDIUM confidence)
- Directory listings confirming file existence/absence
- Build output confirming 0 errors

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All SDK contracts verified by reading actual source files
- Architecture: HIGH - Patterns verified from multiple existing implementations
- Pitfalls: HIGH - Based on direct observation of codebase state (missing directories, SDK mismatch)
- T102/T103/T104 status: MEDIUM - Marked complete but not individually verified for Rule 13 compliance
- T111/T112 scope: HIGH - Verified from TODO.md sub-task listings and confirmed directories don't exist

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (stable codebase, SDK contracts unlikely to change)
