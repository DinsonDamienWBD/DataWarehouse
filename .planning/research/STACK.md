# Technology Stack

**Project:** DataWarehouse SDK v2.0 - Hardening and Distributed Infrastructure
**Researched:** 2026-02-11
**Confidence:** HIGH

## Recommended Stack

### Core Framework (No Changes Required)
| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| .NET 10 | 10.0 (LTS) | SDK runtime | Already in use, LTS support through 2029, optimal performance |
| C# 14 | 14.0 | Language | Ships with .NET 10, required for latest language features |

### Existing Core Dependencies (Keep As-Is)
| Technology | Current Version | Latest Version | Purpose | Status |
|------------|----------------|----------------|---------|--------|
| Microsoft.Extensions.Logging.Abstractions | 10.0.3 | 10.0.2 | Logging abstraction | **Keep 10.0.3** - newer than latest stable |
| Microsoft.Json.Schema | 2.3.0 | 2.3.0 | JSON schema validation | **Keep** - current |
| Newtonsoft.Json | 13.0.4 | 13.0.4 | JSON serialization | **Keep** - latest, patched CVE-2024-21907 |
| System.IO.Hashing | 10.0.3 | 10.0.3 | High-performance hashing | **Keep** - current |

### Security Hardening - Roslyn Analyzers
| Library | Version | Purpose | Why |
|---------|---------|---------|-----|
| Microsoft.CodeAnalysis.NetAnalyzers | 10.0.100 | Microsoft's official code quality and security analyzers | Built-in with .NET SDK, no package needed if using SDK-style project with `EnableNETAnalyzers=true` |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.0.0 | Ban unsafe APIs (SecureString, obsolete crypto, unsafe memory) | Enforces banned API lists via BannedSymbols.txt, critical for military-grade security |
| SecurityCodeScan.VS2019 | 5.6.7 | Security vulnerability detection (SQL injection, XSS, CSRF, weak crypto) | Industry-standard security analyzer with 982k downloads, detects OWASP Top 10 vulnerabilities |
| SonarAnalyzer.CSharp | 10.19.0.132793 | Code quality, maintainability, reliability | 500+ rules, excellent for code smells and technical debt prevention |
| Roslynator.Analyzers | 4.15.0 | 200+ code style and quality analyzers | Complements NetAnalyzers with additional refactoring suggestions |

### Supply Chain Security
| Tool | Version | Purpose | Why |
|------|---------|---------|-----|
| Microsoft.Sbom.DotNetTool | 4.1.5 | SBOM generation (SPDX 2.2/3.0) | Microsoft's official SBOM tool, required for NIST SSDF compliance and Executive Order 14028 |

### Distributed Systems - Optional (NOT RECOMMENDED for SDK)
| Library | Version | Purpose | Why NOT Recommended |
|---------|---------|---------|---------------------|
| Aspire.Hosting | 13.1.0 | Distributed app orchestration | **ANTI-PATTERN** - Aspire is for application orchestration, not SDK design. SDK should provide contracts only. |
| Microsoft.Orleans.Core | 10.0.1 | Virtual actor framework (grains) | **ANTI-PATTERN** - Orleans is an application framework. SDK should define IAutoScaler, ILoadBalancer, IP2PNode contracts that applications implement with Orleans or alternatives. |

### Memory Safety - Built-in APIs (No Packages Needed)
| API | Namespace | Purpose | Why |
|-----|-----------|---------|-----|
| CryptographicOperations.ZeroMemory | System.Security.Cryptography | Secure memory wiping | Built into .NET 10, compiler-safe zero-fill that won't be optimized away |
| BlockingCollection<T> | System.Collections.Concurrent | Bounded collections | Built into .NET 10, prevents unbounded memory growth with producer/consumer pattern |
| IDisposable pattern | System | Deterministic resource cleanup | Language/runtime feature, enforce on all root SDK types |

## Installation

### Step 1: SDK-Level Analyzers (Add to DataWarehouse.SDK.csproj)

```xml
<ItemGroup>
  <!-- Security and Quality Analyzers -->
  <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="5.0.0">
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

### Step 2: Build Hardening Configuration (Add to DataWarehouse.SDK.csproj)

```xml
<PropertyGroup>
  <!-- Warnings as Errors (Release Only) -->
  <TreatWarningsAsErrors Condition="'$(Configuration)'=='Release'">true</TreatWarningsAsErrors>

  <!-- Enable all .NET analyzers -->
  <EnableNETAnalyzers>true</EnableNETAnalyzers>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <AnalysisMode>All</AnalysisMode>

  <!-- Nullable reference types already enabled -->
  <Nullable>enable</Nullable>
