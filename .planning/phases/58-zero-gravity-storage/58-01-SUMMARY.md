---
phase: 58-zero-gravity-storage
plan: 01
subsystem: SDK Storage Contracts
tags: [sdk, contracts, placement, billing, migration, zero-gravity]
dependency_graph:
  requires: []
  provides:
    - IPlacementAlgorithm
    - IPlacementOptimizer
    - IRebalancer
    - IBillingProvider
    - IMigrationEngine
    - PlacementTypes (NodeDescriptor, PlacementTarget, PlacementDecision, DataGravityScore, RebalanceJob/Plan/Move/Options)
    - BillingTypes (BillingReport, CostBreakdown, SpotPricing, ReservedCapacity, CostForecast, CostRecommendation)
    - MigrationTypes (MigrationJob, MigrationPlan, MigrationObject, ReadForwardingEntry, MigrationCheckpoint)
  affects:
    - Wave 2+ plans implementing against these contracts
tech_stack:
  added: []
  patterns:
    - Sealed records for immutable value types
    - Sync interfaces for pure math (IPlacementAlgorithm)
    - Async interfaces with CancellationToken for I/O-bound operations
    - IAsyncEnumerable for real-time monitoring streams
key_files:
  created:
    - DataWarehouse.SDK/Storage/Placement/PlacementTypes.cs
    - DataWarehouse.SDK/Storage/Placement/IPlacementAlgorithm.cs
    - DataWarehouse.SDK/Storage/Placement/IPlacementOptimizer.cs
    - DataWarehouse.SDK/Storage/Placement/IRebalancer.cs
    - DataWarehouse.SDK/Storage/Billing/BillingTypes.cs
    - DataWarehouse.SDK/Storage/Billing/IBillingProvider.cs
    - DataWarehouse.SDK/Storage/Migration/MigrationTypes.cs
    - DataWarehouse.SDK/Storage/Migration/IMigrationEngine.cs
  modified: []
decisions:
  - "Sync methods on IPlacementAlgorithm (CRUSH is pure math, no I/O)"
  - "Separate enums for PlacementConstraintType, RebalanceStatus, MigrationStatus, CostCategory, RecommendationType"
  - "All public types annotated with SdkCompatibility 5.0.0"
metrics:
  duration: 219s
  completed: 2026-02-19
---

# Phase 58 Plan 01: Zero-Gravity Storage Core Type System Summary

SDK contracts for placement (CRUSH-equivalent deterministic + gravity-aware optimizer + rebalancer), cloud billing (multi-provider cost tracking with spot/reserved/forecast), and zero-downtime migration (checkpointed moves with read-forwarding).

## What Was Done

### Task 1: Placement and Rebalancer SDK Contracts
Created 4 files in `DataWarehouse.SDK/Storage/Placement/`:
- **PlacementTypes.cs** -- 12 types: StorageClass enum (11 tiers from Hot to Holographic), PlacementConstraintType enum, RebalanceStatus enum, NodeDescriptor, PlacementTarget, PlacementDecision, PlacementConstraint, DataGravityScore, RebalanceMove, RebalancePlan, RebalanceJob, RebalanceOptions records
- **IPlacementAlgorithm.cs** -- 3 synchronous methods: ComputePlacement, RecomputeOnNodeChange, EstimateMovementOnResize. Sync because CRUSH is pure math.
- **IPlacementOptimizer.cs** -- 4 async methods: ComputeGravityAsync, ComputeGravityBatchAsync, OptimizePlacementAsync, GenerateRebalancePlanAsync
- **IRebalancer.cs** -- 7 async methods: StartRebalanceAsync, PauseRebalanceAsync, ResumeRebalanceAsync, CancelRebalanceAsync, GetStatusAsync, ListJobsAsync, MonitorAsync (IAsyncEnumerable)

### Task 2: Billing and Migration SDK Contracts
Created 4 files in `DataWarehouse.SDK/Storage/Billing/` and `Migration/`:
- **BillingTypes.cs** -- 10 types: CloudProvider enum (7 providers), CostCategory enum (8 categories), RecommendationType enum (5 types), BillingReport, CostBreakdown, SpotPricing, ReservedCapacity, CostForecast, CostRecommendation records
- **IBillingProvider.cs** -- 5 async methods + Provider property: GetBillingReportAsync, GetSpotPricingAsync, GetReservedCapacityAsync, ForecastCostAsync, ValidateCredentialsAsync
- **MigrationTypes.cs** -- 7 types: MigrationStatus enum (9 states), MigrationObject, MigrationPlan, MigrationJob, ReadForwardingEntry, MigrationCheckpoint records
- **IMigrationEngine.cs** -- 8 methods: StartMigrationAsync, PauseMigrationAsync, ResumeMigrationAsync, CancelMigrationAsync, GetStatusAsync, ListJobsAsync, MonitorAsync (IAsyncEnumerable), GetForwardingEntryAsync

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- SDK build: 0 errors, 0 warnings
- All 8 files exist in correct directories
- No plugin namespace references found
- All public types have [SdkCompatibility("5.0.0")] attribute

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 66375017 | feat(58-01): add placement and rebalancer SDK contracts |
| 2 | 9d6f6e0c | feat(58-01): add billing and migration SDK contracts |

## Self-Check: PASSED

All 8 files verified present. Both commits (66375017, 9d6f6e0c) confirmed in git log.
