---
phase: 27-plugin-migration-decoupling
plan: 04
subsystem: plugins
tags: [hierarchy, standalone, aeds, infrastructure, platform, security, data-management, orchestration, compute, streaming, storage]

requires:
  - phase: 27-01
    provides: SDK intermediate bases re-parented to Hierarchy domain bases
  - phase: 27-03
    provides: 32 Feature-branch plugins on Hierarchy bases
provides:
  - AirGapBridge migrated from raw IFeaturePlugin to InfrastructurePluginBase
  - 9 standalone LegacyFeaturePluginBase plugins on correct Hierarchy bases
  - 11 AEDS LegacyFeaturePluginBase plugins on correct Hierarchy bases
  - Zero LegacyFeaturePluginBase references in any plugin file
affects: [27-05-decoupling-verification, 28-obsoletion]

tech-stack:
  added: []
  patterns: [powershell-batch-migration, domain-property-pattern, category-e-raw-interface-migration]

key-files:
  modified:
    - Plugins/DataWarehouse.Plugins.AirGapBridge/AirGapBridgePlugin.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/AdaptiveTransportPlugin.cs
    - Plugins/DataWarehouse.Plugins.DataMarketplace/DataMarketplacePlugin.cs
    - Plugins/DataWarehouse.Plugins.PluginMarketplace/PluginMarketplacePlugin.cs
    - Plugins/DataWarehouse.Plugins.SelfEmulatingObjects/SelfEmulatingObjectsPlugin.cs
    - Plugins/DataWarehouse.Plugins.FuseDriver/FuseDriverPlugin.cs
    - Plugins/DataWarehouse.Plugins.WinFspDriver/WinFspDriverPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/UltimateFilesystemPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateResourceManager/UltimateResourceManagerPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataLineage/UltimateDataLineagePlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/AedsCorePlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/ClientCourierPlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/ZeroTrustPairingPlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/SwarmIntelligencePlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/PreCogPlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/PolicyEnginePlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/NotificationPlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/MulePlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/GlobalDeduplicationPlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/DeltaSyncPlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Extensions/CodeSigningPlugin.cs

key-decisions:
  - "AirGapBridge is Category E (raw IFeaturePlugin -> InfrastructurePluginBase); most complex single migration"
  - "StoragePluginBase stubs required 7 abstract method implementations with exact signatures (IDictionary, Task<StorageObjectMetadata>, IAsyncEnumerable, etc.)"
  - "AEDS intermediate bases (ControlPlaneTransport, DataPlaneTransport, ServerDispatcher, ClientSentinel, ClientExecutor) already implement Hierarchy abstract members"
  - "11 AEDS Extension plugins on LegacyFeaturePluginBase migrated to domain-appropriate Hierarchy bases"
  - "Used DateTime.UtcNow (not DateTimeOffset) for StorageObjectMetadata Created/Modified fields"

patterns-established:
  - "Category E migration: raw interface -> full hierarchy base with manual override keywords on all PluginBase members"
  - "StoragePluginBase exact signatures: StoreAsync returns Task<StorageObjectMetadata>, ListAsync returns IAsyncEnumerable, DeleteAsync returns Task (void)"
  - "AEDS domain classification: security plugins -> SecurityPluginBase, orchestration -> OrchestrationPluginBase, data utilities -> DataManagementPluginBase"

duration: 15min
completed: 2026-02-14
---

# Plan 27-04: Standalone & Special-Case Plugin Migration Summary

**AirGapBridge migrated from raw IFeaturePlugin, 9 standalone + 11 AEDS LegacyFeaturePluginBase plugins migrated to Hierarchy domain bases, achieving zero LegacyFeaturePluginBase references across all plugin directories**

## Performance

- **Duration:** ~15 min
- **Tasks:** 2
- **Files modified:** 21

