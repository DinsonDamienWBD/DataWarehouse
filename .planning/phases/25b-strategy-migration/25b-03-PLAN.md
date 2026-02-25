---
phase: 25b-strategy-migration
plan: 03
type: execute
wave: 1
depends_on: []
files_modified: []
autonomous: true

must_haves:
  truths:
    - "All 69 KeyManagement strategies compile (including Pkcs11HsmStrategyBase intermediate)"
    - "All 47 RAID strategies compile (using SDK RaidStrategyBase via SdkRaidStrategyBase alias)"
    - "All 130 Storage strategies compile (including UltimateStorageStrategyBase intermediate)"
    - "All 49 DatabaseStorage strategies compile (DatabaseStorageStrategyBase extends StorageStrategyBase)"
    - "All 280 Connector strategies compile (10 sub-category bases extend ConnectionStrategyBase)"
    - "Intermediate base inheritance chains verified: all cascade correctly through SDK bases to StrategyBase"
    - "Zero code changes needed in any strategy file"
  artifacts:
    - path: "Plugins/DataWarehouse.Plugins.UltimateKeyManagement/"
      provides: "69 KeyManagement strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateRAID/"
      provides: "47 RAID strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateStorage/"
      provides: "130 Storage strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/"
      provides: "49 DatabaseStorage strategies verified"
    - path: "Plugins/DataWarehouse.Plugins.UltimateConnector/"
      provides: "280 Connector strategies verified"
  key_links:
    - from: "Pkcs11HsmStrategyBase"
      to: "KeyStoreStrategyBase"
      via: "intermediate inheritance"
      pattern: "class Pkcs11HsmStrategyBase.*:.*KeyStoreStrategyBase"
    - from: "UltimateStorageStrategyBase"
      to: "StorageStrategyBase"
      via: "intermediate inheritance"
      pattern: "class UltimateStorageStrategyBase.*:.*StorageStrategyBase"
    - from: "10 Connection sub-category bases"
      to: "ConnectionStrategyBase"
      via: "sub-category inheritance"
      pattern: "class.*ConnectionStrategyBase.*:.*ConnectionStrategyBase"
    - from: "DatabaseStorageStrategyBase"
      to: "StorageStrategyBase"
      via: "intermediate inheritance"
      pattern: "class DatabaseStorageStrategyBase.*:.*StorageStrategyBase"
---

<objective>
Verify that 5 large SDK-base domains with intermediate bases (KeyManagement 69, RAID 47, Storage 130, DatabaseStorage 49, Connector 280 = 575 strategies total) compile correctly against the new StrategyBase hierarchy.

Purpose: These are Type A strategies but with INTERMEDIATE BASE CLASSES between the concrete strategy and the SDK domain base. The intermediate bases (Pkcs11HsmStrategyBase, UltimateStorageStrategyBase, DatabaseStorageStrategyBase, 10 Connector sub-category bases) must cascade correctly through the SDK bases to StrategyBase. This plan verifies the full inheritance chains work for the largest strategy populations.

Output: Verified compilation of 5 plugins, all intermediate base cascade chains documented, strategy counts confirmed.
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
@.planning/phases/25b-strategy-migration/25b-RESEARCH.md (Section 1: verified counts, Section 4: intermediate bases, Connector sub-categories)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Verify KeyManagement, RAID, and DatabaseStorage plugins (165 strategies with intermediates)</name>
  <files></files>
  <action>
**VERIFICATION task -- no strategy files should be modified.**

**Step 1: Build and audit KeyManagement.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateKeyManagement/DataWarehouse.Plugins.UltimateKeyManagement.csproj
```

Count strategies:
```
grep -rn "class.*:.*KeyStoreStrategyBase\|class.*:.*Pkcs11HsmStrategyBase" Plugins/DataWarehouse.Plugins.UltimateKeyManagement/ --include="*.cs"
```
Expected: 69 classes total.

Verify Pkcs11HsmStrategyBase intermediate:
```
grep -rn "class Pkcs11HsmStrategyBase" Plugins/DataWarehouse.Plugins.UltimateKeyManagement/ --include="*.cs"
```
Read the file to confirm inheritance chain: Pkcs11HsmStrategyBase : KeyStoreStrategyBase : StrategyBase.

**Step 2: Build and audit RAID.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateRAID/DataWarehouse.Plugins.UltimateRAID.csproj
```

