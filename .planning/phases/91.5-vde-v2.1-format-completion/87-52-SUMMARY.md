---
phase: 91.5-vde-v2.1-format-completion
plan: 87-52
subsystem: vde
tags: [qos, nvme, iops, bandwidth, extent-flags, policy-vault, module-overflow-block, binary-serialization]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: "VDE v2.1 format structures and spec (87-65 foundation)"

provides:
  - "QoSClass enum: 8-level QoS class (Background=0 through Realtime=7) with NVMe ioprio mapping"
  - "NvmeQoSPriority enum: 2-bit per-extent hardware priority for extent Flags bits 6-7"
  - "QoSModuleField: 4-byte Module Overflow Block inode field (TenantId + QoSClass + Reserved)"
  - "QoSPolicyRecord: 64-byte Policy Vault record (type tag 0x0003) with IOPS/bandwidth/burst/quota"
  - "ExtentQoSFlags: bit-manipulation helpers for extent Flags bits 6-7 and 19-22 (DeadlineTier)"

affects:
  - vde-mount-pipeline
  - background-scanner
  - io-scheduler
  - policy-vault-reader
  - qos-enforcement

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "BinaryPrimitives LE serialization with explicit byte-offset slice notation (buffer[0x00..0x02])"
    - "IEquatable<T> readonly struct pattern for all wire-format value types"
    - "Static helper class for bit-field extraction (ExtentQoSFlags)"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/QoS/QoSClass.cs
    - DataWarehouse.SDK/VirtualDiskEngine/QoS/QoSModuleField.cs
    - DataWarehouse.SDK/VirtualDiskEngine/QoS/QoSPolicyRecord.cs
    - DataWarehouse.SDK/VirtualDiskEngine/QoS/ExtentQoSFlags.cs
  modified: []

key-decisions:
  - "NvmeQoSPriority (2-bit, bits 6-7) is distinct from QoSClass (3-bit, 8 levels): updating Policy Vault records never requires rewriting extent flags"
  - "QoSPolicyRecord.PolicyVaultTypeTag = 0x0003 distinguishes QoS records from other Policy Vault entry types"
  - "ExtentQoSFlags.SetDeadlineTier throws ArgumentOutOfRangeException for tier > 0x0F to enforce spec constraint"
  - "DeadlineTier 0xC-0xE are reserved (return null from GetDeadlineFromTier) per spec; 0xF is best-effort (also null)"

patterns-established:
  - "QoS struct serialization: slice notation with hex offsets mirrors spec table layout for readability and verifiability"
  - "Reserved bytes written as explicit buffer[n] = 0 / buffer[a..b].Clear() on serialization, silently ignored on deserialization"

# Metrics
duration: 3min
completed: 2026-03-02
---

# Phase 87-52: Format-Native QoS Structures Summary

**Four production-ready QoS structs enabling hardware-native I/O scheduling: 8-level QoSClass with NVMe ioprio mapping, 4-byte Module Overflow Block field, 64-byte Policy Vault record (type 0x0003), and extent Flags bit helpers for 2-bit NVMe priority (bits 6-7) and 4-bit DeadlineTier (bits 19-22).**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-03-02T14:01:46Z
- **Completed:** 2026-03-02T14:04:40Z
- **Tasks:** 1/1
- **Files modified:** 4 created

## Accomplishments

- Implemented complete VOPT-71-73 QoS format layer: class enum, module inode field, policy vault record, extent flag helpers
- All structs are readonly, IEquatable, fully BinaryPrimitives LE serialized with precise spec-matching byte offsets
- Build compiles with zero errors and zero warnings

## Task Commits

1. **Task 1: QoS class, module field, policy record, and extent flag helpers** - `360cc470` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/QoS/QoSClass.cs` - QoSClass (8-level, 3-bit) and NvmeQoSPriority (4-level, 2-bit) enums
- `DataWarehouse.SDK/VirtualDiskEngine/QoS/QoSModuleField.cs` - 4-byte Module Overflow Block inode field: TenantId (uint16 LE) + QoSClass (uint8) + Reserved (0)
- `DataWarehouse.SDK/VirtualDiskEngine/QoS/QoSPolicyRecord.cs` - 64-byte Policy Vault record with all spec fields at exact byte offsets; type tag 0x0003
- `DataWarehouse.SDK/VirtualDiskEngine/QoS/ExtentQoSFlags.cs` - GetQoSClass/SetQoSClass (bits 6-7), GetDeadlineTier/SetDeadlineTier (bits 19-22), GetDeadlineFromTier tier→TimeSpan map

## Decisions Made

- NvmeQoSPriority (2-bit per-extent, extent Flags bits 6-7) is kept strictly distinct from QoSClass (3-bit per-inode, Module Overflow Block). These are independent fields: Policy Vault IOPS/bandwidth record updates never require rewriting extent Flags.
- PolicyVaultTypeTag = 0x0003 is a compile-time const on QoSPolicyRecord so Policy Vault readers can use it as a discriminator.
- SetDeadlineTier validates tier <= 0x0F (throws ArgumentOutOfRangeException) to enforce the spec constraint at the point of mutation.
- DeadlineTier values 0xC-0xE are reserved and return null from GetDeadlineFromTier (not exceptions) for forward-compatibility.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- QoS layer is complete and ready for use by the VDE mount pipeline, Background Scanner, and I/O scheduler
- Policy Vault reader can use PolicyVaultTypeTag = 0x0003 to identify QoS records
- ExtentQoSFlags helpers are ready for InodeExtent.Flags manipulation wherever extent QoS priority or deadline tiers are set

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
