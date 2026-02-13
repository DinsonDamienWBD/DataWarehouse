---
phase: 25b-strategy-migration
plan: 06
type: execute
wave: 3
depends_on: ["25b-04", "25b-05"]
files_modified:
  - DataWarehouse.SDK/Contracts/StrategyBase.cs
autonomous: true

must_haves:
  truths:
    - "Legacy backward-compat shim methods removed from StrategyBase (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable)"
    - "Full solution (DataWarehouse.slnx) builds with zero new errors after shim removal"
    - "All existing tests pass (dotnet test)"
    - "No strategy class in any plugin directly inherits a fragmented base -- all go through StrategyBase"
    - "No concrete strategy file contains ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability overrides"
    - "Intelligence boilerplate exists ONLY at the plugin level (if at all), not at strategy level"
    - "All ~1,727 strategies produce identical results for identical inputs (behavioral equivalence via AD-08)"
  artifacts:
    - path: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      provides: "Clean StrategyBase with zero intelligence code"
      contains: "abstract class StrategyBase"
  key_links:
    - from: "All ~1,727 concrete strategies"
      to: "StrategyBase"
      via: "inheritance chain through domain bases"
      pattern: "class.*:.*StrategyBase"
    - from: "55 strategies with MessageBus usage"
      to: "Plugin-level intelligence (Phase 27)"
      via: "MessageBus access removed from StrategyBase -- strategies must get MessageBus from plugin"
      pattern: "MessageBus"
---

<objective>
Remove the backward-compatibility shim methods from StrategyBase, perform final build verification across the entire solution, run all tests, and verify the complete strategy hierarchy migration.

Purpose: After plans 25b-01 through 25b-05, all ~1,727 strategies are verified against the new hierarchy and all plugin-local bases have been migrated. The StrategyBase backward-compat shim (added in 25a-03) provided legacy ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability/MessageBus/IsIntelligenceAvailable methods to prevent compilation errors during migration. Now that migration is complete, these shims can be removed -- making StrategyBase truly intelligence-free per AD-05.

CRITICAL WARNING: The 55 strategy files that actively use MessageBus (45 Interface, 9 DataProtection, 1 DataManagement) will lose their MessageBus access when the shim is removed. This plan must handle this carefully -- either these strategies need to get MessageBus from elsewhere, or the shim must be partially preserved for them.

Output: Clean StrategyBase (zero intelligence), full solution builds, all tests pass, complete hierarchy verification.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05: no intelligence on strategies, AD-08: zero regression)
@.planning/phases/25b-strategy-migration/25b-RESEARCH.md (Section 5: shim removal, Section 7: MessageBus handling)
@.planning/phases/25b-strategy-migration/25b-04-SUMMARY.md (plugin-local bases migrated)
@.planning/phases/25b-strategy-migration/25b-05-SUMMARY.md (intelligence bases migrated, MessageBus strategies handled)
@DataWarehouse.SDK/Contracts/StrategyBase.cs (current state with legacy shim)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Remove backward-compat shim from StrategyBase and handle MessageBus strategies</name>
  <files>DataWarehouse.SDK/Contracts/StrategyBase.cs</files>
  <action>
**Step 1: Read current StrategyBase.cs.**
Read `DataWarehouse.SDK/Contracts/StrategyBase.cs` fully. Identify the legacy backward-compat region added by 25a-03:
```
#region Legacy Intelligence Compatibility (Phase 25b removes these)
```
This region contains:
- `ConfigureIntelligence(IMessageBus? messageBus)` -- virtual no-op
- `GetStrategyKnowledge()` -- virtual returning default KnowledgeObject
- `GetStrategyCapability()` -- virtual returning default RegisteredCapability
- `MessageBus` property -- always null
- `IsIntelligenceAvailable` property -- always false

**Step 2: Assess impact of removing the shim on MessageBus-using strategies.**

Before removing, check if ANY concrete strategy still overrides these methods:
```
grep -rn "override.*ConfigureIntelligence\|override.*GetStrategyKnowledge\|override.*GetStrategyCapability" Plugins/ --include="*.cs"
```

