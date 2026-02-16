# Phase 34: Federated Object Storage & Translation Layer

## Overview

Phase 34 builds a federated object storage layer that routes requests across multiple storage nodes using both object-language (UUID-based) and filepath-language (path-based) addressing, with permission-aware and location-aware routing, cross-node replication awareness, and a manifest/catalog service.

## Dependencies

- **Phase 32**: StorageAddress abstraction (universal addressing)
- **Phase 33**: Virtual Disk Engine (local storage backend)
- **Phase 29**: Raft consensus + SWIM membership (cluster coordination)
- **Existing**: UltimateAccessControl (permissions), IClusterMembership (node discovery)

## Requirements Coverage

| Requirement | Description | Plan |
|-------------|-------------|------|
| FOS-01 | Dual-Head Router | 34-01 |
| FOS-02 | UUID Object Addressing | 34-02 |
| FOS-03 | Permission-Aware Routing | 34-03 |
| FOS-04 | Location-Aware Routing | 34-04 |
| FOS-05 | Federation Orchestrator | 34-05 |
| FOS-06 | Manifest/Catalog Service | 34-06 |
| FOS-07 | Replication-Aware Reads | 34-07 |

## Execution Plans

### Wave 1: Foundations (Parallel)
- **34-01-PLAN.md** — Dual-head router + request classification engine
  - RequestLanguage enum, IRequestClassifier, DualHeadRouter
  - Pattern-based classification (UUID vs path)
  - O(1) classification via StorageAddress kind + pattern matching

- **34-02-PLAN.md** — UUID v7 addressing + object identity model
  - UuidGenerator (UUID v7 with time-ordering)
  - ObjectIdentity value type
  - UuidObjectAddress integration with StorageAddress

### Wave 2: Routing Intelligence (Depends on Wave 1)
- **34-03-PLAN.md** — Permission-aware routing (depends on 34-01)
  - IPermissionCache with bounded in-memory cache
  - PermissionAwareRouter decorator with deny-early pattern
  - Message bus integration with `accesscontrol.check`
  - Target: >95% cache hit rate

- **34-04-PLAN.md** — Location-aware routing (depends on 34-01)
  - NodeTopology metadata (rack/DC/region/lat-long/health)
  - RoutingPolicy enum (latency/throughput/cost optimized)
  - LocationAwareRouter with proximity scoring
  - ProximityCalculator with Haversine distance

### Wave 3: Cluster Management (Depends on Phase 29)
- **34-05-PLAN.md** — Federation orchestrator (depends on Phase 29 SWIM + Raft)
  - IFederationOrchestrator with node registration/heartbeat
  - ClusterTopology with Raft-backed consistency
  - Health monitoring with automatic score degradation
  - Split-brain prevention via Raft quorum

- **34-06-PLAN.md** — Manifest/catalog service (depends on Phase 29 Raft, 34-02, 34-05)
  - ManifestStateMachine (Raft state machine)
  - RaftBackedManifest with O(1) UUID lookups
  - ManifestCache (bounded 100K entries)
  - Batch lookup API + time-range queries via UUID v7

### Wave 4: Replication (Depends on Waves 2 & 3)
- **34-07-PLAN.md** — Replication-aware reads (depends on 34-04, 34-06)
  - ConsistencyLevel enum (eventual/bounded-staleness/strong)
  - LocationAwareReplicaSelector with proximity-based selection
  - ReplicationAwareRouter with automatic fallback
  - Fallback chain with timeout handling

## Wave Structure

```
Wave 1 (Parallel):
  ├─ 34-01 (Dual-head router)
  └─ 34-02 (UUID addressing)

Wave 2 (Parallel, after Wave 1):
  ├─ 34-03 (Permission routing) ← depends on 34-01
  └─ 34-04 (Location routing)   ← depends on 34-01

Wave 3 (Parallel, after Phase 29):
  ├─ 34-05 (Orchestrator)       ← depends on Phase 29
  └─ 34-06 (Manifest)            ← depends on Phase 29, 34-02, 34-05

Wave 4 (Sequential, after Waves 2 & 3):
  └─ 34-07 (Replication reads)  ← depends on 34-04, 34-06
```

