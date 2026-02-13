---
phase: 25a-strategy-hierarchy-api-contracts
plan: 05
type: execute
wave: 4
depends_on: ["25a-03", "25a-04"]
files_modified:
  - DataWarehouse.SDK/Contracts/IStrategy.cs
  - DataWarehouse.SDK/Contracts/StrategyBase.cs
autonomous: true

must_haves:
  truths:
    - "Full solution (DataWarehouse.slnx) builds with zero NEW errors"
    - "All 17+ domain strategy bases inherit from StrategyBase (verified by grep)"
    - "Zero intelligence code remains in any strategy base file (verified by grep)"
    - "All ~1,500 concrete plugin strategies compile via backward-compat layer"
    - "SdkCompatibilityAttribute and NullMessageBus exist and are usable"
    - "[SdkCompatibility] applied to IStrategy and StrategyBase"
    - "Pre-existing test suite still passes"
  artifacts:
    - path: "DataWarehouse.SDK/Contracts/IStrategy.cs"
      provides: "Root strategy interface with SdkCompatibility attribute"
      contains: "[SdkCompatibility"
    - path: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      provides: "Root strategy base with SdkCompatibility attribute"
      contains: "[SdkCompatibility"
  key_links:
    - from: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      to: "DataWarehouse.SDK/Contracts/IStrategy.cs"
      via: "implements"
      pattern: "class StrategyBase.*IStrategy"
    - from: "DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs"
      to: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      via: "inherits"
      pattern: "class EncryptionStrategyBase : StrategyBase"
---

<objective>
Final build verification and attribute application for Phase 25a. Verify the entire solution compiles, apply SdkCompatibility attributes to new types, confirm all requirements are met, and run the existing test suite (STRAT-01, STRAT-02, STRAT-04, STRAT-05, API-03, API-04).

Purpose: Verify zero regression (AD-08). Every plugin, every strategy, every test must work after the hierarchy changes. This is the final gate before Phase 25a is considered complete.

Output: Clean build, passing tests, SdkCompatibility attributes on new types.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05, AD-08)
@.planning/REQUIREMENTS.md (STRAT-01 through STRAT-05, API-01 through API-04)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-01-SUMMARY.md
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-02-SUMMARY.md
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-03-SUMMARY.md
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-04-SUMMARY.md
</context>

<tasks>

<task type="auto">
  <name>Task 1: Apply SdkCompatibility attributes and verify full solution build</name>
  <files>
    DataWarehouse.SDK/Contracts/IStrategy.cs
    DataWarehouse.SDK/Contracts/StrategyBase.cs
  </files>
  <action>
**Step 1: Apply [SdkCompatibility("2.0.0")] to new Phase 25a types.**

Open each file and add the attribute:

In `DataWarehouse.SDK/Contracts/IStrategy.cs`:
```csharp
[SdkCompatibility("2.0.0", Notes = "Root strategy interface -- AD-05 flat hierarchy")]
public interface IStrategy : IDisposable, IAsyncDisposable
```

In `DataWarehouse.SDK/Contracts/StrategyBase.cs`:
```csharp
[SdkCompatibility("2.0.0", Notes = "Root strategy base class -- AD-05 flat hierarchy, no intelligence")]
public abstract class StrategyBase : IStrategy
```

(NullMessageBus, NullLogger, and SdkCompatibilityAttribute itself should already have the attribute from Plan 04.)

**Step 2: Full solution build.**

```bash
dotnet build DataWarehouse.slnx --verbosity minimal
```

Capture ALL output. Analyze:
- Zero NEW errors (pre-existing CS1729/CS0234 in UltimateCompression and AedsCore are acceptable as documented in STATE.md)
- Zero NEW warnings
- All 60+ projects build successfully

If ANY new errors exist, diagnose and fix. Common post-hierarchy issues:
- CS0115 in plugins: concrete strategy overrides a method that no longer exists on the base. The backward-compat layer from Plan 03 should handle this. If not, add the missing method to the backward-compat region in StrategyBase.
- CS0108: hiding inherited member. Add `override` keyword.
- CS0534: missing abstract member. The concrete strategy needs to implement it.

**Step 3: Run test suite.**

```bash
dotnet test DataWarehouse.slnx --verbosity minimal --no-build
```

If tests were run after the build, use `--no-build` to save time. All 1,039+ tests must pass. Zero failures. If any test fails:
- Read the failure message
- Determine if it's related to Phase 25a changes
- If yes: fix the issue (likely a missing override or changed method signature)
- If no (pre-existing): document it but do not block on it

**Step 4: Grep verification of intelligence removal.**

Run these verification greps across ALL strategy base files (not plugin files -- plugins still have backward-compat overrides):

