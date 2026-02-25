# Testing Patterns

**Analysis Date:** 2026-02-10

## Test Framework

**Runner:**
- xUnit v3.2.2 - primary test framework
- Config: `C:\Temp\DataWarehouse\DataWarehouse\DataWarehouse.Tests\DataWarehouse.Tests.csproj`
- Test project targets: net10.0

**Assertion Library:**
- FluentAssertions 8.8.0 - used for readable, chainable assertions

**Mocking Framework:**
- Moq 4.20.72 - used for mocking interfaces and dependencies

**Coverage:**
- Coverlet 6.0.4 - coverage collection tool included in test project

**Run Commands:**
```bash
dotnet test                                # Run all tests
dotnet test --watch                        # Watch mode (continuous testing)
dotnet test /p:CollectCoverage=true       # Run with coverage collection
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover  # OpenCover format
dotnet build --configuration Release       # Build benchmarks with release config
dotnet run --project DataWarehouse.Benchmarks -c Release  # Run benchmarks
```

## Test File Organization

**Location:**
- Central test assembly at `C:\Temp\DataWarehouse\DataWarehouse\DataWarehouse.Tests\`
- Co-located with feature by folder: `Tests/Dashboard/`, `Tests/Database/`, `Tests/Infrastructure/`
- Separate from production code (not co-located in src)

**Naming:**
- Test classes: `{Subject}Tests` suffix (e.g., `SystemHealthServiceTests`, `StorageManagementServiceTests`)
- Test methods: `{Method}_{Condition}_{Expected}` or just `{Method}_{Expected}` pattern
  - Example: `GetSystemHealthAsync_ReturnsValidHealthStatus`
  - Example: `GetPool_WhenNotFound_ReturnsNull`
  - Example: `DeletePoolAsync_WhenPoolExists_ReturnsTrue`
- Test files match test class: `DashboardServiceTests.cs` contains `SystemHealthServiceTests`, `PluginDiscoveryServiceTests`, `StorageManagementServiceTests`

**Structure:**
```
DataWarehouse.Tests/
├── GlobalUsings.cs                    # Global using directives
├── Compliance/
│   └── ComplianceTestSuites.cs
├── Dashboard/
│   └── DashboardServiceTests.cs
├── Database/
│   ├── EmbeddedDatabasePluginTests.cs (excluded)
│   └── RelationalDatabasePluginTests.cs (excluded)
├── Hyperscale/
│   └── ErasureCodingTests.cs (excluded)
├── Infrastructure/
│   ├── CircuitBreakerPolicyTests.cs (excluded)
│   ├── MetricsCollectorTests.cs (excluded)
│   ├── TokenBucketRateLimiterTests.cs (excluded)
│   ├── SdkInfrastructureTests.cs (excluded)
│   └── CodeCleanupVerificationTests.cs (excluded)
├── Integration/
│   └── KernelIntegrationTests.cs (excluded)
├── Kernel/
│   └── DataWarehouseKernelTests.cs (excluded)
├── Messaging/
│   └── AdvancedMessageBusTests.cs (excluded)
├── Pipeline/
│   └── PipelineOrchestratorTests.cs (excluded)
├── Replication/
│   └── GeoReplicationPluginTests.cs (excluded)
└── DataWarehouse.Tests.csproj
```

**Note:** Many test files are excluded from build via `<Compile Remove>` in .csproj to fix build errors. Only `DashboardServiceTests.cs` and `ComplianceTestSuites.cs` are currently compiled.

## Test Structure

**Suite Organization:**

From `DashboardServiceTests.cs`:
```csharp
public class SystemHealthServiceTests
{
    private readonly Mock<ILogger<TestSystemHealthService>> _loggerMock;
    private readonly TestSystemHealthService _service;

    public SystemHealthServiceTests()
    {
        _loggerMock = new Mock<ILogger<TestSystemHealthService>>();
        _service = new TestSystemHealthService(_loggerMock.Object);
    }

    [Fact]
    public async Task GetSystemHealthAsync_ReturnsValidHealthStatus()
    {
        // Act
        var result = await _service.GetSystemHealthAsync();

        // Assert
        result.Should().NotBeNull();
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        result.Components.Should().NotBeEmpty();
    }
}
```

**Patterns:**
- **Setup:** Constructor initializes mocks and service under test
- **Teardown:** None explicitly used - .NET handles resource cleanup
- **Assertion:** FluentAssertions chaining: `result.Should().NotBeNull().And.Property.Should().BeOfType<T>()`
- **Arrangement:** Inline in test methods using `// Arrange` comments
- **Act/Assert:** Separated with `// Act` and `// Assert` comments
- **Test Attributes:** `[Fact]` for single-outcome tests, `[Theory]` would be used for parameterized tests (not found in current tests)

## Mocking

**Framework:** Moq 4.20.72

