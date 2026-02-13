---
phase: 25a-strategy-hierarchy-api-contracts
plan: 03
type: execute
wave: 3
depends_on: ["25a-02"]
files_modified:
  - DataWarehouse.SDK/Contracts/StrategyAdapters.cs
autonomous: true

must_haves:
  truths:
    - "Adapter wrappers exist for all domain strategy bases that have concrete strategies in plugins"
    - "Each adapter wraps an old-hierarchy strategy and delegates all domain-specific calls to it"
    - "Each adapter inherits from the NEW domain strategy base (which now inherits StrategyBase)"
    - "Adapters are marked [Obsolete] with clear message about Phase 25b migration"
    - "Existing two adapters (CompressionStrategyAdapter, EncryptionStrategyAdapter) preserved and coexist"
    - "All adapters compile with zero warnings under TreatWarningsAsErrors"
  artifacts:
    - path: "DataWarehouse.SDK/Contracts/StrategyAdapters.cs"
      provides: "Backward-compatibility adapter wrappers for all domain strategy bases"
      contains: "class LegacyEncryptionStrategyAdapter"
  key_links:
    - from: "DataWarehouse.SDK/Contracts/StrategyAdapters.cs"
      to: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      via: "adapter inheritance chain through domain bases"
      pattern: "StrategyBase"
---

<objective>
Create adapter wrappers that allow ~1,500 existing plugin strategies to continue compiling while the new StrategyBase hierarchy exists (STRAT-04).

Purpose: Since Plan 25a-02 changed the domain bases to inherit StrategyBase and removed intelligence code, any concrete strategy in a plugin that overrides ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability will now have compilation errors. The adapters bridge this gap by providing the old API surface on top of the new hierarchy. Phase 25b removes these adapters during the domain-by-domain migration.

Output: Extended StrategyAdapters.cs with adapter classes for backward compatibility.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05, AD-08)
@.planning/REQUIREMENTS.md (STRAT-04)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-RESEARCH.md (Section 6: adapter pattern assessment)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-01-SUMMARY.md
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-02-SUMMARY.md
@DataWarehouse.SDK/Contracts/StrategyAdapters.cs (existing pipeline adapters to preserve)
@DataWarehouse.SDK/Contracts/StrategyBase.cs
</context>

<tasks>

<task type="auto">
  <name>Task 1: Assess actual compilation impact and determine which adapters are needed</name>
  <files>DataWarehouse.SDK/Contracts/StrategyAdapters.cs</files>
  <action>
IMPORTANT CONTEXT: After Plan 25a-02, the domain strategy bases no longer declare ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, or IsIntelligenceAvailable. Any concrete strategy in a plugin that OVERRIDES these methods will now have a compiler error (CS0115: no suitable method found to override).

**Step 1: Assess the actual damage.**

Before writing adapters, build the FULL solution to see which plugins break:
```
dotnet build DataWarehouse.slnx
```

Capture the errors. The errors will be in Plugins/ directories and will look like:
- `CS0115: 'FooStrategy.ConfigureIntelligence(IMessageBus?)': no suitable method found to override`
- `CS0115: 'FooStrategy.GetStrategyKnowledge()': no suitable method found to override`
- `CS0115: 'FooStrategy.GetStrategyCapability()': no suitable method found to override`
- `CS0117: 'EncryptionStrategyBase' does not contain a definition for 'MessageBus'` (if a concrete strategy accesses base.MessageBus)

**Step 2: Determine adapter strategy.**

There are two possible approaches:
- **Option A (Adapters in SDK):** Add virtual methods back to domain bases as no-op stubs marked [Obsolete]. This is simpler but pollutes the new clean hierarchy.
- **Option B (Intermediate adapter bases):** Create `LegacyXxxStrategyBase` classes that sit between StrategyBase and the domain base, providing the old API surface.

**Choose Option A** -- it's the simplest approach that preserves backward compatibility. The intelligence methods become no-op virtual methods on the domain bases, marked `[Obsolete("Intelligence integration removed from strategies per AD-05. Remove this override -- intelligence belongs at the plugin level. Will be removed in Phase 25b.")]`. This means:
- Existing concrete strategies that override these methods still compile (the override target exists)
- The overrides do nothing (no-ops, MessageBus is always null)
- Phase 25b removes the overrides from concrete strategies AND the obsolete virtual methods from domain bases

**Step 3: Implement the backward-compatibility shim.**

Add an intermediate abstract class to StrategyBase.cs or create a new file -- but the SIMPLEST approach is to add the backward-compat members to StrategyBase itself since ALL domain bases inherit from it:

In `DataWarehouse.SDK/Contracts/StrategyBase.cs`, add a clearly-marked region:

