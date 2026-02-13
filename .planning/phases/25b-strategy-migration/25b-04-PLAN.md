---
phase: 25b-strategy-migration
plan: 04
type: execute
wave: 2
depends_on: ["25b-01", "25b-02", "25b-03"]
files_modified:
  - Plugins/DataWarehouse.Plugins.UltimateAccessControl/AccessControlStrategyBase.cs
  - Plugins/DataWarehouse.Plugins.UltimateCompliance/ComplianceStrategyBase.cs
  - Plugins/DataWarehouse.Plugins.UltimateDataManagement/DataManagementStrategyBase.cs
  - Plugins/DataWarehouse.Plugins.UltimateStreamingData/StreamingDataStrategyBase.cs
autonomous: true

must_haves:
  truths:
    - "AccessControlStrategyBase inherits from SDK SecurityStrategyBase (or StrategyBase + IAccessControlStrategy) instead of directly implementing IAccessControlStrategy"
    - "Compliance plugin's ComplianceStrategyBase inherits from SDK ComplianceStrategyBase without name collision issues"
    - "DataManagementStrategyBase inherits from SDK base, all 10 sub-bases cascade correctly"
    - "StreamingDataStrategyBase inherits from SDK StreamingStrategyBase instead of directly implementing IStreamingDataStrategy"
    - "All 146 AccessControl strategies compile unchanged after base migration"
    - "All 149 Compliance strategies compile unchanged after base migration"
    - "All 101 DataManagement strategies compile unchanged after base migration"
    - "All 58 Streaming strategies compile unchanged after base migration"
    - "Zero intelligence boilerplate was present in these 4 bases -- no removal needed"
    - "Zero behavioral regressions -- all strategies produce identical results (AD-08)"
  artifacts:
    - path: "Plugins/DataWarehouse.Plugins.UltimateAccessControl/AccessControlStrategyBase.cs"
      provides: "Migrated AccessControl base inheriting SDK hierarchy"
      contains: "class AccessControlStrategyBase"
    - path: "Plugins/DataWarehouse.Plugins.UltimateCompliance/ComplianceStrategyBase.cs"
      provides: "Migrated Compliance plugin base with name collision resolved"
      contains: "class ComplianceStrategyBase"
    - path: "Plugins/DataWarehouse.Plugins.UltimateDataManagement/DataManagementStrategyBase.cs"
      provides: "Migrated DataManagement base with 10 sub-bases cascading"
      contains: "class DataManagementStrategyBase"
    - path: "Plugins/DataWarehouse.Plugins.UltimateStreamingData/StreamingDataStrategyBase.cs"
      provides: "Migrated Streaming base inheriting SDK hierarchy"
      contains: "class StreamingDataStrategyBase"
  key_links:
    - from: "AccessControlStrategyBase (plugin)"
      to: "StrategyBase (SDK)"
      via: "new inheritance replacing IAccessControlStrategy direct implementation"
      pattern: "class AccessControlStrategyBase.*:.*StrategyBase"
    - from: "ComplianceStrategyBase (plugin)"
      to: "ComplianceStrategyBase (SDK) or StrategyBase"
      via: "new inheritance with name collision resolution"
      pattern: "class ComplianceStrategyBase.*:.*StrategyBase"
    - from: "DataManagement 10 sub-bases"
      to: "DataManagementStrategyBase (plugin)"
      via: "cascade inheritance"
      pattern: "class.*StrategyBase.*:.*DataManagementStrategyBase"
---

<objective>
Migrate 4 plugin-local strategy bases that have NO intelligence boilerplate (AccessControl 146, Compliance 149, DataManagement 101, Streaming 58 = 454 strategies total) to inherit from the SDK hierarchy.

Purpose: These are Type B migrations -- the plugin-local bases currently implement domain interfaces directly (e.g., `AccessControlStrategyBase : IAccessControlStrategy`) instead of inheriting from SDK bases. They need to be changed to inherit from the appropriate SDK domain base (or StrategyBase + interface) so all strategies are unified under StrategyBase. Research confirmed these 4 bases have ZERO intelligence code, making them lower risk than the bases in 25b-05.