The research found RAID uses `SdkRaidStrategyBase` as an alias for the SDK's `RaidStrategyBase`. Verify:
```
grep -rn "SdkRaidStrategyBase\|using.*RaidStrategyBase" Plugins/DataWarehouse.Plugins.UltimateRAID/ --include="*.cs" | head -5
```

Count strategies:
```
grep -rn "class.*:.*RaidStrategyBase\|class.*:.*SdkRaidStrategyBase" Plugins/DataWarehouse.Plugins.UltimateRAID/ --include="*.cs"
```
Expected: 47 classes. Note: RAID has multi-class files (4+ classes each).

Also check: RAID has a PLUGIN-LOCAL `RaidStrategyBase` with 0 strategies using it (per research). Verify this plugin base exists but has no inheritors:
```
grep -rn "class RaidStrategyBase" Plugins/DataWarehouse.Plugins.UltimateRAID/ --include="*.cs"
```

**Step 3: Build and audit DatabaseStorage.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/DataWarehouse.Plugins.UltimateDatabaseStorage.csproj
```

Count strategies:
```
grep -rn "class.*:.*DatabaseStorageStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/ --include="*.cs"
```
Expected: 49 classes.

Verify DatabaseStorageStrategyBase intermediate:
```
grep -rn "class DatabaseStorageStrategyBase" Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/ --include="*.cs"
```
Read the file to confirm: DatabaseStorageStrategyBase : StorageStrategyBase : StrategyBase.

**Step 4: Verify zero intelligence boilerplate across all 3 plugins.**
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateKeyManagement/ --include="*.cs"
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateRAID/ --include="*.cs"
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/ --include="*.cs"
```
Expected: zero in concrete strategy files (may match in plugin-local bases -- document these).
  </action>
  <verify>
All 3 plugins compile with zero errors. Intermediate base chains verified:
- Pkcs11HsmStrategyBase : KeyStoreStrategyBase : StrategyBase
- SdkRaidStrategyBase alias correctly routes to SDK RaidStrategyBase : StrategyBase
- DatabaseStorageStrategyBase : StorageStrategyBase : StrategyBase
Strategy counts match expected values.
  </verify>
  <done>KeyManagement (69), RAID (47), DatabaseStorage (49) = 165 strategies verified. All intermediate bases cascade correctly to StrategyBase. Zero code changes.</done>
</task>

<task type="auto">
  <name>Task 2: Verify Storage and Connector plugins (410 strategies -- largest domains)</name>
  <files></files>
  <action>
**VERIFICATION task -- no strategy files should be modified.**

**Step 1: Build and audit Storage.**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateStorage/DataWarehouse.Plugins.UltimateStorage.csproj
```

Count strategies:
```
grep -rn "class.*:.*StorageStrategyBase\|class.*:.*UltimateStorageStrategyBase" Plugins/DataWarehouse.Plugins.UltimateStorage/ --include="*.cs"
```
Expected: 130 classes.

Verify UltimateStorageStrategyBase intermediate:
```
grep -rn "class UltimateStorageStrategyBase" Plugins/DataWarehouse.Plugins.UltimateStorage/ --include="*.cs"
```
Read the file to confirm: UltimateStorageStrategyBase : StorageStrategyBase : StrategyBase.

Verify zero intelligence boilerplate:
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateStorage/ --include="*.cs"
```

**Step 2: Build and audit Connector (the largest domain at 280 strategies).**
```
dotnet build Plugins/DataWarehouse.Plugins.UltimateConnector/DataWarehouse.Plugins.UltimateConnector.csproj
```

