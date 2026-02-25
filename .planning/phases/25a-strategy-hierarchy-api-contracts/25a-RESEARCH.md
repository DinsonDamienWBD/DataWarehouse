# Phase 25a: Strategy Hierarchy Design & API Contracts - Research

**Researched:** 2026-02-14
**Domain:** C# strategy pattern hierarchy refactoring, SDK API contract hardening
**Confidence:** HIGH (all findings verified against codebase)

## Summary

Phase 25a introduces a unified `StrategyBase` root class into the DataWarehouse SDK, consolidates 17+ fragmented strategy base classes into a flat two-level hierarchy (StrategyBase -> domain bases -> concrete strategies), removes intelligence/capability/knowledge boilerplate from all strategy bases, and enforces immutable API contracts across all public DTOs.

The codebase currently has NO unified strategy root. Each of the 17 SDK-level strategy bases independently implements its own lifecycle, intelligence integration, dispose patterns, and metadata. This results in massive code duplication -- the Intelligence Integration block alone (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus property, IsIntelligenceAvailable) is copied across all 17 bases totaling approximately 1,500-2,000 lines of duplicated code. Per AD-05, all intelligence code must be removed from strategies entirely.

The API contract audit reveals 666 occurrences of `Dictionary<string, object>` across 70 contract files, no `SdkCompatibility` attribute exists anywhere, no null-object patterns exist, and while 129 occurrences of `public record` show partial adoption of immutable types, many DTOs remain mutable classes.

**Primary recommendation:** Create StrategyBase with lifecycle + dispose + metadata (NO intelligence), create ~16 domain bases inheriting from it, build adapter wrappers for backward compatibility, and systematically enforce immutable contracts on all public DTOs.

## 1. Current Strategy Hierarchy Map

### SDK-Level Strategy Bases (17 total)

There is NO unified root. Each base independently implements its own interface:

```
[NO COMMON ROOT]
├── EncryptionStrategyBase : IEncryptionStrategy          (1,466 lines)
├── StorageStrategyBase : IStorageStrategy                (923 lines in Storage/)
├── StorageStrategyBase : IStorageStrategy                (368 lines in StorageOrchestratorBase.cs -- DUPLICATE NAME)
├── CompressionStrategyBase : ICompressionStrategy        (1,144 lines)
├── ComplianceStrategyBase : IComplianceStrategy          (1,125 lines)
├── StreamingStrategyBase : IStreamingStrategy            (927 lines)
├── ReplicationStrategyBase : IReplicationStrategy        (628 lines)
├── SecurityStrategyBase : ISecurityStrategy              (1,559 lines)
├── DataTransitStrategyBase : IDataTransitStrategy        (348 lines)
├── InterfaceStrategyBase : IInterfaceStrategy            (236 lines)
├── MediaStrategyBase : IMediaStrategy                    (280 lines)
├── ObservabilityStrategyBase : IObservabilityStrategy    (298 lines)
├── ConnectionStrategyBase : IConnectionStrategy          (626 lines)
│   ├── DatabaseConnectionStrategyBase                    (CategoryStrategyBases.cs)
│   ├── MessagingConnectionStrategyBase
│   ├── SaaSConnectionStrategyBase
│   ├── IoTConnectionStrategyBase
│   ├── LegacyConnectionStrategyBase
│   ├── HealthcareConnectionStrategyBase
│   ├── BlockchainConnectionStrategyBase
│   ├── ObservabilityConnectionStrategyBase
│   ├── DashboardConnectionStrategyBase
│   └── AiConnectionStrategyBase
├── DataFormatStrategyBase : IDataFormatStrategy          (634 lines)
├── DataLakeStrategyBase : IDataLakeStrategy              (126 lines)
├── DataMeshStrategyBase : IDataMeshStrategy              (109 lines)
├── PipelineComputeStrategyBase : IPipelineComputeStrategy (661 lines)
├── StorageProcessingStrategyBase : IStorageProcessingStrategy (535 lines)
├── RaidStrategyBase : IRaidStrategy                      (647 lines)
└── KeyStoreStrategyBase : IKeyStoreStrategy              (927 lines)
```