Check how many strategies reference MessageBus or IsIntelligenceAvailable:
```
grep -rn "MessageBus\b" Plugins/ --include="*.cs" -l | wc -l
grep -rn "IsIntelligenceAvailable" Plugins/ --include="*.cs" -l | wc -l
```

**Step 3: Decide shim removal strategy.**

If the override count from Step 2 is ZERO and all MessageBus/IsIntelligenceAvailable references are safely guarded:
- Remove the entire shim region
- Any strategy that references `MessageBus` (the property) will get a compilation error
- This means the 55 strategies need to be fixed

If MessageBus removal would break 55+ files, use a PRAGMATIC approach per research recommendation:
- Remove ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability (no concrete strategy overrides these)
- KEEP MessageBus property and IsIntelligenceAvailable as MINIMAL stubs (not full intelligence infrastructure, just the property that returns null)
- Mark them `// PRESERVED: 55 strategies reference this. Phase 27 moves MessageBus to plugin-level injection.`
- This preserves AD-08 (zero regression) while removing the intelligence INTERFACE methods per AD-05

The PRAGMATIC approach is recommended: remove the intelligence interface methods (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability) but KEEP the minimal MessageBus property and IsIntelligenceAvailable check. This satisfies AD-05 (strategies don't participate in intelligence protocol) while preserving AD-08 (no regression in the 55 strategies that use MessageBus).

**Step 4: Remove intelligence interface methods from StrategyBase.**
Remove:
- `public virtual void ConfigureIntelligence(IMessageBus? messageBus) { }` -- no overrides exist
- `public virtual KnowledgeObject GetStrategyKnowledge() { ... }` -- no overrides exist
- `public virtual RegisteredCapability GetStrategyCapability() { ... }` -- no overrides exist

If keeping MessageBus/IsIntelligenceAvailable (pragmatic approach), simplify them:
```csharp
// PRESERVED: ~55 strategy files reference these. Phase 27 migrates MessageBus
// access to plugin-level injection. Until then, these provide safe null access.
protected IMessageBus? MessageBus { get; private set; }
protected bool IsIntelligenceAvailable => MessageBus != null;
```

Remove any `using` statements that are no longer needed after removing GetStrategyKnowledge/GetStrategyCapability (e.g., `using DataWarehouse.SDK.AI;` if KnowledgeObject/RegisteredCapability are no longer referenced).

**Step 5: Build full solution.**
```
dotnet build DataWarehouse.slnx
```
If any compilation errors appear:
- Strategy overriding ConfigureIntelligence -> remove the override (it's a no-op)
- Strategy overriding GetStrategyKnowledge -> remove the override (it was already a no-op via shim)
- Strategy overriding GetStrategyCapability -> same
- Strategy accessing MessageBus without MessageBus property -> if pragmatic approach kept the property, this should still compile

Fix ALL errors until the full solution builds.

**Step 6: Run all tests.**
```
dotnet test DataWarehouse.slnx --no-build
```
If build succeeded, tests should pass. Document results: how many tests ran, how many passed, how many failed.

Expected: 939+ tests pass, zero failures.

If any test fails, analyze the failure:
- If related to strategy intelligence (ConfigureIntelligence, etc.) -> the shim removal caused a regression, fix it
- If pre-existing failure -> document but do not fix (not Phase 25b scope)
  </action>
  <verify>
- `dotnet build DataWarehouse.slnx` exits 0 (zero new errors)
- `dotnet test DataWarehouse.slnx` -- all tests pass (or only pre-existing failures)
- `grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" DataWarehouse.SDK/Contracts/StrategyBase.cs` returns zero matches (intelligence interface methods removed)
- StrategyBase has ZERO intelligence protocol methods
  </verify>
  <done>Backward-compat shim intelligence methods (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability) removed from StrategyBase. MessageBus property handled appropriately (removed or preserved as minimal stub for 55 strategies). Full solution builds. All tests pass.</done>
</task>

<task type="auto">
  <name>Task 2: Final hierarchy verification and comprehensive audit</name>
  <files></files>
  <action>
**Step 1: Verify no strategy directly inherits a fragmented base outside the hierarchy.**
Run comprehensive grep to find any strategy class that inherits from something OTHER than a recognized hierarchy path:

List all abstract strategy base classes in the SDK:
```
grep -rn "abstract class.*StrategyBase" DataWarehouse.SDK/ --include="*.cs"
```

Verify every concrete strategy in Plugins/ inherits (directly or transitively) from one of these known bases:
```
grep -rn "class.*:.*StrategyBase\|class.*:.*EncryptionStrategyBase\|class.*:.*CompressionStrategyBase\|class.*:.*StorageStrategyBase\|class.*:.*SecurityStrategyBase\|class.*:.*ComplianceStrategyBase\|class.*:.*StreamingStrategyBase\|class.*:.*ReplicationStrategyBase\|class.*:.*DataTransitStrategyBase\|class.*:.*InterfaceStrategyBase\|class.*:.*MediaStrategyBase\|class.*:.*ObservabilityStrategyBase\|class.*:.*ConnectionStrategyBase\|class.*:.*DataFormatStrategyBase\|class.*:.*DataLakeStrategyBase\|class.*:.*DataMeshStrategyBase\|class.*:.*PipelineComputeStrategyBase\|class.*:.*StorageProcessingStrategyBase\|class.*:.*RaidStrategyBase\|class.*:.*KeyStoreStrategyBase" Plugins/ --include="*.cs" | wc -l
```

Also check for plugin-local bases that should be in the hierarchy:
```
grep -rn "class.*:.*AccessControlStrategyBase\|class.*:.*ComputeRuntimeStrategyBase\|class.*:.*DataManagementStrategyBase\|class.*:.*DataProtectionStrategyBase\|class.*:.*StreamingDataStrategyBase" Plugins/ --include="*.cs" | wc -l
```

**Step 2: Grand total strategy count.**
Sum all concrete strategy classes found across ALL domains. Compare to research estimate of ~1,727. Document the actual total.

**Step 3: Verify zero intelligence boilerplate in ALL plugin-local bases.**
```
grep -rn "void ConfigureIntelligence\|KnowledgeObject GetStrategyKnowledge\|RegisteredCapability GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateCompute/ Plugins/DataWarehouse.Plugins.UltimateDataProtection/ Plugins/DataWarehouse.Plugins.UltimateRAID/ --include="*.cs"
```
Expected: zero matches (all intelligence boilerplate removed from plugin-local bases in 25b-04/25b-05).

**Step 4: Verify RAID plugin-local base (edge case).**
Research found RAID has a plugin-local `RaidStrategyBase` with a `_messageBus` field and `SetMessageBus` method, but 0 strategies use it. Verify:
```
grep -rn "class.*:.*RaidStrategyBase" Plugins/DataWarehouse.Plugins.UltimateRAID/ --include="*.cs"
```
If the plugin-local RaidStrategyBase has 0 inheritors, it's dead code. Document for Phase 28 cleanup. Do NOT delete it now (AD-08 Rule 7: no silent removals).

**Step 5: Verify StrategyBase is clean (final state).**
Read `DataWarehouse.SDK/Contracts/StrategyBase.cs` and confirm:
- NO `ConfigureIntelligence` method
- NO `GetStrategyKnowledge` method
- NO `GetStrategyCapability` method
- NO `KnowledgeObject` references
- NO `RegisteredCapability` references
- MessageBus/IsIntelligenceAvailable: either removed entirely or minimal stub with Phase 27 comment

**Step 6: Run full solution build one final time.**
```
dotnet build DataWarehouse.slnx
```
Must exit 0 with zero new errors.

**Step 7: Run tests one final time.**
```
dotnet test DataWarehouse.slnx --no-build
```
Document: {passed}/{total} tests pass.

**Step 8: Create comprehensive summary table.**
| Domain | Plugin | Strategies | Type | Base Changed | Intelligence Removed | MessageBus Users | Build |
|--------|--------|------------|------|--------------|---------------------|------------------|-------|
| Transit | UltimateDataTransit | 11 | A | No | 0 | 0 | OK |
| Media | Transcoding.Media | 20 | A | No | 0 | 0 | OK |
| DataFormat | UltimateDataFormat | 28 | A | No | 0 | 0 | OK |
| StorageProcessing | UltimateStorageProcessing | 43 | A | No | 0 | 0 | OK |
| Observability | UniversalObservability | 55 | A | No | 0 | 0 | OK |
| DataLake | UltimateDataLake | 56 | A | No | 0 | 0 | OK |
| DataMesh | UltimateDataMesh | 56 | A | No | 0 | 0 | OK |
| Compression | UltimateCompression | 59 | A | No | 0 | 0 | OK* |
| Replication | UltimateReplication | 61 | A | No | 0 | 0 | OK |
| KeyManagement | UltimateKeyManagement | 69 | A | No | 0 | 0 | OK |
| RAID | UltimateRAID | 47 | A | No | 0 | 0 | OK |
| Storage | UltimateStorage | 130 | A | No | 0 | 0 | OK |
| DatabaseStorage | UltimateDatabaseStorage | 49 | A | No | 0 | 0 | OK |
| Connector | UltimateConnector | 280 | A | No | 0 | 1 | OK |
| Encryption | UltimateEncryption | 69 | A | No | 0 | 0 | OK |
| AccessControl | UltimateAccessControl | 146 | B | Yes | 0 | 0 | OK |
| Compliance | UltimateCompliance | 149 | B | Yes | 0 | 0 | OK |
| DataManagement | UltimateDataManagement | 101 | B | Yes | 0 | 1 | OK |
| Streaming | UltimateStreamingData | 58 | B | Yes | 0 | 0 | OK |
| Compute | UltimateCompute | 85 | B | Yes | ~60 lines | 0 | OK |
| DataProtection | UltimateDataProtection | 82 | B+C | Yes | ~30 lines | 9 | OK |
| Interface | UltimateInterface | 73 | C | No | 0 | 45 | OK |
| **TOTAL** | | **~1,727** | | | | **56** | |

(*Compression has pre-existing errors)
  </action>
  <verify>
- Full solution `dotnet build DataWarehouse.slnx` exits 0
- `dotnet test DataWarehouse.slnx --no-build` -- all tests pass
- StrategyBase has zero ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability
- Grand total strategy count documented (~1,727)
- All plugin-local bases verified migrated
- RAID dead plugin-local base documented for Phase 28
- Comprehensive summary table created
  </verify>
  <done>Phase 25b complete. All ~1,727 strategies verified against unified StrategyBase hierarchy. 6 plugin-local bases migrated (AccessControl, Compliance, DataManagement, Streaming, Compute, DataProtection). Intelligence boilerplate removed from Compute (~60 lines) and DataProtection (~30 lines) bases. StrategyBase intelligence interface methods removed. 56 strategies with active MessageBus usage preserved (AD-08). Full solution builds. All tests pass. Comprehensive audit table documents every domain.</done>
</task>

</tasks>

<verification>
1. StrategyBase has ZERO intelligence interface methods (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability removed)
2. Full solution `dotnet build DataWarehouse.slnx` exits 0 with zero new errors
3. All existing tests pass via `dotnet test`
4. Grand total of ~1,727 strategies confirmed in hierarchy
5. All 6 plugin-local bases migrated to SDK hierarchy
6. All intermediate bases (EnhancedReplicationStrategyBase, Pkcs11HsmStrategyBase, UltimateStorageStrategyBase, DatabaseStorageStrategyBase, 10 Connector sub-bases, WasmLanguageStrategyBase, 10 DataManagement sub-bases) cascade correctly
7. 56 strategies with active MessageBus usage preserved per AD-08
8. Zero behavioral regressions
9. RAID dead plugin-local base documented for Phase 28 cleanup
</verification>

<success_criteria>
- StrategyBase is intelligence-free (AD-05 satisfied)
- Full solution builds with zero new errors
- All 939+ tests pass
- ~1,727 strategies in unified hierarchy
- Zero behavioral regressions (AD-08 satisfied)
- STRAT-04 complete (fragmented bases consolidated)
- STRAT-06 complete (all strategies migrated, boilerplate removed)
- REGR-01, REGR-02 satisfied at phase boundary
</success_criteria>

<output>
After completion, create `.planning/phases/25b-strategy-migration/25b-06-SUMMARY.md`
</output>
