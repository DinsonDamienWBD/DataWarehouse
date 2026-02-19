---
phase: 58-zero-gravity-storage
plan: 08
subsystem: Storage/Placement
tags: [rebalancer, autonomous, gravity, migration, background-service]
dependency_graph:
  requires: [58-04, 58-05, 58-07]
  provides: [AutonomousRebalancer, RebalancerOptions]
  affects: [cluster-balance, data-placement, migration-engine]
tech_stack:
  added: []
  patterns: [timer-based-monitoring, interlocked-concurrency, positional-records, async-enumerable]
key_files:
  created:
    - DataWarehouse.SDK/Storage/Placement/RebalancerOptions.cs
    - DataWarehouse.SDK/Storage/Placement/AutonomousRebalancer.cs
  modified: []
decisions:
  - "Used Task<(int,int)> return values instead of ref parameters for async migration tracking"
  - "Batch migrations grouped by source-target node pair for efficiency"
metrics:
  duration: 210s
  completed: 2026-02-19T18:34:52Z
  tasks: 2/2
  files_created: 2
  files_modified: 0
---

# Phase 58 Plan 08: Autonomous Rebalancer Summary

Continuous background rebalancer using CRUSH + gravity optimizer to detect cluster imbalance, generate minimal-disruption migration plans, and execute them via the migration engine with configurable budgets and quiet hours.

## What Was Built

### RebalancerOptions (Task 1)
Configuration record for all rebalancer behavior including check intervals, imbalance thresholds, concurrent migration limits, cost/egress budgets, gravity protection, throttling, quiet hours, and checksum validation. Three presets: Default (balanced), Aggressive (maintenance windows), and Conservative (production).

### AutonomousRebalancer (Task 2)
Full IRebalancer implementation with:
- **Monitor loop**: Timer-based imbalance check every `CheckInterval` using usage ratio spread across nodes
- **Plan generation**: Delegates to `IPlacementOptimizer.GenerateRebalancePlanAsync` with gravity-aware scoring
- **Execution**: Groups moves by source-target pair, creates `MigrationPlan` per group, delegates to `IMigrationEngine`
- **Lifecycle**: Start/pause/resume/cancel with thread-safe ConcurrentDictionary tracking
- **Quiet hours**: Skips rebalancing during configured UTC hour ranges (handles midnight wraparound)
- **Concurrency control**: `Interlocked.CompareExchange` prevents overlapping monitor runs; configurable max concurrent migrations
- **Real-time monitoring**: `IAsyncEnumerable<RebalanceJob>` stream for job progress

## Key Integration Points

| Component | Integration | Pattern |
|-----------|------------|---------|
| IPlacementOptimizer | Generates rebalance plans | `_optimizer.GenerateRebalancePlanAsync` |
| IMigrationEngine | Executes data moves | `_migrationEngine.StartMigrationAsync` + `MonitorAsync` |
| IPlacementAlgorithm | Available for ideal placement queries | Constructor-injected |
| ClusterMapProvider | Host-provided cluster topology | `Func<CancellationToken, Task<IReadOnlyList<NodeDescriptor>>>` |

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 392bc14f | RebalancerOptions with 3 presets |
| 2 | 4372892a | AutonomousRebalancer implementation |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Async ref parameter compile error**
- **Found during:** Task 2
- **Issue:** Plan's code used `ref int completed, ref int failed` parameters in async method `ExecuteSingleMigrationAsync`, which is illegal in C#
- **Fix:** Changed to `Task<(int Completed, int Failed)>` return type with aggregation after `Task.WhenAll`
- **Files modified:** AutonomousRebalancer.cs
- **Commit:** 4372892a

**2. [Rule 1 - Bug] Nullable dereference in pause loop**
- **Found during:** Task 2
- **Issue:** `_jobs.TryGetValue` inside while loop could set `out` variable to null, causing CS8602
- **Fix:** Used separate local variable with explicit null check for updated job state
- **Files modified:** AutonomousRebalancer.cs
- **Commit:** 4372892a

**3. [Rule 2 - Critical] Added null argument validation**
- **Found during:** Task 2
- **Issue:** Constructor did not validate null dependencies
- **Fix:** Added `ArgumentNullException.ThrowIfNull` for all constructor parameters
- **Files modified:** AutonomousRebalancer.cs
- **Commit:** 4372892a

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- Build succeeded, 0 errors, 0 warnings
- AutonomousRebalancer implements all 7 IRebalancer methods (StartRebalanceAsync, PauseRebalanceAsync, ResumeRebalanceAsync, CancelRebalanceAsync, GetStatusAsync, ListJobsAsync, MonitorAsync)
- Uses IPlacementOptimizer for plan generation and IMigrationEngine for execution
- Imbalance threshold and gravity protection threshold respected in CheckAndRebalanceAsync
- SdkCompatibility("5.0.0") attribute on both types

## Self-Check: PASSED

- RebalancerOptions.cs: FOUND
- AutonomousRebalancer.cs: FOUND
- Commit 392bc14f: FOUND
- Commit 4372892a: FOUND
