# Phase 22: Build Safety & Supply Chain - Research

**Researched:** 2026-02-13
**Domain:** .NET Build Configuration, Roslyn Analyzers, NuGet Supply Chain, SBOM Generation, CLI Migration
**Confidence:** HIGH

## Summary

Phase 22 targets build-time quality enforcement and supply chain security for a .NET 10 solution with 69 projects (1 SDK, 60 plugins, 1 kernel, 1 shared, 1 CLI, 1 launcher, 1 GUI, 1 dashboard, 1 benchmarks, 1 tests). The codebase currently has a solid foundation -- Directory.Build.props already configures `Nullable enable`, `LangVersion latest`, `EnableNETAnalyzers true`, `EnforceCodeStyleInBuild true`, and `WarningsAsErrors nullable`. However, `TreatWarningsAsErrors` is NOT globally enabled, no Roslyn analyzer packages are installed, no .editorconfig exists, no SBOM tooling exists, no NuGet lock files exist, and 43+ floating version ranges need pinning.

The current build state is better than expected: the SDK, Shared, and CLI projects compile with zero warnings. The full solution produces 10 unique NuGet warnings (NU1608 dependency constraint violations, NU1603 version not found, NU1510 unnecessary packages). These are all NuGet-sourced and fixable. 12 of 60 plugin projects already have `TreatWarningsAsErrors true` (set individually in their .csproj files during prior phases). The CLI uses the deprecated `System.CommandLine.NamingConventionBinder` package which was never stabilized -- it must be migrated to the stable System.CommandLine 2.0.3 SetAction API.

**Primary recommendation:** Add Roslyn analyzers to Directory.Build.props (not individual .csproj files) so all 69 projects get them automatically, move TreatWarningsAsErrors to Directory.Build.props, fix the 10 NuGet warnings, pin all floating versions, generate SBOM with CycloneDX, and migrate CLI from NamingConventionBinder to stable SetAction pattern.

## Standard Stack

### Core (Already In Place)
| Setting | Current Value | Source | Status |
|---------|---------------|--------|--------|
| TargetFramework | net10.0 | All .csproj files | OK |
| Nullable | enable | Directory.Build.props | OK |
| LangVersion | latest | Directory.Build.props | OK |
| ImplicitUsings | enable | Directory.Build.props | OK |
| EnableNETAnalyzers | true | Directory.Build.props | OK |
| AnalysisLevel | latest | Directory.Build.props | OK -- upgrade to `latest-recommended` |
| EnforceCodeStyleInBuild | true | Directory.Build.props | OK |
| WarningsAsErrors | nullable | Directory.Build.props | OK -- keep, subsume under TreatWarningsAsErrors |

### Analyzer Packages to Add
| Package | Version | Purpose | Confidence |
|---------|---------|---------|------------|
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 3.3.4 | Ban unsafe APIs via BannedSymbols.txt | HIGH - latest stable on NuGet |
| SonarAnalyzer.CSharp | 10.19.0.132793 | Code quality, maintainability, reliability (500+ rules) | HIGH - verified NuGet Jan 30, 2026 |
| Roslynator.Analyzers | 4.15.0 | 200+ code style and quality analyzers | HIGH - verified NuGet |
| SecurityCodeScan.VS2019 | 5.6.7 | Security vulnerability detection (OWASP Top 10) | MEDIUM - last updated Sep 2022, may have compatibility issues with .NET 10 |

### Supply Chain Tools
| Tool | Version | Purpose | Confidence |
|------|---------|---------|------------|
| CycloneDX | 6.0.0 | SBOM generation (CycloneDX format) | HIGH - actively maintained, supports .slnx |
| Microsoft.Sbom.DotNetTool | 4.1.5 | SBOM generation (SPDX format) | HIGH - Microsoft official |

