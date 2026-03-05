// Hardening tests for UltimateIntelligence findings 1-7
// Finding 1 (MEDIUM): .Result blocking calls in IntelligenceTestSuites
// Finding 2 (HIGH): Message handlers not returning results
// Finding 3 (MEDIUM): Task.Run proliferation in TemporalKnowledge, SemanticStorageStrategies
// Finding 4 (LOW): ~69 empty catch blocks
// Finding 5 (MEDIUM): PossibleLossOfFraction in AccuracyVerifier.cs line 435
// Finding 6-7 (LOW): Naming: CalculateLCSSimilarity -> CalculateLcsSimilarity, CalculateLCSLength -> CalculateLcsLength

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class SystemicAndAccuracyTests
{
    private static readonly string PluginDir = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIntelligence");

    private static readonly string AccuracyVerifierFile = Path.Combine(
        PluginDir, "Strategies", "Memory", "Regeneration", "AccuracyVerifier.cs");

    /// <summary>
    /// Finding 5 (MEDIUM): Possible loss of fraction in Jaro-Winkler calculation.
    /// transpositions / 2 should use double division.
    /// </summary>
    [Fact]
    public void Finding005_AccuracyVerifier_NoIntegerDivisionLoss()
    {
        var code = File.ReadAllText(AccuracyVerifierFile);
        // Should use (double) cast or 2.0 to avoid integer division
        Assert.True(
            code.Contains("transpositions / 2.0") || code.Contains("(double)transpositions / 2") ||
            code.Contains("transpositions / 2d"),
            "AccuracyVerifier: transpositions / 2 should use double division to avoid loss of fraction");
    }

    /// <summary>
    /// Finding 6 (LOW): CalculateLCSSimilarity -> CalculateLcsSimilarity
    /// </summary>
    [Fact]
    public void Finding006_AccuracyVerifier_LcsSimilarityNaming()
    {
        var code = File.ReadAllText(AccuracyVerifierFile);
        Assert.DoesNotContain("CalculateLCSSimilarity", code);
        Assert.Contains("CalculateLcsSimilarity", code);
    }

    /// <summary>
    /// Finding 7 (LOW): CalculateLCSLength -> CalculateLcsLength
    /// </summary>
    [Fact]
    public void Finding007_AccuracyVerifier_LcsLengthNaming()
    {
        var code = File.ReadAllText(AccuracyVerifierFile);
        Assert.DoesNotContain("CalculateLCSLength", code);
        Assert.Contains("CalculateLcsLength", code);
    }
}
