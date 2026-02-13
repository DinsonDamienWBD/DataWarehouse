# Phase 27: Plugin Migration & Decoupling Verification - Research

**Researched:** 2026-02-14
**Domain:** C# plugin base class migration, decoupling verification, message bus audit
**Confidence:** HIGH

## Summary

Phase 27 migrates all ~78 plugin classes (across 60 plugin projects) from their current base classes to the new two-branch hierarchy (DataPipelinePluginBase / FeaturePluginBase) established in Phase 24. The research reveals three distinct migration populations: (1) plugins already on the correct IntelligenceAware* specialized bases that map to new hierarchy bases, (2) plugins on LegacyFeaturePluginBase or bare IntelligenceAwarePluginBase that need remapping to the correct domain base, and (3) special cases (AirGapBridge on raw IFeaturePlugin, EdgeComputing/TamperProof on bare PluginBase).

The codebase is in excellent decoupling shape: zero plugin-to-plugin ProjectReferences exist, and the kernel references only SDK. All `using DataWarehouse.Plugins.X` directives found are intra-plugin (plugin referencing its own sub-namespaces). Message bus usage is widespread (219 Publish/Send/Subscribe calls across 56 plugin files), confirming communication patterns already flow through the bus. The primary migration work is changing base class inheritance declarations and implementing any newly required abstract methods from the domain bases.

**Primary recommendation:** Verify-first approach. Categorize all 78 plugin classes by current base, map to target base, batch by migration complexity (trivial/moderate/complex). Most Ultimate plugins on IntelligenceAware* specialized bases need a simple swap to the corresponding Hierarchy base. LegacyFeaturePluginBase plugins and special cases need more attention.

## Standard Stack

### Core (no new dependencies -- this is a refactoring phase)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| DataWarehouse.SDK | current | Contains all target base classes | All plugins reference ONLY SDK |
| DataWarehouse.SDK.Contracts.Hierarchy | current | New two-branch plugin hierarchy (Phase 24) | Target namespace for all migrations |

### Key Namespaces

| Namespace | Contains | Migration Target For |
|-----------|----------|---------------------|
| `DataWarehouse.SDK.Contracts.Hierarchy` | `FeaturePluginBase`, `DataPipelinePluginBase` | Branch roots |
| `DataWarehouse.SDK.Contracts.Hierarchy.DataPipeline` | `EncryptionPluginBase`, `CompressionPluginBase`, `StoragePluginBase`, `ReplicationPluginBase`, `DataTransitPluginBase`, `IntegrityPluginBase`, `DataTransformationPluginBase` | Data pipeline plugins |
| `DataWarehouse.SDK.Contracts.Hierarchy.Feature` | `SecurityPluginBase`, `InterfacePluginBase`, `DataManagementPluginBase`, `ComputePluginBase`, `ObservabilityPluginBase`, `StreamingPluginBase`, `MediaPluginBase`, `FormatPluginBase`, `InfrastructurePluginBase`, `OrchestrationPluginBase`, `PlatformPluginBase` | Feature/service plugins |

## Architecture Patterns

### Current Hierarchy (As-Is)

```
IPlugin
└── PluginBase (lifecycle, capability, knowledge, IDisposable)
    ├── UltimateIntelligencePlugin (PipelinePluginBase : DataTransformationPluginBase : PluginBase)
    ├── UltimateEdgeComputingPlugin (directly on PluginBase)
    ├── TamperProofPlugin (directly on PluginBase)
    ├── IntelligenceAwarePluginBase (AI socket)
    │   ├── IntelligenceAwareEncryptionPluginBase [Obsolete] → 1 plugin (UltimateEncryption)
    │   ├── IntelligenceAwareCompressionPluginBase [Obsolete] → 1 plugin (UltimateCompression)
    │   ├── IntelligenceAwareStoragePluginBase [Obsolete] → 1 plugin (UltimateStorage)
    │   ├── IntelligenceAwareAccessControlPluginBase [Obsolete] → 1 plugin (UltimateAccessControl)
    │   ├── IntelligenceAwareCompliancePluginBase [Obsolete] → 1 plugin (UltimateCompliance)
    │   ├── IntelligenceAwareDataManagementPluginBase [Obsolete] → 7 plugins
    │   ├── IntelligenceAwareKeyManagementPluginBase [Obsolete] → 1 plugin (UltimateKeyManagement)
    │   ├── IntelligenceAwareInterfacePluginBase [Obsolete] → 1 plugin (UltimateInterface)
    │   ├── IntelligenceAwareConnectorPluginBase [Obsolete] → 1 plugin (UltimateConnector)
    │   ├── IntelligenceAwareDatabasePluginBase [Obsolete] → 0 plugins (unused)
    │   └── [bare IntelligenceAwarePluginBase] → 25 plugins
    ├── LegacyFeaturePluginBase [Obsolete]
    │   ├── ConsensusPluginBase → 1 plugin (Raft)
    │   ├── InterfacePluginBase (old) → 1 plugin (KubernetesCsi)
    │   ├── AEDS bases (ControlPlane/DataPlane/ServerDispatcher/etc.) → 9 plugins
    │   ├── WasmFunctionPluginBase → 1 plugin (WasmCompute)
    │   ├── DataVirtualizationPluginBase → 1 plugin (SqlOverObject)
    │   ├── MediaTranscodingPluginBase → 1 plugin (MediaTranscoding)
    │   └── [bare LegacyFeaturePluginBase] → 7 direct plugins
    └── AirGapBridgePlugin (implements IFeaturePlugin directly -- no base class!)
```

