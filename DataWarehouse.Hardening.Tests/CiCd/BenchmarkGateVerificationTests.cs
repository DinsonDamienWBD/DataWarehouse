using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace DataWarehouse.Hardening.Tests.CiCd;

/// <summary>
/// INTENTIONAL ALLOCATION -- verifies BenchmarkDotNet Gen2 gate catches allocations
/// on zero-alloc paths.
///
/// This benchmark class is decorated with [MemoryDiagnoser] and categorized as
/// [ZeroAlloc]. The method intentionally allocates a byte[] array on each invocation,
/// which the BenchmarkDotNet Gen2 gate in audit.yml should flag as a regression.
///
/// This is a benchmark (runs via dotnet run), not a unit test (dotnet test).
/// The xUnit [Fact] below verifies the class structure is correct for CI validation.
///
/// Report: "Stage 4 - Gate Verification"
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[BenchmarkCategory("ZeroAlloc")]
[SimpleJob(RuntimeMoniker.Net90, iterationCount: 3, warmupCount: 1)]
public class BenchmarkGateVerificationTests
{
    /// <summary>
    /// A zero-alloc path that INTENTIONALLY allocates a 1 KB byte array.
    /// The BenchmarkDotNet memory diagnoser will report Gen0/Gen1/Gen2 collections
    /// and BytesAllocated &gt; 0, causing the Gen2 gate to reject the PR.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("ZeroAlloc")]
    public byte[] ZeroAllocPath_WithIntentionalLeak()
    {
        // INTENTIONAL: This allocation violates the zero-alloc contract.
        // BenchmarkDotNet's MemoryDiagnoser will report ~1,024 bytes allocated.
        // The CI gate checks BytesAllocated == 0 for [ZeroAlloc] benchmarks.
        var buffer = new byte[1024];

        // Touch the buffer to prevent dead-code elimination
        buffer[0] = 0xFF;
        buffer[1023] = 0xFE;

        return buffer;
    }

    /// <summary>
    /// Baseline: a truly zero-allocation method for comparison.
    /// The gate should pass this one, showing the gate is selective.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ZeroAlloc")]
    public int ZeroAllocPath_Compliant()
    {
        // No allocations -- this method should pass the Gen2 gate.
        int result = 0;
        for (int i = 0; i < 100; i++)
        {
            result ^= i;
        }
        return result;
    }
}

/// <summary>
/// xUnit verification that the benchmark class is correctly structured.
/// This runs during dotnet test to validate the gate test infrastructure.
/// </summary>
public class BenchmarkGateStructureTests
{
    [Fact]
    public void BenchmarkClass_Has_MemoryDiagnoser_Attribute()
    {
        var type = typeof(BenchmarkGateVerificationTests);
        var attr = Attribute.GetCustomAttribute(type, typeof(MemoryDiagnoserAttribute));
        Assert.NotNull(attr);
    }

    [Fact]
    public void BenchmarkClass_Has_ZeroAlloc_Category()
    {
        var type = typeof(BenchmarkGateVerificationTests);
        var categories = Attribute.GetCustomAttributes(type, typeof(BenchmarkCategoryAttribute));
        Assert.Contains(categories, a =>
            ((BenchmarkCategoryAttribute)a).Categories.Contains("ZeroAlloc"));
    }

    [Fact]
    public void IntentionalLeak_Method_Allocates_Buffer()
    {
        var benchmark = new BenchmarkGateVerificationTests();
        var result = benchmark.ZeroAllocPath_WithIntentionalLeak();

        // Verify the method does allocate (it returns a non-null byte array)
        Assert.NotNull(result);
        Assert.Equal(1024, result.Length);
        Assert.Equal(0xFF, result[0]);
        Assert.Equal(0xFE, result[1023]);
    }
}
