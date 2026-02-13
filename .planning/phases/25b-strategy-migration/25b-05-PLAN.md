---
phase: 25b-strategy-migration
plan: 05
type: execute
wave: 2
depends_on: ["25b-01", "25b-02", "25b-03"]
files_modified:
  - Plugins/DataWarehouse.Plugins.UltimateCompute/ComputeRuntimeStrategyBase.cs
  - Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataProtectionStrategyBase.cs
autonomous: true

must_haves:
  truths:
    - "ComputeRuntimeStrategyBase inherits from SDK PipelineComputeStrategyBase (or StrategyBase + interface)"
    - "Intelligence boilerplate removed from ComputeRuntimeStrategyBase (~60 lines: ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable)"
    - "DataProtectionStrategyBase inherits from SDK hierarchy"
    - "Intelligence boilerplate removed from DataProtectionStrategyBase (~30 lines + 26 intelligence helper refs)"
    - "All 85 Compute strategies compile unchanged after base migration"
    - "All 82 DataProtection strategies compile unchanged after base migration"
    - "All 73 Interface strategies compile -- their 45 MessageBus usages are PRESERVED (not removed)"
    - "All 69 Encryption strategies compile (verified, no changes needed -- already SDK-base)"
    - "WasmLanguageStrategyBase (Compute sub-base) cascades correctly"
    - "9 DataProtection strategies with active MessageBus usage still function (MessageBus accessible via StrategyBase shim)"
    - "Zero behavioral regressions (AD-08)"
  artifacts:
    - path: "Plugins/DataWarehouse.Plugins.UltimateCompute/ComputeRuntimeStrategyBase.cs"
      provides: "Migrated Compute base, intelligence removed"
      contains: "class ComputeRuntimeStrategyBase"
    - path: "Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataProtectionStrategyBase.cs"
      provides: "Migrated DataProtection base, intelligence removed"
      contains: "class DataProtectionStrategyBase"
  key_links:
    - from: "ComputeRuntimeStrategyBase (plugin)"
      to: "PipelineComputeStrategyBase or StrategyBase (SDK)"
      via: "new inheritance replacing IComputeRuntimeStrategy direct implementation"
      pattern: "class ComputeRuntimeStrategyBase.*:.*StrategyBase"
    - from: "DataProtectionStrategyBase (plugin)"
      to: "StrategyBase (SDK)"
      via: "new inheritance replacing IDataProtectionStrategy direct implementation"
      pattern: "class DataProtectionStrategyBase.*:.*StrategyBase"
    - from: "Interface strategies (45 files)"
      to: "StrategyBase.MessageBus (legacy shim)"
      via: "backward-compat shim preserves MessageBus access"
      pattern: "MessageBus"
    - from: "DataProtection strategies (9 files)"
      to: "StrategyBase.MessageBus (legacy shim)"
      via: "backward-compat shim preserves MessageBus access"
      pattern: "MessageBus"
---

<objective>
Migrate the 2 plugin-local bases WITH intelligence boilerplate (Compute 85, DataProtection 82) and verify the 2 complex SDK-base domains (Interface 73, Encryption 69 = 309 strategies total).

Purpose: This is the HARDEST plan in Phase 25b. ComputeRuntimeStrategyBase and DataProtectionStrategyBase have real intelligence boilerplate that must be removed (ConfigureIntelligence, GetStrategyKnowledge, etc.). Interface has 45 strategy files that actively use MessageBus for event publishing -- this functionality MUST be preserved per AD-08 (the StrategyBase backward-compat shim from 25a-03 provides MessageBus access). DataProtection has 9 strategies with active MessageBus usage. Encryption was grossly miscounted (69 not 12) and has multi-class files averaging 3.6 classes/file.

Output: 2 plugin bases migrated with intelligence boilerplate removed, ~309 strategies verified, MessageBus-using strategies confirmed functional.
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
@.planning/phases/25b-strategy-migration/25b-RESEARCH.md (Section 2: ComputeRuntimeStrategyBase intelligence, Section 3: Type C pattern, Section 5: boilerplate inventory, Section 7: MessageBus handling)
@.planning/phases/25b-strategy-migration/25b-04-SUMMARY.md (plugin-local base migration pattern established)
@DataWarehouse.SDK/Contracts/StrategyBase.cs (has legacy MessageBus shim from 25a-03)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Migrate ComputeRuntimeStrategyBase (intelligence removal) and verify Encryption (221 strategies)</name>
  <files>
    Plugins/DataWarehouse.Plugins.UltimateCompute/ComputeRuntimeStrategyBase.cs
  </files>
  <action>
