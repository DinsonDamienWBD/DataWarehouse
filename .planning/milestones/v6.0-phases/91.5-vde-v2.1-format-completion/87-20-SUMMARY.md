---
phase: 91.5-vde-v2.1-format-completion
plan: 87-20
subsystem: VDE Encryption
tags: [ekey, vopt-33, ephemeral-keys, crypto-shredding, volatile-key-ring, mvcc]
dependency_graph:
  requires: []
  provides:
    - EphemeralKeyModule (32B on-disk EKEY inode module, Module bit 20)
    - IVolatileKeyRing (RAM-only key ring contract)
    - VolatileKeyRingEntry (key ring slot with TTL epoch)
  affects:
    - DataWarehouse.SDK/VirtualDiskEngine/Encryption
tech_stack:
  added:
    - EphemeralKeyModule: 32B serializable inode module with BinaryPrimitives LE encoding
    - IVolatileKeyRing: async contract for volatile (RAM-only or TPM-sealed) key ring
    - VolatileKeyRingEntry: readonly record struct holding key material + TTL metadata
    - EphemeralKeyFlags: [Flags] enum (None/TpmSealed/AutoReap/ForceExpireOnUnmount)
  patterns:
    - RAM-only key material guarantee (no disk serialization)
    - TTL-epoch-based expiry via MVCC epoch comparison
    - O(1) crypto-shredding via DropKeyAsync without data I/O
    - SlotIndex as fast-path hint with ID-based fallback
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Encryption/EphemeralKeyModule.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Encryption/IVolatileKeyRing.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Encryption/VolatileKeyRingEntry.cs
  modified: []
decisions:
  - "Guid.TryWriteBytes used for GUID serialization into raw Span<byte> (consistent with SDK patterns)"
  - "KeyRingSlot is fast-path hint only; implementations must fall back to GetKeyAsync by EphemeralKeyId"
  - "IVolatileKeyRing comments explicitly enumerate anti-patterns (no KMS backup, no disk serialization, no logging)"
metrics:
  duration: 3min
  completed: 2026-03-02T11:59:39Z
  tasks_completed: 1
  files_created: 3
  files_modified: 0
---

# Phase 91.5 Plan 87-20: EKEY Ephemeral Key Module Summary

EKEY module (Module bit 20, VOPT-33): per-inode ephemeral key ring reference with 32B on-disk layout and RAM-only volatile key ring interface enabling O(1) crypto-shredding.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | EKEY Module, Volatile Key Ring Contract, and Entry | dfd209bd | EphemeralKeyModule.cs, IVolatileKeyRing.cs, VolatileKeyRingEntry.cs |

## What Was Built

### EphemeralKeyModule (32B on-disk inode module)

Maps to EKEY module at **Module bit 20** in the ModuleManifest. Packed as:

```
[EphemeralKeyID:16][TTL_Epoch:8][KeyRingSlot:4][Flags:4]
```

- `EphemeralKeyId` (Guid): 16-byte lookup handle into the volatile key ring
- `TtlEpoch` (ulong): MVCC epoch at expiry — key is eligible for reaping when `currentEpoch >= TtlEpoch`
- `KeyRingSlot` (uint): fast-path slot hint; implementations fall back to ID lookup
- `Flags` (EphemeralKeyFlags): None / TpmSealed / AutoReap / ForceExpireOnUnmount
- `Serialize` / `Deserialize` using `System.Buffers.Binary.BinaryPrimitives` (little-endian)
- `const int SerializedSize = 32`, `const byte ModuleBitPosition = 20`

### IVolatileKeyRing (RAM-only key ring contract)

Contract for RAM-only (or TPM-sealed) key storage with explicit anti-pattern documentation:

- `AllocateKeyAsync(ttlEpoch, ct)`: generates AES-256 key, stores in ring, returns VolatileKeyRingEntry
- `GetKeyAsync(keyId, ct)`: retrieves key or returns null if dropped/expired
- `DropKeyAsync(keyId, ct)`: O(1) crypto-shred — removes key from ring, ciphertext becomes irrecoverable
- `ReapExpiredAsync(currentEpoch, ct)`: removes all keys with `TtlEpoch <= currentEpoch`
- `ActiveKeyCount` / `Capacity`: ring utilization properties

### VolatileKeyRingEntry (key ring slot)

`readonly record struct` holding:

- `EphemeralKeyId` (Guid), `KeyMaterial` (byte[32]), `TtlEpoch` (ulong), `SlotIndex` (uint), `CreatedUtc` (DateTimeOffset)
- `IsExpired(ulong currentEpoch)` predicate

## Deviations from Plan

None — plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` succeeded with 0 errors, 0 warnings
- EphemeralKeyModule serializes to exactly 32 bytes matching spec layout [EphemeralKeyID:16][TTL_Epoch:8][KeyRingSlot:4][Flags:4]
- IVolatileKeyRing has no persistence methods — keys are never written to disk

## Self-Check: PASSED

- FOUND: DataWarehouse.SDK/VirtualDiskEngine/Encryption/EphemeralKeyModule.cs
- FOUND: DataWarehouse.SDK/VirtualDiskEngine/Encryption/IVolatileKeyRing.cs
- FOUND: DataWarehouse.SDK/VirtualDiskEngine/Encryption/VolatileKeyRingEntry.cs
- FOUND: commit dfd209bd
