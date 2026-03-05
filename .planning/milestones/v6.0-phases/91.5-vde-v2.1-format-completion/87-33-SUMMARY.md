---
phase: 91.5-vde-v2.1-format-completion
plan: 87-33
subsystem: vde-allocation
tags: [heat-tiering, allocation, migration, vde, block-device, tiering, iops-budget]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: AllocationGroup, IBlockAllocator, IBlockDevice, RegionPointerTable with ShardId

provides:
  - HeatTier enum (Hot/Warm/Cold/Frozen) for extent classification
  - TieringConfig with access-frequency thresholds and per-tier shard mappings
  - HeatRecord struct for per-extent access tracking
  - MigrationResult struct summarising background migration outcomes
  - HeatDrivenTieringAllocator with RecordAccess, ClassifyExtent, AllocateWithHeatHint
  - Background migration cycle RunMigrationCycleAsync respecting I/O budget
  - RunContinuousAsync for periodic heat-scan and migrate loops

affects: [vde-decorator-chain, vde-mount-pipeline, phase-92, workload-profile]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Heat-tier classification uses access frequency (accesses/hour) and staleness (hours without access)"
    - "async ref param workaround: return updated value instead of ref parameter in Task-returning methods"
    - "MigrationCandidate uses internal visibility to avoid file-local type leaking into public signatures"
    - "Background loop: RunContinuousAsync catches all non-cancellation exceptions to keep loop alive"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Allocation/TieringConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Allocation/HeatDrivenTieringAllocator.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Mvcc/TemporalPointQueryEngine.cs

key-decisions:
  - "Converted async ref params to return-value pattern (Task<long>) to comply with CS1988"
  - "MigrationCandidate marked internal (not file-local) to avoid CS9051 in private method signatures"
  - "AllocateWithHeatHint falls back to primary IBlockAllocator when no shard group is registered for the hint tier"
  - "HeatTier.Warm used as default for unknown extents (conservative — not hot, not cold)"
  - "Priority formula: hot misplacements scored 100 + freq_boost; cold/frozen scored by staleness"

patterns-established:
  - "RecordExtentSize must be called alongside RecordAccess for accurate migration cost accounting"
  - "RegisterShardGroup wires physical AllocationGroup instances to logical ShardIds from TieringConfig.TierToShardId"

# Metrics
duration: 12min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-33: Heat-Driven Allocation Tiering Summary

**HeatDrivenTieringAllocator classifying extents into Hot/Warm/Cold/Frozen tiers via access-frequency tracking and migrating misplaced extents between shard-mapped AllocationGroups within a configurable I/O budget**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-03-02T00:00:00Z
- **Completed:** 2026-03-02T00:12:00Z
- **Tasks:** 1
- **Files modified:** 3 (2 created, 1 pre-existing fixed)

## Accomplishments

- `TieringConfig` with `HeatTier` enum, configurable access thresholds (hot: ≥10/hr, cold: 24h idle, frozen: 30d idle), 100 MB/s migration budget, 64 max migrations/cycle, and `TierToShardId` dictionary mapping tiers to physical shards
- `HeatDrivenTieringAllocator` with per-extent heat tracking (`ConcurrentDictionary<long, HeatRecord>`), tier classification, priority-sorted background migration, and hint-aware allocation via `AllocateWithHeatHint`
- Build fixed: pre-existing `TemporalPointQueryEngine.cs` CS1988/CS0246 errors resolved (async ref params + Snapshot type ambiguity)

## Task Commits

Each task was committed atomically:

1. **Task 1: TieringConfig and HeatDrivenTieringAllocator** - `16ce1065` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Allocation/TieringConfig.cs` - HeatTier enum and TieringConfig with thresholds, budget, shard mappings
- `DataWarehouse.SDK/VirtualDiskEngine/Allocation/HeatDrivenTieringAllocator.cs` - HeatRecord, MigrationResult, MigrationCandidate, HeatDrivenTieringAllocator
- `DataWarehouse.SDK/VirtualDiskEngine/Mvcc/TemporalPointQueryEngine.cs` - Fixed CS1988 async ref params + CS0246 Snapshot type ambiguity (pre-existing bug)

## Decisions Made

- Used `internal` (not `file`-local) for `MigrationCandidate` to avoid CS9051 when the type appears in private method return types
- Async `ref long logicalOffset` converted to return-value pattern (`Task<long>`) — C# disallows ref params in async methods
- `Snapshot` type replaced with `VdeSnapshot` alias already defined at top of file but not applied to all usages

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed pre-existing TemporalPointQueryEngine compilation errors**
- **Found during:** Task 1 (build verification)
- **Issue:** `TemporalPointQueryEngine.cs` had 4 pre-existing errors: CS1988 (async methods with ref params on lines 504/534) and CS0246 (`Snapshot` type not found on lines 565/594). The file already had `VdeSnapshot` alias defined but never applied.
- **Fix:** Converted `ref long logicalOffset` async params to return-value pattern (`Task<long>`); replaced all bare `Snapshot` and `IReadOnlyList<Snapshot>` with `VdeSnapshot`/`IReadOnlyList<VdeSnapshot>` using the pre-existing alias
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/Mvcc/TemporalPointQueryEngine.cs`
- **Verification:** Build succeeded with 0 errors
- **Committed in:** `16ce1065` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - pre-existing bug)
**Impact on plan:** Required for build to succeed; no scope creep.

## Issues Encountered

None beyond the pre-existing compilation errors documented above.

## Next Phase Readiness

- VOPT-51 satisfied: heat-driven tiering foundation with background migration is complete
- Callers should invoke `RegisterShardGroup` to wire AllocationGroups to ShardIds before use
- `RecordAccess` and `RecordExtentSize` should be called from the VDE read/write path to populate heat data
- Plan 87-34 (TemporalPointQueryEngine) can now build cleanly

## Self-Check: PASSED

- TieringConfig.cs: FOUND
- HeatDrivenTieringAllocator.cs: FOUND
- Commit 16ce1065: FOUND
- Build: 0 errors

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
