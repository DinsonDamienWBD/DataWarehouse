# Phase 16: Testing & Quality Assurance - Research

**Researched:** 2026-02-11
**Domain:** .NET Testing (xUnit v3, Coverlet, Moq, FluentAssertions), Security Penetration Testing (STRIDE, OWASP)
**Confidence:** HIGH

## Summary

Phase 16 covers two major tasks: T121 (Comprehensive Test Suite) and T122 (Security Penetration Test Plan). The project already has a test infrastructure with 845 test methods across 34 active test classes using xUnit v3 3.2.2, FluentAssertions 8.8.0, Moq 4.20.72, and Coverlet 6.0.4. Of these, 826 pass, 18 fail, and 1 is skipped. However, 17 test files are excluded from compilation via `<Compile Remove>` directives in the .csproj, and major plugin categories (Encryption, Compression, Intelligence, Interface, RAID, Replication) have zero dedicated test coverage.

The test project currently references only 4 projects: SDK, Kernel, UltimateAccessControl, and UltimateCompliance. To achieve the 80% coverage target for critical paths, references to additional Ultimate plugins are needed, along with shared test utilities (TestMessageBus, MockStorage helpers) that are currently duplicated or missing.

For T122, the TODO.md has a well-structured penetration test plan framework using STRIDE threat modeling and OWASP Top 10 verification. This is a documentation/planning task -- the deliverable is a formal pen test plan document, not automated test execution.

**Primary recommendation:** Focus on (1) fixing the 18 failing tests, (2) re-enabling the 17 excluded test files or replacing them with updated versions, (3) adding unit tests for the 6+ untested Ultimate plugin categories, (4) configuring Coverlet thresholds, and (5) writing the security penetration test plan document.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| xunit.v3 | 3.2.2 | Test framework | Already in project; v3 provides TestContext, CancellationToken support, improved parallelism |
| xunit.runner.visualstudio | 3.1.5 | Test runner for VS/CLI | Required for `dotnet test` discovery with xUnit v3 |
| Microsoft.NET.Test.Sdk | 18.0.1 | Test platform SDK | Required for `dotnet test` integration |
| FluentAssertions | 8.8.0 | Assertion library | Already in project; readable assertion syntax |
| Moq | 4.20.72 | Mocking framework | Already in project; interface mocking for plugin isolation |
| coverlet.collector | 6.0.4 | Code coverage | Already in project; integrates with `dotnet test --collect:"XPlat Code Coverage"` |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| ReportGenerator | (global tool) | Coverage report HTML | Convert Cobertura XML to readable HTML reports |
| coverlet.msbuild | 6.0.4 | MSBuild coverage integration | Alternative to collector; enables `/p:Threshold=80` enforcement |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Moq | NSubstitute | NSubstitute has cleaner syntax but Moq is already established in codebase |
| Stopwatch benchmarks | BenchmarkDotNet | BenchmarkDotNet is more rigorous but project already uses Stopwatch-based perf tests in TamperProof; consistency matters more |
| coverlet.collector | Microsoft.Testing.Platform Code Coverage | Microsoft's new platform is available but Coverlet is already integrated and well-documented |

**Installation (no new packages needed -- all already present):**
```bash
# Only if ReportGenerator is desired for coverage HTML reports:
dotnet tool install -g dotnet-reportgenerator-globaltool
```

## Architecture Patterns

### Existing Test Project Structure
```
DataWarehouse.Tests/
├── Compliance/          # ComplianceTestSuites.cs, DataSovereigntyEnforcerTests.cs (73 tests)
├── Dashboard/           # DashboardServiceTests.cs (27 tests)
├── Infrastructure/      # SDK strategy tests + encryption benchmarks (163 tests)
├── Security/            # CanaryStrategy, Ephemeral, Steg, Watermarking (148 tests)
├── Storage/             # Storage ops, InMemoryPlugin, StoragePool (51 tests)
├── TamperProof/         # 12 files: integrity, blockchain, WORM, pipelines (193 tests)
├── Database/            # EXCLUDED - 2 files
├── Hyperscale/          # EXCLUDED - 1 file
├── Integration/         # EXCLUDED - 1 file
├── Kernel/              # EXCLUDED - 1 file
├── Messaging/           # EXCLUDED - 1 file
├── Pipeline/            # EXCLUDED - 1 file
├── Replication/         # EXCLUDED - 1 file
├── Telemetry/           # EXCLUDED - 1 file
├── GlobalUsings.cs
└── DataWarehouse.Tests.csproj
```

