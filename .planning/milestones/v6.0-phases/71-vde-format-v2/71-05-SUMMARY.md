---
phase: 71-vde-format-v2
plan: 05
subsystem: vde-format
tags: [inode, extent, variable-size, self-describing, binary-serialization]

requires:
  - phase: 71-01
    provides: "FormatConstants (InodeCoreSize, InodeAlignmentMultiple, MaxExtentsPerInode, ExtentSize)"
  - phase: 71-04
    provides: "ModuleRegistry, ModuleId, ModuleManifestField, VdeModule with InodeFieldBytes"

provides:
  - "InodeV2: variable-size inode (320-576B) with core fields + module extension area"
  - "InodeLayoutDescriptor: self-describing inode field layout for any module combination"
  - "InodeExtent: 24-byte extent descriptor with sparse/CoW/compression flags"
  - "InodeSizeCalculator: computes exact inode size from module manifest"

affects: [71-06, vde-io, vde-engine, inode-table-v2]

tech-stack:
  added: []
  patterns:
    - "Variable-size structs with descriptor-based field access"
    - "Self-describing binary layout for forward/backward compatibility"
    - "Module field offsets computed from bit-position order"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/InodeExtent.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/InodeSizeCalculator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/InodeV2.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/InodeLayoutDescriptor.cs
  modified: []

key-decisions:
  - "InodeV2 is a class (not struct) because variable size prevents value-type semantics"
  - "Module fields stored as raw byte[] blob interpreted via InodeLayoutDescriptor offsets"
  - "ModuleFieldEntry includes FieldVersion byte for in-place module field evolution"
  - "Core inode reserves 4 bytes at [300..304) for future use without migration"

patterns-established:
  - "Self-describing layout: InodeLayoutDescriptor + InodeV2 together enable any engine to parse inodes regardless of which modules it understands"
  - "Variable-size with alignment: raw size rounded up to 64-byte boundary, padding reserved for future module additions"

duration: 4min
completed: 2026-02-23
---

# Phase 71 Plan 05: Inode V2 Layout Summary

**Variable-size inode (320-576B) with self-describing InodeLayoutDescriptor, 8-extent addressing, and module-aware size calculator**

## Performance

- **Duration:** 4 min
- **Started:** 2026-02-23T11:54:09Z
- **Completed:** 2026-02-23T11:58:17Z
- **Tasks:** 2
- **Files created:** 4

## Accomplishments
- InodeExtent (24-byte) with sparse/CoW/compressed/encrypted flags and full serialize/deserialize
- InodeSizeCalculator matching all 4 spec examples: 320B (none), 512B (SEC+TAGS+REPL+INTL), 384B (SEC+CMPL+RAID+CMPR+QURY), 576B (all 19)
- InodeV2 class with 304-byte core (inode number, type, flags, permissions, timestamps, 8 extents, indirect blocks) plus variable module field area
- InodeLayoutDescriptor with 7-byte ModuleFieldEntry records enabling self-describing inode parsing
- Convenience accessors for Security (key slot, ACL, content hash), Tags (inline tag area 128B), Replication, RAID, Streaming module fields

## Task Commits

Each task was committed atomically:

1. **Task 1: Extent structure and inode size calculator** - `63278b81` (feat)
2. **Task 2: InodeV2 structure and InodeLayoutDescriptor** - `00cf62ac` (feat)

## Files Created
- `DataWarehouse.SDK/VirtualDiskEngine/Format/InodeExtent.cs` - 24-byte extent descriptor (StartBlock, BlockCount, Flags, LogicalOffset) with sparse/CoW support
- `DataWarehouse.SDK/VirtualDiskEngine/Format/InodeSizeCalculator.cs` - Computes inode size from module manifest with alignment and per-module layout
- `DataWarehouse.SDK/VirtualDiskEngine/Format/InodeLayoutDescriptor.cs` - Self-describing layout with ModuleFieldEntry array (7 bytes each)
- `DataWarehouse.SDK/VirtualDiskEngine/Format/InodeV2.cs` - Variable-size inode with core fields, module field blob, and typed accessors

## Decisions Made
- InodeV2 is a class (not struct) because its variable size and mutable module field data prevent value-type semantics
- Module fields stored as raw byte[] blob interpreted via descriptor offsets, allowing engines to skip unknown modules
- ModuleFieldEntry includes a FieldVersion byte enabling in-place module field format evolution without full inode migration
- Core inode has 4 reserved bytes at [300..304) for future additions without migration

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- All 4 inode files compile cleanly (zero errors, zero warnings)
- Inode size calculation verified against all 4 spec examples
- InodeLayoutDescriptor.Create() ready for superblock storage
- InodeV2 serialize/deserialize ready for inode table I/O
- Plan 71-06 can proceed with remaining VDE format structures

---
*Phase: 71-vde-format-v2*
*Completed: 2026-02-23*
