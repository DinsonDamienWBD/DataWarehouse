---
phase: 91.5-vde-v2.1-format-completion
plan: 87-18
subsystem: VDE BlockAllocation
tags: [vde, metaslab, allocation, hierarchy, lazy-loading, size-class, morph-level]
dependency_graph:
  requires: ["87-65"]
  provides: ["MetaslabAllocator", "MetaslabDirectory", "Metaslab"]
  affects: ["IBlockAllocator", "BitmapAllocator", "AllocationGroupDescriptorTable"]
tech_stack:
  added: []
  patterns:
    - "Lazy bitmap loading via EnsureBitmapLoadedAsync"
    - "Size-class segregated free lists (small/medium/large)"
    - "LRU metaslab eviction with LinkedList+Dictionary"
    - "Online morph promotion via TryPromoteAsync"
    - "O(1) metaslab lookup by block number via integer division"
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/Metaslab.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/MetaslabDirectory.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/MetaslabAllocator.cs
  modified: []
decisions:
  - "MorphLevel.Level0_FlatBitmap delegates to existing BitmapAllocator (zero duplication)"
  - "MorphLevel.Level1_ShardedGroups delegates to AllocationGroupDescriptorTable (existing)"
  - "MetaslabDirectory entries are 32 bytes per spec; zone/region encoded in ZoneId(byte)/RegionId(byte) fields"
  - "Bitmap bit convention: 0=free, 1=allocated (consistent with BitmapAllocator for L0 parity)"
  - "AllocateBlock hot path synchronously awaits EnsureBitmapLoadedAsync — avoids async state machine overhead on already-loaded fast path"
  - "IsAllocated for Level1 is conservative (returns true) since AllocationGroup.IsBitSet is private"
metrics:
  duration: 6min
  completed: "2026-03-02T12:49:10Z"
  tasks_completed: 2
  files_created: 3
  files_modified: 0
---

# Phase 91.5 Plan 87-18: Hierarchical Metaslab Allocator Summary

**One-liner:** Hierarchical metaslab allocator (VOPT-31) with 4 morph levels from flat bitmap through hierarchical metaslab trees, lazy bitmap loading, size-class segregated free lists, and online promotion.

## What Was Built

### Metaslab.cs

Individual metaslab with lazy bitmap loading and size-class free lists:

- `MetaslabState` enum (5 states: Unloaded/Active/Full/Fragmented/EvictionCandidate)
- Lazy bitmap loading via `EnsureBitmapLoadedAsync` — bitmap stays null until first access
- Three size-class free lists (`SortedList<long, int>`): small (1-16 blocks), medium (17-256), large (257+)
- `AllocateBlock` / `AllocateExtent` — dispatches to appropriate size class with best-fit within class
- `FreeBlock` / `FreeExtent` — marks bitmap free and coalesces adjacent free runs
- `PersistAsync` — writes dirty bitmap back to device block-by-block
- `Unload` — releases bitmap memory for LRU eviction under memory pressure
- `FreeBlockCount` maintained without bitmap loading via directory entry tracking

### MetaslabDirectory.cs

Directory of metaslabs with zone/region/AG hierarchy:

- `MetaslabDirectoryEntry` readonly struct (32 bytes per spec): MetaslabId + StartBlock + BlockCount + FreeBlockCount + State + ZoneId + RegionId
- Zone/Region hierarchy: 16 zones (first 16 PB), 256 regions per zone
- `GetBestMetaslab(requestedBlocks)` — O(1) lookup from directory entries; picks least-utilized metaslab with sufficient free space
- `GetMetaslabForBlock(blockNumber)` — O(1) via integer division by `_metaslabBlockCount`
- `LoadDirectoryAsync` / `PersistDirectoryAsync` — read/write directory entries from/to device
- LRU eviction: `LinkedList<int>` + `Dictionary<int, LinkedListNode<int>>` track access order; evicts bitmaps when loaded count exceeds `MaxLoadedMetaslabs` (default 64)
- `UpdateEntry` — updates free block count and state in directory without I/O

### MetaslabAllocator.cs

`IBlockAllocator` implementation with 4 morph levels and online promotion:

- `MorphLevel` enum (Level0-Level3) with auto-detection from volume size:
  - Level 0 (<128 MB): delegates to `BitmapAllocator`
  - Level 1 (128 MB–1 TB): delegates to `AllocationGroupDescriptorTable`
  - Level 2 (1 TB–1 PB): uses `MetaslabDirectory` (flat metaslabs)
  - Level 3 (>1 PB): uses `MetaslabDirectory` (zone/region hierarchy)
- All `IBlockAllocator` methods: `AllocateBlock`, `AllocateExtent`, `FreeBlock`, `FreeExtent`, `FreeBlockCount`, `TotalBlockCount`, `FragmentationRatio`, `IsAllocated`, `PersistAsync`
- `TryPromoteAsync` — non-blocking online promotion when VDE resized past tier boundary:
  - L0→L1: builds new `AllocationGroupDescriptorTable`
  - L1→L2: builds `MetaslabDirectory` with `LoadDirectoryAsync`
  - L2→L3: logical upgrade (zone IDs already encoded in directory entries)
  - Multi-step promotion via recursive call

## Verification

Build result: `0 Warning(s)  0 Error(s)` on `DataWarehouse.SDK.csproj`.

## Deviations from Plan

None — plan executed exactly as written.

## Self-Check

- Metaslab.cs: FOUND
- MetaslabDirectory.cs: FOUND
- MetaslabAllocator.cs: FOUND
- Task 1 commit (a10f2c2a): FOUND
- Task 2 commit (9051fc2e): FOUND

## Self-Check: PASSED
