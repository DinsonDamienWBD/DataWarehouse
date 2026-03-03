---
phase: 91.5-vde-v2.1-format-completion
plan: 87-19
subsystem: VDE MVCC Vacuum
tags: [mvcc, vacuum, epoch-tracking, sla, worm, metrics]
dependency_graph:
  requires: ["87-65"]
  provides: ["EpochTracker", "EpochBasedVacuum", "VacuumConfig", "EpochExpiredException", "VacuumMetrics"]
  affects: ["MvccGarbageCollector", "MvccVersionStore", "MvccManager"]
tech_stack:
  added: []
  patterns:
    - "Epoch-based reader lease tracking with ConcurrentDictionary + Interlocked"
    - "EMA (alpha=0.3) rolling reclaim-rate metric"
    - "Aggressive vacuum mode: 5s interval, doubled MaxVersionsPerCycle above dead-space threshold"
    - "WORM exemption via InodeFlags.Worm (bit 2) in stored version payload byte 9"
    - "IAsyncDisposable background loop with CancellationTokenSource.CreateLinkedTokenSource"
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Mvcc/VacuumConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mvcc/EpochTracker.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mvcc/EpochBasedVacuum.cs
  modified: []
decisions:
  - "EpochTracker wraps MvccGarbageCollector rather than replacing it — preserves existing version chain index and free list"
  - "OldestActiveEpoch returns CurrentGlobalEpoch (not 0) when no readers are active, so vacuum safely reclaims all old versions"
  - "Physical snapshot materialization is signalled via MarkMaterialized but executed by the higher-level VDE layer; EpochBasedVacuum only records intent"
  - "WORM check inspects stored version data payload byte 9 (InodeFlags offset in InodeV2 layout) — avoids needing live inode table access"
  - "Dead-space ratio computed from (used - remaining) / used; remaining = max(0, used - totalReclaimed)"
metrics:
  duration: 5min
  completed: "2026-03-02"
  tasks_completed: 1
  files_created: 3
---

# Phase 91.5 Plan 87-19: Epoch-Based MVCC Vacuum Summary

**One-liner:** Epoch-based MVCC dead version GC with reader lease SLA enforcement (300s default), EpochExpiredException, WORM object exemption, long-running snapshot materialization signalling, aggressive mode at 30% dead-space ratio, and VacuumMetrics with EMA rolling reclaim rate.

## Objective

Implement VOPT-32: background MVCC vacuum that tracks reader epoch leases, prevents unbounded version chain growth, respects SLA timeouts, and never reclaims WORM-protected data.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | EpochTracker, VacuumConfig, and EpochBasedVacuum | 3d29dd9d | VacuumConfig.cs, EpochTracker.cs, EpochBasedVacuum.cs |

## What Was Built

### VacuumConfig
Configures all vacuum parameters:
- `SlaLeaseTimeout` (default 300s) — max epoch lease duration
- `VacuumInterval` (30s) and `MaxVersionsPerCycle` (2000)
- `WormExemptionEnabled` (true) — never vacuum WORM data
- `MaterializeLongRunningSnapshots` (true) + `MaterializationWarningThreshold` (240s)
- `DeadSpaceRatioThreshold` (0.3) — triggers aggressive mode

### EpochExpiredException
`InvalidOperationException` subclass with `ExpiredEpoch`, `Elapsed`, and `Limit` properties, raised when a reader's lease exceeds the SLA timeout without materialization.

### ReaderLease (readonly struct)
Captures `ReaderId`, `Epoch`, `AcquiredAt`, and `IsMaterialized` state for each active reader.

### EpochTracker
- `ConcurrentDictionary<long, ReaderLease>` of active leases
- `CurrentGlobalEpoch` — Interlocked.Read of monotonic counter
- `AdvanceEpoch()` — Interlocked.Increment, called by MvccManager on commit
- `AcquireReaderLease(readerId)` — registers at CurrentGlobalEpoch
- `ReleaseReaderLease(readerId)` — removes lease
- `OldestActiveEpoch` — min epoch across all leases; returns CurrentGlobalEpoch when empty
- `GetExpiredLeases(timeout)` — snapshot of leases older than timeout
- `MarkMaterialized(readerId)` — record-with pattern to update IsMaterialized
- `ExpireLease(readerId)` — forcibly remove

### EpochBasedVacuum (`IAsyncDisposable`)
Background loop (`RunAsync` / `StartAsync`):
1. `ProcessExpiredLeasesAsync`: for each expired lease → MarkMaterialized (if configured) → ExpireLease
2. `RunVacuumCycleAsync`: delegates to `MvccGarbageCollector.RunCycleAsync`; drains freed blocks; WORM-checks each block via `ReadVersionAsync` + InodeFlags byte 9; frees non-WORM blocks via `IBlockAllocator.FreeBlock`
3. Aggressive mode: if dead-space ratio > threshold → 5s interval, doubled MaxVersionsPerCycle
4. `UpdateMetrics`: EMA (alpha=0.3) rolling reclaim rate, full VacuumMetrics snapshot

### VacuumMetrics (readonly struct)
`DeadSpaceRatio`, `ReclaimRateBytesPerSecond`, `OldestLivingEpoch`, `TotalVersionsReclaimed`, `TotalBlocksFreed`, `ActiveReaderCount`, `ExpiredLeaseCount`, `LastVacuumTime`, `LastVacuumDuration`.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore`: **0 errors, 0 warnings**

## Deviations from Plan

None — plan executed exactly as written.

## Self-Check: PASSED

- [x] `DataWarehouse.SDK/VirtualDiskEngine/Mvcc/VacuumConfig.cs` — FOUND
- [x] `DataWarehouse.SDK/VirtualDiskEngine/Mvcc/EpochTracker.cs` — FOUND
- [x] `DataWarehouse.SDK/VirtualDiskEngine/Mvcc/EpochBasedVacuum.cs` — FOUND
- [x] Commit `3d29dd9d` — FOUND
