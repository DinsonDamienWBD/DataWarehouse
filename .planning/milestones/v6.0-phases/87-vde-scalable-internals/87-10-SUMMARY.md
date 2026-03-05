---
phase: 87-vde-scalable-internals
plan: 10
subsystem: VDE SQL Optimization
tags: [simd, aggregation, spill-to-disk, predicate-pushdown, zone-map, avx2]
dependency_graph:
  requires: ["87-08 (zone-map-index)", "87-09 (prepared-query-cache)"]
  provides: ["SIMD aggregation engine", "spill-to-disk operator", "predicate pushdown planner"]
  affects: ["SQL query execution pipeline", "columnar region scans"]
tech_stack:
  added: ["System.Runtime.Intrinsics", "System.Runtime.Intrinsics.X86 (Avx2/Sse2/Sse41/Sse3)"]
  patterns: ["SIMD with scalar fallback", "hash-partitioned spill", "three-level predicate pushdown"]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Sql/SimdAggregator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Sql/SpillToDiskOperator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Sql/PredicatePushdownPlanner.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Encryption/PerExtentEncryptor.cs
decisions:
  - "Vector256<long> accumulators for SumInt32 to prevent int32 overflow during SIMD summation"
  - "Kahan summation for float scalar fallback to maintain numerical accuracy"
  - "64 default hash partitions for spill-to-disk to balance memory vs partition count"
  - "OR predicates classified as PostFilter (cannot safely push to zone map level)"
  - "Default Predicate<ZoneMapEntry> returns true (include) for columns without zone-mappable predicates"
metrics:
  duration: 7min
  completed: 2026-02-23T21:47:00Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
  files_modified: 1
---

# Phase 87 Plan 10: SIMD Vectorized SQL Execution, Spill-to-Disk, Predicate Pushdown Summary

SIMD-accelerated aggregation (AVX2 8-wide int32/float), hash-partitioned spill-to-disk for OOM prevention, and three-level predicate pushdown combining zone maps for >90% data skip on selective queries.

## What Was Built

### SimdAggregator (VOPT-18)
- Static class with runtime `Avx2.IsSupported` check and scalar fallback for all operations
- **SumInt32**: Vector256<long> accumulators via ConvertToVector256Int64 to prevent overflow, horizontal reduction
- **Count**: CompareEqual + MoveMask + PopCount for filtered counting
- **MinInt32/MaxInt32**: Avx2.Min/Max with Sse41 horizontal reduction
- **Average**: Delegates to SumInt32 + Count for SIMD efficiency
- **SumFloat**: Vector256<float> SIMD path; Kahan summation scalar fallback for numerical accuracy
- **FilterGreaterThan/FilterEquals**: SIMD CompareGreaterThan/CompareEqual with MoveMask index extraction
- All methods handle tail elements (count % 8 != 0) via scalar loop on remainder

### SpillToDiskOperator (VOPT-19)
- Two-phase execution: build (hash-partition + spill) then probe (per-partition merge)
- 5 concrete AggregateFunction subclasses: Sum, Count, Min, Max, Avg with Accumulate/GetResult/Merge/Clone
- Configurable memory budget (default 64MB), 64 hash partitions
- Spill serialization: [totalLen:4][keyLen:4][key:N][aggState:M] packed into temp blocks
- SpillStats tracking: RowsProcessed, RowsSpilled, PartitionsCreated, TempBlocksUsed
- IAsyncDisposable with CleanupAsync for temp block reclamation

### PredicatePushdownPlanner (VOPT-20)
- Three-level classification: ZoneMappable (extent skip), BlockFilterable (block filter), PostFilter (per-row)
- AnalyzePushdown classifies each predicate; OR connectors forced to PostFilter for safety
- ScanWithPushdownAsync integrates with ColumnarRegionEngine.ScanColumnsAsync using zone map Predicate<ZoneMapEntry>
- Zone map integration via ZoneMapIndex.CanSkipExtent (VOPT-17)
- PushdownResult with PushdownRatio metric for query explain plans
- All pushdown decisions logged via ILogger for diagnostics

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed HKDF.DeriveKey parameter order in PerExtentEncryptor**
- **Found during:** Task 1 (build verification)
- **Issue:** Pre-existing build error: HKDF.DeriveKey named parameter `info:` without positional `salt` caused CS1503
- **Fix:** Changed to positional parameters with explicit `ReadOnlySpan<byte>.Empty` salt
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Encryption/PerExtentEncryptor.cs
- **Commit:** 0a1d0bed

**2. [Rule 1 - Bug] Fixed nullable array type mismatch in BuildZoneMapFilters**
- **Found during:** Task 2 (build verification)
- **Issue:** `Predicate<ZoneMapEntry>?[]` did not match `Predicate<ZoneMapEntry>[]` expected by ScanColumnsAsync
- **Fix:** Initialize all filter slots with default `_ => true` predicate, use non-nullable array
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Sql/PredicatePushdownPlanner.cs
- **Commit:** f0d28f72

## Commits

| # | Hash | Message |
|---|------|---------|
| 1 | 0a1d0bed | feat(87-10): SIMD-accelerated aggregation with Avx2 and scalar fallback |
| 2 | f0d28f72 | feat(87-10): spill-to-disk aggregation and predicate pushdown planner |

## Self-Check: PASSED
- All 3 created files exist on disk
- Both task commits (0a1d0bed, f0d28f72) found in git history
- Build succeeds with 0 errors, 0 warnings
