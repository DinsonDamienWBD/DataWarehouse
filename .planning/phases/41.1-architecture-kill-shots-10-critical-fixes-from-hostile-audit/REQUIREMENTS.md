# Phase 41.1: Architecture Kill Shots — 20 Critical Fixes

## Origin
6-persona hostile architecture audit (security auditor, distributed systems PhD, performance engineer, compliance officer, chaos engineer, adversarial tester) identified 10 "kill shots" — architectural issues that would cause production failures at scale. Subsequent deep analysis of plugin hierarchy, strategy hierarchy, and configuration system added 10 more critical items.

## Requirements

### Section A: Kill Shots from Hostile Audit (KS1-KS9)

#### KILLSHOT-01: Async Pipeline (Delete Sync Wrappers)
- **Problem:** Sync-over-async wrappers (`.Result`, `.GetAwaiter().GetResult()`) in pipeline paths cause threadpool starvation under load
- **Fix:** Delete all sync wrappers in pipeline hot paths. Pipeline calls async directly. Add `HasAccessAsync` to replace sync `HasAccess`
- **Scope:** SDK pipeline classes, plugin base classes with sync wrappers

#### KILLSHOT-02: Kernel DI Wiring (1-Line Fix)
- **Problem:** Plugins don't receive kernel services (IMessageBus, IStorageEngine, etc.) — `InjectKernelServices` never called
- **Fix:** Add `InjectKernelServices(plugin)` call in `RegisterPluginAsync` after plugin construction
- **Scope:** Kernel plugin registration path

#### KILLSHOT-03: Typed Message Handlers (Base Class Registration)
- **Problem:** Message handlers use untyped `object` casting. No compile-time safety. No handler discovery
- **Fix:** Add `RegisterHandler<TRequest, TResponse>()` in base class. Type-safe handler registration with automatic discovery
- **Scope:** IntelligenceAwarePluginBase, message bus infrastructure
- **Note:** KS4 (handler discovery) subsumed by KS3

#### KILLSHOT-05: Native Key Memory (NativeKeyHandle)
- **Problem:** Cryptographic keys stored as `byte[]` on managed heap — GC can copy/move them, leaving key material scattered in memory
- **Fix:** Create `NativeKeyHandle` using `NativeMemory.AllocZeroed`. Change `IKeyStore` to return `NativeKeyHandle` via DIM. Update SecurityPluginBase and EncryptionPluginBase
- **Scope:** SDK IKeyStore, NativeKeyHandle (new), SecurityPluginBase, EncryptionPluginBase, UltimateKeyManagement, UltimateEncryption

#### KILLSHOT-06+10: Tenant-Scoped Persistent Storage (DataManagementPluginBase)
- **Problem:** Data management plugins use flat `ConcurrentDictionary` — no tenant isolation, no persistence
- **Fix:** Add tenant-scoped persistent storage in `DataManagementPluginBase` using `ISecurityContext.TenantId`. All data management plugins inherit tenant isolation automatically
- **Scope:** DataManagementPluginBase (SDK), all inheriting data management plugins

#### KILLSHOT-07: Scalable Vector Clocks (DVV + Pruning)
- **Problem:** `VectorClock` grows unbounded with every node. No pruning. Impossible at 10M+ nodes
- **Fix:** Replace with Dotted Version Vectors (DVV) with cluster membership-based pruning. Mark old VectorClock `[Obsolete]`
- **Scope:** SDK ReplicationStrategy VectorClock, CrdtReplicationSync, UltimateReplication

#### KILLSHOT-08: Multi-Raft Consensus Consolidation
- **Problem:** Three scattered Raft implementations. Single-group Raft is O(N) per heartbeat
- **Fix:** Create ConsensusPluginBase + ResiliencePluginBase in SDK. New UltimateConsensus plugin with Multi-Raft (3-5 voters per group). Absorb standalone Raft plugin. UltimateResilience → ResiliencePluginBase
- **Scope:** New SDK bases, new UltimateConsensus plugin, UltimateResilience refactor, Raft deprecation

#### KILLSHOT-09: Lineage Default Implementations
- **Problem:** 13 of 18 lineage strategies return `Array.Empty<LineageNode>()`
- **Fix:** Default BFS traversal in `LineageStrategyBase` using `_nodes`/`_edges`. Strategies only override `TrackAsync`
- **Scope:** LineageStrategyBase, all stub strategies in UltimateDataLineage

