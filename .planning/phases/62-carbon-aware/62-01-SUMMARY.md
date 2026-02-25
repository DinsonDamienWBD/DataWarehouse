---
phase: "62"
plan: "01"
subsystem: "SDK Carbon Contracts"
tags: [carbon-aware, sdk, contracts, sustainability, ghg-protocol]
dependency_graph:
  requires: []
  provides:
    - "DataWarehouse.SDK.Contracts.Carbon namespace"
    - "EnergyMeasurement, CarbonBudget, GridCarbonData, GreenScore, GhgReportEntry, CarbonPlacementDecision types"
    - "IEnergyMeasurementService, ICarbonBudgetService, IGreenPlacementService, ICarbonReportingService contracts"
  affects:
    - "Plans 62-02 through 62-06 (all depend on these SDK types)"
tech_stack:
  added: []
  patterns:
    - "Sealed record types with computed properties for domain invariants"
    - "Enum-based measurement source/component classification"
    - "GHG Protocol Scope 1/2/3 emissions categorization"
key_files:
  created:
    - DataWarehouse.SDK/Contracts/Carbon/CarbonTypes.cs
    - DataWarehouse.SDK/Contracts/Carbon/IEnergyMeasurement.cs
    - DataWarehouse.SDK/Contracts/Carbon/ICarbonBudget.cs
    - DataWarehouse.SDK/Contracts/Carbon/IGreenPlacement.cs
    - DataWarehouse.SDK/Contracts/Carbon/ICarbonReporting.cs
  modified: []
decisions:
  - "All 12 domain types in single CarbonTypes.cs file for discoverability"
  - "CarbonThrottleDecision co-located with ICarbonBudget.cs, CarbonSummary with ICarbonReporting.cs"
  - "Computed properties (EnergyWh, IsThrottled, IsExhausted, FossilPercentage) enforce invariants at type level"
metrics:
  duration: "192s"
  completed: "2026-02-19"
---

# Phase 62 Plan 01: SDK Carbon Contracts Summary

Sealed record types and service interfaces for energy measurement, per-tenant carbon budgets with throttle thresholds, GHG Protocol Scope 1/2/3 reporting, and renewable-aware green placement decisions.

## What Was Built

### Task 1: Core Carbon Domain Types (CarbonTypes.cs, 444 lines)
- **12 types** covering the complete carbon-aware domain model
- **6 enums**: EnergyComponent, EnergySource, CarbonBudgetPeriod, GridDataSource, GhgScopeCategory, DataQualityLevel
- **6 records**: EnergyMeasurement, CarbonBudget, GridCarbonData, GreenScore, GhgReportEntry, CarbonPlacementDecision
- Computed properties enforce domain invariants (EnergyWh from watts/duration, IsThrottled from usage/threshold, FossilPercentage from renewable)
- Commit: `ea41c850`

### Task 2: Carbon Service Contracts (4 files, 301 lines)
- **IEnergyMeasurementService**: measure operations, query historical measurements, get current power draw
- **ICarbonBudgetService**: per-tenant budget CRUD, proceed/block checks, throttle evaluation with CarbonThrottleDecision
- **IGreenPlacementService**: select greenest backend from candidates, get green scores, query/refresh grid carbon data
- **ICarbonReportingService**: GHG Protocol report generation, emissions by scope/region, CarbonSummary aggregation
- Commit: `c2c8a549`

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors, 0 warnings
- All types in namespace `DataWarehouse.SDK.Contracts.Carbon`
- No circular dependencies introduced (SDK-only, no plugin references)

## Decisions Made

1. **Single file for domain types**: All 12 types in CarbonTypes.cs for discoverability, following existing SDK patterns
2. **Supporting types co-located with interfaces**: CarbonThrottleDecision in ICarbonBudget.cs, CarbonSummary in ICarbonReporting.cs
3. **Computed properties for invariants**: EnergyWh, RemainingGramsCO2e, IsThrottled, IsExhausted, FossilPercentage computed from other properties to prevent inconsistencies

## Self-Check: PASSED

- All 5 created files exist on disk
- Commit ea41c850 found in git log
- Commit c2c8a549 found in git log
- SDK build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
