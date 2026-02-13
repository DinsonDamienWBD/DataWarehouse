---
phase: 25a-strategy-hierarchy-api-contracts
plan: 03
subsystem: sdk
tags: [backward-compat, shims, legacy-methods, intelligence-bridge, AD-05]

requires:
  - phase: 25a-01
    provides: "StrategyBase root class"
  - phase: 25a-02
    provides: "Domain bases inheriting StrategyBase with intelligence removed"
provides:
  - "Legacy ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability on StrategyBase"
  - "Legacy MessageBus/IsIntelligenceAvailable properties on StrategyBase"
  - "Re-added StrategyId/StrategyName virtual properties on Replication/RAID/Compression bases"
  - "Name bridge properties on PipelineCompute/DataLake/DataMesh/DataFormat bases"
  - "Default StrategyId/Name on Interface/Observability/KeyStore bases"
  - "Zero new build errors across all 60 plugins"
affects: [25a-05, 25b-strategy-migration]

tech-stack:
  added: []
  patterns: [backward-compat-shim, name-bridge-pattern, default-identity-pattern]

key-files:
  created: []
  modified:
    - DataWarehouse.SDK/Contracts/StrategyBase.cs
    - DataWarehouse.SDK/Contracts/Replication/ReplicationStrategy.cs
    - DataWarehouse.SDK/Contracts/RAID/RaidStrategy.cs
    - DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs
    - DataWarehouse.SDK/Contracts/Compute/PipelineComputeStrategy.cs
    - DataWarehouse.SDK/Contracts/DataLake/DataLakeStrategy.cs
    - DataWarehouse.SDK/Contracts/DataMesh/DataMeshStrategy.cs
    - DataWarehouse.SDK/Contracts/DataFormat/DataFormatStrategy.cs
    - DataWarehouse.SDK/Contracts/Interface/InterfaceStrategyBase.cs
    - DataWarehouse.SDK/Contracts/Observability/ObservabilityStrategyBase.cs
    - DataWarehouse.SDK/Security/IKeyStore.cs

key-decisions:
  - "Legacy intelligence methods on StrategyBase (not domain bases) for single override target"
  - "Interface/Observability/KeyStore get default StrategyId from class name (backward-compat)"
  - "Re-added GetStrategyDescription/GetKnowledgeTags/GetKnowledgePayload virtual helpers to Replication/RAID/Compression bases"
  - "Fixed 69 Interface plugin strategies with override keyword on StrategyId"

patterns-established:
  - "Legacy region: #region Legacy Intelligence Compatibility (Phase 25b removes these)"
  - "TODO(25b) comments on all legacy methods for future removal"
  - "Default identity: GetType().Name.Replace('Strategy', '') for bases that never had StrategyId"

duration: 90min
completed: 2026-02-14
---

# Phase 25a Plan 03: Backward-Compatibility Shims Summary

**Legacy intelligence methods on StrategyBase + re-added domain identity properties, achieving zero new build errors across 60 plugins**

## Performance

- **Duration:** ~90 min
- **Tasks:** 1 (complex multi-phase backward-compat work)
- **Files modified:** 83

## Accomplishments
- Added legacy backward-compat region to StrategyBase with ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable
- Re-added StrategyId/StrategyName virtual properties to Replication, RAID, Compression bases (lost during intelligence region removal)
- Added Name bridge properties to 4 bases using DisplayName pattern
- Added default StrategyId/Name to 3 bases that never had them (Interface, Observability, KeyStore)
- Re-added legacy GetStrategyDescription/GetKnowledgeTags/GetKnowledgePayload helpers
- Fixed 69 Interface plugin strategies with override keyword
- Fixed Dispose() hiding in 2 replication strategies with new keyword
- Full solution: 0 new errors (only 26 pre-existing)

## Task Commits

1. **Task 1: Add backward-compat shims** - `de83c09` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Contracts/StrategyBase.cs` - Added legacy intelligence region with 5 backward-compat methods/properties
- 10 domain strategy base files - Re-added StrategyId/StrategyName/Name/legacy helpers
- 69 UltimateInterface plugin strategies - Added override keyword to StrategyId
- 3 UltimateReplication plugin files - Fixed Characteristics and Dispose hiding

## Decisions Made
- Legacy methods placed on StrategyBase (not domain bases) to provide single override target
- Interface/Observability/KeyStore use GetType().Name-based defaults instead of abstract (avoids breaking 282 concrete strategies)
- All legacy code marked with TODO(25b) for Phase 25b removal
- Used sed script for bulk override keyword addition across 69 Interface plugin files

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Re-added StrategyId/StrategyName properties lost during intelligence removal**
- **Found during:** Task 1
- **Issue:** Intelligence region removal also removed domain identity properties (StrategyId, StrategyName) from Replication, RAID, Compression bases -- these were incorrectly co-located in #region Intelligence Integration
- **Fix:** Re-added as standalone virtual properties with override from StrategyBase.StrategyId/Name
- **Files modified:** ReplicationStrategy.cs, RaidStrategy.cs, CompressionStrategy.cs
- **Verification:** Full solution build with zero new errors

**2. [Rule 1 - Bug] Re-added GetStrategyDescription/GetKnowledgeTags/GetKnowledgePayload helpers**
- **Found during:** Task 1
- **Issue:** Concrete replication/RAID/compression strategies override these virtual helper methods that were in intelligence regions
- **Fix:** Re-added as legacy helpers in new #region Legacy Intelligence Helpers blocks
- **Files modified:** ReplicationStrategy.cs, RaidStrategy.cs, CompressionStrategy.cs

**3. [Rule 3 - Blocking] Fixed 69 Interface plugin strategies lacking override keyword**
- **Found during:** Task 1
- **Issue:** Concrete InterfaceStrategyBase subclasses declared StrategyId without override, causing CS0114 warnings-as-errors
- **Fix:** Added override keyword to all 69 affected files via sed script
- **Files modified:** 69 files in Plugins/DataWarehouse.Plugins.UltimateInterface/Strategies/

**4. [Rule 1 - Bug] Fixed Dispose/Characteristics hiding in UltimateReplication**
- **Found during:** Task 1
- **Issue:** EnhancedReplicationStrategyBase.Characteristics hides StrategyBase.Characteristics (different type), 2 strategies' Dispose() hides StrategyBase.Dispose()
- **Fix:** Added new keyword to Characteristics, new keyword + base.Dispose() call to Dispose()
- **Files modified:** 3 files in Plugins/DataWarehouse.Plugins.UltimateReplication/

---

**Total deviations:** 4 auto-fixed (3 bugs, 1 blocking)
**Impact on plan:** All fixes necessary for zero-regression build. Intelligence region removal was more aggressive than planned -- domain identity properties and helper methods were co-located with intelligence code.

## Issues Encountered
- Intelligence regions contained non-intelligence code (StrategyId, StrategyName, helper methods) that was incorrectly removed
- 871 build errors after initial backward-compat addition required systematic investigation and multi-layer fixes
- Linter reverted invalid `override virtual` syntax (C# override is implicitly virtual)

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Full solution builds with zero new errors
- All ~1,500 concrete strategies compile via backward-compat layer
- Ready for final verification (Plan 25a-05)
- Legacy methods marked with TODO(25b) for Phase 25b removal

---
*Phase: 25a-strategy-hierarchy-api-contracts*
*Completed: 2026-02-14*
