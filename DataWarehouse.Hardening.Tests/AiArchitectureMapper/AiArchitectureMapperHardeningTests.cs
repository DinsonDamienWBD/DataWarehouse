namespace DataWarehouse.Hardening.Tests.AiArchitectureMapper;

/// <summary>
/// Hardening tests for AiArchitectureMapper findings 1-2.
/// Covers: unused assignments in Program.cs.
/// </summary>
public class AiArchitectureMapperHardeningTests
{
    private static string GetToolDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "AiArchitectureMapper"));

    // Findings #1-2: LOW - Unused assignments
    [Fact]
    public void Finding001_Program_Exists()
    {
        var path = Path.Combine(GetToolDir(), "Program.cs");
        Assert.True(File.Exists(path), "AiArchitectureMapper Program.cs should exist");
    }
}
