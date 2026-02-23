---
phase: 88-dynamic-subsystem-scaling
plan: 10
subsystem: compute-workflow-scaling
tags: [wasm, pipeline, scaling, instance-pooling, rollback, backpressure]
dependency_graph:
  requires: ["88-01"]
  provides: ["WasmScalingManager", "PipelineScalingManager"]
  affects: ["UltimateCompute", "UltimateWorkflow"]
tech_stack:
  added: []
  patterns: ["EMA-smoothed dynamic concurrency", "warm instance pooling", "spill-to-disk state capture", "parallel rollback"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateCompute/Scaling/WasmScalingManager.cs
    - Plugins/DataWarehouse.Plugins.UltimateWorkflow/Scaling/PipelineScalingManager.cs
  modified: []
decisions:
  - "volatile long not allowed in C#; use Interlocked.Read/Exchange pattern for _maxStateBytesPerStage"
  - "EMA alpha=0.3 for CPU load smoothing; 70% thread pool + 30% memory pressure weighting"
  - "SemaphoreSlim recreated on reconfigure (not resized) to match existing scaling manager pattern"
metrics:
  duration: 5min
  completed: 2026-02-24T16:12:00Z
---

# Phase 88 Plan 10: WASM & Pipeline Scaling Managers Summary

WASM runtime scaling with per-module configurable limits, EMA-smoothed dynamic concurrency, warm instance pooling, and state persistence. Pipeline scaling with depth limits, semaphore-bounded transactions, per-stage state spill-to-disk, and parallel rollback of independent stages.

## Task 1: WasmScalingManager

Created `WasmScalingManager` implementing `IScalableSubsystem` with:

- **Per-module MaxPages**: `BoundedCache<string, WasmModuleConfig>` with LRU eviction. Each module gets configurable MaxPages (1-65536, default 256). Config persists via `IPersistentBackingStore` with write-through.
- **Dynamic MaxConcurrentExecutions**: Timer-based (10s interval) adjustment using exponential moving average (alpha=0.3). CPU estimation combines thread pool utilization (70%) and GC memory pressure (30%). Scales up to `2 * ProcessorCount` when load <50%, down to `ProcessorCount / 2` when load >80%.
- **Module state persistence**: Serializes linear memory to `dw://internal/wasm-state/{moduleId}` on suspension. Only persists if module opts in via `WasmModuleConfig.PersistState` flag.
- **Warm instance pooling**: `BoundedCache<string, ConcurrentQueue<WasmInstance>>` with LRU eviction of least-used module types. Configurable per-module pool size (default 10). Tracks pool hit/miss rates.

**Commit:** a5aba6a1

## Task 2: PipelineScalingManager

Created `PipelineScalingManager` implementing `IScalableSubsystem` with:

- **Maximum pipeline depth**: Configurable `MaxPipelineDepth` (default 50 stages). `ValidatePipelineDepth()` rejects excessively deep pipelines.
- **Concurrent transaction limits**: `SemaphoreSlim` with configurable timeout (default 30s). Returns false on timeout for backpressure signaling. Default concurrency: `ProcessorCount * 2`.
- **CapturedState size limits**: Per-stage limit of 10MB (configurable). Oversized state spills to `dw://internal/pipeline-state/{pipelineId}/{stageIndex}`. Loaded back on rollback.
- **Parallel rollback**: Identifies independent stages (no `DependsOnStages`) and rolls back in parallel via `Task.WhenAll`. Dependent stages roll back sequentially in reverse order. Returns `PipelineRollbackResult` with error tracking.

**Commit:** 9b3915f8

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Volatile long field not allowed in C#**
- **Found during:** Task 2 build
- **Issue:** `volatile long _maxStateBytesPerStage` produces CS0677 (volatile cannot be long on 32-bit)
- **Fix:** Removed volatile modifier, used `Interlocked.Read`/`Interlocked.Exchange` pattern for all accesses
- **Files modified:** PipelineScalingManager.cs
- **Commit:** 9b3915f8

## Verification

- Both plugins build with 0 errors, 0 warnings
- WASM MaxPages is per-module configurable via BoundedCache (no hardcoded 256 as page limit)
- Instance pool uses BoundedCache with LRU eviction
- Pipeline depth limit enforced via MaxPipelineDepth property and ValidatePipelineDepth()
- CapturedState spills to IPersistentBackingStore when oversized
- SDK builds clean with 0 errors

## Self-Check: PASSED
