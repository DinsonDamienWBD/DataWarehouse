---
phase: 25b-strategy-migration
plan: 01
type: execute
wave: 1
depends_on: []
files_modified: []
autonomous: true

must_haves:
  truths:
    - "All 11 Transit strategies compile against new StrategyBase hierarchy"
    - "All 20 Media strategies compile against new StrategyBase hierarchy"
    - "All 28 DataFormat strategies compile against new StrategyBase hierarchy"
    - "All 43 StorageProcessing strategies compile against new StrategyBase hierarchy"
    - "Zero code changes needed in any strategy file -- these are Type A verification-only"
    - "Zero intelligence boilerplate exists in any of these 102 strategy classes"
  artifacts:
    - path: "Plugins/DataWarehouse.Plugins.UltimateDataTransit/"
      provides: "11 Transit strategies verified against hierarchy"
    - path: "Plugins/DataWarehouse.Plugins.Transcoding.Media/"
      provides: "20 Media strategies verified against hierarchy"
    - path: "Plugins/DataWarehouse.Plugins.UltimateDataFormat/"
      provides: "28 DataFormat strategies verified against hierarchy"
    - path: "Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/"
      provides: "43 StorageProcessing strategies verified against hierarchy"
  key_links:
    - from: "Transit concrete strategies"
      to: "DataWarehouse.SDK/Contracts/Transit/DataTransitStrategyBase.cs"
      via: "class inheritance"
      pattern: "class.*:.*DataTransitStrategyBase"
    - from: "Media concrete strategies"
      to: "DataWarehouse.SDK/Contracts/Media/MediaStrategyBase.cs"
      via: "class inheritance"
      pattern: "class.*:.*MediaStrategyBase"
    - from: "DataFormat concrete strategies"
      to: "DataWarehouse.SDK/Contracts/DataFormat/DataFormatStrategy.cs"
      via: "class inheritance"
      pattern: "class.*:.*DataFormatStrategyBase"
    - from: "StorageProcessing concrete strategies"
      to: "DataWarehouse.SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs"
      via: "class inheritance"
      pattern: "class.*:.*StorageProcessingStrategyBase"
---

<objective>
Verify that the 4 smallest SDK-base domains (Transit 11, Media 20, DataFormat 28, StorageProcessing 43 = 102 strategies total) compile correctly against the new StrategyBase hierarchy established by Phase 25a.

Purpose: These are Type A strategies -- they already inherit from SDK domain bases which now inherit from StrategyBase. No code changes are expected. This plan proves the hierarchy works on the simplest, cleanest domains before tackling harder ones.

Output: Verified compilation of 4 plugins, documented strategy counts per domain, grep confirmation of zero intelligence boilerplate in concrete strategies.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05: flat strategy hierarchy, AD-08: zero regression)
@.planning/REQUIREMENTS.md (STRAT-06, REGR-01, REGR-02)
@.planning/phases/25b-strategy-migration/25b-RESEARCH.md (Section 1: verified counts, Section 3: Type A pattern)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Verify and audit Transit and Media plugins (31 strategies)</name>
  <files></files>
  <action>
**This is a VERIFICATION task -- no strategy files should be modified.** If any strategy file needs changes, STOP and document the issue.

**Step 1: Build Transit plugin individually.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDataTransit/DataWarehouse.Plugins.UltimateDataTransit.csproj
```
Must compile with zero errors. Document any warnings.

**Step 2: Audit Transit strategy count.**
Count concrete strategy classes in UltimateDataTransit:
```
grep -rn "class.*:.*DataTransitStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataTransit/ --include="*.cs"
```
Expected: 11 classes. Record actual count.

**Step 3: Verify zero intelligence boilerplate in Transit strategies.**
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability\|IsIntelligenceAvailable" Plugins/DataWarehouse.Plugins.UltimateDataTransit/ --include="*.cs"
```
Expected: zero matches in concrete strategy files (may match in plugin base if exists). If matches found in strategy files, document but do NOT remove (this plan is verify-only).

**Step 4: Build Media plugin individually.**
```
dotnet build Plugins/DataWarehouse.Plugins.Transcoding.Media/DataWarehouse.Plugins.Transcoding.Media.csproj
```
Must compile with zero errors.

**Step 5: Audit Media strategy count.**
```
grep -rn "class.*:.*MediaStrategyBase" Plugins/DataWarehouse.Plugins.Transcoding.Media/ --include="*.cs"
```
Expected: 20 classes. Record actual count.

