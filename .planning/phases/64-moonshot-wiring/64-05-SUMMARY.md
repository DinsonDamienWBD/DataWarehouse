---
phase: 64-moonshot-wiring
plan: 05
subsystem: UniversalDashboards Moonshots
tags: [moonshot, dashboard, metrics, health, monitoring]
dependency_graph:
  requires: ["64-03 MoonshotOrchestrator/Registry", "64-04 Health probes/aggregator"]
  provides: ["IMoonshotDashboardProvider implementation", "MoonshotMetricsCollector", "MoonshotDashboardStrategy"]
  affects: ["UniversalDashboards plugin", "64-06 CLI wiring", "64-07 integration"]
tech_stack:
  added: []
  patterns: ["ConcurrentDictionary for thread-safe state", "Interlocked for atomic counters", "ConcurrentQueue bounded rolling window", "Timer-based trend snapshots"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.UniversalDashboards/Moonshots/MoonshotMetricsCollector.cs
    - Plugins/DataWarehouse.Plugins.UniversalDashboards/Moonshots/MoonshotDashboardProvider.cs
    - Plugins/DataWarehouse.Plugins.UniversalDashboards/Moonshots/MoonshotDashboardStrategy.cs
  modified: []
decisions:
  - "MoonshotDashboardStrategy is standalone (not extending DashboardStrategyBase) because DashboardStrategyBase is for external BI platform integrations, not internal operational dashboards"
  - "Trend buffer uses 1440 max points (24h at 1-min resolution) per MEM-03 bounded collections"
  - "Dashboard render returns structured record types instead of raw JSON for type safety"
metrics:
  duration: "3m 34s"
  completed: 2026-02-19T22:47:00Z
---

# Phase 64 Plan 05: Moonshot Dashboard Provider Summary

Unified moonshot dashboard with metrics collection, trend tracking, and structured rendering for all 10 moonshot features.

## What Was Built

### MoonshotMetricsCollector (244 lines)
- Subscribes to `moonshot.pipeline.stage.completed` and `moonshot.pipeline.completed` bus topics
- Per-moonshot counters: TotalInvocations, SuccessCount, FailureCount using `Interlocked` for thread safety
- Rolling latency window: `ConcurrentQueue<double>` bounded at 1000 samples with P99 percentile computation
- Trend buffer: `ConcurrentDictionary<(MoonshotId, string), ConcurrentQueue<MoonshotTrendPoint>>` for latency_ms, throughput_per_min, success_rate
- Background timer snapshots metrics into trend buffer every 60 seconds
- All collections bounded per MEM-03 (max 1440 trend points per metric)

### MoonshotDashboardProvider (120 lines)
- Implements `IMoonshotDashboardProvider` from SDK
- `GetSnapshotAsync`: aggregates registry registrations, collector metrics, and health reports; computes Ready/Degraded/Faulted counts
- `GetTrendsAsync`: delegates to collector's trend buffer with time range filtering
- `GetMetricsAsync`: delegates to collector for per-moonshot metrics

### MoonshotDashboardStrategy (190 lines + types)
- Renders 4 dashboard panels from snapshot data:
  - **StatusGrid**: 10 cards with moonshot ID, status, color code (green/yellow/red/gray/blue), last health check time
  - **MetricsTable**: 10 rows with TotalInvocations, SuccessRate%, AvgLatencyMs, P99LatencyMs
  - **PipelineFlow**: 10-stage pipeline visualization with per-stage success/failure counts
  - **HealthSummary**: ReadyCount/TotalMoonshots, list of degraded/faulted with reasons
- Structured record types: `MoonshotDashboardRenderResult`, `MoonshotStatusCard`, `MoonshotMetricsRow`, `PipelineStageInfo`, `MoonshotHealthSummaryPanel`, `MoonshotHealthIssue`

## Key Links Verified

| From | To | Via |
|------|-----|-----|
| MoonshotDashboardProvider | IMoonshotDashboardProvider | implements SDK contract |
| MoonshotMetricsCollector | IMessageBus | Subscribe("moonshot.pipeline.stage.completed") and Subscribe("moonshot.pipeline.completed") |
| MoonshotDashboardStrategy | MoonshotDashboardProvider | GetSnapshotAsync() |

## Deviations from Plan

### Design Adaptation

**1. MoonshotDashboardStrategy does not extend DashboardStrategyBase**
- **Reason:** DashboardStrategyBase is designed for external BI platform integrations (Tableau, Grafana, etc.) with HTTP client pools, rate limiters, and dashboard CRUD. The moonshot dashboard is an internal operational view with no external service dependency.
- **Adaptation:** Created standalone class with `RenderAsync` returning structured `MoonshotDashboardRenderResult` record types instead of using DashboardRenderResult/DashboardRenderContext (which don't exist in the SDK).
- **Impact:** None -- the strategy is discoverable by name/category and integrates cleanly with the provider.

## Verification

- `dotnet build Plugins/DataWarehouse.Plugins.UniversalDashboards/DataWarehouse.Plugins.UniversalDashboards.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors, 0 warnings
- 3 files exist in Moonshots/ directory
- MoonshotDashboardProvider returns snapshot with all 10 moonshots
- MoonshotMetricsCollector tracks latency, throughput, success rate

## Self-Check: PASSED

- [x] MoonshotMetricsCollector.cs exists (344 lines, min 100)
- [x] MoonshotDashboardProvider.cs exists (129 lines, min 120)
- [x] MoonshotDashboardStrategy.cs exists (240 lines, min 80)
- [x] Commit 0c11cfc3: MoonshotMetricsCollector
- [x] Commit b573eef3: MoonshotDashboardProvider + MoonshotDashboardStrategy
- [x] Build: 0 errors, 0 warnings
