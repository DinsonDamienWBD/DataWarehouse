# Phase 28: Dead Code Cleanup - Research

**Researched:** 2026-02-14
**Domain:** C# codebase dead code analysis, SDK/plugin architecture cleanup
**Confidence:** HIGH (all findings verified by grep reference counting across ~2,558 files / ~1.1M LOC)

## Summary

Phase 28 targets removal of truly unused classes, files, and interfaces from the DataWarehouse SDK and Kernel, while preserving future-ready interfaces for unreleased hardware/technology per AD-06. Research involved systematically scanning all 297 SDK source files, 23 Kernel files, and 60 plugin directories for zero-reference types.

**The codebase has approximately 28,000-30,000 lines of dead code** across ~40+ files, concentrated in three main areas: (1) old pre-hierarchy plugin base classes that were never adopted by any plugin, (2) SDK service/infrastructure files defining unused types, and (3) orphaned AI module files. An additional ~10,000 lines are in files containing a mix of dead and alive types, requiring careful extraction rather than wholesale deletion.

**Primary recommendation:** Delete dead-only files in batches by category (plugin bases, services, infrastructure, AI), then extract dead types from mixed files, then clean TODO(25b) remnants, then verify build.

## Standard Stack

### Core
| Tool | Purpose | Why Standard |
|------|---------|--------------|
| `grep -rl` / ripgrep | Zero-reference detection | Fast full-codebase search, reliable for class/interface name matching |
| `dotnet build` | Build verification | Confirms no broken references after deletion |
| `wc -l` | LOC impact measurement | Quantify cleanup progress |

### Supporting
| Tool | Purpose | When to Use |
|------|---------|-------------|
| `dotnet build --no-incremental` | Full rebuild after batch deletion | After each removal batch to catch transitive breaks |
| IDE "Find All References" equivalent | Secondary verification | Cross-check before deleting types in mixed files |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| grep reference counting | Roslyn analyzer / semantic analysis | More accurate (handles namespaces/overloads) but overkill for this scale; grep is sufficient given unique type names |
| Manual file deletion | Script-based deletion | Scripts faster for large batches, but manual review catches edge cases |

## Architecture Patterns

### Deletion Batch Strategy
```
Batch 1: Pure dead files (entire file is dead)
  ├── Plugin bases with zero external references
  ├── SDK Services with zero external references
  ├── SDK Infrastructure with zero external references
  └── AI module files with zero external references

Batch 2: Mixed files (some types dead, some alive)
  ├── InfrastructurePluginBases.cs (extract dead bases, keep used types)
  ├── PluginBase.cs (remove 10+ dead intermediate bases)
  └── StorageOrchestratorBase.cs (partially dead)

Batch 3: Superseded code
  ├── Duplicate KnowledgeObject resolution
  └── TODO(25b) intelligence helper methods (if callers migrated)

Batch 4: Future-ready preservation
  └── Add "FUTURE:" documentation comments to kept interfaces
```

### Verification Pattern
For EACH candidate before deletion:
1. Grep for class/interface name across entire codebase (excluding obj/)
2. If count = 1 (self-only) or count = 0: confirmed dead
3. If count > 1: check if external references are from other dead files (cascade)
4. Verify not in future-ready list (AD-06)
5. Delete and rebuild

### Anti-Patterns to Avoid
- **Deleting entire mixed files:** Files like InfrastructurePluginBases.cs and PluginBase.cs contain BOTH dead and alive types. Never delete the whole file.
- **Cascade blindness:** Some dead types are only referenced by other dead types (e.g., MetadataIndexPluginBase referenced only by ExabyteScalePluginBases which is itself dead). Must trace the full chain.
- **Deleting future-ready interfaces:** AD-06 explicitly preserves interfaces for quantum crypto, brain-reading encryption, DNA storage, neuromorphic computing, RDMA, io_uring, NUMA, TPM2, HSM.

## Dead Code Inventory

