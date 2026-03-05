---
phase: 91.5-vde-v2.1-format-completion
plan: 87-53
subsystem: vde
tags: [opjr, operation-journal, online-resize, crash-recovery, state-machine, vde-v2.1]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: BlockTypeTags.OPJR (0x4F504A52) and UniversalBlockTrailer for block serialisation
provides:
  - OperationEntry 128-byte struct with 10 operation types and 6-state machine
  - OperationJournalRegion crash-recoverable OPJR region with header, entry CRUD, serialise/deserialise
  - OnlineResizeManager implementing the VOPT-75 resize protocol with 4096-block checkpoints
affects: [vde-mount-pipeline, online-operations, crash-recovery, resize-protocol]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "OPJR region pattern: 64-byte header + variable 128-byte entries, OPJR-tagged blocks with UniversalBlockTrailer"
    - "State machine enforcement: no outgoing transitions from terminal states (Completed/Failed/Cancelled)"
    - "Checkpoint protocol: caller checkpoints every N units, crash recovery resumes from CompletedUnits"
    - "Payload encoding: operation-specific parameters in 32-byte OperationPayload field (e.g. newTotalBlocks at offset 0)"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationEntry.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationJournalRegion.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Operations/OnlineResizeManager.cs
  modified: []

key-decisions:
  - "OperationEntry uses readonly struct (not class) for zero-allocation serialisation round-trips"
  - "OPJR private constructor pattern allows Deserialize to reconstruct full internal state without exposing mutable setters"
  - "OnlineResizeManager.BeginResize immediately transitions Queued->InProgress so a crash after OPJR write but before Superblock write is detectable on recovery"
  - "ResizeOperation/ResizeCheckpoint/ResizeRecoveryInfo use readonly record struct for value semantics and deconstruction support"

patterns-established:
  - "Operations directory under VirtualDiskEngine: new home for long-running operation management types"
  - "CheckpointIntervalBlocks = 4096 blocks = 16 MiB max re-work per spec VOPT-75"

# Metrics
duration: 6min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-53: OPJR Operation Journal and Online Resize Manager Summary

**128-byte operation journal entries (OPJR) with crash-recoverable state machine and 4096-block checkpoint online resize protocol satisfying VOPT-74 and VOPT-75**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-02T14:06:25Z
- **Completed:** 2026-03-02T14:12:21Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- OPJR region header (magic 0x4F504A524E4C0000, ActiveOperations, TotalOperations, OldestActiveId, NewestId, LastCheckpointUtc) with UniversalBlockTrailer serialisation
- OperationEntry readonly struct: exactly 128 bytes, 10 operation types (Resize through Rebalance), 6 states with terminal-guard, 32-byte operation payload for type-specific parameters
- OnlineResizeManager: BeginResize (OPJR entry + Superblock fields), ProcessBlocks (4096-block checkpoints), CompleteResize, CheckForPendingResize for crash recovery

## Task Commits

Each task was committed atomically:

1. **Task 1: OperationEntry struct and OperationJournalRegion** - `ad1c47ba` (feat)
2. **Task 2: OnlineResizeManager with OPJR integration** - `eda3f43e` (feat)

**Plan metadata:** (pending final commit)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationEntry.cs` - OperationType/OperationState/OperationFlags enums, OperationStateHelper, OperationEntry 128-byte readonly struct with WriteTo/ReadFrom
- `DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationJournalRegion.cs` - OPJR region: header, 64-byte layout, entry management, Serialize/Deserialize with block trailers
- `DataWarehouse.SDK/VirtualDiskEngine/Operations/OnlineResizeManager.cs` - Resize protocol manager with ResizeOperation/ResizeCheckpoint/ResizeRecoveryInfo value types

## Decisions Made

- OperationEntry uses `readonly struct` to allow stack allocation and avoid heap pressure during frequent progress scans; the 32-byte `OperationPayload` array is the only heap allocation per entry
- OPJR private constructor with all fields allows `Deserialize` to fully reconstruct internal state while keeping public properties read-only
- `BeginResize` immediately moves the entry from `Queued` to `InProgress` before returning, ensuring that if the engine crashes after the OPJR write but before the Superblock write, `CheckForPendingResize` will still detect and offer to resume the operation (worst case: re-do the operation from scratch since Superblock was never updated)
- `ProcessBlocks` checkpoints at `blocksProcessedSoFar % 4096 == 0` (not `!= 0` guard), meaning the first checkpoint fires at exactly block 4096 per spec

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Replaced object-initializer on sealed class with private constructor**
- **Found during:** Task 1 (OperationJournalRegion implementation)
- **Issue:** Initial `Deserialize` implementation used C# object initializer syntax to set private backing fields, which is invalid — object initializers can only set public properties or fields
- **Fix:** Introduced a private constructor accepting all state fields; `Deserialize` calls it directly
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationJournalRegion.cs
- **Verification:** Build succeeds with 0 errors 0 warnings
- **Committed in:** ad1c47ba (Task 1 commit)

**2. [Rule 1 - Bug] Replaced explicit ArgumentOutOfRangeException throws with ThrowIfLessThanOrEqual**
- **Found during:** Task 1 build
- **Issue:** CA1512 analyzer error: project enforces use of `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual` helper (linter auto-fixed one instance; remaining instances fixed manually)
- **Fix:** Replaced two `if (x <= y) throw new ArgumentOutOfRangeException(...)` patterns with `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(x, y)`
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationJournalRegion.cs
- **Verification:** Build succeeds with 0 errors 0 warnings
- **Committed in:** ad1c47ba (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (2 Rule 1 bugs)
**Impact on plan:** Both fixes required for compilation. No scope creep.

## Issues Encountered

None beyond the auto-fixed deviations above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- OPJR region ready for use by VdeMountPipeline (Phase 92) to detect and resume in-progress operations after crash
- OnlineResizeManager ready for integration with SuperblockV2 PendingTotalBlocks and ResizeJournalOperationId fields
- Pattern established for future operation types (RaidMigrate, Encrypt, etc.) to use the same OPJR journal infrastructure

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
