# Coding Conventions

**Analysis Date:** 2026-02-10

## Naming Patterns

**Files:**
- PascalCase for all C# files: `ConfigCommands.cs`, `PluginDescriptor.cs`, `DataWarehouseKernel.cs`
- File names match public class/namespace: `ConfigCommands.cs` contains `ConfigCommands` class
- Test files use TestSuffix: `DashboardServiceTests.cs`, `SystemHealthServiceTests.cs`

**Functions/Methods:**
- PascalCase for all public methods: `ShowConfigAsync()`, `GetConfigurationAsync()`, `ExecuteAsync()`
- Async methods end with `Async`: `GetSystemHealthAsync()`, `ConnectAsync()`, `CreatePoolAsync()`
- Private methods use PascalCase: `GetConfiguration()`, `GetCurrentMetrics()` (even when private)
- Boolean methods prefixed with `Is` or `Has`: `IsReady`, `IsClustered`, `HasAICapabilities`
- Commands methods match CLI verbs: `ListEntriesAsync()`, `CreateBackupAsync()`, `DeleteBackupAsync()`

**Variables:**
- Local variables: camelCase: `section`, `config`, `eventFired`, `pool`, `result`
- Private fields: camelCase with underscore prefix: `_logger`, `_loggerMock`, `_service`, `_config`
- Parameters: camelCase: `profileName`, `localPath`, `authToken`, `useTls`
- Constants/Static readonly: PascalCase or SCREAMING_SNAKE_CASE context-dependent

**Types:**
- PascalCase for all types: `TestSystemHealthService`, `PluginDiscoveryService`, `StorageManagementService`
- Interface names prefixed with `I`: `IPlugin`, `IMessageBus`, `IStorageProvider`, `ISecurityContext`
- Abstract classes: `WasmFunctionPluginBase`, `SemanticAnalyzerBase`, `FeaturePluginBase`
- Record types use PascalCase: `TestStoragePoolInfo`, `PluginMessage`, `TestComponentHealth`
- Enum names singular or plural depending on context: `PluginCategory`, `CapabilityCategory`, `OperatingMode`

## Code Style