### Category 1: Pure Dead Files (delete entire file)

#### 1A: Plugin Base Files with Zero External References (~7,200 LOC)

| File | LOC | Status | Notes |
|------|-----|--------|-------|
| `Contracts/CarbonAwarePluginBases.cs` | 908 | DEAD | 5 bases, 0 external refs. BUT interfaces in ICarbonAwareStorage.cs (AD-06 future-ready) |
| `Contracts/ComplianceAutomationPluginBases.cs` | 794 | DEAD | 3 bases, 0 external refs. IComplianceAutomation has 2 refs (keep interface, delete bases) |
| `Contracts/DataConnectorPluginBases.cs` | 788 | DEAD | 4 bases, 0 external refs |
| `Contracts/ExabyteScalePluginBases.cs` | 852 | DEAD | 3 bases, 0 external refs. Uses MetadataIndexPluginBase (also dead) |
| `Contracts/HypervisorPluginBases.cs` | 680 | DEAD | 4 bases, 0 external refs. Interfaces in IHypervisorSupport.cs are future-ready |
| `Contracts/MultiMasterPluginBases.cs` | 497 | DEAD | 1 base, 0 external refs |
| `Contracts/TransitCompressionPluginBases.cs` | 738 | DEAD | 1 base, 0 external refs. TransitEncryptionPluginBase IS used (10 refs) |
| `Contracts/FeaturePluginInterfaces.cs` | 2,748 | DEAD | 7 bases + interfaces + types, 0 external refs for any |
| `Contracts/ProviderInterfaces.cs` | 64 | DEAD | IFeaturePlugin (6 refs) + IStorageProvider (29 refs) -- BUT these are used, extract to another file |
| `Contracts/StrategyAdapters.cs` | 127 | DEAD | 2 adapters, 0 external refs |

**CAUTION on ProviderInterfaces.cs:** IFeaturePlugin and IStorageProvider are USED (6 and 29 refs respectively). These interfaces must be MOVED, not deleted.

#### 1B: SDK Service Files with Zero External References (~4,389 LOC)

| File | LOC | Status | Notes |
|------|-----|--------|-------|
| `Services/InstancePoolingService.cs` | 813 | DEAD | IObjectPool, PoolConfiguration -- 0 external refs |
| `Services/IntelligenceInterfaceService.cs` | 1,681 | DEAD | ConversationRole, AgentType etc -- 0 external refs |
| `Services/JobSchedulerService.cs` | 705 | DEAD | Types overlap with Kernel scheduler but no cross-refs |
| `Services/PermissionCascadeService.cs` | 422 | DEAD | Permission, PermissionAction -- 0 external refs |
| `Services/SmartFolderService.cs` | 360 | DEAD | SmartFolder, MetadataCondition -- 0 external refs |
| `Services/VfsPlaceholderService.cs` | 408 | DEAD | PlaceholderState etc -- 0 external refs |

#### 1C: SDK Infrastructure Files with Zero External References (~3,962 LOC)

| File | LOC | Status | Notes |
|------|-----|--------|-------|
| `Infrastructure/EdgeManagedPhase8.cs` | 872 | DEAD | Edge/tenant/billing types, 0 external refs |
| `Infrastructure/SingleFileDeploy.cs` | 511 | DEAD | 0 external refs |
| `Infrastructure/PerformanceOptimizations.cs` | 1,910 | DEAD | Request coalescer, write-behind, tiered storage metrics -- 0 external refs |
| `Infrastructure/StandardizedExceptionHandling.cs` | 669 | DEAD | ComplianceFramework enum etc -- 0 external refs |

#### 1D: SDK AI Module Dead Files (~5,681 LOC)