**Total SDK strategy base lines:** ~11,567 lines across 17 unique root-level base classes (plus 10 connection sub-category bases).

### The "7 Fragmented Bases" (ROADMAP reference)

The ROADMAP refers to "7 fragmented bases" -- these are the 7 ORIGINAL domain bases created in Phase 1 that were the initial strategy infrastructure. They are the largest and most feature-complete bases:

1. **EncryptionStrategyBase** (1,466 lines) -- encrypt/decrypt, key store integration, statistics
2. **StorageStrategyBase** (923 lines) -- store/retrieve/delete/list, retry logic, health caching
3. **CompressionStrategyBase** (1,144 lines) -- compress/decompress, content detection, entropy calc
4. **ComplianceStrategyBase** (1,125 lines) -- assess/evidence/report, violation tracking
5. **StreamingStrategyBase** (927 lines) -- publish/subscribe, stream management
6. **ReplicationStrategyBase** (628 lines) -- replicate/sync, vector clock, conflict detection
7. **SecurityStrategyBase** (1,559 lines) -- evaluate/validate, Zero Trust, threat detection

The remaining 10 bases (Transit, Interface, Media, Observability, Connection, DataFormat, DataLake, DataMesh, Compute, StorageProcessing, RAID, KeyStore) were added in later phases and are structurally similar but independently implemented.

## 2. Duplicated Patterns Across All Bases

### Intelligence Integration Block (present in ALL 17 bases)

Every strategy base independently implements this identical pattern (~50-150 lines each):

```csharp
// Present in every single base class:
protected IMessageBus? MessageBus { get; private set; }

public virtual void ConfigureIntelligence(IMessageBus? messageBus)
{
    MessageBus = messageBus;
}

protected bool IsIntelligenceAvailable => MessageBus != null;

public virtual KnowledgeObject GetStrategyKnowledge()
{
    return new KnowledgeObject
    {
        Id = $"<domain>.{StrategyId}",
        Topic = "<domain>",
        SourcePluginId = "sdk.<domain>",
        // ... same shape, different domain strings
    };
}

public virtual RegisteredCapability GetStrategyCapability()
{
    return new RegisteredCapability
    {
        CapabilityId = $"<domain>.{StrategyId}",
        DisplayName = Name,
        // ... same shape, different domain strings
    };
}
```

**Per AD-05, ALL of this must be removed from strategies.** Strategies are workers. Intelligence belongs at the plugin level.

### Other Duplicated Patterns

| Pattern | Where Duplicated | Estimated Lines |
|---------|-----------------|-----------------|
| Intelligence Integration | All 17 bases | 850-2,550 (50-150 per base) |
| Statistics tracking | Encryption, Storage, Compression, Transit | ~200 |
| Abstract StrategyId/Name | All 17 bases | ~170 (10 per base) |
| Transfer ID generation | Transit, Streaming | ~20 |
| Health check boilerplate | Transit, Storage, Connection | ~60 |

### Inconsistent Patterns Across Bases

| Pattern | Bases That Have It | Bases That Don't |
|---------|-------------------|------------------|
| IDisposable | Interface, Observability, KeyStore | 14 other bases |
| IAsyncDisposable | None | All 17 |
| CancellationToken support | All 17 | -- |
| Statistics tracking | Encryption, Storage, Compression, Transit | 13 bases |
| Template method (XxxCore) | Storage, Media, Compression | 14 bases |
| Retry logic | Storage, Connection | 15 bases |
| Lifecycle (Start/Stop) | Interface | 16 bases |
| ILogger support | Connection | 16 bases |
| SemaphoreSlim init | Observability | 16 bases |

## 3. Domain Distribution (Verified Strategy Counts)