### Target Hierarchy (To-Be)

```
PluginBase
├── UltimateIntelligencePlugin (stays on PipelinePluginBase -- special case, IS the intelligence)
├── IntelligenceAwarePluginBase
│   ├── DataPipelinePluginBase
│   │   ├── DataTransformationPluginBase
│   │   │   ├── EncryptionPluginBase ← UltimateEncryptionPlugin
│   │   │   └── CompressionPluginBase ← UltimateCompressionPlugin
│   │   ├── StoragePluginBase ← UltimateStorage, UltimateDatabaseStorage, UltimateDatabaseProtocol, UltimateStorageProcessing
│   │   ├── ReplicationPluginBase ← UltimateReplication, UltimateRAID
│   │   ├── DataTransitPluginBase ← UltimateDataTransit
│   │   └── IntegrityPluginBase ← TamperProofPlugin
│   └── FeaturePluginBase
│       ├── SecurityPluginBase ← UltimateAccessControl, UltimateCompliance, UltimateKeyManagement, UltimateDataProtection
│       ├── InterfacePluginBase ← UltimateInterface, UltimateConnector, KubernetesCsi
│       ├── DataManagementPluginBase ← UltimateDataGovernance/Catalog/Quality/Lineage/Management/Privacy/Lake/Mesh/Fabric
│       ├── ComputePluginBase ← UltimateCompute, UltimateServerless
│       ├── ObservabilityPluginBase ← UniversalObservability
│       ├── StreamingPluginBase ← UltimateStreamingData, UltimateIoTIntegration, UltimateRTOSBridge
│       ├── MediaPluginBase ← MediaTranscoding
│       ├── FormatPluginBase ← UltimateDataFormat
│       ├── InfrastructurePluginBase ← UltimateDeployment, UltimateResilience, UltimateSustainability, UltimateMultiCloud
│       ├── OrchestrationPluginBase ← UltimateWorkflow, UltimateEdgeComputing, UltimateDataIntegration
│       └── PlatformPluginBase ← UltimateSDKPorts, UltimateMicroservices, UltimateDocGen
```

### Standalone Plugin Target Mapping

Standalone (non-Ultimate) plugins map to new hierarchy FeaturePluginBase branches:

| Plugin | Current Base | Target Base |
|--------|-------------|-------------|
| AirGapBridgePlugin | IFeaturePlugin (raw) | InfrastructurePluginBase |
| AdaptiveTransportPlugin | LegacyFeaturePluginBase | StreamingPluginBase |
| RaftConsensusPlugin | ConsensusPluginBase | InfrastructurePluginBase |
| KubernetesCsiPlugin | InterfacePluginBase (old) | InterfacePluginBase (new Hierarchy) |
| FuseDriverPlugin | LegacyFeaturePluginBase | InterfacePluginBase (filesystem interface) |
| WinFspDriverPlugin | LegacyFeaturePluginBase | InterfacePluginBase (filesystem interface) |
| DataMarketplacePlugin | LegacyFeaturePluginBase | PlatformPluginBase |
| PluginMarketplacePlugin | LegacyFeaturePluginBase | PlatformPluginBase |
| SelfEmulatingObjectsPlugin | LegacyFeaturePluginBase | ComputePluginBase |
| WasmComputePlugin | WasmFunctionPluginBase | ComputePluginBase |
| SqlOverObjectPlugin | DataVirtualizationPluginBase | InterfacePluginBase |
| MediaTranscodingPlugin | MediaTranscodingPluginBase | MediaPluginBase |
| AppPlatformPlugin | IntelligenceAwarePluginBase | PlatformPluginBase |
| UniversalDashboardsPlugin | IntelligenceAwarePluginBase | InterfacePluginBase |
| UltimateFilesystemPlugin | LegacyFeaturePluginBase | StoragePluginBase (data pipeline) |
| UltimateResourceManagerPlugin | LegacyFeaturePluginBase | InfrastructurePluginBase |

### AEDS Sub-Plugins Target Mapping

The AedsCore project contains 17 plugin classes across multiple intermediate SDK bases:

