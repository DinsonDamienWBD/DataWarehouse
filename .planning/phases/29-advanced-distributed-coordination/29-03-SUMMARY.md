---
phase: 29-advanced-distributed-coordination
plan: 03
subsystem: distributed-replication
tags: [crdt, conflict-resolution, multi-master, vector-clock, gossip]
dependency-graph:
  requires: [29-01 (gossip protocol for propagation), Phase 26 contracts (IReplicationSync)]
  provides: [CrdtReplicationSync, CrdtRegistry, SdkGCounter, SdkPNCounter, SdkLWWRegister, SdkORSet]
  affects: [Multi-master replication consumers]
tech-stack:
  added: []
  patterns: [CRDT merge semantics, VectorClock causality, epidemic propagation, STJ serialization]
key-files:
  created:
    - DataWarehouse.SDK/Infrastructure/Distributed/Replication/SdkCrdtTypes.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Replication/CrdtRegistry.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Replication/CrdtReplicationSync.cs
  modified: []
decisions:
  - Non-generic ICrdtType interface for runtime dispatch from CrdtRegistry (instead of ICrdtType<TSelf>)
  - CrdtRegistry made public (for DI), but CreateInstance/Deserialize kept internal (they return ICrdtType)
  - LWWRegister as default CRDT type when no key pattern matches
  - ORSet uses Guid-based unique tags per add operation for observed-remove semantics
metrics:
  duration: ~6 minutes
  completed: 2026-02-16
---

# Phase 29 Plan 03: CRDT Conflict Resolution with Multi-Master Replication Summary

Four SDK-level CRDT types with mathematically proven merge semantics plus IReplicationSync implementation with VectorClock causality tracking and epidemic gossip propagation.

## What Was Built

### SdkCrdtTypes (453 lines)
Four CRDT types implementing ICrdtType:
- **SdkGCounter**: Grow-only counter with per-node counts. Merge uses Math.Max (NOT sum) per node for idempotency
- **SdkPNCounter**: Positive-negative counter using two GCounters internally. Value = positive - negative
- **SdkLWWRegister**: Last-Writer-Wins register. Higher timestamp wins; equal timestamps use NodeId string comparison as deterministic tiebreaker
- **SdkORSet**: Observed-Remove Set with add-set/remove-set tag pairs. Element present iff it has add-tags not in remove-set. Each add generates unique tag (nodeId:Guid)
- All types: ConcurrentDictionary for thread safety, System.Text.Json serialization, commutative/associative/idempotent merge

### CrdtRegistry (122 lines)
Key-to-CRDT-type mapping:
- Exact key registration and prefix-based registration
- Longest prefix match wins
- DefaultType = SdkLWWRegister (most general)
- CreateInstance creates new CRDT by key; Deserialize reconstitutes from bytes

### CrdtReplicationSync (530 lines)
Full IReplicationSync implementation:
- **SyncAsync**: Batches items, serializes with VectorClock, spreads via IGossipProtocol.SpreadAsync
- **ResolveConflictAsync**: Uses CRDT merge for Merge strategy, timestamp-based for LatestWins, direct selection for LocalWins/RemoteWins
- **Background reception**: Subscribes to OnGossipReceived, deserializes incoming data, checks VectorClock causality
  - Remote HappensBefore local: skip (local is newer)
  - Local HappensBefore remote: replace with remote
  - Concurrent (neither happens-before): CRDT merge + VectorClock.Merge
- **Background propagation**: PeriodicTimer spreads recently modified items via gossip
- **Bounded store**: MaxStoredItems with LRU eviction by LastModified
- Uses existing SDK VectorClock (Increment, HappensBefore, Merge)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed accessibility mismatch between CrdtRegistry and CrdtReplicationSync**
- **Found during:** Task 2 build verification
- **Issue:** CrdtReplicationSync (public) took CrdtRegistry (internal) as constructor parameter
- **Fix:** Made CrdtRegistry public, kept ICrdtType-returning methods internal
- **Files modified:** CrdtRegistry.cs
- **Commit:** c3866ed

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- All 3 files exist at expected paths
- Four CRDT types implement ICrdtType with correct merge semantics
- GCounter uses Math.Max per node (verified not sum)
- CrdtReplicationSync implements IReplicationSync (SyncAsync, GetSyncStatusAsync, ResolveConflictAsync)
- VectorClock from SDK.Replication used for causality tracking
- using SyncResult alias follows InMemoryReplicationSync pattern

## Commits

| Commit | Description |
|--------|-------------|
| c3866ed | feat(29-03): CRDT conflict resolution with multi-master replication |