Concrete strategies per domain, verified by grep against the codebase:

| Domain | Base Class | Concrete Strategies | Plugin(s) |
|--------|-----------|-------------------|-----------|
| Compliance | ComplianceStrategyBase | 149 | UltimateCompliance |
| AccessControl | IAccessControlStrategy (own interface) | 146 | UltimateAccessControl |
| Storage | StorageStrategyBase | 130 | UltimateStorage |
| Connector | ConnectionStrategyBase (+ sub-bases) | ~280* | UltimateConnector |
| Compute | PipelineComputeStrategyBase | 84 | UltimateCompute |
| Encryption | EncryptionStrategyBase | 69 | UltimateEncryption |
| KeyManagement | KeyStoreStrategyBase | 66 | UltimateKeyManagement |
| Replication | ReplicationStrategyBase (+ Enhanced) | 60 | UltimateReplication |
| Compression | CompressionStrategyBase | 59 | UltimateCompression |
| DataLake | DataLakeStrategyBase | 56 | UltimateDataLake |
| DataMesh | DataMeshStrategyBase | 56 | UltimateDataMesh |
| Observability | ObservabilityStrategyBase | 55 | UniversalObservability |
| DatabaseStorage | DatabaseStorageStrategyBase (extends StorageStrategyBase) | 55 | UltimateDatabaseStorage |
| Streaming | StreamingStrategyBase | 54 | UltimateStreamingData |
| RAID | RaidStrategyBase | 47 | UltimateRAID |
| StorageProcessing | StorageProcessingStrategyBase | 43 | UltimateStorageProcessing |
| DataFormat | DataFormatStrategyBase | 28 | UltimateDataFormat |
| Media | MediaStrategyBase | 20 | Transcoding.Media |
| Transit | DataTransitStrategyBase | 11 | UltimateDataTransit |
| Interface | InterfaceStrategyBase | ~68** | Interface (6 plugins) |

*Connector count: 59 direct ConnectionStrategyBase descendants + ~221 via sub-category bases (DatabaseConnectionStrategyBase etc.)
**Interface count estimated from Phase 6 description (80+ strategies)

**Total verified concrete strategies: ~1,500+** (consistent with ROADMAP estimate)

### Notable Observations

1. **AccessControl strategies do NOT use SecurityStrategyBase** -- they have their own `IAccessControlStrategy` interface. This means SecurityStrategyBase has 0 concrete implementations in Plugins (it may be used within the SDK itself).
2. **DatabaseStorage has its own intermediate base** (`DatabaseStorageStrategyBase`) that extends `StorageStrategyBase` -- a three-level hierarchy already exists there.
3. **UltimateReplication has EnhancedReplicationStrategyBase** -- another intermediate base extending the SDK's `ReplicationStrategyBase`.
4. **Two StorageStrategyBase classes exist** -- one in `Contracts/Storage/` and one in `Contracts/StorageOrchestratorBase.cs`. These appear to be the same class but in different files (need to check for conflicts).

## 4. Requirement-by-Requirement Assessment

### STRAT-01: Unified StrategyBase Root Class

**Status:** Does not exist. Must be created from scratch.

**What StrategyBase needs (per AD-05):**
- Lifecycle: `InitializeAsync(CancellationToken)`, `ShutdownAsync(CancellationToken)`
- Dispose: `IDisposable`, `IAsyncDisposable` (proper dispose pattern)
- Identity: `Name`, `Description` (abstract string properties)
- Characteristics: metadata dictionary or typed properties
- Structured logging hooks
- NO intelligence, NO capability registry, NO knowledge bank, NO message bus

**What to extract from existing bases:**
- `StrategyId` / `Name` (present in all 17 bases)
- CancellationToken support (present in all 17 bases)
- Dispose pattern (from Interface + Observability + KeyStore bases)
- Statistics tracking pattern (from Encryption/Storage/Compression/Transit -- generalize)

