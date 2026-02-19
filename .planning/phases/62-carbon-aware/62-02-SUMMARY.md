---
phase: 62-carbon-aware
plan: 02
subsystem: UltimateSustainability
tags: [energy, measurement, rapl, powercap, cloud, estimation, carbon]
dependency_graph:
  requires: ["62-01"]
  provides: ["IEnergyMeasurementService implementation", "RAPL/powercap/cloud/estimation strategies"]
  affects: ["62-03", "62-04", "62-05", "62-06"]
tech_stack:
  added: []
  patterns: ["strategy cascade (RAPL->Powercap->Cloud->Estimation)", "bounded ConcurrentQueue", "type alias for namespace conflict resolution", "message bus publishing"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/RaplEnergyMeasurementStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/PowercapEnergyMeasurementStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/CloudProviderEnergyStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/EstimationEnergyStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/EnergyMeasurement/EnergyMeasurementService.cs
  modified:
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/SustainabilityEnhancedStrategies.cs
decisions:
  - "Used type alias (CarbonEnergyMeasurement) to resolve namespace/type conflict between EnergyMeasurement folder namespace and SDK EnergyMeasurement type"
  - "Renamed legacy plugin types (EnergyMeasurement->TrackingEnergyMeasurement, CarbonBudget->SchedulingCarbonBudget) to avoid conflict with SDK types from 62-01"
metrics:
  duration: "7m42s"
  completed: "2026-02-19"
  tasks: 2
  files_created: 5
  files_modified: 1
  total_lines_added: 1540
---

# Phase 62 Plan 02: Energy Measurement Engine Summary

Energy measurement cascade with 4 sources (RAPL, powercap, cloud API, TDP estimation) and a composite IEnergyMeasurementService that auto-detects the best available source on any platform.

## What Was Built

### Task 1: Hardware Energy Measurement Strategies

**RaplEnergyMeasurementStrategy** (265 lines): Reads Intel RAPL MSR counters from `/sys/class/powercap/intel-rapl/intel-rapl:0/energy_uj`. Discovers all RAPL domains (package, core, uncore, dram), handles 32-bit counter overflow at 2^32 microjoules, uses `File.ReadAllTextAsync` for non-blocking sysfs reads, and tracks per-domain readings in a `ConcurrentDictionary<string, double>`.

**PowercapEnergyMeasurementStrategy** (281 lines): Generic Linux powercap interface at `/sys/class/powercap/*`. Works with any powercap-compatible driver (Intel RAPL, AMD RAPL, ARM energy probes). Dynamically enumerates all powercap zones, aggregates package-level readings for multi-socket systems, and handles the same counter overflow logic.

### Task 2: Cloud/Estimation Strategies + Composite Service

**CloudProviderEnergyStrategy** (433 lines): Detects AWS/Azure/GCP via environment variables, queries instance metadata via IMDSv2/Azure IMDS/GCE Metadata for instance type, maps instance families to TDP-per-vCPU, and applies region-specific PUE factors (1.08 for Nordic to 1.25 for Singapore). Converts CPU utilization to watts: `watts = cpuUtilization * instanceTdp * PUE`.

**EstimationEnergyStrategy** (240 lines): Universal fallback, always available. Detects CPU architecture (x64/ARM64/ARM) and estimates TDP (65W desktop to 280W server for x64, 15W-80W for ARM64). Power model: `watts = baseTdp * (0.30 + 0.70 * cpuUtilization)`. Includes storage I/O energy models (SSD read 0.003 Wh/GB, write 0.005 Wh/GB, HDD read 0.01 Wh/GB, write 0.015 Wh/GB).

**EnergyMeasurementService** (321 lines): Composite service implementing `IEnergyMeasurementService`. Auto-detects best source at construction. Stores measurements in a bounded `ConcurrentQueue` (100K entries). Maintains per-operation-type rolling averages (10K per type). Publishes every measurement to `sustainability.energy.measured` message bus topic. Falls back to estimation if the primary strategy throws.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Namespace/type conflict with EnergyMeasurement**
- **Found during:** Task 1
- **Issue:** The `EnergyMeasurement` folder created a namespace `Strategies.EnergyMeasurement` that conflicted with the existing `EnergyMeasurement` record type in `SustainabilityEnhancedStrategies.cs` (same parent namespace). Additionally, the SDK type `DataWarehouse.SDK.Contracts.Carbon.EnergyMeasurement` could not be referenced unqualified from within the `EnergyMeasurement` namespace.
- **Fix:** (a) Renamed legacy plugin types: `EnergyMeasurement` -> `TrackingEnergyMeasurement`, `CarbonBudget` -> `SchedulingCarbonBudget` in SustainabilityEnhancedStrategies.cs. (b) Added type alias `using CarbonEnergyMeasurement = DataWarehouse.SDK.Contracts.Carbon.EnergyMeasurement` in all new files.
- **Files modified:** SustainabilityEnhancedStrategies.cs (type rename), all 5 new files (type alias)
- **Commit:** 52d48cea

## Verification

- Plugin build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
- All 5 files exist in `Strategies/EnergyMeasurement/`
- All files exceed min_lines requirements (265/150, 281/120, 433/150, 240/100, 321/120)
- EnergyMeasurementService implements IEnergyMeasurementService from SDK
- Strategies extend SustainabilityStrategyBase (auto-register via reflection)
- All communication via message bus (`sustainability.energy.measured` topic)
- No mocks, stubs, or placeholders -- all strategies read real hardware/API data or produce real estimates

## Self-Check: PASSED