CRITICAL RISK: Compliance plugin has its OWN `ComplianceStrategyBase` which collides with the SDK's `ComplianceStrategyBase`. This name collision must be resolved carefully.

Output: 4 plugin-local bases migrated, all ~454 concrete strategies compile unchanged, name collision resolved.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05, AD-08)
@.planning/phases/25b-strategy-migration/25b-RESEARCH.md (Section 2: plugin-local bases, Section 4: Compliance name collision, DataManagement sub-bases)
@.planning/phases/25b-strategy-migration/25b-01-SUMMARY.md (verification results)
@.planning/phases/25b-strategy-migration/25b-02-SUMMARY.md (verification results)
@.planning/phases/25b-strategy-migration/25b-03-SUMMARY.md (verification results)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Migrate AccessControl and Streaming plugin bases (204 strategies)</name>
  <files>
    Plugins/DataWarehouse.Plugins.UltimateAccessControl/AccessControlStrategyBase.cs
    Plugins/DataWarehouse.Plugins.UltimateStreamingData/StreamingDataStrategyBase.cs
  </files>
  <action>
**Step 1: Read and understand AccessControlStrategyBase.**
Read `Plugins/DataWarehouse.Plugins.UltimateAccessControl/AccessControlStrategyBase.cs` (or find it via grep if path differs):
```
grep -rn "class AccessControlStrategyBase" Plugins/DataWarehouse.Plugins.UltimateAccessControl/ --include="*.cs" -l
```

Current state (per research): `AccessControlStrategyBase : IAccessControlStrategy` -- directly implements interface, no SDK base, no intelligence code.

**Step 2: Determine the correct SDK base for AccessControl.**
Check if an SDK AccessControl domain base exists:
```
grep -rn "class.*AccessControl.*StrategyBase\|class.*SecurityStrategyBase" DataWarehouse.SDK/ --include="*.cs"
```
The SDK has `SecurityStrategyBase` in Contracts/Security/. AccessControl might use this or might need `StrategyBase` directly.

Determine the correct approach:
- If an SDK `AccessControlStrategyBase` or relevant domain base exists -> inherit from it, keep `IAccessControlStrategy` as additional interface
- If no SDK domain base matches -> change to `StrategyBase, IAccessControlStrategy` (inherit StrategyBase, implement interface)

**Step 3: Migrate AccessControlStrategyBase.**
Change the class declaration from:
```csharp
internal abstract class AccessControlStrategyBase : IAccessControlStrategy
```
to:
```csharp
internal abstract class AccessControlStrategyBase : StrategyBase, IAccessControlStrategy
```
(or the appropriate SDK domain base if one exists)

CRITICAL CHECKS:
- If the plugin base has its own `StrategyId`, `Name`, `Description` abstract/virtual properties that overlap with StrategyBase, resolve conflicts. The plugin base's properties should become `override` if they match StrategyBase's, or remain as-is if they're different properties.
- If the plugin base has its own `Dispose(bool)` pattern, it must chain to `base.Dispose(disposing)` from StrategyBase.
- Verify all abstract members expected by concrete strategies are still present.
- Add `using DataWarehouse.SDK.Contracts;` if not already present.