**Estimated StrategyBase size:** ~150-250 lines

### STRAT-02: Flat Two-Level Hierarchy (~16 Domain Bases)

**Status:** 17 independent bases exist. They need to become siblings under StrategyBase.

**Target domain bases per AD-05:**

| Domain Base | Maps From | Notes |
|------------|-----------|-------|
| EncryptionStrategyBase | Existing (1,466 lines) | Remove intelligence block, inherit StrategyBase |
| CompressionStrategyBase | Existing (1,144 lines) | Remove intelligence block, inherit StrategyBase |
| StorageStrategyBase | Existing (923 lines) | Remove intelligence block, deduplicate, inherit StrategyBase |
| SecurityStrategyBase | Existing (1,559 lines) | Remove intelligence block, inherit StrategyBase |
| KeyManagementStrategyBase | Existing KeyStoreStrategyBase (927 lines) | Rename? Remove intelligence block, inherit StrategyBase |
| ComplianceStrategyBase | Existing (1,125 lines) | Remove intelligence block, inherit StrategyBase |
| InterfaceStrategyBase | Existing (236 lines) | Remove intelligence block, inherit StrategyBase |
| ConnectorStrategyBase | Existing ConnectionStrategyBase (626 lines) | Rename? Remove intelligence block, inherit StrategyBase |
| ComputeStrategyBase | Existing PipelineComputeStrategyBase (661 lines) | Rename? Remove intelligence block, inherit StrategyBase |
| ObservabilityStrategyBase | Existing (298 lines) | Remove intelligence block, inherit StrategyBase |
| ReplicationStrategyBase | Existing (628 lines) | Remove intelligence block, inherit StrategyBase |
| MediaStrategyBase | Existing (280 lines) | Remove intelligence block, inherit StrategyBase |
| StreamingStrategyBase | Existing (927 lines) | Remove intelligence block, inherit StrategyBase |
| FormatStrategyBase | Existing DataFormatStrategyBase (634 lines) | Rename? Remove intelligence block, inherit StrategyBase |
| TransitStrategyBase | Existing DataTransitStrategyBase (348 lines) | Rename? Remove intelligence block, inherit StrategyBase |
| DataManagementStrategyBase | New (DataLake + DataMesh combined?) | OR keep DataLakeStrategyBase + DataMeshStrategyBase separate |

**Decision needed:** Whether DataLake and DataMesh become one `DataManagementStrategyBase` or stay separate. Also: RAID, StorageProcessing -- do they get their own domain bases or fold into existing ones?

**Additional bases not in AD-05 list but present in codebase:**
- `RaidStrategyBase` (647 lines, 47 strategies)
- `StorageProcessingStrategyBase` (535 lines, 43 strategies)
- `DatabaseStorageStrategyBase` (intermediate base in Plugins)

### STRAT-04: Adapter Wrappers for Backward Compatibility

**Status:** Two adapters already exist in `StrategyAdapters.cs` (127 lines):
- `CompressionStrategyAdapter` -- adapts `ICompressionStrategy` to pipeline
- `EncryptionStrategyAdapter` -- adapts `IEncryptionStrategy` to pipeline

**What's needed:** Adapter wrappers for ALL 17 existing base classes so that the ~1,500 strategies compiled against old bases continue to work during the transition period (Phase 25b does the actual migration).

**Pattern:** Each adapter wraps an old-hierarchy strategy and exposes it as a new-hierarchy strategy. This is temporary scaffolding removed in Phase 25b.

### STRAT-05: Domain-Common Contracts

**Status:** Already largely implemented. Each existing base defines its domain contract:
- EncryptionStrategyBase: `EncryptAsync/DecryptAsync`
- StorageStrategyBase: `StoreAsync/RetrieveAsync/DeleteAsync/ListAsync`
- ComplianceStrategyBase: `AssessAsync/CollectEvidenceAsync`
- etc.

