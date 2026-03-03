---
phase: 78-online-module-addition
plan: 05
subsystem: VirtualDiskEngine/ModuleManagement
tags: [vde, defragmentation, indirection, block-relocation, zero-downtime]
dependency_graph:
  requires: [WalJournaledRegionWriter, FreeSpaceScanner, RegionDirectory, RegionPointerTable, FormatConstants, BlockTypeTags]
  provides: [RegionIndirectionLayer, IndirectionTable, BlockMapping, FragmentationMetrics, FragmentationReport, RegionFragInfo, OnlineDefragmenter, DefragResult, DefragProgress, CompactionPlan, RegionMove]
  affects: [VDE online maintenance, storage compaction, region lifecycle]
tech_stack:
  added: []
  patterns: [indirection-table logical-to-physical translation, batch WAL-journaled block relocation, IOPS throttle, ArrayPool buffer management]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/RegionIndirectionLayer.cs
    - DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/FragmentationMetrics.cs
    - DataWarehouse.SDK/VirtualDiskEngine/ModuleManagement/OnlineDefragmenter.cs
  modified: []
decisions:
  - "IndirectionTable uses INDR magic, version 1, XxHash64 checksum at end of block"
  - "Fixed region boundary at block 10 (superblocks 0-7, region directory 8-9)"
  - "Defragmentation skipped when fragmentation below 5% threshold"
  - "Indirection table block placed after highest-addressed region"
  - "Wasted blocks defined as free runs smaller than 16 blocks (MinUsefulRunSize)"
metrics:
  duration: 5min
  completed: 2026-02-23T15:54:00Z
  tasks_completed: 2
  files_created: 3
---

# Phase 78 Plan 05: Online Defragmentation via Region Indirection Summary

Zero-downtime block compaction through logical-to-physical indirection layer with batch WAL-journaled relocation, IOPS throttling, and before/after fragmentation analysis.

## What Was Built

### RegionIndirectionLayer.cs (253 lines)
- `BlockMapping` readonly record struct: logical/physical/isRemapped
- `IndirectionTable` sealed class: Dictionary<long,long> mapping with Resolve/RemapBlock/RemoveMapping/GetAllMappings/HasMapping
- On-disk serialization: [INDR:4][Version:2][EntryCount:4][Reserved:6] header + [Logical:8][Physical:8] entries + XxHash64 checksum
- Deserialize returns empty table on invalid/corrupt data (graceful recovery)
- `RegionIndirectionLayer` sealed class: transparent read/write through indirection, async LoadTableAsync/PersistTableAsync for disk persistence

### FragmentationMetrics.cs (222 lines)
- `RegionFragInfo` readonly record struct: per-region type, start, count, gap-after
- `FragmentationReport` readonly record struct: overall%, regions, gaps, largest/smallest/avg free run, wasted blocks, region details
- `AnalyzeAsync`: reads RegionDirectory, sorts by StartBlock, computes inter-region gaps, fragmentation = 1-(largest/total), wasted = sum(gaps < 16 blocks)
- `EstimateImprovementAsync`: simulates perfect compaction, returns % improvement in contiguous free space
- `FormatReport`: human-readable output with ASCII bar chart visualization

### OnlineDefragmenter.cs (432 lines)
- `DefragPhase` enum: Analyzing, RelocatingBlocks, UpdatingRegionPointers, CompactingIndirectionTable, Complete
- `DefragProgress` readonly record struct: total/moved/percent/phase
- `DefragResult` readonly record struct: success, blocksRelocated, freeSpaceReclaimed, before/after fragmentation, duration
- `RegionMove` readonly record struct with NeedsMove property
- `CompactionPlan` internal class: ordered moves, totalBlocksToMove, freeSpaceAfter
- `DefragmentAsync` 5-phase algorithm:
  1. Analyze fragmentation (skip if <5%)
  2. Relocate blocks in batches through indirection layer
  3. Update RegionDirectory + RegionPointerTable atomically
  4. Persist and clear indirection table
  5. Report final metrics
- `DefragmentRegionAsync` for targeted single-region compaction
- `BuildCompactionPlan` positions regions contiguously after fixed blocks (0-9)
- IOPS throttling via configurable MaxIopsPerSecond
- Indirection table persisted after each batch for crash safety

## Key Design Decisions

1. **Indirection table block placement**: dynamically computed as first block after highest-addressed region
2. **Fixed region boundary**: blocks 0-9 (superblocks + mirror + region directory) never relocated
3. **5% fragmentation threshold**: skip defrag for well-compacted volumes
4. **Batch-then-persist**: indirection table written after each batch ensures crash recovery always finds consistent state
5. **Wasted blocks**: free runs under 16 blocks classified as waste (too small for region allocation)

## Deviations from Plan

None -- plan executed exactly as written.

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 75cbbe94 | RegionIndirectionLayer + FragmentationMetrics |
| 2 | e1d20c97 | OnlineDefragmenter with background block relocation |

## Self-Check: PASSED