**Step 4: Build AccessControl plugin and fix any compilation issues.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateAccessControl/DataWarehouse.Plugins.UltimateAccessControl.csproj
```
Fix any compilation errors in concrete strategies caused by the base change. Common issues:
- Property name conflicts (strategy has `Name` that now conflicts with StrategyBase.Name)
- Missing `override` keyword on properties that are now defined in StrategyBase
- Constructor parameter changes if StrategyBase has a constructor

**Step 5: Verify AccessControl strategy count still matches.**
```
grep -rn "class.*:.*AccessControlStrategyBase" Plugins/DataWarehouse.Plugins.UltimateAccessControl/ --include="*.cs" | wc -l
```
Expected: 146 concrete strategies, all still compiling.

**Step 6: Repeat Steps 1-5 for StreamingDataStrategyBase.**
Read `Plugins/DataWarehouse.Plugins.UltimateStreamingData/StreamingDataStrategyBase.cs`:
```
grep -rn "class StreamingDataStrategyBase" Plugins/DataWarehouse.Plugins.UltimateStreamingData/ --include="*.cs" -l
```

Current state: `StreamingDataStrategyBase : IStreamingDataStrategy` -- no intelligence code.

Check if SDK has a matching base:
```
grep -rn "class.*StreamingStrategyBase" DataWarehouse.SDK/ --include="*.cs"
```
The SDK has `StreamingStrategyBase` in Contracts/Streaming/. Determine if `StreamingDataStrategyBase` should inherit from `StreamingStrategyBase` or `StrategyBase, IStreamingDataStrategy`.

Migrate similarly. Build and verify:
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateStreamingData/DataWarehouse.Plugins.UltimateStreamingData.csproj
```
Verify 58 strategies still compile.
  </action>
  <verify>
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateAccessControl/DataWarehouse.Plugins.UltimateAccessControl.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateStreamingData/DataWarehouse.Plugins.UltimateStreamingData.csproj` exits 0
- `grep -c "class.*:.*AccessControlStrategyBase" Plugins/DataWarehouse.Plugins.UltimateAccessControl/` = 146
- `grep -c "class.*:.*StreamingDataStrategyBase" Plugins/DataWarehouse.Plugins.UltimateStreamingData/` = 58
  </verify>
  <done>AccessControlStrategyBase and StreamingDataStrategyBase now inherit from SDK hierarchy. All 204 concrete strategies (146 + 58) compile without errors. Zero behavioral changes (AD-08).</done>
</task>

<task type="auto">
  <name>Task 2: Migrate Compliance (name collision) and DataManagement (10 sub-bases) plugin bases (250 strategies)</name>
  <files>
    Plugins/DataWarehouse.Plugins.UltimateCompliance/ComplianceStrategyBase.cs
    Plugins/DataWarehouse.Plugins.UltimateDataManagement/DataManagementStrategyBase.cs
  </files>
  <action>
**Step 1: Handle the Compliance name collision (HIGHEST RISK in this plan).**

Read the plugin's ComplianceStrategyBase:
```
grep -rn "class ComplianceStrategyBase" Plugins/DataWarehouse.Plugins.UltimateCompliance/ --include="*.cs" -l
```
Read the file.

Read the SDK's ComplianceStrategyBase:
```
grep -rn "class ComplianceStrategyBase" DataWarehouse.SDK/ --include="*.cs" -l
```
Read the file.

**The problem:** Both the plugin and SDK have a class called `ComplianceStrategyBase`. When the plugin base tries to inherit from the SDK base, `ComplianceStrategyBase : ComplianceStrategyBase` is a circular reference.

**Resolution options (choose the simplest that works):**

Option A: Use fully-qualified name in inheritance:
```csharp
internal abstract class ComplianceStrategyBase : DataWarehouse.SDK.Contracts.Compliance.ComplianceStrategyBase, IComplianceStrategy
```

Option B: Add a using alias:
```csharp
using SdkComplianceStrategyBase = DataWarehouse.SDK.Contracts.Compliance.ComplianceStrategyBase;
// ...
internal abstract class ComplianceStrategyBase : SdkComplianceStrategyBase, IComplianceStrategy
```

Option C: Rename the plugin base to `UltimateComplianceStrategyBase`. This would require updating all 149 concrete strategies to change their base class name. AVOID unless Options A/B fail.

**Choose Option B** (using alias) -- it's clean, requires no changes to concrete strategies, and makes the intent explicit.

**Step 2: Migrate Compliance plugin base.**
Add the using alias at the top of the file. Change the inheritance. Verify the SDK ComplianceStrategyBase provides the properties/methods that concrete strategies expect from the plugin base. Any plugin-specific abstract methods must remain.

Handle property conflicts between plugin base and SDK base (StrategyId, Name, etc.) the same way as Task 1.

Build and verify:
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateCompliance/DataWarehouse.Plugins.UltimateCompliance.csproj
```
Verify 149 strategies still compile:
```
grep -rn "class.*:.*ComplianceStrategyBase" Plugins/DataWarehouse.Plugins.UltimateCompliance/ --include="*.cs" | wc -l
```

