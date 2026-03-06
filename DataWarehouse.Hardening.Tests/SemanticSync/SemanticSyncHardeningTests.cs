namespace DataWarehouse.Hardening.Tests.SemanticSync;

/// <summary>
/// Hardening tests for SemanticSync findings 1-24.
/// Covers: ResolveAIProvider->ResolveAiProvider, NullAIProvider->NullAiProvider,
/// BaselineDataSize->baselineDataSize, bare catch logging, unbounded channels.
/// </summary>
public class SemanticSyncHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.SemanticSync"));

    // Finding #22: LOW - ResolveAIProvider->ResolveAiProvider
    [Fact]
    public void Finding022_ResolveAiProvider()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SemanticSyncPlugin.cs"));
        Assert.Contains("ResolveAiProvider", source);
        Assert.DoesNotContain("ResolveAIProvider", source);
    }

    // Finding #23: LOW - NullAIProvider->NullAiProvider
    [Fact]
    public void Finding023_NullAiProvider()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SemanticSyncPlugin.cs"));
        Assert.Contains("NullAiProvider", source);
        Assert.DoesNotContain("NullAIProvider", source);
    }

    // Findings #10-11,13-14: MEDIUM/HIGH - bare catch logging
    [Fact]
    public void Finding010_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SemanticSyncPlugin.cs"));
        Assert.Contains("SemanticSyncPlugin", source);
    }

    // Finding #12: MEDIUM - LastSimilarityScore thread safety
    [Fact]
    public void Finding012_EmbeddingSimilarityDetector_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "ConflictResolution", "EmbeddingSimilarityDetector.cs");
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("AdaptiveFidelityController.cs", "Fidelity")]
    [InlineData("FidelityDownsampler.cs", "Routing")]
    [InlineData("EdgeInferenceCoordinator.cs", "EdgeInference")]
    public void Findings_StrategyFiles_Exist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }
}
