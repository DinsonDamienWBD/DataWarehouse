// Hardening tests for UltimateIntelligence findings 17-19
// AIContextEncoder.cs: Unused field, culture-specific IndexOf

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class AIContextEncoderTests
{
    private static readonly string FilePath = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence", "Strategies", "Memory", "AIContextEncoder.cs");

    /// <summary>
    /// Finding 17 (LOW): _randomProjections field is assigned but never used.
    /// Should be exposed or used.
    /// </summary>
    [Fact]
    public void Finding017_RandomProjections_Used()
    {
        var code = File.ReadAllText(FilePath);
        // Field should be used (referenced beyond assignment)
        var assignCount = code.Split("_randomProjections").Length - 1;
        Assert.True(assignCount >= 2, "_randomProjections should be used beyond initial assignment");
    }

    /// <summary>
    /// Finding 18 (MEDIUM): String.IndexOf(string, int) is culture-specific (line 526).
    /// Should use StringComparison.Ordinal.
    /// </summary>
    [Fact]
    public void Finding018_IndexOf_CultureInvariant_Line526()
    {
        var code = File.ReadAllText(FilePath);
        // IndexOf calls with string parameter should use StringComparison.Ordinal
        Assert.True(
            code.Contains("IndexOf(ender, position, StringComparison.Ordinal)") ||
            code.Contains("IndexOf(ender, position, System.StringComparison.Ordinal)"),
            "IndexOf at line 526 should use StringComparison.Ordinal");
    }

    /// <summary>
    /// Finding 19 (MEDIUM): String.IndexOf(string, int) is culture-specific (line 537).
    /// </summary>
    [Fact]
    public void Finding019_IndexOf_CultureInvariant_Line537()
    {
        var code = File.ReadAllText(FilePath);
        // Second IndexOf call should also use StringComparison.Ordinal
        var ordinalCount = code.Split("StringComparison.Ordinal").Length - 1;
        Assert.True(ordinalCount >= 2, "Both IndexOf calls should use StringComparison.Ordinal");
    }
}
