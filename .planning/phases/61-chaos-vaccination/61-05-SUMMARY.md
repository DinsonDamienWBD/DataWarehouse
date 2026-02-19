---
phase: 61-chaos-vaccination
plan: 05
subsystem: chaos-vaccination
tags: [scheduling, cron, results-database, in-memory, chaos-engineering]
dependency_graph:
  requires: ["61-01", "61-02"]
  provides: ["vaccination-scheduler", "cron-parser", "chaos-results-database"]
  affects: ["61-06", "61-07"]
tech_stack:
  added: []
  patterns: ["cron-scheduling", "weighted-random-selection", "FIFO-eviction", "time-window-constraints"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.ChaosVaccination/Scheduling/CronParser.cs
    - Plugins/DataWarehouse.Plugins.ChaosVaccination/Scheduling/VaccinationScheduler.cs
    - Plugins/DataWarehouse.Plugins.ChaosVaccination/Storage/InMemoryChaosResultsDatabase.cs
  modified: []
decisions:
  - "Built custom cron parser instead of adding NuGet dependency (Rule 13)"
  - "Used ConcurrentDictionary + lock for FIFO eviction rather than lock-free approach for simplicity and correctness"
  - "FaultType filtering in results uses FaultSignature when available since ExperimentRecord only exposes ChaosExperimentResult"
metrics:
  duration: "4m 32s"
  completed: "2026-02-19"
---

# Phase 61 Plan 05: Vaccination Scheduler and Results Database Summary

Cron-based and interval-based vaccination scheduler with time window constraints, plus bounded in-memory results database with FIFO eviction and aggregate statistics.

## What Was Built

### CronParser (Lightweight Cron Expression Parser)
- Parses standard 5-field cron: minute hour day-of-month month day-of-week
- Supports: wildcard (*), step (*/N), range (N-M), list (N,M,O), combined (N-M/S)
- `CronSchedule.GetNextOccurrence()` with fast-forward optimization (skips non-matching months/days/hours)
- `CronSchedule.Matches()` for minute-level precision checks
- Clear FormatException messages for invalid expressions
- No external NuGet packages -- fully self-contained

### VaccinationScheduler (implements IVaccinationScheduler)
- Two scheduling modes: cron-based (via CronParser) and interval-based (millisecond intervals)
- Background Timer with 1-minute tick evaluates all enabled schedules
- Time window constraints: timezone-aware day-of-week + hour range checks using TimeZoneInfo
- Weighted random experiment selection across scheduled experiments
- Concurrent experiment limits per schedule (MaxConcurrent enforcement)
- Double-execution prevention: tracks last run time per schedule at minute granularity
- Fire-and-forget experiment execution with error isolation (scheduler never crashes)
- Full CRUD: AddScheduleAsync, RemoveScheduleAsync, GetSchedulesAsync, GetScheduleAsync
- EnableScheduleAsync toggles without removing schedule
- GetNextRunTimeAsync computes next occurrence respecting time windows
- IDisposable + IAsyncDisposable for clean timer shutdown
- Message bus integration for schedule lifecycle events

### InMemoryChaosResultsDatabase (implements IChaosResultsDatabase)
- ConcurrentDictionary storage keyed by experiment ID
- Bounded capacity: configurable max records (default 100,000) with FIFO eviction
- ConcurrentQueue tracks insertion order; eviction synchronized with lock
- StoreAsync: add + evict + publish "chaos.results.stored"
- QueryAsync: filters by date range (pre-filtered first), fault types, statuses, plugin IDs
- GetByIdAsync: direct dictionary lookup
- GetSummaryAsync: success rate, average recovery time, fault type distribution, blast radius distribution
- PurgeAsync: removes records older than cutoff, rebuilds insertion order queue

## Deviations from Plan

None -- plan executed exactly as written.

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 16e3d223 | CronParser and VaccinationScheduler |
| 2 | a5afe3a6 | InMemoryChaosResultsDatabase |

## Verification

- Plugin builds with zero errors
- Kernel builds with zero errors
- VaccinationScheduler implements IVaccinationScheduler
- InMemoryChaosResultsDatabase implements IChaosResultsDatabase
- CronParser handles standard 5-field cron expressions
- No external NuGet packages added