### CLI Dependency
| Package | Current | Target | Notes |
|---------|---------|--------|-------|
| System.CommandLine | 2.0.3 | 2.0.3 | Already on stable version -- keep |
| System.CommandLine.NamingConventionBinder | (implicit/transitive) | REMOVE | Deprecated, legacy, never reached stable |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| SecurityCodeScan.VS2019 | Meziantou.Analyzer | SecurityCodeScan unmaintained since 2022; Meziantou is actively maintained but less OWASP-focused. Keep SecurityCodeScan for now but flag for replacement if .NET 10 issues arise |
| CycloneDX | Microsoft.Sbom.DotNetTool (SPDX) | CycloneDX is lighter, integrates better with dotnet CLI. SPDX is required for US govt compliance. Could generate both. |
| BannedApiAnalyzers 3.3.4 | BannedApiAnalyzers 5.0.0-preview | 5.0.0 is prerelease only. Use 3.3.4 stable. |

**Installation (add to Directory.Build.props):**
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.3.4">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
  <PackageReference Include="SecurityCodeScan.VS2019" Version="5.6.7">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
  <PackageReference Include="SonarAnalyzer.CSharp" Version="10.19.0.132793">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
  <PackageReference Include="Roslynator.Analyzers" Version="4.15.0">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

**SBOM tool installation:**
```bash
dotnet tool install --global CycloneDX --version 6.0.0
```

## Current Codebase State (Verified)

### Directory.Build.props (Root)
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <WarningsAsErrors>nullable</WarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <NoWarn>$(NoWarn);CS0618;CS0649;CS0219;CS0414;CS0169;CS0162;CS0067;CS0168;CA1416;CA1418;CA2024</NoWarn>
  </PropertyGroup>
</Project>
```

### What Does NOT Exist Yet
- No `.editorconfig` file anywhere in the solution
- No `Directory.Build.targets`
- No `*.ruleset` files
- No `*.globalconfig` files
- No `NuGet.config`
- No `packages.lock.json` (no lock files)
- No `.config/dotnet-tools.json` (no local tool manifest)
- No `BannedSymbols.txt`
- No analyzer PackageReferences in any .csproj
- No SBOM tooling configuration

### TreatWarningsAsErrors State
- **Directory.Build.props**: NOT SET (only WarningsAsErrors=nullable)
- **12 plugin .csproj files**: SET to true individually (from prior phases)
  - AdaptiveTransport, AppPlatform, DataMarketplace, KubernetesCsi, PluginMarketplace, UltimateDataCatalog, UltimateDataFabric, UltimateDataTransit, UltimateDocGen, UltimateReplication, UltimateSDKPorts, UltimateWorkflow
- **48 plugin .csproj files**: NOT SET
- **Core projects** (SDK, Shared, CLI, Kernel, Launcher, GUI, Dashboard, Benchmarks, Tests): NOT SET

### NoWarn Settings per Project
| Project | Extra NoWarn (beyond Directory.Build.props) |
|---------|---------------------------------------------|
| 16 plugin projects | CS1591 (missing XML doc comments) |
| KubernetesCsi | CS1591, CS8619 |
| Tests | xUnit1051, xUnit1031, xUnit2002, xUnit2031 |

### GenerateDocumentationFile State
- 20 plugin projects have `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
- SDK, Shared, CLI, Kernel, etc. do NOT have it
- BUILD-04 requires XML doc enforcement on SDK public APIs