| Plugin | Current Base | Target Base |
|--------|-------------|-------------|
| AedsCorePlugin | LegacyFeaturePluginBase | OrchestrationPluginBase |
| ClientCourierPlugin | LegacyFeaturePluginBase | InterfacePluginBase |
| ServerDispatcherPlugin | ServerDispatcherPluginBase | OrchestrationPluginBase |
| Http2DataPlanePlugin | DataPlaneTransportPluginBase | InterfacePluginBase |
| Http3DataPlanePlugin | DataPlaneTransportPluginBase | InterfacePluginBase |
| QuicDataPlanePlugin | DataPlaneTransportPluginBase | InterfacePluginBase |
| WebTransportDataPlanePlugin | DataPlaneTransportPluginBase | InterfacePluginBase |
| GrpcControlPlanePlugin | ControlPlaneTransportPluginBase | InterfacePluginBase |
| MqttControlPlanePlugin | ControlPlaneTransportPluginBase | InterfacePluginBase |
| WebSocketControlPlanePlugin | ControlPlaneTransportPluginBase | InterfacePluginBase |
| CodeSigningPlugin | LegacyFeaturePluginBase | SecurityPluginBase |
| DeltaSyncPlugin | LegacyFeaturePluginBase | DataManagementPluginBase |
| GlobalDeduplicationPlugin | LegacyFeaturePluginBase | DataManagementPluginBase |
| MulePlugin | LegacyFeaturePluginBase | InterfacePluginBase |
| NotificationPlugin | LegacyFeaturePluginBase | InterfacePluginBase |
| PolicyEnginePlugin | LegacyFeaturePluginBase | SecurityPluginBase |
| PreCogPlugin | LegacyFeaturePluginBase | ComputePluginBase |
| SwarmIntelligencePlugin | LegacyFeaturePluginBase | ComputePluginBase |
| ZeroTrustPairingPlugin | LegacyFeaturePluginBase | SecurityPluginBase |

## Plugin Migration Census

### Complete Plugin Inventory (78 plugin classes)

#### Category A: Ultimate Plugins on IntelligenceAware* Specialized Bases (14 plugins)
**Migration effort: MODERATE** -- swap IntelligenceAware*PluginBase to Hierarchy equivalent, implement any new abstract methods.

| Plugin | Current | Target | New Abstract Methods |
|--------|---------|--------|---------------------|
| UltimateEncryptionPlugin | IntelligenceAwareEncryptionPluginBase | EncryptionPluginBase (DataPipeline) | Transform methods if not overridden |
| UltimateCompressionPlugin | IntelligenceAwareCompressionPluginBase | CompressionPluginBase (DataPipeline) | Transform methods if not overridden |
| UltimateStoragePlugin | IntelligenceAwareStoragePluginBase | StoragePluginBase (DataPipeline) | StoreAsync, RetrieveAsync, DeleteAsync, ExistsAsync, ListAsync, GetMetadataAsync, GetHealthAsync |
| UltimateAccessControlPlugin | IntelligenceAwareAccessControlPluginBase | SecurityPluginBase (Feature) | SecurityDomain property |
| UltimateCompliancePlugin | IntelligenceAwareCompliancePluginBase | SecurityPluginBase (Feature) | SecurityDomain property |
| UltimateKeyManagementPlugin | IntelligenceAwareKeyManagementPluginBase | SecurityPluginBase (Feature) | SecurityDomain property |
| UltimateInterfacePlugin | IntelligenceAwareInterfacePluginBase | InterfacePluginBase (Feature) | none (virtual only) |
| UltimateConnectorPlugin | IntelligenceAwareConnectorPluginBase | InterfacePluginBase (Feature) | none (virtual only) |
| UltimateDataGovernancePlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |
| UltimateDataCatalogPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |
| UltimateDataQualityPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |
| UltimateDataLakePlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |
| UltimateDataMeshPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |
| UltimateDataPrivacyPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |
| UltimateDataManagementPlugin | IntelligenceAwareDataManagementPluginBase | DataManagementPluginBase (Feature) | none (virtual only) |

#### Category B: Ultimate Plugins on Bare IntelligenceAwarePluginBase (25 plugins)
**Migration effort: LOW-MODERATE** -- change from IntelligenceAwarePluginBase to specific domain base from Hierarchy, implement new abstract methods if any.

| Plugin | Target Hierarchy Base | New Abstract Methods |
|--------|----------------------|---------------------|
| UltimateComputePlugin | ComputePluginBase | ExecuteWorkloadAsync |
| UltimateServerlessPlugin | ComputePluginBase | ExecuteWorkloadAsync |
| UltimateStreamingDataPlugin | StreamingPluginBase | PublishAsync, SubscribeAsync |
| UltimateIoTIntegrationPlugin | StreamingPluginBase | PublishAsync, SubscribeAsync |
| UltimateRTOSBridgePlugin | StreamingPluginBase | PublishAsync, SubscribeAsync |
| UltimateDataFormatPlugin | FormatPluginBase | none (virtual only) |
| UniversalObservabilityPlugin | ObservabilityPluginBase | none (virtual only) |
| UltimateDeploymentPlugin | InfrastructurePluginBase | none (virtual only) |
| UltimateResiliencePlugin | InfrastructurePluginBase | none (virtual only) |
| UltimateSustainabilityPlugin | InfrastructurePluginBase | none (virtual only) |
| UltimateMultiCloudPlugin | InfrastructurePluginBase | none (virtual only) |
| UltimateWorkflowPlugin | OrchestrationPluginBase | none (virtual only) |
| UltimateEdgeComputingPlugin* | OrchestrationPluginBase | none (virtual only) |
| UltimateDataIntegrationPlugin | OrchestrationPluginBase | none (virtual only) |
| UltimateSDKPortsPlugin | PlatformPluginBase | none (virtual only) |
| UltimateMicroservicesPlugin | PlatformPluginBase | none (virtual only) |
| UltimateDocGenPlugin | PlatformPluginBase | none (virtual only) |
| UltimateReplicationPlugin | ReplicationPluginBase (DataPipeline) | ReplicateAsync, GetSyncStatusAsync |
| UltimateRaidPlugin | ReplicationPluginBase (DataPipeline) | ReplicateAsync, GetSyncStatusAsync |
| UltimateDatabaseStoragePlugin | StoragePluginBase (DataPipeline) | StoreAsync, RetrieveAsync, DeleteAsync, ExistsAsync, ListAsync, GetMetadataAsync, GetHealthAsync |
| UltimateDatabaseProtocolPlugin | StoragePluginBase (DataPipeline) | StoreAsync, RetrieveAsync, DeleteAsync, ExistsAsync, ListAsync, GetMetadataAsync, GetHealthAsync |
| UltimateStorageProcessingPlugin | StoragePluginBase (DataPipeline) | StoreAsync, RetrieveAsync, DeleteAsync, ExistsAsync, ListAsync, GetMetadataAsync, GetHealthAsync |
| UltimateDataProtectionPlugin | SecurityPluginBase | SecurityDomain property |
| AppPlatformPlugin | PlatformPluginBase | none (virtual only) |
| UniversalDashboardsPlugin | InterfacePluginBase | none (virtual only) |