**Step 6: Verify zero intelligence boilerplate in Media strategies.**
Same grep pattern as Step 3 against Transcoding.Media plugin directory.

**Step 7: Document results.**
Record for SUMMARY:
- Transit: {actual_count} strategies verified, {build_result}, {intelligence_boilerplate_count}
- Media: {actual_count} strategies verified, {build_result}, {intelligence_boilerplate_count}
  </action>
  <verify>
Both plugins compile with zero errors:
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateDataTransit/DataWarehouse.Plugins.UltimateDataTransit.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.Transcoding.Media/DataWarehouse.Plugins.Transcoding.Media.csproj` exits 0
Grep for intelligence boilerplate in concrete strategy files returns zero matches.
  </verify>
  <done>Transit (11 strategies) and Media (20 strategies) compile cleanly against new hierarchy. Zero intelligence boilerplate found in concrete strategy files. Zero code changes needed.</done>
</task>

<task type="auto">
  <name>Task 2: Verify and audit DataFormat and StorageProcessing plugins (71 strategies)</name>
  <files></files>
  <action>
**This is a VERIFICATION task -- no strategy files should be modified.**

**Step 1: Build DataFormat plugin individually.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDataFormat/DataWarehouse.Plugins.UltimateDataFormat.csproj
```
Must compile with zero errors.

**Step 2: Audit DataFormat strategy count.**
```
grep -rn "class.*:.*DataFormatStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataFormat/ --include="*.cs"
```
Expected: 28 classes. Record actual count.

**Step 3: Verify zero intelligence boilerplate in DataFormat strategies.**
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability\|IsIntelligenceAvailable" Plugins/DataWarehouse.Plugins.UltimateDataFormat/ --include="*.cs"
```

**Step 4: Build StorageProcessing plugin individually.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/DataWarehouse.Plugins.UltimateStorageProcessing.csproj
```
Must compile with zero errors.

**Step 5: Audit StorageProcessing strategy count.**
```
grep -rn "class.*:.*StorageProcessingStrategyBase" Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/ --include="*.cs"
```
Expected: 43 classes. Record actual count.

**Step 6: Verify zero intelligence boilerplate in StorageProcessing strategies.**
Same grep pattern against StorageProcessing plugin directory.

**Step 7: Run full solution build as cross-check.**
```
dotnet build DataWarehouse.slnx
```
Document total error/warning counts. Pre-existing errors in UltimateCompression/AedsCore are expected and OK.

**Step 8: Create summary table of all 4 domains.**
| Domain | Expected | Actual | Build | Intelligence Boilerplate |
|--------|----------|--------|-------|-------------------------|
| Transit | 11 | ? | ? | ? |
| Media | 20 | ? | ? | ? |
| DataFormat | 28 | ? | ? | ? |
| StorageProcessing | 43 | ? | ? | ? |
| **Total** | **102** | **?** | | |
  </action>
  <verify>
Both plugins compile with zero errors:
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateDataFormat/DataWarehouse.Plugins.UltimateDataFormat.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateStorageProcessing/DataWarehouse.Plugins.UltimateStorageProcessing.csproj` exits 0
Full solution `dotnet build DataWarehouse.slnx` has zero NEW errors beyond pre-existing ones.
  </verify>
  <done>DataFormat (28 strategies) and StorageProcessing (43 strategies) compile cleanly. All 4 domains (102 strategies total) verified. Summary table documents actual counts and build results. Zero code changes needed in any strategy file.</done>
</task>

</tasks>

<verification>
1. All 4 plugins build individually with zero errors
2. Strategy counts verified via grep match expected values (Transit 11, Media 20, DataFormat 28, StorageProcessing 43)
3. Zero intelligence boilerplate (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability) found in concrete strategy files
4. Zero code changes made to any strategy file
5. Full solution build shows no NEW errors
</verification>

<success_criteria>
- 4 plugins (UltimateDataTransit, Transcoding.Media, UltimateDataFormat, UltimateStorageProcessing) compile with zero errors
- ~102 strategy classes verified via grep counts
- Zero intelligence boilerplate in concrete strategies
- Zero files modified (verify-only plan)
- Summary table with actual counts per domain
</success_criteria>

<output>
After completion, create `.planning/phases/25b-strategy-migration/25b-01-SUMMARY.md`
</output>