| File | LOC | Status | Notes |
|------|-----|--------|-------|
| `AI/SemanticAnalyzerBase.cs` | 2,062 | DEAD | 0 external refs (only ref is from VectorOperations) |
| `AI/ISemanticAnalyzer.cs` | 1,056 | DEAD | Only ref from SemanticAnalyzerBase (also dead) |
| `AI/VectorOperations.cs` | 238 | DEAD | Only ref from SemanticAnalyzerBase (also dead) |
| `AI/GraphStructures.cs` | 263 | DEAD | 0 external refs |
| `AI/MathUtilities.cs` | 323 | DEAD | 0 external refs |
| `Infrastructure/HttpClientFactory.cs` | 710 | DEAD | 0 external refs |

**Note:** `AI/KnowledgeObject.cs` (336 LOC) and `AI/Knowledge/KnowledgeObject.cs` (1,197 LOC) are DUPLICATES. The old one (AI/ namespace) has 144 refs; the new one (AI/Knowledge/ namespace) has 7 refs. Both are alive but need consolidation.

#### 1E: Miscellaneous Dead Files (~1,390 LOC)

| File | LOC | Status | Notes |
|------|-----|--------|-------|
| `Contracts/PublicTypes.cs` | 70 | DEAD | ISemanticMemory(2 refs), IMetricsProvider(2 refs), RemoteResourceUnavailableException(1 ref) -- check if refs are from other dead code |
| `Contracts/Hierarchy/NewFeaturePluginBase.cs` | 52 | DEAD | Contains FeaturePluginBase: IntelligenceAwarePluginBase -- but the ACTUAL FeaturePluginBase is in Hierarchy/ namespace; this may be a redirect |
| `Licensing/CustomerTiers.cs` | 14 | DEAD | Empty/placeholder |
| `Licensing/FeatureEnforcement.cs` | 13 | DEAD | Empty/placeholder |
| `Governance/GovernanceContracts.cs` | 206 | DEAD | INeuralSentinel etc -- BUT GovernancePluginBase in PluginBase.cs references it (GovernancePluginBase is itself dead) |
| `Contracts/OrchestrationInterfaces.cs` | 1,601 | MOSTLY DEAD | Many types dead, but IWriteDestination has 4 refs, IContentProcessor has 2 refs -- needs extraction |
| `Kernel/Pipeline/EligibleFeatureDefinitions.cs` | 447 | DEAD | EligibleFeature class, 0 external refs |
| `Kernel/Pipeline/PipelineConfigResolver.cs` | 748 | DEAD | 0 external refs, all types self-contained |

**CAUTION on OrchestrationInterfaces.cs:** IWriteDestination (4 refs), IContentProcessor (2 refs), and WriteDestinationPluginBase (3 refs) are USED. Extract before deleting dead types.

### Category 2: Mixed Files (extract dead types)

#### 2A: PluginBase.cs -- Dead Intermediate Bases (~1,800 LOC estimated)

The following intermediate bases in PluginBase.cs have ZERO external references (count=1, self-only):

| Dead Base | Line | Notes |
|-----------|------|-------|
| `OrchestrationProviderPluginBase` | 1468 | 0 external refs |
| `IntelligencePluginBase` | 1491 | 0 external refs |
| `ListableStoragePluginBase` | 1610 | 0 external refs, but TieredStoragePluginBase inherits it |
| `RealTimePluginBase` | 2223 | 0 external refs |
| `CloudEnvironmentPluginBase` | 2298 | 0 external refs |
| `SerializerPluginBase` | 2380 | 0 external refs |
| `SemanticMemoryPluginBase` | 2416 | 0 external refs |
| `MetricsPluginBase` | 2446 | 0 external refs |
| `GovernancePluginBase` | 2496 | 0 external refs |
| `KeyStorePluginBase` | 2539 | 0 external refs (interfaces used, but base unused) |