*Note: UltimateEdgeComputingPlugin is currently on bare PluginBase, not IntelligenceAwarePluginBase. Extra migration needed.

#### Category C: Plugins on LegacyFeaturePluginBase (21 direct + via intermediate bases)
**Migration effort: MODERATE-HIGH** -- need to add IntelligenceAware layer (StartAsync/StopAsync -> new lifecycle), implement domain-specific abstract methods.

21 plugins directly on LegacyFeaturePluginBase (listed in "Standalone Plugin Target Mapping" above).

#### Category D: Plugins on Old Intermediate SDK Bases (14 plugins via non-Hierarchy SDK bases)
**Migration effort: MODERATE** -- intermediate bases (DataPlaneTransportPluginBase, ControlPlaneTransportPluginBase, etc.) inherit from LegacyFeaturePluginBase. Need to migrate both the SDK intermediate bases AND the concrete plugins.

9 AEDS transport plugins, 1 Raft consensus, 1 KubernetesCsi, 1 WasmCompute, 1 SqlOverObject, 1 MediaTranscoding.

#### Category E: Special Cases (3 plugins)
**Migration effort: HIGH** -- non-standard inheritance, need significant restructuring.

| Plugin | Issue | Migration Path |
|--------|-------|----------------|
| AirGapBridgePlugin | Implements IFeaturePlugin directly (no PluginBase at all) | Must be rewritten to extend InfrastructurePluginBase, inheriting full PluginBase lifecycle |
| UltimateEdgeComputingPlugin | On bare PluginBase (skips IntelligenceAwarePluginBase) | Must be changed to OrchestrationPluginBase (adds IntelligenceAware layer) |
| TamperProofPlugin | On bare PluginBase | Must be changed to IntegrityPluginBase (DataPipeline branch) |

### Migration Summary by Effort

| Category | Count | Effort | Description |
|----------|-------|--------|-------------|
| A: IntelligenceAware* → Hierarchy domain base | 14 | MODERATE | Swap obsolete specialized base for new Hierarchy equivalent |
| B: Bare IntelligenceAwarePluginBase → specific domain | 25 | LOW-MODERATE | Add domain specificity (most new bases have virtual-only methods) |
| C: LegacyFeaturePluginBase → FeaturePluginBase branch | 21 | MODERATE-HIGH | Need IntelligenceAware layer added |
| D: Old intermediate SDK bases → new bases | 14 | MODERATE | Both SDK base and plugin need updating |
| E: Special cases (raw interface / bare PluginBase) | 3 | HIGH | Structural rewrite needed |
| **Total** | **77** + UltimateIntelligencePlugin (no change) | | |

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Lifecycle management | Custom init/shutdown in each plugin | PluginBase.InitializeAsync/ShutdownAsync | Already standardized in Phase 24 |
| IntelligenceAware wiring | Manual AI socket connection | IntelligenceAwarePluginBase hooks | Graceful degradation built in |
| Capability registration | Custom registration per plugin | PluginBase.RegisterCapability() | Centralized, observable |
| Knowledge bank access | Direct ConcurrentDictionary | PluginBase.RegisterKnowledge()/QueryKnowledge() | Bounded, cached |
| Cross-plugin communication | Direct method calls | MessageBus.PublishAsync/SubscribeAsync | Decoupling requirement (DECPL-02) |

## Common Pitfalls

### Pitfall 1: Sealed Class + Abstract Method Incompatibility
**What goes wrong:** New hierarchy base classes may declare abstract methods. If a sealed plugin doesn't implement them, compile error.
**Why it happens:** The old IntelligenceAware* bases had different abstract method signatures than the new Hierarchy bases.
**How to avoid:** For each migration, diff abstract members between old and new base. Implement missing abstracts with delegate-to-existing-method pattern.
**Warning signs:** CS0534 (does not implement inherited abstract member) errors.

### Pitfall 2: Breaking the Dispose Chain
**What goes wrong:** Changing base class can break the Dispose(bool) chain established in Phase 23.
**How to avoid:** Every plugin that overrides Dispose(bool) MUST call base.Dispose(disposing). Verify after migration.
**Warning signs:** CS0108 (hides inherited member) warnings on Dispose.

