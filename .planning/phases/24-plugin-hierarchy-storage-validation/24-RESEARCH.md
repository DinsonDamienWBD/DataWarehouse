# Phase 24: Plugin Hierarchy, Storage Core & Input Validation - Research

**Researched:** 2026-02-14
**Domain:** C# class hierarchy refactoring, storage architecture, input validation
**Confidence:** HIGH (all findings from direct codebase analysis)

## Summary

Phase 24 restructures the plugin base class hierarchy into the target two-branch design (DataPipeline + Feature), unifies storage around an object/key model, extracts specialized bases into composable services, and hardens all SDK boundaries with input validation. This is the most architecturally impactful phase in v2.0 -- it touches the class hierarchy that all 60 plugins inherit from.

The current state is far more developed than a greenfield effort. PluginBase already has capability registry, knowledge registry, and lifecycle methods (OnHandshake, OnMessage). IntelligenceAwarePluginBase already provides AI socket with graceful degradation. The key gaps are: (1) `DataPipelinePluginBase` does not exist, (2) the current hierarchy puts IntelligenceAwarePluginBase under FeaturePluginBase (wrong per AD-01), (3) specialized storage bases are locked in inheritance chains, and (4) Regex timeout coverage is inconsistent.

**Primary recommendation:** Execute in strict dependency order: verify/fix PluginBase foundation first, then restructure the IntelligenceAwarePluginBase hierarchy (move it from inheriting FeaturePluginBase to inheriting PluginBase directly), create the two-branch siblings (DataPipelinePluginBase + FeaturePluginBase both under IntelligenceAwarePluginBase), then cascade domain bases, then storage unification, then composable services, then validation, then build verification.

**CRITICAL DEPENDENCY: Phase 23 must complete first.** The SDK currently does NOT build because PluginBase declares `IDisposable, IAsyncDisposable` but does not implement them (Phase 23 Plan 23-01 adds the implementations). Phase 24 cannot execute until this build error is resolved.

## Current Plugin Hierarchy Map

### PluginBase Direct Inheritance (all in SDK)

```
IPlugin (interface: Id, Name, Version, Category, OnHandshakeAsync, OnMessageAsync)
|
+-- PluginBase : IPlugin, IDisposable, IAsyncDisposable [3,855 lines]
    |   Provides: capability registry, knowledge registry, message bus, lifecycle hooks
    |   MISSING: Dispose(bool), DisposeAsync() implementations (Phase 23 will add)
    |   MISSING: explicit Initialize(), Execute(), Shutdown() with CancellationToken
    |   HAS: OnHandshakeAsync, OnMessageAsync, SetCapabilityRegistry, SetMessageBus, InjectKernelServices
    |
    +-- DataTransformationPluginBase : PluginBase, IDataTransformation
    |   |   Provides: OnWrite/OnRead stream transformation, SubCategory, QualityLevel
    |   |
    |   +-- PipelinePluginBase : DataTransformationPluginBase
    |       |   Provides: DefaultOrder, AllowBypass, RequiredPrecedingStages, IncompatibleStages
    |       |
    |       +-- EncryptionPluginBase : PipelinePluginBase, IDisposable
    |       |   Provides: key management, envelope encryption, per-user config
    |       |
    |       +-- [UltimateIntelligencePlugin : PipelinePluginBase] (PLUGIN, not base)
    |
    +-- StorageProviderPluginBase : PluginBase, IStorageProvider
    |   |   Provides: Scheme, SaveAsync(Uri), LoadAsync(Uri), DeleteAsync(Uri), ExistsAsync(Uri)
    |   |   NOTE: Uses Uri-based API (old model per AD-04)
    |   |
    |   +-- ListableStoragePluginBase : StorageProviderPluginBase, IListableStorage
    |   |   |
    |   |   +-- TieredStoragePluginBase : ListableStoragePluginBase, ITieredStorage
    |   |   |
    |   |   +-- CacheableStoragePluginBase : ListableStoragePluginBase, ICacheableStorage
    |   |       |   Provides: TTL cache, expiration, cache statistics
    |   |       |
    |   |       +-- IndexableStoragePluginBase : CacheableStoragePluginBase, IIndexableStorage
    |   |           |
    |   |           +-- HybridStoragePluginBase<TConfig> : IndexableStoragePluginBase, IAsyncDisposable [674 lines]
    |   |           |
    |   |           +-- HybridDatabasePluginBase<TConfig> : IndexableStoragePluginBase, IAsyncDisposable [829 lines]
    |   |
    |   +-- LowLatencyStoragePluginBase : StorageProviderPluginBase, ILowLatencyStorage, IIntelligenceAware
    |
    +-- MetadataIndexPluginBase : PluginBase, IMetadataIndex
    |
    +-- FeaturePluginBase : PluginBase, IFeaturePlugin  *** CURRENT LOCATION (wrong per AD-01) ***
    |   |   Provides: StartAsync(CancellationToken), StopAsync()
    |   |
    |   +-- InterfacePluginBase : FeaturePluginBase  (Protocol, Port, BasePath)
    |   +-- ConsensusPluginBase : FeaturePluginBase, IConsensusEngine
    |   +-- RealTimePluginBase : FeaturePluginBase, IRealTimeProvider
    |   +-- ReplicationPluginBase : FeaturePluginBase, IReplicationService
    |   +-- ContainerManagerPluginBase : FeaturePluginBase, IContainerManager
    |   +-- StoragePoolBase : FeaturePluginBase, IStoragePool
    |   +-- ~60+ more FeaturePluginBase derivatives (AEDS, Carbon, Compliance, Connector, etc.)
    |   |
    |   +-- IntelligenceAwarePluginBase : FeaturePluginBase, IIntelligenceAware [1,581 lines]
    |       |   *** CURRENTLY INHERITS FROM FeaturePluginBase -- must move to PluginBase per AD-01 ***
    |       |   Provides: AI socket, discovery, graceful degradation, capability caching
    |       |
    |       +-- IntelligenceAwareConnectorPluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareInterfacePluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareEncryptionPluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareCompressionPluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareStoragePluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareAccessControlPluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareCompliancePluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareDataManagementPluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareKeyManagementPluginBase : IntelligenceAwarePluginBase
    |       +-- IntelligenceAwareDatabasePluginBase : IntelligenceAwarePluginBase
    |
    +-- SecurityProviderPluginBase : PluginBase  (no IDisposable, no lifecycle)
    +-- OrchestrationProviderPluginBase : PluginBase
    +-- IntelligencePluginBase : PluginBase  (ProviderType, ModelId)
    +-- CloudEnvironmentPluginBase : PluginBase, ICloudEnvironment
    +-- SerializerPluginBase : PluginBase, ISerializer
    +-- SemanticMemoryPluginBase : PluginBase, ISemanticMemory
    +-- MetricsPluginBase : PluginBase, IMetricsProvider
    +-- GovernancePluginBase : PluginBase, INeuralSentinel
    +-- CipherPresetProviderPluginBase : PluginBase, IIntelligenceAware  (TransitEncryption)
    +-- TranscryptionPluginBase : PluginBase, IIntelligenceAware  (TransitEncryption)
    +-- PreOperationInterceptorBase : PluginBase, IPreOperationInterceptor
    +-- PostOperationInterceptorBase : PluginBase, IPostOperationInterceptor
```

