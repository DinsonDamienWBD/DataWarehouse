---
phase: 91.5-vde-v2.1-format-completion
plan: 87-32
subsystem: vde
tags: [epoch, retention, lazy-deletion, vacuum, time-series, trlr, superblock]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: RetentionEpochMapper (87-31), IBlockDevice, IBlockAllocator, UniversalBlockTrailer
provides:
  - EpochGatedLazyDeletion: O(1) bulk delete via OldestActiveEpoch advancement
  - LazyReclaimResult: incremental TRLR scan with resumable position
  - EpochAdvanceResult: epoch fence detail with estimated reclaim scope
  - EpochDeletionStats: vacuum metrics and scan progress snapshot
  - Retention-driven auto-advancement from registered retention policies
  - Continuous background vacuum loop (RunAsync)
affects: [phase-92, phase-95, phase-96, phase-99]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Epoch-gated deletion: advance OldestActiveEpoch to logically delete O(N) records in O(1) I/O"
    - "Incremental TRLR scan: circular scan with _lastScanPosition for resumable background vacuum"
    - "TRLR GenerationNumber as epoch tag: UniversalBlockTrailer.GenerationNumber encodes write epoch"
    - "Metadata block persistence: epoch state written to block 0 with 64-byte header layout"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Retention/EpochGatedLazyDeletion.cs
  modified: []

key-decisions:
  - "OldestActiveEpoch persisted to block 0 (metadata block) with 64-byte layout including magic 'EGLD'"
  - "UniversalBlockTrailer.GenerationNumber (uint32) serves as per-block epoch tag for TRLR scan"
  - "Scan position (_lastScanPosition) persisted to metadata block for cross-cycle resumability"
  - "CircularTRLR scan: wraps around device end, excludes block 0 (metadata), respects IsAllocated"
  - "RetentionEpochMapper.GetOldestRetentionEpoch returns -1 when no AutoDelete policies exist"

patterns-established:
  - "Epoch metadata block layout: magic[8] + OldestActiveEpoch[8] + CurrentEpoch[8] + scanPos[8] + stats[32]"
  - "AutoAdvanceFromRetentionAsync clamps to CurrentEpoch to prevent deleting future data"
  - "VacuumScanAsync skips non-allocated blocks via IBlockAllocator.IsAllocated for efficiency"

# Metrics
duration: 15min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-32: EpochGatedLazyDeletion Summary

**Epoch-gated bulk deletion that converts millions of time-series record deletes into a single OldestActiveEpoch superblock write, with incremental TRLR-scan background vacuum for lazy physical reclamation**

## Performance

- **Duration:** 15 min
- **Started:** 2026-03-02T00:00:00Z
- **Completed:** 2026-03-02T00:15:00Z
- **Tasks:** 1
- **Files modified:** 1 created

## Accomplishments

- EpochGatedLazyDeletion implements VOPT-49: single superblock write advances OldestActiveEpoch, logically deleting all pre-epoch blocks without touching each individually (O(1) vs O(N) I/O)
- VacuumScanAsync provides incremental TRLR-based lazy reclamation: reads UniversalBlockTrailer.GenerationNumber per block, frees those below OldestActiveEpoch, resumes from _lastScanPosition
- AutoAdvanceFromRetentionAsync wires RetentionEpochMapper policies into automatic epoch fence advancement, enabling hands-off time-series expiration for IoT/metrics/logs workloads
- RunAsync background loop orchestrates retention-advance + vacuum cycles at configurable intervals
- Metadata block layout (EGLD magic, 64 bytes on block 0) provides durable epoch state across mounts

## Task Commits

Each task was committed atomically:

1. **Task 1: RetentionEpochMapper and EpochGatedLazyDeletion** - `d0aa2d18` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Retention/EpochGatedLazyDeletion.cs` - Epoch-gated bulk deletion engine with O(1) advance, lazy vacuum scan, retention-driven auto-advance, background loop, and stats

## Decisions Made

- **Block 0 for metadata:** Epoch state (OldestActiveEpoch, CurrentEpoch, scan position, stats) is stored at block 0 using a 64-byte layout with "EGLD" magic. This avoids needing a new superblock field while providing durable persistence in a single block write.
- **TRLR GenerationNumber as epoch:** UniversalBlockTrailer.GenerationNumber (uint32) already records the write generation per block. We reuse it as the epoch tag for TRLR scanning — blocks with GenerationNumber < OldestActiveEpoch are logically dead.
- **Circular incremental scan:** The vacuum scan wraps around the device in a circular pattern, resuming from _lastScanPosition. This bounds per-cycle I/O to maxBlocksPerCycle blocks while ensuring the full device is covered over time.
- **Skip unallocated blocks:** VacuumScanAsync calls IBlockAllocator.IsAllocated(blockIndex) before reading each block, skipping free blocks to avoid reading garbage data.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None - build succeeded with 0 errors after linter auto-corrected a class-level XML doc comment in RetentionEpochMapper.cs (removed an invalid `<paramref>` reference in the remarks section).

## Next Phase Readiness

- EpochGatedLazyDeletion is ready for integration with VdeMountPipeline and time-series workload strategies
- RetentionEpochMapper + EpochGatedLazyDeletion together satisfy VOPT-49 (epoch-gated lazy deletion)
- Phase 92 (VDE Decorator Chain) can use EpochGatedLazyDeletion as part of the WAL decorator layer

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