### Pitfall 3: StartAsync/StopAsync Contract Change
**What goes wrong:** LegacyFeaturePluginBase requires abstract StartAsync(CancellationToken)/StopAsync(). The new FeaturePluginBase (Hierarchy) uses PluginBase lifecycle (InitializeAsync/ExecuteAsync/ShutdownAsync) instead.
**Why it happens:** Different lifecycle model between legacy and new bases.
**How to avoid:** When migrating from LegacyFeaturePluginBase, map StartAsync to InitializeAsync or a virtual OnStart, map StopAsync to ShutdownAsync. Verify the plugin's existing lifecycle calls are preserved.
**Warning signs:** Missing StartAsync implementation errors or plugins that never start.

### Pitfall 4: AEDS Intermediate Base Functionality Loss
**What goes wrong:** AEDS bases (ControlPlaneTransportPluginBase, DataPlaneTransportPluginBase, ServerDispatcherPluginBase) provide transport-specific abstract methods and lifecycle. Simply swapping to InterfacePluginBase loses these.
**Why it happens:** The intermediate bases are domain-specific wrappers not yet replicated in the new Hierarchy.
**How to avoid:** Two options: (1) Keep intermediate bases but re-parent them from LegacyFeaturePluginBase to the appropriate Hierarchy base, or (2) merge functionality into plugin code. Option 1 is safer per AD-08 zero regression.
**Warning signs:** Transport plugins that can't connect/disconnect after migration.

### Pitfall 5: IFeaturePlugin Interface Missing After Migration
**What goes wrong:** Some plugins implement IFeaturePlugin explicitly. IntelligenceAwarePluginBase already implements it, but FeaturePluginBase may not if there's a chain gap.
**How to avoid:** Verify IFeaturePlugin is satisfied through the base chain. IntelligenceAwarePluginBase implements IFeaturePlugin, so all Hierarchy bases (which inherit from it) should satisfy this.
**Warning signs:** CS0535 (does not implement interface member) errors.

### Pitfall 6: Message Bus Subscription Timing
**What goes wrong:** Plugins that subscribe to MessageBus in constructors may fail if the bus isn't available until Initialize phase.
**Why it happens:** New Phase 26 multi-phase initialization: construction (zero deps) -> initialization (MessageBus) -> activation.
**How to avoid:** Ensure all MessageBus.Subscribe calls are in InitializeAsync, not constructors.
**Warning signs:** NullReferenceException on MessageBus during construction.

## Decoupling Verification Findings

### DECPL-01: Plugin-to-Plugin Dependencies
**Status: PASS** -- Zero plugin-to-plugin ProjectReferences found in any .csproj file.
- All 60 plugin projects reference ONLY `DataWarehouse.SDK.csproj`
- Kernel references ONLY `DataWarehouse.SDK.csproj`
- Test project references individual plugins (acceptable for testing)

### DECPL-02: Message Bus Communication
**Status: PARTIALLY VERIFIED** -- 219 MessageBus.Publish/Send/Subscribe calls found across 56 plugin files. Need to verify no direct method calls exist between plugin instances at runtime.
- Plugins with heaviest MessageBus usage: AppPlatform (40), UltimateRAID (15), UltimateCompliance (8), UltimateWorkflow (12), UltimateReplication (22)
- 22 plugins have zero MessageBus usage -- these need audit to confirm they don't need inter-plugin communication or that they communicate through other SDK mechanisms

### DECPL-03/DECPL-04: Capability and Knowledge Registration
**Status: LOW USAGE** -- Only 5 occurrences of RegisterCapability/RegisterKnowledge found across 3 files (all in UltimateIntelligence plugin).
- Most plugins do NOT currently register capabilities/knowledge
- Phase 27 should add capability registration to all plugins per DECPL-04
- This is additive work, not migration

### DECPL-05: Distributed Infrastructure Leverage
**Status: DEPENDS ON PHASE 26** -- Phase 26 (distributed contracts) is a prerequisite. Once those contracts exist in SDK base classes, Phase 27 verifies plugins inherit them.

## AEDS/Intermediate Base Migration Strategy

The SDK contains ~40+ intermediate plugin bases between LegacyFeaturePluginBase and concrete plugins. Key intermediate bases and recommended approach:

### Strategy: Re-parent Intermediate Bases (Safest)

Rather than migrating every concrete AEDS plugin individually, re-parent the **SDK intermediate bases** themselves from LegacyFeaturePluginBase to the appropriate new Hierarchy base. This preserves all intermediate functionality (AD-08 zero regression) while achieving the hierarchy migration.

