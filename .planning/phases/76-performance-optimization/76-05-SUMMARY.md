---
phase: 76-performance-optimization
plan: 05
subsystem: Policy Performance Optimization
tags: [simulation, sandbox, impact-report, what-if, PADV-02]
dependency_graph:
  requires: [76-04]
  provides: [PolicySimulationSandbox, SimulationImpactReport]
  affects: [policy-engine, operator-tooling]
tech_stack:
  added: []
  patterns: [isolated-sandbox-clone, before-after-comparison, severity-classification, deployment-tier-projection]
key_files:
  created:
    - DataWarehouse.SDK/Infrastructure/Policy/Performance/SimulationImpactReport.cs
    - DataWarehouse.SDK/Infrastructure/Policy/Performance/PolicySimulationSandbox.cs
  modified: []
decisions:
  - Security feature IDs (encryption, access_control, auth_model, key_management, fips_mode) hardcoded for Critical severity classification
  - FeatureImpact uses record-with pattern for immutable severity assignment after initial construction
  - SimulationImpactReport is class (not record) due to mutable approval workflow state
  - Sandbox clones only features present in active profile; hypothetical policies may introduce new features
metrics:
  duration: 6min
  completed: 2026-02-23T14:42:00Z
---

# Phase 76 Plan 05: Policy Simulation Sandbox Summary

**One-liner:** Isolated sandbox replays full workloads against hypothetical policies producing latency/throughput/storage/compliance impact reports before live application.

## What Was Built

### SimulationImpactReport (Task 1)

Complete impact report data model for before/after policy comparison:

- **ImpactSeverity enum**: None/Low/Medium/High/Critical classification. Security downgrades are Critical; cascade changes are High; intensity>20 or AI autonomy changes are Medium.
- **FeatureImpact record**: Per-feature assessment with intensity delta, AI autonomy change detection, cascade change detection, and timing classification from CheckClassificationTable.
- **LatencyProjection**: Maps DeploymentTier to estimated nanoseconds (VdeOnly=0ns, ContainerStop=20ns, FullCascade=200ns) with before/after comparison.
- **ThroughputProjection**: Average intensity across all features with 10-point threshold for significant change detection.
- **StorageProjection**: Tracks compression and replication intensity changes with human-readable impact descriptions.
- **ComplianceProjection**: Identifies security feature upgrades/downgrades with feature-level tracking.
- **SimulationImpactReport class**: Aggregates all projections with mutable IsApproved/ApprovalReason for operator workflow.

### PolicySimulationSandbox (Task 2)

Isolated execution environment for what-if policy analysis:

- **SimulateWorkloadAsync**: Clones live store into InMemoryPolicyStore, applies hypothetical policies, resolves all features in both live and sandbox engines, builds complete SimulationImpactReport.
- **SimulateProfileAsync**: Evaluates entire OperationalProfile change by extracting FeaturePolicy entries and delegating to SimulateWorkloadAsync.
- **ClassifySeverity**: Static severity classification -- security features (encryption, access_control, auth_model, key_management, fips_mode) with intensity decrease = Critical; cascade change = High; intensity delta >20 or AI autonomy change = Medium; otherwise Low.
- **Isolation guarantee**: Live engine and store are NEVER modified. All hypothetical changes go exclusively to sandboxed InMemoryPolicyStore and PolicyResolutionEngine.
- **Thread-safe initialization**: SemaphoreSlim(1,1) guards lazy initialization of sandbox store and engine.
- **Reset()**: Clears sandbox state for reuse across multiple simulation runs.

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- Build succeeded, 0 Warning(s), 0 Error(s)
- SimulateWorkloadAsync produces complete impact report with all 4 projection dimensions
- Sandbox uses cloned InMemoryPolicyStore; live engine/store parameters are readonly
- Before-apply comparison shows current vs hypothetical for all affected features
- ImpactSeverity correctly classifies security downgrades as Critical

## Commits

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | SimulationImpactReport | 7bf560a3 | SimulationImpactReport.cs |
| 2 | PolicySimulationSandbox | 4d361c6f | PolicySimulationSandbox.cs |

## Self-Check: PASSED

- [x] SimulationImpactReport.cs exists
- [x] PolicySimulationSandbox.cs exists
- [x] Commit 7bf560a3 exists
- [x] Commit 4d361c6f exists
- [x] Build succeeded with 0 warnings, 0 errors