### Current Build Warnings (Verified Feb 13, 2026)
| Warning | Project | Category | Fix |
|---------|---------|----------|-----|
| NU1608 | Tests, UltimateKeyManagement | Nethereum.JsonRpc.Client requires M.E.L.Abstractions <10.0 but 10.0.3 resolved | Update Nethereum or suppress NU1608 for NuGet-sourced |
| NU1608 | Tests, UltimateStorage | AWSSDK.SecurityToken 3.x requires AWSSDK.Core <4.0 but 4.0.x resolved | Update AWSSDK.SecurityToken to 4.x |
| NU1510 | UltimateDatabaseProtocol | System.Data.Common unnecessary (pruning) | Remove PackageReference |
| NU1603 | UltimateDatabaseProtocol | MySqlConnector >=2.4.1 not found, 2.5.0 resolved | Pin to 2.5.0 |
| NU1603 | UltimateDatabaseProtocol | Neo4j.Driver >=5.30.0 not found, 6.0.0 resolved | Pin to 6.0.0 |
| NU1510 | UltimateKeyManagement | System.Net.Http.Json unnecessary | Remove PackageReference |
| NU1510 | UniversalDashboards | System.Net.WebSockets.Client unnecessary | Remove PackageReference |
| NU1510 | UniversalDashboards | System.Net.Http.Json unnecessary | Remove PackageReference |

**Total unique warnings: 10 (all NuGet-sourced, zero code warnings)**

### Floating Version Ranges (Must Pin - SUPPLY-02)
43+ PackageReferences use floating ranges (`*` wildcards). Major offenders:
- **UltimateStorage**: 11 floating (Google.Cloud.Storage.V1 4.14.*, Npgsql 10.*, FluentFTP 53.*, etc.)
- **UltimateDatabaseStorage**: 13 floating (Npgsql 10.*, MySqlConnector 2.5.*, CassandraCSharpDriver 3.22.*, etc.)
- **UltimateMultiCloud**: 7 floating (Azure.ResourceManager 1.*, Azure.Identity 1.*, Google.Cloud.Storage.V1 4.*, etc.)
- **UltimateCompression**: 3 floating (K4os.Compression.LZ4 1.3.*, ZstdSharp.Port 0.8.*)
- **UltimateKeyManagement**: 2 floating (AWSSDK.CloudHSMV2 4.0.*, Konscious.Security.Cryptography.Argon2 1.3.*)
- **AedsCore**: 2 floating (Grpc.Net.Client 2.*, Google.Protobuf 3.*)
- **UltimateAccessControl**: 1 floating (SixLabors.ImageSharp 3.1.*)
- **UniversalObservability**: 2 floating (Prometheus-net 8.2.*, StatsdClient 3.0.*)
- **UltimateDataTransit**: 1 floating (Grpc.Net.Client 2.*)

### Pragma Warning Disable Usage
- 27 total occurrences across 18 files
- Most are in plugin code (FuseDriver, AdaptiveTransport, MultiCloud, AedsCore, various strategies)
- 2 in planning docs (not code)
- Low count relative to 1.1M LOC -- not a major concern

### NuGet Vulnerability Status
- SDK: **Zero known vulnerabilities** (verified)
- Full solution: Not yet scanned (would require full restore of all 69 projects)

## Architecture Patterns

### Pattern 1: Centralized Build Configuration via Directory.Build.props
**What:** All build settings, analyzer references, and common properties in a single Directory.Build.props at solution root. Projects only override when necessary.
**When to use:** Always. This is the standard .NET approach for multi-project solutions.
**Why it matters:** With 69 projects, per-project configuration is unmaintainable. Directory.Build.props is automatically imported by ALL projects in the directory tree.

```xml
<!-- Directory.Build.props - centralized configuration -->
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <AnalysisMode>All</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS0618;CS0649;CS0219;CS0414;CS0169;CS0162;CS0067;CS0168;CA1416;CA1418;CA2024</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <!-- Analyzers: build-time only, not shipped -->
    <PackageReference Include="..." />
  </ItemGroup>
</Project>
```