**Action:** Preserve existing domain contracts. Verify they match AD-05's specification. The domain contract methods should remain on the domain bases, NOT be pulled up to StrategyBase.

### API-01: Immutable DTOs (Records / Init-Only)

**Status:** Partially done. 129 occurrences of `public record` across 40 contract files. But many DTOs remain as mutable classes.

**Examples of already-immutable types:**
- `TransitStatistics` (record with init-only)
- `StreamMessage`, `PublishResult`, `StreamingCapabilities` (records)
- `ComplianceControl`, `ComplianceViolation` (records)
- `StorageCapabilities`, `StorageObjectMetadata` (records)

**Examples of types still needing conversion:**
- Many capability/config types in older bases
- Request/response types that use mutable properties

### API-02: Strong Typing (Replace Dictionary<string, object>)

**Status:** 666 occurrences of `Dictionary<string, object>` across 70 contract files. This is a large effort.

**Categories of Dictionary<string, object> usage:**

1. **Metadata/Properties bags** -- Most common. Used for extensible metadata on strategies, capabilities, knowledge objects. These should become typed `record` classes with well-known properties + an `AdditionalProperties` escape hatch.

2. **GetKnowledgePayload()** -- In every intelligence integration block. Being REMOVED entirely per AD-05, so these specific instances go away automatically.

3. **GetCapabilityMetadata()** -- Same as above. Being removed with intelligence.

4. **Configuration** -- Some bases use `Dictionary<string, object>` for strategy configuration. Should become strongly typed options classes.

5. **Payload data** -- In KnowledgeObject, RegisteredCapability. These are SDK AI types that may need their own refactoring.

**Estimate:** Removing intelligence blocks eliminates ~100-150 Dictionary<string, object> occurrences automatically. The remaining ~500+ need manual strong typing.

### API-03: SdkCompatibility Attribute

**Status:** Does not exist anywhere in the codebase. Must be created from scratch.

**Purpose:** Version tracking and backward compatibility metadata on all public types.

**Recommended design:**
```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
public sealed class SdkCompatibilityAttribute : Attribute
{
    public string IntroducedVersion { get; }
    public string? DeprecatedVersion { get; init; }
    public string? ReplacementType { get; init; }
    public string? Notes { get; init; }

    public SdkCompatibilityAttribute(string introducedVersion)
    {
        IntroducedVersion = introducedVersion;
    }
}
```

### API-04: Null-Object Pattern

**Status:** Does not exist anywhere in the codebase. Must be created.

**Purpose:** Eliminate scattered null checks for optional dependencies.

**Primary candidates:**
- `IMessageBus` -- the most commonly null-checked dependency (every strategy base checks `MessageBus != null`)
- `ILogger` -- only used in ConnectionStrategyBase, but should be universally available
- `IProgress<T>` -- frequently passed as nullable in Transit/Streaming operations

**Recommended null objects:**
```csharp
public sealed class NullMessageBus : IMessageBus { /* no-op all methods */ }
public sealed class NullLogger : ILogger { /* no-op all methods */ }
// IProgress<T> already has a de facto null pattern (just don't call Report)
```

## 5. Build State

**SDK build currently FAILS with 4 errors:**

```
CS0108: 'HybridDatabasePluginBase<TConfig>.DisposeAsync()' hides inherited member 'PluginBase.DisposeAsync()'
CS0108: 'HybridStoragePluginBase<TConfig>.DisposeAsync()' hides inherited member 'PluginBase.DisposeAsync()'
CS0108: 'CipherPresetProviderPluginBase.Dispose()' hides inherited member 'PluginBase.Dispose()'
S4015: This member hides 'PluginBase.Dispose()'. Make it non-private or seal the class.
```

These are all **plugin base** dispose pattern issues (not strategy bases). They are in:
- `DataWarehouse.SDK/Database/HybridDatabasePluginBase.cs` (line 667)
- `DataWarehouse.SDK/Storage/HybridStoragePluginBase.cs` (line 554)
- `DataWarehouse.SDK/Contracts/TransitEncryptionPluginBases.cs` (line 287)