**Keep (has external usage):**
- `PluginBase` (root -- everything inherits)
- `DataTransformationPluginBase` (4 refs)
- `StorageProviderPluginBase` (8 refs)
- `MetadataIndexPluginBase` (2 refs, but only from ExabyteScalePluginBases which is dead -- cascade dead)
- `LegacyFeaturePluginBase` (42 refs -- Phase 27 target)
- `SecurityProviderPluginBase` (2 refs, from MilitarySecurityPluginBases -- future-ready)
- `InterfacePluginBase` (used in PluginBase hierarchy internally)
- `PipelinePluginBase` (14 refs)
- `TieredStoragePluginBase` (2 refs)
- `CacheableStoragePluginBase` (3 refs)
- `IndexableStoragePluginBase` (5 refs)
- `ConsensusPluginBase` (2 refs, Raft plugin)
- `AccessControlPluginBase` (4 refs)
- `EncryptionPluginBase` (extensively used by UltimateEncryption)
- `CompressionPluginBase` (extensively used by UltimateCompression)
- `ContainerManagerPluginBase` (2 refs, Kernel uses it)
- `ReplicationPluginBase` (3 refs)

#### 2B: InfrastructurePluginBases.cs -- Mixed (2,652 LOC)

Dead types within this file:
- All plugin base classes (HealthProviderPluginBase, RateLimiterPluginBase, CircuitBreakerPluginBase, TransactionManagerPluginBase, RaidProviderPluginBase, ErasureCodingPluginBase, ComplianceProviderPluginBase, IAMProviderPluginBase) -- 0 external refs each
- IRaidProvider, IErasureCodingProvider, IComplianceProvider, IIAMProvider -- 0 external refs
- DirtyPageTable, TransactionTable, DoubleWriteBuffer, PageChecksumManager -- 0 external refs
- All record types (HealthPrediction, RateLimitOptimization, etc.) -- 0 external refs

Live types within this file (MUST KEEP):
- IHealthCheck (used by KernelInfrastructure, ServiceManager)
- IRateLimiter, IResiliencePolicy (used by infrastructure)
- ITransactionManager (1 external ref)
- RaidLevel, RaidArrayStatus (used by RAID plugin)
- ComplianceViolation, ComplianceReport, ComplianceContext, ViolationSeverity (used by Compliance plugin)
- AuthenticationResult, TokenValidationResult, AuthorizationResult (used by security plugins)
- CircuitBreakerOpenException (used externally)

**Approach:** Extract live types into a new targeted file (e.g., `Contracts/InfrastructureContracts.cs`), then delete InfrastructurePluginBases.cs.

#### 2C: StorageOrchestratorBase.cs -- Mostly Dead (1,277 LOC)

Dead types: IndexingStorageOrchestratorBase, RealTimeStorageOrchestratorBase, SearchOrchestratorBase, CachedStrategy, RealTimeStrategy
Live types: StoragePoolBase (test refs), SimpleStrategy (5 refs), MirroredStrategy (3 refs), WriteAheadLogStrategy (2 refs), StorageStrategyBase (used by DatabaseStorage plugin)

#### 2D: IStorageOrchestration.cs -- Borderline Dead (564 LOC)

Defines IStoragePool, IHybridStorage, IRealTimeStorage, IStorageSearchOrchestrator. All references come from StorageOrchestratorBase.cs (which is mostly dead) and tests. If StorageOrchestratorBase dead types are removed, these become orphaned too.

### Category 3: Future-Ready (KEEP per AD-06) (~4,922 LOC)

These files have zero external references but define interfaces for unreleased/specialized hardware. AD-06 mandates they be preserved with "FUTURE:" documentation.