### Section B: Plugin Hierarchy Optimization (FIX-10 through FIX-16)

#### FIX-10: Migrate Common Plugin Implementations Down
- **Problem:** Common implementations (SelectOptimalAlgorithmAsync, statistics tracking, health checks) duplicated across leaf base classes instead of being in lower ancestors
- **Fix:** Audit ALL common functions across FeaturePluginBase branches and DataPipelinePluginBase branches. Migrate each common implementation to the LOWEST possible base class (prefer PluginBase → IntelligenceAwarePluginBase → branch bases → leaf bases). Only truly unique domain logic stays in leaf bases
- **Scope:** PluginBase, IntelligenceAwarePluginBase, FeaturePluginBase, DataPipelinePluginBase, all leaf base classes
- **Specific:** SelectOptimalAlgorithmAsync duplicated in Compression+Encryption → promote to DataTransformationPluginBase

#### FIX-11: Delete 14 Legacy Base Classes (Clean Break)
- **Problem:** 14 legacy inner base classes in PluginBase.cs (lines 1162-3200+) — most lack [Obsolete], some bypass IntelligenceAwarePluginBase entirely
- **Fix:** Migrate any useful implementations into the v3.0 hierarchy, then COMPLETELY DELETE all 14 legacy classes. Clean break, not [Obsolete]
- **Legacy classes to delete:** DataTransformationPluginBase:1162, StorageProviderPluginBase:1254, SecurityProviderPluginBase:1349, InterfacePluginBase:1375, PipelinePluginBase:1394, ListableStoragePluginBase:1435, TieredStoragePluginBase:1455, CacheableStoragePluginBase:1487, IndexableStoragePluginBase:1722, ConsensusPluginBase:1986, ReplicationPluginBase:2051, AccessControlPluginBase:2093, EncryptionPluginBase:2114, CompressionPluginBase:2950, ContainerManagerPluginBase:3139
- **Scope:** PluginBase.cs legacy section, all plugins currently referencing legacy bases

#### FIX-12: Port Old EncryptionPluginBase Key Management
- **Problem:** Old EncryptionPluginBase (PluginBase.cs:2114) has rich key management (envelope encryption, key store registry, DEK generation, stats, audit log) that the new thin hierarchy EncryptionPluginBase lacks
- **Fix:** Port ALL useful key management logic from old EncryptionPluginBase into new `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/EncryptionPluginBase.cs`. Then old one gets deleted as part of FIX-11
- **Scope:** Old EncryptionPluginBase (PluginBase.cs), new EncryptionPluginBase (Hierarchy/DataPipeline/)

#### FIX-13: Port Legacy Storage Chain Features
- **Problem:** Old storage chain (Listable→Cacheable→Indexable) has caching/indexing features not in new StoragePluginBase
- **Fix:** Port useful caching and indexing infrastructure from legacy ListableStoragePluginBase, CacheableStoragePluginBase, IndexableStoragePluginBase into new StoragePluginBase or appropriate v3.0 base
- **Scope:** Legacy storage bases (PluginBase.cs), new StoragePluginBase (Hierarchy/DataPipeline/)

#### FIX-14: Fix Strategy Base Lifecycle Shadowing Bugs
- **Problem:** 4 strategy bases use `new` keyword to shadow parent lifecycle, breaking polymorphism
- **Fix:** Remove `new` keyword, use `override` instead. Fix:
  - DataMeshStrategy.cs — shadows parent lifecycle
  - DataLakeStrategy.cs — shadows parent lifecycle
  - ObservabilityStrategyBase.cs — shadows _initialized, _disposed
  - KeyStoreStrategyBase (IKeyStore.cs:931+) — shadows _initialized
- **Scope:** 4 strategy base classes in SDK

#### FIX-15: Enhance StrategyBase with Common Infrastructure
- **Problem:** ~1,500+ lines of duplicated infrastructure code across 19 strategy bases (statistics in 7, health checks in 5, retry in 2, init lock in 3)
- **Fix:** Promote common infrastructure into StrategyBase as protected opt-in methods:
  - `EnsureInitializedAsync()` — SemaphoreSlim double-check pattern (saves ~150 lines across 3 bases)
  - `IncrementCounter(name)` / `GetStatistics()` — Interlocked statistics tracking (saves ~1000 lines across 7 bases)
  - `ExecuteWithRetryAsync()` — configurable retry + backoff (saves ~200 lines across 2 bases)
  - `GetCachedHealthAsync()` — health check with TTL caching (saves ~250 lines across 5 bases)
  - `ThrowIfNotInitialized()` — guard method