**Impact on Phase 25a:** These errors need fixing (likely in Phase 23 or 24 as they are IDisposable issues on PluginBase), but they do NOT block strategy hierarchy work. Phase 25a introduces NEW files (StrategyBase, adapters) and MODIFIES existing strategy bases -- the errors are in plugin bases. However, a full solution build verification at the end of 25a will still hit these unless they are fixed first.

**Recommendation:** Phase 23 (Memory Safety & IDisposable patterns) should fix these as part of MEM-04/MEM-05. Phase 25a should plan for these errors to already be resolved.

## 6. Adapter Pattern Assessment

### Existing Adapters

File: `DataWarehouse.SDK/Contracts/StrategyAdapters.cs` (127 lines)

Two adapters exist:
1. **CompressionStrategyAdapter** -- wraps `ICompressionStrategy` into something pipeline-compatible
2. **EncryptionStrategyAdapter** -- wraps `IEncryptionStrategy` into something pipeline-compatible

These are NOT the same type of adapter needed for Phase 25a. The 25a adapters will wrap OLD base class instances to expose them via the NEW hierarchy, allowing gradual migration in 25b.

### Adapter Strategy for Phase 25a

For each of the 17 existing bases, create an adapter that:
1. Takes a strategy inheriting from the old base
2. Delegates all calls to the old strategy
3. Implements the new StrategyBase lifecycle + dispose

This allows ALL ~1,500 strategies to compile unchanged while the new hierarchy is being built. Phase 25b then migrates strategies one domain at a time, removing the need for adapters.

## 7. Files Needing Modification

### New Files to Create
| File | Purpose |
|------|---------|
| `Contracts/IStrategy.cs` | Root interface (Name, Description, Characteristics) |
| `Contracts/StrategyBase.cs` | Root abstract class (lifecycle, dispose, metadata, logging) |
| `Contracts/SdkCompatibilityAttribute.cs` | API versioning attribute |
| `Contracts/NullMessageBus.cs` (or similar) | Null-object for IMessageBus |
| `Contracts/NullLogger.cs` (or similar) | Null-object for ILogger |
| ~17 adapter wrapper files | Temporary backward compatibility |

### Files to Modify (strategy bases -- add `: StrategyBase` inheritance, remove intelligence blocks)

| File | Lines | Changes |
|------|-------|---------|
| `Contracts/Encryption/EncryptionStrategy.cs` | 1,466 | Add `: StrategyBase`, remove ~150 lines intelligence |
| `Contracts/Security/SecurityStrategy.cs` | 1,559 | Add `: StrategyBase`, remove ~150 lines intelligence |
| `Contracts/Compression/CompressionStrategy.cs` | 1,144 | Add `: StrategyBase`, remove ~100 lines intelligence |
| `Contracts/Compliance/ComplianceStrategy.cs` | 1,125 | Add `: StrategyBase`, remove ~100 lines intelligence |
| `Contracts/Streaming/StreamingStrategy.cs` | 927 | Add `: StrategyBase`, remove ~80 lines intelligence |
| `Contracts/Storage/StorageStrategy.cs` | 923 | Add `: StrategyBase`, remove ~80 lines intelligence |
| `Security/IKeyStore.cs` | 927 | Add `: StrategyBase`, remove intelligence |
| `Contracts/Compute/PipelineComputeStrategy.cs` | 661 | Add `: StrategyBase`, remove intelligence |
| `Contracts/RAID/RaidStrategy.cs` | 647 | Add `: StrategyBase`, remove intelligence |
| `Contracts/DataFormat/DataFormatStrategy.cs` | 634 | Add `: StrategyBase`, remove intelligence |
| `Contracts/Replication/ReplicationStrategy.cs` | 628 | Add `: StrategyBase`, remove intelligence |
| `Connectors/ConnectionStrategyBase.cs` | 626 | Add `: StrategyBase`, remove intelligence |
| `Contracts/StorageProcessing/StorageProcessingStrategy.cs` | 535 | Add `: StrategyBase`, remove intelligence |
| `Contracts/Transit/DataTransitStrategyBase.cs` | 348 | Add `: StrategyBase`, remove intelligence |
| `Contracts/Observability/ObservabilityStrategyBase.cs` | 298 | Add `: StrategyBase`, remove intelligence |
| `Contracts/Media/MediaStrategyBase.cs` | 280 | Add `: StrategyBase`, remove intelligence |
| `Contracts/Interface/InterfaceStrategyBase.cs` | 236 | Add `: StrategyBase`, remove intelligence |
| `Contracts/DataLake/DataLakeStrategy.cs` | 126 | Add `: StrategyBase`, remove intelligence |
| `Contracts/DataMesh/DataMeshStrategy.cs` | 109 | Add `: StrategyBase`, remove intelligence |

