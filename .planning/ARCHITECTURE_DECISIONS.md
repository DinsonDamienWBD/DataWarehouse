# Architecture Decisions: v2.0 SDK Hardening

**Decided:** 2026-02-12
**Context:** Discussion between user and architect during v2.0 milestone planning

---

## AD-01: Plugin Hierarchy — Two Branches Under IntelligenceAwarePluginBase

**Decision:** All plugins (except UltimateIntelligencePlugin) inherit from IntelligenceAwarePluginBase. Below that, two sibling branches:
- **DataPipelinePluginBase** — plugins that data flows THROUGH (encryption, storage, replication, transit, integrity)
- **FeaturePluginBase** — plugins that provide SERVICES (security, interfaces, governance, compute, observability)

**Rationale:** Not everything is a pipeline stage. MetricsPluginBase observes the pipeline, GovernancePluginBase enforces policy on it, InterfacePluginBase serves data from it. These don't need pipeline semantics (stage ordering, back-pressure, throughput tracking). But storage, encryption, compression, replication — data literally flows through them.

**Target hierarchy:**
```
IPlugin (interface)
└── PluginBase (lifecycle, capability registry, knowledge registry, dispose)
    ├── UltimateIntelligencePlugin (IS the intelligence — inherits PluginBase directly)
    └── IntelligenceAwarePluginBase (AI socket, graceful degradation)
        ├── DataPipelinePluginBase (data flows THROUGH these)
        │   ├── DataTransformationPluginBase (mutates data)
        │   │   ├── EncryptionPluginBase → UltimateEncryptionPlugin
        │   │   └── CompressionPluginBase → UltimateCompressionPlugin
        │   ├── StoragePluginBase (data persistence endpoint)
        │   │   └── UltimateStoragePlugin (composes all storage capabilities via strategies)
        │   ├── ReplicationPluginBase (distributes data)
        │   │   ├── UltimateReplicationPlugin
        │   │   └── UltimateRAIDPlugin
        │   ├── DataTransitPluginBase (moves data between nodes)
        │   │   └── UltimateDataTransitPlugin
        │   └── IntegrityPluginBase (verifies data at boundaries)
        │       Strategies: TamperProof, Blockchain, WORM, HashChain
        │
        └── FeaturePluginBase (provides services/capabilities)
            ├── SecurityPluginBase
            │   ├── AccessControlPluginBase → UltimateAccessControlPlugin
            │   ├── KeyManagementPluginBase → UltimateKeyManagementPlugin
            │   ├── CompliancePluginBase → UltimateCompliancePlugin
            │   └── ThreatDetectionPluginBase
            ├── InterfacePluginBase → UltimateInterfacePlugin
            ├── DataManagementPluginBase
            │   → UltimateDataGovernancePlugin, UltimateDataCatalogPlugin,
            │     UltimateDataQualityPlugin, UltimateDataLineagePlugin, etc.
            ├── ComputePluginBase → UltimateComputePlugin
            ├── ObservabilityPluginBase → UniversalObservabilityPlugin
            ├── StreamingPluginBase → UltimateStreamingDataPlugin
            ├── MediaPluginBase
            ├── FormatPluginBase → UltimateDataFormatPlugin
            ├── InfrastructurePluginBase
            │   → UltimateDeploymentPlugin, UltimateResiliencePlugin,
            │     UltimateSustainabilityPlugin, UltimateMultiCloudPlugin
            ├── OrchestrationPluginBase
            │   → UltimateWorkflowPlugin, UltimateEdgeComputingPlugin
            └── PlatformPluginBase
                → UltimateSDKPortsPlugin, UltimateMicroservicesPlugin
```

---

## AD-02: Single Encryption/Compression Base — No AtRest vs Transit Split

**Decision:** One `EncryptionPluginBase` and one `CompressionPluginBase`. No separate AtRest/Transit base classes.

**Rationale:** The difference between at-rest and in-transit is about key management and lifecycle (session keys vs stored keys, forward secrecy vs key wrapping), not about the algorithm. This is exactly what the strategy pattern handles — the base provides the common interface, strategies handle context-specific behavior. UltimateEncryptionPlugin already works this way with 30+ strategies.

---

## AD-03: Specialized Bases Become Composable Services/Strategies