**Step 1: Read ComputeRuntimeStrategyBase thoroughly.**
```
grep -rn "class ComputeRuntimeStrategyBase" Plugins/DataWarehouse.Plugins.UltimateCompute/ --include="*.cs" -l
```
Read the full file. Per research, it contains ~60 lines of intelligence boilerplate:
- `ConfigureIntelligence(IMessageBus?)` (x2 overloads)
- `MessageBus` property
- `IsIntelligenceAvailable` property
- `GetStrategyKnowledge()` method
- `GetStrategyCapability()` method

**Step 2: Migrate ComputeRuntimeStrategyBase.**
Change the inheritance to use SDK hierarchy:
```csharp
// BEFORE:
internal abstract class ComputeRuntimeStrategyBase : IComputeRuntimeStrategy

// AFTER:
internal abstract class ComputeRuntimeStrategyBase : StrategyBase, IComputeRuntimeStrategy
// OR if SDK has a matching base:
internal abstract class ComputeRuntimeStrategyBase : PipelineComputeStrategyBase, IComputeRuntimeStrategy
```

Check if SDK has `PipelineComputeStrategyBase`:
```
grep -rn "class PipelineComputeStrategyBase" DataWarehouse.SDK/ --include="*.cs" -l
```
If it exists and provides the right domain contract, inherit from it.

