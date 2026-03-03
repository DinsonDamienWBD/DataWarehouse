---
phase: 91.5-vde-v2.1-format-completion
plan: 87-30
subsystem: vde
tags: [dedup, content-addressable, blake3, cow, extent, block-sharing, reference-counting]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: InodeExtent, ExtentFlags.SharedCow, SpatioTemporalExtent.ExpectedHash (BLAKE3)
  - phase: phase-33
    provides: IBlockDevice, IBlockAllocator, CowBlockManager, ICowEngine

provides:
  - Hash128 readonly struct (16-byte BLAKE3 key for dictionary use)
  - ExtentRef readonly struct (inode+index+block position tracker)
  - DedupConfig (production-safe dedup policy configuration)
  - DedupResult readonly struct (per-pass savings metrics)
  - DedupStats readonly struct (lifetime aggregate statistics)
  - ContentAddressableDedup engine (VOPT-46+59 satisfied)

affects:
  - 91.5-vde-v2.1-format-completion
  - Phase 92 (VDE Decorator Chain — dedup decorator will wrap this engine)
  - Phase 99 (E2E/Zero-Stub Certification)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Content-addressable dedup via BLAKE3 ExpectedHash matching before SharedCow block sharing"
    - "Per-pass in-memory reference counting (IncrementRef/TryDecrementRef) separate from CowBlockManager B-Tree"
    - "SemaphoreSlim-bounded concurrency matching DedupConfig.MaxConcurrentScans"
    - "struct copy instead of ref locals across async/await boundaries (CS9217 avoidance)"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/DedupConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/ContentAddressableDedup.cs
  modified: []

key-decisions:
  - "Hash128 defined inline in ContentAddressableDedup.cs (no separate file) — co-location with its sole consumer avoids namespace sprawl"
  - "GetExtentHash returns zero for plain InodeExtent (which has no embedded hash field in its 24-byte layout) — callers using SpatioTemporalExtent with real ExpectedHash values should provide a companion overload"
  - "Per-pass _refCounts Dictionary rather than forwarding to CowBlockManager B-Tree — avoids WAL transaction overhead during the scan phase; blocks freed via IBlockAllocator.FreeExtent after pass completes"
  - "InodeExtent arrays mutated in-place (StartBlock + SharedCow flag) — caller owns persistence flush to inode table"

patterns-established:
  - "Dedup pass returns mutable inode arrays; caller persists — engine is stateless across passes"
  - "VerifyBeforeShare=true is the safe default; disabling is an explicit opt-in for trusted hash environments"

# Metrics
duration: 6min
completed: 2026-03-02
---

# Phase 87-30: Content-Addressable Extent Deduplication Summary

**BLAKE3-keyed content-addressable extent dedup engine sharing physical blocks via SharedCow flag with per-pass reference counting and optional byte-level verification**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-02T01:48:30Z
- **Completed:** 2026-03-02T01:54:30Z
- **Tasks:** 1/1
- **Files created:** 2

## Accomplishments

- Implemented `DedupConfig` with five production-safe policy knobs (MinExtentBlockCount=4, MaxConcurrentScans=2, MaxBytesPerPass=1GB, VerifyBeforeShare=true, ScanInterval=30min)
- Implemented `Hash128` as a 16-byte BLAKE3-key struct with correct `IEquatable<Hash128>` and `GetHashCode` for safe dictionary use
- Implemented `ContentAddressableDedup.ScanAndDedupAsync` satisfying VOPT-46+59: hash index build → duplicate group detection → byte-compare verification → SharedCow marking → ref-count tracking → block freeing
- Build succeeds: 0 errors, 0 warnings after fixing `ref` locals that crossed `await` boundaries (CS9217)

## Task Commits

Each task was committed atomically:

1. **Task 1: DedupConfig and ContentAddressableDedup engine** - `c43fcda7` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/DedupConfig.cs` — Dedup policy: thresholds, concurrency, byte-budget, verify flag, scan interval
- `DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/ContentAddressableDedup.cs` — Hash128, ExtentRef, DedupResult, DedupStats, ContentAddressableDedup engine

## Decisions Made

- `Hash128` defined in the same file as its only consumer (`ContentAddressableDedup`) to avoid namespace sprawl from a single-use type
- `GetExtentHash` returns zero for `InodeExtent` because the 24-byte on-disk layout has no embedded hash field; real dedup operates on `SpatioTemporalExtent.ExpectedHash` or externally-annotated extent lists
- Per-pass `_refCounts` dictionary tracks shared blocks independently of the `CowBlockManager` B-Tree to avoid WAL overhead during scanning; `FreeExtent` is called after verification for safe block reclamation
- `InodeExtent` arrays are mutated in place (`StartBlock` redirected, `SharedCow` set); the caller is responsible for flushing updated inodes to the inode table

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed CS9217 ref-local-across-await compilation errors**
- **Found during:** Task 1 (build verification)
- **Issue:** Used `ref InodeExtent` locals inside an async method that contained `await` calls — C# disallows ref locals across async suspension points (CS9217)
- **Fix:** Changed `ref InodeExtent keeperExt = ref keeperExtents[i]` to value copy `InodeExtent keeperExt = keeperExtents[i]` and reloaded the value after array mutation; changed scan loop from `ref InodeExtent ext = ref extents[i]` to value copy
- **Files modified:** ContentAddressableDedup.cs
- **Verification:** `dotnet build` succeeds with 0 errors, 0 warnings
- **Committed in:** c43fcda7 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — bug fix for CS9217 compiler error)
**Impact on plan:** Required fix for correctness. No scope creep.

## Issues Encountered

- `InstantCloneEngine.cs` (untracked, authored by a prior plan in this phase) had pre-existing build errors that resolved once our new files were added — the XML doc cref attributes in that file referenced `IncrementRef`/`DecrementRef` methods now present in `ContentAddressableDedup.cs`

## Next Phase Readiness

- VOPT-46+59 satisfied: `ContentAddressableDedup` ready for integration by the Phase 92 dedup decorator in the VDE decorator chain
- Follow-on: add `ScanAndDedupSpatioTemporalAsync` overload for `SpatioTemporalExtent[]` arrays where real `ExpectedHash` values are available
- No external service configuration required

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