### Actual Plugin Inheritance (60 Plugins)

| Plugin | Current Base | Target Base (AD-01) | Change Needed |
|--------|-------------|---------------------|---------------|
| UltimateIntelligencePlugin | PipelinePluginBase | PluginBase directly (HIER-07) | YES - change base |
| UltimateEncryptionPlugin | IntelligenceAwareEncryptionPluginBase | EncryptionPluginBase under DataPipeline | YES - reparent |
| UltimateCompressionPlugin | IntelligenceAwareCompressionPluginBase | CompressionPluginBase under DataPipeline | YES - reparent |
| UltimateStoragePlugin | IntelligenceAwareStoragePluginBase | StoragePluginBase under DataPipeline | YES - reparent |
| UltimateReplicationPlugin | IntelligenceAwarePluginBase | ReplicationPluginBase under DataPipeline | YES - new base |
| UltimateRaidPlugin | IntelligenceAwarePluginBase | ReplicationPluginBase under DataPipeline | YES - new base |
| UltimateDataTransitPlugin | FeaturePluginBase | DataTransitPluginBase under DataPipeline | YES - new base |
| TamperProofPlugin | PluginBase | IntegrityPluginBase under DataPipeline | YES - new base |
| UltimateAccessControlPlugin | IntelligenceAwareAccessControlPluginBase | AccessControlPluginBase under Feature/Security | OK (reparent) |
| UltimateKeyManagementPlugin | IntelligenceAwareKeyManagementPluginBase | KeyManagementPluginBase under Feature/Security | OK (reparent) |
| UltimateCompliancePlugin | IntelligenceAwareCompliancePluginBase | CompliancePluginBase under Feature/Security | OK (reparent) |
| UltimateInterfacePlugin | IntelligenceAwareInterfacePluginBase | InterfacePluginBase under Feature | OK (reparent) |
| UltimateConnectorPlugin | IntelligenceAwareConnectorPluginBase | ConnectorPluginBase under Feature | OK (reparent) |
| UltimateComputePlugin | IntelligenceAwarePluginBase | ComputePluginBase under Feature | YES - new base |
| UniversalObservabilityPlugin | IntelligenceAwarePluginBase | ObservabilityPluginBase under Feature | YES - new base |
| UltimateStreamingDataPlugin | IntelligenceAwarePluginBase | StreamingPluginBase under Feature | YES - new base |
| UltimateDataFormatPlugin | IntelligenceAwarePluginBase | FormatPluginBase under Feature | YES - new base |
| UltimateDataGovernancePlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDataCatalogPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDataQualityPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDataLineagePlugin | FeaturePluginBase | DataManagementPluginBase under Feature | YES - new base |
| UltimateDataLakePlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDataMeshPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDataPrivacyPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDataManagementPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase under Feature | OK (reparent) |
| UltimateDeploymentPlugin | IntelligenceAwarePluginBase | InfrastructurePluginBase under Feature | YES - new base |
| UltimateResiliencePlugin | IntelligenceAwarePluginBase | InfrastructurePluginBase under Feature | YES - new base |
| UltimateSustainabilityPlugin | IntelligenceAwarePluginBase | InfrastructurePluginBase under Feature | YES - new base |
| UltimateMultiCloudPlugin | IntelligenceAwarePluginBase | InfrastructurePluginBase under Feature | YES - new base |
| UltimateWorkflowPlugin | IntelligenceAwarePluginBase | OrchestrationPluginBase under Feature | YES - new base |
| UltimateEdgeComputingPlugin | PluginBase | OrchestrationPluginBase under Feature | YES - new base |
| UltimateSDKPortsPlugin | IntelligenceAwarePluginBase | PlatformPluginBase under Feature | YES - new base |
| UltimateMicroservicesPlugin | IntelligenceAwarePluginBase | PlatformPluginBase under Feature | YES - new base |
| UltimateDatabaseStoragePlugin | IntelligenceAwarePluginBase | StoragePluginBase under DataPipeline | YES - reparent |
| UltimateDatabaseProtocolPlugin | IntelligenceAwarePluginBase | InterfacePluginBase under Feature | YES - new base |
| UltimateStorageProcessingPlugin | IntelligenceAwarePluginBase | ComputePluginBase under Feature | YES - new base |
| UltimateDataProtectionPlugin | IntelligenceAwarePluginBase | SecurityPluginBase under Feature | YES - new base |
| UltimateDataIntegrationPlugin | IntelligenceAwarePluginBase | DataManagementPluginBase under Feature | YES - new base |
| UltimateDataFabricPlugin | IntelligenceAwarePluginBase | DataManagementPluginBase under Feature | YES - new base |
| UltimateDocGenPlugin | IntelligenceAwarePluginBase | FeaturePluginBase under Feature | OK (stays) |
| UltimateIoTIntegrationPlugin | IntelligenceAwarePluginBase | PlatformPluginBase under Feature | YES - new base |
| UltimateRTOSBridgePlugin | IntelligenceAwarePluginBase | PlatformPluginBase under Feature | YES - new base |
| UltimateServerlessPlugin | IntelligenceAwarePluginBase | ComputePluginBase under Feature | YES - new base |
| UltimateResourceManagerPlugin | FeaturePluginBase | InfrastructurePluginBase under Feature | YES - new base |
| UltimateFilesystemPlugin | FeaturePluginBase | StoragePluginBase under DataPipeline | YES - new base |
| UniversalDashboardsPlugin | IntelligenceAwarePluginBase | ObservabilityPluginBase under Feature | YES - new base |
| AppPlatformPlugin | IntelligenceAwarePluginBase | PlatformPluginBase under Feature | YES - new base |
| PluginMarketplacePlugin | FeaturePluginBase | PlatformPluginBase under Feature | YES - new base |
| AdaptiveTransportPlugin | FeaturePluginBase | DataTransitPluginBase under DataPipeline | YES - new base |
| AirGapBridgePlugin | IFeaturePlugin (DIRECT!) | DataTransitPluginBase under DataPipeline | YES - needs base |
| FuseDriverPlugin | FeaturePluginBase | StoragePluginBase under DataPipeline | YES - new base |
| WinFspDriverPlugin | FeaturePluginBase | StoragePluginBase under DataPipeline | YES - new base |
| KubernetesCsiPlugin | InterfacePluginBase | InterfacePluginBase under Feature | OK (reparent) |
| DataMarketplacePlugin | FeaturePluginBase | PlatformPluginBase under Feature | YES - new base |
| SelfEmulatingObjectsPlugin | FeaturePluginBase | StoragePluginBase under DataPipeline | YES - new base |
| WasmComputePlugin | WasmFunctionPluginBase | ComputePluginBase under Feature | YES - new base |
| SqlOverObjectPlugin | DataVirtualizationPluginBase | ComputePluginBase under Feature | YES - new base |
| MediaTranscodingPlugin | MediaTranscodingPluginBase | MediaPluginBase under Feature | OK (reparent) |
| RaftConsensusPlugin | ConsensusPluginBase | InfrastructurePluginBase under Feature | YES - new base |
| AEDS plugins (9) | FeaturePluginBase / specialized | FeaturePluginBase variants | Various |