**Step 3: Read and understand DataManagementStrategyBase and its 10 sub-bases.**
```
grep -rn "abstract class.*StrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataManagement/ --include="*.cs"
```
Map the full hierarchy:
- DataManagementStrategyBase (root plugin base)
  - BranchingStrategyBase
  - CachingStrategyBase
  - (8 more sub-bases)

Current state: `DataManagementStrategyBase : IDataManagementStrategy` or similar -- no intelligence code, but 10 sub-bases cascade from it.

**Step 4: Migrate DataManagementStrategyBase (root only).**
Change the root plugin base to inherit from the SDK hierarchy:
```csharp
internal abstract class DataManagementStrategyBase : StrategyBase, IDataManagementStrategy
```
(or appropriate SDK domain base if one exists -- check first)

The 10 sub-bases already inherit from DataManagementStrategyBase, so they automatically cascade to StrategyBase via the root change. Do NOT modify the sub-bases unless compilation fails.

**Step 5: Build DataManagement and fix cascade issues.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDataManagement/DataWarehouse.Plugins.UltimateDataManagement.csproj
```

If sub-bases have property conflicts with StrategyBase (StrategyId, Name, Description, Dispose), fix them in the sub-bases as needed. Common pattern: add `override` keyword to properties that now exist in StrategyBase.

**Step 6: Verify DataManagement strategy count.**
```
grep -rn "class.*:.*StrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataManagement/ --include="*.cs" | wc -l
```
Expected: 101+ classes (including 10 sub-bases and 91+ concrete strategies).

Also specifically verify each sub-base still has its concrete strategies:
```
grep -rn "class.*:.*BranchingStrategyBase\|class.*:.*CachingStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataManagement/ --include="*.cs"
```

**Step 7: Full solution build check.**
```
dotnet build DataWarehouse.slnx
```
Confirm zero NEW errors beyond pre-existing ones (UltimateCompression/AedsCore).
  </action>
  <verify>
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateCompliance/DataWarehouse.Plugins.UltimateCompliance.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateDataManagement/DataWarehouse.Plugins.UltimateDataManagement.csproj` exits 0
- `dotnet build DataWarehouse.slnx` -- zero new errors
- Compliance name collision resolved via using alias (no concrete strategy files changed)
- DataManagement 10 sub-bases cascade correctly through migrated root base
- All 250 strategies compile (149 Compliance + 101 DataManagement)
  </verify>
  <done>Compliance base migrated with name collision resolved (using alias pattern). DataManagement base migrated with all 10 sub-bases cascading correctly. All 250 concrete strategies (149 + 101) compile. Full solution builds. Zero behavioral regressions.</done>
</task>

</tasks>

<verification>
1. All 4 plugin-local bases now inherit from SDK hierarchy (StrategyBase or SDK domain base)
2. Zero intelligence code was removed (none existed in these 4 bases per research)
3. Compliance name collision resolved without renaming (using alias or fully-qualified name)
4. DataManagement 10 sub-bases cascade correctly through migrated root
5. All 454 concrete strategies compile unchanged (146 + 149 + 101 + 58)
6. Full solution build passes with zero new errors
7. Zero behavioral regressions (AD-08) -- only base class declarations changed
</verification>

<success_criteria>
- 4 plugin bases migrated to SDK hierarchy
- ~454 strategies compile with zero errors
- Compliance name collision cleanly resolved
- DataManagement sub-base cascade verified
- Full solution builds
- Only base class files modified, not concrete strategy files
</success_criteria>

<output>
After completion, create `.planning/phases/25b-strategy-migration/25b-04-SUMMARY.md`
</output>