**Formatting:**
- Latest C# language features enabled via `<LangVersion>latest</LangVersion>` in project files
- Implicit usings enabled globally: `<ImplicitUsings>enable</ImplicitUsings>`
- 4-space indentation (standard C# convention)
- Opening braces on same line (K&R style): `public class MyClass {`
- No blank lines between class declaration and first member

**Linting:**
- Nullable reference types enabled: `<Nullable>enable</Nullable>`
- Warnings treated as errors for nullable checks: `<WarningsAsErrors>nullable</WarningsAsErrors>`
- Code analyzers enabled: `<EnableNETAnalyzers>true</EnableNETAnalyzers>`
- Analysis level set to latest: `<AnalysisLevel>latest</AnalysisLevel>`
- Code style enforcement: `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
- No .editorconfig file found - relying on Directory.Build.props and analyzer defaults

**Null Safety:**
- Nullable reference types enforced across all projects
- Method return types explicitly marked with `?` when nullable: `Task<TestSystemMetrics?>`, `TestPluginInfo?`
- Parameters marked nullable when accepting nulls: `string?`, `Dictionary<string, object>?`
- Null-coalescing operators used: `value ?? new Dictionary<string, object>()`
- Null-forgiving operator `!` used sparingly when type narrowing is certain: `result.InstanceId!`

## Import Organization

**Order:**
1. System namespaces (System, System.Collections, etc.)
2. Microsoft.Extensions namespaces
3. Third-party packages (Spectre.Console, BenchmarkDotNet, Moq, FluentAssertions)
4. Project-specific namespaces (DataWarehouse.*)
5. Global using directives (in GlobalUsings.cs files)

**Example from ConfigCommands.cs:**
```csharp
using Spectre.Console;
using System.Text.Json;

namespace DataWarehouse.CLI.Commands;
```

**Example from DashboardServiceTests.cs:**
```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
```

**Global Usings:**
- Test project uses GlobalUsings.cs file listing common imports
- Located at: `C:\Temp\DataWarehouse\DataWarehouse\DataWarehouse.Tests\GlobalUsings.cs`
- Includes: System, System.Collections.Generic, System.IO, System.Linq, System.Threading, System.Threading.Tasks

**Path Aliases:**
- No path aliases detected - full namespaces used throughout
- Namespaces follow folder structure exactly: `DataWarehouse.CLI.Commands` maps to `DataWarehouse.CLI\Commands\`

## Error Handling

**Patterns:**
- Try-catch-catch pattern used for async operations:
  ```csharp
  try
  {
      // operation
  }
  catch (OperationCanceledException)
  {
      // handle cancellation
  }
  catch (Exception ex)
  {
      // handle general exception
  }
  ```
- Specific exception types caught before general Exception
- Custom exceptions thrown with descriptive messages:
  ```csharp
  throw new InvalidOperationException("Function execution failed")
  throw new KeyNotFoundException($"Function {functionId} not found")
  ```
- Null checks using pattern matching and conditionals:
  ```csharp
  if (!string.IsNullOrEmpty(section)) { /* handle */ }
  if (config.TryGetValue(key, out var value)) { /* handle */ }
  ```
- Guard clauses for early returns: early exit on error conditions

**Logging:**
- Injected via constructor parameters: `ILogger<T>`
- NullLoggerFactory.Instance used for placeholder loggers: `new DataWarehouseHost(NullLoggerFactory.Instance)`
- Log methods: LogInfo(), LogWarning(), LogError() typically defined in interfaces/base classes
- Async operations: logging happens during and after Status() operations for CLI feedback

## Comments

**When to Comment:**
- XML documentation comments (///) on public types and methods (see Pattern below)
- Class-level summaries describe responsibility and behavior
- Remarks sections explain non-obvious design decisions
- Method summaries explain what the method does, not how
- Inline comments avoided - code should be self-documenting
- TODO/FIXME comments used but not extensively found in production code

**JSDoc/TSDoc:**
- C# uses XML documentation format (///) not JSDoc
- Summary, Remarks, Param, Returns, Examples all common
- Example from PluginPriorityAttribute.cs:
  ```csharp
  /// <summary>
  /// Declares the load priority of a plugin.
  /// Higher values are loaded first. Used for conflict resolution.
  /// </summary>
  /// <remarks>
  /// Initializes a new instance of the PluginPriorityAttribute.
  /// </remarks>
  /// <param name="priority">Higher numbers indicate preference.</param>
  /// <param name="optimizedFor">The mode this plugin performs best in.</param>
  ```

## Function Design

**Size:**
- Small, focused functions preferred
- Methods typically 5-50 lines in CLI commands
- Async methods use `await` patterns and Status() wrappers for CLI feedback
- Static methods used for stateless operations: `ConfigCommands` contains all static methods

**Parameters:**
- Explicit parameters for configuration (no magic strings)
- Optional parameters marked with `?` for nullability
- Named parameters in method calls for clarity:
  ```csharp
  new JsonSerializerOptions { WriteIndented = true }
  table.AddRow("Key", "Value")
  ```
- Parameter order: required params first, optional params last, cancellation token at end

**Return Values:**
- Async methods return `Task<T>` for values, `Task` for void operations
- Boolean returns for operations that may fail: `await DeletePoolAsync()` returns `bool`
- Null returned when lookup fails: `GetPlugin(id)` returns `TestPluginInfo?`
- Collections never null: `new List<>()`, `new Dictionary<>()` as defaults
- Status checks via properties: `IsReady`, `Success`, `IsAcknowledged`

## Module Design

**Exports:**
- Public classes in separate files
- Test helper classes in same file as tests (marked as `#region`)
- Interface definitions in dedicated files: `IMessageBus.cs`, `IStorageProvider.cs`
- Static command classes group related operations: `ConfigCommands`, `BackupCommands`

**File Organization:**
- Related classes grouped by concern: all Dashboard tests in `DashboardServiceTests.cs`
- Multiple test classes in single file when tightly related
- Helper/stub classes at end of test files in `#region Test Implementations`

**Internal/Private Access:**
- Internal constructors for restricted instantiation: `internal DataWarehouseKernel(KernelConfiguration config, ...)`
- Builder pattern used for complex initialization: `KernelBuilder` for fluent configuration
- Sealed classes for security: `public sealed class DataWarehouseKernel`
- Abstract base classes for extensibility: `WasmFunctionPluginBase`, `FeaturePluginBase`

---

*Convention analysis: 2026-02-10*
