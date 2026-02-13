---
phase: 25a-strategy-hierarchy-api-contracts
plan: 04
type: execute
wave: 1
depends_on: []
files_modified:
  - DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs
  - DataWarehouse.SDK/Contracts/NullObjects.cs
autonomous: true

must_haves:
  truths:
    - "SdkCompatibilityAttribute exists and can be applied to classes, interfaces, structs, and enums"
    - "NullMessageBus implements IMessageBus with no-op methods that do not throw"
    - "NullLogger implements ILogger with no-op methods"
    - "All new types compile with zero warnings under TreatWarningsAsErrors"
    - "SdkCompatibilityAttribute is applied to IStrategy and StrategyBase (if they exist from 25a-01)"
  artifacts:
    - path: "DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs"
      provides: "Version tracking attribute for all public SDK types"
      contains: "class SdkCompatibilityAttribute"
    - path: "DataWarehouse.SDK/Contracts/NullObjects.cs"
      provides: "Null-object implementations for optional dependencies"
      contains: "class NullMessageBus"
  key_links:
    - from: "DataWarehouse.SDK/Contracts/NullObjects.cs"
      to: "DataWarehouse.SDK/Contracts/IMessageBus.cs"
      via: "interface implementation"
      pattern: "class NullMessageBus : IMessageBus"
---

<objective>
Create the API contract safety infrastructure: SdkCompatibilityAttribute for version tracking, null-object implementations for optional dependencies (API-03, API-04).

Purpose: Establish the patterns that all subsequent phases will follow for API safety. SdkCompatibility enables version tracking on all public types. Null-object pattern eliminates scattered null checks for IMessageBus and ILogger throughout the codebase.

Output: Two new files providing the API contract safety infrastructure.

NOTE on API-01 (immutable DTOs) and API-02 (strong typing): These are NOT addressed in this plan. Research shows 129 existing records (partially done) and 666 Dictionary<string, object> usages across 70 files. Converting all DTOs is too large for one plan and many will be handled naturally during Phase 25b migration. This plan establishes the PATTERNS (attribute, null-objects). The DTO/strong-typing work is deferred to 25b and 27 where strategies are touched individually.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05)
@.planning/REQUIREMENTS.md (API-01, API-02, API-03, API-04)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-RESEARCH.md (Sections on API-01 through API-04)
@DataWarehouse.SDK/Contracts/IMessageBus.cs (IMessageBus interface for null-object)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Create SdkCompatibilityAttribute</name>
  <files>DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs</files>
  <action>
Create file `DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs` in namespace `DataWarehouse.SDK.Contracts`.

```csharp
using System;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Marks a public SDK type with version compatibility metadata.
    /// Applied to all public types to track when they were introduced,
    /// when they were deprecated, and what replaces them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This attribute enables tooling and consumers to:
    /// <list type="bullet">
    /// <item><description>Determine which SDK version introduced a type</description></item>
    /// <item><description>Identify deprecated types and their replacements</description></item>
    /// <item><description>Plan migration paths between SDK versions</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Version strings follow semantic versioning (e.g., "2.0.0", "1.5.0").
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [SdkCompatibility("2.0.0")]
    /// public class StrategyBase { }
    ///
    /// [SdkCompatibility("1.0.0", DeprecatedVersion = "2.0.0", ReplacementType = "NewType")]
    /// public class OldType { }
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct |
        AttributeTargets.Enum | AttributeTargets.Delegate,
        Inherited = false,
        AllowMultiple = false)]
    public sealed class SdkCompatibilityAttribute : Attribute
    {
        /// <summary>
        /// Gets the SDK version in which this type was introduced.
        /// </summary>
        public string IntroducedVersion { get; }

        /// <summary>
        /// Gets or sets the SDK version in which this type was deprecated.
        /// Null if the type is not deprecated.
        /// </summary>
        public string? DeprecatedVersion { get; init; }

        /// <summary>
        /// Gets or sets the fully qualified name of the replacement type.
        /// Only applicable when <see cref="DeprecatedVersion"/> is set.
        /// </summary>
        public string? ReplacementType { get; init; }

        /// <summary>
        /// Gets or sets additional notes about compatibility, migration, or usage.
        /// </summary>
        public string? Notes { get; init; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdkCompatibilityAttribute"/> class.
        /// </summary>
        /// <param name="introducedVersion">The SDK version that introduced this type (e.g., "2.0.0").</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="introducedVersion"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="introducedVersion"/> is empty or whitespace.</exception>
        public SdkCompatibilityAttribute(string introducedVersion)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(introducedVersion);
            IntroducedVersion = introducedVersion;
        }
    }
}
```

