---
phase: "62"
plan: "05"
subsystem: "Green Tiering"
tags: [carbon-aware, green-tiering, cold-data, migration, sustainability, scheduling]
dependency_graph:
  requires:
    - "62-01 SDK Carbon Contracts (GreenScore, CarbonBudget, ThrottleLevel, CarbonThrottleDecision)"
    - "62-03 Carbon Budget Enforcement (budget evaluate via message bus)"
  provides:
    - "GreenTieringPolicyEngine for per-tenant tiering configuration"
    - "GreenTieringStrategy for cold data detection and migration batch planning"
    - "ColdDataCarbonMigrationStrategy for migration execution with carbon savings tracking"
    - "Message bus topics: sustainability.green-tiering.batch.planned, sustainability.green-tiering.batch.complete"
  affects:
    - "62-06 and any future sustainability reporting plans (migration statistics)"
    - "Storage layer via storage.list, storage.health, storage.migrate.request topics"
tech_stack:
  added: []
  patterns:
    - "SemaphoreSlim for concurrent migration throttling"
    - "ConcurrentQueue bounded history with automatic pruning"
    - "Atomic JSON persistence via temp file + rename"
    - "Green score differential for carbon savings projection over estimated data lifetime"
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenTiering/GreenTieringPolicyEngine.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenTiering/GreenTieringStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenTiering/ColdDataCarbonMigrationStrategy.cs
  modified: []
decisions:
  - "Used GreenTiering namespace (no collision unlike CarbonBudget namespace issue in 62-03)"
  - "Carbon budget check uses message bus request-response with conservative fallback (skip migration on failure)"
  - "Carbon savings formula: green score differential * base intensity * storage energy * lifetime projection (3 years)"
  - "Migration history bounded at 10K entries with automatic pruning"
metrics:
  duration: "373s"
  completed: "2026-02-19"
---

# Phase 62 Plan 05: Green Tiering Summary

Automatic cold data migration from high-carbon to lowest-carbon backends with per-tenant policy configuration, carbon budget enforcement, and low-carbon window scheduling.

## What Was Built

### Task 1: Green Tiering Policy Engine and Detection Strategy (1036 lines)

**GreenTieringPolicyEngine.cs (312 lines):**
- Per-tenant policy stored in `ConcurrentDictionary<string, GreenTieringPolicy>`
- `GreenTieringPolicy` record: ColdThreshold (30d default), TargetGreenScore (80), MigrationSchedule, MaxMigrationBatchSizeBytes (10GB), MaxConcurrentMigrations (5), RespectCarbonBudget, Enabled
- `GreenMigrationSchedule` enum: Immediate, LowCarbonWindowOnly, DailyBatch, WeeklyBatch
- JSON persistence with atomic writes (temp file + rename), debounced 5-second save timer
- Policy validation: positive thresholds, score range 0-100, valid batch hour
- Commit: `ef78d929`

**GreenTieringStrategy.cs (724 lines):**
- Extends `SustainabilityStrategyBase`, Category: Scheduling, Capabilities: Scheduling | CarbonCalculation | ActiveControl
- `IdentifyColdDataAsync`: queries `storage.list` for tenant objects, filters by ColdThreshold and backend green score
- `PlanMigrationBatchAsync`: selects highest-scoring green backend, estimates migration carbon cost, checks budget via `sustainability.carbon.budget.evaluate`
- `ShouldMigrateNowAsync`: Immediate (always), LowCarbonWindowOnly (below 24h average), DailyBatch (02:00-04:00 UTC), WeeklyBatch (Sunday 02:00-04:00 UTC)
- Background timer: every 15 minutes, scans enabled tenants, plans and queues batches
- Publishes `sustainability.green-tiering.batch.planned` events
- Commit: `ef78d929`

### Task 2: Cold Data Carbon Migration Execution (741 lines)

**ColdDataCarbonMigrationStrategy.cs (741 lines):**
- Extends `SustainabilityStrategyBase`, Category: Scheduling, Capabilities: ActiveControl | CarbonCalculation | Reporting
- `ExecuteBatchAsync`: validates target health via `storage.health`, executes per-object migrations via `storage.migrate.request` with SemaphoreSlim concurrency control
- Carbon savings formula: `(sourceIntensity - targetIntensity) * storageEnergyPerByte * sizeBytes * lifetimeYears` where energy = 6 nWh/byte/year for SSD
- Migration history in bounded `ConcurrentQueue<MigrationRecord>` (max 10,000 entries)
- Retry queue for failed migrations
- Generates recommendations: retry queue growth, low success rate, annual carbon savings report, default placement optimization
- Publishes `sustainability.green-tiering.batch.complete` with full metrics
- Commit: `e88e090c`

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- Plugin build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
- All 3 files exist in `Strategies/GreenTiering/`
- GreenTieringPolicyEngine: 312 lines (min 120 required)
- GreenTieringStrategy: 724 lines (min 200 required)
- ColdDataCarbonMigrationStrategy: 741 lines (min 150 required)
- Message bus topics: storage.list, sustainability.placement.scores, sustainability.carbon.budget.evaluate, storage.health, storage.migrate.request, sustainability.green-tiering.batch.planned, sustainability.green-tiering.batch.complete
- No direct plugin references -- all communication via message bus
- Carbon budget respected before migration (ThrottleLevel.Hard = skip)

## Decisions Made

1. **GreenTiering namespace**: No collision (unlike 62-03's CarbonBudget namespace issue)
2. **Conservative budget check fallback**: If budget evaluation fails, skip migration rather than risk budget overrun
3. **Carbon savings formula**: Uses green score as inverse carbon fraction (score 100 = 0% fossil, score 0 = 100% fossil) * base 400 gCO2e/kWh * 6 nWh/byte/year * 3 year lifetime
4. **10K history limit**: Bounded migration history prevents unbounded memory growth in long-running systems

## Self-Check: PASSED

- All 3 created files exist on disk
- Commit ef78d929 found in git log
- Commit e88e090c found in git log
- Plugin build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
