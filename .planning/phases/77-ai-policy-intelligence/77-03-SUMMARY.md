---
phase: 77-ai-policy-intelligence
plan: 03
subsystem: infra
tags: [threat-detection, cost-analysis, data-sensitivity, pii, sliding-window, iai-advisor]

# Dependency graph
requires:
  - phase: 77-01
    provides: IAiAdvisor, ObservationEvent, AiObservationPipeline
provides:
  - ThreatDetector (4-signal sliding-window threat analysis with policy tightening)
  - CostAnalyzer (per-algorithm compute cost tracking with cloud billing projections)
  - DataSensitivityAnalyzer (PII detection and data classification assessment)
affects: [77-04, 77-05]

# Tech tracking
tech-stack:
  added: []
  patterns: [sliding-window-analysis, volatile-swap-assessment, concurrent-queue-windows, signal-confidence-weighting]

# File tracking
key-files:
  created:
    - DataWarehouse.SDK/Infrastructure/Intelligence/ThreatDetector.cs
    - DataWarehouse.SDK/Infrastructure/Intelligence/CostAnalyzer.cs
    - DataWarehouse.SDK/Infrastructure/Intelligence/DataSensitivityAnalyzer.cs
  modified: []

# Decisions
decisions:
  - ThreatDetector uses ConcurrentQueue sliding windows (5min anomaly, 1min auth) for lock-free tracking
  - CostAnalyzer parses algorithm IDs from metric name patterns with enc_/cmp_ prefixes for encryption/compression
  - DataSensitivityAnalyzer auto-elevates to Confidential when PII detected regardless of explicit classification
  - Signal weights tuned for composite score: auth_failure_spike 0.30, anomaly_rate_high 0.25, data_exfiltration 0.40, access_pattern 0.20

# Metrics
metrics:
  duration: 4min
  completed: 2026-02-24
  tasks: 2/2
  files: 3
---

# Phase 77 Plan 03: Threat, Cost, and Sensitivity Advisors Summary

Three IAiAdvisor implementations providing security, cost, and sensitivity context for the PolicyAdvisor (Plan 04).

## What was built

### ThreatDetector (AIPI-04)
Sliding-window threat signal detection with four signal types: anomaly_rate_high (>10 anomalies in 5 min), auth_failure_spike (>5 failures per minute), access_pattern_anomaly (single plugin >5x rolling average), and data_exfiltration_attempt (10x spike in data_read_bytes). Composite threat score computed from weighted signal confidences, with configurable tightening threshold (default 0.3 = Elevated). CascadeStrategy escalation: MostRestrictive at High, Enforce at Critical.

### CostAnalyzer (AIPI-05)
Per-algorithm compute cost tracking parsing algorithm IDs from metric name patterns (algorithm_*_duration_ms, encryption_*_ms, compression_*_ms). Rolling average duration tracking, CPU-seconds-per-operation estimation, and cloud billing projection at configurable rate (default $0.000012/CPU-sec). Hourly and monthly cost projections with most-expensive/most-efficient algorithm identification.

### DataSensitivityAnalyzer (AIPI-06)
PII pattern detection across 5 signal types (email, SSN, phone, HIPAA PHI, GDPR personal data) with anomaly-based PII detection. Data classification from Public through TopSecret mapped from observation values. RequiresEncryption (Confidential+) and RequiresAuditTrail (PII or Restricted+) properties for policy integration. Auto-elevates to Confidential when any PII is detected.

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- zero errors, zero warnings
- All three classes implement IAiAdvisor with AdvisorId and ProcessObservationsAsync
- ThreatDetector: ShouldTightenPolicy with configurable threshold, RecommendedCascade property
- CostAnalyzer: per-algorithm costs with USD projections, GetCostForAlgorithm API
- DataSensitivityAnalyzer: PII detection, classification, RequiresEncryption/RequiresAuditTrail

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 965f0792 | ThreatDetector with 4 signal types and policy tightening |
| 2 | 2dad5505 | CostAnalyzer and DataSensitivityAnalyzer advisors |

## Self-Check: PASSED
