---
phase: 64-moonshot-wiring
plan: 02
subsystem: Moonshot Configuration
tags: [moonshot, configuration, hierarchy, validation, sdk]
dependency_graph:
  requires: [MoonshotId from 64-01]
  provides: [MoonshotConfiguration, MoonshotConfigurationDefaults, MoonshotConfigurationValidator, IMoonshotConfigValidator]
  affects: [All moonshot plugins that need configuration, Kernel moonshot bootstrap]
tech_stack:
  added: []
  patterns: [Immutable records, Override policy enforcement, Hierarchy merging, ISO 8601 duration validation]
key_files:
  created:
    - DataWarehouse.SDK/Moonshots/MoonshotConfiguration.cs
    - DataWarehouse.SDK/Moonshots/MoonshotConfigurationDefaults.cs
    - DataWarehouse.SDK/Moonshots/MoonshotConfigurationValidator.cs
  modified: []
decisions:
  - "MoonshotOverridePolicy enum (Locked/TenantOverridable/UserOverridable) instead of boolean AllowUserToOverride for finer-grained control"
  - "Compliance, Sovereignty, and CryptoTimeLocks locked by default -- cannot be disabled at Tenant or User level"
  - "MergeWith validates child level is more specific than parent level, preventing upward override"
  - "Validator uses XmlConvert.ToTimeSpan for ISO 8601 duration parsing (standard .NET approach)"
metrics:
  duration: 330s
  completed: 2026-02-19T22:34:51Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
  files_modified: 0
  total_lines: 759
---

# Phase 64 Plan 02: Moonshot Configuration Summary

Hierarchical moonshot configuration with per-moonshot strategy selection, three-tier override policies (Locked/TenantOverridable/UserOverridable), and production defaults for all 10 moonshots with constraint validation.

## What Was Built

### MoonshotConfiguration.cs (263 lines)
- `ConfigHierarchyLevel` enum: Instance -> Tenant -> User hierarchy
- `MoonshotOverridePolicy` enum: Locked, TenantOverridable, UserOverridable
- `MoonshotStrategySelection` record: per-capability strategy name + parameters
- `MoonshotFeatureConfig` record: enabled, override policy, strategies, settings, dependencies, defined-at level
- `MoonshotConfiguration` class: dictionary of per-moonshot configs with `MergeWith()` hierarchy merging that enforces override policies (Locked = parent wins, TenantOverridable = only Tenant can change, UserOverridable = anyone can change)
- Convenience methods: `GetEffectiveConfig()`, `IsEnabled()`, `GetStrategy()`

### MoonshotConfigurationDefaults.cs (197 lines)
- `CreateProductionDefaults()`: All 10 moonshots enabled at Instance level with:
  - UserOverridable: UniversalTags, SemanticSync
  - TenantOverridable: DataConsciousness, ZeroGravityStorage, ChaosVaccination, CarbonAwareLifecycle, UniversalFabric
  - Locked: CompliancePassports, SovereigntyMesh, CryptoTimeLocks
  - Dependencies: CompliancePassports->UniversalTags, SovereigntyMesh->CompliancePassports, SemanticSync->DataConsciousness
  - Strategy defaults: InvertedIndex, CrdtVersioning, AiValueScoring, ContinuousAudit, DeclarativeZones, CrushPlacement, SoftwareTimeLock, AiClassifier, ControlledInjection, CarbonIntensityApi, DwNamespaceRouter
- `CreateMinimalDefaults()`: Only UniversalTags + UniversalFabric enabled

### MoonshotConfigurationValidator.cs (299 lines)
- `IMoonshotConfigValidator` interface + `MoonshotConfigurationValidator` implementation
- 7 validation rules:
  1. **DEP_MISSING**: Enabled moonshot requires disabled dependency
  2. **OVERRIDE_VIOLATION**: Child level overrides locked parent config
  3. **MISSING_STRATEGY**: Enabled moonshot has empty strategy name
  4. **MISSING_SETTING**: Required setting absent (ConsciousnessThreshold, DefaultZone, DefaultLockDuration, MaxBlastRadius, CarbonBudgetKgPerTB)
  5. **INVALID_DURATION**: CryptoTimeLocks DefaultLockDuration not valid ISO 8601
  6. **INVALID_RANGE**: ChaosVaccination MaxBlastRadius not 1-100
  7. **INVALID_BUDGET**: CarbonAwareLifecycle CarbonBudgetKgPerTB not positive

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Removed duplicate MoonshotId enum**
- **Found during:** Task 1
- **Issue:** Initially defined MoonshotId in MoonshotConfiguration.cs, but MoonshotPipelineTypes.cs (from 64-01) already defines it in the same namespace
- **Fix:** Removed the duplicate enum definition, using the existing one from MoonshotPipelineTypes.cs
- **Files modified:** DataWarehouse.SDK/Moonshots/MoonshotConfiguration.cs

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- zero errors, zero warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- zero errors, zero warnings
- All 3 files exist in `DataWarehouse.SDK/Moonshots/`
- All files exceed minimum line counts (263/200, 197/120, 299/100)

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 62b98b6d | MoonshotConfiguration types with hierarchy and override control |
| 2 | c88d249d | Configuration defaults and validator |

## Self-Check: PASSED

- All 3 source files exist
- All 2 task commits verified in git log
- SUMMARY.md created
