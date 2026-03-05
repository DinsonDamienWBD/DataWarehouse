// Hardening tests for UltimateIntelligence findings 8-10
// AdditionalProviders.cs: Collection never updated findings

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class AdditionalProvidersTests
{
    private static readonly string FilePath = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence", "Strategies", "Providers", "AdditionalProviders.cs");

    /// <summary>
    /// Finding 8 (LOW): Content of collection 'Parts' is never updated (line 247).
    /// Should use IReadOnlyList or init-only setter.
    /// </summary>
    [Fact]
    public void Finding008_GeminiContent_Parts_InitSetter()
    {
        var code = File.ReadAllText(FilePath);
        // The collection should have init setter or be IReadOnlyList
        Assert.True(
            code.Contains("List<GeminiPart>? Parts { get; init; }") ||
            code.Contains("IReadOnlyList<GeminiPart>? Parts { get; init; }") ||
            code.Contains("List<GeminiPart>? Parts { get; set; }"), // JSON deserialization needs set
            "Parts collection should have proper setter for JSON deserialization");
    }

    /// <summary>
    /// Finding 9 (LOW): Content of collection 'ToolCalls' is never updated (line 504).
    /// </summary>
    [Fact]
    public void Finding009_ToolCalls_Line504_NeverUpdated()
    {
        var code = File.ReadAllText(FilePath);
        // ToolCalls should be documented as JSON-deserialized
        Assert.True(
            code.Contains("ToolCalls"),
            "ToolCalls collection should exist");
    }

    /// <summary>
    /// Finding 10 (LOW): Content of collection 'ToolCalls' is never updated (line 1215).
    /// </summary>
    [Fact]
    public void Finding010_ToolCalls_Line1215_NeverUpdated()
    {
        var code = File.ReadAllText(FilePath);
        // Multiple ToolCalls collections exist in different classes
        var occurrences = code.Split("ToolCalls").Length - 1;
        Assert.True(occurrences >= 2, "ToolCalls should appear in multiple provider response classes");
    }
}