Count strategies -- Connector has 10 sub-category bases, each with its own concrete strategies:
```
grep -rn "class.*:.*ConnectionStrategyBase\|class.*:.*DatabaseConnectionStrategyBase\|class.*:.*MessageQueueConnectionStrategyBase\|class.*:.*CacheConnectionStrategyBase\|class.*:.*FileStorageConnectionStrategyBase\|class.*:.*StreamConnectionStrategyBase\|class.*:.*CloudConnectionStrategyBase\|class.*:.*SearchConnectionStrategyBase\|class.*:.*ApiConnectionStrategyBase\|class.*:.*GraphConnectionStrategyBase\|class.*:.*TimeSeriesConnectionStrategyBase" Plugins/DataWarehouse.Plugins.UltimateConnector/ --include="*.cs"
```
Expected: ~280 classes total across all sub-categories.

**Step 3: Map the Connector sub-category base hierarchy.**
List all sub-category bases:
```
grep -rn "abstract class.*ConnectionStrategyBase\|abstract class.*StrategyBase.*:.*ConnectionStrategyBase" Plugins/DataWarehouse.Plugins.UltimateConnector/ --include="*.cs"
```
Also check SDK Connectors directory:
```
grep -rn "abstract class.*ConnectionStrategyBase\|abstract class.*StrategyBase" DataWarehouse.SDK/Connectors/ --include="*.cs"
```
Verify each sub-category base inherits from the SDK ConnectionStrategyBase. The chain should be:
ConcreteStrategy -> SubCategoryBase -> ConnectionStrategyBase -> StrategyBase.

**Step 4: Check for MessageBus usage in Connector (research says 1 file).**
```
grep -rn "MessageBus" Plugins/DataWarehouse.Plugins.UltimateConnector/ --include="*.cs"
```
Research found 1 Connector strategy uses MessageBus. Document which file. Do NOT modify it -- this is tracked for 25b-05.

**Step 5: Verify zero intelligence boilerplate in Connector.**
```
grep -rn "ConfigureIntelligence\|GetStrategyKnowledge\|GetStrategyCapability" Plugins/DataWarehouse.Plugins.UltimateConnector/ --include="*.cs"
```

**Step 6: Summary table for all 5 domains.**
| Domain | Expected | Actual | Intermediates | Build | Intelligence |
|--------|----------|--------|---------------|-------|-------------|
| KeyManagement | 69 | ? | Pkcs11HsmStrategyBase | ? | ? |
| RAID | 47 | ? | SdkRaidStrategyBase alias | ? | ? |
| DatabaseStorage | 49 | ? | DatabaseStorageStrategyBase | ? | ? |
| Storage | 130 | ? | UltimateStorageStrategyBase | ? | ? |
| Connector | 280 | ? | 10 sub-category bases | ? | ? |
| **Total** | **575** | **?** | | | |
  </action>
  <verify>
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateStorage/DataWarehouse.Plugins.UltimateStorage.csproj` exits 0
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateConnector/DataWarehouse.Plugins.UltimateConnector.csproj` exits 0
- UltimateStorageStrategyBase : StorageStrategyBase verified
- All 10 Connector sub-category bases inherit ConnectionStrategyBase
- Strategy counts match expected values
  </verify>
  <done>Storage (130) and Connector (280) = 410 strategies verified. All intermediate bases cascade correctly. Connector sub-category hierarchy documented. All 5 domains (575 strategies total) verified. Zero code changes.</done>
</task>

</tasks>

<verification>
1. All 5 plugins build with zero errors (or pre-existing only)
2. Strategy counts verified: KeyManagement 69, RAID 47, DatabaseStorage 49, Storage 130, Connector 280
3. All intermediate base chains documented and verified:
   - Pkcs11HsmStrategyBase : KeyStoreStrategyBase : StrategyBase
   - SdkRaidStrategyBase -> SDK RaidStrategyBase : StrategyBase
   - DatabaseStorageStrategyBase : StorageStrategyBase : StrategyBase
   - UltimateStorageStrategyBase : StorageStrategyBase : StrategyBase
   - 10 Connector sub-category bases : ConnectionStrategyBase : StrategyBase
4. Zero code changes to any file
5. Connector MessageBus usage (1 file) documented for 25b-05
</verification>

<success_criteria>
- 5 plugins compile with zero new errors
- ~575 strategy classes verified via grep counts
- All intermediate base cascades verified
- Connector sub-category hierarchy fully mapped
- Zero files modified
</success_criteria>

<output>
After completion, create `.planning/phases/25b-strategy-migration/25b-03-SUMMARY.md`
</output>
