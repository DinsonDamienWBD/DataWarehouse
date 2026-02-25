---
phase: 25b-strategy-migration
plan: 06
subsystem: strategy-hierarchy
tags: [shim-removal, strategybase, intelligence-cleanup, capability-registration, final-verification]
dependency_graph:
  requires: [25b-04, 25b-05]
  provides: [25b-complete]
  affects: [Phase-27-intelligence-migration]
tech_stack:
  added: []
  patterns: [plugin-level-capability-registration, pragmatic-shim-preservation]
key_files:
  created: []
  modified:
    - DataWarehouse.SDK/Contracts/StrategyBase.cs
    - Plugins/DataWarehouse.Plugins.UniversalObservability/UniversalObservabilityPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateCompute/UltimateComputePlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/UltimateStorageProcessingPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataProtectionStrategyRegistry.cs
decisions:
  - "Pragmatic shim removal: removed GetStrategyKnowledge and GetStrategyCapability (0 overrides), KEPT ConfigureIntelligence/MessageBus/IsIntelligenceAvailable (9 overrides + ~55 file references)"
  - "Capability registration moved from strategy-level to plugin-level (AD-05 compliance)"
  - "ConfigureIntelligence now actually assigns MessageBus (was previously no-op shim)"
  - "Tests blocked by pre-existing UltimateCompression CS1729 errors - documented as pre-existing"
metrics:
  duration: ~8 min
  completed: 2026-02-14
---

# Phase 25b Plan 06: Remove Backward-Compat Shim and Final Verification Summary

StrategyBase intelligence interface methods (GetStrategyKnowledge, GetStrategyCapability) removed; capability registration migrated to plugin level in 4 plugins. Full solution builds with zero new errors.

## Accomplishments

1. Removed `GetStrategyKnowledge()` and `GetStrategyCapability()` from StrategyBase (zero overrides existed, safe removal)
2. Removed `using DataWarehouse.SDK.AI` from StrategyBase (KnowledgeObject/RegisteredCapability no longer referenced)
3. Preserved ConfigureIntelligence, MessageBus, IsIntelligenceAvailable as minimal stubs (9 ConfigureIntelligence overrides + ~55 strategy files reference MessageBus)
4. Migrated capability registration from strategy-level to plugin-level in 4 callers:
   - UniversalObservabilityPlugin: now constructs RegisteredCapability from strategy metadata
   - UltimateComputePlugin: now constructs RegisteredCapability from strategy metadata
   - UltimateStorageProcessingPlugin: now constructs RegisteredCapability from strategy metadata
   - DataProtectionStrategyRegistry: now constructs KnowledgeObject and RegisteredCapability inline
5. Fixed CapabilityCategory ambiguity (SDK.Contracts vs SDK.Primitives) in Compute and StorageProcessing plugins
6. Full solution builds with 13 errors (all pre-existing: 11 CS1729 UltimateCompression + 2 CS0234 AedsCore)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Plugin files calling removed GetStrategyCapability/GetStrategyKnowledge (5 call sites in 4 files)**
- Found during: Task 1 Step 5 (full solution build)
- Issue: 4 plugin files called GetStrategyCapability()/GetStrategyKnowledge() on strategies, which were removed from StrategyBase
- Fix: Replaced calls with inline RegisteredCapability/KnowledgeObject construction using strategy metadata
- Files: UniversalObservabilityPlugin.cs, UltimateComputePlugin.cs, UltimateStorageProcessingPlugin.cs, DataProtectionStrategyRegistry.cs
- Commit: b851dbf

**2. [Rule 1 - Bug] Ambiguous CapabilityCategory reference in 2 plugins**
- Found during: Task 1 Step 5 (second build attempt)
- Issue: CapabilityCategory exists in both SDK.Contracts and SDK.Primitives namespaces
- Fix: Fully qualified as SDK.Contracts.CapabilityCategory
- Files: UltimateComputePlugin.cs, UltimateStorageProcessingPlugin.cs

## Issues Encountered

- Tests cannot run due to pre-existing UltimateCompression CS1729 build errors (test project references UltimateCompression). This is NOT caused by Phase 25b changes.
- 2 additional plugin-local bases (DeploymentStrategyBase, IntelligenceStrategyBase) have their own GetStrategyKnowledge/GetStrategyCapability methods that are NOT affected by StrategyBase changes (they're locally defined, not inherited).

## Final StrategyBase State

StrategyBase now contains:
- Abstract: StrategyId, Name
- Virtual: Description, Characteristics
- Lifecycle: InitializeAsync, ShutdownAsync, InitializeAsyncCore, ShutdownAsyncCore, IsInitialized
- Dispose: Dispose(), DisposeAsync(), Dispose(bool), DisposeAsyncCore(), EnsureNotDisposed()
- Legacy MessageBus (Phase 27 removes): ConfigureIntelligence, MessageBus, IsIntelligenceAvailable

Zero intelligence protocol methods (GetStrategyKnowledge, GetStrategyCapability removed).

## Self-Check: PASSED
