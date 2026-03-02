---
phase: 91.5-vde-v2.1-format-completion
plan: 45
subsystem: vde
tags: [preamble, bootable, bare-metal, vde-format, binary-serialization]

requires:
  - phase: 71
    provides: "VDE v2.0 format structures (FeatureFlags, FormatConstants, MagicSignature)"
provides:
  - "PreambleHeader 64-byte readonly struct with BinaryPrimitives serialization"
  - "VdeDetector for preamble-present vs v2.x vs legacy v1.x detection"
  - "PreambleReader with header checksum validation"
  - "PreambleWriter with automatic checksum computation"
  - "INCOMPAT_HAS_PREAMBLE bit 8 on IncompatibleFeatureFlags"
affects: [87-46, 87-47, 87-48, preamble-payload, boot-loader]

tech-stack:
  added: []
  patterns: ["SHA-256 truncated to 8 bytes as BLAKE3 fallback for header checksum", "static Serialize/Deserialize pattern for readonly structs"]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleHeader.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleReader.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleWriter.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/VdeDetector.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/FeatureFlags.cs

key-decisions:
  - "SHA-256 truncated to 8 bytes as BLAKE3 header checksum fallback (consistent with existing SDK pattern)"
  - "Content hash validation uses IncrementalHash for streaming payload reads"

patterns-established:
  - "Preamble detection: read 8 bytes, branch on DWVD-BOO magic vs DWVD superblock magic"
  - "WriteHeader computes checksum automatically — callers never set HeaderChecksum manually"

duration: 5min
completed: 2026-03-02
---

# Phase 91.5 Plan 45: Bootable Preamble Header and Detection Summary

**64-byte PreambleHeader struct with VDE file type detection, reader/writer pair, and INCOMPAT_HAS_PREAMBLE flag for bare-metal boot support (VOPT-61/62)**

## Performance

- **Duration:** 5 min
- **Started:** 2026-03-02T14:38:52Z
- **Completed:** 2026-03-02T14:44:02Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- PreambleHeader readonly struct with exact 64-byte on-disk layout, BinaryPrimitives serialization, and full IEquatable support
- VdeDetector that classifies files into 4 categories: PreamblePresent, V2PreambleFree, LegacyV1, NotDwvd
- PreambleReader with magic validation and SHA-256-based header checksum verification plus content hash validation
- PreambleWriter with automatic checksum computation and CreateHeader factory with 4KiB alignment enforcement
- INCOMPAT_HAS_PREAMBLE (bit 8) added to IncompatibleFeatureFlags enum

## Task Commits

Each task was committed atomically:

1. **Task 1: INCOMPAT_HAS_PREAMBLE flag and PreambleHeader struct** - `7bf0a7c0` (feat)
2. **Task 2: VdeDetector, PreambleReader, and PreambleWriter** - `590ecd3e` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Format/FeatureFlags.cs` - Added HasPreamble = 1 << 8 to IncompatibleFeatureFlags
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleHeader.cs` - 64-byte readonly struct with magic, payload offsets, checksum
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleReader.cs` - Header deserialization with checksum + content hash validation
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleWriter.cs` - Header serialization with auto-checksum and CreateHeader factory
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/VdeDetector.cs` - File type detection (preamble vs v2 vs v1 vs unknown)

## Decisions Made
- Used SHA-256 truncated to 8 bytes as BLAKE3 fallback for header checksum, consistent with existing SDK pattern in CrossExtentIntegrityChain and EpochBatchedMerkleUpdater
- Content hash validation uses IncrementalHash with streaming reads to avoid loading entire payloads into memory
- VdeDetector uses synchronous Detect(ReadOnlySpan<byte>) overload for span-based callers alongside async DetectAsync for stream-based callers

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PreambleHeader struct is ready for preamble payload writers (kernel, SPDK, runtime packing)
- VdeDetector can be integrated into VDE mount pipeline for automatic preamble detection
- All 4 VDE file type detection cases are covered

## Self-Check: PASSED

All 5 source files verified present. Both task commits (7bf0a7c0, 590ecd3e) verified in git log.

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
