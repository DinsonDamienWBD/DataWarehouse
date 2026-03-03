---
phase: 91.5-vde-v2.1-format-completion
plan: 87-31
subsystem: vde
tags: [instant-clone, copy-on-write, extent, SharedCow, ExtendedInode512, metadata-clone]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: ExtentAwareCowManager (VOPT-26), ExtendedInode512, InodeExtent/ExtentFlags
provides:
  - InstantCloneEngine: O(metadata) instant clone with SharedCow extent sharing
  - CloneOptions: configurable clone behavior (xattrs, permissions, timestamps, deep)
  - CloneResult / CowSplitResult: typed result structs with metrics
  - SplitOnWriteAsync: write-time CoW split allocating private blocks on first write
affects:
  - VdeMountPipeline
  - Write path (must call SplitOnWriteAsync before mutating SharedCow extents)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "O(metadata) clone: copy only ExtendedInode512 inode block + extent pointer array; mark SharedCow on both source and clone"
    - "Inner NullWal: no-op IWriteAheadLog satisfies ExtentAwareCowManager constructor without exposing WAL dependency to clone callers"
    - "ExtentAwareCowManager re-used for ref-count IncrementRef/DecrementRef (WAL-free paths)"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/CloneOptions.cs
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/InstantCloneEngine.cs
  modified: []

key-decisions:
  - "Store ExtendedInode512 in dedicated 1-block-per-inode layout; block number == inode number for direct O(1) lookup"
  - "Re-use ExtentAwareCowManager for extent reference counting rather than implementing a duplicate ref-count table"
  - "NullWal inner class avoids requiring an external WAL; only IncrementRef/DecrementRef used, both WAL-free"
  - "AllocateExtent used for SplitOnWriteAsync to ensure contiguous allocation of private copy"

patterns-established:
  - "Clone = copy metadata + mark SharedCow on both sides + IncrementRef per extent"
  - "Write = check SharedCow flag -> SplitOnWriteAsync -> update inode extent pointer"

# Metrics
duration: 12min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-31: Instant Clone Engine Summary

**O(metadata) instant clone via ExtendedInode512 extent pointer copy with SharedCow flags and lazy write-time CoW split using ExtentAwareCowManager ref counting**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-03-02T00:00:00Z
- **Completed:** 2026-03-02T00:12:00Z
- **Tasks:** 1/1
- **Files modified:** 2 (2 created)

## Accomplishments
- `CloneOptions` with four configuration knobs: `CloneExtendedAttributes`, `ClonePermissions`, `PreserveTimestamps`, `DeepClone`
- `InstantCloneEngine.CloneInodeAsync` copies only the `ExtendedInode512` inode block + extent pointer array (O(metadata)); sets `ExtentFlags.SharedCow` on all extents in both source and clone; increments ref counts via `ExtentAwareCowManager`
- `SplitOnWriteAsync` allocates new private blocks, copies data from shared blocks, clears `SharedCow`, decrements old ref count (freeing if reaching zero)
- `CloneResult` and `CowSplitResult` readonly record structs capture per-operation metrics
- Deep clone support via `IInodeTable.ReadDirectoryAsync` + recursive `CloneInodeAsync`

## Task Commits

1. **Task 1: CloneOptions and InstantCloneEngine** - `074dfd72` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/CloneOptions.cs` - Clone configuration options (4 bool properties with defaults)
- `DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/InstantCloneEngine.cs` - Instant clone engine with CloneInodeAsync, SplitOnWriteAsync, CloneResult, CowSplitResult, NullWal

## Decisions Made
- Re-used `ExtentAwareCowManager` for extent ref counting rather than adding a new ref-count mechanism; only the WAL-free `IncrementRef`/`DecrementRef` methods are called
- `NullWal` inner class satisfies the `IWriteAheadLog` constructor requirement of `ExtentAwareCowManager` without exposing WAL plumbing to clone callers; `InstantCloneEngine` callers wrap the whole operation in their own external WAL transaction
- `SplitOnWriteAsync` always copies the full extent (not partial-extent split) to keep the ref-count model simple; partial-extent splits are delegated to `ExtentAwareCowManager.CopyOnWriteAsync` if the write path needs finer granularity

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- `IWriteAheadLog` has 7 members beyond what was anticipated; added all to `NullWal` as no-op implementations (Rule 3 auto-fix, build blocking).
- `WalTransaction` internal constructor argument order was `(long transactionId, IWriteAheadLog wal)` not `(IWriteAheadLog, long)` — corrected after first build attempt.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- `InstantCloneEngine` is ready to be wired into the VDE write path (callers check `IsShared` on extents, call `SplitOnWriteAsync` before any in-place block mutation)
- `CloneInodeAsync` can be integrated into VdeMountPipeline or filesystem layer for user-facing clone commands
- Deep clone recursion uses `IInodeTable.ReadDirectoryAsync` which reads legacy `Inode` directory entries; callers using `ExtendedInode512`-only paths should confirm child inode numbers are consistent

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