| SDK Intermediate Base | Current Parent | New Parent |
|-----------------------|---------------|------------|
| ControlPlaneTransportPluginBase | LegacyFeaturePluginBase | InterfacePluginBase (Hierarchy) |
| DataPlaneTransportPluginBase | LegacyFeaturePluginBase | InterfacePluginBase (Hierarchy) |
| ServerDispatcherPluginBase | LegacyFeaturePluginBase | OrchestrationPluginBase (Hierarchy) |
| ClientSentinelPluginBase | LegacyFeaturePluginBase | SecurityPluginBase (Hierarchy) |
| ClientExecutorPluginBase | LegacyFeaturePluginBase | ComputePluginBase (Hierarchy) |
| ConsensusPluginBase | LegacyFeaturePluginBase | InfrastructurePluginBase (Hierarchy) |
| InterfacePluginBase (old, in PluginBase.cs) | LegacyFeaturePluginBase | InterfacePluginBase (Hierarchy) -- name collision, use alias or rename |
| WasmFunctionPluginBase | LegacyFeaturePluginBase | ComputePluginBase (Hierarchy) |
| DataVirtualizationPluginBase | LegacyFeaturePluginBase | InterfacePluginBase (Hierarchy) |
| MediaTranscodingPluginBase | LegacyFeaturePluginBase | MediaPluginBase (Hierarchy) |
| ComplianceAutomationPluginBase | LegacyFeaturePluginBase | SecurityPluginBase (Hierarchy) |
| DataSubjectRightsPluginBase | LegacyFeaturePluginBase | SecurityPluginBase (Hierarchy) |
| ComplianceAuditPluginBase | LegacyFeaturePluginBase | SecurityPluginBase (Hierarchy) |
| CarbonIntensityProviderPluginBase | LegacyFeaturePluginBase | InfrastructurePluginBase (Hierarchy) |
| DataConnectorPluginBase | LegacyFeaturePluginBase | InterfacePluginBase (Hierarchy) |
| All HypervisorPluginBases | LegacyFeaturePluginBase | ComputePluginBase (Hierarchy) |
| All HardwareAccelerationPluginBases | LegacyFeaturePluginBase | ComputePluginBase (Hierarchy) |
| All InfrastructurePluginBases (old) | LegacyFeaturePluginBase | InfrastructurePluginBase (Hierarchy) |
| All LowLatencyPluginBases | LegacyFeaturePluginBase/StorageProviderPluginBase | InfrastructurePluginBase/StoragePluginBase (Hierarchy) |
| All TamperProof bases | LegacyFeaturePluginBase | IntegrityPluginBase (Hierarchy) |
| All MilitarySecurityBases | SecurityProviderPluginBase | SecurityPluginBase (Hierarchy) |
| DeduplicationPluginBase | LegacyFeaturePluginBase | DataManagementPluginBase (Hierarchy) |
| VersioningPluginBase | LegacyFeaturePluginBase | DataManagementPluginBase (Hierarchy) |
| SnapshotPluginBase | LegacyFeaturePluginBase | DataManagementPluginBase (Hierarchy) |
| TelemetryPluginBase | LegacyFeaturePluginBase | ObservabilityPluginBase (Hierarchy) |
| BackupPluginBase | LegacyFeaturePluginBase | DataManagementPluginBase (Hierarchy) |
| OperationsPluginBase | LegacyFeaturePluginBase | InfrastructurePluginBase (Hierarchy) |

**CRITICAL: This re-parenting of SDK intermediate bases is the highest-leverage change.** It automatically migrates all concrete plugins that inherit those intermediates, without touching plugin code at all.

### InterfacePluginBase Name Collision

The old `InterfacePluginBase` (in `PluginBase.cs`, line 1492) inherits from `LegacyFeaturePluginBase`. The new `InterfacePluginBase` (in `Contracts/Hierarchy/Feature/InterfacePluginBase.cs`) inherits from `FeaturePluginBase`. They share the same class name in different namespaces.

**Resolution:** The old one should be re-parented to the new one, or marked [Obsolete] and the concrete plugin (KubernetesCsi) migrated directly. Since KubernetesCsi already uses the old InterfacePluginBase, a simple namespace switch may work if the abstract members are compatible.

## Code Examples

### Pattern 1: Simple Domain Base Swap (Category B)
```csharp
// BEFORE
using DataWarehouse.SDK.Contracts.IntelligenceAware;

public sealed class UltimateDeploymentPlugin : IntelligenceAwarePluginBase, IDisposable
{
    // ...
}

// AFTER
using DataWarehouse.SDK.Contracts.Hierarchy.Feature;

public sealed class UltimateDeploymentPlugin : InfrastructurePluginBase, IDisposable
{
    // InfrastructurePluginBase has only virtual members, no new abstracts required
    // ...
}
```

### Pattern 2: Specialized Base Swap with Abstract Methods (Category A)
```csharp
// BEFORE
using DataWarehouse.SDK.Contracts.IntelligenceAware;

public sealed class UltimateAccessControlPlugin : IntelligenceAwareAccessControlPluginBase, IDisposable
{
    // IntelligenceAwareAccessControlPluginBase provided AuthenticateAsync, AuthorizeAsync, etc.
}

// AFTER
using DataWarehouse.SDK.Contracts.Hierarchy.Feature;

public sealed class UltimateAccessControlPlugin : SecurityPluginBase, IDisposable
{
    // SecurityPluginBase requires SecurityDomain property
    public override string SecurityDomain => "AccessControl";
    // Existing auth methods stay -- they were plugin-level, not base-level
}
```

