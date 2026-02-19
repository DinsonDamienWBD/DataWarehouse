---
phase: "62"
plan: "03"
subsystem: "Carbon Budget Enforcement"
tags: [carbon-aware, budget, throttling, sustainability, tenant, enforcement]
dependency_graph:
  requires:
    - "62-01 SDK Carbon Contracts (CarbonBudget, ICarbonBudgetService, CarbonThrottleDecision, ThrottleLevel)"
  provides:
    - "CarbonBudgetStore for persistent per-tenant budget tracking"
    - "CarbonBudgetEnforcementStrategy implementing ICarbonBudgetService"
    - "CarbonThrottlingStrategy with ThrottleIfNeededAsync for plugin consumption"
    - "Message bus topics: sustainability.carbon.budget.* and sustainability.carbon.throttle.applied"
  affects:
    - "Plans 62-04 through 62-06 (consumers of budget enforcement)"
    - "Any plugin that needs carbon-aware operation gating"
tech_stack:
  added: []
  patterns:
    - "Progressive throttling with linear delay scaling (100ms at 80% to 2000ms at 100%)"
    - "Debounced file persistence with atomic writes (temp file + rename)"
    - "ConcurrentDictionary with per-entry locking for thread-safe budget mutations"
    - "Message bus request-response for decoupled throttle evaluation"
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonBudget/CarbonBudgetStore.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonBudget/CarbonBudgetEnforcementStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/CarbonBudget/CarbonThrottlingStrategy.cs
  modified: []
decisions:
  - "Used namespace CarbonBudgetEnforcement instead of CarbonBudget to avoid CS0101 conflict with existing CarbonBudget record type in parent namespace"
  - "Throttle evaluation uses message bus request-response with direct-call fallback for resilience"
  - "Energy-to-carbon conversion uses bus request for grid intensity with configurable default fallback (400 gCO2e/kWh)"
metrics:
  duration: "512s"
  completed: "2026-02-19"
---

# Phase 62 Plan 03: Carbon Budget Enforcement Summary

Per-tenant carbon budget enforcement with persistent JSON storage, progressive throttling (none/soft delay/hard reject), and message bus integration for energy-to-carbon auto-recording.

## What Was Built

### Task 1: Carbon Budget Persistent Store (CarbonBudgetStore.cs, 388 lines)
- **ConcurrentDictionary** with per-entry object locking for thread-safe budget mutations
- **JSON file persistence** via System.Text.Json with atomic writes (write to .tmp then rename)
- **Debounced auto-save**: mutations trigger a 5-second timer to batch disk writes
- **Period advancement**: expired budgets automatically advance (supports hour/day/week/month/quarter/year)
- **Multi-period catch-up**: handles budgets that expired multiple periods ago
- Commit: `b8af02ff`

### Task 2: Budget Enforcement and Throttling (877 lines total)

**CarbonBudgetEnforcementStrategy.cs (559 lines):**
- Extends `SustainabilityStrategyBase`, implements `ICarbonBudgetService`
- Category: CarbonAwareness, Capabilities: ActiveControl | CarbonCalculation | Alerting
- `GetBudgetAsync`: returns stored budget or default unlimited budget for unknown tenants
- `SetBudgetAsync`: creates/updates budget with computed period boundaries, preserves usage if same period
- `CanProceedAsync`: projects usage including estimated carbon against hard limit
- `RecordUsageAsync`: atomic increment, threshold check, publishes usage/threshold/exhausted events
- `EvaluateThrottleAsync`: None (<80%), Soft (80-100%, linear delay 100-2000ms), Hard (>100%, reject)
- Background timer resets expired budgets every 60 seconds
- Subscribes to `sustainability.energy.measured` for auto-recording (converts Wh to gCO2e via grid intensity)
- Subscribes to `sustainability.carbon.budget.evaluate` for request-response throttle queries
- Recommendation generation for frequently throttled tenants

**CarbonThrottlingStrategy.cs (318 lines):**
- Extends `SustainabilityStrategyBase`
- Category: CarbonAwareness, Capabilities: ActiveControl | Scheduling
- `ThrottleIfNeededAsync(tenantId, ct)`: evaluates via bus (with direct fallback), applies delay or rejects
- `ThrottleResult` enum: Allowed, Delayed, Rejected
- Per-tenant throttle statistics tracking (evaluations, allowed, delayed, rejected counts)
- Publishes `sustainability.carbon.throttle.applied` for observability
- Generates recommendations when tenant throttle rate exceeds 20%

Commit: `b4dd8c33`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Namespace collision with existing CarbonBudget type**
- **Found during:** Task 1
- **Issue:** The plan specified namespace `...Strategies.CarbonBudget` but `CarbonBudget` is already a type name in the parent `...Strategies` namespace (in SustainabilityEnhancedStrategies.cs), causing CS0101 error
- **Fix:** Used namespace `CarbonBudgetEnforcement` while keeping files in the `CarbonBudget/` directory as specified
- **Files modified:** All 3 new files
- **Commit:** `b8af02ff`, `b4dd8c33`

## Verification

- Plugin build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
- All 3 files exist in `Strategies/CarbonBudget/`
- CarbonBudgetEnforcementStrategy implements ICarbonBudgetService (3 references confirmed)
- CarbonBudgetStore: 388 lines (min 150 required)
- CarbonBudgetEnforcementStrategy: 559 lines (min 200 required)
- CarbonThrottlingStrategy: 318 lines (min 120 required)
- Message bus topics follow convention: sustainability.carbon.budget.{usage,threshold,exhausted,set,evaluate}, sustainability.carbon.throttle.applied

## Decisions Made

1. **Namespace CarbonBudgetEnforcement**: Avoids CS0101 collision with existing `CarbonBudget` record in parent namespace
2. **Bus + direct fallback**: Throttling strategy tries message bus first for decoupled evaluation, falls back to direct method call on failure
3. **Default 400 gCO2e/kWh**: Conservative global average carbon intensity used when no real-time grid data available from bus
4. **Per-entry locking**: Each budget entry has its own lock object for fine-grained concurrency (no global lock contention)

## Self-Check: PASSED

- All 3 created files exist on disk
- Commit b8af02ff found in git log
- Commit b4dd8c33 found in git log
- Plugin build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
