---
phase: 60-semantic-sync
plan: 06
subsystem: SemanticSync Plugin - Fidelity Control
tags: [bandwidth, fidelity, policy, adaptive, sync]
dependency_graph:
  requires: ["60-01 SDK types", "60-03 classification models"]
  provides: ["ISyncFidelityController implementation", "bandwidth budget tracking", "fidelity policy enforcement"]
  affects: ["sync pipeline bandwidth management", "critical data fidelity guarantees"]
tech_stack:
  added: []
  patterns: ["lock-free concurrency with Interlocked/ConcurrentDictionary", "3-tier budget degradation", "layered policy enforcement"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Fidelity/BandwidthBudgetTracker.cs
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Fidelity/FidelityPolicyEngine.cs
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Fidelity/AdaptiveFidelityController.cs
  modified: []
decisions:
  - "Used internal constructor on AdaptiveFidelityController to resolve accessibility mismatch with internal tracker/engine types"
  - "BandwidthBudgetTracker uses 60-second sliding window for budget utilization calculation"
  - "FidelityPolicyEngine applies layered rules: Critical > Security > Compliance > Default"
metrics:
  duration: "5m 4s"
  completed: "2026-02-19T19:57:27Z"
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
---

# Phase 60 Plan 06: Adaptive Fidelity Controller Summary

Bandwidth-aware fidelity controller with lock-free budget tracking, layered policy enforcement for critical/compliance/security data, and 3-tier budget degradation.

## What Was Built

### BandwidthBudgetTracker (internal, lock-free)
- Thread-safe bandwidth accounting using `Interlocked` and `ConcurrentDictionary` (zero lock statements)
- 60-second sliding window for budget utilization measurement
- Per-fidelity byte consumption tracking with atomic operations
- Pending sync count management via `Interlocked.Increment`/`Decrement` with underflow protection
- `GetCurrentBudget()` returns `FidelityBudget` snapshot with utilization percentage
- `GetRemainingCapacityPercent()` returns 0-100 clamped capacity

### FidelityPolicyEngine (internal)
- Layered policy enforcement with priority ordering:
  1. Critical importance: enforces `MinFidelityForCritical` minimum
  2. Security/encryption tags: enforces `Detailed` minimum
  3. Compliance/audit tags: enforces `Standard` minimum
  4. Default: uses lower of proposed vs. `DefaultFidelity` (budget-conscious)
- Policy never upgrades fidelity above proposed level (constrains downward degradation only)
- Bandwidth threshold lookup via sorted descending dictionary scan
- Defer logic: returns true when budget utilization > 90% AND importance is Negligible or Low

### AdaptiveFidelityController (public, implements ISyncFidelityController)
- Extends `SemanticSyncStrategyBase` with StrategyId `"fidelity-controller-adaptive"`
- 6-step decision flow: bandwidth lookup -> defer check -> policy constraints -> size estimation -> budget degradation -> build decision
- 3-tier budget-aware degradation:
  - **>50% remaining**: use policy-adjusted fidelity as-is
  - **20-50% remaining**: drop one fidelity level (re-apply policy for safety)
  - **<20% remaining**: drop to minimum fidelity policy allows
- Size estimation using fidelity ratios: Full=1.0, Detailed=0.8, Standard=0.5, Summarized=0.15, Metadata=0.02
- Default policy: 100 MB/s=Full, 10 MB/s=Detailed, 1 MB/s=Standard, 100 KB/s=Summarized, 0=Metadata
- `SetPolicyAsync` atomically replaces both policy and engine instance

## Decisions Made

1. **Internal constructor for public class**: `AdaptiveFidelityController` is `public` (implements `ISyncFidelityController`) but its constructor is `internal` because `BandwidthBudgetTracker` and `FidelityPolicyEngine` are `internal`. Only the plugin assembly creates these instances.
2. **Volatile fields for policy engine**: `_policyEngine` and `_currentPolicy` are `volatile` to ensure thread-safe reads without locks when `SetPolicyAsync` replaces them.
3. **Budget utilization window**: 60-second window chosen to balance responsiveness with stability in bandwidth measurements.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed CS0051 accessibility mismatch**
- **Found during:** Task 2
- **Issue:** `AdaptiveFidelityController` is `public` (required by `ISyncFidelityController` interface) but constructor takes `internal` types `BandwidthBudgetTracker` and `FidelityPolicyEngine`
- **Fix:** Made constructor `internal` -- only the plugin assembly creates these instances
- **Files modified:** AdaptiveFidelityController.cs
- **Commit:** 3032d147

## Commits

| Task | Commit     | Description                                          |
| ---- | ---------- | ---------------------------------------------------- |
| 1    | `57cf9fe3` | BandwidthBudgetTracker + FidelityPolicyEngine        |
| 2    | `3032d147` | AdaptiveFidelityController strategy                  |

## Verification Results

- Kernel build: PASSED (0 errors, 0 warnings)
- ISyncFidelityController implementation: CONFIRMED
- Lock-free concurrency (no lock statements): CONFIRMED
- MinFidelityForCritical enforcement: CONFIRMED
- 3-tier budget degradation (>50%, 20-50%, <20%): CONFIRMED
- Defer logic for low-importance data: CONFIRMED

## Self-Check: PASSED

- [x] BandwidthBudgetTracker.cs exists
- [x] FidelityPolicyEngine.cs exists
- [x] AdaptiveFidelityController.cs exists
- [x] Commit 57cf9fe3 exists
- [x] Commit 3032d147 exists