**NOTE:** Most plugin re-basing is Phase 27 work, NOT Phase 24. Phase 24 creates the target hierarchy in the SDK. Phase 27 migrates plugins to it.

## Requirement-by-Requirement Assessment

### HIER-01: PluginBase IDisposable/IAsyncDisposable
**Status:** PARTIALLY DONE (declaration exists, implementation pending Phase 23)
- `PluginBase` class signature already includes `: IPlugin, IDisposable, IAsyncDisposable`
- `Dispose(bool)` and `DisposeAsync()` implementations are Phase 23-01's deliverable
- Phase 24 can verify this is done but should NOT implement it (Phase 23 dependency)
- **Action:** Verify Phase 23-01 completed; no Phase 24 work needed

### HIER-02: PluginBase Lifecycle (Initialize/Execute/Shutdown with CancellationToken)
**Status:** PARTIALLY EXISTS
- `OnHandshakeAsync(HandshakeRequest)` serves as initialization
- `OnMessageAsync(PluginMessage)` serves as message handling
- `InjectKernelServices(messageBus, capabilityRegistry, knowledgeLake)` exists
- **MISSING:** Explicit `InitializeAsync(CancellationToken)`, `ExecuteAsync(CancellationToken)`, `ShutdownAsync(CancellationToken)` methods
- FeaturePluginBase has `StartAsync(CancellationToken)` and `StopAsync()` (no CancellationToken on Stop)
- **Action:** Add explicit lifecycle methods to PluginBase; these should call the existing plumbing