### Recommended Target Structure (for T121)
```
DataWarehouse.Tests/
├── Helpers/             # NEW: Shared test utilities
│   ├── TestMessageBus.cs        # IMessageBus mock implementation
│   ├── InMemoryTestStorage.cs   # Centralized test storage (deduplicate)
│   └── TestPluginFactory.cs     # Plugin construction helpers
├── SDK/                 # NEW: SDK contract unit tests (121.B1)
├── Encryption/          # NEW: UltimateEncryption strategy tests (121.B3)
├── Compression/         # NEW: UltimateCompression strategy tests (121.B4)
├── KeyManagement/       # EXISTS as Infrastructure/UltimateKeyManagementTests.cs
├── Storage/             # EXISTS: expand with UltimateStorage tests (121.B5)
├── AccessControl/       # EXISTS as Security/: expand (121.B6)
├── Compliance/          # EXISTS: expand (121.B7)
├── Intelligence/        # NEW: UniversalIntelligence tests (121.B8)
├── Interface/           # NEW: UltimateInterface tests (121.B9)
├── TamperProof/         # EXISTS: 12 files, marked COMPLETE (121.B10)
├── Integration/         # RE-ENABLE or rewrite (121.C1-C5)
├── Performance/         # NEW: consolidated perf benchmarks (121.D1-D4)
├── Security/            # EXISTS: expand with security-specific tests (121.E1-E5)
├── Dashboard/           # EXISTS
├── Infrastructure/      # EXISTS
├── GlobalUsings.cs
└── DataWarehouse.Tests.csproj
```

### Pattern 1: SDK Contract Unit Test (Reflection-Based)
**What:** Tests that verify SDK interface contracts via reflection -- no plugin references needed
**When to use:** Testing SDK contracts, enums, interface shapes
**Example:**
```csharp
// Source: Existing pattern in SdkSecurityStrategyTests.cs
[Fact]
public void IComplianceStrategy_DefinesAssessAsyncMethod()
{
    var method = typeof(IComplianceStrategy).GetMethod("AssessAsync");
    Assert.NotNull(method);
    Assert.Equal(typeof(Task<ComplianceAssessmentResult>), method!.ReturnType);
}
```

### Pattern 2: Plugin Strategy Unit Test (Direct Instantiation)
**What:** Tests that directly instantiate and exercise strategy classes
**When to use:** Testing individual strategies within Ultimate plugins
**Example:**
```csharp
// Source: Existing pattern in CanaryStrategyTests.cs
public class CanaryStrategyTests : IDisposable
{
    private readonly CanaryStrategy _strategy;

    public CanaryStrategyTests()
    {
        _strategy = new CanaryStrategy();
    }

    public void Dispose() => _strategy.Dispose();

    [Fact]
    public void CreateCanaryFile_GeneratesFileWithRealisticContent()
    {
        var canary = _strategy.CreateCanaryFile("test-001", CanaryFileType.PasswordsExcel);
        canary.Should().NotBeNull();
        canary.IsActive.Should().BeTrue();
    }
}
```

### Pattern 3: Integration Test with In-Memory Components
**What:** Tests exercising multiple components together using in-memory implementations
**When to use:** End-to-end flows, multi-plugin interactions
**Example:**
```csharp
// Source: Existing pattern in StoragePoolBaseTests.cs
[Fact]
public async Task ConcurrentSaveToSameUri_ShouldBeThreadSafe()
{
    var pool = new TestStoragePool();
    var provider = new InMemoryStoragePlugin();
    pool.AddProvider(provider);
    var uri = new Uri("memory:///concurrent.txt");
    var tasks = Enumerable.Range(0, 100)
        .Select(i => pool.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes($"data-{i}"))))
        .ToList();
    var results = await Task.WhenAll(tasks);
    results.Should().AllSatisfy(r => r.Success.Should().BeTrue());
}
```

### Pattern 4: Performance Benchmark Test (Stopwatch-Based)
**What:** Tests that measure execution time with explicit thresholds
**When to use:** Establishing performance baselines
**Example:**
```csharp
// Source: Existing pattern in PerformanceBenchmarkTests.cs
[Fact]
[Trait("Category", "Performance")]
public void Benchmark_SHA256_1MB_ShouldCompleteWithin100ms()
{
    var data = new byte[1 * 1024 * 1024];
    RandomNumberGenerator.Fill(data);
    var sw = Stopwatch.StartNew();
    var hash = SHA256.HashData(data);
    sw.Stop();
    hash.Should().HaveCount(32);
    sw.ElapsedMilliseconds.Should().BeLessThan(100);
}
```

