---
phase: 91.5-vde-v2.1-format-completion
plan: 87-25
subsystem: VirtualDiskEngine.Format
tags: [vde, raid, erasure-coding, polymorphic-raid, per-inode, extent-flags]
dependency_graph:
  requires:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/InodeExtent.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Regions/RaidMetadataRegion.cs
    - DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs
  provides:
    - PolymorphicRaidModule (32B per-inode erasure coding descriptor)
    - ExtentRaidTopology (per-extent RAID scheme from flags bits 9-11)
    - RaidTopologyScheme enum (Standard/Mirror/EC_2_1/EC_4_2/EC_8_3)
    - PolymorphicRaidFlags enum
  affects:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/ModuleDefinitions.cs (Raid module, bit 5, InodeFieldBytes=4 base)
tech_stack:
  added: []
  patterns:
    - readonly struct with init-only properties for immutable per-inode descriptors
    - static helper class for bit-field encoding/decoding (ExtentRaidTopology)
    - BinaryPrimitives little-endian serialization
    - Factory methods (Standard property, CreateMirror(), CreateErasureCoded())
    - Typed overloads for ExtentFlags alongside raw uint API
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/PolymorphicRaidModule.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/ExtentRaidTopology.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/SpatioTemporalModule.cs (auto-fix: broken XML cref)
decisions:
  - "RaidTopologyScheme enum values 0-4 match extent flag bits 9-11 exactly; values 5-7 reserved for future schemes"
  - "DeviceMap is 28-byte byte[] (not fixed array) to remain a readonly struct on the heap-allocation boundary"
  - "CreateErasureCoded maps well-known (2,1)/(4,2)/(8,3) shard pairs to named enum values; custom shard counts accepted with nearest named scheme"
  - "ExtentRaidTopology exposes both uint and ExtentFlags overloads for ergonomic integration without cast noise"
  - "Pre-existing SpatioTemporalModule.cs XML cref errors auto-fixed (Rule 1: broken build) — types SpatioTemporalExtent and SpatialGeohash were never defined"
metrics:
  duration: 4min
  completed: 2026-03-02T12:33:08Z
  tasks_completed: 1
  files_created: 2
  files_modified: 1
---

# Phase 91.5 Plan 87-25: Polymorphic RAID Per-Inode Erasure Coding Summary

**One-liner:** 32B per-inode polymorphic RAID descriptor with RS erasure coding params and 28-device shard map, plus per-extent RAID_Topology bits 9-11 reader/writer enabling mixed RAID levels on the same NVMe volume.

## What Was Built

### PolymorphicRaidModule (PolymorphicRaidModule.cs)

A 32-byte `readonly struct` that extends the volume-level RAID module (bit 5, `ModuleId.Raid`) to support per-inode erasure coding. Key design points:

- **Layout:** `[Scheme:1][DataShards:1][ParityShards:1][Flags:1][DeviceMap:28]` — exactly 32 bytes
- **RaidTopologyScheme enum:** `Standard=0, Mirror=1, EC_2_1=2, EC_4_2=3, EC_8_3=4` — values directly match extent flag bits 9-11 encoding
- **PolymorphicRaidFlags enum:** `InheritFromVolume=1, AllowDegradation=2, ParityDecayEligible=4`
- **DeviceMap:** 28-byte array; each byte is a device index (0-254) into `RaidMetadataRegion`; `0xFF` = unassigned slot; max 28 shard device assignments per inode
- **Computed properties:** `IsErasureCoded`, `TotalShards`, `StorageOverhead`
- **Factory methods:** `Standard` (static property, no redundancy), `CreateMirror()`, `CreateErasureCoded(byte dataShards, byte parityShards)`
- **Serialization:** `Serialize(in PolymorphicRaidModule, Span<byte>)` and `Deserialize(ReadOnlySpan<byte>)` using `BinaryPrimitives` little-endian

### ExtentRaidTopology (ExtentRaidTopology.cs)

A static class that reads and writes the `RAID_Topology` 3-bit field from bits 9-11 of the `ExtentFlags` uint32. Key design points:

- **Constants:** `RaidTopologyBitOffset = 9`, `RaidTopologyMask = 0b111u << 9 = 0x00000E00`
- **Core API (uint overloads):** `GetTopology(uint)`, `SetTopology(uint, RaidTopologyScheme)`, `HasRedundancy(uint)`
- **Typed overloads:** `GetTopology(ExtentFlags)`, `SetTopology(ExtentFlags, RaidTopologyScheme)`, `HasRedundancy(ExtentFlags)` — avoids cast noise at call sites
- **Validation:** `IsValidScheme(byte)` — returns true for 0-4, false for reserved values 5-7
- **Description:** `DescribeTopology(RaidTopologyScheme)` — e.g. `"EC 4+2 (Reed-Solomon)"`, `"Mirror (1:1 copy)"`, `"Reserved (5)"`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Pre-existing broken XML cref in SpatioTemporalModule.cs**
- **Found during:** Build verification
- **Issue:** Three XML doc `<cref>` attributes referenced `SpatioTemporalExtent.SpatialGeohash` and `SpatialGeohash` types that were never defined in the codebase. Caused 3 CS1574 errors and a failed build, blocking verification of new files.
- **Fix:** Replaced broken `<see cref="..."/>` with equivalent `<c>...</c>` plain text in XML doc comments at lines 8, 60, and 89 of SpatioTemporalModule.cs
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/Format/SpatioTemporalModule.cs`
- **Commit:** Included in task commit `0f1624ef`

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` — **PASS** (0 warnings, 0 errors)
- `RaidTopologyScheme` values 0-4 match extent flag bits 9-11 encoding — **VERIFIED**
- `DeviceMap` supports 28 device positions per inode — **VERIFIED** (`DeviceMapSize = 28`)
- `PolymorphicRaidModule.ModuleBitPosition = 5` matches `ModuleId.Raid` — **VERIFIED**

## Self-Check: PASSED

- FOUND: `DataWarehouse.SDK/VirtualDiskEngine/Format/PolymorphicRaidModule.cs`
- FOUND: `DataWarehouse.SDK/VirtualDiskEngine/Format/ExtentRaidTopology.cs`
- FOUND commit: `0f1624ef`