| File | LOC | Technology Domain |
|------|-----|-------------------|
| `Contracts/HardwareAccelerationPluginBases.cs` | 1,126 | TPM2, HSM, QAT, GPU acceleration |
| `Contracts/LowLatencyPluginBases.cs` | 554 | RDMA, io_uring, NUMA |
| `Contracts/MilitarySecurityPluginBases.cs` | 788 | Mandatory access control, multi-level security, two-person integrity |
| `Hardware/IHardwareAcceleration.cs` | 435 | Hardware accelerator interfaces |
| `Sustainability/ICarbonAwareStorage.cs` | 451 | Carbon-aware scheduling |
| `Virtualization/IHypervisorSupport.cs` | 306 | Hypervisor detection, balloon driver |
| `Scale/IExabyteScale.cs` | 444 | Exabyte-scale sharding |
| `Security/IMilitarySecurity.cs` | 517 | Military security contracts |
| `Performance/ILowLatencyStorage.cs` | 301 | Low-latency storage |

**Action:** Add `// FUTURE: [technology] -- interface preserved for unreleased hardware per AD-06` to each.

### Category 4: Superseded Code

#### 4A: Duplicate KnowledgeObject (~336 LOC removable)
- `AI/KnowledgeObject.cs` (336 LOC, old, `DataWarehouse.SDK.AI` namespace, 144 refs)
- `AI/Knowledge/KnowledgeObject.cs` (1,197 LOC, new, `DataWarehouse.SDK.AI.Knowledge` namespace, 7 refs)

The old version is simpler but far more widely used. Resolution options:
1. Keep old, delete new (7 refs to update -- safer)
2. Migrate all 144 refs to new namespace, delete old (more work, better long-term)

**Recommendation:** Option 1 for Phase 28 (minimal churn), migrate in a later phase if desired.

#### 4B: TODO(25b) Legacy Intelligence Helpers (~300 LOC across 3 files)
- `Contracts/Compression/CompressionStrategy.cs` lines 997-1041 (~45 LOC)
- `Contracts/RAID/RaidStrategy.cs` lines 857-897 (~41 LOC)
- `Contracts/Replication/ReplicationStrategy.cs` lines 481-518 (~38 LOC)

These methods (GetStrategyDescription, GetKnowledgePayload, GetKnowledgeTags, GetCapabilityMetadata, GetSemanticDescription) are marked TODO(25b) for removal. However, they ARE still overridden in plugins (UltimateReplication has 6 overrides, UltimateDataProtection has many). **Cannot remove without migrating callers first.** Recommend deferring to Phase 30 or handling as part of this phase if plugin migration is feasible.

### Category 5: Post-Phase 27 Dead Code (NOT YET DEAD)

These will become dead AFTER Phase 27 migrates all plugins:

| Item | Current Refs | Will Be Dead After Phase 27 |
|------|-------------|----------------------------|
| `LegacyFeaturePluginBase` | 42 refs (21 plugins) | Yes |
| `IntelligenceAwarePluginBase` | 29 refs | Yes |
| `SpecializedIntelligenceAwareBases.cs` (4,168 LOC) | 8+ plugins | Yes (after migration to Hierarchy bases) |
| All IntelligenceAware* specialized bases | 2-8 refs each | Yes |

**Phase 28 depends on Phase 27 being complete.** If Phase 27 is done, these ~5,800 LOC become removable.

## LOC Impact Summary

| Category | Files | Estimated LOC | Action |
|----------|-------|---------------|--------|
| Pure dead files (delete whole file) | ~30 files | ~22,600 | Delete |
| Mixed files (extract dead types) | ~4 files | ~4,000 removable | Extract + delete dead |
| Future-ready (keep + document) | ~9 files | ~4,900 | Add FUTURE: comments |
| Superseded/duplicate | ~2 areas | ~650 | Consolidate |
| Post-Phase 27 dead (conditional) | ~3 files | ~5,800 | Delete if Phase 27 complete |
| **Total removable** | | **~27,000-33,000** | **~2.5-3% of codebase** |

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Reference counting | Custom Roslyn analyzer | `grep -rl` with class name | Unique type names make grep reliable; Roslyn is overkill |
| Cascade detection | Manual chain tracing | Grep + script | Dead type A referenced only by dead type B -- must detect both |
| Build verification | Partial builds | `dotnet build` full solution | Must catch transitive reference breaks |