**Patterns:**
```csharp
// Setup mock
private readonly Mock<ILogger<TestSystemHealthService>> _loggerMock;

// Constructor initialization
_loggerMock = new Mock<ILogger<TestSystemHealthService>>();

// Pass mock.Object to service
_service = new TestSystemHealthService(_loggerMock.Object);

// Setup return values (example from tests)
_configMock.Setup(c => c.GetValue<string>("PluginsDirectory", It.IsAny<string>()))
    .Returns((string)null!);
```

**What to Mock:**
- Dependencies that are external or slow: `ILogger<T>`, `IConfiguration`
- Interfaces that define contracts but aren't under test: `IMessageBus`, `IStorageProvider`
- Services with side effects (database, network calls)

**What NOT to Mock:**
- The subject under test itself (the service being tested)
- Value types and simple data structures
- Test helper implementations (TestSystemHealthService, TestStorageManagementService)

## Fixtures and Factories

**Test Data:**
Test helper classes serve as both fixtures and simple factories:
```csharp
public class TestSystemHealthService
{
    private readonly ILogger<TestSystemHealthService> _logger;
    private readonly List<TestSystemMetrics> _metricsHistory = new();
    private readonly Dictionary<string, TestSystemAlert> _alerts = new();

    public TestSystemHealthService(ILogger<TestSystemHealthService> logger)
    {
        _logger = logger;
    }

    public Task<TestSystemHealthStatus> GetSystemHealthAsync()
    {
        return Task.FromResult(new TestSystemHealthStatus
        {
            OverallStatus = "Healthy",
            Timestamp = DateTime.UtcNow,
            Components = new List<TestComponentHealth>
            {
                new() { Name = "Kernel", Status = "Healthy" },
                new() { Name = "Memory", Status = "Healthy" },
                new() { Name = "Disk", Status = "Healthy" }
            }
        });
    }
}
```

**Location:**
- Test helpers/fixtures defined in same file as tests
- Placed in `#region Test Implementations` section at end of file
- Marked with clear comments: `// Test Implementations (Simplified versions for testing)`
- Example models:
  - `TestSystemHealthService`, `TestPluginDiscoveryService`, `TestStorageManagementService`
  - `TestSystemHealthStatus`, `TestComponentHealth`, `TestSystemMetrics`
  - `TestStoragePoolInfo`, `TestStorageInstance`, `TestRaidConfiguration`

**Data Generation:**
- Guids generated with `Guid.NewGuid()`
- Timestamps from `DateTime.UtcNow` (not mocked to test time-sensitive logic)
- Collection initialization with inline object creation
- Random data via short string interpolation: `$"TestPool-{Guid.NewGuid():N}"`

## Coverage

**Requirements:** No enforced coverage targets found in project configuration

**View Coverage:**
```bash
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover /p:Excludes="[*Tests]*"
```

**Test Coverage Gaps:**
- Many test files excluded from build (see structure section)
- Excluded areas indicate incomplete test coverage:
  - Database plugins (relational and embedded)
  - Message bus implementation
  - Pipeline orchestration
  - Geo-replication functionality
  - Kernel integration
  - Circuit breaker and rate limiting infrastructure

## Test Types

**Unit Tests:**
- Scope: Individual service/component methods
- Approach: Mock dependencies, test behavior in isolation
- Example: `GetSystemHealthAsync_ReturnsValidHealthStatus()` tests one service method
- Verification: FluentAssertions on return values and state

**Integration Tests:**
- Scope: Multiple components working together
- Approach: Uses real implementations of some components
- Status: Test file `KernelIntegrationTests.cs` excluded from build (not currently running)
- Pattern would be: Testing kernel with real message bus and plugin registry

**E2E Tests:**
- Framework: Not currently used
- Would test: Full CLI workflows from command parsing to output
- Current state: Dashboard tests use simplified implementations rather than full E2E setup

**Benchmarks:**
- Framework: BenchmarkDotNet
- Location: `C:\Temp\DataWarehouse\DataWarehouse\DataWarehouse.Benchmarks\`
- Benchmarks for:
  - Storage operations (reads/writes at various sizes)
  - Cryptographic operations (SHA256, SHA512, AES encryption)
  - Serialization (JSON, MessagePack)
  - Concurrency (ConcurrentDict, ConcurrentQueue, Channels)
  - Memory allocation patterns (ArrayPool vs new allocation)
  - Compression (GZip, Brotli, Deflate)

## Common Patterns

**Async Testing:**
```csharp
[Fact]
public async Task GetSystemHealthAsync_ReturnsValidHealthStatus()
{
    // Act
    var result = await _service.GetSystemHealthAsync();

    // Assert
    result.Should().NotBeNull();
}

