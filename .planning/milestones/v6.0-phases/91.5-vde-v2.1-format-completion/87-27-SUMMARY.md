---
phase: 91.5-vde-v2.1-format-completion
plan: 87-27
subsystem: vde-compliance
tags: [gdpr, tombstone, blake3, sha256, deletion-proof, wal, crash-recovery, block-device]

# Dependency graph
requires:
  - phase: 91-compound-block-device-raid
    provides: IBlockDevice, IWriteAheadLog, JournalEntry — block device and WAL infrastructure
  - phase: 71-vde-v2
    provides: InodeExtent, ExtentFlags — extent descriptor format
provides:
  - GdprTombstoneEngine: provable erasure engine with WAL-journaled block zeroing and deletion proofs
  - DeletionProof: cryptographic proof struct for verifiable block erasure (BLAKE3/SHA-256 hash)
  - TombstoneResult: batch erasure result with proofs, inode number, block counts, duration
affects: [phase-92-vde-decorator-chain, phase-95-crdt-wal, phase-99-e2e-certification]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "WAL-journal-before-mutate: all tombstone ops journaled before block writes (crash idempotency)"
    - "Deletion proof: BLAKE3/SHA-256(zeros[blockSize] || timestamp_nanos) truncated to 16 bytes"
    - "Delta-flatten-before-tombstone: reconstruct full data from patches before zeroing"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Compliance/DeletionProof.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Compliance/GdprTombstoneEngine.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/QuorumSealConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/QuorumSealedWritePath.cs

key-decisions:
  - "SHA-256 used as BLAKE3 fallback (both produce 32-byte digest, truncated to 16 bytes for ExpectedHash field)"
  - "DeletionProof captures single block representative per extent; caller maps proof hash to ExtentPointer.ExpectedHash"
  - "FlattenDeltaChainAsync takes explicit startBlock param — caller owns extent-to-block mapping"
  - "WAL marker encoding: upper 32 bits = op type ('TOMB'/'TCOM'/'FLAT'/'FLCO'), lower 32 bits = block address"
  - "BatchTombstone has two overloads: typed (full extent list) and simple (inode numbers only)"

patterns-established:
  - "Compliance directory: DataWarehouse.SDK/VirtualDiskEngine/Compliance/ for GDPR/audit types"
  - "Idempotent re-execution: tombstone-start without tombstone-complete triggers safe re-zeroing on recovery"

# Metrics
duration: 15min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-27: GDPR Tombstone Engine Summary

**GdprTombstoneEngine with provable block erasure: SHA-256 deletion proofs, delta chain flattening, WAL crash recovery, and batch vacuum API — satisfying VOPT-40 GDPR Article 17 compliance**

## Performance

- **Duration:** 15 min
- **Started:** 2026-03-02T00:00:00Z
- **Completed:** 2026-03-02T00:15:00Z
- **Tasks:** 1
- **Files modified:** 4 (2 created, 2 pre-existing bug fixes)

## Accomplishments

- Implemented `DeletionProof` readonly struct: 36-byte serializable proof of intentional block erasure using SHA-256(zeros||timestamp) truncated to 16 bytes matching ExtentPointer.ExpectedHash field
- Implemented `GdprTombstoneEngine`: WAL-journaled block overwrite with cryptographic zeros, delta chain flattening before tombstoning, crash-safe recovery via WAL replay, and batch tombstone API for Background Vacuum
- Implemented `TombstoneResult` readonly struct: proof array, inode metadata, block counts, wall-clock duration
- Fixed 3 pre-existing build-blocking errors in QuorumSealConfig.cs and QuorumSealedWritePath.cs

## Task Commits

Each task was committed atomically:

1. **Task 1: DeletionProof and GdprTombstoneEngine** - `20bd0f1e` (feat)

**Plan metadata:** (this summary commit)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Compliance/DeletionProof.cs` - Deletion proof struct: Compute(), Verify(), Serialize()/Deserialize(), 36-byte wire format
- `DataWarehouse.SDK/VirtualDiskEngine/Compliance/GdprTombstoneEngine.cs` - Erasure engine: TombstoneExtentAsync, FlattenDeltaChainAsync, ExecuteGdprDeletionAsync, RecoverFromWalAsync, ExecuteBatchTombstonesAsync
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/QuorumSealConfig.cs` - Added == / != operators to QuorumOverflowInode (CA2231 fix)
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/QuorumSealedWritePath.cs` - Replaced variable-length stackalloc with heap alloc in Schnorr verifiers (CA2014 fix)

## Decisions Made

- Used SHA-256 as fallback for BLAKE3 (BLAKE3 unavailable in .NET standard library; SHA-256 produces equivalent 32-byte digest truncated to 16 bytes for the ExpectedHash field)
- WAL marker encoding packs op type into upper 32 bits of TargetBlockNumber to avoid adding new JournalEntryType values
- `FlattenDeltaChainAsync` takes explicit `startBlock` parameter — the engine does not parse inode structures, keeping the compliance layer free of inode format coupling
- `ExecuteGdprDeletionAsync` has two overloads: a typed list version (full extent descriptors with delta chain flags) and a convenience version (raw inode data treated as a single extent)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1/3 - Bug/Blocking] Fixed pre-existing CA2231 on QuorumOverflowInode struct**
- **Found during:** Task 1 (build verification)
- **Issue:** `QuorumOverflowInode` implements `IEquatable<T>` and `Equals()` but was missing `==` / `!=` operators, causing CA2231 error that blocked the build
- **Fix:** Added `operator ==` and `operator !=` delegating to `Equals()`
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/Integrity/QuorumSealConfig.cs`
- **Verification:** Build succeeds with 0 errors
- **Committed in:** `20bd0f1e` (task commit)

**2. [Rule 1/3 - Bug/Blocking] Fixed pre-existing CA2014 variable-length stackalloc in Schnorr verifiers**
- **Found during:** Task 1 (build verification)
- **Issue:** `VerifyEd25519Schnorr` and `VerifyRistretto255Schnorr` used `stackalloc byte[32 + 4 + message.Length]` (variable-length) — CA2014 potential stack overflow, blocking build
- **Fix:** Replaced both with `new byte[...]` heap allocations
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/Integrity/QuorumSealedWritePath.cs`
- **Verification:** Build succeeds with 0 errors, 0 warnings
- **Committed in:** `20bd0f1e` (task commit)

---

**Total deviations:** 2 auto-fixed (2 pre-existing blocking build errors)
**Impact on plan:** Both fixes necessary to unblock build. No scope creep. New compliance files not involved.

## Issues Encountered

- Build had 2 pre-existing errors from QuorumSealedWritePath/QuorumSealConfig (introduced by a prior plan, 87-28). Fixed inline as deviation Rule 1/3.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- VOPT-40 GDPR Tombstone Engine is ready for integration with VdeMountPipeline (Phase 92)
- `GdprTombstoneEngine` is injectable via constructor — ready for DI registration
- `DeletionProof.Verify()` enables auditor workflows without coupling to VDE internals
- Delta flattening requires caller to provide extent data — Phase 92 decorator chain will provide this via inode reading

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