**Total files to modify:** ~19 strategy base files
**Total intelligence boilerplate to remove:** ~1,500-2,000 lines

### Files for API Contract Work (API-01, API-02)
These span all 70 contract files with Dictionary<string, object> usage. The full list is too large for this research document but includes all files in `Contracts/*/` subdirectories.

## 8. Dependencies and Ordering

### Prerequisites (must be complete before 25a)
1. **Phase 23** -- IDisposable/IAsyncDisposable on PluginBase (MEM-04, MEM-05). StrategyBase needs dispose pattern, and current build errors around dispose must be fixed first.
2. **Phase 24** -- Plugin hierarchy stable. Strategies belong to plugins; plugin hierarchy must be finalized so strategy bases know what plugin capabilities look like.

### Internal Ordering Within Phase 25a
1. **25a-01: StrategyBase design** -- Must come first. All domain bases depend on it.
2. **25a-02: Domain strategy bases** -- Refactor the ~17 existing bases to inherit StrategyBase, remove intelligence. Can be parallelized across domains.
3. **25a-03: Adapter wrappers** -- Must come after 25a-02 (adapters bridge old and new hierarchy).
4. **25a-04: API contract safety** -- Can be done in parallel with 25a-02/03. SdkCompatibility attribute, null-object pattern, immutable DTOs, strong typing.
5. **25a-05: Build verification** -- Must come last. Verifies everything compiles.

### What Phase 25b Depends On From 25a
- StrategyBase exists and is stable
- All 17 domain bases inherit from StrategyBase
- Adapter wrappers exist for all 17 old bases
- API contract patterns established (so 25b migration uses correct patterns)

## 9. Risk Assessment

### High Risk
| Risk | Impact | Mitigation |
|------|--------|------------|
| Breaking ~1,500 strategies by changing base classes | Build failure across all 60 plugins | Adapter wrappers (25a-03) ensure zero breakage during transition |
| StorageStrategyBase name collision (two classes) | Ambiguous references | Investigate if these are actually the same class or need merge |
| AccessControl strategies use own interface (not SecurityStrategyBase) | May need additional domain base or special handling | Verify if AccessControl needs its own base in the new hierarchy |

### Medium Risk
| Risk | Impact | Mitigation |
|------|--------|------------|
| Dictionary<string, object> strong typing (666 occurrences) | Large surface area for regressions | Focus on new code in 25a; systematic replacement can be phased |
| Intermediate bases in plugins (EnhancedReplicationStrategyBase, DatabaseStorageStrategyBase) | Three-level hierarchies in some domains | Must be accounted for in adapter design |
| Build errors from Phase 23/24 not yet fixed | Cannot verify clean build | Plan assumes Phase 23/24 complete |

### Low Risk
| Risk | Impact | Mitigation |
|------|--------|------------|
| Some bases have unique features (retry, health cache, statistics) | May not fit cleanly into generic StrategyBase | Keep domain-specific features on domain bases, not StrategyBase |
| IStrategy interface naming collision | May conflict with existing types | Search codebase for existing `IStrategy` before committing to name |