**Critical consideration:** When `TreatWarningsAsErrors` is moved to Directory.Build.props, the 12 plugins that already set it individually can have that line removed (it's redundant). BUT -- all new analyzer warnings from SonarAnalyzer/Roslynator/SecurityCodeScan will become errors. This requires a phased approach:
1. First add analyzers WITHOUT TreatWarningsAsErrors
2. Discover and fix/suppress new warnings
3. Then enable TreatWarningsAsErrors

### Pattern 2: NuGet Warning Suppression for Known NuGet Issues
**What:** Suppress NuGet warnings that are caused by third-party package dependency conflicts, not by our code.
**When to use:** When NU1608/NU1603 warnings come from upstream packages we cannot control.

```xml
<!-- In Directory.Build.props, add to NoWarn -->
<NoWarn>$(NoWarn);NU1608;NU1603;NU1510</NoWarn>
```

**Alternative (preferred):** Fix the root causes first:
- Remove unnecessary packages (NU1510)
- Pin to correct versions (NU1603)
- Update dependencies with constraint violations (NU1608)
- Only suppress if truly unfixable

### Pattern 3: CLI SetAction Migration Pattern
**What:** Replace deprecated NamingConventionBinder `CommandHandler.Create` with stable System.CommandLine 2.0 `SetAction` API.
**When to use:** All command handler definitions in DataWarehouse.CLI/Program.cs.

**Before (deprecated):**
```csharp
using System.CommandLine.NamingConventionBinder;

command.Handler = CommandHandler.Create<InvocationContext>(async (context) =>
{
    var value = context.ParseResult.GetValueForOption(someOption);
    // ...
});
```

**After (stable 2.0):**
```csharp
// No NamingConventionBinder import needed
command.SetAction(async (ParseResult parseResult, CancellationToken token) =>
{
    var value = parseResult.GetValue<int>("--optionName");
    // ...
});
```

**Key API changes (from beta4 to 2.0 stable):**
| Old (NamingConventionBinder) | New (2.0 Stable) |
|------------------------------|-------------------|
| `command.Handler = CommandHandler.Create(...)` | `command.SetAction(...)` |
| `rootCommand.Invoke(args)` | `rootCommand.Parse(args)` then `parseResult.InvokeAsync()` |
| `context.ParseResult.GetValueForOption(opt)` | `parseResult.GetValue<T>("--name")` |
| `context.ParseResult.GetValueForArgument(arg)` | `parseResult.GetValue<T>("argName")` |
| `option.AddAlias("-v")` | `option.Aliases.Add("-v")` |
| `rootCommand.Add(subcommand)` | `rootCommand.Subcommands.Add(subcommand)` |
| `rootCommand.Add(option)` | `rootCommand.Options.Add(option)` |
| `command.Add(arg)` | `command.Arguments.Add(arg)` |
| `InvocationContext` parameter | `ParseResult` + `CancellationToken` parameters |

**Source:** [Microsoft migration guide](https://learn.microsoft.com/en-us/dotnet/standard/commandline/migration-guide-2.0.0-beta5) (verified Feb 13, 2026)

### Anti-Patterns to Avoid
- **Per-project analyzer configuration:** Don't add analyzer PackageReferences to individual .csproj files. Use Directory.Build.props.
- **Global TreatWarningsAsErrors day-one without discovery:** Don't enable TreatWarningsAsErrors before knowing what new analyzers will flag. Run a discovery build first.
- **Suppressing all NuGet warnings globally:** Fix root causes. Only suppress truly unfixable NuGet-sourced warnings.
- **BannedSymbols.txt that's too aggressive:** Start with clearly dangerous APIs (broken crypto, deprecated memory). Don't ban `File.WriteAllText` on day one -- it would affect hundreds of files.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Security vulnerability detection | Custom code review checklist | SecurityCodeScan.VS2019 + BannedApiAnalyzers | Automated, catches OWASP Top 10 on every build |
| Code quality rules | Manual style guide enforcement | SonarAnalyzer.CSharp + Roslynator | 700+ rules, automated, consistent |
| SBOM generation | Manual dependency inventory | CycloneDX 6.0.0 dotnet tool | Handles transitive deps, standard format |
| Version pinning | Manual .csproj edits | `dotnet list package` + scripted updates | Enumerate all floating ranges programmatically |
| CLI argument parsing | Custom reflection-based binding | System.CommandLine 2.0.3 SetAction | Type-safe, async-aware, CancellationToken support |

## Common Pitfalls

### Pitfall 1: Analyzer Storm When Enabling Multiple Analyzers Simultaneously
**What goes wrong:** Adding 4 analyzer packages + TreatWarningsAsErrors at once produces hundreds of new errors, build completely broken.
**Why it happens:** SonarAnalyzer alone has 500+ rules. Roslynator adds 200+. Combined with existing code patterns, expect 50-200 new diagnostics.
**How to avoid:**
1. Add analyzers first WITHOUT TreatWarningsAsErrors
2. Build solution to discover all new warnings
3. Categorize: fix vs suppress vs configure severity
4. Fix/suppress incrementally
5. Only THEN enable TreatWarningsAsErrors
**Warning signs:** Build time increases dramatically (normal with analyzers -- expect 2-5x slower first build)

### Pitfall 2: SecurityCodeScan.VS2019 Compatibility with .NET 10
**What goes wrong:** SecurityCodeScan.VS2019 5.6.7 was last updated September 2022. It may produce false positives or fail to load on .NET 10's Roslyn version.
**Why it happens:** The analyzer was built against an older Roslyn API surface. Newer Roslyn versions may have breaking changes.
**How to avoid:** Add SecurityCodeScan first and build. If it produces RS-prefixed errors or fails to load, remove it and document the gap. Consider Meziantou.Analyzer as a replacement.
**Warning signs:** `warning AD0001: Analyzer 'SecurityCodeScan...' threw an exception` in build output

### Pitfall 3: NoWarn in Directory.Build.props Masking Real Issues
**What goes wrong:** The existing NoWarn list (CS0618, CS0649, CS0219, CS0414, CS0169, CS0162, CS0067, CS0168) suppresses warnings that may indicate real bugs.
**Why it happens:** These were added to silence warnings in a plugin-based architecture. But CS0618 (obsolete API usage) and CS0168 (unused catch variables) can hide real issues.
**How to avoid:** Review the NoWarn list during this phase. Consider narrowing scope:
- CS0618 (obsolete): Keep for now but flag for Phase 27 review
- CS0168 (unused catch var): Can be fixed with discard pattern `catch (Exception _)`
- CS0649 (unassigned field): May indicate initialization bugs in plugins
**Warning signs:** Suppressed warnings count grows without review

### Pitfall 4: Floating Versions Causing Non-Reproducible Builds
**What goes wrong:** 43+ floating PackageReferences mean builds on different dates resolve different versions. A developer's local build works but CI fails (or vice versa).
**Why it happens:** Using `Version="10.*"` instead of `Version="10.0.1"`.
**How to avoid:** Pin ALL versions to exact values. Run `dotnet restore` to discover current resolved versions, then pin to those. Consider `RestorePackagesWithLockFile` in Directory.Build.props for extra reproducibility.
**Warning signs:** "It works on my machine" issues, intermittent CI failures

### Pitfall 5: CLI NamingConventionBinder Migration Breaking Command Parsing
**What goes wrong:** After removing NamingConventionBinder, commands that relied on convention-based parameter matching silently fail to bind values.
**Why it happens:** NamingConventionBinder matched parameters by name convention. The new SetAction API requires explicit `parseResult.GetValue<T>("--name")` calls.
**How to avoid:** Migrate one command at a time. Test each command with representative arguments. The `CreateSubCommand` helper in Program.cs handles most commands -- migrating this single method covers ~30 subcommands.
**Warning signs:** Command handlers receive default/null values for parameters that should have been populated

### Pitfall 6: BannedSymbols.txt Breaking Existing Code
**What goes wrong:** A too-aggressive BannedSymbols.txt produces hundreds of errors in plugin code that legitimately uses the banned APIs.
**Why it happens:** Banning `File.WriteAllText` or `new List<T>()` affects too much existing code.
**How to avoid:** Start with a minimal BannedSymbols.txt targeting only clearly dangerous APIs:
- Deprecated crypto (MD5, SHA1, DES, TripleDES)
- SecureString
- Leave file I/O and collection APIs for a later phase
**Warning signs:** More than 20 BannedApiAnalyzer errors on first build

## Code Examples

### Example 1: Updated Directory.Build.props (Target State)
```xml
<!-- Source: Pattern based on Microsoft official docs for .NET 10 SDK-style projects -->
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- Code Analysis -->
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <AnalysisMode>All</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>

    <!-- Warning Enforcement -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

    <!-- Suppressions: intentional in plugin architecture + NuGet-sourced -->
    <NoWarn>$(NoWarn);CS0618;CS0649;CS0219;CS0414;CS0169;CS0162;CS0067;CS0168;CA1416;CA1418;CA2024;NU1608;NU1603;NU1510</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <!-- Analyzers: build-time only, NOT shipped with output -->
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.3.4">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="SecurityCodeScan.VS2019" Version="5.6.7">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="SonarAnalyzer.CSharp" Version="10.19.0.132793">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Roslynator.Analyzers" Version="4.15.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

### Example 2: Minimal BannedSymbols.txt
```text
# BannedSymbols.txt - DataWarehouse SDK
# Phase 22: Start minimal, expand in future phases

# Deprecated Cryptographic Algorithms (broken, must not use)
T:System.Security.Cryptography.MD5; Use SHA256 or higher
T:System.Security.Cryptography.SHA1; Use SHA256 or higher
T:System.Security.Cryptography.DES; Use AES-256
T:System.Security.Cryptography.TripleDES; Use AES-256

# Deprecated Security Types
T:System.Security.SecureString; Deprecated in .NET Core - use CryptographicOperations.ZeroMemory
```

### Example 3: CLI SetAction Migration (CreateSubCommand helper)
```csharp
// BEFORE (deprecated NamingConventionBinder):
command.Handler = CommandHandler.Create<InvocationContext>(async (context) =>
{
    var parameters = new Dictionary<string, object?>();
    foreach (var arg in arguments)
    {
        var value = context.ParseResult.GetValueForArgument(arg);
        if (value != null) parameters[arg.Name] = value;
    }
    foreach (var opt in options)
    {
        var value = context.ParseResult.GetValueForOption(opt);
        if (value != null)
        {
            var optName = opt.Name.TrimStart('-').Replace("-", "");
            parameters[optName] = value;
        }
    }
    var format = context.ParseResult.GetValueForOption(formatOption);
    var result = await _executor!.ExecuteAsync(sharedCommandName, parameters);
    _renderer.Render(result, format);
    _history?.Add(sharedCommandName, parameters, result.Success);
    context.ExitCode = result.ExitCode;
});

// AFTER (stable System.CommandLine 2.0.3):
command.SetAction(async (ParseResult parseResult, CancellationToken token) =>
{
    var parameters = new Dictionary<string, object?>();
    foreach (var arg in arguments)
    {
        var value = parseResult.GetValue(arg);
        if (value != null) parameters[arg.Name] = value;
    }
    foreach (var opt in options)
    {
        var value = parseResult.GetValue(opt);
        if (value != null)
        {
            var optName = opt.Name.TrimStart('-').Replace("-", "");
            parameters[optName] = value;
        }
    }
    var format = parseResult.GetValue(formatOption);
    var result = await _executor!.ExecuteAsync(sharedCommandName, parameters);
    _renderer.Render(result, format);
    _history?.Add(sharedCommandName, parameters, result.Success);
    return result.ExitCode;
});
```

### Example 4: SBOM Generation with CycloneDX
```bash
# Install as global tool
dotnet tool install --global CycloneDX --version 6.0.0

# Generate SBOM for entire solution
dotnet CycloneDX DataWarehouse.slnx -o ./artifacts/sbom -f sbom.json --json

# Generate SBOM for SDK only
dotnet CycloneDX DataWarehouse.SDK/DataWarehouse.SDK.csproj -o ./artifacts/sbom -f sdk-sbom.json --json
```

### Example 5: Version Pinning Discovery Script
```bash
# List all packages with resolved versions for the full solution
dotnet list DataWarehouse.slnx package --format json > packages.json

# Check for floating versions in all .csproj files
grep -rn 'Version="[0-9]*\.\*\|Version="[0-9]*\.[0-9]*\.\*' --include="*.csproj" .
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| System.CommandLine.NamingConventionBinder | System.CommandLine 2.0 SetAction API | Feb 2026 (2.0 GA) | Must migrate -- NamingConventionBinder deprecated |
| CommandHandler.Create<T>(...) | command.SetAction(parseResult => ...) | 2.0.0-beta5+ | New handler pattern, explicit ParseResult |
| rootCommand.Invoke(args) | rootCommand.Parse(args) then .Invoke() | 2.0.0-beta5+ | Separated parsing from invocation |
| option.AddAlias("-v") | option.Aliases.Add("-v") | 2.0.0-beta5+ | Mutable collections pattern |
| rootCommand.Add(cmd) | rootCommand.Subcommands.Add(cmd) | 2.0.0-beta5+ | Explicit collection properties |
| InvocationContext | ParseResult + CancellationToken | 2.0.0-beta5+ | InvocationContext removed entirely |
| BannedApiAnalyzers 3.3.4 (stable) | 5.0.0 (prerelease) | Upcoming | Use 3.3.4 stable for now |

**Deprecated/outdated:**
- `System.CommandLine.NamingConventionBinder`: Never reached stable. Deprecated legacy package.
- `SecurityCodeScan.VS2019`: Last updated September 2022. Functional but unmaintained. Monitor for .NET 10 issues.
- `BannedApiAnalyzers 5.0.0`: The prior research (STACK.md) referenced version 5.0.0 but this is prerelease only. Use 3.3.4 stable.

## Impact Assessment

### Project Counts
| Category | Count | TreatWarningsAsErrors | GenerateDocFile |
|----------|-------|----------------------|-----------------|
| SDK | 1 | NO | NO |
| Shared | 1 | NO | NO |
| CLI | 1 | NO | NO |
| Kernel | 1 | NO | NO |
| Launcher | 1 | NO | NO |
| GUI | 1 | NO | NO |
| Dashboard | 1 | NO | NO |
| Benchmarks | 1 | NO | NO |
| Tests | 1 | NO | NO |
| Plugins (with TWAE) | 12 | YES (individual) | YES (most) |
| Plugins (without TWAE) | 48 | NO | SOME |
| **Total** | **69** | **12/69** | **20/69** |

### CLI Migration Scope
- **File:** DataWarehouse.CLI/Program.cs (642 lines)
- **Handler assignments:** 8 uses of `CommandHandler.Create`
- **Critical helper:** `CreateSubCommand` (line 575-639) -- migrating this covers ~30 subcommands automatically
- **Other handlers:** rootCommand (welcome), bash/zsh/fish/pwsh completions, interactive, connect
- **API changes needed:** AddAlias -> Aliases.Add, .Add(cmd) -> .Subcommands.Add(cmd), Invoke -> Parse+Invoke
- **Import to remove:** `using System.CommandLine.NamingConventionBinder;`
- **Import to remove:** `using System.CommandLine.Invocation;` (InvocationContext gone)
- **Risk:** LOW -- all handlers follow the same pattern, most are thin wrappers to Shared services

## Open Questions

1. **SecurityCodeScan.VS2019 on .NET 10**
   - What we know: Last updated Sep 2022, may not have been tested with .NET 10's Roslyn version
   - What's unclear: Whether it produces AD0001 analyzer load failures on .NET 10
   - Recommendation: Add it and test. If it fails, remove and document. Meziantou.Analyzer (actively maintained) is the backup.

2. **NoWarn List Review**
   - What we know: 11 warning codes suppressed globally in Directory.Build.props
   - What's unclear: How many of these suppress real bugs vs intentional architecture patterns
   - Recommendation: Keep existing suppressions for Phase 22. Schedule review for Phase 27 when plugins are being migrated.

3. **SBOM Format Choice**
   - What we know: REQUIREMENTS.md says "CycloneDX or SPDX". CycloneDX 6.0.0 is actively maintained and supports .slnx. Microsoft.Sbom.DotNetTool 4.1.5 generates SPDX.
   - What's unclear: Whether government/compliance needs require SPDX specifically
   - Recommendation: Use CycloneDX for developer workflow (lighter, better .NET integration). Add SPDX via Microsoft tool if compliance requires it later.

4. **System.CommandLine 2.0.3 .Add() Backward Compatibility**
   - What we know: Migration guide says `.Add(subcommand)` changed to `.Subcommands.Add(subcommand)`
   - What's unclear: Whether 2.0.3 still supports the old `.Add()` extension method for backward compat
   - Recommendation: Since the project already has System.CommandLine 2.0.3, verify at migration time. The current code compiles, suggesting `.Add()` may still work as extension methods. Migrate to canonical pattern regardless.

## Sources

### Primary (HIGH confidence)
- Directory.Build.props, DataWarehouse.SDK.csproj, DataWarehouse.CLI.csproj, etc. -- direct file reads from codebase
- `dotnet build DataWarehouse.slnx --no-restore` -- actual build output, Feb 13, 2026
- `dotnet list package --vulnerable` -- actual vulnerability scan, Feb 13, 2026
- [System.CommandLine migration guide](https://learn.microsoft.com/en-us/dotnet/standard/commandline/migration-guide-2.0.0-beta5) -- Microsoft official, updated Dec 2025
- [NuGet Gallery: System.CommandLine 2.0.3](https://www.nuget.org/packages/System.CommandLine) -- stable release Feb 10, 2026
- [NuGet Gallery: SonarAnalyzer.CSharp 10.19.0](https://www.nuget.org/packages/sonaranalyzer.csharp/) -- latest Jan 30, 2026
- [NuGet Gallery: Roslynator.Analyzers 4.15.0](https://www.nuget.org/packages/roslynator.analyzers/) -- latest stable
- [NuGet Gallery: Microsoft.Sbom.DotNetTool 4.1.5](https://www.nuget.org/packages/Microsoft.Sbom.DotNetTool) -- Microsoft official SBOM tool

### Secondary (MEDIUM confidence)
- [NuGet Gallery: CycloneDX 6.0.0](https://www.nuget.org/packages/CycloneDX/) -- current stable
- [NuGet Gallery: BannedApiAnalyzers 3.3.4](https://www.nuget.org/packages/Microsoft.CodeAnalysis.BannedApiAnalyzers) -- latest stable (NOT 5.0.0 which is prerelease)
- [CycloneDX dotnet GitHub](https://github.com/CycloneDX/cyclonedx-dotnet) -- supports .slnx

### Tertiary (LOW confidence)
- [NuGet Gallery: SecurityCodeScan.VS2019 5.6.7](https://www.nuget.org/packages/SecurityCodeScan.VS2019/) -- last updated Sep 5, 2022. May have .NET 10 compatibility issues.
- Prior research STACK.md (from .planning/research/) -- referenced BannedApiAnalyzers 5.0.0 which is prerelease only; corrected to 3.3.4 stable.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - all packages verified on NuGet with current versions, except SecurityCodeScan (MEDIUM due to age)
- Architecture: HIGH - Directory.Build.props pattern is standard .NET, CLI migration guide is official Microsoft docs
- Pitfalls: HIGH - based on direct codebase investigation showing actual warning counts, floating versions, and NamingConventionBinder usage
- Supply chain: HIGH - vulnerability scan ran, floating versions enumerated, SBOM tools verified

**Research date:** 2026-02-13
**Valid until:** 2026-03-15 (stable domain, 30-day validity)
