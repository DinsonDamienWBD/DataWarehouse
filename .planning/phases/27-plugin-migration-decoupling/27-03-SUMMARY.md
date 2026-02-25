---
phase: 27-plugin-migration-decoupling
plan: 03
subsystem: plugins
tags: [hierarchy, feature, security, interface, data-management, compute, streaming, infrastructure, orchestration, platform]

requires:
  - phase: 27-01
    provides: SDK intermediate bases re-parented to Hierarchy domain bases
  - phase: 24
    provides: Hierarchy Feature domain bases (SecurityPluginBase, InterfacePluginBase, etc.)
provides:
  - 32 Feature-branch plugins on correct Hierarchy Feature domain bases
  - Domain property implementations (SecurityDomain, Protocol, RuntimeType, etc.)
  - Abstract method stubs for Compute (ExecuteWorkloadAsync), Streaming (PublishAsync/SubscribeAsync)
affects: [27-05-decoupling-verification, 28-obsoletion]

tech-stack:
  added: []
  patterns: [powershell-batch-migration, domain-property-pattern, nlp-method-migration]

key-files:
  modified:
    - Plugins/DataWarehouse.Plugins.UltimateAccessControl/UltimateAccessControlPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateCompliance/UltimateCompliancePlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateKeyManagement/UltimateKeyManagementPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataProtection/UltimateDataProtectionPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateInterface/UltimateInterfacePlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateConnector/UltimateConnectorPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateCompute/UltimateComputePlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateServerless/UltimateServerlessPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateStreamingData/UltimateStreamingDataPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateWorkflow/UltimateWorkflowPlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateEdgeComputing/UltimateEdgeComputingPlugin.cs

key-decisions:
  - "Used PowerShell Migrate-Plugin function for batch migration of 32 plugins across 10 domains"
  - "Added PluginCategory.SecurityProvider override for AccessControl/Compliance (was on old IntelligenceAware base)"
  - "Migrated NLP methods (ParseIntentAsync, GenerateConversationResponseAsync, DetectLanguageAsync) inline to UltimateInterface"
  - "Used FQN for InterfacePluginBase and ReplicationPluginBase to avoid name collision with old SDK classes"
  - "Changed KeyStoreType and InterfaceProtocol from override to plain properties (not on new base chain)"

patterns-established:
  - "Domain property pattern: each Feature base requires a string property identifying the sub-domain"
  - "NLP method migration: when base provided utility methods, copy them to the concrete plugin as private methods"
  - "Category property propagation: verify if old base provided Category override; add to plugin if new base does not"

duration: 35min
completed: 2026-02-14
---

# Plan 27-03: Feature Plugin Migration Summary

**32 Feature-branch plugins migrated across 10 domains (Security, Interface, DataManagement, Compute, Observability, Streaming, Format, Infrastructure, Orchestration, Platform) with domain properties, abstract method stubs, and NLP method migration**

## Performance

- **Duration:** ~35 min
- **Tasks:** 2 (executed as single batch)
- **Files modified:** 32

## Accomplishments
- All 32 Feature-branch Ultimate plugins migrated from obsolete IntelligenceAware* bases to Hierarchy Feature domain bases
- Domain-specific abstract properties implemented for all 10 Feature base classes
- Compute plugins got ExecuteWorkloadAsync stubs, Streaming plugins got PublishAsync/SubscribeAsync stubs
- UltimateInterface NLP methods (ParseIntentAsync, GenerateConversationResponseAsync, DetectLanguageAsync) preserved by copying from old base class
- UltimateEdgeComputing migrated from bare PluginBase (Category E special case)

## Task Commits

1. **Task 1+2: Migrate all 32 Feature-branch plugins** - `b33d959` (feat)

## Files Created/Modified

### Security Domain (4 plugins -> SecurityPluginBase)
- `UltimateAccessControlPlugin.cs` - Added SecurityDomain, Category override
- `UltimateCompliancePlugin.cs` - Added SecurityDomain, Category override
- `UltimateKeyManagementPlugin.cs` - Added SecurityDomain, changed KeyStoreType to plain property
- `UltimateDataProtectionPlugin.cs` - Added SecurityDomain

### Interface Domain (2+1 plugins -> InterfacePluginBase)
- `UltimateInterfacePlugin.cs` - Added Protocol, migrated NLP helper methods inline, changed InterfaceProtocol to plain property
- `UltimateConnectorPlugin.cs` - Added Protocol
- `UniversalDashboardsPlugin.cs` - Added Protocol="Dashboard"

