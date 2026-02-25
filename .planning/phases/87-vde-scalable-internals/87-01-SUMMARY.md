---
phase: 87-vde-scalable-internals
plan: "01"
subsystem: VDE Allocation Groups
tags: [vde, allocation, bitmap, block-allocator, scalability]
dependency_graph:
  requires: [IBlockAllocator, BlockTypeTags, FormatConstants]
  provides: [AllocationGroup, AllocationGroupDescriptorTable, AllocationPolicy]
  affects: [sub-block-packing, defragmentation, tag-bloom-filters]
tech_stack:
  added: []
  patterns: [per-group-bitmap, reader-writer-lock, first-fit, best-fit, binary-serialization]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Allocation/AllocationPolicy.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Allocation/AllocationGroup.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Allocation/AllocationGroupDescriptorTable.cs
  modified: []
decisions:
  - "AllocationGroup uses ReaderWriterLockSlim for read/write separation on bitmap operations"
  - "32-byte fixed descriptor format: GroupId(4)+StartBlock(8)+BlockCount(8)+FreeCount(8)+Policy(1)+Reserved(3)"
  - "Remainder blocks distributed to last group for exact coverage"
  - "Cross-group extent freeing handled transparently by descriptor table"
metrics:
  duration: "6min"
  completed: "2026-02-23T21:21:18Z"
  tasks: 2
  files: 3
---

# Phase 87 Plan 01: Allocation Groups & Descriptor Table Summary

Per-group bitmap allocation with FirstFit/BestFit policies and 32-byte descriptor serialization using BMAP region tag.

## Task Completion

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | AllocationPolicy enum and AllocationGroup class | 52583237 | AllocationPolicy.cs, AllocationGroup.cs |
| 2 | AllocationGroupDescriptorTable with region serialization | d722e182 | AllocationGroupDescriptorTable.cs |

## What Was Built

### AllocationPolicy (enum)
- `FirstFit` (sequential scan, streaming writes) and `BestFit` (smallest gap, random writes)
- `[SdkCompatibility("6.0.0")]` attribute

### AllocationGroup (sealed class)
- Per-group bitmap (1 bit per block), all initially free
- `ReaderWriterLockSlim` for concurrent read/exclusive write access
- `AllocateBlock()` with policy-aware scanning (FirstFit or BestFit)
- `AllocateExtent(count)` finds contiguous runs per policy
- `FreeBlock(blockNumber)` / `FreeExtent(startBlock, blockCount)` with validation
- `FragmentationRatio` computes fragment count vs free blocks
- Binary serialization: `[FreeCount:8][Policy:1][BitmapLength:4][Bitmap:N]`
- Default group size: 128 MB (`DefaultGroupSizeBytes`)
- Single-group mode for VDEs smaller than 128 MB

### AllocationGroupDescriptorTable (sealed class)
- Partitions data region into N groups based on `totalDataBlocks * blockSize / groupSizeBytes`
- Routes `AllocateBlock`/`AllocateExtent` to preferred or first available group
- `FreeExtent` transparently handles extents spanning multiple groups
- 32-byte on-disk descriptor per group, tagged with `BlockTypeTags.BMAP`
- `SerializeDescriptors`/`DeserializeDescriptors` for region-backed persistence
- `GetGroupForBlock` reverse-maps any block to its owning group

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] CA1512 argument validation pattern**
- **Found during:** Task 1 build verification
- **Issue:** Project enforces CA1512 (use ThrowIfNegative/ThrowIfNegativeOrZero)
- **Fix:** Replaced manual `if/throw` with `ArgumentOutOfRangeException.ThrowIfNegative`/`ThrowIfNegativeOrZero`
- **Files modified:** AllocationGroup.cs
- **Commit:** 52583237

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` -- 0 errors, 0 warnings
- AllocationGroup supports both FirstFit and BestFit allocation policies
- AllocationGroupDescriptorTable correctly partitions data region into groups
- Single-group mode works for small VDEs (< 128 MB)
- VOPT-01 and VOPT-02 requirements satisfied
- All classes have `[SdkCompatibility("6.0.0")]` attribute
- No mocks, stubs, or placeholders

## Self-Check: PASSED
