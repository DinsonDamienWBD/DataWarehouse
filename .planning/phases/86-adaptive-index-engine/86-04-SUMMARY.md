---
phase: 86-adaptive-index-engine
plan: "04"
subsystem: adaptive-index-engine
tags: [be-tree-forest, hilbert-curve, learned-routing, sharding, auto-split]
dependency-graph:
  requires: ["86-01", "86-02"]
  provides: ["BeTreeForest", "HilbertPartitioner", "LearnedShardRouter"]
  affects: ["AdaptiveIndexEngine"]
tech-stack:
  added: []
  patterns: ["Hilbert space-filling curve", "CDF-based learned routing", "per-shard morph independence", "volatile model swap"]
key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/HilbertPartitioner.cs
    - DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/LearnedShardRouter.cs
    - DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/BeTreeForest.cs
  modified: []
decisions:
  - "Hilbert curve uses 2D/16-bit default for shard routing (65536 range per dimension)"
  - "LearnedShardRouter uses volatile CdfModel swap for lock-free reader thread safety"
  - "Each shard wraps AdaptiveIndexEngine for independent morphing (DirectPointer through ART)"
  - "Auto-split samples up to 1000 keys to find median; auto-merge picks smallest adjacent neighbor"
metrics:
  duration: "6min"
  completed: "2026-02-23T20:24:00Z"
  tasks: 2
  files: 3
---

# Phase 86 Plan 04: Sharded Be-tree Forest Summary

Hilbert-partitioned Be-tree forest (Level 5) with learned O(1) shard routing and auto-split/merge

## What Was Built

### HilbertPartitioner (static, pure)
- `KeyToHilbertValue`: Generalized Butz algorithm (bit-interleaving + Gray code) for 1-8 dimensions
- `PartitionHilbertSpace`: Equal-width range partitioning of Hilbert space for N shards
- `GetShardId`: Direct key-to-shard mapping via Hilbert value with 2D/16-bit default
- `HilbertValueToKey`: Inverse mapping for range query shard identification
- All methods static with `AggressiveInlining` on hot paths

### LearnedShardRouter
- Piecewise linear CDF model trained from key samples
- `Route`: O(1) amortized via binary search on small boundary array (S segments where S = numShards)
- `volatile CdfModel` reference swap: readers get consistent snapshot, writer builds-then-assigns
- `ShardLoadFactors`: Per-shard normalized load tracking via `Interlocked` counters
- `IsImbalanced(threshold)`: Max/min ratio detection for retrain signaling
- `Retrain`: Full CDF rebuild with counter reset

### BeTreeForest (Level 5 IAdaptiveIndex)
- `List<ForestShard>` with per-shard `AdaptiveIndexEngine` for independent morphing
- Per-shard `SemaphoreSlim(1,1)` for mutations; `ReaderWriterLockSlim` for forest-level split/merge
- **Auto-split**: Triggered when shard exceeds `splitThreshold` (default 10M). Samples up to 1000 keys, finds median, transfers keys > median to new shard, WAL-protects entire operation
- **Auto-merge**: Triggered when shard drops below `splitThreshold/4`. Merges into smallest adjacent neighbor, expands key range, removes empty shard
- **Range query**: Identifies overlapping shards via key range comparison, merge-sorts results
- **Shard inspection**: `ShardCount`, `GetShardLevel(i)`, `GetShardObjectCount(i)` for monitoring

## Deviations from Plan

None - plan executed exactly as written.

## Self-Check: PASSED
- [x] HilbertPartitioner.cs exists
- [x] LearnedShardRouter.cs exists
- [x] BeTreeForest.cs exists
- [x] Build succeeds with 0 errors, 0 warnings
- [x] Commit fa0f56b2 (Task 1) verified
- [x] Commit f360c8a5 (Task 2) verified