```csharp
#region Legacy Intelligence Compatibility (Phase 25b removes these)

/// <summary>
/// Legacy method for intelligence configuration. No longer used.
/// Intelligence belongs at the plugin level per AD-05.
/// </summary>
[Obsolete("Intelligence integration removed from strategies per AD-05. Override is a no-op. Will be removed in Phase 25b.")]
public virtual void ConfigureIntelligence(IMessageBus? messageBus) { }

/// <summary>
/// Legacy method for strategy knowledge. No longer used.
/// The parent plugin registers knowledge on behalf of its strategies per AD-05.
/// </summary>
[Obsolete("Intelligence integration removed from strategies per AD-05. Override is a no-op. Will be removed in Phase 25b.")]
public virtual KnowledgeObject GetStrategyKnowledge()
{
    return new KnowledgeObject
    {
        Id = $"strategy.{StrategyId}",
        Topic = "strategy",
        SourcePluginId = "sdk",
        SourcePluginName = Name,
        KnowledgeType = "capability",
        Description = Description,
        Payload = new Dictionary<string, object>(),
        Tags = Array.Empty<string>()
    };
}

/// <summary>
/// Legacy method for strategy capability. No longer used.
/// The parent plugin registers capabilities on behalf of its strategies per AD-05.
/// </summary>
[Obsolete("Intelligence integration removed from strategies per AD-05. Override is a no-op. Will be removed in Phase 25b.")]
public virtual RegisteredCapability GetStrategyCapability()
{
    return new RegisteredCapability
    {
        CapabilityId = $"strategy.{StrategyId}",
        DisplayName = Name,
        Description = Description,
        PluginId = "sdk",
        PluginName = Name,
        PluginVersion = "1.0.0",
        Tags = Array.Empty<string>(),
        SemanticDescription = Description
    };
}

/// <summary>
/// Legacy message bus property. Always null in new hierarchy.
/// Intelligence belongs at the plugin level per AD-05.
/// </summary>
[Obsolete("Intelligence integration removed from strategies per AD-05. Always returns null. Will be removed in Phase 25b.")]
protected IMessageBus? MessageBus { get; private set; }

/// <summary>
/// Legacy intelligence availability check. Always returns false in new hierarchy.
/// </summary>
[Obsolete("Intelligence integration removed from strategies per AD-05. Always returns false. Will be removed in Phase 25b.")]
protected bool IsIntelligenceAvailable => false;

#endregion
```

This requires adding `using DataWarehouse.SDK.AI;` to StrategyBase.cs for KnowledgeObject and RegisteredCapability types. The `#pragma warning disable CS0618` around the Obsolete usages is NOT needed -- the methods themselves are [Obsolete] but calling them from plugin code just generates a warning, which is the desired behavior (telling plugin authors to remove the overrides).

HOWEVER -- since TreatWarningsAsErrors is enabled, the [Obsolete] methods will cause CS0618 warnings in plugin code that calls or overrides them, which become ERRORS. To prevent this from breaking the build:
- Use `[Obsolete]` WITHOUT a message on the properties (just the attribute), which generates CS0612 (not an error by default)
- OR better: Add a `#pragma warning disable CS0612, CS0618` in each plugin file that overrides these
- OR simplest: Do NOT mark as [Obsolete] yet. Just add `// TODO(25b): Remove -- intelligence belongs at plugin level per AD-05` comments. Mark as [Obsolete] during Phase 25b AFTER the overrides are removed.

**Choose the simplest**: Add the backward-compat virtual methods WITHOUT [Obsolete] attribute. Add clear `// LEGACY: Phase 25b removes this. Intelligence belongs at plugin level per AD-05.` comments instead. This avoids all CS0612/CS0618 warning issues.

**Step 4: Handle IMessageBus and AI type references.**

StrategyBase.cs will need `using DataWarehouse.SDK.AI;` and `using DataWarehouse.SDK.Contracts;` (for IMessageBus) to declare the legacy compatibility methods. This is acceptable as temporary backward-compat.

**Step 5: Build the full solution.**

After adding backward-compat methods to StrategyBase, build the full solution:
```
dotnet build DataWarehouse.slnx
```

Fix any remaining errors in plugins. Common issues:
- Some concrete strategies may access `base.MessageBus` -- the backward-compat property on StrategyBase satisfies this
- Some may call `base.ConfigureIntelligence()` -- the backward-compat method satisfies this
- Some may use intelligence helpers that were on the domain base but not on StrategyBase (e.g., `RequestEncryptionOptimizationAsync`). These should be rare -- if found, add a minimal no-op stub to the appropriate domain base, NOT to StrategyBase

Also update StrategyAdapters.cs: The existing CompressionStrategyAdapter and EncryptionStrategyAdapter inherit from CompressionPluginBase and EncryptionPluginBase (PLUGIN bases, not strategy bases). They should NOT be affected by strategy hierarchy changes. Verify they still compile. Do NOT modify them unless they break.
  </action>
  <verify>
Run `dotnet build DataWarehouse.slnx` (full solution) -- zero errors. Count warnings -- should be zero or only pre-existing CS1729/CS0234 errors in UltimateCompression and AedsCore (documented as pre-existing in STATE.md). No NEW warnings introduced.
  </verify>
  <done>All ~1,500 plugin strategies compile against the new hierarchy. Backward-compatible legacy methods (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable) exist on StrategyBase as no-op stubs clearly marked for removal in Phase 25b. Full solution builds successfully.</done>
</task>

</tasks>

<verification>
1. `dotnet build DataWarehouse.slnx` exits with code 0 (zero new errors)
2. Every plugin directory under Plugins/ compiles successfully
3. Legacy methods exist on StrategyBase with clear removal comments
4. Existing StrategyAdapters.cs (CompressionStrategyAdapter, EncryptionStrategyAdapter) still work
5. No domain-specific logic was lost from any strategy (AD-08)
</verification>

<success_criteria>
- Full solution (SDK + all 60 plugins) builds with zero new errors
- ~1,500 concrete strategies compile unchanged
- Legacy backward-compatibility methods clearly documented for Phase 25b removal
- Zero regression -- all existing strategy functionality preserved
</success_criteria>

<output>
After completion, create `.planning/phases/25a-strategy-hierarchy-api-contracts/25a-03-SUMMARY.md`
</output>
