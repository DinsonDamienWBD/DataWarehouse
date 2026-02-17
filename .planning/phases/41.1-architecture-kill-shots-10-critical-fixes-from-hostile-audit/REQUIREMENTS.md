# Phase 41.1: Architecture Kill Shots — 10 Critical Fixes from Hostile Audit

## Origin
6-persona hostile architecture audit (security auditor, distributed systems PhD, performance engineer, compliance officer, chaos engineer, adversarial tester) identified 10 "kill shots" — architectural issues that would cause production failures at scale.

## Requirements

### KILLSHOT-01: Async Pipeline (Delete Sync Wrappers)
- **Problem:** Sync-over-async wrappers (`.Result`, `.GetAwaiter().GetResult()`) in pipeline paths cause threadpool starvation under load
- **Fix:** Delete all sync wrappers in pipeline hot paths. Pipeline calls async directly. Add `HasAccessAsync` to replace sync `HasAccess`
- **Scope:** SDK pipeline classes, plugin base classes with sync wrappers

### KILLSHOT-02: Kernel DI Wiring (1-Line Fix)
- **Problem:** Plugins don't receive kernel services (IMessageBus, IStorageEngine, etc.) — `InjectKernelServices` never called
- **Fix:** Add `InjectKernelServices(plugin)` call in `RegisterPluginAsync` after plugin construction
- **Scope:** Kernel plugin registration path

### KILLSHOT-03: Typed Message Handlers (Base Class Registration)
- **Problem:** Message handlers use untyped `object` casting. No compile-time safety. No handler discovery
- **Fix:** Add `RegisterHandler<TRequest, TResponse>()` in base class. Type-safe handler registration with automatic discovery
- **Scope:** IntelligenceAwarePluginBase or FeaturePluginBase, message bus infrastructure

### KILLSHOT-04: (Subsumed by KS3)
- Handler discovery issues resolved by typed handler registration in KS3

### KILLSHOT-05: Native Key Memory (NativeKeyHandle)
- **Problem:** Cryptographic keys stored as `byte[]` on managed heap — GC can copy/move them, leaving key material scattered in memory
- **Fix:**
  - Create `NativeKeyHandle` class using `NativeMemory.AllocZeroed` — keys live outside managed heap entirely
  - Wraps `Span<byte>` via pointer. GC cannot touch native memory
  - Zero and free in Dispose
  - Pure approach: Change `IKeyStore` to return `NativeKeyHandle` instead of `byte[]`
  - Update `SecurityPluginBase` and `EncryptionPluginBase` in v3.0 hierarchy
  - Delete old hierarchy from `PluginBase.cs` (keep only v3.0 hierarchy in `Contracts/Hierarchy/`)
- **Scope:** SDK IKeyStore, NativeKeyHandle (new), SecurityPluginBase, EncryptionPluginBase, UltimateKeyManagement, UltimateEncryption

### KILLSHOT-06: Tenant-Scoped Persistent Storage (DataManagementPluginBase)
- **Problem:** Data management plugins use flat `ConcurrentDictionary` — no tenant isolation, no persistence, memory-only
- **Fix:** Add tenant-scoped persistent storage in `DataManagementPluginBase`. All inheriting plugins get tenant isolation automatically. Uses `ISecurityContext.TenantId` for scoping. Delegates actual persistence to storage subsystem
- **Scope:** DataManagementPluginBase (SDK), all data management plugins (UltimateDataCatalog, UltimateDataLineage, UltimateDataQuality, UltimateDataGovernance, etc.)

### KILLSHOT-07: Scalable Vector Clocks (DVV/ITC + Pruning)
- **Problem:** `VectorClock` uses `Dictionary<string, long>` that grows with every node. No pruning. At 10M+ nodes, each clock entry is unbounded
- **Fix:**
  - Replace with Dotted Version Vectors (DVV) or Interval Tree Clocks (ITC)
  - Add clock pruning tied to cluster membership (departed nodes get compacted)
  - Design for 10M+ node clusters (hyperscale/global deployment)
- **Scope:** SDK ReplicationStrategy VectorClock, CrdtReplicationSync, UltimateReplication EnhancedVectorClock

### KILLSHOT-08: Multi-Raft Consensus Consolidation
- **Problem:** Three scattered Raft implementations (SDK RaftConsensusEngine, standalone Raft plugin, UltimateResilience consensus strategies). Single-group Raft sends heartbeats to ALL peers — O(N) per heartbeat, impossible at 10K+ nodes
- **Fix:**
  - Create new `ConsensusPluginBase` in SDK hierarchy under `InfrastructurePluginBase`
  - Create new `ResiliencePluginBase` in SDK hierarchy under `InfrastructurePluginBase`
  - Split `UltimateResilience`: extract consensus strategies → new `UltimateConsensus` plugin
  - Absorb standalone Raft plugin into `UltimateConsensus`
  - Implement Multi-Raft: partition into consensus groups (3-5 voters per group)
  - Add Learner/Witness roles, Hierarchical consensus
  - Design for 10M+ node clusters
- **Scope:** New ConsensusPluginBase, new ResiliencePluginBase, new UltimateConsensus plugin, UltimateResilience refactor, SDK RaftConsensusEngine, standalone Raft plugin deletion

### KILLSHOT-09: Lineage Default Implementations
- **Problem:** 13 of 18 lineage strategies are stubs returning `Array.Empty<LineageNode>()`
- **Fix:**
  - Add default BFS traversal implementation in `LineageStrategyBase` (using existing `_nodes`/`_edges` infrastructure)
  - Add default persistence (delegate to storage subsystem)
  - Pattern: persist by default, override to discard
  - Strategies only need to override `TrackAsync` for their specific tracking logic
- **Scope:** LineageStrategyBase, all 13 stub strategies in UltimateDataLineage

### KILLSHOT-10: Tenant Isolation for All Plugins (Merged with KS6)
- **Problem:** Catalog, lineage, quality, governance plugins have flat global dictionaries with no tenant key
- **Fix:** Same as KS6 — fix in `DataManagementPluginBase` so ALL data management plugins inherit tenant-scoped storage automatically
- **Scope:** Covered by KILLSHOT-06

## Hierarchy Changes (v3.0 only — delete old hierarchy)

```
IntelligenceAwarePluginBase
  ├─ FeaturePluginBase
  │   ├─ SecurityPluginBase (UltimateKeyManagement, UltimateCompliance, UltimateAccessControl, UltimateDataProtection)
  │   ├─ InfrastructurePluginBase
  │   │   ├─ ResiliencePluginBase (NEW) → UltimateResilience
  │   │   └─ ConsensusPluginBase (NEW) → UltimateConsensus (NEW plugin)
  │   └─ DataManagementPluginBase (UltimateDataCatalog, UltimateDataLineage, etc.)
  └─ DataPipelinePluginBase
      └─ DataTransformationPluginBase
          └─ EncryptionPluginBase → UltimateEncryption
```

## New Plugin
- **UltimateConsensus** — Consolidates all consensus implementations (Raft, Paxos, PBFT, ZAB) as strategies. Multi-Raft by default. Extends `ConsensusPluginBase`

## Deleted Plugin
- **DataWarehouse.Plugins.Raft** — Absorbed into UltimateConsensus

## Priority
All items are HIGH priority — required for hyperscale/global deployment (10M+ nodes).