### Anti-Patterns to Avoid
- **Inline test helpers:** The codebase has duplicated `InMemoryTestStorage` classes in multiple files (StorageTests.cs, ErasureCodingTests.cs). Extract to shared helper.
- **Testing excluded files:** 17 test files are `<Compile Remove>`d because they reference APIs that changed during refactoring. These must be rewritten, not just re-enabled.
- **ITestOutputHelper in xUnit v3:** Several files still use `ITestOutputHelper` (EnvelopeEncryptionIntegrationTests, EnvelopeEncryptionBenchmarks). In xUnit v3, prefer `TestContext.Current.TestOutputHelper` or `TestContext.Current.CancellationToken` (xUnit1051 analyzer warnings already flag this).
- **Blocking task operations:** EphemeralSharingStrategyTests has `xUnit1031` warnings for blocking task operations. Use `async/await` instead.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Code coverage collection | Custom coverage tracking | Coverlet `--collect:"XPlat Code Coverage"` | Industry-standard, integrates with CI/CD |
| Coverage HTML reports | Custom report generator | ReportGenerator global tool | Renders Cobertura XML to browseable HTML |
| Test data generation | Manual test data factories | `[Theory]` + `[InlineData]` / `[MemberData]` | xUnit handles parameterized test data natively |
| Mock/Spy creation | Custom mock classes | Moq `Mock<T>()` | Already in project; handles setup/verify patterns |
| Assertion messages | Custom `if/throw` assertions | FluentAssertions `.Should()` | Already in project; rich failure messages |
| Test categorization | Custom test filtering | `[Trait("Category", "X")]` | xUnit built-in; filter with `--filter Category=Unit` |
| Cancellation tokens in tests | Custom timeout logic | `TestContext.Current.CancellationToken` | xUnit v3 built-in; automatically signals on test timeout |

## Common Pitfalls

### Pitfall 1: Excluded Test Files Growing Stale
**What goes wrong:** The 17 `<Compile Remove>` test files reference APIs from before the Ultimate plugin refactor. The longer they remain excluded, the harder to recover.
**Why it happens:** Build errors in test files after SDK refactoring caused blanket exclusion rather than test updates.
**How to avoid:** Evaluate each excluded file: either rewrite to work with current APIs or delete if the tested functionality moved to a different plugin.
**Warning signs:** `<Compile Remove>` count increases over time; no tests cover Kernel, Pipeline, Integration, Messaging, Database, or Replication.

### Pitfall 2: Test Project Reference Scope Too Narrow
**What goes wrong:** The test project only references SDK, Kernel, UltimateAccessControl, and UltimateCompliance. Tests for other plugins (Encryption, Compression, Storage, etc.) cannot instantiate strategy classes.
**Why it happens:** References were added incrementally only for the plugins being actively tested.
**How to avoid:** Add `<ProjectReference>` entries for each Ultimate plugin that needs direct strategy testing. Alternatively, test only via SDK interfaces.
**Warning signs:** Cannot instantiate strategy classes from untested plugins.

### Pitfall 3: xUnit v3 Analyzer Warnings Indicate Real Issues
**What goes wrong:** The test run shows many `xUnit1051` warnings (use `TestContext.Current.CancellationToken`) and `xUnit1031` warnings (blocking task operations).
**Why it happens:** Tests were written for xUnit v2 patterns and not fully migrated.
**How to avoid:** Address analyzer warnings during test expansion. Use `TestContext.Current.CancellationToken` instead of `CancellationToken.None`.
**Warning signs:** `xUnit1051` and `xUnit1031` warnings in build output.

### Pitfall 4: Performance Tests with Tight Thresholds
**What goes wrong:** `Benchmark_ManifestSerialization_ShouldCompleteWithin50ms` fails (took 611ms) because CI environments have variable performance.
**Why it happens:** Hard-coded timing thresholds don't account for different machine specs or CI resource contention.
**How to avoid:** Use generous thresholds (5-10x expected) for CI, or use `[Trait("Category", "Performance")]` and exclude from CI runs.
**Warning signs:** Flaky test results that pass locally but fail in CI.