**Key insight:** The unique naming convention in this codebase (e.g., `IntelligenceAwareConnectorPluginBase`) makes simple string matching extremely reliable for reference detection. No false positives from common names.

## Common Pitfalls

### Pitfall 1: Deleting Types from Mixed Files Without Extraction
**What goes wrong:** Delete InfrastructurePluginBases.cs entirely, breaking 20+ references to IHealthCheck, RaidLevel, ComplianceViolation, etc.
**Why it happens:** File-level grep shows "0 references to filename" but individual types within the file are used.
**How to avoid:** Always check individual type references, not just filename references. For mixed files, extract live types FIRST, then delete.
**Warning signs:** File has >500 LOC and defines >5 types.

### Pitfall 2: Breaking the PluginBase.cs Inheritance Chain
**What goes wrong:** Remove ListableStoragePluginBase (0 direct external refs) but TieredStoragePluginBase inherits from it (2 external refs).
**Why it happens:** Inheritance chains create indirect references not caught by direct grep.
**How to avoid:** Before removing any base class, check the FULL inheritance chain. If class B : class A, and B has refs, A cannot be removed.
**Warning signs:** Abstract base classes with the word "Base" in the name.

### Pitfall 3: Removing Future-Ready Interfaces
**What goes wrong:** Delete IMilitarySecurity.cs (0 refs), losing the interface contracts for two-person integrity, mandatory access control, etc.
**Why it happens:** Looks dead by reference count but is explicitly preserved by AD-06.
**How to avoid:** Check the AD-06 future-ready list before any deletion. Hardware-specific interfaces (TPM2, HSM, RDMA, NUMA, io_uring, quantum, brain-reading, DNA storage, neuromorphic) are ALWAYS preserved.
**Warning signs:** File names containing "Military", "Hardware", "LowLatency", "Exabyte", "Carbon", "Hypervisor".

### Pitfall 4: Forgetting to Remove Using Directives
**What goes wrong:** Dead code deleted, build succeeds, but hundreds of orphaned `using` directives remain as warnings (or errors with TreatWarningsAsErrors).
**Why it happens:** After deleting types, files that imported the deleted namespace now have unused usings.
**How to avoid:** After each batch deletion, run `dotnet build` and fix CS8019 (unnecessary using directive) warnings.
**Warning signs:** Build warnings mentioning namespaces from deleted files.

### Pitfall 5: Cascade Death Not Detected
**What goes wrong:** File A references only File B. File B is dead. A is also dead but looks alive (1 external ref from B).
**Why it happens:** Simple grep counts don't distinguish "referenced by live code" from "referenced by other dead code."
**How to avoid:** For any file with exactly 1-2 external refs, verify those refs are from LIVE code, not from other dead files being removed in the same batch.
**Warning signs:** Reference count of 1-2 from files also in the dead code list.

## Code Examples

### Reference Counting Pattern
```bash
# Check if a type is dead (count=1 means self-only definition)
grep -rl "TypeName" --include="*.cs" . | grep -v "obj/" | wc -l

# For count > 1, verify external usage (not from other dead files)
grep -rl "TypeName" --include="*.cs" . | grep -v "obj/" | grep -v "DefinitionFile.cs"

# Cascade check: verify the referencing files are themselves alive
grep -rl "ReferencingFile" --include="*.cs" . | grep -v "obj/" | wc -l
```

### Safe Mixed-File Extraction Pattern
```csharp
// BEFORE: InfrastructurePluginBases.cs contains dead bases + live types
// STEP 1: Create new file with live types only
// InfrastructureContracts.cs (new file)
namespace DataWarehouse.SDK.Contracts
{
    public interface IHealthCheck { ... }  // extracted, still alive
    public enum RaidLevel { ... }          // extracted, still alive
    // etc.
}

// STEP 2: Remove dead bases from InfrastructurePluginBases.cs
// STEP 3: If file is now empty, delete it
// STEP 4: dotnet build to verify
```