**Decision:** Specialized base classes (TieredStoragePluginBase, CacheableStoragePluginBase, IndexableStoragePluginBase, MandatoryAccessControlPluginBase, etc.) should NOT be in the inheritance chain. Their logic should be extracted into composable services that the domain PluginBase orchestrates.

**Rationale:** C# has single inheritance. UltimateStoragePlugin needs ALL storage capabilities (tiered + cached + indexed + low-latency + database), but can only inherit ONE base. If these capabilities are locked in an inheritance chain, Ultimate plugins can't use them all.

**Pattern:**
```csharp
// BEFORE: Logic locked in inheritance chain
class TieredStoragePluginBase : ListableStoragePluginBase { /* tiering logic */ }
class CacheableStoragePluginBase : ListableStoragePluginBase { /* caching logic */ }
// UltimateStoragePlugin can't inherit both

// AFTER: Logic extracted into composable services
class StoragePluginBase : DataPipelinePluginBase
{
    protected ITierManager TierManager { get; }       // from TieredStoragePluginBase
    protected ICacheManager CacheManager { get; }     // from CacheableStoragePluginBase
    protected IStorageIndex StorageIndex { get; }     // from IndexableStoragePluginBase
    protected IConnectionRegistry Connections { get; } // from HybridStoragePluginBase
    protected IHealthMonitor HealthMonitor { get; }   // from HybridStoragePluginBase
}
// UltimateStoragePlugin inherits StoragePluginBase, composes ALL capabilities
```

**Rule:** If there are SEPARATE Ultimate plugins for sub-domains (AccessControl, KeyManagement, Compliance are separate plugins), sub-domain bases stay. If ONE Ultimate plugin handles all variations (UltimateStoragePlugin handles all 130+ backends), the specialized bases become strategies/services.

---

## AD-04: Object Storage as THE Core Model — Translation Layer for Paths

**Decision:** Object/key-based storage is the default and only internal model. A translation layer maps file-path (URI) operations to object operations for users who want path-based access.

**Rationale:** The SDK currently has two competing storage abstractions:
- `IStorageProvider` (Uri-based, file-path model) — used by base classes, kernel
- `IStorageStrategy` (key-based, object model) — used by UltimateStoragePlugin, 130+ strategies

Advanced features (replication, tiering, RAID, dedup, AEDS, governance) all work on objects with rich metadata (ETag, tier, version, custom metadata). If some plugins use file paths and others use objects, the foundation doesn't support the features uniformly.

**Architecture:**
```
User-facing: Path API (URI)  ←→  Translation Layer  ←→  Object Storage Core (key-based)
                                                           ↓
                                                    All advanced features
                                                    (tiering, replication, RAID, etc.)
                                                           ↓
                                                    130+ backend strategies
```

Even FileSystem strategy stores objects — it maps `key="data/file.csv"` to a file at `{root}/data/file.csv` with metadata in a sidecar. The user sees files; the system sees objects.

---

## AD-05: Strategy Hierarchy — Flat, Simple, No Intelligence Layer

**Decision:** Strategy hierarchy is two levels deep: StrategyBase → DomainStrategyBase → ConcreteStrategy. No IntelligenceAwareStrategyBase. No DataPipelineStrategyBase.

### Key Principles:

**Plugin = Container/Orchestrator.** A collection of ways to do a similar thing.
**Strategy = Worker.** One specific way to do it.

**Strategies do NOT have:**
- ✗ Intelligence/AI integration (plugin handles this via IntelligenceAwarePluginBase)
- ✗ Capability registry (strategy IS a capability of its parent plugin)
- ✗ Knowledge bank access (plugin registers on behalf of strategy)
- ✗ Message bus access (plugin orchestrates messaging)
- ✗ Pipeline awareness (plugin handles pipeline context)

**Strategies DO have:**
- ✓ Lifecycle (InitializeAsync / ShutdownAsync)
- ✓ Dispose (IDisposable / IAsyncDisposable)
- ✓ Identity (Name, Description)
- ✓ Characteristics/metadata (key sizes, performance profile, hardware requirements)
- ✓ Structured logging hooks

### Capability Flow:
- Plugin registers strategy characteristics as plugin capabilities
- Strategy declares characteristics via metadata properties
- Plugin queries AI for strategy selection, passes decisions as options to strategy
- Strategy executes, returns results including operational metadata
- Plugin registers strategy's operational metadata as runtime knowledge