## Accomplishments
- AirGapBridge fully migrated from raw IFeaturePlugin to InfrastructurePluginBase (Category E special case)
- 9 standalone LegacyFeaturePluginBase plugins migrated across 7 Hierarchy domains (Streaming, Platform, Compute, Interface, Storage, Infrastructure, DataManagement)
- 11 AEDS LegacyFeaturePluginBase plugins migrated across 4 domains (Security, Orchestration, DataManagement, Platform)
- AEDS intermediate bases (ControlPlaneTransport, DataPlaneTransport, ServerDispatcher, ClientSentinel, ClientExecutor) verified compiling after Plan 27-01 re-parenting
- RaftConsensus, KubernetesCsi, WasmCompute verified compiling through their intermediate bases
- Zero LegacyFeaturePluginBase references remain in any plugin file
- Zero new build errors (only pre-existing CS1729/CS0234)

## Task Commits

1. **Task 1: AirGapBridge + 9 standalone plugins** - `5cda898` (feat)
2. **Task 2: 11 AEDS plugins + verification** - `a0a65c8` (feat)

## Files Created/Modified

### Task 1: Standalone Plugins
- `AirGapBridgePlugin.cs` - IFeaturePlugin -> InfrastructurePluginBase (Category E), InfrastructureDomain="AirGap"
- `AdaptiveTransportPlugin.cs` - LegacyFeaturePluginBase -> StreamingPluginBase, PublishAsync/SubscribeAsync stubs
- `DataMarketplacePlugin.cs` - LegacyFeaturePluginBase -> PlatformPluginBase, PlatformDomain="DataMarketplace"
- `PluginMarketplacePlugin.cs` - LegacyFeaturePluginBase -> PlatformPluginBase, PlatformDomain="PluginMarketplace"
- `SelfEmulatingObjectsPlugin.cs` - LegacyFeaturePluginBase -> ComputePluginBase, RuntimeType="SelfEmulating", ExecuteWorkloadAsync stub
- `FuseDriverPlugin.cs` - LegacyFeaturePluginBase -> InterfacePluginBase (FQN), Protocol="FUSE"
- `WinFspDriverPlugin.cs` - LegacyFeaturePluginBase -> InterfacePluginBase (FQN), Protocol="WinFsp"
- `UltimateFilesystemPlugin.cs` - LegacyFeaturePluginBase -> StoragePluginBase, 7 storage method stubs with exact signatures
- `UltimateResourceManagerPlugin.cs` - LegacyFeaturePluginBase -> InfrastructurePluginBase, InfrastructureDomain="ResourceManagement"
- `UltimateDataLineagePlugin.cs` - LegacyFeaturePluginBase -> DataManagementPluginBase, DataManagementDomain="DataLineage"

### Task 2: AEDS Plugins
- `AedsCorePlugin.cs` - LegacyFeaturePluginBase -> OrchestrationPluginBase, OrchestrationMode="AedsCore"
- `ClientCourierPlugin.cs` - LegacyFeaturePluginBase -> PlatformPluginBase, PlatformDomain="AedsCourier"
- `ZeroTrustPairingPlugin.cs` - LegacyFeaturePluginBase -> SecurityPluginBase, SecurityDomain="ZeroTrustPairing"
- `SwarmIntelligencePlugin.cs` - LegacyFeaturePluginBase -> OrchestrationPluginBase, OrchestrationMode="SwarmIntelligence"
- `PreCogPlugin.cs` - LegacyFeaturePluginBase -> DataManagementPluginBase, DataManagementDomain="PredictiveAnalytics"
- `PolicyEnginePlugin.cs` - LegacyFeaturePluginBase -> SecurityPluginBase, SecurityDomain="PolicyEngine"
- `NotificationPlugin.cs` - LegacyFeaturePluginBase -> PlatformPluginBase, PlatformDomain="Notification"
- `MulePlugin.cs` - LegacyFeaturePluginBase -> DataManagementPluginBase, DataManagementDomain="DataTransfer"
- `GlobalDeduplicationPlugin.cs` - LegacyFeaturePluginBase -> DataManagementPluginBase, DataManagementDomain="GlobalDedup"
- `DeltaSyncPlugin.cs` - LegacyFeaturePluginBase -> DataManagementPluginBase, DataManagementDomain="DeltaSync"
- `CodeSigningPlugin.cs` - LegacyFeaturePluginBase -> SecurityPluginBase, SecurityDomain="CodeSigning"

