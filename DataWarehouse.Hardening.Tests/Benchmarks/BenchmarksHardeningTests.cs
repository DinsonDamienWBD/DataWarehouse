namespace DataWarehouse.Hardening.Tests.Benchmarks;

/// <summary>
/// Hardening tests for Benchmarks findings 1-6.
/// Covers: Write1KB->Write1Kb, Write1MB->Write1Mb, Read1KB->Read1Kb,
/// Read1MB->Read1Mb, _data1MB->_data1Mb naming conventions.
/// </summary>
public class BenchmarksHardeningTests
{
    private static string GetBenchmarkDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "DataWarehouse.Benchmarks"));

    // Findings #1-4: LOW - Method naming KB/MB->Kb/Mb
    [Fact]
    public void Finding001_Write1Kb()
    {
        var source = File.ReadAllText(Path.Combine(GetBenchmarkDir(), "Program.cs"));
        Assert.Contains("Write1Kb", source);
        Assert.DoesNotContain("Write1KB", source);
    }

    [Fact]
    public void Finding002_Write1Mb()
    {
        var source = File.ReadAllText(Path.Combine(GetBenchmarkDir(), "Program.cs"));
        Assert.Contains("Write1Mb", source);
        Assert.DoesNotContain("Write1MB", source);
    }

    [Fact]
    public void Finding003_Read1Kb()
    {
        var source = File.ReadAllText(Path.Combine(GetBenchmarkDir(), "Program.cs"));
        Assert.Contains("Read1Kb", source);
        Assert.DoesNotContain("Read1KB", source);
    }

    [Fact]
    public void Finding004_Read1Mb()
    {
        var source = File.ReadAllText(Path.Combine(GetBenchmarkDir(), "Program.cs"));
        Assert.Contains("Read1Mb", source);
        Assert.DoesNotContain("Read1MB", source);
    }

    // Finding #6: LOW - _data1MB->_data1Mb
    [Fact]
    public void Finding006_Data1MbFieldNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetBenchmarkDir(), "Program.cs"));
        Assert.Contains("_data1Mb", source);
        Assert.DoesNotContain("_data1MB", source);
    }
}
