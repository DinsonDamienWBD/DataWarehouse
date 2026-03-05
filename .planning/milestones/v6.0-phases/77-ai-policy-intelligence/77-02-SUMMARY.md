---
phase: 77-ai-policy-intelligence
plan: 02
subsystem: SDK Infrastructure Intelligence
tags: [ai-advisor, hardware-probe, workload-analyzer, observation-pipeline]
dependency_graph:
  requires: [77-01]
  provides: [HardwareProbe, WorkloadAnalyzer, HardwareSnapshot, WorkloadProfile]
  affects: [77-04]
tech_stack:
  added: []
  patterns: [volatile-reference-swap, circular-buffer, percentile-inference, time-of-day-classification]
key_files:
  created:
    - DataWarehouse.SDK/Infrastructure/Intelligence/HardwareProbe.cs
    - DataWarehouse.SDK/Infrastructure/Intelligence/WorkloadAnalyzer.cs
  modified: []
decisions:
  - "Thermal state inferred from p99/p50 latency ratio (3x=Warm, 5x=Throttling, 10x=Critical)"
  - "ARM SIMD detection (AdvSimd/Aes) added for cross-platform support beyond plan spec"
  - "28-day daily circular buffer for seasonal trend (14-day minimum for comparison)"
  - "StorageSpeedClass from env var only (no runtime benchmark) to avoid I/O side effects"
metrics:
  duration: 3min
  completed: 2026-02-23T14:59:42Z
---

# Phase 77 Plan 02: HardwareProbe & WorkloadAnalyzer Summary

Hardware-aware CPU/RAM/thermal probe and workload pattern analyzer as IAiAdvisor implementations feeding the AI observation pipeline.

## What Was Built

### HardwareProbe (AIPI-02)
- **CPU detection**: x86 (AVX2, AES, SSE4.2) and ARM (AdvSimd, AES) via `System.Runtime.Intrinsics`
- **RAM tracking**: `GC.GetGCMemoryInfo()` for total/available/pressure percentage
- **Storage speed**: `DATAWAREHOUSE_STORAGE_SPEED` environment variable (HDD/SATA/NVMe/Unknown)
- **Thermal inference**: 256-sample latency buffer, percentile analysis (p99/p50 ratio thresholds)
- **30s refresh cooldown** via `Stopwatch.GetTimestamp()` ticks comparison
- **`IsHardwareConstrained`**: true when memory > 85% or thermal >= Warm
- Enums: `StorageSpeedClass` (Unknown/Hdd/Sata/NvMe), `ThermalState` (Normal/Warm/Throttling/Critical)
- Record: `HardwareSnapshot` with 11 fields

### WorkloadAnalyzer (AIPI-03)
- **Per-minute tracking**: 1440-bucket circular buffer (24 hours) with minute-epoch timestamps
- **Time-of-day**: UTC hour classification (OffPeak 0-6/22-24, Normal 6-9/17-22, Peak 9-17)
- **Seasonal trend**: 28-day daily totals, 7-day vs previous-7-day comparison (10% threshold)
- **Burst detection**: current minute count > 3x rolling 60-minute average
- **`ShouldOptimizeForThroughput`**: true during Peak or burst
- **`ShouldConserveResources`**: true during OffPeak with Falling trend
- Enums: `TimeOfDayPattern` (Unknown/OffPeak/Normal/Peak), `SeasonalTrend` (Unknown/Stable/Rising/Falling)
- Record: `WorkloadProfile` with 6 fields

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added ARM SIMD detection**
- **Found during:** Task 1
- **Issue:** Plan specified only x86 intrinsics; ARM platforms (Apple Silicon, Graviton) would report no SIMD capabilities
- **Fix:** Added `HasArmAdvSimd` and `HasArmAes` fields with `System.Runtime.Intrinsics.Arm` checks
- **Files modified:** HardwareProbe.cs
- **Commit:** c3f9f4c4

## Verification

- SDK build: zero errors, zero warnings
- HardwareProbe contains Avx2.IsSupported and Aes.IsSupported checks
- WorkloadAnalyzer contains BurstThresholdMultiplier = 3.0 burst detection
- Both implement IAiAdvisor interface

## Commits

| Task | Commit   | Description                        |
|------|----------|------------------------------------|
| 1    | c3f9f4c4 | HardwareProbe AI advisor           |
| 2    | 0bbb4708 | WorkloadAnalyzer AI advisor        |

## Self-Check: PASSED