### DataManagement Domain (7 plugins -> DataManagementPluginBase)
- 7 plugins with DataManagementDomain property (no abstract methods required)

### Compute Domain (2 plugins -> ComputePluginBase)
- `UltimateComputePlugin.cs` - Added RuntimeType, ExecuteWorkloadAsync stub
- `UltimateServerlessPlugin.cs` - Added RuntimeType, ExecuteWorkloadAsync stub (fixed placement from nested class)

### Other Domains
- `UniversalObservabilityPlugin.cs` - ObservabilityPluginBase with ObservabilityDomain
- 3 Streaming plugins - StreamingPluginBase with PublishAsync/SubscribeAsync stubs
- `UltimateDataFormatPlugin.cs` - FormatPluginBase with FormatFamily
- 4 Infrastructure plugins - InfrastructurePluginBase with InfrastructureDomain
- 3 Orchestration plugins - OrchestrationPluginBase with OrchestrationMode
- 3+2 Platform plugins - PlatformPluginBase with PlatformDomain

## Decisions Made
- Used PowerShell `Migrate-Plugin` function for consistent batch migration
- Added `PluginCategory.SecurityProvider` Category override for AccessControl and Compliance (their old base provided it)
- Used FQN `DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase` to avoid ambiguity with old InterfacePluginBase
- Migrated NLP methods from IntelligenceAwareInterfacePluginBase to UltimateInterface as private methods (still have access to HasCapability/SendIntelligenceRequestAsync via IntelligenceAwarePluginBase in the chain)
- Used FQN for LanguageDetectionResult to avoid ambiguity between SDK.AI and SDK.Contracts.IntelligenceAware namespaces

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed StreamingData abstract stubs inserted inside IInitializable interface**
- **Found during:** Task 2 (Streaming migration)
- **Issue:** PowerShell script's last-brace replacement put PublishAsync/SubscribeAsync stubs inside IInitializable interface instead of main plugin class
- **Fix:** Moved stubs to correct location in UltimateStreamingDataPlugin class, added missing #endregion
- **Files modified:** UltimateStreamingDataPlugin.cs
- **Committed in:** b33d959

**2. [Rule 1 - Bug] Fixed Serverless ExecuteWorkloadAsync stub inserted inside FunctionStatistics record**
- **Found during:** Task 2 (Compute migration)
- **Issue:** Same last-brace replacement issue put ExecuteWorkloadAsync inside nested FunctionStatistics record
- **Fix:** Moved stub to UltimateServerlessPlugin class before Dispose method
- **Files modified:** UltimateServerlessPlugin.cs
- **Committed in:** b33d959

**3. [Rule 2 - Missing Critical] Added Category override for AccessControl and Compliance plugins**
- **Found during:** Task 1 (Security migration)
- **Issue:** Old IntelligenceAwareAccessControlPluginBase/CompliancePluginBase provided `Category => PluginCategory.SecurityProvider`. New SecurityPluginBase chain doesn't provide Category (it's abstract on PluginBase). CS0534 error.
- **Fix:** Added `public override PluginCategory Category => PluginCategory.SecurityProvider;` to both plugins
- **Files modified:** UltimateAccessControlPlugin.cs, UltimateCompliancePlugin.cs
- **Committed in:** b33d959

**4. [Rule 3 - Blocking] Migrated NLP helper methods for UltimateInterface**
- **Found during:** Task 1 (Interface migration)
- **Issue:** UltimateInterfacePlugin called ParseIntentAsync, GenerateConversationResponseAsync, DetectLanguageAsync which were on old IntelligenceAwareInterfacePluginBase. CS0103 errors.
- **Fix:** Copied method bodies from old base class to plugin as private methods (they use HasCapability/SendIntelligenceRequestAsync which are still accessible via IntelligenceAwarePluginBase in chain)
- **Files modified:** UltimateInterfacePlugin.cs
- **Committed in:** b33d959

---

**Total deviations:** 4 auto-fixed (2 bugs, 1 missing critical, 1 blocking)
**Impact on plan:** All fixes necessary for compilation. PowerShell batch migration occasionally misplaces code at file boundaries. No scope creep.

## Issues Encountered
- PowerShell last-brace replacement strategy inserts code at wrong nesting level when files have nested types after the main class
- Some old IntelligenceAware* bases provided Category and domain-specific helper methods that are not on the new Hierarchy chain

## Next Phase Readiness
- All 32 Feature-branch plugins on Hierarchy bases, ready for Plan 27-05 verification
- Combined with Plan 27-02, 42 of ~78 total plugin classes are now migrated

---
*Phase: 27-plugin-migration-decoupling*
*Completed: 2026-02-14*
