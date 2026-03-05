---
phase: 91.5-vde-v2.1-format-completion
plan: 87-21
subsystem: VirtualDiskEngine/ZNS
tags: [zns, nvme, epoch-mapping, zone-reset, vopt-34, znsm]
dependency_graph:
  requires:
    - BlockTypeTags.ZNSM (0x5A4E534D) — already defined in Format/BlockTypeTags.cs
    - FormatConstants.UniversalBlockTrailerSize — used for MaxEntriesPerBlock
  provides:
    - ZnsZoneMapRegion — persists epoch-to-zone mapping entries (ZNSM block type)
    - ZnsZoneAllocator — drives epoch-to-zone lifecycle (Free->Active->Full->Dead->Free)
    - ZnsZoneMapEntry — 16-byte on-disk serializable struct
  affects:
    - VdeMountPipeline (future): reads ZnsZoneMapRegion to rebuild allocator at mount
    - Background Vacuum: calls MarkDeadEpochs + GetZonesPendingReset for GC-free reclaim
tech_stack:
  added:
    - ZnsZoneMapEntry readonly struct with IEquatable<T> and BinaryPrimitives LE serialization
    - ZnsZoneMapRegion append-and-query zone map region
    - ZnsZoneAllocator with internal Dictionary-based O(1) epoch/zone lookups
    - ZnsAllocationStats readonly struct for snapshot metrics
  patterns:
    - Record-with for immutable state transitions on ZnsZoneMapEntry
    - Append-and-supersede pattern: new entries logically replace old Free slots
    - Dictionary-backed fast allocation index decoupled from persistence region
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Regions/ZnsZoneMapRegion.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Allocation/ZnsZoneAllocator.cs
  modified: []
decisions:
  - "ZnsZoneAllocator maintains internal Dictionary<ulong, ZnsZoneMapEntry> (_activeMapping) and Dictionary<uint, ZnsZoneMapEntry> (_zoneMapping) for O(1) allocation lookups rather than scanning the ZnsZoneMapRegion list on every call"
  - "ZnsZoneMapRegion uses append-and-supersede semantics; the allocator's internal dicts are authoritative for live state while the region provides persistence serialisation"
  - "IsZnsEnabled=false (constructor param) provides a safe fallback: every allocation method throws InvalidOperationException so callers must check before entering the ZNS path — matching the dual-namespace topology requirement from the spec"
  - "MarkZoneReset clears EpochId=0 on the free entry so Free zones have a deterministic epoch sentinel"
  - "ModuleBitPosition=23 constant on ZnsZoneAllocator matches the v2.1 module registry spec for ZNSM"
metrics:
  duration: 4min
  completed: 2026-03-02T12:11:43Z
  tasks: 1
  files: 2
---

# Phase 91.5 Plan 87-21: ZNSM Epoch-to-ZNS Zone Allocator Summary

**One-liner:** ZNS zone map region and zone-aware allocator that maps MVCC epochs 1:1 to physical NVMe ZNS zones, enabling dead epoch reclaim via a single ZNS_ZONE_RESET hardware command.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | ZnsZoneMapRegion and ZnsZoneAllocator | `5739dd2d` | ZnsZoneMapRegion.cs, ZnsZoneAllocator.cs |

## What Was Built

### ZnsZoneMapEntry (16-byte on-disk struct)

On-disk layout matching the spec: `[EpochId:8][ZoneId:4][State:2][Flags:2]` = 16 bytes, little-endian.

- `ZnsZoneState` enum: Free=0 / Active=1 / Full=2 / Dead=3 / Resetting=4
- `ZnsZoneFlags` [Flags] enum: None=0 / MetadataZone=1 / DataOnly=2 / Reserved=4
- `Serialize` / `Deserialize` via `System.Buffers.Binary.BinaryPrimitives`
- `IEquatable<ZnsZoneMapEntry>` with operator overloads

### ZnsZoneMapRegion

- `BlockTypeTag = 0x5A4E534D` ("ZNSM") — matches `BlockTypeTags.ZNSM`
- `MaxEntriesPerBlock` = `(blockSize - UniversalBlockTrailerSize) / 16`
- Entry CRUD: `AddEntry`, `FindByEpoch`, `FindByZone`, `GetDeadZones`
- State transitions: `MarkZoneDead` (Active/Full → Dead), `MarkZoneReset` (Dead/Resetting → Free)
- Full serialization: `Serialize(Span<byte>)` / `static Deserialize(ReadOnlySpan<byte>, ...)`

### ZnsZoneAllocator

- `ModuleBitPosition = 23` (ZNSM module registry bit)
- `IsZnsEnabled` — `false` = standard NVMe fallback (every method throws `InvalidOperationException`)
- `AllocateZoneForEpoch(epochId)` — Free → Active; throws if no free zones
- `GetZoneForEpoch(epochId)` — returns `uint?` zone ID
- `CompleteEpoch(epochId)` — Active → Full
- `MarkDeadEpochs(IReadOnlyList<ulong>)` — Full/Active → Dead; returns count
- `GetZonesPendingReset()` — Dead → Resetting; returns zone IDs for hardware command issuance
- `ConfirmZoneReset(zoneId)` — Resetting → Free after hardware acknowledgement
- `GetStats()` — `ZnsAllocationStats` snapshot
- `RegisterZone(entry)` — rebuilds in-memory index from persisted region at mount

## Zone Lifecycle

```
Free -> Active (AllocateZoneForEpoch)
Active -> Full (CompleteEpoch)
Full -> Dead (MarkDeadEpochs)
Active -> Dead (MarkDeadEpochs, active epoch killed early)
Dead -> Resetting (GetZonesPendingReset)
Resetting -> Free (ConfirmZoneReset)
```

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` — **0 errors, 0 warnings**
- `ZnsZoneMapEntry.EntrySize = 16` matches spec exactly (`[8][4][2][2]`)
- Zone lifecycle transitions enforce state machine invariants with `InvalidOperationException` on invalid transitions

## Deviations from Plan

None — plan executed exactly as written.

## Self-Check: PASSED

- `DataWarehouse.SDK/VirtualDiskEngine/Regions/ZnsZoneMapRegion.cs` — FOUND
- `DataWarehouse.SDK/VirtualDiskEngine/Allocation/ZnsZoneAllocator.cs` — FOUND
- Commit `5739dd2d` — FOUND