### Pattern 3: LegacyFeaturePluginBase Migration (Category C)
```csharp
// BEFORE
public sealed class DataMarketplacePlugin : LegacyFeaturePluginBase
{
    public override Task StartAsync(CancellationToken ct) { ... }
    public override Task StopAsync() { ... }
}

// AFTER
using DataWarehouse.SDK.Contracts.Hierarchy.Feature;

public sealed class DataMarketplacePlugin : PlatformPluginBase
{
    // PlatformPluginBase inherits FeaturePluginBase → IntelligenceAwarePluginBase → PluginBase
    // PluginBase lifecycle: InitializeAsync, ExecuteAsync, ShutdownAsync
    // Map old StartAsync → override InitializeAsync or a Start call in InitializeAsync
    // Map old StopAsync → override ShutdownAsync

    protected override Task InitializeAsync(CancellationToken ct)
    {
        // old StartAsync logic here
        return base.InitializeAsync(ct);
    }

    protected override Task ShutdownAsync(CancellationToken ct)
    {
        // old StopAsync logic here
        return base.ShutdownAsync(ct);
    }
}
```

### Pattern 4: Re-parent SDK Intermediate Base (AEDS)
```csharp
// BEFORE (in SDK)
public abstract class ControlPlaneTransportPluginBase : LegacyFeaturePluginBase, IControlPlaneTransport
{
    public abstract string TransportId { get; }
    public bool IsConnected { get; protected set; }
    // ... transport methods ...
}

// AFTER (in SDK)
using DataWarehouse.SDK.Contracts.Hierarchy.Feature;

public abstract class ControlPlaneTransportPluginBase : InterfacePluginBase, IControlPlaneTransport
{
    public abstract string TransportId { get; }
    public bool IsConnected { get; protected set; }
    // ... transport methods unchanged ...
    // All 3 ControlPlane plugins automatically get new hierarchy without code changes
}
```

### Pattern 5: AirGapBridge Special Case (Category E)
```csharp
// BEFORE
public sealed class AirGapBridgePlugin : IFeaturePlugin, IDisposable
{
    // Implements IFeaturePlugin directly -- no PluginBase lifecycle at all
    // Has its own _isRunning, manual _disposed tracking
}

// AFTER
using DataWarehouse.SDK.Contracts.Hierarchy.Feature;

public sealed class AirGapBridgePlugin : InfrastructurePluginBase, IDisposable
{
    // Now inherits full PluginBase lifecycle
    // Move _isRunning tracking to InitializeAsync/ShutdownAsync
    // PluginBase already provides IDisposable via Dispose(bool) pattern
    // Must implement Id, Name, Version, Category properties from PluginBase
    public override string Id => "air-gap-bridge";
    public override string Name => "Air-Gap Bridge";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.FeatureProvider;
}
```

## Migration Execution Order

### Recommended Plan Structure

**Plan 27-01: SDK Intermediate Base Re-parenting (highest leverage)**
- Re-parent all ~40 intermediate SDK bases from LegacyFeaturePluginBase to appropriate Hierarchy bases
- This automatically migrates all AEDS plugins, WasmCompute, SqlOverObject, MediaTranscoding, Raft, KubernetesCsi, and all future-ready bases
- Handle InterfacePluginBase name collision
- Handle lifecycle method mapping (StartAsync/StopAsync -> InitializeAsync/ShutdownAsync)
- Build verification

**Plan 27-02: Ultimate Plugin Migration Batch 1 (DataPipeline branch)**
- UltimateEncryption (IntelligenceAwareEncryptionPluginBase -> EncryptionPluginBase)
- UltimateCompression (IntelligenceAwareCompressionPluginBase -> CompressionPluginBase)
- UltimateStorage (IntelligenceAwareStoragePluginBase -> StoragePluginBase)
- UltimateDatabaseStorage, UltimateDatabaseProtocol, UltimateStorageProcessing (IntelligenceAwarePluginBase -> StoragePluginBase)
- UltimateReplication, UltimateRAID (IntelligenceAwarePluginBase -> ReplicationPluginBase)
- UltimateDataTransit (LegacyFeaturePluginBase -> DataTransitPluginBase)
- TamperProofPlugin (PluginBase -> IntegrityPluginBase)
- Build verification

**Plan 27-03: Ultimate Plugin Migration Batch 2 (Feature branch)**
- Security: UltimateAccessControl, UltimateCompliance, UltimateKeyManagement, UltimateDataProtection
- Interface: UltimateInterface, UltimateConnector
- DataManagement: all 9 DataManagement plugins
- Compute: UltimateCompute, UltimateServerless
- Observability: UniversalObservability
- Streaming: UltimateStreamingData, UltimateIoTIntegration, UltimateRTOSBridge
- Format: UltimateDataFormat
- Infrastructure: UltimateDeployment, UltimateResilience, UltimateSustainability, UltimateMultiCloud
- Orchestration: UltimateWorkflow, UltimateEdgeComputing, UltimateDataIntegration
- Platform: UltimateSDKPorts, UltimateMicroservices, UltimateDocGen
- Remaining: AppPlatform, UniversalDashboards
- Build verification

**Plan 27-04: Standalone Plugin Migration + Special Cases**
- AirGapBridgePlugin (IFeaturePlugin -> InfrastructurePluginBase -- major rewrite)
- All remaining LegacyFeaturePluginBase direct plugins (DataMarketplace, PluginMarketplace, SelfEmulatingObjects, AdaptiveTransport, FuseDriver, WinFspDriver, UltimateFilesystem, UltimateResourceManager, UltimateDataLineage)
- Build verification

