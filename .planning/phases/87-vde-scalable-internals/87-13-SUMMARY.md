---
phase: 87-vde-scalable-internals
plan: 13
subsystem: vde-integrity
tags: [merkle-tree, xxhash64, crc32c, sha256, checksums, corruption-detection]

requires:
  - phase: 87-04
    provides: "Block trailers with XxHash64 checksums"
  - phase: 87-12
    provides: "Per-extent encryption and compression infrastructure"
provides:
  - "3-tier hierarchical checksum verification (block XxHash64, extent CRC32C, object Merkle root)"
  - "Binary-search corruption localization in O(log n) block reads"
  - "Incremental Merkle tree updates after single-block writes"
  - "Full object scrub with timing and corrupt block reporting"
affects: [87-14, 87-15, vde-maintenance, vde-scrub]

tech-stack:
  added: [System.IO.Hashing.Crc32, System.IO.Hashing.XxHash64, System.Security.Cryptography.SHA256]
  patterns: [hierarchical-checksums, merkle-binary-search, incremental-tree-update]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/HierarchicalChecksumTree.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleIntegrityVerifier.cs

key-decisions:
  - "Leaf hashes are SHA256 of XxHash64 bytes for consistent 32-byte nodes throughout tree"
  - "48-byte node format (32 hash + 8 left ptr + 8 right ptr) for ~85 nodes per 4KB block"
  - "byte[] instead of stackalloc in async methods to avoid Span/await boundary violations"
  - "Deterministic object-to-block mapping via inode number * maxBlocksPerTree slot size"

patterns-established:
  - "Three-tier integrity: fast (XxHash64), medium (CRC32C), strong (Merkle SHA-256)"
  - "Binary-search corruption: compare subtree hashes, descend only into mismatched branches"
  - "Level-order BFS serialization for cache-friendly Merkle tree persistence"

duration: 6min
completed: 2026-02-23
---

# Phase 87 Plan 13: Hierarchical Checksums Summary

**Three-tier integrity (per-block XxHash64, per-extent CRC32C, per-object Merkle root) with binary-search corruption localization in O(log n) reads**

## Performance

- **Duration:** 6 min
- **Started:** 2026-02-23T21:49:08Z
- **Completed:** 2026-02-23T21:55:07Z
- **Tasks:** 2
- **Files created:** 2

## Accomplishments
- HierarchicalChecksumTree provides layered integrity: fast block verification via existing trailer checksums, medium-strength extent CRC32C, and strong per-object Merkle root
- MerkleIntegrityVerifier enables binary-search corruption localization (O(log n) vs O(n) full scan), incremental updates after writes, and full scrub operations
- ChecksumVerificationResult, ExtentChecksumRecord, MerkleNode, and ScrubResult types for comprehensive integrity reporting

## Task Commits

Each task was committed atomically:

1. **Task 1: HierarchicalChecksumTree** - `ce8cf4f7` (feat)
2. **Task 2: MerkleIntegrityVerifier** - `285cf793` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/HierarchicalChecksumTree.cs` - 3-tier checksum hierarchy with per-block XxHash64, per-extent CRC32C, per-object Merkle root, and full object verification
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleIntegrityVerifier.cs` - Merkle tree persistence, binary-search corruption localization, incremental updates, and scrub support

## Decisions Made
- Leaf hashes use SHA256(XxHash64 bytes) to produce consistent 32-byte nodes throughout the tree, bridging the fast XxHash64 block checksums with the cryptographic Merkle structure
- Used byte[] arrays instead of stackalloc Span for hash buffers in async methods due to C# Span/await boundary restriction (CA2014 and CS4007)
- Deterministic object-to-tree mapping uses fixed-size slots (2048 blocks per object) within the integrity tree region
- Level-order (BFS) serialization chosen for cache-friendly sequential reads during tree traversal

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed stackalloc in async method loops**
- **Found during:** Task 1 (HierarchicalChecksumTree)
- **Issue:** stackalloc byte[8] inside loops in async methods caused CA2014 errors, and Span cannot cross await boundaries (CS4007)
- **Fix:** Replaced stackalloc with heap-allocated byte[8] arrays hoisted before loops
- **Files modified:** HierarchicalChecksumTree.cs
- **Verification:** Build succeeds with zero errors
- **Committed in:** ce8cf4f7 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Essential fix for C# async/Span constraints. No scope creep.

## Issues Encountered
None beyond the stackalloc/async fix documented above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Hierarchical checksums ready for integration with VDE scrub and repair workflows
- Merkle tree infrastructure available for snapshot verification and incremental integrity checks

---
*Phase: 87-vde-scalable-internals*
*Completed: 2026-02-23*
