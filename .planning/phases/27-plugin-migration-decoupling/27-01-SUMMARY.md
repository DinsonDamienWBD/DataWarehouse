---
phase: 27-plugin-migration-decoupling
plan: 01
subsystem: sdk
tags: [hierarchy, plugin-bases, inheritance, re-parenting, IntelligenceAwarePluginBase]

requires:
  - phase: 24-plugin-hierarchy-storage-validation
    provides: "Hierarchy domain bases (DataPipelinePluginBase, FeaturePluginBase, 18 domain bases)"
provides:
  - "All ~60 SDK intermediate plugin bases on Hierarchy domain branches"
  - "StorageProviderPluginBase on DataPipelinePluginBase"
  - "SecurityProviderPluginBase on SecurityPluginBase"
  - "Name collision resolution for InterfacePluginBase and ReplicationPluginBase"
affects: [27-02, 27-03, 27-04, 27-05, 28-dead-code-cleanup]

tech-stack:
  added: []
  patterns:
    - "Using aliases for name collisions (HierarchyInterfacePluginBase, HierarchyReplicationPluginBase)"
    - "[Obsolete] marking on old bases being re-parented"
    - "new keyword on IIntelligenceAware members when hiding IntelligenceAwarePluginBase"

key-files:
  created: []
  modified:
    - DataWarehouse.SDK/Contracts/PluginBase.cs
    - DataWarehouse.SDK/Contracts/AedsPluginBases.cs
    - DataWarehouse.SDK/Contracts/ActiveStoragePluginBases.cs
    - DataWarehouse.SDK/Contracts/FeaturePluginInterfaces.cs
    - DataWarehouse.SDK/Contracts/InfrastructurePluginBases.cs
    - DataWarehouse.SDK/Contracts/HypervisorPluginBases.cs
    - DataWarehouse.SDK/Contracts/HardwareAccelerationPluginBases.cs
    - DataWarehouse.SDK/Contracts/CarbonAwarePluginBases.cs
    - DataWarehouse.SDK/Contracts/ComplianceAutomationPluginBases.cs
    - DataWarehouse.SDK/Contracts/LowLatencyPluginBases.cs
    - DataWarehouse.SDK/Contracts/TamperProof/IAccessLogProvider.cs
    - DataWarehouse.SDK/Contracts/TamperProof/IBlockchainProvider.cs
    - DataWarehouse.SDK/Contracts/TamperProof/ITamperProofProvider.cs
    - DataWarehouse.SDK/Contracts/TamperProof/IIntegrityProvider.cs
    - DataWarehouse.SDK/Contracts/TamperProof/IWormStorageProvider.cs
    - DataWarehouse.SDK/Contracts/DataConnectorPluginBases.cs
    - DataWarehouse.SDK/Contracts/ExabyteScalePluginBases.cs
    - DataWarehouse.SDK/Contracts/MilitarySecurityPluginBases.cs
    - DataWarehouse.SDK/Contracts/MultiMasterPluginBases.cs
    - DataWarehouse.SDK/Contracts/OrchestrationInterfaces.cs

key-decisions:
  - "Used using aliases to resolve InterfacePluginBase and ReplicationPluginBase name collisions rather than renaming"
  - "Marked old colliding bases [Obsolete] while keeping them functional for backward compatibility"
  - "Left Hierarchy abstract members abstract in intermediate bases to flow through to concrete plugins"
  - "Added new keyword on IIntelligenceAware explicit implementations that hide IntelligenceAwarePluginBase members"
  - "Implemented sensible domain property defaults (Protocol, SecurityDomain, etc.) in intermediate bases where concrete values exist"

patterns-established:
  - "Name collision: use using alias + [Obsolete] on old type"
  - "IIntelligenceAware hiding: add new keyword to IsIntelligenceAvailable, AvailableCapabilities, DiscoverIntelligenceAsync"
  - "Abstract member bridging: implement Hierarchy abstract methods as abstract or virtual with defaults in intermediate bases"