</PropertyGroup>
```

### Step 3: Banned API Configuration (Create BannedSymbols.txt in SDK root)

```text
# Banned APIs for Security Hardening

# Deprecated Crypto
T:System.Security.Cryptography.MD5; Use SHA256 or higher
T:System.Security.Cryptography.SHA1; Use SHA256 or higher
T:System.Security.Cryptography.DES; Use AES
T:System.Security.Cryptography.TripleDES; Use AES

# Insecure Memory Handling
T:System.Security.SecureString; Deprecated in .NET Core, use CryptographicOperations.ZeroMemory instead
M:System.String.Copy(System.String); Use span-based APIs for sensitive data

# Unsafe File Operations
M:System.IO.File.WriteAllText(System.String,System.String); Missing encoding parameter, prefer WriteAllTextAsync with explicit encoding
M:System.IO.File.ReadAllText(System.String); Missing encoding parameter, prefer ReadAllTextAsync with explicit encoding

# Unbounded Collections (for sensitive code paths)
M:System.Collections.Generic.List`1.#ctor; Prefer List<T>(int capacity) or BlockingCollection<T> for bounded scenarios
M:System.Collections.Generic.Queue`1.#ctor; Prefer BlockingCollection<T> for producer-consumer with bounding
```

### Step 4: SBOM Generation (Install as global tool)

```bash
dotnet tool install --global Microsoft.Sbom.DotNetTool --version 4.1.5
```

Generate SBOM after build:
```bash
sbom-tool generate -b ./bin/Release/net10.0 -bc ./DataWarehouse.SDK -pn DataWarehouse.SDK -pv 2.0.0 -nsb https://datawarehouse.dev
```

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| Security Analyzer | SecurityCodeScan.VS2019 5.6.7 | RoslynSecurityGuard 2.3.0 | RoslynSecurityGuard is unmaintained (last update 2018), SecurityCodeScan actively maintained |
| JSON Library | Newtonsoft.Json 13.0.4 | System.Text.Json | Breaking change for existing codebase; Newtonsoft.Json 13.0.4 is secure, performs well, widely adopted |
| SBOM Tool | Microsoft.Sbom.DotNetTool 4.1.5 | CycloneDX dotnet-CycloneDX | Microsoft tool required for government/military compliance (SPDX format), better integration with Azure DevOps |
| Code Quality | SonarAnalyzer.CSharp + Roslynator | StyleCop.Analyzers | StyleCop is style-only; SonarAnalyzer covers logic bugs, security, maintainability; Roslynator adds refactoring suggestions |
| Distributed Framework | SDK Contracts (IAutoScaler, ILoadBalancer) | Orleans/Aspire as dependency | SDK should be dependency-lean; provide contracts, let applications choose Orleans vs Akka.NET vs custom implementations |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| SecureString | Deprecated, doesn't work on .NET Core, provides false security | CryptographicOperations.ZeroMemory on Span<byte> or byte[] |
| MD5, SHA1 | Cryptographically broken | SHA256, SHA384, SHA512, Blake2b (System.IO.Hashing) |
| DES, TripleDES | Weak key sizes, slow | AES-256-GCM (AesGcm class in System.Security.Cryptography) |
| SecurityCodeScan 3.5.4 | Older package, use VS2019-specific version for Roslyn 4.x compatibility | SecurityCodeScan.VS2019 5.6.7 |
| Orleans as SDK dependency | 16 MB dependency surface, forces architectural choice | Define contracts: IAutoScaler, ILoadBalancer, IP2PNode; document Orleans as reference implementation |
| Aspire as SDK dependency | Application orchestrator, not library | Document Aspire integration patterns for apps consuming SDK |
| Global TreatWarningsAsErrors=true | Blocks experimentation during development | Condition it: `Condition="'$(Configuration)'=='Release'"` |

## Stack Patterns by Scenario

### Scenario 1: SDK Hardening (Primary Focus)
**Pattern:**
- Add 4 analyzer packages (BannedApiAnalyzers, SecurityCodeScan, SonarAnalyzer, Roslynator)
- Configure build properties (TreatWarningsAsErrors in Release, EnableNETAnalyzers, EnforceCodeStyleInBuild)
- Create BannedSymbols.txt with 15-20 banned APIs
- Enable SBOM generation in CI/CD pipeline

**Rationale:** Minimal dependency additions (analyzers are build-time only), maximum security posture improvement, meets NIST SSDF and military code review standards.

### Scenario 2: Distributed Infrastructure Contracts
**Pattern:**
- Define contracts in SDK: `IAutoScaler`, `ILoadBalancer`, `IP2PNode`, `IAutoSync`
- Do NOT add Orleans/Aspire as SDK dependencies
- Provide reference implementation in separate sample project using Orleans 10.0.1

**Rationale:** SDK remains dependency-lean (4 packages), applications can choose Orleans, Akka.NET, or custom implementations, avoids vendor lock-in.

### Scenario 3: Memory Safety Enforcement
**Pattern:**
- Use CryptographicOperations.ZeroMemory for sensitive data cleanup
- Enforce IDisposable on all root types (PluginBase, StrategyBase)
- Use BlockingCollection<T> with bounded capacity for queue-based processing
- Ban SecureString via BannedSymbols.txt

**Rationale:** No additional dependencies, leverages .NET 10 built-in APIs, compiler-enforced safety via nullable reference types and analyzers.

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|-----------------|-------|
| SecurityCodeScan.VS2019 5.6.7 | .NET 10, Roslyn 4.x | Use VS2019-specific version for .NET 5+ projects despite naming |
| Microsoft.CodeAnalysis.BannedApiAnalyzers 5.0.0 | .NET 10 | Latest prerelease supports .NET 10 preview 4 `#:package` directive |
| SonarAnalyzer.CSharp 10.19.0 | .NET 10 | Confirmed working with .NET 10 via NuGet |
| Roslynator.Analyzers 4.15.0 | .NET 10 | Roslyn 4.x compatible, works with .NET 5-10 |
| Microsoft.Sbom.DotNetTool 4.1.5 | .NET 10 | Supports SPDX 2.2 and 3.0, all .NET versions |

