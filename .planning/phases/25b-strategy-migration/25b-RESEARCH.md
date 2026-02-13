# Phase 25b: Strategy Migration - Research

**Researched:** 2026-02-14
**Domain:** Mechanical strategy class migration, intelligence boilerplate removal, behavioral equivalence verification
**Confidence:** HIGH (all findings verified against codebase)

## Summary

Phase 25b is the mechanical migration phase that follows Phase 25a (strategy hierarchy design). After 25a completes, the SDK will have a unified `StrategyBase` root class with ~17 domain strategy bases inheriting from it, intelligence code removed from SDK bases, and backward-compatibility shims on StrategyBase allowing all ~1,500+ strategies to compile unchanged.

Phase 25b's job is to:
1. Migrate plugin-local strategy bases (AccessControl, Compute, DataManagement, DataProtection, Streaming, RAID's plugin base) to inherit from the new SDK hierarchy
2. Remove intelligence boilerplate from those plugin-local bases
3. Handle the ~55 strategy files that actively USE MessageBus/IsIntelligenceAvailable (concentrated in Interface and DataProtection domains)
4. Remove the backward-compatibility shims from StrategyBase
5. Verify compilation and behavioral equivalence across all 60 plugins

A critical finding is that the ROADMAP strategy count estimates are significantly wrong for several domains. Encryption has 69 strategies (not 12), Streaming has 58 (not 17), and DataProtection has 82 (not 35). The total verified count across the named domains is approximately 1,734 concrete strategy classes, not the estimated ~1,500.

Another critical finding: the vast majority of concrete strategies (>95%) have ZERO intelligence boilerplate to remove. Only 1 strategy overrides ConfigureIntelligence, 0 override GetStrategyKnowledge/GetStrategyCapability, and only ~55 strategy files use MessageBus directly (45 in Interface, 9 in DataProtection, 1 in DataManagement). The migration is therefore primarily a base-class-chain verification exercise for most strategies, with real work concentrated on plugin-local base classes and the ~55 strategies with active MessageBus usage.

**Primary recommendation:** Regroup plans around actual complexity, not just strategy count. Interface (73 strategies, 45 use MessageBus) is harder than Connector (280 strategies, 0 use MessageBus). Plugin-local base migrations (AccessControl, Compute, DataManagement, DataProtection) require more care than SDK-base strategies that "just work" after 25a.

## 1. Verified Strategy Counts Per Domain

### SDK-Base-Inheriting Strategies (inherit directly from SDK Contracts bases)

| Domain | Plugin | SDK Base | Strategies | Files | ROADMAP Est. | Delta |
|--------|--------|----------|-----------|-------|-------------|-------|
| Transit | UltimateDataTransit | DataTransitStrategyBase | 11 | 11 | 11 | 0 |
| Encryption | UltimateEncryption | EncryptionStrategyBase | 69 | 19 | 12 | **+57** |
| Media | Transcoding.Media | MediaStrategyBase | 20 | 20 | 20 | 0 |
| DataFormat | UltimateDataFormat | DataFormatStrategyBase | 28 | 28 | 28 | 0 |
| StorageProcessing | UltimateStorageProcessing | StorageProcessingStrategyBase | 43 | 43 | 43 | 0 |
| DatabaseStorage | UltimateDatabaseStorage | DatabaseStorageStrategyBase : StorageStrategyBase | 49 | 49 | 49 | 0 |
| Observability | UniversalObservability | ObservabilityStrategyBase | 55 | 55 | 55 | 0 |
| Compression | UltimateCompression | CompressionStrategyBase | 59 | 59 | 59 | 0 |
| Replication | UltimateReplication | ReplicationStrategyBase + EnhancedReplicationStrategyBase | 61 | 15 | 60 | +1 |
| Interface | UltimateInterface | InterfaceStrategyBase | 73 | 68 | 68 | +5 |
| KeyManagement | UltimateKeyManagement | KeyStoreStrategyBase (+ Pkcs11HsmStrategyBase) | 69 | 69 | 68 | +1 |
| DataLake | UltimateDataLake | DataLakeStrategyBase | 56 | 56 | -- | N/A |
| DataMesh | UltimateDataMesh | DataMeshStrategyBase | 56 | 56 | -- | N/A |
| Connector | UltimateConnector | ConnectionStrategyBase + 10 sub-category bases | 280 | 293 | 280 | 0 |
| Storage | UltimateStorage | StorageStrategyBase (via UltimateStorageStrategyBase) | 130 | 130 | 130 | 0 |
| RAID | UltimateRAID | SDK RaidStrategyBase (aliased as SdkRaidStrategyBase) | 47 | 47 | -- | N/A |
| **Subtotal** | | | **1,106** | | | |

### Plugin-Local Base Strategies (inherit from plugin-defined bases, NOT SDK bases)

| Domain | Plugin | Plugin Base | Inherits SDK? | Strategies | ROADMAP Est. | Delta |
|--------|--------|------------|---------------|-----------|-------------|-------|
| AccessControl | UltimateAccessControl | AccessControlStrategyBase : IAccessControlStrategy | NO | 146 | 142 | +4 |
| Compliance | UltimateCompliance | ComplianceStrategyBase : IComplianceStrategy | NO (plugin) | 149 | 145 | +4 |
| Compute | UltimateCompute | ComputeRuntimeStrategyBase : IComputeRuntimeStrategy | NO | 85 | 83 | +2 |
| DataManagement | UltimateDataManagement | DataManagementStrategyBase + 10 sub-bases | NO | 101 | 78 | **+23** |
| DataProtection | UltimateDataProtection | DataProtectionStrategyBase : IDataProtectionStrategy | NO | 82 | 35 | **+47** |
| Streaming | UltimateStreamingData | StreamingDataStrategyBase : IStreamingDataStrategy | NO | 58 | 17 | **+41** |
| **Subtotal** | | | | **621** | | |

**NOTE:** The UltimateCompliance plugin has its OWN `ComplianceStrategyBase` class (in plugin), distinct from the SDK's `ComplianceStrategyBase`. These are TWO DIFFERENT classes with the same name. The plugin one inherits `IComplianceStrategy` directly; the SDK one inherits `IComplianceStrategy` through the SDK Contracts. This name collision must be handled carefully during migration.

### Total Phase 25b Scope

| Category | Count |
|----------|-------|
| SDK-base-inheriting strategies | 1,106 |
| Plugin-local base strategies | 621 |
| **Total** | **~1,727** |

The ROADMAP estimate of ~1,500 is an undercount by ~227. Major discrepancies: Encryption (69 vs 12), Streaming (58 vs 17), DataProtection (82 vs 35), DataManagement (101 vs 78).

### Multi-Class Files

Several domains pack multiple strategy classes per file:
- **Encryption**: 19 files containing 69 classes (avg 3.6 classes/file)
- **Replication**: 15 files containing 61 classes (avg 4.1 classes/file)
- **RAID**: strategy files contain 4+ classes each

This means file count does not equal class count. Migration tools/scripts must handle multiple classes per file.

## 2. The 7+10 Fragmented Bases Analysis

### Original 7 SDK Bases (Phase 1 era, largest and most feature-complete)

| Base | File | Lines | Intelligence Code | Domain-Specific Helpers | Dispose? |
|------|------|-------|-------------------|------------------------|----------|
| EncryptionStrategyBase | Contracts/Encryption/EncryptionStrategy.cs | 1,466 | ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable, RequestKeyRecommendationAsync | EncryptCoreAsync/DecryptCoreAsync template, CombineIvAndCiphertext, GenerateIv, statistics | No |
| CompressionStrategyBase | Contracts/Compression/CompressionStrategy.cs | 1,144 | Same 5 + RequestOptimalAlgorithmAsync | CompressCoreAsync/DecompressCoreAsync template, content type detection, entropy calculation | No |
| StorageStrategyBase | Contracts/Storage/StorageStrategy.cs | 923 | Same 5 + RequestTierRecommendationAsync | StoreCoreAsync/RetrieveCoreAsync/etc template, retry logic, health caching | No |
| SecurityStrategyBase | Contracts/Security/SecurityStrategy.cs | 1,559 | Same 5 + RequestSecurityOptimizationAsync | Zero Trust evaluation, threat detection, domain evaluation | No |
| ComplianceStrategyBase | Contracts/Compliance/ComplianceStrategy.cs | 1,125 | Same 5 + RequestComplianceRecommendationAsync | AssessCoreAsync template, evidence collection, violation tracking | No |
| StreamingStrategyBase | Contracts/Streaming/StreamingStrategy.cs | 927 | Same 5 + RequestStreamingOptimizationAsync | Publish/subscribe, stream management | No |
| ReplicationStrategyBase | Contracts/Replication/ReplicationStrategy.cs | 628 | Same 5 + RequestReplicationRecommendationAsync | Vector clock, conflict detection, replication sync | No |

### Additional 10 SDK Bases (later phases)

| Base | File | Lines | Intelligence Code | Dispose? |
|------|------|-------|-------------------|----------|
| DataTransitStrategyBase | Contracts/Transit/DataTransitStrategyBase.cs | 348 | Same 5 | No |
| InterfaceStrategyBase | Contracts/Interface/InterfaceStrategyBase.cs | 236 | Same 5 | Yes (IDisposable) |
| MediaStrategyBase | Contracts/Media/MediaStrategyBase.cs | 280 | Same 5 | No |
| ObservabilityStrategyBase | Contracts/Observability/ObservabilityStrategyBase.cs | 298 | Same 5 + RequestObservabilityOptimizationAsync | Yes (IDisposable, SemaphoreSlim) |
| ConnectionStrategyBase | Connectors/ConnectionStrategyBase.cs | 626 | Same 5 + RequestConnectionOptimizationAsync | No (but has ILogger) |
| DataFormatStrategyBase | Contracts/DataFormat/DataFormatStrategy.cs | 634 | Same 5 + RequestFormatRecommendationAsync | No |
| DataLakeStrategyBase | Contracts/DataLake/DataLakeStrategy.cs | 126 | Same 5 | No |
| DataMeshStrategyBase | Contracts/DataMesh/DataMeshStrategy.cs | 109 | Same 5 | No |
| PipelineComputeStrategyBase | Contracts/Compute/PipelineComputeStrategy.cs | 661 | Same 5 + RequestComputeRecommendationAsync | No |
| StorageProcessingStrategyBase | Contracts/StorageProcessing/StorageProcessingStrategy.cs | 535 | Same 5 + RequestStorageProcessingOptimizationAsync | No |
| RaidStrategyBase (SDK) | Contracts/RAID/RaidStrategy.cs | 647 | Same 5 + RequestRaidRecommendationAsync | No |
| KeyStoreStrategyBase | Security/IKeyStore.cs | 927 | Same 5 | Yes (IDisposable) |

### Intelligence Boilerplate (removed by 25a, shims on StrategyBase)

Every SDK base has this identical pattern (~50-150 lines each):

```csharp
// STANDARD 5 (on every base):
protected IMessageBus? MessageBus { get; private set; }
public virtual void ConfigureIntelligence(IMessageBus? messageBus) { MessageBus = messageBus; }
protected bool IsIntelligenceAvailable => MessageBus != null;
public virtual KnowledgeObject GetStrategyKnowledge() { ... }
public virtual RegisteredCapability GetStrategyCapability() { ... }

// DOMAIN-SPECIFIC (on 12 of 17 bases):
protected async Task<T?> RequestXxxRecommendationAsync(...) { ... } // uses MessageBus
```

**After 25a:** The standard 5 are shims on StrategyBase (no-ops). The domain-specific RequestXxxAsync methods are removed from SDK bases. No concrete strategies call them (verified: 0 usages).

### Plugin-Local Bases (NOT modified by 25a)

| Base | Plugin | Intelligence Code | Strategies |
|------|--------|-------------------|-----------|
| AccessControlStrategyBase | UltimateAccessControl | None (no ConfigureIntelligence, no MessageBus) | 146 |
| ComplianceStrategyBase (plugin) | UltimateCompliance | None | 149 |
| ComputeRuntimeStrategyBase | UltimateCompute | ConfigureIntelligence(2x), MessageBus, IsIntelligenceAvailable, GetStrategyKnowledge, GetStrategyCapability | 85 |
| DataManagementStrategyBase | UltimateDataManagement | None (clean) | 101 |
| DataProtectionStrategyBase | UltimateDataProtection | ConfigureIntelligence, MessageBus, IsIntelligenceAvailable (26 refs) | 82 |
| StreamingDataStrategyBase | UltimateStreamingData | None | 58 |
| RaidStrategyBase (plugin) | UltimateRAID | _messageBus field, SetMessageBus method | 0 (all use SDK base) |

## 3. Migration Pattern (Before/After)

### Type A: SDK-Base Strategy (vast majority, ~1,106 strategies)

**Before migration (current state after 25a):**
```csharp
// File: Plugins/UltimateEncryption/Strategies/Aes/AesGcmStrategy.cs
public sealed class AesGcmStrategy : EncryptionStrategyBase  // Already inherits StrategyBase via 25a
{
    public override string StrategyId => "aes-256-gcm";
    public override string StrategyName => "AES-256-GCM";
    public override CipherInfo CipherInfo => new() { ... };

    protected override Task<byte[]> EncryptCoreAsync(...) { ... }
    protected override Task<byte[]> DecryptCoreAsync(...) { ... }
}
```

**After migration (25b):**
```csharp
// IDENTICAL. No changes needed for Type A strategies.
// The base class chain (AesGcmStrategy : EncryptionStrategyBase : StrategyBase)
// is already correct after 25a.
```

**Phase 25b action for Type A: VERIFY ONLY.** Build the plugin, run tests, confirm no regressions. No code changes needed in ~1,106 strategy files. The only action is removing the backward-compat shims from StrategyBase in 25b-06.

### Type B: Plugin-Local Base Strategy (AccessControl, Compute, DataManagement, DataProtection, Streaming, Compliance-plugin)

**Before migration:**
```csharp
// File: Plugins/UltimateCompute/ComputeRuntimeStrategyBase.cs
internal abstract class ComputeRuntimeStrategyBase : IComputeRuntimeStrategy
{
    public abstract string StrategyId { get; }
    public abstract string StrategyName { get; }
    // ... domain contract ...

    // INTELLIGENCE BOILERPLATE (must remove):
    protected IMessageBus? MessageBus { get; private set; }
    public void ConfigureIntelligence(IMessageBus? messageBus) { MessageBus = messageBus; }
    protected bool IsIntelligenceAvailable => MessageBus != null;
    public virtual KnowledgeObject GetStrategyKnowledge() { ... }
    public virtual RegisteredCapability GetStrategyCapability() { ... }
}
```

**After migration:**
```csharp
// File: Plugins/UltimateCompute/ComputeRuntimeStrategyBase.cs
internal abstract class ComputeRuntimeStrategyBase : PipelineComputeStrategyBase
// OR: internal abstract class ComputeRuntimeStrategyBase : StrategyBase, IComputeRuntimeStrategy
{
    // Domain contract preserved exactly
    // Intelligence boilerplate REMOVED
    // StrategyId, StrategyName now provided by StrategyBase
}
```

**Phase 25b action for Type B:**
1. Change base class to inherit from appropriate SDK domain base (or StrategyBase if no SDK domain base exists)
2. Remove intelligence boilerplate (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable)
3. Resolve any property/method conflicts between plugin base and new SDK base
4. Verify all concrete strategies still compile

### Type C: Strategy with Active MessageBus Usage (Interface 45 files, DataProtection 9 files)

**Before migration:**
```csharp
// File: Plugins/UltimateInterface/Strategies/Convergence/ConvergenceChoiceDialogStrategy.cs
internal sealed class ConvergenceChoiceDialogStrategy : InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // ... domain logic ...

    // ACTIVE MessageBus usage:
    if (MessageBus != null)
    {
        await MessageBus.PublishAsync("convergence.choice.recorded", new PluginMessage { ... });
    }
}
```

**After migration (per AD-05, MessageBus moves to plugin level):**
```csharp
// OPTION 1: Pass IMessageBus as method parameter from plugin
// The plugin calls strategy.HandleRequestAsync(request, messageBus, ct)
// Strategy receives it as a parameter, does NOT store it

// OPTION 2: Strategy returns events/actions, plugin publishes them
// Strategy returns a result containing "events to publish"
// Plugin iterates and publishes via its own MessageBus

// OPTION 3: Inject via constructor/initialization (pragmatic)
// Plugin passes IMessageBus during strategy initialization
// Strategy stores it but does NOT own it
```

**Phase 25b action for Type C:** This is the hardest part. Each of the 55 strategy files needs individual analysis to determine how to decouple MessageBus usage. Recommend Option 3 (pragmatic injection) for Phase 25b with a comment noting the coupling for future cleanup.

**CRITICAL per AD-08:** These strategies' MessageBus calls are REAL functionality (event publishing, AI requests). Removing them would be a regression. The code must be preserved in some form.

## 4. Domain-Specific Risks and Concerns

### HIGH RISK Domains

| Domain | Risk | Why | Mitigation |
|--------|------|-----|------------|
| Interface (73 strategies) | Active MessageBus usage in 45/73 files | Strategies publish real events to bus | Preserve functionality, move to parameter passing or plugin-level delegation |
| DataProtection (82 strategies) | Active MessageBus usage in 9 files, custom ConfigureIntelligence override | AI-enhanced strategies use intelligence for predictions | Preserve all intelligence calls, wrap with null checks |
| Compliance (149 strategies) | Name collision: plugin has its OWN ComplianceStrategyBase vs SDK's | Ambiguous class resolution when changing inheritance | Must rename or namespace-qualify carefully |
| Storage (130+49 strategies) | Intermediate bases: UltimateStorageStrategyBase AND DatabaseStorageStrategyBase | Three-level hierarchy already exists | Verify adapter chain through intermediates |

### MEDIUM RISK Domains

| Domain | Risk | Why | Mitigation |
|--------|------|-----|------------|
| Connector (280 strategies) | 10 sub-category bases (DatabaseConnectionStrategyBase, etc.) | Multi-level hierarchy adds complexity | Change sub-category bases to inherit new ConnectionStrategyBase |
| DataManagement (101 strategies) | 10+ sub-bases (BranchingStrategyBase, CachingStrategyBase, etc.) | Deep hierarchy complicates migration | Migrate root DataManagementStrategyBase first, sub-bases cascade |
| Compression (59 strategies) | Many strategies have custom Dispose(bool) logic | Must not lose dispose logic during migration | Verify all Dispose overrides preserved |
| Encryption (69 strategies) | Multi-class files (3.6 classes avg per file) | Bulk editing must handle all classes in file | Process per-class, not per-file |

### LOW RISK Domains

| Domain | Risk | Reason Low Risk |
|--------|------|-----------------|
| Transit (11 strategies) | None | Small count, clean pattern, no intelligence usage |
| Media (20 strategies) | None | Clean inheritance, no intelligence usage |
| DataFormat (28 strategies) | None | Clean inheritance, no intelligence usage |
| Observability (55 strategies) | None | Clean inheritance, no intelligence usage |
| StorageProcessing (43 strategies) | None | Clean inheritance, no intelligence usage |
| KeyManagement (69 strategies) | Minor: Pkcs11HsmStrategyBase intermediate | One extra level, but inherits SDK base |
| DataLake (56 strategies) | None | Clean, small SDK base |
| DataMesh (56 strategies) | None | Clean, small SDK base |
| RAID (47 strategies) | None | Already use SDK base via alias |
| Replication (61 strategies) | Minor: EnhancedReplicationStrategyBase intermediate | Already inherits SDK base |

### Inter-Strategy Dependencies

| Dependency | Where | Impact |
|------------|-------|--------|
| UltimateStorageStrategyBase extends StorageStrategyBase | UltimateStorage plugin | Intermediate base must be updated after SDK base |
| DatabaseStorageStrategyBase extends StorageStrategyBase | UltimateDatabaseStorage plugin | Same as above |
| EnhancedReplicationStrategyBase extends ReplicationStrategyBase | UltimateReplication plugin | Intermediate base cascades from SDK base change |
| Pkcs11HsmStrategyBase extends KeyStoreStrategyBase | UltimateKeyManagement plugin | Same cascade pattern |
| WasmLanguageStrategyBase extends ComputeRuntimeStrategyBase | UltimateCompute plugin | Plugin-local cascade |
| 10 DataManagement sub-bases extend DataManagementStrategyBase | UltimateDataManagement plugin | Deep hierarchy, all cascade from root base change |
| 10 Connection sub-category bases extend ConnectionStrategyBase | SDK Connectors | Already in SDK, cascade from 25a |

## 5. Boilerplate Removal Inventory

### Concrete Strategy Boilerplate (actually present in strategy files)

| Pattern | Files Affected | Domains | Lines to Remove |
|---------|---------------|---------|----------------|
| `override ConfigureIntelligence` | 1 file | DataProtection | ~10 lines |
| `override GetStrategyKnowledge` | 0 files | -- | 0 |
| `override GetStrategyCapability` | 0 files | -- | 0 |
| Active `MessageBus` usage | 56 files | Interface(45), DataProtection(9), DataManagement(1), Connector(1) | 0 (PRESERVE, do not remove) |
| Active `IsIntelligenceAvailable` usage | 43 files | Interface(34), DataProtection(9) | 0 (PRESERVE, do not remove) |
| Custom `Dispose(bool)` | ~40+ files | Compression (many), others | 0 (PRESERVE) |

**Key insight:** Almost zero boilerplate needs removal from concrete strategy files. The intelligence boilerplate is in BASE classes, not concrete strategies. The ~55 strategy files with MessageBus usage contain REAL functionality that must be preserved per AD-08.

### Plugin-Local Base Boilerplate (to remove during base migration)

| Base | Intelligence Lines to Remove | Notes |
|------|------------------------------|-------|
| ComputeRuntimeStrategyBase | ~60 lines (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable) | Full intelligence block |
| DataProtectionStrategyBase | ~30 lines (ConfigureIntelligence, MessageBus, IsIntelligenceAvailable) + 26 intelligence helper refs | Heavy intelligence usage throughout base |
| RaidStrategyBase (plugin) | ~10 lines (_messageBus, SetMessageBus) | Minimal, but 0 strategies use this base |
| AccessControlStrategyBase | 0 lines | No intelligence code |
| ComplianceStrategyBase (plugin) | 0 lines | No intelligence code |
| DataManagementStrategyBase | 0 lines | No intelligence code |
| StreamingDataStrategyBase | 0 lines | No intelligence code |

### StrategyBase Shim Removal (25b-06)

The backward-compatibility shims added by 25a-03 to `StrategyBase`:
- `ConfigureIntelligence(IMessageBus?)` -- virtual no-op
- `GetStrategyKnowledge()` -- virtual returning default KnowledgeObject
- `GetStrategyCapability()` -- virtual returning default RegisteredCapability
- `MessageBus` property -- always null
- `IsIntelligenceAvailable` property -- always false

These can ONLY be removed after ALL strategy overrides and direct usages are eliminated.

## 6. Recommended Plan Structure

### Revised Plan Grouping (by complexity, not just count)

**25b-01: Verify SDK-base domains, smallest first (Transit 11, Media 20, DataFormat 28, StorageProcessing 43)**
- Type A strategies only. No code changes needed in strategy files.
- Verify build, run tests, confirm each domain compiles with new hierarchy.
- ~102 strategies, all clean inheritance, zero intelligence usage.

**25b-02: Verify SDK-base domains, medium (Observability 55, DataLake 56, DataMesh 56, Compression 59, Replication 61)**
- Type A strategies. Some have custom Dispose (Compression).
- Replication has EnhancedReplicationStrategyBase intermediate -- verify cascade.
- ~287 strategies.

**25b-03: Verify SDK-base domains, large with intermediates (KeyManagement 69, RAID 47, Storage 130, DatabaseStorage 49, Connector 280)**
- Type A strategies but with intermediate bases.
- KeyManagement: Pkcs11HsmStrategyBase extends KeyStoreStrategyBase.
- RAID: All use SDK base (via alias SdkRaidStrategyBase) -- just verify.
- Storage: UltimateStorageStrategyBase intermediate must be verified.
- DatabaseStorage: DatabaseStorageStrategyBase extends StorageStrategyBase.
- Connector: 10 sub-category bases extend ConnectionStrategyBase.
- ~575 strategies.

**25b-04: Migrate plugin-local bases WITHOUT intelligence (AccessControl 146, Compliance-plugin 149, DataManagement 101, Streaming 58)**
- Type B migration: change plugin-local bases to inherit SDK domain base or StrategyBase.
- No intelligence code to remove (these bases are clean).
- CAUTION: Compliance name collision (plugin ComplianceStrategyBase vs SDK ComplianceStrategyBase).
- DataManagement has 10 sub-bases that cascade.
- ~454 strategies.

**25b-05: Migrate plugin-local bases WITH intelligence + Interface/DataProtection strategies (Encryption 69, Compute 85, DataProtection 82, Interface 73)**
- Type B + Type C: hardest migrations.
- ComputeRuntimeStrategyBase: remove full intelligence block, change base class.
- DataProtectionStrategyBase: remove intelligence from base, handle 9 strategy files with MessageBus usage.
- Interface: 45 strategy files use MessageBus for event publishing -- must preserve functionality.
- Encryption in this group because it was miscounted (69 not 12) and merits its own verification pass.
- ~309 strategies.

**25b-06: Remove backward-compat shims, final verification**
- Remove legacy methods from StrategyBase (ConfigureIntelligence, GetStrategyKnowledge, etc.)
- Full solution build verification (`dotnet build DataWarehouse.slnx`)
- Run all 939+ tests
- Grep verification: no strategy inherits fragmented base directly
- Behavioral equivalence spot checks

### Why This Ordering

1. **25b-01/02/03** are verification-only for Type A strategies. They prove the new hierarchy works across all domains before touching any code.
2. **25b-04** migrates clean plugin-local bases (no intelligence). Lower risk, proves the pattern works.
3. **25b-05** migrates the complex cases last: intelligence removal and MessageBus preservation.
4. **25b-06** removes shims only after ALL strategies are verified.

## 7. Handling MessageBus in Strategies (AD-05 Compliance)

### The Problem

AD-05 says strategies should NOT have MessageBus access. But 55 strategy files actively use MessageBus:
- 45 Interface strategies publish events (`convergence.choice.recorded`, `convergence.instance.arrived`, etc.)
- 9 DataProtection strategies send AI requests and publish events (`intelligence.predict`, `backup.completed`)
- 1 DataManagement strategy uses MessageBus

### Recommended Approach

**For Interface strategies (event publishing):**
The `InterfaceStrategyBase` from the SDK still provides a MessageBus property (as a backward-compat shim). After 25a, this is a no-op. However, these strategies need MessageBus to FUNCTION.

Recommendation: During 25b, keep MessageBus accessible to these strategies via the InterfaceStrategyBase domain base. Add a clear comment: `// TODO(Phase 27): Move event publishing to plugin level`. Phase 27 (Plugin Migration) is when the UltimateInterfacePlugin itself gets refactored to handle event publishing on behalf of its strategies.

**For DataProtection strategies (AI requests + event publishing):**
Same approach: preserve via DataProtectionStrategyBase (plugin-local). Mark for Phase 27 refactoring.

**Rationale:** AD-05 is a design GOAL. Phase 25b is about mechanical migration, not architectural redesign. Forcefully removing MessageBus from 55 strategies would require redesigning how those strategies interact with the system -- that is Phase 27's job. Phase 25b should NOT break working functionality per AD-08.

## 8. Build and Test State

### Current Build State
- SDK build has 4 pre-existing errors (HybridDatabasePluginBase, HybridStoragePluginBase, CipherPresetProviderPluginBase dispose conflicts)
- These are PLUGIN BASE issues, not strategy issues
- Expected to be fixed by Phase 23 (IDisposable patterns) or Phase 24 (plugin hierarchy)
- Phase 25b assumes these are resolved

### Test Coverage
- 58 test files, 939 test methods
- Strategy-specific tests exist for: Compression, Encryption, Compliance, DataFormat, Interface, Media, Observability, Processing, Security, Storage, Streaming
- No strategy-level behavioral equivalence test suite exists yet
- Phase 30 creates comprehensive behavioral verification tests

### Behavioral Equivalence Verification Strategy
For Phase 25b, verification is primarily compile-time:
1. `dotnet build DataWarehouse.slnx` -- zero errors after each domain migration
2. `dotnet test` -- all existing 939 tests pass
3. Grep verification: no strategy directly inherits old fragmented bases
4. Grep verification: no intelligence boilerplate remains in bases (except marked backward-compat)

Runtime behavioral equivalence testing is Phase 30's responsibility.

## 9. Risk Assessment

### High Risk

| Risk | Impact | Mitigation |
|------|--------|------------|
| Interface MessageBus removal breaks 45 strategies | Event publishing stops working | Keep MessageBus accessible, defer true decoupling to Phase 27 |
| Compliance name collision (plugin vs SDK ComplianceStrategyBase) | Ambiguous references, wrong base class | Use fully-qualified names, or rename plugin base to `UltimateComplianceStrategyBase` |
| DataProtection intelligence removal breaks AI-enhanced backup | AI predictive features stop working | Preserve MessageBus access in DataProtection base, mark for Phase 27 |
| ROADMAP count estimates wrong, plans sized incorrectly | Plans take longer than expected | Use verified counts from this research |

### Medium Risk

| Risk | Impact | Mitigation |
|------|--------|------------|
| Intermediate base cascade failures | Strategies fail to compile due to missed intermediate | Map all intermediate bases, migrate bottom-up |
| Multi-class files partially migrated | Some classes in file migrated, others missed | Process per-class not per-file, verify class count before/after |
| Custom Dispose logic lost during base change | Resource leaks | Verify all Dispose overrides preserved after migration |
| Build errors from prior phases not resolved | Cannot verify clean build | Document dependency, ensure 23/24 complete first |

### Low Risk

| Risk | Impact | Mitigation |
|------|--------|------------|
| Type A strategies need code changes | Unexpected compilation errors | 25a backward-compat shims prevent this |
| Performance regression from hierarchy change | Slower strategy execution | Virtual call overhead is negligible; no runtime behavior changes |

## Sources

### Primary (HIGH confidence)
All findings verified against codebase at `C:\Temp\DataWarehouse\DataWarehouse`:
- All 17 SDK strategy base files examined for intelligence patterns
- Plugin-local strategy bases in 7 plugins examined for intelligence code
- Strategy counts verified via `grep -r "class.*: BaseClassName"` across all plugin directories
- MessageBus and IsIntelligenceAvailable usage counted across all strategy files
- ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability override counts verified
- Multi-class file patterns verified for Encryption, Compression, Replication
- Custom Dispose patterns verified across all plugins
- Intermediate bases enumerated via `grep -rn "abstract class.*StrategyBase"` across plugins
- Test coverage counted: 58 test files, 939 test methods
- 25a research document cross-referenced for hierarchy design
- 25a plan files (01-05) cross-referenced for migration approach
- ARCHITECTURE_DECISIONS.md AD-05 and AD-08 reviewed for constraints

### Secondary (MEDIUM confidence)
- ROADMAP.md count estimates used as baseline (now corrected by actual counts)
- Interface strategy count (73) includes some that may be service classes, not pure strategies

## Metadata

**Confidence breakdown:**
- Strategy counts per domain: HIGH -- verified via grep against codebase
- Intelligence boilerplate patterns: HIGH -- every base and 100+ strategy files examined
- Migration pattern (Type A/B/C): HIGH -- verified which strategies need changes vs verification only
- MessageBus handling recommendation: MEDIUM -- pragmatic approach, but AD-05 compliance is a judgment call
- Build state: HIGH -- existing errors documented, dependency on Phase 23/24 noted

**Research date:** 2026-02-14
**Valid until:** 2026-03-14 (stable codebase, no external dependencies)