Key design decisions:
- `Inherited = false` -- the attribute describes the specific type, not its subclasses
- `AllowMultiple = false` -- a type has one introduction version
- `init` setters on optional properties -- immutable after construction (API-01 pattern)
- Targets include `Delegate` in addition to Class/Interface/Struct/Enum
- Input validation via ArgumentException.ThrowIfNullOrWhiteSpace

Do NOT apply this attribute broadly to existing types yet -- that would touch hundreds of files. Apply it ONLY to the new types created in Phase 25a (IStrategy, StrategyBase, NullMessageBus, NullLogger, SdkCompatibilityAttribute itself). Broader application happens during Phase 25b and 27 as files are touched.
  </action>
  <verify>
Run `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- zero errors, zero warnings. Verify the attribute can be instantiated: check that the constructor parameter is `string introducedVersion` and optional properties use `init`.
  </verify>
  <done>SdkCompatibilityAttribute.cs exists, has correct AttributeUsage, constructor validates input, optional properties use init-only setters, compiles cleanly.</done>
</task>

<task type="auto">
  <name>Task 2: Create null-object implementations (NullMessageBus, NullLogger)</name>
  <files>DataWarehouse.SDK/Contracts/NullObjects.cs</files>
  <action>
Create file `DataWarehouse.SDK/Contracts/NullObjects.cs` in namespace `DataWarehouse.SDK.Contracts`.

This file contains null-object implementations for optional dependencies per API-04.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// A no-op implementation of <see cref="IMessageBus"/> for use when intelligence
    /// integration is not available. All publish/send operations complete successfully
    /// as no-ops. Subscribe operations return a no-op disposable.
    /// </summary>
    /// <remarks>
    /// Use this instead of null checks throughout strategy and plugin code.
    /// <code>
    /// // Instead of:
    /// if (MessageBus != null) await MessageBus.PublishAsync(...);
    ///
    /// // Use:
    /// await MessageBus.PublishAsync(...); // NullMessageBus does nothing
    /// </code>
    /// </remarks>
    [SdkCompatibility("2.0.0", Notes = "Null-object pattern for optional IMessageBus dependency")]
    public sealed class NullMessageBus : IMessageBus
    {
        /// <summary>
        /// Gets the shared singleton instance.
        /// </summary>
        public static NullMessageBus Instance { get; } = new();

        private NullMessageBus() { }

        /// <inheritdoc />
        public Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
            => Task.FromResult(new MessageResponse { Success = false, ErrorMessage = "No message bus configured (NullMessageBus)" });

        /// <inheritdoc />
        public Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(new MessageResponse { Success = false, ErrorMessage = "No message bus configured (NullMessageBus)" });

        /// <inheritdoc />
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
            => NullSubscription.Instance;

        /// <inheritdoc />
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
            => NullSubscription.Instance;

        /// <inheritdoc />
        public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
            => NullSubscription.Instance;

        /// <inheritdoc />
        public void Unsubscribe(string topic) { }

        /// <inheritdoc />
        public IEnumerable<string> GetActiveTopics()
            => Enumerable.Empty<string>();

        /// <summary>
        /// A no-op disposable returned by subscribe methods.
        /// </summary>
        private sealed class NullSubscription : IDisposable
        {
            public static NullSubscription Instance { get; } = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A no-op implementation of <see cref="ILogger"/> for use when logging
    /// is not configured. All log operations are silently discarded.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Null-object pattern for optional ILogger dependency")]
    public sealed class NullLogger : ILogger
    {
        /// <summary>
        /// Gets the shared singleton instance.
        /// </summary>
        public static NullLogger Instance { get; } = new();

        private NullLogger() { }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
            => false;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Intentionally empty -- null-object pattern
        }
    }

    /// <summary>
    /// A no-op implementation of <see cref="ILogger{TCategoryName}"/> for use when logging
    /// is not configured for a specific category.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Null-object pattern for optional ILogger<T> dependency")]
    public sealed class NullLogger<T> : ILogger<T>
    {
        /// <summary>
        /// Gets the shared singleton instance.
        /// </summary>
        public static NullLogger<T> Instance { get; } = new();

        private NullLogger() { }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
            => false;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Intentionally empty -- null-object pattern
        }
    }
}
```