### Pitfall 5: Failing Tests Left Unfixed
**What goes wrong:** 18 tests currently fail. If new tests are added without fixing existing failures, the failure count grows and test reliability degrades.
**Why it happens:** Failing tests were deprioritized during feature development.
**How to avoid:** Fix all 18 failing tests BEFORE adding new ones. Group them: 8 Dashboard failures (PluginDiscoveryService), 3 StoragePoolBase failures, 3 InMemoryStorage failures, 2 Compliance failures, 1 CanaryStrategy failure, 1 Performance benchmark failure.
**Warning signs:** Test failure count increasing over time.

### Pitfall 6: Security Pen Test Plan vs. Automated Security Tests
**What goes wrong:** Confusing T122 (pen test PLAN document) with T121.E (automated security tests in code).
**Why it happens:** Both involve "security testing" but have different deliverables.
**How to avoid:** T122 produces a document (Markdown) with threat models, attack scenarios, and procedures. T121.E produces code (C# test classes) that automate security checks. They are complementary but distinct.
**Warning signs:** Attempting to write C# code for T122, or writing only documents for T121.E.

## Code Examples

### Running Tests with Coverage Collection
```bash
# Source: coverlet.collector documentation
dotnet test DataWarehouse.Tests \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults

# Generate HTML report from Cobertura XML
reportgenerator \
  -reports:./TestResults/*/coverage.cobertura.xml \
  -targetdir:./TestResults/CoverageReport \
  -reporttypes:Html

# Run with coverage threshold enforcement (MSBuild integration)
dotnet test DataWarehouse.Tests \
  /p:CollectCoverage=true \
  /p:CoverageOutputFormat=cobertura \
  /p:Threshold=80 \
  /p:ThresholdType=line
```

### Filtering Tests by Category
```bash
# Run only unit tests
dotnet test DataWarehouse.Tests --filter "Category!=Performance&Category!=Integration"

# Run only performance benchmarks
dotnet test DataWarehouse.Tests --filter "Category=Performance"

# Run specific test class
dotnet test DataWarehouse.Tests --filter "FullyQualifiedName~TamperProof.IntegrityProviderTests"
```

### xUnit v3 TestContext Usage (Replacing ITestOutputHelper)
```csharp
// Source: xunit.net/docs/getting-started/v3/whats-new
public class ModernTestPattern
{
    [Fact]
    public async Task Example_WithCancellationToken()
    {
        // xUnit v3: use TestContext for cancellation
        var ct = TestContext.Current.CancellationToken;

        // xUnit v3: use TestContext for output
        TestContext.Current.TestOutputHelper.WriteLine("Test starting...");

        var result = await SomeOperationAsync(ct);
        result.Should().NotBeNull();
    }
}
```

### Test .runsettings File for Coverlet Configuration
```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- Source: coverlet-coverage/coverlet documentation -->
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat Code Coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Exclude>[DataWarehouse.Tests]*</Exclude>
          <Include>[DataWarehouse.SDK]*,[DataWarehouse.Kernel]*,[DataWarehouse.Plugins.*]*</Include>
          <ExcludeByAttribute>Obsolete,GeneratedCodeAttribute,CompilerGeneratedAttribute</ExcludeByAttribute>
          <SingleHit>false</SingleHit>
          <UseSourceLink>true</UseSourceLink>
          <IncludeTestAssembly>false</IncludeTestAssembly>
          <SkipAutoProps>true</SkipAutoProps>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

### Shared TestMessageBus Helper (Recommended Pattern)
```csharp
// Recommended: Extract from inline test helpers
internal sealed class TestMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, List<Func<PluginMessage, Task<PluginMessage?>>>> _handlers = new();

    public Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        if (_handlers.TryGetValue(topic, out var handlers))
        {
            foreach (var handler in handlers)
                _ = handler(message);
        }
        return Task.CompletedTask;
    }

    public Task<TResponse> RequestAsync<TResponse>(string topic, object request,
        TimeSpan timeout, CancellationToken ct = default)
    {
        // Configurable response for testing
        throw new NotImplementedException("Configure response via SetupResponse<T>");
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task<PluginMessage?>> handler)
    {
        _handlers.GetOrAdd(topic, _ => new()).Add(handler);
        return new TestSubscription(() => _handlers[topic].Remove(handler));
    }
}
```

## Current Test Coverage Analysis

### Active Test Coverage Map

| Category | Test Files | Test Count | Plugins Tested | Coverage Gap |
|----------|-----------|------------|----------------|-------------|
| TamperProof | 12 | 193 | T1-T4 | COMPLETE - 12 files covering all subsystems |
| SDK Infrastructure | 8 | 163 | T99 SDK contracts | Partial - SDK strategy interfaces only |
| Security (AccessControl) | 4 | 148 | T95 UltimateAccessControl | Partial - 4 strategies tested |
| Compliance | 2 | 73 | T96 UltimateCompliance | Partial - framework validation + sovereignty |
| Storage | 3 | 51 | Kernel InMemoryStorage | Minimal - only in-memory; no UltimateStorage |
| Dashboard | 1 | 27 | Kernel services | Has 8 failing tests |
| **Encryption** | **0** | **0** | **T93 UltimateEncryption** | **CRITICAL GAP** |
| **Compression** | **0** | **0** | **T92 UltimateCompression** | **CRITICAL GAP** |
| **Key Management** | **1** | **18** | **T94 UltimateKeyManagement** | **Minimal** |
| **Intelligence** | **0** | **0** | **T90 UniversalIntelligence** | **CRITICAL GAP** |
| **Interface** | **0** | **0** | **T109 UltimateInterface** | **CRITICAL GAP** |
| **RAID** | **0** | **0** | **T91 UltimateRAID** | **GAP** |
| **Replication** | **0** | **0** | **T98 UltimateReplication** | **GAP** |
| Integration | 0 (excluded) | 0 | Multi-plugin | **CRITICAL GAP** |
| Performance | 1 (TamperProof only) | 12 | Hashing benchmarks | Needs expansion |

### Excluded Test Files (17 files, ~311 test methods)

| File | Reason for Exclusion | Action Needed |
|------|---------------------|---------------|
| CircuitBreakerPolicyTests.cs | API changed in refactor | Rewrite for current SDK |
| MetricsCollectorTests.cs | API changed in refactor | Rewrite for current SDK |
| TokenBucketRateLimiterTests.cs | API changed in refactor | Rewrite for current SDK |
| SdkInfrastructureTests.cs | API changed in refactor | Rewrite for current SDK |
| DistributedTracingTests.cs | API changed in refactor | Rewrite for UniversalObservability |
| ErasureCodingTests.cs | API changed in refactor | Rewrite for UltimateRAID |
| DurableStateTests.cs | API changed in refactor | Rewrite for current Storage APIs |
| DataWarehouseKernelTests.cs | API changed in refactor | Rewrite for current Kernel |
| RelationalDatabasePluginTests.cs | Plugin removed/consolidated | Delete or rewrite |
| EmbeddedDatabasePluginTests.cs | Plugin removed/consolidated | Delete or rewrite |
| CodeCleanupVerificationTests.cs | One-time verification | Delete (purpose served) |
| KernelIntegrationTests.cs | API changed in refactor | Rewrite as integration tests |
| AdvancedMessageBusTests.cs | API changed in refactor | Rewrite for current IMessageBus |
| PluginTests.cs | API changed in refactor | Rewrite for current plugin system |
| PipelineOrchestratorTests.cs | API changed in refactor | Rewrite for current pipeline |
| GeoReplicationPluginTests.cs | Plugin consolidated into UltimateReplication | Rewrite for UltimateReplication |
| MpcStrategyTests.cs | Compilation issue | Fix reference or rewrite |

### Failing Tests (18 failures)

| Test Class | Failure Count | Root Cause |
|-----------|--------------|------------|
| PluginDiscoveryServiceTests | 8 | DashboardService API mismatch |
| StoragePoolBaseTests | 3 | Stream position / multi-provider logic bugs |
| InMemoryStoragePluginTests | 3 | LRU eviction logic bugs |
| FaangScaleTests | 2 | SDK infrastructure type changes |
| CanaryStrategyTests | 1 | String.Substring bounds error in CanaryGenerator |
| PerformanceBenchmarkTests | 1 | Timing threshold too tight (50ms, took 611ms) |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| xUnit v2 `ITestOutputHelper` | xUnit v3 `TestContext.Current.TestOutputHelper` | xUnit v3 (Dec 2024) | Update constructor injection pattern |
| `CancellationToken.None` in tests | `TestContext.Current.CancellationToken` | xUnit v3 (Dec 2024) | Enables proper test cancellation/timeout |
| Separate xunit + xunit.runner packages | Single `xunit.v3` package | xUnit v3 (Dec 2024) | Simplified package references |
| `Assert.Equal` / `Assert.True` only | FluentAssertions `.Should()` preferred | Convention | Richer failure messages, method chaining |

## Security Penetration Test Plan Scope (T122)

### Deliverable
A formal Markdown document, NOT automated test code. The document should follow the structure in TODO.md T122 phases A-E.

### Key Components
1. **Threat Model (STRIDE):** Spoofing, Tampering, Repudiation, Information Disclosure, Denial of Service, Elevation of Privilege -- applied to DataWarehouse's attack surfaces
2. **Attack Surface Map:** REST API endpoints (UltimateInterface), storage paths, encryption key stores, message bus, plugin loading
3. **OWASP Top 10 Checklist:** Each OWASP category mapped to DataWarehouse-specific scenarios
4. **AI-Assisted Testing Procedures:** Step-by-step procedures for Claude to perform static code analysis, configuration review, and logic-based vulnerability assessment
5. **Remediation Framework:** Severity rating system (Critical/High/Medium/Low), remediation timelines, re-test procedures

### Security-Relevant Plugin Areas
| Plugin | Security Concern | Pen Test Focus |
|--------|-----------------|----------------|
| UltimateEncryption (T93) | Weak algorithms, IV reuse, key leakage | Cryptographic failures |
| UltimateKeyManagement (T94) | Key storage, rotation, access control | Key management audit |
| UltimateAccessControl (T95) | RBAC bypass, privilege escalation | Broken access control |
| TamperProof (T1-T4) | Tamper detection bypass, WORM circumvention | Data integrity |
| UltimateInterface (T109) | API injection, rate limiting, auth bypass | API security |
| UltimateCompliance (T96) | Compliance bypass, audit log tampering | Regulatory compliance |

## Open Questions

1. **CI/CD Pipeline Configuration**
   - What we know: No GitHub Actions, Azure DevOps, or other CI configuration exists in the repo
   - What's unclear: What CI/CD system will be used; whether T121.A5 is in scope for this phase
   - Recommendation: Create a basic GitHub Actions workflow or document CI/CD setup as a future task. For this phase, focus on making tests runnable via `dotnet test` with coverage.

2. **Plugin Reference Expansion in Test Project**
   - What we know: Test project only references SDK, Kernel, UltimateAccessControl, UltimateCompliance
   - What's unclear: Should ALL Ultimate plugins be referenced, or only critical path ones?
   - Recommendation: Add references for the 6 critical path plugins (Encryption, Compression, KeyManagement, Storage, AccessControl, Compliance) plus TamperProof. Reference additional plugins only as tests are written.

3. **Docker Integration Tests (121.A3)**
   - What we know: No Docker configuration exists in the project
   - What's unclear: Whether containerized integration testing is in scope
   - Recommendation: Defer Docker-based integration tests. Use in-memory components for integration tests in this phase.

4. **Coverage Measurement Baseline**
   - What we know: Coverlet is installed but no .runsettings or threshold configured
   - What's unclear: Current actual coverage percentage
   - Recommendation: Run coverage once to establish baseline before writing new tests.

## Sources

### Primary (HIGH confidence)
- Direct file analysis of `DataWarehouse.Tests/DataWarehouse.Tests.csproj` -- package versions, project references, compile exclusions
- Direct test execution via `dotnet test --list-tests` and `dotnet test` -- 845 tests, 826 pass, 18 fail, 1 skip
- `Metadata/TODO.md` lines 12666-12800 -- T121 and T122 full sub-task breakdowns
- `.planning/ROADMAP.md` lines 349-363 -- Phase 16 success criteria
- `.planning/REQUIREMENTS.md` lines 220-221 -- TEST-01 and TEST-02 requirements

### Secondary (MEDIUM confidence)
- [xUnit v3 What's New](https://xunit.net/docs/getting-started/v3/whats-new) -- TestContext, CancellationToken, migration
- [xUnit v3 Migration Guide](https://xunit.net/docs/getting-started/v3/migration) -- v2 to v3 migration patterns
- [Coverlet documentation](https://github.com/coverlet-coverage/coverlet) -- configuration, thresholds, formats
- [Microsoft code coverage docs](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-code-coverage) -- `dotnet test` integration

### Tertiary (LOW confidence)
- None

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All libraries already installed and verified in .csproj
- Architecture: HIGH - Based on direct analysis of existing test files and patterns
- Pitfalls: HIGH - Based on actual test execution results (18 failures, 17 exclusions observed)
- Coverage gaps: HIGH - Based on complete enumeration of test namespaces vs plugin list

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (stable stack, well-understood domain)