### Knowledge Flow:
- **Plugin knowledge (static):** "AES, Serpent available; AES disabled, Serpent enabled"
- **Strategy knowledge (runtime):** "Block X encrypted with Serpent-256, key ID abc"
- Strategy produces runtime knowledge as return values
- Plugin registers it in the knowledge bank

### Intelligence Flow:
- Plugin has AI socket via IntelligenceAwarePluginBase
- Plugin asks AI: "which strategy for this data?" → gets recommendation
- Plugin passes recommendation as options to strategy method
- Strategy executes with the given options (doesn't know AI was involved)
- Plugin collects strategy metrics, feeds back to AI for learning

### Target hierarchy:
```
IStrategy (interface — Name, Description, Characteristics)
└── StrategyBase (lifecycle, dispose, metadata, logging hooks)
    ├── EncryptionStrategyBase (encrypt/decrypt contract)
    ├── CompressionStrategyBase (compress/decompress contract)
    ├── StorageStrategyBase (store/retrieve/delete/list contract)
    ├── SecurityStrategyBase (authenticate/authorize contract)
    ├── KeyManagementStrategyBase (generate/rotate/store key contract)
    ├── ComplianceStrategyBase (audit/verify/report contract)
    ├── InterfaceStrategyBase (start/handle/stop contract)
    ├── ConnectorStrategyBase (connect/execute/disconnect + retry/pooling)
    ├── ComputeStrategyBase (execute/sandbox contract)
    ├── ObservabilityStrategyBase (metrics/trace/log/health contract)
    ├── ReplicationStrategyBase (replicate/sync/resolve contract)
    ├── MediaStrategyBase (transcode/extract contract)
    ├── StreamingStrategyBase (publish/subscribe contract)
    ├── FormatStrategyBase (serialize/deserialize contract)
    ├── TransitStrategyBase (transfer/receive contract)
    └── DataManagementStrategyBase (catalog/lineage/quality contract)
```

---

## AD-06: Dead Code Cleanup Policy

**Decision:** Remove truly dead code (classes, files, interfaces referenced by nothing). Keep future-ready interfaces for unreleased hardware/technology.

**Keep (future-ready):**
- Interfaces/bases for hardware not yet available (brain-reading encryption, quantum crypto, neuromorphic computing, DNA storage)
- Abstract contracts where the domain is defined but implementation awaits SDK releases
- Hardware-specific bases (RDMA, io_uring, NUMA, TPM2, HSM) — hardware exists but may not be present on every machine

**Remove (truly dead):**
- Classes/files with zero references (no inheritance, no instantiation, no import)
- Superseded implementations (logic reimplemented elsewhere)
- Duplicate code from copy-paste that was never cleaned up
- Obsolete specialized base classes whose logic moves to composable services (per AD-03)
- Test stubs, placeholder files, commented-out code blocks

**Verification:** Before removing any file, confirm:
1. Zero references in codebase (grep for class name, file name)
2. Not referenced in tests
3. Not a future-ready interface for unreleased technology
4. Logic is available elsewhere (if it was useful, confirm it's been extracted)

---

## AD-07: Base Class Consolidation Numbers

**Current state:**
- ~111+ plugin base classes (many unused by any Ultimate plugin)
- ~7 fragmented strategy bases (each independently implementing same boilerplate)
- ~1,500+ strategy classes across all plugins

**Target state:**
- ~15-20 domain plugin base classes (from AD-01 hierarchy)
- ~15-16 domain strategy bases (from AD-05 hierarchy)
- ~1,500 strategy classes (same count, but inheriting correct bases with less boilerplate)
- Specialized bases → composable services (per AD-03)

**Boilerplate eliminated:**
- ~1,000 lines of duplicated intelligence code removed from strategy bases
- ~50+ lines of capability/knowledge code removed per strategy (×1,500 strategies)
- Common lifecycle/dispose consolidated into StrategyBase once

---

## Summary Table

| ID | Decision | Impact |
|----|----------|--------|
| AD-01 | Two branches: DataPipeline + Feature | Clarifies plugin identity |
| AD-02 | Single encryption/compression base | Eliminates unnecessary split |
| AD-03 | Specialized bases → composable services | Unlocks composition for Ultimate plugins |
| AD-04 | Object storage core + translation layer | Uniform foundation for all features |
| AD-05 | Flat strategy hierarchy, no intelligence | Massive simplification (~1,000 lines removed) |
| AD-06 | Dead code cleanup (keep future-ready) | Cleaner codebase, less confusion |
| AD-07 | 111+ bases → ~15-20 domain bases | Maintainable hierarchy |

---

## AD-08: Zero Regression Policy — DO NOT LOSE IMPLEMENTED LOGIC

**Decision:** The v2.0 refactor MUST NOT lose ANY already-implemented logic, functionality, or behavior. Every line of production code that was painstakingly implemented across v1.0's 21 phases, 116 plans, 863 commits, and 1,110,244 lines of C# MUST be preserved or correctly migrated.

**Rationale:** v1.0 took 30 days of intensive implementation across 60 plugins with ~1,500 strategies covering encryption, compression, storage, RAID, security, compliance, interfaces, compute, formats, media, governance, AEDS, marketplace, app platform, WASM ecosystem, data transit, and more. Losing any of this through careless refactoring would be catastrophic.

**Mandatory Rules for ALL v2.0 Work:**

1. **VERIFY BEFORE MODIFY**: Before changing any file, read and understand what it does. Never assume code is unused without grep verification.
2. **EXTRACT, DON'T DELETE**: When moving logic from specialized bases to composable services (AD-03), the logic MUST be extracted into the new location FIRST, verified to compile and work, and ONLY THEN can the old location be deprecated.
3. **BEHAVIORAL EQUIVALENCE**: After any strategy migration (Phase 25b), the strategy MUST produce identical results for identical inputs. This is not optional — it is verified by tests.
4. **BASE CLASS MIGRATION ≠ LOGIC REMOVAL**: Changing a plugin's base class (Phase 27) means updating the inheritance chain. It does NOT mean removing the plugin's unique functionality. All 60 plugins must retain their full feature set.
5. **STRATEGY BOILERPLATE vs STRATEGY LOGIC**: When removing "boilerplate" from strategies (intelligence/capability/dispose code that moves to the plugin level), only remove code that is TRULY redundant. If a strategy has custom dispose logic (e.g., releasing hardware resources), that STAYS.
6. **COMPILE + TEST GATE**: Every plan execution MUST end with a successful `dotnet build` and `dotnet test`. If the build breaks or tests fail, the plan is NOT complete.
7. **NO SILENT REMOVALS**: Any file/class removal must be logged with justification. Dead code cleanup (Phase 28) happens AFTER migration is verified, not during.

**What "No Regression" Means Specifically:**
- All 1,039+ tests continue to pass at every phase boundary
- All 60 plugins compile and retain their full strategy catalogs
- All ~1,500 strategies produce identical results after migration
- All message bus subscriptions, capability registrations, and knowledge registrations still function
- All distributed features (AEDS, replication, RAID, transit) still operate correctly
- All interface protocols (REST, gRPC, GraphQL, SQL wire, WebSocket, etc.) still work
- All security features (encryption, access control, key management, compliance, zero trust) remain functional
- All data formats (columnar, graph, scientific, lakehouse, streaming, media) still parse and serialize correctly

**Verification Approach:**
- Phase 24/25: Build compiles, existing tests pass, adapter wrappers maintain backward compat
- Phase 25b: Each strategy domain migration verified before proceeding to next
- Phase 27: Each plugin batch verified individually
- Phase 28: Dead code removal only AFTER Phase 27 verifies everything works
- Phase 30: Final comprehensive regression test suite

---

## Summary Table

| ID | Decision | Impact |
|----|----------|--------|
| AD-01 | Two branches: DataPipeline + Feature | Clarifies plugin identity |
| AD-02 | Single encryption/compression base | Eliminates unnecessary split |
| AD-03 | Specialized bases → composable services | Unlocks composition for Ultimate plugins |
| AD-04 | Object storage core + translation layer | Uniform foundation for all features |
| AD-05 | Flat strategy hierarchy, no intelligence | Massive simplification (~1,000 lines removed) |
| AD-06 | Dead code cleanup (keep future-ready) | Cleaner codebase, less confusion |
| AD-07 | 111+ bases → ~15-20 domain bases | Maintainable hierarchy |
| AD-08 | ZERO REGRESSION — preserve all v1.0 logic | Protects 30 days of implementation work |

---
*Decided: 2026-02-12*
*Participants: User (architect), Claude (analysis)*