### HIER-03: PluginBase Capability Registry
**Status:** FULLY EXISTS
- `CapabilityRegistry` property (IPluginCapabilityRegistry?)
- `SetCapabilityRegistry()`, `RegisterCapabilitiesAsync()`, `GetCapabilityRegistrations()`
- `DeclaredCapabilities`, `GetCapabilities()`
- Cleanup in `UnregisterKnowledgeAsync()` via `CapabilityRegistry.UnregisterPluginAsync(Id)`
- **Action:** Verify completeness, possibly add runtime deregister method

### HIER-04: PluginBase Knowledge Registry
**Status:** FULLY EXISTS
- `_knowledgeCache` (ConcurrentDictionary)
- `RegisterKnowledgeAsync()`, `HandleKnowledgeQueryAsync()`, `SubscribeToKnowledgeRequests()`
- `GetRegistrationKnowledge()`, `GetStaticKnowledge()`, `HandleDynamicKnowledgeQueryAsync()`
- `CacheKnowledge()`, `GetCachedKnowledge()`, `ClearKnowledgeCache()`
- KnowledgeLake integration via `RegisterStaticKnowledgeAsync()`
- **Action:** Verify completeness, no new work expected

### HIER-05: IntelligenceAwarePluginBase extends PluginBase
**Status:** WRONG INHERITANCE
- **CURRENT:** `IntelligenceAwarePluginBase : FeaturePluginBase, IIntelligenceAware`
- **TARGET (AD-01):** `IntelligenceAwarePluginBase : PluginBase, IIntelligenceAware`
- This is the MOST CRITICAL change in Phase 24
- IntelligenceAwarePluginBase uses `StartAsync()` from FeaturePluginBase for discovery
- Must extract/replicate that lifecycle hook when moving to PluginBase
- **Risk:** HIGH -- 40+ plugins transitively inherit from IntelligenceAwarePluginBase
- **Action:** Change inheritance, move StartAsync logic, ensure all plugins still compile

### HIER-06: Feature-specific bases inherit from IntelligenceAwarePluginBase
**Status:** PARTIALLY EXISTS
- `IntelligenceAwareEncryptionPluginBase`, `IntelligenceAwareCompressionPluginBase`, etc. already exist
- They already inherit from IntelligenceAwarePluginBase
- BUT: These are "Intelligence-specialized" bases, not the target "domain" bases from AD-01
- AD-01 wants `EncryptionPluginBase` under `DataTransformationPluginBase` under `DataPipelinePluginBase`
- **Action:** Create the target domain bases; existing IntelligenceAware* bases may need to be reconciled or replaced