- **Scope:** StrategyBase, then refactor all 19 domain strategy bases to use these

#### FIX-16: Fix Strategy Naming Inconsistencies
- **Problem:** StorageStrategyBase name collision (2 classes with same name in different namespaces). DisplayName vs StrategyName inconsistency across bases
- **Fix:** Resolve name collision. Standardize on single naming pattern across all strategy bases
- **Scope:** SDK strategy base classes

### Section C: Configuration & Deployment System (CFG-17 through CFG-20)

#### CFG-17: Unified Configuration Object with Presets
- **Problem:** No single configuration object. Settings scattered across KernelConfiguration, SecurityPolicySettings, EmbeddedConfiguration, etc.
- **Fix:** Create unified `DataWarehouseConfiguration` object that consolidates ALL configurable aspects. Define presets from "unsafe" (all security disabled, no encryption, no auth) through safety levels to "god-tier" (everything enabled: FIPS, quantum-safe, MFA, full audit, paranoid logging). Presets include:
  - `unsafe` — Zero security, maximum performance, dev/testing only
  - `minimal` — Basic auth, no encryption, no audit
  - `standard` — AES-256, RBAC, basic audit, TLS
  - `secure` — Quantum-safe crypto, MFA, full audit trail, encrypted-at-rest
  - `paranoid` — FIPS 140-3, HSM-backed keys, air-gap ready, tamper-proof logging
  - `god-tier` — Everything enabled, adaptive security, ML-based anomaly detection, self-healing
- **Scope:** New DataWarehouseConfiguration class in SDK, references from all plugin bases

#### CFG-18: XML/YAML Preset Files Embedded in Deployment
- **Problem:** No declarative configuration format for deployment
- **Fix:** Create XML/YAML preset templates embedded as resources in the deployment package. Each preset from CFG-17 has a corresponding template file. Users can supply custom XML/YAML to override any preset
- **Scope:** New configuration templates in SDK or shared project, serialization/deserialization

#### CFG-19: Hardware Probe Integration with Install Wizard
- **Problem:** Hardware probe exists (IHardwareProbe, WindowsHardwareProbe, LinuxHardwareProbe, MacOsHardwareProbe) but isn't integrated into deployment flow
- **Fix:** During CLI/GUI install mode: run hardware probe → detect available resources (CPU, RAM, disk, GPU, HSM, TPM, network) → intelligently select best-fit preset → present to user for approval/modification → apply
- **Scope:** CLI install flow, GUI install flow, hardware probe integration, preset selection logic

#### CFG-20: Per-Item AllowUserToOverride + Instance Injection
- **Problem:** No per-item configurability control. No mechanism to lock certain settings while allowing others to be changed at runtime
- **Fix:** Each configuration item has `AllowUserToOverride` flag (set by admin during deployment or by preset policy). Configuration object injected at instance level — all plugins, strategies, and kernel components receive their configuration from this single source. Runtime config changes respect AllowUserToOverride constraints
- **Scope:** Configuration model enhancement, injection into PluginBase/StrategyBase, runtime config change API

## Hierarchy Changes

### Plugin Hierarchy (v3.0 — delete old, keep only new)
```
PluginBase (enhanced: common implementations migrated down)
  └─ IntelligenceAwarePluginBase (enhanced: typed message handlers)
      ├─ FeaturePluginBase
      │   ├─ SecurityPluginBase → UltimateKeyManagement, UltimateCompliance, UltimateAccessControl, UltimateDataProtection
      │   ├─ InfrastructurePluginBase
      │   │   ├─ ResiliencePluginBase (NEW) → UltimateResilience
      │   │   └─ ConsensusPluginBase (NEW) → UltimateConsensus (NEW plugin)
      │   ├─ DataManagementPluginBase (enhanced: tenant-scoped storage) → all data management plugins
      │   ├─ ComputePluginBase, ObservabilityPluginBase, StreamingPluginBase, etc.
      │   └─ InterfacePluginBase, MediaPluginBase, FormatPluginBase, etc.
      └─ DataPipelinePluginBase
          ├─ StoragePluginBase (enhanced: port caching/indexing from legacy chain)
          ├─ ReplicationPluginBase
          ├─ IntegrityPluginBase
          ├─ DataTransitPluginBase
          └─ DataTransformationPluginBase (enhanced: SelectOptimalAlgorithmAsync)
              ├─ EncryptionPluginBase (enhanced: port key management from legacy)
              └─ CompressionPluginBase
```

