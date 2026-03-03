---
phase: 91.5-vde-v2.1-format-completion
plan: 87-55
subsystem: VirtualDiskEngine.Recovery
tags: [disaster-recovery, vde, format, rcvr, failover, recovery-point, wal]
dependency_graph:
  requires: ["87-65"]
  provides: ["EmergencyRecoveryBlock", "RecoveryFooter", "RecoveryPointMarker", "FailoverState"]
  affects: ["VDE format layout", "SuperblockV2 +0x188 FailoverState slot"]
tech_stack:
  added: []
  patterns: ["Span<byte> serialization", "HMACSHA256 stand-in for BLAKE3", "readonly struct IEquatable", "switch expression state machine"]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Recovery/EmergencyRecoveryBlock.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Recovery/RecoveryFooter.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Recovery/RecoveryPointMarker.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Recovery/FailoverState.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/BlockTypeTags.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationJournalRegion.cs
decisions:
  - "HMACSHA256 used as BCL stand-in for HMAC-BLAKE3 in EmergencyRecoveryBlock; documented with production NOTE for replacement"
  - "RecoveryFooter uses SUPB block type tag (not a new tag) so recovery tools identify it as a superblock copy"
  - "ResolveSplitBrain returns Promoting (not Secondary) as hold-off state for both equal-epoch and losing cases, with caller responsible for SplitBrainDetected flag"
metrics:
  duration: "7 minutes"
  completed: "2026-03-02"
  tasks_completed: 2
  files_created: 4
  files_modified: 2
---

# Phase 91.5 Plan 87-55: Format-Native Disaster Recovery Structures Summary

Implemented four disaster recovery structures in `DataWarehouse.SDK/VirtualDiskEngine/Recovery/`:
Emergency Recovery Block (RCVR at block 14 with HMAC-BLAKE3 seal), Recovery Footer (EOF superblock mirror), Recovery Point Markers (48-byte RP01 WAL records), and Failover State (36-byte struct with epoch-based split-brain resolution).

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | EmergencyRecoveryBlock and RecoveryFooter | `74d44af1` | EmergencyRecoveryBlock.cs, RecoveryFooter.cs, BlockTypeTags.cs, OperationJournalRegion.cs |
| 2 | RecoveryPointMarker and FailoverState | `4e017a1c` | RecoveryPointMarker.cs, FailoverState.cs |

## What Was Built

### EmergencyRecoveryBlock.cs
- Always at `FixedBlockNumber = 14`, never encrypted
- Magic `DWVDRCVR` (0x4457564452435652), block type tag `RCVR` (0x52435652)
- Plaintext fields: VdeUuid, CreationTimestamp, FormatVersion, ModuleManifest, TotalBlocks, BlockSize, InodeSize, AdminContact (128B), OrganizationName (128B), VolumeLabel (64B), NamespacePrefix (32B), RecoveryNotes (256B)
- HMAC covers bytes [0x00..0x29B]; uses HMACSHA256 as BCL stand-in for HMAC-BLAKE3
- `ComputeHmac(byte[] key)`, `VerifyHmac(byte[] key)` with constant-time compare
- `WriteFixedString` / `ReadFixedString` helpers for null-padded UTF-8 fields

### RecoveryFooter.cs
- Last block of VDE file at `fileSizeBytes - blockSize`
- Mirrors Superblock Block 0 raw bytes with SUPB block type tag
- `ShouldUpdate(RecoveryFooterUpdateReason)` returns true for CleanUnmount, EpochBoundary, VdeGrow, VdeShrink; false for NormalWrite
- Full update protocol documented: grow (write new footer first, mark old RESERVED, update superblock, free old), shrink (move footer first, then release space)

### RecoveryPointMarker.cs (struct)
- Exactly `Size = 48` bytes, record type `RP01` (0x52503031)
- Fields: Flags (DATA_WAL_CONSISTENT bit 0, APPLICATION_CONSISTENT bit 1), MarkerSequence, UtcNanoseconds, MetadataWalLsn, DataWalLsn (0 if not synced), MerkleRootSnapshot
- `WriteTo(Span<byte>)` / `ReadFrom(ReadOnlySpan<byte>)` with RecordType validation
- `Create(...)` factory with UTC nanoseconds via `DateTimeOffset.UtcNow`

### FailoverState.cs (struct)
- Exactly `Size = 36` bytes for Superblock +0x188..+0x1AB
- `FailoverRole` enum: Standalone(0), Secondary(1), Promoting(2), Primary(3), Demoting(4)
- `FailoverFlags` [Flags]: FencingActive, SplitBrainDetected, WitnessRequired
- `IsValidTransition(from, to)` validates all 5 permitted transitions
- `ResolveSplitBrain(localEpoch, remoteEpoch)`: higher epoch -> Primary, lower epoch -> Promoting (hold-off), equal epochs -> Promoting with caller required to set SplitBrainDetected + WitnessRequired

### BlockTypeTags.cs (modified)
- Added `RCVR = 0x52435652` under Identity & Recovery section
- Added RCVR to `KnownTags` FrozenSet

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed pre-existing CA1512 in OperationJournalRegion.cs**
- **Found during:** Task 1 build
- **Issue:** `throw new ArgumentOutOfRangeException(nameof(blockSize))` in Deserialize method triggered CA1512 analyzer error (treated as build error in this project)
- **Fix:** Replaced with `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, UniversalBlockTrailer.Size)`
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Operations/OperationJournalRegion.cs
- **Commit:** `74d44af1`

**2. [Rule 1 - Bug] Fixed CA1512 in EmergencyRecoveryBlock.cs**
- **Found during:** Task 1 build
- **Issue:** Initial code used `throw new ArgumentOutOfRangeException(nameof(blockSize))` which triggers CA1512
- **Fix:** Changed to `ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, UniversalBlockTrailer.Size)`
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Recovery/EmergencyRecoveryBlock.cs
- **Commit:** `74d44af1`

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` -> Build succeeded, 0 errors, 0 warnings
- RCVR block: FixedBlockNumber = 14, magic DWVDRCVR verified in constants
- RecoveryFooter: GetFooterOffset returns fileSizeBytes - blockSize; uses SUPB trailer
- RecoveryPointMarker: Size = 48, RecordType = 0x52503031 (RP01)
- FailoverState: Size = 36, IsValidTransition covers all 5 transitions, ResolveSplitBrain epoch comparison correct

## Self-Check: PASSED

Files verified present:
- DataWarehouse.SDK/VirtualDiskEngine/Recovery/EmergencyRecoveryBlock.cs: FOUND
- DataWarehouse.SDK/VirtualDiskEngine/Recovery/RecoveryFooter.cs: FOUND
- DataWarehouse.SDK/VirtualDiskEngine/Recovery/RecoveryPointMarker.cs: FOUND
- DataWarehouse.SDK/VirtualDiskEngine/Recovery/FailoverState.cs: FOUND

Commits verified:
- 74d44af1: Task 1 (EmergencyRecoveryBlock, RecoveryFooter) FOUND
- 4e017a1c: Task 2 (RecoveryPointMarker, FailoverState) FOUND