### HIER-07: UltimateIntelligencePlugin inherits PluginBase directly
**Status:** WRONG -- currently inherits PipelinePluginBase
- `UltimateIntelligencePlugin : PipelinePluginBase` (which is DataTransformationPluginBase : PluginBase)
- **TARGET:** `UltimateIntelligencePlugin : PluginBase` directly
- **Action:** Change base class; may lose SubCategory/QualityLevel/Pipeline metadata (must verify what's used)

### HIER-08: Feature-specific bases implement common domain functionality
**Status:** PARTIALLY EXISTS
- `IntelligenceAwareEncryptionPluginBase` has AI-driven algorithm selection (419 lines)
- `IntelligenceAwareStoragePluginBase` has AI-driven storage optimization (280 lines)
- `IntelligenceAwareAccessControlPluginBase` has AI-driven access evaluation (269 lines)
- Each already provides significant domain-specific AI hooks
- **Action:** Restructure into target domain bases, preserve all domain functionality

### HIER-09: All 60 plugins compile after changes
**Status:** NOT YET (prerequisite)
- SDK currently does NOT build (IDisposable not implemented -- Phase 23 dependency)
- Once Phase 23 completes, need to verify all 60 plugins build
- **Action:** Build verification at every step of hierarchy changes

### VALID-01: Every public SDK method validates inputs
**Status:** FRAMEWORK EXISTS, COVERAGE INCOMPLETE
- `IInputValidator`, `ValidationRules`, `SecurityRules` exist in `DataWarehouse.SDK/Validation/InputValidation.cs`
- `ValidationMiddleware` exists with pluggable filters
- `Required()`, `StringLength()`, `Pattern()`, `Range<T>()`, `SafeString()`, `SafePath()` rules exist
- 414 `ArgumentNullException/ArgumentException` occurrences across 79 SDK files
- **Gap:** Many public methods likely lack validation; systematic audit needed
- **Action:** Audit all public SDK methods, add Guards/validation at entry points

### VALID-02: Path traversal protection
**Status:** WELL IMPLEMENTED
- `SecurityRules.ContainsPathTraversal()` with compiled regex + 100ms timeout
- `PathTraversalFilter` in ValidationMiddleware
- `ErrorHandling.ValidatePath()` with `Path.GetFullPath()` normalization
- Multiple encoding bypass detection (URL encoded, overlong UTF-8)
- **Action:** Ensure all file/URI operations use these helpers; may need a wrapper method

### VALID-03: Configurable size limits
**Status:** PARTIALLY EXISTS
- `InputValidatorConfig.MaxStringLength = 10000` exists
- `StringLengthRule` exists
- **Gap:** No configurable size limits on messages, knowledge objects, capability payloads at SDK level
- **Action:** Add `SizeLimitOptions` to SDK configuration; enforce in message bus and registries

### VALID-04: Regex bounded timeouts (ReDoS)
**Status:** MOSTLY DONE, GAPS EXIST
- `SecurityRules` in InputValidation.cs: ALL 6 regex patterns have `TimeSpan.FromMilliseconds(100)` -- GOOD
- `ErrorHandling.cs`: `new Regex(pattern, regexOptions, TimeSpan.FromMilliseconds(500))` -- GOOD
- `InputValidation.cs` PatternRule: `new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100))` -- GOOD
- **GAP:** `ValidationMiddleware.cs` PathTraversalFilter: `new Regex(..., RegexOptions.Compiled)` -- NO TIMEOUT
- **GAP:** `SemanticAnalyzerBase.cs`: 16+ `new Regex()` without timeout, 16+ `Regex.IsMatch/Match/Split` static calls without timeout
- **GAP:** `SqlSecurity.cs`: 7 `Regex.IsMatch()` static calls without timeout
- **GAP:** `SmartFolderService.cs`: 1 `Regex.IsMatch()` without timeout
- **GAP:** `IntelligenceInterfaceService.cs`: 2 `Regex.Match()` without timeout
- **GAP:** `SecretManager.cs`: 1 `Regex.Match()` without timeout
- Total: ~20 `new Regex()` instances in SDK, ~34 `Regex.Static*()` calls -- roughly half lack timeouts
- **Action:** Add timeouts to all remaining regex operations; consider `GeneratedRegex` for .NET 8+ source generators

### VALID-05: Plugin identity verification via cryptographic keys
**Status:** NOT IMPLEMENTED
- No existing code for plugin cryptographic identity
- No signing infrastructure for plugin verification
- **Action:** Design and implement plugin identity verification (asymmetric key pair, signature verification on plugin load)

## Storage Architecture Analysis

### Current Dual Model (AD-04 Problem)

**Model A: Uri-based (IStorageProvider)**
```csharp
// In ProviderInterfaces.cs
interface IStorageProvider : IPlugin {
    string Scheme { get; }
    Task SaveAsync(Uri uri, Stream data);
    Task<Stream> LoadAsync(Uri uri);
    Task DeleteAsync(Uri uri);
    Task<bool> ExistsAsync(Uri uri);
}
```
Used by: `StorageProviderPluginBase`, `ListableStoragePluginBase`, `TieredStoragePluginBase`, `CacheableStoragePluginBase`, `IndexableStoragePluginBase`, kernel.

**Model B: Key-based (IStorageStrategy)**
```csharp
// In Storage/StorageStrategy.cs
interface IStorageStrategy {
    Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct);
    Task<Stream> RetrieveAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
    IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, CancellationToken ct);
    Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct);
    Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct);
    Task<long?> GetAvailableCapacityAsync(CancellationToken ct);
}
```
Used by: `UltimateStoragePlugin` (130+ strategies), all advanced features (replication, RAID, tiering, AEDS).

### StorageObjectMetadata (already exists!)
```csharp
public record StorageObjectMetadata {
    string Key { get; init; }
    long Size { get; init; }
    DateTime Created { get; init; }
    DateTime Modified { get; init; }
    string? ETag { get; init; }
    string? ContentType { get; init; }
    IReadOnlyDictionary<string, string>? CustomMetadata { get; init; }
    StorageTier? Tier { get; init; }
    string? VersionId { get; init; }
    // ... more fields
}
```

### PathStorageAdapter (does NOT exist)
- No `PathStorageAdapter` class found in the codebase
- **Action:** Create `PathStorageAdapter` that wraps `IStorageStrategy` and exposes `IStorageProvider` (Uri-based) API
- This allows the kernel and legacy code to continue using Uri-based access while the core is key-based

### Specialized Storage Bases to Extract (AD-03)

| Base Class | Lines | Logic to Extract | Service Name |
|-----------|-------|------------------|-------------|
| TieredStoragePluginBase | ~30 | Tier movement, tier detection | ITierManager |
| CacheableStoragePluginBase | ~240 | TTL cache, expiration, stats | ICacheManager |
| IndexableStoragePluginBase | ~90 | Metadata indexing, search | IStorageIndex |
| HybridStoragePluginBase<T> | 674 | Multi-instance management, connection registry | IConnectionRegistry |
| HybridDatabasePluginBase<T> | 829 | Full storage+indexing+caching | IConnectionRegistry (same pattern) |
| StoragePoolBase | ~550 | Multi-provider orchestration | IStoragePool (already an interface!) |
| IndexingStorageOrchestratorBase | ~140 | Hybrid storage config | Part of IStoragePool |

**TieredStorageManager already exists** in `Infrastructure/PerformanceOptimizations.cs` (1,566 lines) with full tiering logic, statistics, and metrics. This can be composed directly.

## Files Needing Modification

### Phase 24 Core Files (SDK only -- plugin changes are Phase 27)

**Must Modify:**
1. `DataWarehouse.SDK/Contracts/PluginBase.cs` -- Add lifecycle methods (HIER-02), verify capability/knowledge registries
2. `DataWarehouse.SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs` -- Change base from FeaturePluginBase to PluginBase (HIER-05)
3. `DataWarehouse.SDK/Contracts/IntelligenceAware/SpecializedIntelligenceAwareBases.cs` -- Reorganize specialized bases

**Must Create:**
4. `DataWarehouse.SDK/Contracts/DataPipelinePluginBase.cs` -- New base for data-flow plugins
5. `DataWarehouse.SDK/Contracts/FeaturePluginBase.cs` -- Extract from PluginBase.cs, reparent under IntelligenceAwarePluginBase
6. ~15-20 domain plugin base files under appropriate branch
7. `DataWarehouse.SDK/Storage/PathStorageAdapter.cs` -- URI translation layer (AD-04)
8. Composable service interfaces: `ITierManager`, `ICacheManager`, `IStorageIndex`, `IConnectionRegistry`
9. Composable service implementations extracted from specialized bases
10. `DataWarehouse.SDK/Validation/Guards.cs` -- Centralized guard clauses for VALID-01
11. Plugin identity infrastructure for VALID-05

**Must Audit:**
12. All `new Regex()` in SDK for timeout compliance (VALID-04)
13. All public SDK methods for input validation (VALID-01)

## Dependencies and Ordering

### External Dependencies (BLOCKING)

| Dependency | From Phase | What Phase 24 Needs | Status |
|-----------|-----------|---------------------|--------|
| IDisposable on PluginBase | Phase 23-01 | Dispose(bool), DisposeAsync() implementations | NOT STARTED |
| SDK builds with zero errors | Phase 22/23 | Cannot modify hierarchy if SDK doesn't build | BLOCKED (IDisposable missing) |

### Internal Ordering (within Phase 24)

```
24-01: Verify PluginBase foundation (HIER-01 verify, HIER-02 add, HIER-03 verify, HIER-04 verify)
  |
24-02: Restructure IntelligenceAwarePluginBase hierarchy (HIER-05)
  |    Move IntelligenceAwarePluginBase from FeaturePluginBase -> PluginBase
  |    Create DataPipelinePluginBase + FeaturePluginBase as siblings under IntelligenceAwarePluginBase
  |    Fix UltimateIntelligencePlugin to inherit PluginBase directly (HIER-07)
  |
24-03: Create domain plugin bases under correct branch (HIER-06, HIER-08)
  |    DataPipeline branch: DataTransformation, Encryption, Compression, Storage, Replication, Transit, Integrity
  |    Feature branch: Security, Interface, DataManagement, Compute, Observability, Streaming, Media, Format, Infrastructure, Orchestration, Platform
  |
24-04: Object storage core + PathStorageAdapter (AD-04)
  |    Ensure new StoragePluginBase under DataPipeline uses object/key model
  |    Create PathStorageAdapter for URI compatibility
  |
24-05: Extract composable services from specialized bases (AD-03)
  |    ITierManager, ICacheManager, IStorageIndex, IConnectionRegistry
  |    Wire into new StoragePluginBase
  |
24-06: Input validation framework (VALID-01 through VALID-05)
  |    Guard clauses for public methods
  |    Regex timeout audit
  |    Size limit configuration
  |    Plugin identity verification
  |
24-07: Build verification across all 60 plugins (HIER-09)
       Full build, existing tests
```

## Risk Assessment

### HIGH Risk

1. **IntelligenceAwarePluginBase reparenting (HIER-05)** -- This class is the most-inherited base in the codebase (40+ direct/transitive inheritors). Moving it from `FeaturePluginBase` to `PluginBase` removes `StartAsync(CancellationToken)` and `StopAsync()`. All 40+ plugins that call these lifecycle methods will break. Mitigation: IntelligenceAwarePluginBase must re-declare `StartAsync`/`StopAsync` (which it already overrides anyway), or the target `FeaturePluginBase` (new one under IntelligenceAwarePluginBase) must provide them.

2. **FeaturePluginBase circular re-definition** -- Currently `FeaturePluginBase : PluginBase` exists at PluginBase.cs:1206. AD-01 wants `FeaturePluginBase : IntelligenceAwarePluginBase`. This means the EXISTING FeaturePluginBase must be renamed/removed and a NEW one created. But ~60 FeaturePluginBase derivatives exist. Must update them all or use a temporary alias. Mitigation: Create new hierarchy in parallel, use `[Obsolete]` on old bases, update in Phase 27.

3. **Build stability** -- SDK already doesn't build (Phase 23 dependency). If Phase 23 is not completed cleanly, Phase 24 cannot start. Mitigation: Strict dependency enforcement.

### MEDIUM Risk

4. **StorageProviderPluginBase refactoring** -- The existing Uri-based storage bases (`StorageProviderPluginBase` -> `ListableStoragePluginBase` -> `TieredStoragePluginBase` -> `CacheableStoragePluginBase` -> `IndexableStoragePluginBase`) form a deep inheritance chain used by HybridStoragePluginBase and HybridDatabasePluginBase. Refactoring this into composable services while maintaining backward compatibility is complex. Mitigation: Keep old bases temporarily, create new composable service layer in parallel, migrate in Phase 27.

5. **Domain base proliferation** -- AD-01 targets ~15-20 domain bases. Many don't exist yet. Creating them all at once risks over-engineering. Mitigation: Only create bases that have at least one plugin to inherit from them.

### LOW Risk

6. **Input validation (VALID-01 through VALID-04)** -- Framework already exists. Adding guard clauses to public methods is mechanical work. Regex timeout additions are straightforward.

7. **Plugin identity (VALID-05)** -- New feature with no existing code to break. Risk is only in design decisions.

## Recommendations for Plan Structure

### Plan 24-01: PluginBase Foundation Verification
- Verify Phase 23 delivered IDisposable/IAsyncDisposable on PluginBase (HIER-01)
- Add explicit lifecycle methods: `InitializeAsync(CancellationToken)`, `ExecuteAsync(CancellationToken)`, `ShutdownAsync(CancellationToken)` (HIER-02)
- Verify capability registry completeness (HIER-03)
- Verify knowledge registry completeness (HIER-04)
- Build and test

### Plan 24-02: IntelligenceAwarePluginBase Hierarchy Restructure
- **CRITICAL:** Move IntelligenceAwarePluginBase from `FeaturePluginBase` -> `PluginBase` (HIER-05)
- Re-declare `StartAsync`/`StopAsync` on IntelligenceAwarePluginBase (it already overrides them)
- Create NEW `DataPipelinePluginBase : IntelligenceAwarePluginBase` with pipeline semantics
- Create NEW `FeaturePluginBase : IntelligenceAwarePluginBase` with service semantics
- Rename OLD `FeaturePluginBase` to `LegacyFeaturePluginBase` with `[Obsolete]` (backward compat for Phase 27)
- Fix `UltimateIntelligencePlugin` to inherit `PluginBase` directly (HIER-07)
- Build and test

### Plan 24-03: Domain Plugin Bases
- Create domain bases under DataPipelinePluginBase: DataTransformationPluginBase, EncryptionPluginBase (new), CompressionPluginBase (new), StoragePluginBase (new), ReplicationPluginBase, DataTransitPluginBase, IntegrityPluginBase
- Create domain bases under FeaturePluginBase: SecurityPluginBase (with AccessControl, KeyManagement, Compliance sub-bases), InterfacePluginBase, DataManagementPluginBase, ComputePluginBase, ObservabilityPluginBase, StreamingPluginBase, MediaPluginBase, FormatPluginBase, InfrastructurePluginBase, OrchestrationPluginBase, PlatformPluginBase
- Each domain base implements common functionality (HIER-08)
- Keep existing IntelligenceAware* bases as adapters with `[Obsolete]` (removed in Phase 27)
- Build and test

### Plan 24-04: Object Storage Core + PathStorageAdapter
- Ensure new `StoragePluginBase` uses key-based `IStorageStrategy` as its core model
- Create `PathStorageAdapter` that wraps `IStorageStrategy` and exposes `IStorageProvider` (Uri-based) API
- Update SDK kernel references to use `PathStorageAdapter` when Uri-based access is needed
- StorageObjectMetadata already exists -- verify it has all needed fields
- Build and test

### Plan 24-05: Composable Services Extraction
- Extract `ITierManager` from TieredStoragePluginBase (leverage existing `TieredStorageManager`)
- Extract `ICacheManager` from CacheableStoragePluginBase
- Extract `IStorageIndex` from IndexableStoragePluginBase
- Extract `IConnectionRegistry` from HybridStoragePluginBase
- Wire services into new `StoragePluginBase` as optional injectable services
- Mark old specialized storage bases `[Obsolete]` (removed in Phase 28)
- Build and test

### Plan 24-06: Input Validation Framework
- Add `Guards` static class with common guard methods for all public SDK methods (VALID-01)
- Audit and add `TimeSpan.FromMilliseconds(100)` to all remaining `new Regex()` without timeout (VALID-04)
- Audit and add timeout to all `Regex.Static*()` calls or convert to pre-compiled instances (VALID-04)
- Add `SizeLimitOptions` configuration for messages, knowledge objects, capability payloads (VALID-03)
- Verify path traversal protection coverage (VALID-02) -- likely already sufficient
- Design plugin identity verification with asymmetric key pairs (VALID-05)
- Build and test

### Plan 24-07: Hierarchy Build Verification
- Full solution build (`dotnet build`)
- Run all existing tests (`dotnet test`)
- Verify all 60 plugins still compile (HIER-09)
- Verify no regression (AD-08)
- Document any plugins that need attention in Phase 27

## Code Examples

### Current IntelligenceAwarePluginBase Declaration (to be changed)
```csharp
// Source: DataWarehouse.SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs:56
public abstract class IntelligenceAwarePluginBase : FeaturePluginBase, IIntelligenceAware, IIntelligenceAwareNotifiable
```

### Current FeaturePluginBase (to be reparented)
```csharp
// Source: DataWarehouse.SDK/Contracts/PluginBase.cs:1206
public abstract class FeaturePluginBase : PluginBase, IFeaturePlugin
{
    public abstract Task StartAsync(CancellationToken ct);
    public abstract Task StopAsync();
}
```

### Target Hierarchy (AD-01)
```csharp
// PluginBase stays as-is (with IDisposable from Phase 23)
public abstract class PluginBase : IPlugin, IDisposable, IAsyncDisposable { ... }

// Intelligence moves to inherit PluginBase directly
public abstract class IntelligenceAwarePluginBase : PluginBase, IIntelligenceAware { ... }

// Two sibling branches
public abstract class DataPipelinePluginBase : IntelligenceAwarePluginBase { ... }
public abstract class FeaturePluginBase : IntelligenceAwarePluginBase { ... }

// UltimateIntelligence IS the intelligence -- no IntelligenceAware needed
public sealed class UltimateIntelligencePlugin : PluginBase { ... }
```

### StorageObjectMetadata (already exists)
```csharp
// Source: DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs:264
public record StorageObjectMetadata
{
    public string Key { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime Created { get; init; }
    public DateTime Modified { get; init; }
    public string? ETag { get; init; }
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, string>? CustomMetadata { get; init; }
    public StorageTier? Tier { get; init; }
    public string? VersionId { get; init; }
}
```

### Existing Validation Infrastructure
```csharp
// Source: DataWarehouse.SDK/Validation/InputValidation.cs
// SecurityRules with timeouts -- GOOD PATTERN
private static readonly Regex SqlInjectionPattern = new(
    @"...",
    RegexOptions.IgnoreCase | RegexOptions.Compiled,
    TimeSpan.FromMilliseconds(100));  // ReDoS protection

// GAP: SemanticAnalyzerBase.cs has 16+ regex without timeout
["EMAIL"] = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b",
    RegexOptions.Compiled),  // MISSING: TimeSpan timeout
```

## Open Questions

1. **Lifecycle method naming and backward compatibility**
   - What we know: PluginBase has OnHandshakeAsync but no explicit Initialize/Execute/Shutdown
   - What's unclear: Should Initialize call OnHandshakeAsync internally, or replace it?
   - Recommendation: Add new methods that wrap existing ones; mark old ones `[Obsolete]`

2. **Old FeaturePluginBase migration strategy**
   - What we know: ~60+ classes inherit from FeaturePluginBase (PluginBase:1206)
   - What's unclear: Should we rename to `LegacyFeaturePluginBase` or use a type alias?
   - Recommendation: Rename to `LegacyFeaturePluginBase`, mark `[Obsolete]`, create new `FeaturePluginBase : IntelligenceAwarePluginBase` in a separate file. Plugins migrate in Phase 27.

3. **Domain base granularity**
   - What we know: AD-01 shows ~20 domain bases
   - What's unclear: Some bases (MediaPluginBase, StreamingPluginBase) may be too thin to justify
   - Recommendation: Create all bases per AD-01 target hierarchy even if thin -- consistency matters more than code volume

4. **IntelligenceAware* specialized bases disposition**
   - What we know: 10 specialized IntelligenceAware* bases exist with substantial domain logic
   - What's unclear: Do the new domain bases replace them, wrap them, or inherit from them?
   - Recommendation: The new domain bases (e.g., `EncryptionPluginBase`) should incorporate the AI hooks from their IntelligenceAware* counterparts. Mark IntelligenceAware* as `[Obsolete]`.

## Sources

### Primary (HIGH confidence -- direct codebase analysis)
- `DataWarehouse.SDK/Contracts/PluginBase.cs` -- 3,855 lines, full hierarchy analysis
- `DataWarehouse.SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs` -- 1,581 lines
- `DataWarehouse.SDK/Contracts/IntelligenceAware/SpecializedIntelligenceAwareBases.cs` -- 4,158 lines
- `DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs` -- StorageObjectMetadata definition
- `DataWarehouse.SDK/Validation/InputValidation.cs` -- validation framework and security rules
- `DataWarehouse.SDK/Validation/ValidationMiddleware.cs` -- pluggable validation filters
- `.planning/ARCHITECTURE_DECISIONS.md` -- AD-01 through AD-08 (authoritative design)
- `.planning/REQUIREMENTS.md` -- HIER-01 through HIER-09, VALID-01 through VALID-05
- All 60 plugin files in `Plugins/` directory -- actual inheritance mapping

### Secondary (HIGH confidence -- project state)
- `.planning/STATE.md` -- Phase 22 complete, Phase 23 planned but not executed
- `.planning/phases/23-memory-safety-crypto-hygiene/23-01-PLAN.md` -- IDisposable plan details
- `dotnet build` output -- confirms SDK does not build (IDisposable missing)

## Metadata

**Confidence breakdown:**
- Current hierarchy map: HIGH -- direct grep/read of all files
- Requirement assessment: HIGH -- each verified against codebase
- Storage architecture: HIGH -- dual model verified in source
- Validation state: HIGH -- line-by-line regex audit completed
- Target architecture: HIGH -- defined in ARCHITECTURE_DECISIONS.md (user-approved)
- Risk assessment: HIGH -- based on actual inheritance counts and file analysis

**Research date:** 2026-02-14
**Valid until:** Until Phase 23 completes (expected within days); re-verify IDisposable state before starting Phase 24