### Strategy Hierarchy (flat topology preserved, StrategyBase enhanced)
```
StrategyBase (enhanced: EnsureInitializedAsync, statistics, retry, health, ThrowIfNotInitialized)
  ├─ All 19 domain strategy bases (refactored to use StrategyBase infrastructure)
  └─ Fix 4 lifecycle shadowing bugs, fix naming inconsistencies
```

## New Artifacts
- **UltimateConsensus plugin** — Multi-Raft consensus (absorbs Raft plugin)
- **NativeKeyHandle** — Unmanaged memory key handle
- **DottedVersionVector** — Bounded vector clock with pruning
- **DataWarehouseConfiguration** — Unified configuration object
- **Configuration preset templates** — XML/YAML for unsafe→god-tier

## Deleted Artifacts
- **14 legacy base classes** in PluginBase.cs (lines 1162-3200+) — DELETED (clean break)
- **DataWarehouse.Plugins.Raft** — Absorbed into UltimateConsensus

## Plan Structure (7 plans, 2 waves)

### Wave 1 (independent, foundational)
- **Plan 01:** KS2 (kernel DI) + KS1 (async pipeline)
- **Plan 02:** KS9 (lineage defaults) + KS6+10 (tenant storage)
- **Plan 03:** FIX-14/15/16 (strategy base fixes + StrategyBase enhancement)
- **Plan 04:** FIX-10/11/12/13 (plugin hierarchy optimization + legacy deletion)

### Wave 2 (depends on Wave 1)
- **Plan 05:** KS5 (NativeKeyHandle) + KS3 (typed handlers) — depends on 01,04
- **Plan 06:** KS7 (DVV) + KS8 (Multi-Raft consensus) — depends on 01
- **Plan 07:** CFG-17/18/19/20 (configuration system) — depends on 04

### Section D: Cross-Cutting Concerns

#### DOC-21: Living Documentation — Plugin Catalog & Architecture Diagrams
- **Problem:** After numerous iterations, codebase exploration is required to understand current state. Documentation drifts from reality
- **Fix:** Every plan in Phase 41.1 includes a mandatory final task to update:
  - `.planning/PLUGIN-CATALOG.md` — plugin hierarchy, base classes, strategies, message handlers
  - Architecture diagrams (hierarchy trees, data flow, message bus topology)
  - Block diagrams for any changed subsystems
- **Goal:** Once Phase 41.1 completes, documents reflect ground truth — no codebase exploration needed
- **Scope:** Applied to ALL 7 plans as a mandatory final task

#### CFG-18-ENHANCED: Complete Configuration File as Living System State
- **Problem:** Initial CFG-18 only embedded preset templates. Real need is a comprehensive, persistent, bidirectional configuration file
- **Enhancement over CFG-18:**
  - XML/YAML file contains **EVERY single setting** in DW (not just bootstrapping basics)
  - All settings neatly categorized and laid out in sensible, meaningful hierarchy
  - File is **loaded at every DW startup** — same config persists across runs
  - **Bidirectional:** Runtime config changes are **written back** to the file
  - **Full audit trail:** Every config change logged with: who, when, what setting, old value → new value
  - Analogy: Windows Answer File + Settings/Control Panel hybrid, but comprehensive
- **Audit Requirements:**
  - Separate `configuration-audit.log` (or stored in DW's own audit system)
  - Each entry: `{timestamp, user/system, setting_path, old_value, new_value, reason}`
  - Immutable append-only log (uses tamper-proof logging if enabled)
  - Queryable: "Show me all changes to Security.* in the last 7 days"
- **Scope:** ConfigurationSerializer (bidirectional), ConfigurationAuditLog (new), kernel startup flow, ConfigurationChangeApi (write-back + audit)

## Priority
All items are HIGH priority — required for hyperscale/global deployment (10M+ nodes) and production readiness.
