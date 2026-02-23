---
phase: 71-vde-format-v2
plan: 01
subsystem: VDE Format v2.0
tags: [vde, format, constants, magic-signature, feature-flags, block-types]
dependency_graph:
  requires: []
  provides:
    - FormatConstants (version, block, superblock, region, inode, module constants)
    - MagicSignature (16-byte file identity with serialize/deserialize/validate)
    - IncompatibleFeatureFlags, ReadOnlyCompatibleFeatureFlags, CompatibleFeatureFlags
    - FormatVersionInfo (version gating with serialize/deserialize)
    - BlockTypeTags (28 uint32 constants with string conversion utilities)
  affects:
    - All subsequent VDE v2.0 plans (superblock, region directory, inode, modules)
tech_stack:
  added: []
  patterns:
    - Readonly structs with BinaryPrimitives for zero-allocation serialization
    - FrozenSet for O(1) known-tag lookup
    - Span-based serialize/deserialize avoiding heap allocations
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/FormatConstants.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/MagicSignature.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/FeatureFlags.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/BlockTypeTags.cs
  modified: []
decisions:
  - "NamespaceAnchor stored as ulong (8 bytes) to avoid heap allocation for 5+3 byte field"
  - "Block type tags use big-endian encoding (first ASCII char in MSB) matching on-disk format"
  - "28 block type tags defined (22 core + 6 module registry extensions from spec)"
  - "FormatVersionInfo serialized size is 16 bytes (2+2+4+4+4) for alignment"
metrics:
  duration: 4min
  completed: 2026-02-23T11:38:43Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 4
  files_modified: 0
---

# Phase 71 Plan 01: VDE v2.0 Format Constants and Identity Summary

Foundational DWVD v2.0 format identity: 16-byte magic signature, three-tier feature flags with version gating, and 28 block type tags as uint32 constants with FrozenSet lookup.

## What Was Built

### FormatConstants
Static class containing all v2.0 numeric constants: version (major=2, minor=0, rev=1), block sizes (512-65536), superblock layout (4-block groups, mirror at block 4), region directory (block 8, 2 blocks, 127 slots), encryption (63 key slots), inode geometry (304-byte core, 64-byte alignment, 8 extents, 128-byte tag area), and module limits (32 max, 19 defined).

### MagicSignature
Readonly struct representing the 16-byte file header. Layout: bytes 0-3 "DWVD" (big-endian uint32), byte 4 major version, byte 5 minor version, bytes 6-7 spec revision (LE), bytes 8-12 "dw://", bytes 13-15 zero padding. Uses BinaryPrimitives for endianness-correct serialization. Validate() checks both the DWVD identifier and dw:// namespace anchor. IsCompatible() does major.minor version comparison.

### Feature Flags
Three [Flags] enums covering the ext4-style compatibility model:
- **IncompatibleFeatureFlags** (6 flags): encryption, WORM, RAID, compute cache, fabric links, consensus log
- **ReadOnlyCompatibleFeatureFlags** (5 flags): compression, replication, streaming, snapshots, intelligence cache
- **CompatibleFeatureFlags** (7 flags): tag index, policy engine, tamper-proof chain, compliance vault, airgap, FUSE compat, dirty flag

### FormatVersionInfo
Readonly struct combining MinReaderVersion/MinWriterVersion with all three flag enums. CanRead/CanWrite for version gating, HasIncompatibleFeatures for bitwise checks. Serializes to 16 bytes.

### BlockTypeTags
28 block type tags as uint32 constants (big-endian ASCII encoding). Includes 22 core tags (SUPB through FREE) plus 6 module registry extensions (CMVT, ALOG, CLOG, DICT, ANON, MLOG). TagToString/StringToTag for conversion, IsKnownTag backed by FrozenSet.

## Deviations from Plan

### Auto-added (Rule 2)
**1. [Rule 2 - Missing] Added 6 module registry block type tags**
- Plan specified 22 tags but also noted "Also add: CMVT, ALOG, CLOG, DICT, ANON, MLOG"
- These were included as they are needed by the module system in subsequent plans

**2. [Rule 2 - Completeness] Added IEquatable, equality operators, ToString**
- MagicSignature and FormatVersionInfo implement IEquatable<T> for value semantics
- Required for correct dictionary/collection behavior and debugging

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 219af01a | FormatConstants + MagicSignature |
| 2 | 14077d50 | FeatureFlags + BlockTypeTags |

## Self-Check: PASSED

- [x] FormatConstants.cs exists
- [x] MagicSignature.cs exists
- [x] FeatureFlags.cs exists
- [x] BlockTypeTags.cs exists
- [x] Commit 219af01a found
- [x] Commit 14077d50 found
- [x] Build: zero errors, zero warnings
