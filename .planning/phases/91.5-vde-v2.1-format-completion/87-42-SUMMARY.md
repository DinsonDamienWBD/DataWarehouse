---
phase: 91.5-vde-v2.1-format-completion
plan: 87-42
subsystem: vde
tags: [progressive-rollout, ab-testing, module-management, xxhash64, inode, activemodules]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: ModuleManifest, ModuleId, ModuleDefinitions, ExtendedInode512 with per-inode ActiveModules bitmap

provides:
  - FeatureActivationConfig with FeatureRolloutPolicy records for per-bit rollout control
  - ProgressiveFeatureActivation engine with deterministic XxHash64 group assignment
  - RolloutStats monitoring struct for actual vs target rate measurement
  - Per-inode module bit computation guarded by volume ModuleManifest bitmap

affects:
  - vde-creator
  - vde-mount-pipeline
  - inode-allocation
  - module-activation

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "XxHash64 + inode number + seed for deterministic group assignment (0-99)"
    - "Volume ModuleManifest as gate: module can only activate if volume supports it"
    - "Rollout controls only affect new inodes; existing inodes are immutable"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/FeatureActivationConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/ProgressiveFeatureActivation.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/BlockExport/ExportConfig.cs

key-decisions:
  - "XxHash64 over FNV/MurmurHash: already used in StableHash.cs, consistent with project conventions"
  - "Volume ModuleManifest guard prevents activating modules not present at volume level"
  - "DateTimeOffset? time window in FeatureRolloutPolicy enables automatic sunset without code changes"
  - "IdentifyRollbackCandidates scans ActiveModules bitmaps directly for O(n) rollback detection"

patterns-established:
  - "Progressive rollout: policies enumerate bit positions, not ModuleId, for future-proofing"
  - "Experiment group hash: combine inodeNumber (8 bytes) + seed (4 bytes) + UTF-8 name bytes"

# Metrics
duration: 3min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-42: Progressive Feature Activation (VOPT-55) Summary

**Per-inode A/B testing engine using XxHash64 deterministic group assignment with configurable rollout percentages controlling the ActiveModules bitmap for new VDE inodes**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-02T13:47:54Z
- **Completed:** 2026-03-02T13:50:38Z
- **Tasks:** 1
- **Files modified:** 3

## Accomplishments

- `FeatureActivationConfig` with `FeatureRolloutPolicy` records: each policy maps a module bit position (0-63) to a rollout percentage, experiment name, enabled flag, and optional time window
- `ProgressiveFeatureActivation` engine: deterministically assigns inode numbers to groups 0-99 via XxHash64 of (inodeNumber + seed + experimentName), activates module bits in new inodes based on rollout threshold
- `RolloutStats` struct: samples inode populations to compare actual vs target activation rates for experiment monitoring
- Rollout control API: `SetRolloutPercentage`, `PauseRollout`, `CompleteRollout` all thread-safe via config mutation
- `IdentifyRollbackCandidates` returns inode numbers with the experiment module bit set for manual rollback targeting

## Task Commits

1. **Task 1: FeatureActivationConfig and ProgressiveFeatureActivation** - `a73e97d1` (feat)

**Plan metadata:** (pending final commit)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/FeatureActivationConfig.cs` - Rollout policy configuration (FeatureActivationConfig + FeatureRolloutPolicy)
- `DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/ProgressiveFeatureActivation.cs` - A/B testing engine (ProgressiveFeatureActivation + RolloutStats)
- `DataWarehouse.SDK/VirtualDiskEngine/BlockExport/ExportConfig.cs` - Pre-existing broken XML cref fixed

## Decisions Made

- Used `XxHash64` (already in `StableHash.cs`) for deterministic group assignment, mixing inode number + hash seed + UTF-8 experiment name into a 12+ byte buffer
- Volume `ModuleManifest` acts as a safety gate: a policy can only set a bit if the volume-level manifest also has that bit; prevents activating modules the volume was not formatted with
- Group range is 0-99 (100 buckets) to make `RolloutPercentage * 100` a direct integer threshold with no floating-point edge cases

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed pre-existing broken XML cref in ExportConfig.cs**
- **Found during:** Task 1 build verification
- **Issue:** `ExportConfig.cs` line 27 had `<see cref="ExportResult.SizeLimitReached"/>` referencing a class that does not exist in the codebase, causing a CS1574 build error that blocked verification
- **Fix:** Replaced the cref with a plain text reference `<c>SizeLimitReached</c>` flag
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/BlockExport/ExportConfig.cs`
- **Verification:** `dotnet build` succeeds with 0 errors, 0 warnings
- **Committed in:** `a73e97d1` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - pre-existing bug)
**Impact on plan:** Auto-fix was necessary to get a passing build. No scope creep.

## Issues Encountered

None - one pre-existing build error in unrelated file fixed inline.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- `ProgressiveFeatureActivation` is ready to be called by the inode allocation path (VdeCreator or equivalent) to compute the initial ActiveModules bitmap for new inodes
- Rollout policies need to be persisted to/from the SuperblockV2 or a dedicated config region (not part of this plan)

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