duration: 45min
completed: 2026-02-14
---

# Phase 27 Plan 01: SDK Intermediate Base Re-parenting Summary

**Re-parented ~60 SDK intermediate plugin bases from LegacyFeaturePluginBase to Hierarchy domain bases, plus StorageProviderPluginBase and SecurityProviderPluginBase from bare PluginBase**

## Performance

- **Duration:** ~45 min
- **Started:** 2026-02-14
- **Completed:** 2026-02-14
- **Tasks:** 2
- **Files modified:** 20

## Accomplishments
- Re-parented ~55 SDK intermediate bases from LegacyFeaturePluginBase to correct Hierarchy domain bases (InterfacePluginBase, OrchestrationPluginBase, SecurityPluginBase, ComputePluginBase, InfrastructurePluginBase, DataManagementPluginBase, ObservabilityPluginBase, StreamingPluginBase, MediaPluginBase, IntegrityPluginBase, ReplicationPluginBase)
- Re-parented StorageProviderPluginBase from bare PluginBase to DataPipelinePluginBase
- Re-parented SecurityProviderPluginBase from bare PluginBase to SecurityPluginBase
- Resolved InterfacePluginBase and ReplicationPluginBase name collisions using using aliases and [Obsolete] markers
- Fixed 150+ CS0108 member hiding warnings across all re-parented bases

## Task Commits

Each task was committed atomically:

1. **Task 1: Re-parent all SDK intermediate plugin bases from LegacyFeaturePluginBase** - `b86773a` (feat)
2. **Task 2: Re-parent StorageProviderPluginBase and SecurityProviderPluginBase** - `3c01399` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Contracts/PluginBase.cs` - Re-parented InterfacePluginBase, ConsensusPluginBase, RealTimePluginBase, ReplicationPluginBase, ContainerManagerPluginBase, StorageProviderPluginBase, SecurityProviderPluginBase
- `DataWarehouse.SDK/Contracts/AedsPluginBases.cs` - Re-parented 5 AEDS bases to Interface/Orchestration/Security/Compute
- `DataWarehouse.SDK/Contracts/ActiveStoragePluginBases.cs` - Re-parented WasmFunction, DataVirtualization, MediaTranscoding
- `DataWarehouse.SDK/Contracts/FeaturePluginInterfaces.cs` - Re-parented 7 bases (DataManagement, Observability, Security, Infrastructure)
- `DataWarehouse.SDK/Contracts/InfrastructurePluginBases.cs` - Re-parented 8 bases (Infrastructure, Security)
- `DataWarehouse.SDK/Contracts/HypervisorPluginBases.cs` - Re-parented 4 bases to ComputePluginBase
- `DataWarehouse.SDK/Contracts/HardwareAccelerationPluginBases.cs` - Re-parented 3 bases (Compute, Security)
- `DataWarehouse.SDK/Contracts/CarbonAwarePluginBases.cs` - Re-parented 5 bases to InfrastructurePluginBase
- `DataWarehouse.SDK/Contracts/ComplianceAutomationPluginBases.cs` - Re-parented 3 bases to SecurityPluginBase
- `DataWarehouse.SDK/Contracts/LowLatencyPluginBases.cs` - Re-parented 3 bases to InfrastructurePluginBase, fixed LowLatencyStoragePluginBase hiding
- `DataWarehouse.SDK/Contracts/TamperProof/*.cs` - Re-parented 5 bases to IntegrityPluginBase
- `DataWarehouse.SDK/Contracts/DataConnectorPluginBases.cs` - Re-parented to InterfacePluginBase
- `DataWarehouse.SDK/Contracts/ExabyteScalePluginBases.cs` - Re-parented 3 bases (Infrastructure, DataManagement)
- `DataWarehouse.SDK/Contracts/MilitarySecurityPluginBases.cs` - Fixed CS0108 hiding after SecurityProviderPluginBase re-parenting
- `DataWarehouse.SDK/Contracts/MultiMasterPluginBases.cs` - Re-parented to ReplicationPluginBase
- `DataWarehouse.SDK/Contracts/OrchestrationInterfaces.cs` - Re-parented 5 bases (Orchestration, Interface, Compute)

## Decisions Made
- Used `using` aliases (HierarchyInterfacePluginBase, HierarchyReplicationPluginBase) to resolve name collisions between old and new bases rather than renaming
- Marked old colliding bases `[Obsolete]` for deprecation signaling
- Left abstract members from Hierarchy bases as abstract pass-throughs in intermediate bases -- concrete plugins will implement them in Plans 27-02 through 27-04
- Added `new` keyword on IIntelligenceAware members (IsIntelligenceAvailable, AvailableCapabilities, DiscoverIntelligenceAsync) that hide IntelligenceAwarePluginBase members
- Provided sensible domain property defaults where intermediate bases have clear domain identities

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed nusing syntax error from PowerShell regex**
- **Found during:** Task 1 (AedsPluginBases.cs editing)
- **Issue:** PowerShell `\n` replacement prepended 'n' to 'using' keyword creating `nusing` syntax error
- **Fix:** Manually corrected the malformed directive
- **Files modified:** AedsPluginBases.cs, ActiveStoragePluginBases.cs
- **Verification:** Build passed after correction
- **Committed in:** b86773a

**2. [Rule 3 - Blocking] Restored lost using directives**
- **Found during:** Task 1 (build verification)
- **Issue:** PowerShell replacement overwrote original using directives (DataWarehouse.SDK.Distribution, DataWarehouse.SDK.Utilities)
- **Fix:** Re-added the missing using directives
- **Files modified:** AedsPluginBases.cs, ActiveStoragePluginBases.cs
- **Verification:** CS0246 errors resolved
- **Committed in:** b86773a

**3. [Rule 1 - Bug] Fixed 150+ CS0108 member hiding warnings**
- **Found during:** Task 1 (build verification)
- **Issue:** Re-parented bases with explicit IIntelligenceAware implementations hide IntelligenceAwarePluginBase members
- **Fix:** Added `new` keyword to IsIntelligenceAvailable, AvailableCapabilities, DiscoverIntelligenceAsync across 11+ files
- **Files modified:** Multiple SDK contract files
- **Verification:** Build shows zero CS0108 errors
- **Committed in:** b86773a, 3c01399

**4. [Rule 1 - Bug] Fixed HardwareCapabilities AvailableCapabilities hiding**
- **Found during:** Task 2 (build verification)
- **Issue:** LowLatencyStoragePluginBase.AvailableCapabilities (HardwareCapabilities type) hides IntelligenceAwarePluginBase.AvailableCapabilities (IntelligenceCapabilities type) after StorageProviderPluginBase re-parenting
- **Fix:** Added `new` keyword to the HardwareCapabilities AvailableCapabilities property
- **Files modified:** LowLatencyPluginBases.cs
- **Verification:** Build passed
- **Committed in:** 3c01399

---

**Total deviations:** 4 auto-fixed (2 bugs, 1 blocking, 1 bug)
**Impact on plan:** All auto-fixes were necessary for compilation correctness. No scope creep.

## Issues Encountered
- PowerShell variable expansion in bash consumed `$f` and `$c` variables -- resolved by using PowerShell script files instead of inline commands
- Name collision between old InterfacePluginBase/ReplicationPluginBase and Hierarchy equivalents -- resolved with using aliases

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All SDK intermediate bases are on Hierarchy domain branches
- Concrete plugins now need to implement abstract members from Hierarchy bases (Plans 27-02 through 27-04)
- Full solution builds with zero new errors (only pre-existing CS1729/CS0234 in UltimateCompression and AedsCore)

---
*Phase: 27-plugin-migration-decoupling*
*Completed: 2026-02-14*
