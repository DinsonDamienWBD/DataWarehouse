---
phase: 25b-strategy-migration
plan: 02
type: execute
wave: 1
depends_on: []
files_modified: []
autonomous: true

must_haves:
  truths:
    - "All 55 Observability strategies compile against new StrategyBase hierarchy"
    - "All 56 DataLake strategies compile against new StrategyBase hierarchy"
    - "All 56 DataMesh strategies compile against new StrategyBase hierarchy"
    - "All 59 Compression strategies compile against new StrategyBase hierarchy"
    - "All 61 Replication strategies compile (including EnhancedReplicationStrategyBase intermediate)"
    - "Custom Dispose logic in Compression strategies is preserved (AD-08)"
    - "Multi-class files in Replication (4.1 classes/file avg) all verified"
  artifacts:
    - path: "Plugins/DataWarehouse.Plugins.UniversalObservability/"
      provides: "55 Observability strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateDataLake/"
      provides: "56 DataLake strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateDataMesh/"
      provides: "56 DataMesh strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateCompression/"
      provides: "59 Compression strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateReplication/"
      provides: "61 Replication strategies verified"
  key_links:
    - from: "Replication concrete strategies"
      to: "EnhancedReplicationStrategyBase"
      via: "intermediate base cascade"
      pattern: "class.*:.*EnhancedReplicationStrategyBase|class.*:.*ReplicationStrategyBase"
    - from: "Compression strategies with Dispose"
      to: "StrategyBase.Dispose(bool)"
      via: "override chain"
      pattern: "override.*Dispose.*bool"
---

<objective>
Verify that 5 medium SDK-base domains (Observability 55, DataLake 56, DataMesh 56, Compression 59, Replication 61 = 287 strategies total) compile correctly against the new StrategyBase hierarchy.

Purpose: Type A strategies that need no code changes. Key risks: Compression has many strategies with custom Dispose(bool) logic that must be preserved (AD-08). Replication has EnhancedReplicationStrategyBase as an intermediate base that must cascade correctly. Multi-class files in Replication (4.1 classes/file) need per-class verification.

Output: Verified compilation of 5 plugins, custom Dispose preservation confirmed, intermediate base cascade verified, strategy counts documented.
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
@.planning/phases/25b-strategy-migration/25b-RESEARCH.md (Section 1: verified counts, Section 4: Compression Dispose risk, Replication intermediate)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Verify Observability, DataLake, and DataMesh plugins (167 strategies)</name>
  <files></files>
  <action>
**VERIFICATION task -- no strategy files should be modified.**

**Step 1: Build and audit Observability.**
```
dotnet build Plugins/DataWarehouse.Plugins.UniversalObservability/DataWarehouse.Plugins.UniversalObservability.csproj
```
Count strategies: `grep -rn "class.*:.*ObservabilityStrategyBase" Plugins/DataWarehouse.Plugins.UniversalObservability/ --include="*.cs"`
Expected: 55 classes.

Verify zero intelligence boilerplate:
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UniversalObservability/ --include="*.cs"
```

Note: ObservabilityStrategyBase implements IDisposable with SemaphoreSlim. Verify that concrete strategies' Dispose overrides still work:
```
grep -rn "override.*Dispose" Plugins/DataWarehouse.Plugins.UniversalObservability/ --include="*.cs"
```

**Step 2: Build and audit DataLake.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDataLake/DataWarehouse.Plugins.UltimateDataLake.csproj
```
Count strategies: `grep -rn "class.*:.*DataLakeStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataLake/ --include="*.cs"`
Expected: 56 classes.

Verify zero intelligence boilerplate (same grep pattern).

**Step 3: Build and audit DataMesh.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDataMesh/DataWarehouse.Plugins.UltimateDataMesh.csproj
```
Count strategies: `grep -rn "class.*:.*DataMeshStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDataMesh/ --include="*.cs"`
Expected: 56 classes.

Verify zero intelligence boilerplate (same grep pattern).
  </action>
  <verify>
All 3 plugins compile with zero errors. Strategy counts match expected values. Zero intelligence boilerplate in concrete strategies.
  </verify>
  <done>Observability (55), DataLake (56), DataMesh (56) = 167 strategies verified. All compile cleanly. Zero code changes.</done>
</task>

<task type="auto">
  <name>Task 2: Verify Compression and Replication plugins with special attention to Dispose and intermediate bases (120 strategies)</name>
  <files></files>
  <action>
**VERIFICATION task with extra care for AD-08 risks.**

