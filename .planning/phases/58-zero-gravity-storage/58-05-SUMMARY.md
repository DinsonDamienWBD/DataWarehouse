---
phase: 58-zero-gravity-storage
plan: 05
subsystem: SDK Storage Placement
tags: [sdk, placement, gravity, scoring, optimizer]
dependency_graph:
  requires:
    - IPlacementAlgorithm (58-01)
    - IPlacementOptimizer (58-01)
    - PlacementTypes (58-01)
  provides:
    - GravityAwarePlacementOptimizer
    - GravityScoringWeights
    - AccessMetrics
    - ColocationMetrics
    - CostMetrics
    - LatencyMetrics
    - ComplianceMetrics
  affects:
    - Plan 07 rebalancer service (uses optimizer for move planning)
    - Any gravity-aware placement consumer
tech_stack:
  added: []
  patterns:
    - Func<T> metric providers for dependency injection without heavy interfaces
    - SemaphoreSlim bounded parallelism for batch scoring
    - Logarithmic normalization for access frequency scaling
    - Positional record constructors for immutable gravity scores
key_files:
  created:
    - DataWarehouse.SDK/Storage/Placement/GravityScoringWeights.cs
    - DataWarehouse.SDK/Storage/Placement/GravityAwarePlacementOptimizer.cs
  modified: []
decisions:
  - "Func delegates for metric providers instead of full interfaces (lightweight, composable)"
  - "Logarithmic scaling for access frequency (linear would over-weight hot data)"
  - "0.7 gravity threshold for primary node promotion (balances stability vs optimization)"
  - "$0.12/GB and 200ms as normalization reference points for egress cost and latency"
metrics:
  duration: 145s
  completed: 2026-02-20
---

# Phase 58 Plan 05: Gravity-Aware Placement Optimizer Summary

Multi-dimensional gravity scoring optimizer with 5 weighted dimensions (access, colocation, egress, latency, compliance), 4 preset weight profiles, and CRUSH-delegated placement with high-gravity primary node promotion.

## What Was Done

### Task 1: GravityScoringWeights Configuration
Created `DataWarehouse.SDK/Storage/Placement/GravityScoringWeights.cs`:
- **GravityScoringWeights** sealed record with 5 dimension weights (AccessFrequency=0.30, Colocation=0.20, EgressCost=0.15, Latency=0.15, Compliance=0.20)
- 4 preset profiles: Default, CostOptimized, PerformanceOptimized, ComplianceFirst
- `IsValid()` validates range [0,1] and positive sum
- `Normalize()` scales weights to sum to 1.0

### Task 2: GravityAwarePlacementOptimizer Implementation
Created `DataWarehouse.SDK/Storage/Placement/GravityAwarePlacementOptimizer.cs`:
- **GravityAwarePlacementOptimizer** implements `IPlacementOptimizer` with all 4 interface methods
- `ComputeGravityAsync` -- gathers 5 metric dimensions from injectable Func providers, produces weighted composite score [0,1]
- `ComputeGravityBatchAsync` -- parallel scoring with `SemaphoreSlim(ProcessorCount * 2)` bounded concurrency
- `OptimizePlacementAsync` -- delegates to CRUSH, then promotes current node to primary for high-gravity objects (score > 0.7)
- `GenerateRebalancePlanAsync` -- calculates ideal weight distribution, actual enumeration deferred to Plan 07 rebalancer
- 5 metric input records with `[SdkCompatibility]`: AccessMetrics, ColocationMetrics, CostMetrics, LatencyMetrics, ComplianceMetrics

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed positional record construction**
- **Found during:** Task 2
- **Issue:** Plan used object initializer syntax for positional records (DataGravityScore, RebalancePlan) which would not compile
- **Fix:** Used named constructor arguments matching the positional record definitions in PlacementTypes.cs
- **Files modified:** GravityAwarePlacementOptimizer.cs

**2. [Rule 2 - Missing functionality] Added null/argument validation**
- **Found during:** Task 2
- **Issue:** Plan code lacked input validation on public API methods
- **Fix:** Added ArgumentNullException.ThrowIfNull and ArgumentException.ThrowIfNullOrWhiteSpace checks on all public methods
- **Files modified:** GravityAwarePlacementOptimizer.cs

**3. [Rule 2 - Missing functionality] Added ConfigureAwait(false)**
- **Found during:** Task 2
- **Issue:** Plan code lacked ConfigureAwait(false) on awaited tasks (library code best practice)
- **Fix:** Added ConfigureAwait(false) to all await expressions
- **Files modified:** GravityAwarePlacementOptimizer.cs

## Verification

- SDK build: 0 errors, 0 warnings
- GravityAwarePlacementOptimizer implements IPlacementOptimizer (all 4 methods)
- 5 scoring dimensions with configurable weights
- 4 preset weight profiles available (Default, CostOptimized, PerformanceOptimized, ComplianceFirst)
- All types annotated with [SdkCompatibility("5.0.0")]

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 1fa588d1 | feat(58-05): add GravityScoringWeights configuration |
| 2 | 95f87d1f | feat(58-05): implement GravityAwarePlacementOptimizer |

## Self-Check: PASSED

All 2 files verified present. Both commits (1fa588d1, 95f87d1f) confirmed in git log.