[Fact]
public async Task CreatePoolAsync_CreatesNewPool()
{
    // Arrange
    var name = $"TestPool-{Guid.NewGuid():N}";
    var poolType = "Standard";
    var capacity = 1024L * 1024 * 1024 * 10;

    // Act
    var result = await _service.CreatePoolAsync(name, poolType, capacity);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be(name);
}
```

**Boolean Return Testing:**
```csharp
[Fact]
public async Task DeletePoolAsync_WhenPoolExists_ReturnsTrue()
{
    // Arrange
    var pool = await _service.CreatePoolAsync("ToDelete", "Standard", 1024);

    // Act
    var result = await _service.DeletePoolAsync(pool.Id);

    // Assert
    result.Should().BeTrue();
}

[Fact]
public async Task DeletePoolAsync_WhenPoolDoesNotExist_ReturnsFalse()
{
    // Act
    var result = await _service.DeletePoolAsync("nonexistent-pool");

    // Assert
    result.Should().BeFalse();
}
```

**Null/NotFound Testing:**
```csharp
[Fact]
public void GetPlugin_WhenNotFound_ReturnsNull()
{
    // Act
    var result = _service.GetPlugin("nonexistent-plugin-id");

    // Assert
    result.Should().BeNull();
}

[Fact]
public void GetAlerts_WhenNoAlerts_ReturnsEmptyCollection()
{
    // Act
    var result = _service.GetAlerts(activeOnly: true);

    // Assert
    result.Should().NotBeNull();
}
```

**Event/Callback Testing:**
```csharp
[Fact]
public async Task StorageChanged_FiresOnPoolCreate()
{
    // Arrange
    var eventFired = false;
    _service.StorageChanged += (s, e) => eventFired = true;

    // Act
    await _service.CreatePoolAsync("EventTest", "Standard", 1024);

    // Assert
    eventFired.Should().BeTrue();
}
```

**Range/Value Validation:**
```csharp
[Fact]
public void GetCurrentMetrics_MemoryUsagePercent_ShouldBeBetweenZeroAndHundred()
{
    // Act
    var metrics = _service.GetCurrentMetrics();

    // Assert
    metrics.MemoryUsagePercent.Should().BeInRange(0, 100);
}
```

**Time-Based Assertions:**
```csharp
[Fact]
public async Task GetSystemHealthAsync_ReturnsValidHealthStatus()
{
    // Act
    var result = await _service.GetSystemHealthAsync();

    // Assert
    result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
}
```

**Ordered Collection Testing:**
```csharp
[Fact]
public void GetMetricsHistory_WithValidDuration_ReturnsOrderedMetrics()
{
    // Arrange - Generate some history
    _service.GetCurrentMetrics();
    _service.GetCurrentMetrics();
    _service.GetCurrentMetrics();

    // Act
    var result = _service.GetMetricsHistory(TimeSpan.FromHours(1), TimeSpan.FromSeconds(60));

    // Assert
    result.Should().NotBeNull();
    var list = result.ToList();
    for (int i = 1; i < list.Count; i++)
    {
        list[i].Timestamp.Should().BeOnOrAfter(list[i - 1].Timestamp);
    }
}
```

**Collection Membership Testing:**
```csharp
[Fact]
public async Task AddInstanceAsync_WhenPoolExists_AddsInstance()
{
    // Arrange
    var pool = await _service.CreatePoolAsync("TestWithInstance", "Standard", 4096);

    // Act
    var instance = await _service.AddInstanceAsync(pool.Id, "TestDisk", "filesystem", null);

    // Assert
    _service.GetPool(pool.Id)!.Instances.Should().Contain(i => i.Id == instance.Id);
}

[Fact]
public async Task RemoveInstanceAsync_WhenInstanceExists_ReturnsTrue()
{
    // Arrange
    var pool = await _service.CreatePoolAsync("TestRemoveInstance", "Standard", 4096);
    var instance = await _service.AddInstanceAsync(pool.Id, "TestDisk", "filesystem", null);

    // Act
    var result = await _service.RemoveInstanceAsync(pool.Id, instance!.Id);

    // Assert
    _service.GetPool(pool.Id)!.Instances.Should().NotContain(i => i.Id == instance.Id);
}
```

## Test Dependencies

**From DataWarehouse.Tests.csproj:**
- Microsoft.NET.Test.Sdk 18.0.1
- xunit.runner.visualstudio 3.1.5
- coverlet.collector 6.0.4 (coverage)
- Moq 4.20.72
- FluentAssertions 8.8.0
- xunit.v3 3.2.2
- AWS SDKs (BedrockAgentRuntime, Core)
- Azure.Identity 1.17.1
- Microsoft.Data.SqlClient 6.1.4
- Npgsql 10.0.1 (PostgreSQL)

**Project References:**
- DataWarehouse.SDK
- DataWarehouse.Kernel

---

*Testing analysis: 2026-02-10*