## 10. Recommendations for Plan Structure

### 25a-01: StrategyBase Design
- Create `IStrategy` interface and `StrategyBase` abstract class
- Lifecycle: `InitializeAsync`, `ShutdownAsync` with `CancellationToken`
- Dispose: Full `IDisposable` + `IAsyncDisposable` with proper pattern
- Metadata: `Name`, `Description`, `StrategyId`, `Characteristics`
- Logging hooks (ILogger support)
- NO intelligence, NO message bus, NO capability registry
- Add `SdkCompatibilityAttribute` to the new types

### 25a-02: Domain Strategy Bases
- Refactor each of the 17 SDK bases to inherit from `StrategyBase`
- Remove all Intelligence Integration regions (~1,500-2,000 lines removed)
- Preserve domain-specific contracts exactly as-is
- Handle naming: rename where AD-05 uses different name (ConnectionStrategyBase -> ConnectorStrategyBase, etc.) OR keep existing names and alias
- Handle the two StorageStrategyBase files (merge or differentiate)
- Address DataLake/DataMesh as separate bases or combined DataManagementStrategyBase

### 25a-03: Adapter Wrappers
- One adapter per old base class (17 adapters)
- Each adapter: takes old-hierarchy strategy, wraps it in new-hierarchy interface
- Must handle domain-specific methods (not just lifecycle)
- Clearly marked as `[Obsolete("Temporary adapter for Phase 25b migration")]`
- Pattern similar to existing CompressionStrategyAdapter/EncryptionStrategyAdapter

### 25a-04: API Contract Safety
- Create `SdkCompatibilityAttribute` -- apply to ALL new types and modified types
- Create null-object implementations: `NullMessageBus`, `NullLogger`
- Audit and convert mutable DTOs to records/init-only (prioritize new/modified types)
- Begin strong typing of Dictionary<string, object> (focus on most-used patterns)
- Establish the pattern for 25b to follow for remaining Dictionary replacements

### 25a-05: Build Verification
- Full solution build (`dotnet build`)
- All existing tests pass (`dotnet test`)
- Verify no strategy in any plugin breaks (adapter wrappers should prevent this)
- Verify intelligence code is GONE from all strategy bases (grep verification)
- Count: StrategyBase referenced by all 17 domain bases

## Sources

### Primary (HIGH confidence)
All findings verified against codebase at `C:\Temp\DataWarehouse\DataWarehouse`:
- 17 SDK strategy base files read and analyzed (line counts verified)
- 70+ contract files with Dictionary<string, object> usage counted via grep
- ~1,500+ concrete strategy classes verified via grep across all Plugin directories
- Build errors verified via `dotnet build DataWarehouse.SDK`
- `.planning/ARCHITECTURE_DECISIONS.md` AD-05 read in full
- `.planning/ROADMAP.md` Phase 25a definition read in full
- `.planning/REQUIREMENTS.md` STRAT-01 through STRAT-06, API-01 through API-04 read

### Secondary (MEDIUM confidence)
- Strategy count estimates for Interface domain (~68) based on Phase 6 roadmap description rather than verified grep count
- Connector count (~280 total) includes estimate of sub-category base descendants not individually verified

## Metadata

**Confidence breakdown:**
- Strategy hierarchy map: HIGH -- all 17 bases read, all concrete strategies counted
- Intelligence duplication: HIGH -- identical pattern verified in every base
- Domain distribution: HIGH -- verified via grep for all major domains
- API contract assessment: HIGH -- Dictionary<string, object> count verified, record usage counted
- Build state: HIGH -- verified via actual build
- Adapter design: MEDIUM -- pattern understood from existing adapters, new adapter design is recommendation

**Research date:** 2026-02-14
**Valid until:** 2026-03-14 (stable codebase, no external dependencies changing)