**Step 3: Remove intelligence boilerplate from ComputeRuntimeStrategyBase.**
Remove these members (they are now provided by StrategyBase's backward-compat shim or are no longer needed):
- `ConfigureIntelligence(IMessageBus? messageBus)` -- StrategyBase shim provides this
- `GetStrategyKnowledge()` -- StrategyBase shim provides this
- `GetStrategyCapability()` -- StrategyBase shim provides this
- `MessageBus` property -- StrategyBase shim provides this
- `IsIntelligenceAvailable` property -- StrategyBase shim provides this

PRESERVE all domain-specific members (ComputeCoreAsync, etc.) and any non-intelligence code.

**Step 4: Handle property/method conflicts.**
If ComputeRuntimeStrategyBase has its own `StrategyId`, `Name`, `Description`, `Dispose` that conflict with StrategyBase, resolve them:
- Abstract properties matching StrategyBase -> make them `abstract override`
- Custom Dispose -> chain to `base.Dispose(disposing)`

**Step 5: Check WasmLanguageStrategyBase sub-base cascade.**
```
grep -rn "class WasmLanguageStrategyBase" Plugins/DataWarehouse.Plugins.UltimateCompute/ --include="*.cs"
```
Verify it inherits from ComputeRuntimeStrategyBase. After the migration, it cascades to StrategyBase automatically.

**Step 6: Build Compute plugin.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateCompute/DataWarehouse.Plugins.UltimateCompute.csproj
```
Fix any compilation errors in concrete strategies. Common issues:
- Strategies that `override ConfigureIntelligence` -- the override target is now on StrategyBase (shim), should still compile
- Strategies that access `base.MessageBus` -- StrategyBase shim provides this

**Step 7: Verify Compute strategy count.**
```
grep -rn "class.*:.*ComputeRuntimeStrategyBase\|class.*:.*WasmLanguageStrategyBase" Plugins/DataWarehouse.Plugins.UltimateCompute/ --include="*.cs" | wc -l
```
Expected: 85 concrete strategies.

**Step 8: Verify Encryption (no changes needed, but verify count accuracy).**
Encryption was miscounted in ROADMAP (12 vs actual 69) with multi-class files (3.6 classes/file in 19 files). Build and verify:
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateEncryption/DataWarehouse.Plugins.UltimateEncryption.csproj
```
Count strategies:
```
grep -rn "class.*:.*EncryptionStrategyBase" Plugins/DataWarehouse.Plugins.UltimateEncryption/ --include="*.cs"
```
Expected: 69 classes across 19 files. Count classes, not files.

Verify zero intelligence boilerplate in concrete strategies:
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateEncryption/ --include="*.cs"
```

**Step 9: Verify intelligence boilerplate fully removed from ComputeRuntimeStrategyBase.**
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability\|IsIntelligenceAvailable" Plugins/DataWarehouse.Plugins.UltimateCompute/ComputeRuntimeStrategyBase.cs
```
Expected: zero matches (all removed).
  </action>
  <verify>
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateCompute/DataWarehouse.Plugins.UltimateCompute.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateEncryption/DataWarehouse.Plugins.UltimateEncryption.csproj` exits 0
- ComputeRuntimeStrategyBase has zero intelligence boilerplate (grep returns 0)
- ComputeRuntimeStrategyBase inherits from SDK hierarchy (grep confirms)
- WasmLanguageStrategyBase cascade verified
- 85 Compute + 69 Encryption strategies compile
  </verify>
  <done>ComputeRuntimeStrategyBase migrated to SDK hierarchy, ~60 lines of intelligence boilerplate removed. All 85 Compute strategies compile. WasmLanguageStrategyBase cascades correctly. 69 Encryption strategies verified (3.6 classes/file pattern confirmed). Zero behavioral regressions.</done>
</task>

<task type="auto">
  <name>Task 2: Migrate DataProtectionStrategyBase (intelligence removal) and verify Interface (155 strategies with MessageBus)</name>
  <files>
    Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataProtectionStrategyBase.cs
  </files>
  <action>
**Step 1: Read DataProtectionStrategyBase thoroughly.**
```
grep -rn "class DataProtectionStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataProtection/ --include="*.cs" -l
```
Read the full file. Per research, it contains ~30 lines of intelligence boilerplate plus 26 intelligence helper references throughout the base.

**Step 2: Migrate DataProtectionStrategyBase.**
Change inheritance to SDK hierarchy:
```csharp
// BEFORE:
internal abstract class DataProtectionStrategyBase : IDataProtectionStrategy

// AFTER:
internal abstract class DataProtectionStrategyBase : StrategyBase, IDataProtectionStrategy
```
(or appropriate SDK domain base if one exists)

**Step 3: Remove intelligence boilerplate from DataProtectionStrategyBase.**
Remove:
- `ConfigureIntelligence(IMessageBus? messageBus)` -- StrategyBase shim provides this
- `MessageBus` property -- StrategyBase shim provides this
- `IsIntelligenceAvailable` property -- StrategyBase shim provides this

CRITICAL: The research found 26 intelligence helper REFERENCES throughout the base class. These are likely in domain-specific helper methods that USE MessageBus/IsIntelligenceAvailable. Analyze each one:
- If it's a helper method that publishes to MessageBus -> PRESERVE, but ensure it accesses MessageBus via the StrategyBase shim (which will return null, making the IsIntelligenceAvailable check return false, which means the publish is safely skipped)
- If it's an assignment to MessageBus -> REMOVE (StrategyBase handles this)
- If it's a null check on MessageBus -> PRESERVE (IsIntelligenceAvailable returns false via shim, which safely skips the block)

The key insight: after migration, the StrategyBase shim provides `MessageBus` (always null) and `IsIntelligenceAvailable` (always false). Any code guarded by `if (IsIntelligenceAvailable)` will safely no-op. This preserves behavioral equivalence per AD-08 while removing the intelligence BOILERPLATE.

**Step 4: Handle the 9 concrete strategies with active MessageBus usage.**
```
grep -rn "MessageBus" Plugins/DataWarehouse.Plugins.UltimateDataProtection/ --include="*.cs" -l
```
Research found 9 strategy files use MessageBus. These strategies access MessageBus for real functionality (AI predictions, backup events).

DO NOT remove their MessageBus usage. They access `MessageBus` which is now provided by the StrategyBase shim (returns null). Their code pattern is:
```csharp
if (IsIntelligenceAvailable)  // false via shim -> safely skips
{
    await MessageBus!.PublishAsync(...);
}
```
This safely degrades. The strategies still compile and function (without intelligence features). Phase 27 will properly reconnect intelligence at the plugin level.

Verify each of the 9 strategy files still compiles. If any access MessageBus WITHOUT a null/IsIntelligenceAvailable guard, add a null check to prevent NullReferenceException.

**Step 5: Handle the 1 ConfigureIntelligence override (research found 1 in DataProtection).**
```
grep -rn "override.*ConfigureIntelligence" Plugins/DataWarehouse.Plugins.UltimateDataProtection/ --include="*.cs"
```
This override exists on the StrategyBase shim, so it should still compile. If it does custom work beyond `base.ConfigureIntelligence(messageBus)`, preserve that logic. If it ONLY calls base -> it's safe to remove the override.

**Step 6: Build DataProtection plugin.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataWarehouse.Plugins.UltimateDataProtection.csproj
```
Fix compilation errors. Verify all 82 strategies compile.

**Step 7: Verify Interface plugin (73 strategies, 45 use MessageBus -- verify-only).**
Build Interface:
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateInterface/DataWarehouse.Plugins.UltimateInterface.csproj
```

Interface strategies already inherit from SDK `InterfaceStrategyBase` (Type A). The concern is their active MessageBus usage (45 files). Verify:
```
grep -rn "MessageBus" Plugins/DataWarehouse.Plugins.UltimateInterface/ --include="*.cs" | wc -l
```
Expected: ~45 files.

These strategies access MessageBus via the StrategyBase shim. Verify they compile and that their MessageBus access is guarded by null checks or IsIntelligenceAvailable. Spot-check 3-4 files to confirm the pattern.

Count strategies:
```
grep -rn "class.*:.*InterfaceStrategyBase" Plugins/DataWarehouse.Plugins.UltimateInterface/ --include="*.cs" | wc -l
```
Expected: 73 classes.

**Step 8: Full solution build.**
```
dotnet build DataWarehouse.slnx
```
Confirm zero new errors beyond pre-existing.

**Step 9: Verify intelligence boilerplate removed from DataProtectionStrategyBase.**
```
grep -rn "void ConfigureIntelligence\|KnowledgeObject GetStrategy\|RegisteredCapability GetStrategy" Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataProtectionStrategyBase.cs
```
Expected: zero matches for the intelligence interface methods (domain helpers that USE MessageBus may still reference it, which is OK).
  </action>
  <verify>
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateDataProtection/DataWarehouse.Plugins.UltimateDataProtection.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateInterface/DataWarehouse.Plugins.UltimateInterface.csproj` exits 0
- DataProtectionStrategyBase intelligence boilerplate removed
- 9 DataProtection strategies with MessageBus usage still compile (guarded by null/IsIntelligenceAvailable checks)
- 45 Interface strategies with MessageBus usage still compile (same pattern)
- 82 DataProtection + 73 Interface = 155 strategies verified
- Full solution builds with zero new errors
  </verify>
  <done>DataProtectionStrategyBase migrated to SDK hierarchy, intelligence boilerplate removed. 9 DataProtection strategies and 45 Interface strategies with MessageBus usage confirmed safe (null-guarded, graceful degradation). All 309 strategies (85 Compute + 69 Encryption + 82 DataProtection + 73 Interface) verified. Zero behavioral regressions (AD-08).</done>
</task>

</tasks>

<verification>
1. ComputeRuntimeStrategyBase: intelligence boilerplate removed, inherits SDK hierarchy
2. DataProtectionStrategyBase: intelligence boilerplate removed, inherits SDK hierarchy
3. All 85 Compute strategies compile (WasmLanguageStrategyBase cascade OK)
4. All 69 Encryption strategies compile (multi-class files verified)
5. All 82 DataProtection strategies compile (9 MessageBus users safe)
6. All 73 Interface strategies compile (45 MessageBus users safe)
7. Active MessageBus usage PRESERVED in all 55 strategy files (AD-08)
8. MessageBus access safely degrades via StrategyBase shim (IsIntelligenceAvailable=false)
9. Full solution build passes with zero new errors
10. The 1 ConfigureIntelligence override handled correctly
</verification>

<success_criteria>
- 2 plugin bases (Compute, DataProtection) migrated with intelligence boilerplate removed
- 2 SDK-base domains (Encryption, Interface) verified
- ~309 strategies compile with zero errors
- MessageBus functionality preserved in all 55 strategy files
- Intelligence boilerplate removed ONLY from plugin base classes, NOT from concrete strategies
- Full solution builds
</success_criteria>

<output>
After completion, create `.planning/phases/25b-strategy-migration/25b-05-SUMMARY.md`
</output>
