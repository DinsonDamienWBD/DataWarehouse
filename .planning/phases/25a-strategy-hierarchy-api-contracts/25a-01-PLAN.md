---
phase: 25a-strategy-hierarchy-api-contracts
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - DataWarehouse.SDK/Contracts/IStrategy.cs
  - DataWarehouse.SDK/Contracts/StrategyBase.cs
autonomous: true

must_haves:
  truths:
    - "IStrategy interface defines Name, Description, StrategyId, and Characteristics metadata properties"
    - "StrategyBase implements full IDisposable + IAsyncDisposable dispose pattern with GC.SuppressFinalize"
    - "StrategyBase provides InitializeAsync and ShutdownAsync lifecycle methods with CancellationToken"
    - "StrategyBase has ZERO intelligence code (no IMessageBus, no ConfigureIntelligence, no GetStrategyKnowledge, no GetStrategyCapability)"
    - "StrategyBase compiles with zero warnings under TreatWarningsAsErrors"
  artifacts:
    - path: "DataWarehouse.SDK/Contracts/IStrategy.cs"
      provides: "Root strategy interface defining identity and metadata contract"
      contains: "interface IStrategy"
    - path: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      provides: "Abstract root class for all strategy bases with lifecycle and dispose"
      contains: "abstract class StrategyBase"
      exports: ["StrategyBase", "IStrategy"]
  key_links:
    - from: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      to: "DataWarehouse.SDK/Contracts/IStrategy.cs"
      via: "class inheritance"
      pattern: "StrategyBase.*:.*IStrategy"
---

<objective>
Create the unified StrategyBase root class and IStrategy interface for the flat strategy hierarchy (STRAT-01).

Purpose: Establish the single root that all ~17 domain strategy bases will inherit from, replacing the current fragmented design where 17 bases independently implement lifecycle, dispose, and metadata. Per AD-05, StrategyBase is a WORKER -- NO intelligence, NO capability registry, NO knowledge bank, NO message bus.

Output: Two new files in DataWarehouse.SDK/Contracts/ providing the unified strategy root.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05: flat strategy hierarchy, no intelligence)
@.planning/REQUIREMENTS.md (STRAT-01)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-RESEARCH.md
@DataWarehouse.SDK/Contracts/IPlugin.cs (for namespace conventions)
@DataWarehouse.SDK/Contracts/IMessageBus.cs (IMessageBus interface -- StrategyBase must NOT reference this)
@DataWarehouse.SDK/Contracts/Observability/ObservabilityStrategyBase.cs (example of existing base with dispose pattern to consolidate)
@DataWarehouse.SDK/Contracts/Interface/InterfaceStrategyBase.cs (example of existing base with lifecycle to consolidate)
@DataWarehouse.SDK/Contracts/Transit/DataTransitStrategyBase.cs (example of existing base with statistics to consolidate)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create IStrategy interface</name>
  <files>DataWarehouse.SDK/Contracts/IStrategy.cs</files>
  <action>
Create file `DataWarehouse.SDK/Contracts/IStrategy.cs` in namespace `DataWarehouse.SDK.Contracts`.

Define `IStrategy` interface with these members (all readonly properties):

```csharp
/// <summary>
/// Root interface for all strategy implementations in the DataWarehouse SDK.
/// Strategies are workers -- they perform a specific operation within a domain.
/// Intelligence, capability registration, and knowledge bank access belong at
/// the plugin level (see AD-05).
/// </summary>
public interface IStrategy : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the unique machine-readable identifier for this strategy.
    /// Used for routing, logging, and capability registration by the parent plugin.
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable display name of this strategy.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a description of what this strategy does and when to use it.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the strategy characteristics metadata (performance profile, hardware
    /// requirements, supported features). The parent plugin uses these to register
    /// capabilities and make selection decisions.
    /// </summary>
    IReadOnlyDictionary<string, object> Characteristics { get; }

    /// <summary>
    /// Initializes the strategy, acquiring any resources needed for operation.
    /// Called by the parent plugin during its own initialization phase.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the strategy, releasing all acquired resources.
    /// Called by the parent plugin during its own shutdown phase.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
```

Required usings: `System`, `System.Collections.Generic`, `System.Threading`, `System.Threading.Tasks`.

IMPORTANT: Do NOT add any intelligence-related members (IMessageBus, ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, IsIntelligenceAvailable). Per AD-05, strategies are workers -- intelligence belongs at the plugin level only.

Add full XML documentation on every member. Use the file-scoped namespace style consistent with newer SDK files (e.g., InterfaceStrategyBase.cs uses `namespace DataWarehouse.SDK.Contracts.Interface;`), but since this is in the root Contracts folder, use block-style `namespace DataWarehouse.SDK.Contracts { }` consistent with IPlugin.cs and IMessageBus.cs.
  </action>
  <verify>
Run `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` from the solution root -- must compile with zero errors and zero warnings. Grep `DataWarehouse.SDK/Contracts/IStrategy.cs` for `IMessageBus`, `ConfigureIntelligence`, `GetStrategyKnowledge`, `GetStrategyCapability` -- must return zero matches.
  </verify>
  <done>IStrategy.cs exists in DataWarehouse.SDK/Contracts/, defines StrategyId/Name/Description/Characteristics/InitializeAsync/ShutdownAsync, inherits IDisposable + IAsyncDisposable, has ZERO intelligence members, compiles cleanly.</done>
</task>