## Decisions Made
- AirGapBridge mapped to InfrastructurePluginBase (network infrastructure bridging)
- StoragePluginBase stubs verified against exact signatures (IDictionary, Task<StorageObjectMetadata>, IAsyncEnumerable, DateTime.UtcNow)
- AEDS intermediate bases (from Plan 27-01) already implement all Hierarchy abstract members; no changes needed
- 11 AEDS Extension plugins classified by domain function: security, orchestration, data management, platform
- RaftConsensus, KubernetesCsi, WasmCompute compile through intermediate bases without changes

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed AdaptiveTransport streaming stubs inserted inside Supporting Types region**
- **Found during:** Task 1
- **Issue:** PowerShell last-brace replacement inserted PublishAsync/SubscribeAsync stubs inside supporting type classes, not the main plugin class
- **Fix:** Moved stubs to correct location inside main class, added missing closing brace for last supporting type
- **Files modified:** AdaptiveTransportPlugin.cs
- **Commit:** 5cda898

**2. [Rule 1 - Bug] Fixed UltimateFilesystem StoragePluginBase signatures**
- **Found during:** Task 1
- **Issue:** Migration script used wrong parameter/return types (Dictionary vs IDictionary, Task<bool> vs Task, Task<Stream?> vs Task<Stream>, etc.)
- **Fix:** Corrected all 7 method signatures to match StoragePluginBase abstract definitions
- **Files modified:** UltimateFilesystemPlugin.cs
- **Commit:** 5cda898

**3. [Rule 1 - Bug] Fixed DateTimeOffset.UtcNow -> DateTime.UtcNow for StorageObjectMetadata**
- **Found during:** Task 1
- **Issue:** StorageObjectMetadata.Created/Modified are DateTime, not DateTimeOffset
- **Fix:** Changed to DateTime.UtcNow
- **Files modified:** UltimateFilesystemPlugin.cs
- **Commit:** 5cda898

**4. [Rule 2 - Missing Critical] Migrated 11 AEDS LegacyFeaturePluginBase plugins not in original plan**
- **Found during:** Task 2
- **Issue:** Plan specified only verifying AEDS intermediate-base plugins compile, but 11 AEDS Extension plugins still used LegacyFeaturePluginBase directly
- **Fix:** Migrated all 11 to domain-appropriate Hierarchy bases
- **Files modified:** 11 AEDS plugin files
- **Commit:** a0a65c8

---

**Total deviations:** 4 auto-fixed (3 bugs, 1 missing critical)
**Impact on plan:** All fixes necessary for compilation and complete LegacyFeaturePluginBase elimination. No scope creep.

## Issues Encountered
- PowerShell last-brace replacement continues to misplace code when files have supporting types after the main class
- StoragePluginBase has non-obvious signature choices (IDictionary vs Dictionary, non-nullable Stream returns, IAsyncEnumerable for listing)

## Verification Results
- `grep LegacyFeaturePluginBase Plugins/` -- zero matches
- `grep 'IntelligenceAware.*PluginBase' Plugins/` as base class -- zero matches (only comments)
- AirGapBridge no longer implements IFeaturePlugin directly
- Full solution build: only pre-existing CS1729 (UltimateCompression) and CS0234 (AedsCore MQTTnet) errors

## Next Phase Readiness
- All standalone and special-case plugins on Hierarchy bases
- Combined with Plans 27-02 and 27-03, all ~78 plugin classes are now on Hierarchy bases
- Ready for Plan 27-05 full decoupling verification

---
*Phase: 27-plugin-migration-decoupling*
*Completed: 2026-02-14*
