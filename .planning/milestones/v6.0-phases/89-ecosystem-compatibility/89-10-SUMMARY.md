---
phase: 89-ecosystem-compatibility
plan: 10
subsystem: Jepsen Test Scenarios & Report Generator
tags: [jepsen, distributed-testing, correctness, linearizability, raft, crdt, mvcc, report-generation]
dependency_graph:
  requires: ["89-09 (JepsenTestHarness, JepsenFaultInjection, JepsenWorkloadGenerators)"]
  provides: ["JepsenTestScenarios", "JepsenReportGenerator", "JepsenReport", "ScenarioResult", "OperationTimeline", "TimelineEntry"]
  affects: ["Ecosystem correctness verification", "CI/CD integration"]
tech_stack:
  added: []
  patterns: ["Factory method scenarios", "StringBuilder HTML generation", "System.Text.Json serialization"]
key_files:
  created:
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenTestScenarios.cs
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenReportGenerator.cs
  modified:
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenTestHarness.cs
key_decisions:
  - "Added Tags and ClusterConfig properties to JepsenTestPlan for scenario metadata"
  - "Heuristic fault-active detection via 2x average failure rate threshold"
  - "3-node vs 5-node cluster topology auto-detected from scenario name for diagram rendering"
metrics:
  duration: 4min
  completed: 2026-02-24T00:12:17Z
---

# Phase 89 Plan 10: Jepsen Test Scenarios & Report Generator Summary

Defined 7 Jepsen test scenarios covering all distributed correctness properties and a report generator producing HTML/JSON/Markdown output.

## Tasks Completed

### Task 1: Jepsen Test Scenarios (a24c8e17)
Created `JepsenTestScenarios` static class with 7 factory methods:

1. **LinearizabilityUnderPartition** - 5-node, RegisterWorkload, MajorityMinority partition at T+10s for 30s, 10 clients
2. **RaftLeaderElectionUnderPartition** - 5-node, RegisterWorkload, ProcessKill leader at T+15s, 5 clients
3. **CrdtConvergenceUnderDelay** - 5-node, SetWorkload, ClockSkew 500ms-2s on 2 nodes at T+5s for 40s, 10 clients
4. **WalCrashRecovery** - 3-node, ListAppendWorkload, ProcessKill at T+10s, 5 clients
5. **MvccSnapshotIsolation** - 3-node, BankWorkload, no faults, 20 clients
6. **DvvReplicationConsistency** - 5-node, RegisterWorkload+CAS, HalfAndHalf partition at T+10s for 20s, 10 clients
7. **SplitBrainPrevention** - 5-node, RegisterWorkload, MajorityMinority at T+5s for 45s, 10 clients

Also added `Tags` and `ClusterConfig` properties to `JepsenTestPlan` record for scenario categorization. `GetAllScenarios()` returns all 7.

### Task 2: Jepsen Report Generator (5e6a6c11)
Created `JepsenReportGenerator` static class with:

- `GenerateReport(IReadOnlyList<JepsenTestResult>)` - aggregates results into `JepsenReport` with per-scenario `ScenarioResult`
- `GenerateHtmlReport(JepsenReport)` - full HTML page with CSS styling, summary table (green/red), per-scenario sections, operation timeline bars, fault injection timeline, text-based cluster topology diagrams
- `GenerateJsonReport(JepsenReport, bool truncateHistory)` - structured JSON for CI/CD with optional history truncation
- `GenerateMarkdownSummary(JepsenReport)` - concise table for release notes with overall verdict and confidence statement

Supporting records: `JepsenReport`, `ScenarioResult`, `OperationTimeline`, `TimelineEntry`.

## Deviations from Plan

None - plan executed exactly as written.

## Verification
- Build: `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` = 0 errors, 0 warnings
- GetAllScenarios() returns 7 test plans
- Each scenario has proper fault schedules, workload configurations, and expected consistency models
- Report generator produces HTML with scenario pass/fail table, JSON for machine parsing, Markdown summary

## Self-Check: PASSED
- [x] DataWarehouse.SDK/Contracts/Ecosystem/JepsenTestScenarios.cs exists
- [x] DataWarehouse.SDK/Contracts/Ecosystem/JepsenReportGenerator.cs exists
- [x] Commit a24c8e17 exists
- [x] Commit 5e6a6c11 exists
