---
phase: 91.5-vde-v2.1-format-completion
plan: 87-29
subsystem: VirtualDiskEngine / BlockAllocation
tags: [wear-leveling, allocation, temperature-segregation, nvme, vopt-41]
dependency_graph:
  requires: ["87-65"]
  provides: ["SemanticWearLevelingAllocator", "WearLevelingConfig", "TemperatureClass"]
  affects: ["VDE block allocation layer", "Background Vacuum reclamation"]
tech_stack:
  added: []
  patterns: ["Decorator over IBlockAllocator", "Round-robin band selection", "ZNS gate passthrough"]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/WearLevelingConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/SemanticWearLevelingAllocator.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Retention/RetentionEpochMapper.cs
decisions:
  - "Round-robin within temperature band for even intra-band wear distribution"
  - "Fallback order Hot→Warm→Cold→Frozen when preferred band is exhausted"
  - "Dead-block ratio computed as 1 - FreeBlockCount/BlockCount (allocated = dead until freed)"
  - "ZNS gate check at IsSwlvActive property, not per-call branching"
metrics:
  duration_seconds: 255
  completed_date: "2026-03-02"
  tasks_completed: 1
  tasks_total: 1
  files_created: 2
  files_modified: 1
---

# Phase 91.5 Plan 87-29: Semantic Wear-Leveling Allocator Summary

TTL-hint-based AllocationGroup temperature segregation (Hot/Warm/Cold/Frozen) with ZNS passthrough gate, round-robin intra-band allocation, and whole-group Background Vacuum reclamation support for write-amplification elimination on conventional NVMe.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | WearLevelingConfig and SemanticWearLevelingAllocator | `41dbfccb` | WearLevelingConfig.cs, SemanticWearLevelingAllocator.cs |

## What Was Built

### WearLevelingConfig (WearLevelingConfig.cs)

Configuration class for the SWLV subsystem:

- `TemperatureClass` enum (`Hot=0`, `Warm=1`, `Cold=2`, `Frozen=3`) maps the 2-bit SLC_Wear_Hint from the SPOL module to an allocation band.
- Master switch (`Enabled`) and ZNS gate (`ZnsAwareActive`) — SWLV self-disables on ZNS devices.
- Four group ratio properties defaulting to Hot 20% / Warm 30% / Cold 35% / Frozen 15% (sum = 100%).
- `VacuumGroupReclaimInterval` (5 min default) and `GroupReclaimThreshold` (0.90 default) for Background Vacuum tuning.
- `AreRatiosValid()` helper validates ratios sum to ~1.0 within 1% tolerance.

### SemanticWearLevelingAllocator (SemanticWearLevelingAllocator.cs)

Decorator over `IBlockAllocator` that adds temperature-aware allocation:

**Construction and partitioning:**
- Accepts `IBlockAllocator innerAllocator`, `AllocationGroup[] allGroups`, `WearLevelingConfig config`.
- On construction, partitions `allGroups` into four temperature bands by config ratios (rounded, remainder to Cold).
- Builds `_groupIdToTemperature` reverse index and per-band `_writeCounters` arrays.

**ZNS gate:**
- `IsSwlvActive` property: returns `config.Enabled && !config.ZnsAwareActive`.
- When false, all `IBlockAllocator` calls delegate directly to `_inner` — zero overhead on ZNS devices.

**Temperature-aware allocation:**
- `AllocateBlockWithHint(TemperatureClass, CancellationToken)`: selects from target band (round-robin), falls back to remaining bands in Hot→Warm→Cold→Frozen order.
- `AllocateExtentWithHint(int, TemperatureClass, CancellationToken)`: same pattern for contiguous extents.
- `AllocateBlock` / `AllocateExtent` (IBlockAllocator interface): default to Hot band.

**IBlockAllocator passthrough:**
- `FreeBlock`, `FreeExtent`: delegate to `_inner`.
- `FreeBlockCount`, `TotalBlockCount`, `IsAllocated`: delegate to `_inner`.
- `FragmentationRatio`: weighted average across all temperature bands when SWLV active.
- `PersistAsync`: delegates to `_inner`.

**Vacuum support:**
- `GetReclaimCandidates(TemperatureClass)`: returns groups where dead-block ratio ≥ `GroupReclaimThreshold`. Background Vacuum can reclaim these groups wholesale without scanning for live data.
- `GetGroupTemperature(int)`: reverse lookup from group ID to temperature class.

**Metrics:**
- `GetStats()`: returns `WearLevelingStats` with per-temperature `TemperatureStats` (GroupCount, FreeBlocks, TotalBlocks, Utilization, WriteCount) and `EstimatedWriteAmplificationReduction` ratio.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed RetentionEpochMapper XML documentation compile errors**
- **Found during:** Task 1 build verification
- **Issue:** Pre-existing errors: `cref="EpochGatedLazyDeletion"` (unresolvable type) and `paramref name="epochDuration"` used in class-level remarks where it has no meaning.
- **Fix:** Replaced `cref` with plain text reference; replaced `paramref` with plain text in class-level doc.
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/Retention/RetentionEpochMapper.cs`
- **Commit:** `41dbfccb`

**2. [Rule 1 - Bug] Fixed SemanticWearLevelingAllocator XML doc error**
- **Found during:** Task 1 first build
- **Issue:** Class-level XML doc had `<paramref name="innerAllocator"/>` which is only valid in constructor doc, not class-level summary.
- **Fix:** Replaced with plain text "inner allocator".
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/SemanticWearLevelingAllocator.cs`
- **Commit:** `41dbfccb`

## Verification

Build result: `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` → **0 Warnings, 0 Errors**.

## Success Criteria Check

- [x] VOPT-41: TTL-hint-based allocation routing to temperature-specific AllocationGroups
- [x] SWLV disabled when ZNS_AWARE active (`ZnsAwareActive=true` → full passthrough to inner allocator)
- [x] Allocations route to correct temperature groups (round-robin within band, fallback across bands)
- [x] Group reclamation candidates identified for Background Vacuum (`GetReclaimCandidates`)
- [x] Build compiles with zero errors

## Self-Check: PASSED

Files exist:
- `DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/WearLevelingConfig.cs` — FOUND
- `DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/SemanticWearLevelingAllocator.cs` — FOUND

Commit exists: `41dbfccb` — FOUND in git log