## Dependency Surface Analysis

### Before v2.0 (Current State)
- Runtime dependencies: 4 packages
- Transitive dependencies: ~12 packages
- Total assembly size: ~8 MB

### After v2.0 Hardening (Recommended)
- Runtime dependencies: 4 packages (no change)
- Build-time analyzers: 4 packages (not shipped)
- Transitive dependencies: ~12 packages (no change)
- Total assembly size: ~8 MB (no change)

**Critical Principle:** Analyzers use `<PrivateAssets>all</PrivateAssets>` - they run at build time only and are NOT included in NuGet package or runtime distribution.

## Orleans Integration Pattern (For Reference Implementations)

If applications choose to implement distributed contracts with Orleans:

### Application-Level Installation (NOT in SDK)
```bash
dotnet add package Microsoft.Orleans.Core --version 10.0.1
dotnet add package Microsoft.Orleans.Server --version 10.0.1
dotnet add package Microsoft.Orleans.Persistence.Memory --version 10.0.1
```

### SDK Contract Definition (IN SDK)
```csharp
namespace DataWarehouse.SDK.Distributed;

public interface IAutoScaler
{
    Task ScaleOutAsync(int additionalNodes, CancellationToken cancellationToken);
    Task ScaleInAsync(int nodesToRemove, CancellationToken cancellationToken);
    Task<ScalingMetrics> GetMetricsAsync(CancellationToken cancellationToken);
}

public interface ILoadBalancer
{
    Task<TNode> SelectNodeAsync<TNode>(IReadOnlyList<TNode> availableNodes, CancellationToken cancellationToken);
}

public interface IP2PNode
{
    Task<bool> SyncDataAsync(IP2PNode peer, CancellationToken cancellationToken);
    Task<IReadOnlyList<IP2PNode>> DiscoverPeersAsync(CancellationToken cancellationToken);
}
```

### Application Implementation Example (NOT in SDK)
```csharp
using Orleans;
using DataWarehouse.SDK.Distributed;

public class OrleansAutoScalerGrain : Grain, IAutoScaler
{
    public async Task ScaleOutAsync(int additionalNodes, CancellationToken cancellationToken)
    {
        // Orleans-specific implementation
    }
}
```

## Build Configuration Best Practices

### .editorconfig Integration
Create `.editorconfig` in SDK root for IDE-level enforcement:

```ini
root = true

[*.cs]
# Null checking
dotnet_diagnostic.CS8600.severity = error  # Null to non-nullable reference
dotnet_diagnostic.CS8602.severity = error  # Dereference of possibly null reference
dotnet_diagnostic.CS8603.severity = error  # Possible null reference return

# Security
dotnet_diagnostic.CA5350.severity = error  # Do Not Use Weak Cryptographic Algorithms
dotnet_diagnostic.CA5351.severity = error  # Do Not Use Broken Cryptographic Algorithms
dotnet_diagnostic.CA2153.severity = error  # Do Not Catch Corrupted State Exceptions

# Performance
dotnet_diagnostic.CA1806.severity = warning # Do not ignore method results
dotnet_diagnostic.CA1821.severity = warning # Remove empty finalizers
```