<task type="auto">
  <name>Task 2: Create StrategyBase abstract class</name>
  <files>DataWarehouse.SDK/Contracts/StrategyBase.cs</files>
  <action>
Create file `DataWarehouse.SDK/Contracts/StrategyBase.cs` in namespace `DataWarehouse.SDK.Contracts`.

Define `StrategyBase` abstract class implementing `IStrategy`:

```csharp
/// <summary>
/// Abstract root class for all strategy implementations. Provides lifecycle management,
/// dispose pattern, metadata, and structured logging hooks. All domain strategy bases
/// (Encryption, Storage, Compression, etc.) inherit from this class.
/// <para>
/// Per AD-05: Strategies are workers, NOT orchestrators. They do NOT have intelligence,
/// capability registry, knowledge bank access, or message bus. The parent plugin handles
/// all of that.
/// </para>
/// </summary>
public abstract class StrategyBase : IStrategy
{
    private bool _disposed;
    private bool _initialized;
    private readonly object _lifecycleLock = new();

    // --- Identity (abstract, each domain base/concrete strategy provides) ---
    public abstract string StrategyId { get; }
    public abstract string Name { get; }
    public virtual string Description => $"{Name} strategy";

    // --- Characteristics ---
    /// <summary>
    /// Override to provide domain-specific characteristics metadata.
    /// The parent plugin reads these to register capabilities on the strategy's behalf.
    /// Default returns empty dictionary.
    /// </summary>
    public virtual IReadOnlyDictionary<string, object> Characteristics { get; }
        = new Dictionary<string, object>();

    // --- Lifecycle ---
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (_initialized) return;
        lock (_lifecycleLock)
        {
            if (_initialized) return;
        }
        await InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;
        await ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
        _initialized = false;
    }

    protected virtual Task InitializeAsyncCore(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected virtual Task ShutdownAsyncCore(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Gets whether this strategy has been initialized and is ready for use.
    /// </summary>
    protected bool IsInitialized => _initialized;

    // --- Dispose pattern (IDisposable + IAsyncDisposable) ---
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // Subclasses override to dispose managed resources
        }
        _disposed = true;
    }

    protected virtual ValueTask DisposeAsyncCore()
        => ValueTask.CompletedTask;

    protected void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
```

Key design decisions:
1. `_lifecycleLock` is a plain `object` (not SemaphoreSlim) because the lock only guards the boolean check -- the actual init work runs outside the lock. This matches the pattern from ObservabilityStrategyBase but avoids the SemaphoreSlim overhead for a simple guard.
2. `Characteristics` uses `IReadOnlyDictionary<string, object>` (not strongly typed yet) -- this is the existing pattern. API-02 strong typing is addressed in Plan 25a-04 for NEW types, but existing strategy characteristics use this shape. Changing it would break ~1,500 strategies.
3. The dispose pattern follows the standard Microsoft pattern: `Dispose(bool)` for sync, `DisposeAsyncCore()` for async, both guarded by `_disposed`.
4. `Description` is virtual with a default (not abstract) so existing domain bases don't need to add it immediately.
5. NO `ILogger` parameter -- per existing codebase conventions, only ConnectionStrategyBase has ILogger. Logging hooks will be added if Phase 24 establishes a logging pattern.

CRITICAL: Do NOT add IMessageBus, ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, IsIntelligenceAvailable, or any intelligence-related code. Per AD-05, strategies are workers.

Required usings: `System`, `System.Collections.Generic`, `System.Threading`, `System.Threading.Tasks`.

Use block-style namespace `DataWarehouse.SDK.Contracts { }` (consistent with same-directory files).
Add full XML documentation on every public/protected member.
  </action>
  <verify>
Run `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- must compile with zero errors and zero warnings. Grep `DataWarehouse.SDK/Contracts/StrategyBase.cs` for `IMessageBus|ConfigureIntelligence|GetStrategyKnowledge|GetStrategyCapability|IsIntelligenceAvailable` -- must return zero matches. Verify StrategyBase implements IStrategy by grepping for `class StrategyBase.*IStrategy`.
  </verify>
  <done>StrategyBase.cs exists in DataWarehouse.SDK/Contracts/, implements IStrategy, provides lifecycle (InitializeAsync/ShutdownAsync), full dispose pattern (IDisposable + IAsyncDisposable with Dispose(bool) + DisposeAsyncCore), metadata (StrategyId/Name/Description/Characteristics), ZERO intelligence code, compiles with zero warnings.</done>
</task>

</tasks>

<verification>
1. `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` exits with code 0 (zero errors, zero warnings)
2. `IStrategy` interface has exactly 6 members: StrategyId, Name, Description, Characteristics, InitializeAsync, ShutdownAsync
3. `StrategyBase` class implements `IStrategy`, `IDisposable`, `IAsyncDisposable`
4. Zero occurrences of intelligence-related code in either file (grep for IMessageBus, ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability)
5. Dispose pattern includes `GC.SuppressFinalize(this)` in both Dispose() and DisposeAsync()
</verification>

<success_criteria>
- IStrategy.cs and StrategyBase.cs exist under DataWarehouse.SDK/Contracts/
- StrategyBase provides lifecycle, dispose, and metadata -- NO intelligence
- SDK project builds with zero errors and zero warnings
- No existing code is broken (StrategyBase is a new addition, nothing inherits it yet)
</success_criteria>

<output>
After completion, create `.planning/phases/25a-strategy-hierarchy-api-contracts/25a-01-SUMMARY.md`
</output>