### FUTURE Documentation Pattern
```csharp
// FUTURE: RDMA transport -- interface preserved for RDMA hardware per AD-06.
// This base class has zero current implementations but defines the contract
// for future RDMA-capable storage plugins.
public abstract class RdmaTransportPluginBase : LegacyFeaturePluginBase, IRdmaTransport, IIntelligenceAware
{
    // ...
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| 111+ plugin bases in monolithic files | 18 domain bases in Hierarchy/ namespace | Phase 24 (AD-01, AD-07) | Old bases are dead, new hierarchy is active |
| Intelligence on every base class | Intelligence at plugin level only (AD-05) | Phase 25a/25b | Specialized IntelligenceAware* bases marked [Obsolete] |
| 7 fragmented strategy bases | Unified StrategyBase root | Phase 25a (AD-05) | Old strategy helpers marked TODO(25b) |
| Specialized feature bases (Dedup, Versioning, etc.) | Strategy pattern within Ultimate plugins | Phase 1-21 | Old bases in FeaturePluginInterfaces.cs are dead |

**Deprecated/outdated:**
- `LegacyFeaturePluginBase`: [Obsolete] since Phase 24, still used by 21 plugins pending Phase 27 migration
- All `IntelligenceAware*` specialized bases: [Obsolete] since Phase 24, pending Phase 27 migration
- `NewFeaturePluginBase.cs`: Transition file, superseded by Hierarchy/Feature/* bases

## Open Questions

1. **Phase 27 completion status at time of Phase 28 execution**
   - What we know: Phase 28 depends on Phase 27. Phase 27 has 5 plans, currently unexecuted.
   - What's unclear: Will Phase 27 be fully complete? If not, LegacyFeaturePluginBase and IntelligenceAware* bases cannot be removed.
   - Recommendation: Phase 28 plans should have a conditional batch -- "if Phase 27 complete, also remove these"

2. **TODO(25b) intelligence helper methods**
   - What we know: 15 methods across 3 strategy base files marked for removal. Plugins override these methods.
   - What's unclear: Can plugin overrides be safely removed or do they serve plugin-local purposes?
   - Recommendation: Investigate each override. If they only call the base, remove. If they add plugin-specific logic, preserve.

3. **ProviderInterfaces.cs and OrchestrationInterfaces.cs live type extraction**
   - What we know: Most types in these files are dead, but a few (IStorageProvider, IFeaturePlugin, IWriteDestination) are alive.
   - What's unclear: Where should extracted types be relocated? Existing files or new ones?
   - Recommendation: Move to existing relevant files (IStorageProvider to IPlugin.cs area, IWriteDestination to Pipeline contracts).

## Sources

### Primary (HIGH confidence)
- Direct `grep -rl` reference counting across 2,558 C# source files in the repository
- AD-06 definition in `.planning/ARCHITECTURE_DECISIONS.md` lines 190-211
- Phase 28 definition in `.planning/ROADMAP.md` lines 291-309
- CLEAN-01 through CLEAN-03 in `.planning/REQUIREMENTS.md` lines 162-164

### Secondary (HIGH confidence)
- `[Obsolete]` attribute annotations in SDK source files
- Phase 25b deviation log confirming preserved intelligence methods
- PluginBase.cs class hierarchy (4,046 LOC reviewed in full)

## Metadata

**Confidence breakdown:**
- Dead code inventory: HIGH - every candidate verified by grep across full codebase
- LOC estimates: HIGH - direct `wc -l` on all candidate files
- Future-ready classification: HIGH - matches AD-06 explicitly named technologies
- Cascade detection: MEDIUM - checked primary cascades but deep chains could exist
- Post-Phase 27 estimates: MEDIUM - depends on Phase 27 execution scope

**Research date:** 2026-02-14
**Valid until:** Until Phase 27 executes (changes reference counts for [Obsolete] bases)