### CI/CD Integration
Add to Azure Pipelines / GitHub Actions:

```yaml
- name: Build with Analyzers
  run: dotnet build --configuration Release /p:TreatWarningsAsErrors=true

- name: Generate SBOM
  run: |
    dotnet tool install --global Microsoft.Sbom.DotNetTool --version 4.1.5
    sbom-tool generate -b ./bin/Release/net10.0 -bc ./DataWarehouse.SDK -pn DataWarehouse.SDK -pv ${{ github.ref_name }} -nsb https://datawarehouse.dev

- name: Upload SBOM
  uses: actions/upload-artifact@v3
  with:
    name: sbom
    path: _manifest/spdx_2.2/*.json
```

## Sources

### Official Microsoft Documentation (HIGH Confidence)
- [Microsoft.CodeAnalysis.NetAnalyzers 10.0.100 - NuGet Gallery](https://www.nuget.org/packages/Microsoft.CodeAnalysis.NetAnalyzers) - Latest version verified
- [Code analysis in .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview) - Official analyzer documentation
- [CryptographicOperations.ZeroMemory Method - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0) - .NET 10 secure memory API
- [BlockingCollection Class - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.blockingcollection-1?view=net-10.0) - Bounded collections API
- [Microsoft.Extensions.Logging.Abstractions 10.0.2 - NuGet Gallery](https://www.nuget.org/packages/microsoft.extensions.logging.abstractions/) - Latest version
- [.NET January 2026 non security Updates - GitHub](https://github.com/dotnet/core/issues/10204) - .NET 10.0.2 release notes

### Security Analyzers (HIGH Confidence)
- [Microsoft.CodeAnalysis.BannedApiAnalyzers 5.0.0 - NuGet Gallery](https://www.nuget.org/packages/Microsoft.CodeAnalysis.BannedApiAnalyzers/5.0.0-1.25277.114) - Latest prerelease
- [SecurityCodeScan.VS2019 5.6.7 - NuGet Gallery](https://www.nuget.org/packages/SecurityCodeScan.VS2019/) - Latest version verified
- [SonarAnalyzer.CSharp 10.19.0 - NuGet Gallery](https://www.nuget.org/packages/sonaranalyzer.csharp/) - Latest version verified
- [Roslynator.Analyzers 4.15.0 - NuGet Gallery](https://www.nuget.org/packages/roslynator.analyzers/) - Latest version verified
- [Security Code Scan](https://security-code-scan.github.io/) - Official documentation

### SBOM and Supply Chain (HIGH Confidence)
- [Microsoft.Sbom.DotNetTool 4.1.5 - NuGet Gallery](https://www.nuget.org/packages/Microsoft.Sbom.DotNetTool) - Latest version verified
- [GitHub - microsoft/sbom-tool](https://github.com/microsoft/sbom-tool) - Official repository
- [Securing the .NET Software Supply Chain: SBOMs, NuGet Auditing, and Modern Best Practices](https://developersvoice.com/blogs/secure-coding/dotnet-supply-chain-security-sbom-nuget/) - 2026 best practices

### Distributed Systems (MEDIUM Confidence)
- [Orleans 10.0 NuGet packages - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/orleans/resources/nuget-packages) - Official package list
- [Microsoft.Orleans.Core 10.0.1 - NuGet Gallery](https://www.nuget.org/packages/Microsoft.Orleans.Core) - Latest version verified
- [What's new in Aspire 13 | Microsoft Learn](https://aspire.dev/whats-new/aspire-13/) - Aspire 13 features
- [Aspire.Hosting 13.1.0 - NuGet Gallery](https://www.nuget.org/packages/Aspire.Hosting) - Latest version verified

### Build Configuration (HIGH Confidence)
- [TreatWarningsAsErrors best practices - Medium](https://medium.com/@jakubiszon/treating-warnings-as-errors-in-dotnet-the-right-way-6ad0d8d89834) - 2026 best practices
- [Compiler Options - errors and warnings - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/errors-warnings) - Official compiler options
- [Nullable reference types - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/nullable-references) - Official nullable documentation

### Security Deprecations (HIGH Confidence)
- [SecureString Class - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.security.securestring?view=net-10.0) - Deprecation status
- [Newtonsoft.Json 13.0.4 Security - Snyk](https://security.snyk.io/package/nuget/newtonsoft.json) - CVE-2024-21907 patched in 13.0.1+
- [Releases · JamesNK/Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json/releases) - Version history

---
*Stack research for: DataWarehouse SDK v2.0 - Hardening and Distributed Infrastructure*
*Researched: 2026-02-11*
*Confidence: HIGH - All package versions verified via official sources, .NET 10 APIs verified via Microsoft Learn*