## Namespace Structure

All code in `DataWarehouse.SDK/Federation/` namespace:

```
DataWarehouse.SDK/Federation/
├── Routing/              (34-01)
│   ├── IStorageRouter.cs
│   ├── DualHeadRouter.cs
│   ├── IRequestClassifier.cs
│   └── PatternBasedClassifier.cs
├── Addressing/           (34-02)
│   ├── IObjectIdentityProvider.cs
│   ├── UuidGenerator.cs
│   ├── ObjectIdentity.cs
│   └── UuidObjectAddress.cs
├── Authorization/        (34-03)
│   ├── IPermissionCache.cs
│   ├── PermissionAwareRouter.cs
│   └── InMemoryPermissionCache.cs
├── Topology/             (34-04)
│   ├── ITopologyProvider.cs
│   ├── NodeTopology.cs
│   ├── LocationAwareRouter.cs
│   └── ProximityCalculator.cs
├── Orchestration/        (34-05)
│   ├── IFederationOrchestrator.cs
│   ├── FederationOrchestrator.cs
│   ├── NodeRegistration.cs
│   └── ClusterTopology.cs
├── Catalog/              (34-06)
│   ├── IManifestService.cs
│   ├── RaftBackedManifest.cs
│   ├── ManifestStateMachine.cs
│   └── ManifestCache.cs
└── Replication/          (34-07)
    ├── ConsistencyLevel.cs
    ├── IReplicaSelector.cs
    ├── LocationAwareReplicaSelector.cs
    └── ReplicationAwareRouter.cs
```

## Key Design Decisions

1. **Dual-Head Router**: Supports both object-based (UUID) and path-based addressing for backward compatibility
2. **UUID v7**: Time-ordered UUIDs enable natural chronological sorting and time-range queries
3. **Raft for Manifest**: Linearizable consistency for UUID-to-location mappings prevents split-brain
4. **Permission Cache**: Bounded in-memory cache (10K entries) reduces message bus overhead
5. **Decorator Pattern**: All routing intelligence implemented as decorators over IStorageRouter
6. **Consistency Levels**: Eventual (lowest latency), Bounded-Staleness (configurable bound), Strong (Raft leader only)

## Success Criteria Summary

- **34-01**: Classify 100% of UUID requests as Object, 100% of path requests as FilePath
- **34-02**: UUID v7 generation with time-ordering, O(1) manifest lookup
- **34-03**: >95% permission cache hit rate, deny-early at router
- **34-04**: US-East client → US-East replica routing, unhealthy nodes excluded
- **34-05**: Add/remove node with automatic topology updates, split-brain prevented
- **34-06**: 100K UUID lookups/sec, manifest consistent across all nodes
- **34-07**: Eventual reads from nearest replica, strong reads from leader, automatic fallback

## Verification

All plans include:
- Build verification (`dotnet build` with zero errors)
- Specific file existence checks
- Key pattern greps (interface implementations, Raft integration, etc.)
- Observable truths (e.g., routing counters, cache statistics)

Integration tests in Phase 41 will verify:
- End-to-end 3-node cluster setup
- Object storage with 3x replication
- Cross-node routing with consistency level enforcement
- Failure scenarios (node failures, network partitions)

## Execution Order

1. Complete Phase 29 (Raft + SWIM) first
2. Execute Wave 1 (34-01, 34-02) in parallel
3. Execute Wave 2 (34-03, 34-04) in parallel after Wave 1
4. Execute Wave 3 (34-05, 34-06) in parallel after Phase 29 complete
5. Execute Wave 4 (34-07) after Waves 2 & 3

**Total Plans**: 7 execution plans
**Total Files Created**: ~30 new files across `DataWarehouse.SDK/Federation/`
**Dependencies**: Phase 29 (Raft + SWIM), Phase 32 (StorageAddress), Phase 33 (VDE)
**Next Phase**: Phase 35 (Hardware Accelerator & Hypervisor Integration)
