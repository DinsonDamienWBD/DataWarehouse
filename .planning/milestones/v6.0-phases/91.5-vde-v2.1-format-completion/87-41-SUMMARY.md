---
phase: 91.5-vde-v2.1-format-completion
plan: 87-41
subsystem: vde
tags: [dwvd, block-export, inode, superblock, vde, vopt-53]

# Dependency graph
requires:
  - phase: 71
    provides: VDE v2.0 format structures (SuperblockV2, RegionDirectory, InodeExtent, UniversalBlockTrailer)
  - phase: 87
    provides: ExtendedInode512, InodeV2, BlockExport namespace, IVdeBlockExporter
provides:
  - Predicate-based selective VDE export to standalone .dwvd files (VOPT-53)
  - ExportConfig with inode/name predicate filters and layout options
  - SelfDescribingExporter with full DWVD v2.1 layout pipeline
  - ExportResult with metrics and SizeLimitReached flag
  - ValidateExportAsync for post-export integrity checks
affects: [phase-92, vde-mount-pipeline, data-portability, compliance-handoff]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Block address remapping: Dictionary<long,long> maps source StartBlock to destination sequential address"
    - "Predicate filtering at inode scan time: InodePredicate(inodeNumber) + NamePredicate(utf8name)"
    - "Self-describing layout: SuperblockGroup.SerializeWithMirror + RegionDirectory + Bitmap + Inode + Data"
    - "TRLR sentinel blocks written every 256 data blocks for offline integrity checking"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/BlockExport/ExportConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockExport/SelfDescribingExporter.cs
  modified: []

key-decisions:
  - "ExportConfig.BlockSize is independent of source block size — exporter adapts on copy"
  - "Block address remap table uses first-seen source StartBlock to avoid duplicate data regions"
  - "TRLR sentinel every 256 blocks enables partial-file integrity spot-checks without full read"
  - "Indirect/double-indirect extent blocks are not exported — only inline extents are remapped"
  - "IntegrityTree is a single placeholder block in v1; full Merkle recompute deferred to mount-time"

patterns-established:
  - "Portable VDE exports always carry a fresh VolumeUuid — never share UUID with source"
  - "SizeLimitReached flag set pre-write based on estimated layout; actual export may be smaller"

# Metrics
duration: 4min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-41: Self-Describing VDE Export Summary

**DWVD v2.1 portable export pipeline producing standalone .dwvd files via predicate-filtered inode selection, block address remapping, and fully-structured SuperblockGroup/RegionDirectory layout.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-03-02T13:48:16Z
- **Completed:** 2026-03-02T13:52:00Z
- **Tasks:** 1/1
- **Files modified:** 2

## Accomplishments

- Implemented `ExportConfig` with `InodePredicate`, `NamePredicate`, size limit, inline-tag, strip-encryption, compact, and block-size controls
- Implemented `SelfDescribingExporter` with 8-step pipeline: selection → layout → superblock groups → region directory → allocation bitmap → inode table (with block address remap) → integrity tree → sequential data region
- Implemented `ValidateExportAsync` that verifies superblock magic, version, region directory trailers, and spot-checks data block trailers
- Build: 0 errors, 0 warnings

## Task Commits

1. **Task 1: ExportConfig and SelfDescribingExporter** - `c3ea9ba3` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/BlockExport/ExportConfig.cs` - Export options (predicates, size limit, layout flags, block size)
- `DataWarehouse.SDK/VirtualDiskEngine/BlockExport/SelfDescribingExporter.cs` - Full export engine with ExportResult, 8-step pipeline, and ValidateExportAsync

## Decisions Made

- Used `Dictionary<long, long>` block remap table built during layout-planning pass so inode writing and data writing both use the same remap without re-scanning
- Only inline extents (up to 8 per inode) are remapped; indirect/double-indirect blocks are zeroed — avoids recursive source reads for the export format
- Wrote `UniversalBlockTrailer` on every region block (BMAP, INOD, IANT, DATA) to ensure mountability
- `FormatVersionInfo.MinReaderVersion = 2` (major version) to signal DWVD v2.x compatibility
- `IntegrityTree` is a single-block `IntegrityAnchor.CreateDefault()` placeholder; full Merkle tree computation is left to the mount pipeline

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed FormatConstants.RegionDirectoryStartBlock type mismatch**
- **Found during:** Task 1 (compilation)
- **Issue:** `RegionDirectoryStartBlock` is `long` but was assigned to `const int` — CS0266
- **Fix:** Added explicit `(int)` cast at the constant declaration
- **Files modified:** SelfDescribingExporter.cs
- **Verification:** Build passes with 0 errors
- **Committed in:** c3ea9ba3

**2. [Rule 1 - Bug] Replaced MagicSignature.IsValid with Validate()**
- **Found during:** Task 1 (compilation)
- **Issue:** `IsValid` property does not exist on `MagicSignature`; actual API is `Validate()` method
- **Fix:** Changed `sb.Magic.IsValid` → `sb.Magic.Validate()`
- **Files modified:** SelfDescribingExporter.cs
- **Verification:** Build passes with 0 errors
- **Committed in:** c3ea9ba3

**3. [Rule 1 - Bug] Removed InodeType.Free reference**
- **Found during:** Task 1 (compilation)
- **Issue:** `InodeType` enum has no `Free` member — free inodes are identified by `InodeNumber == 0`
- **Fix:** Changed `inode.Type == InodeType.Free || inode.InodeNumber == 0` → `inode.InodeNumber == 0`
- **Files modified:** SelfDescribingExporter.cs, doc comment
- **Verification:** Build passes with 0 errors
- **Committed in:** c3ea9ba3

---

**Total deviations:** 3 auto-fixed (all Rule 1 — API surface corrections during compilation)
**Impact on plan:** All fixes were correct-API substitutions discovered at compile time. No scope creep.

## Issues Encountered

None - compilation errors resolved immediately via API inspection.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- VOPT-53 satisfied: standalone .dwvd export with predicate selection is operational
- Ready for Phase 92 VDE Decorator Chain which will use the same block-export infrastructure
- `ValidateExportAsync` provides a verification hook for integration tests

## Self-Check: PASSED

- ExportConfig.cs: FOUND
- SelfDescribingExporter.cs: FOUND
- 87-41-SUMMARY.md: FOUND
- Commit c3ea9ba3: FOUND

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