**Step 1: Build Compression plugin.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateCompression/DataWarehouse.Plugins.UltimateCompression.csproj
```
NOTE: UltimateCompression has pre-existing CS1729 errors documented in STATE.md. These are NOT caused by Phase 25b. Document whether these are the ONLY errors or if new errors appear.

**Step 2: Audit Compression strategy count.**
```
grep -rn "class.*:.*CompressionStrategyBase" Plugins/DataWarehouse.Plugins.UltimateCompression/ --include="*.cs"
```
Expected: 59 classes.

**Step 3: CRITICAL -- Verify custom Dispose logic preserved in Compression.**
Many Compression strategies have custom `Dispose(bool)` that releases real resources. This logic MUST be preserved per AD-08.
```
grep -rn "override.*void.*Dispose.*bool" Plugins/DataWarehouse.Plugins.UltimateCompression/ --include="*.cs"
```
Record count of Dispose overrides. Verify the override chain: ConcreteStrategy.Dispose(bool) -> CompressionStrategyBase.Dispose(bool) -> StrategyBase.Dispose(bool). Each level must call `base.Dispose(disposing)`.

Spot-check 2-3 Compression strategy Dispose methods to confirm they still compile and preserve their resource cleanup logic. Read the file and verify the pattern is intact.

**Step 4: Build Replication plugin.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateReplication/DataWarehouse.Plugins.UltimateReplication.csproj
```
Must compile with zero errors.

**Step 5: Audit Replication strategy count -- handle multi-class files.**
```
grep -rn "class.*:.*ReplicationStrategyBase\|class.*:.*EnhancedReplicationStrategyBase" Plugins/DataWarehouse.Plugins.UltimateReplication/ --include="*.cs"
```
Expected: 61 classes across 15 files (avg 4.1 classes/file). The class count matters more than file count.

**Step 6: Verify EnhancedReplicationStrategyBase intermediate cascade.**
```
grep -rn "class EnhancedReplicationStrategyBase" Plugins/DataWarehouse.Plugins.UltimateReplication/ --include="*.cs"
```
Confirm it inherits from `ReplicationStrategyBase` (SDK base). Read the file to verify the inheritance chain: ConcreteStrategy -> EnhancedReplicationStrategyBase -> ReplicationStrategyBase -> StrategyBase.

**Step 7: Verify zero intelligence boilerplate in both plugins.**
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateCompression/ --include="*.cs"
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateReplication/ --include="*.cs"
```

**Step 8: Summary table for all 5 domains.**
| Domain | Expected | Actual | Files | Multi-class | Build | Dispose Overrides | Intelligence |
|--------|----------|--------|-------|-------------|-------|-------------------|-------------|
| Observability | 55 | ? | ? | No | ? | ? | ? |
| DataLake | 56 | ? | ? | No | ? | 0 | ? |
| DataMesh | 56 | ? | ? | No | ? | 0 | ? |
| Compression | 59 | ? | ? | No | ? | ? | ? |
| Replication | 61 | ? | 15 | Yes (4.1/file) | ? | ? | ? |
| **Total** | **287** | **?** | | | | | |
  </action>
  <verify>
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateCompression/DataWarehouse.Plugins.UltimateCompression.csproj` -- only pre-existing errors, zero new errors
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateReplication/DataWarehouse.Plugins.UltimateReplication.csproj` exits 0
- Compression Dispose overrides count matches previous audit
- EnhancedReplicationStrategyBase inherits ReplicationStrategyBase (verified by grep)
- Zero intelligence boilerplate in concrete strategies
  </verify>
  <done>Compression (59 strategies, custom Dispose preserved) and Replication (61 strategies, EnhancedReplicationStrategyBase cascade verified) compile. All 5 domains (287 strategies total) verified. Multi-class files in Replication all accounted for. Zero code changes.</done>
</task>

</tasks>

<verification>
1. All 5 plugins build (Compression may have pre-existing errors only)
2. Strategy counts verified: Observability 55, DataLake 56, DataMesh 56, Compression 59, Replication 61
3. Compression custom Dispose(bool) overrides are intact (AD-08)
4. EnhancedReplicationStrategyBase intermediate inherits ReplicationStrategyBase correctly
5. Multi-class Replication files have all classes counted
6. Zero intelligence boilerplate in concrete strategy files
7. Zero code changes to any file
</verification>

<success_criteria>
- 5 plugins compile (Compression with pre-existing errors only)
- ~287 strategy classes verified via grep counts
- Custom Dispose logic in Compression confirmed preserved
- EnhancedReplicationStrategyBase cascade verified
- Zero files modified
</success_criteria>

<output>
After completion, create `.planning/phases/25b-strategy-migration/25b-02-SUMMARY.md`
</output>