**Plan 27-05: Decoupling Verification & Capability Registration**
- Static analysis: verify zero plugin-to-plugin references remain
- Message bus audit: verify all inter-plugin communication uses message bus
- Add capability registration to plugins that lack it
- Add knowledge bank registration where appropriate
- Verify distributed infrastructure features are accessible from base classes (Phase 26 dependency)
- Full build verification with all 1,039+ tests passing

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| IntelligenceAware*PluginBase | Hierarchy domain bases | Phase 24 (created), Phase 27 (migration) | All [Obsolete] specialized bases removable in Phase 28 |
| LegacyFeaturePluginBase | FeaturePluginBase (Hierarchy) | Phase 24 (created), Phase 27 (migration) | Standardized lifecycle across all feature plugins |
| StartAsync/StopAsync lifecycle | InitializeAsync/ExecuteAsync/ShutdownAsync | Phase 24 (PluginBase) | Consistent 3-phase lifecycle |
| Bare PluginBase for plugins | Always through IntelligenceAwarePluginBase (except Intelligence) | AD-01 | Every plugin gets AI socket and graceful degradation |

## Open Questions

1. **Abstract method compatibility between old and new bases**
   - What we know: New Hierarchy bases have abstract methods (e.g., StoragePluginBase has 7 abstract methods). Old IntelligenceAware*PluginBase had different abstract methods.
   - What's unclear: Whether Ultimate plugins already implement the new abstract signatures or need adaptation.
   - Recommendation: Verify each Category A plugin's existing methods against new base requirements before migration. May need bridge methods.

2. **UltimateIntelligencePlugin special handling**
   - What we know: It inherits from PipelinePluginBase : DataTransformationPluginBase : PluginBase. It IS the intelligence provider per HIER-07.
   - What's unclear: Whether it should stay on PipelinePluginBase or move somewhere else. The SDK_REFACTOR_PLAN says "inherits PluginBase directly."
   - Recommendation: Leave on current PipelinePluginBase for now (it works, and PipelinePluginBase is on the DataPipeline branch which goes through IntelligenceAwarePluginBase -- but Intelligence is special). Flag for review but do not change.

3. **Lifecycle method mapping for LegacyFeaturePluginBase plugins**
   - What we know: LegacyFeaturePluginBase requires StartAsync(CancellationToken) and StopAsync(). New hierarchy uses PluginBase's InitializeAsync/ShutdownAsync.
   - What's unclear: Whether all LegacyFeaturePluginBase plugins' StartAsync/StopAsync logic maps cleanly to InitializeAsync/ShutdownAsync.
   - Recommendation: Map StartAsync -> InitializeAsync, StopAsync -> ShutdownAsync. The new FeaturePluginBase provides SupportsHotReload and FeatureCategory as additional override points.

4. **Phase 26 dependency for DECPL-05**
   - What we know: DECPL-05 requires plugins to leverage auto-scaling, load balancing, P2P, auto-sync, auto-tier, auto-governance from SDK base classes. These are Phase 26 deliverables.
   - What's unclear: Phase 26 is not yet executed. How much of DECPL-05 can be verified?
   - Recommendation: Phase 27 plan should include a verification task for DECPL-05 that checks whether distributed infrastructure hooks exist in base classes. If Phase 26 is not complete, DECPL-05 verification becomes a post-Phase-26 gate check.

## Sources

### Primary (HIGH confidence)
- Codebase analysis: Direct grep/read of all 60 plugin projects, 78 plugin classes
- SDK hierarchy: `DataWarehouse.SDK/Contracts/Hierarchy/` -- 20 new base classes (Phase 24 output)
- SDK obsolete bases: `DataWarehouse.SDK/Contracts/IntelligenceAware/SpecializedIntelligenceAwareBases.cs` -- 11 [Obsolete] markers
- SDK legacy base: `DataWarehouse.SDK/Contracts/PluginBase.cs` -- LegacyFeaturePluginBase [Obsolete]
- Target hierarchy: `SDK_REFACTOR_PLAN.md` lines 246-402
- Architecture decisions: `.planning/ARCHITECTURE_DECISIONS.md` -- AD-01, AD-03, AD-05, AD-08

### Secondary (MEDIUM confidence)
- Phase 24 execution notes: `.planning/STATE.md` -- 18 domain plugin bases created, two-branch hierarchy established
- Phase 25b execution notes: `.planning/STATE.md` -- all 1,727 strategies migrated to unified hierarchy

## Metadata

**Confidence breakdown:**
- Plugin inventory: HIGH -- direct codebase analysis of all 78 plugin classes
- Migration mapping: HIGH -- target hierarchy documented in SDK_REFACTOR_PLAN.md and ARCHITECTURE_DECISIONS.md
- Abstract method analysis: MEDIUM -- checked new Hierarchy base signatures but full compatibility with existing plugin code needs per-plugin verification
- Decoupling status: HIGH -- zero cross-plugin references confirmed via ProjectReference and using-directive analysis
- AEDS strategy: MEDIUM -- re-parenting intermediate bases is sound but lifecycle method mapping needs careful implementation

**Research date:** 2026-02-14
**Valid until:** 2026-03-14 (stable -- internal codebase, no external dependency changes)