```bash
# Should return ZERO matches in strategy base files (SDK only):
grep -rn "ConfigureIntelligence" DataWarehouse.SDK/Contracts/*/  --include="*StrategyBase.cs" --include="*Strategy.cs"
grep -rn "ConfigureIntelligence" DataWarehouse.SDK/Connectors/ --include="*StrategyBase.cs"
grep -rn "ConfigureIntelligence" DataWarehouse.SDK/Security/ --include="*KeyStore.cs"

# Except for the backward-compat region in StrategyBase.cs itself (1 match expected):
grep -rn "ConfigureIntelligence" DataWarehouse.SDK/Contracts/StrategyBase.cs
```

The ONLY file that should contain intelligence method declarations is `StrategyBase.cs` (backward-compat region). All domain bases (EncryptionStrategyBase, StorageStrategyBase, etc.) should be clean.

**Step 5: Verify hierarchy structure.**

```bash
# All domain bases inherit StrategyBase:
grep -rn ": StrategyBase" DataWarehouse.SDK/ --include="*.cs"
```

Should return 17+ matches (one per domain base). Verify the list matches the research:
- EncryptionStrategyBase, StorageStrategyBase, CompressionStrategyBase, SecurityStrategyBase
- ComplianceStrategyBase, StreamingStrategyBase, ReplicationStrategyBase
- DataTransitStrategyBase, InterfaceStrategyBase, MediaStrategyBase, ObservabilityStrategyBase
- PipelineComputeStrategyBase (or renamed), DataFormatStrategyBase, DataLakeStrategyBase
- DataMeshStrategyBase, StorageProcessingStrategyBase, RaidStrategyBase
- ConnectionStrategyBase, KeyStoreStrategyBase

**Step 6: Count intelligence lines removed.**

Compare the total lines in modified strategy base files before and after. The research estimated 1,500-2,000 lines of intelligence boilerplate removed. Verify by comparing file sizes.

**Step 7: Requirement verification checklist.**

Verify each Phase 25a requirement:

- [ ] **STRAT-01**: StrategyBase exists with lifecycle, dispose, metadata, CancellationToken, NO intelligence
- [ ] **STRAT-02**: All domain bases inherit StrategyBase (flat two-level hierarchy)
- [ ] **STRAT-04**: Backward-compat layer ensures existing strategies compile (adapter wrappers)
- [ ] **STRAT-05**: Domain-specific contracts preserved (EncryptAsync, StoreAsync, etc.)
- [ ] **API-01**: Partially addressed (existing records preserved, new types use records/init-only). Full conversion deferred to 25b/27.
- [ ] **API-02**: Partially addressed (no new Dictionary<string, object> in new types). Full conversion deferred to 25b/27.
- [ ] **API-03**: SdkCompatibilityAttribute exists and applied to all new types
- [ ] **API-04**: NullMessageBus and NullLogger (if applicable) exist as null-object pattern
  </action>
  <verify>
Run `dotnet build DataWarehouse.slnx` -- zero new errors. Run `dotnet test DataWarehouse.slnx --no-build` -- all tests pass. Grep for `ConfigureIntelligence` in domain strategy base files -- zero matches (only backward-compat in StrategyBase.cs). Grep for `: StrategyBase` -- 17+ matches.
  </verify>
  <done>Full solution builds with zero new errors, all 1,039+ tests pass, SdkCompatibility applied to new types, intelligence code removed from all domain bases (only backward-compat stubs remain on StrategyBase), all Phase 25a requirements verified.</done>
</task>

</tasks>

<verification>
1. `dotnet build DataWarehouse.slnx` -- zero new errors, zero new warnings
2. `dotnet test DataWarehouse.slnx` -- all tests pass (1,039+ tests, zero failures)
3. Intelligence code absent from ALL domain strategy base files (17+ files clean)
4. Backward-compat stubs present ONLY on StrategyBase.cs
5. [SdkCompatibility("2.0.0")] applied to IStrategy, StrategyBase, SdkCompatibilityAttribute, NullMessageBus, NullLogger
6. NullMessageBus.Instance is usable (implements all IMessageBus methods)
7. Hierarchy verified: 17+ domain bases inherit StrategyBase
8. Requirements STRAT-01, STRAT-02, STRAT-04, STRAT-05, API-03, API-04 satisfied
9. Requirements API-01, API-02 partially addressed (pattern established, full conversion deferred)
</verification>

<success_criteria>
- Full solution (SDK + 60 plugins) builds successfully
- All existing tests pass (zero regression -- AD-08)
- All Phase 25a requirements verified
- Intelligence boilerplate removed from strategy hierarchy (~1,500-2,000 lines)
- New API contract patterns established (SdkCompatibility, null-objects)
- Phase 25a complete and ready for 25b strategy migration
</success_criteria>

<output>
After completion, create `.planning/phases/25a-strategy-hierarchy-api-contracts/25a-05-SUMMARY.md`
</output>
