---
phase: 91.5-vde-v2.1-format-completion
plan: 87-23
subsystem: vde-format
tags: [vde, delt, vcdiff, delta-extents, copy-on-write, compaction, binary-patching]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: ExtendedInode512 layout with DataOsModuleArea and fixed-offset module slots
  - phase: 71-vde-v2-format
    provides: InodeExtent, FormatConstants, InodeV2 base types

provides:
  - DeltaExtentModule: 8B inode module at fixed offset 0x1F2 with MaxDeltaDepth/CurrentDepth/CompactionPolicy
  - DeltaExtentPatcher: VCDIFF-style binary patch generation, application, and delta chain flattening
  - DeltaCompactionPolicy enum (Eager/Lazy/OnRead/Manual) for background Vacuum policy control
  - ShouldUseDelta threshold check enabling CoW bypass for small writes (<10% block changed)

affects:
  - VdeMountPipeline (reads DELT bit 22 from ModuleManifest to activate patcher)
  - CoW write path (calls ShouldUseDelta before allocating new block)
  - Vacuum/compaction background task (reads NeedsCompaction to trigger FlattenChain)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - VCDIFF-style Copy/Insert op encoding with 14-byte header for sub-block binary patches
    - Threshold-based CoW bypass (default 10% changed bytes) for write amplification reduction
    - Readonly struct with init-only properties and record-with mutation for immutable module state

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/DeltaExtentModule.cs
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/DeltaExtentPatcher.cs
  modified: []

key-decisions:
  - "DeltaExtentModule layout: [MaxDeltaDepth:2][CurrentDepth:2][CompactionPolicy:4] = 8B at inode offset 0x1F2 (byte 498)"
  - "Patch header magic 0x44454C54 (ASCII DELT) + origSize + patchedSize + numOps; Copy ops store offset+length only; Insert ops store literal data inline"
  - "IsDeltaWorthwhile threshold set at 50% of blockSize (not 10%): 10% is the ShouldUseDelta trigger, 50% is patch-vs-block breakeven"
  - "NeedsCompaction only fires for Eager policy; Lazy/OnRead/Manual require explicit caller checks"
  - "FlattenChain is a semantic alias for ApplyDeltaChain to signal compaction intent to the background Vacuum caller"

patterns-established:
  - "Module bit constants: ModuleBitPosition as const byte, InodeOffset as const int, SerializedSize as const int"
  - "Static Serialize/Deserialize with in parameter modifier for zero-copy struct pass-through"
  - "WithXxx mutation helpers returning new struct via record-with for immutable state transitions"

# Metrics
duration: 3min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-23: DELT Module and VCDIFF Patcher Summary

**8-byte DeltaExtentModule inode entry at fixed offset 0x1F2 plus a full VCDIFF-style binary patch engine for sub-block delta extents that near-eliminates write amplification for small random writes**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-02T12:22:58Z
- **Completed:** 2026-03-02T12:25:38Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments

- `DeltaExtentModule` readonly struct: 8B at inode DataOsModuleArea offset 0x1F2, storing MaxDeltaDepth, CurrentDepth, and DeltaCompactionPolicy with full LE serialization
- `DeltaCompactionPolicy` enum: Eager (at MaxDeltaDepth), Lazy (at 2x MaxDeltaDepth), OnRead (lazy read-triggered), Manual (explicit only)
- `DeltaExtentPatcher` class: VCDIFF-style Copy/Insert op engine with `GeneratePatch`, `ApplyPatch`, `ApplyDeltaChain`, `FlattenChain`, and threshold-gated `ShouldUseDelta`
- VCDIFF header magic 0x44454C54 ("DELT") enables format self-identification and integrity validation on application
- Build verified: zero errors, zero warnings against DataWarehouse.SDK.csproj

## Task Commits

1. **Task 1: DeltaExtentModule and DeltaExtentPatcher** - `6e46667e` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Format/DeltaExtentModule.cs` — 8B inode module at offset 0x1F2 for DELT chain tracking with compaction policy, mutation helpers, static serialization
- `DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/DeltaExtentPatcher.cs` — VCDIFF-style patch engine: generate, apply, chain-apply, flatten, threshold check

## Decisions Made

- IsDeltaWorthwhile threshold is 50% of blockSize (patchSize < blockSize * 0.5): this is the breakeven where patching costs less than a full CoW block allocation. ShouldUseDelta (10% changed bytes) is a separate pre-generation gate.
- NeedsCompaction only fires for Eager policy; other policies require the caller (Vacuum, read path) to implement their own depth checks against MaxDeltaDepth.
- FlattenChain is a semantic alias over ApplyDeltaChain to clearly signal compaction intent in calling code.
- Copy ops store source offset within the original block; Insert ops store literal modified bytes inline. This matches RFC 3284 VCDIFF semantics without requiring a secondary compression pass.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- DELT module (bit 22) is ready for VdeMountPipeline to activate based on SuperblockV2.ModuleManifest
- CoW write path can call `ShouldUseDelta` before deciding whether to allocate a full new block or invoke `GeneratePatch`
- Background Vacuum can read `DeltaExtentModule.NeedsCompaction` and call `FlattenChain` to compact delta chains
- Delta chain reconstruction on read: load base block + ordered patch list, call `ApplyDeltaChain`

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