Key design decisions:
- **Singleton pattern** -- null-objects are stateless, so a single instance suffices. Thread-safe via static readonly.
- **NullMessageBus.SendAsync returns Success=false** -- callers that check the response know the bus is not connected. This is intentional: publish (fire-and-forget) silently succeeds, but send (request-response) reports no bus available.
- **NullLogger.IsEnabled returns false** -- callers checking `if (logger.IsEnabled(LogLevel.Debug))` skip expensive string formatting.
- **Private constructors** -- enforce singleton usage via `Instance` property.
- **NullSubscription** -- nested class for the IDisposable returned by Subscribe. Prevents null returns.

Check that `PluginMessage`, `MessageResponse`, `IMessageBus` are all available from `DataWarehouse.SDK.Contracts` namespace. If `MessageResponse` has additional required properties beyond `Success` and `ErrorMessage`, check the class definition and provide them.

Also check if `Microsoft.Extensions.Logging` is already a PackageReference in the SDK project. If not, do NOT add it -- instead skip NullLogger and only implement NullMessageBus. ConnectionStrategyBase is the only base that uses ILogger, and it already has its own null handling.

Actually -- read the SDK `.csproj` to check for Microsoft.Extensions.Logging.Abstractions. If it's there, implement NullLogger. If not, implement ONLY NullMessageBus and add a comment explaining NullLogger is deferred until the logging package is available.
  </action>
  <verify>
Run `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- zero errors, zero warnings. Verify NullMessageBus.Instance compiles. Verify SdkCompatibilityAttribute is applied to both NullMessageBus and NullLogger (if implemented).
  </verify>
  <done>NullObjects.cs exists with NullMessageBus (and NullLogger if logging package available), both using singleton pattern, all methods are no-ops, SdkCompatibility attribute applied, compiles cleanly.</done>
</task>

</tasks>

<verification>
1. `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` exits with code 0
2. SdkCompatibilityAttribute can be applied: `[SdkCompatibility("2.0.0")]` compiles on a test class
3. NullMessageBus.Instance is not null and all IMessageBus methods are callable without exception
4. NullLogger.Instance is not null (if implemented) and all ILogger methods are callable without exception
5. No existing code is broken by these additions (they are all new types)
</verification>

<success_criteria>
- SdkCompatibilityAttribute.cs exists with proper AttributeUsage, constructor validation, init-only optional properties
- NullObjects.cs exists with NullMessageBus implementing IMessageBus (and NullLogger if applicable)
- Both files compile with zero errors and zero warnings
- [SdkCompatibility("2.0.0")] applied to all new types in this plan
- Pattern established for broader application in subsequent phases
</success_criteria>

<output>
After completion, create `.planning/phases/25a-strategy-hierarchy-api-contracts/25a-04-SUMMARY.md`
</output>
