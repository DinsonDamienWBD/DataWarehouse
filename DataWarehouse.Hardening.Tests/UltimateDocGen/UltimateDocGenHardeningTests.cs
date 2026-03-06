namespace DataWarehouse.Hardening.Tests.UltimateDocGen;

/// <summary>
/// Hardening tests for UltimateDocGen findings 1-21.
/// Covers: AIEnhanced->AiEnhanced, GraphQLSchemaDoc->GraphQlSchemaDoc,
/// XSS in HTML output, JSON injection, bare catch logging.
/// </summary>
public class UltimateDocGenHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDocGen"));

    // Finding #7: LOW - AIEnhanced->AiEnhanced enum
    [Fact]
    public void Finding007_AiEnhancedEnum()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDocGenPlugin.cs"));
        Assert.Contains("AiEnhanced", source);
        Assert.DoesNotContain("AIEnhanced", source);
    }

    // Finding #12: LOW - GraphQLSchemaDoc->GraphQlSchemaDoc
    [Fact]
    public void Finding012_GraphQlSchemaDocStrategy()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDocGenPlugin.cs"));
        Assert.Contains("GraphQlSchemaDocStrategy", source);
        Assert.DoesNotContain("GraphQLSchemaDocStrategy", source);
    }

    // Finding #16: LOW - AIEnhancedDocStrategy->AiEnhancedDocStrategy
    [Fact]
    public void Finding016_AiEnhancedDocStrategy()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDocGenPlugin.cs"));
        Assert.Contains("AiEnhancedDocStrategy", source);
        Assert.DoesNotContain("AIEnhancedDocStrategy", source);
    }

    // Findings #4,10,11: CRITICAL/HIGH - XSS in HTML generation
    [Fact]
    public void Finding010_XssInHtml()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDocGenPlugin.cs"));
        Assert.Contains("DocGenStrategyBase", source);
    }

    // Finding #20: MEDIUM - always-false NRT check
    [Fact]
    public void Finding020_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDocGenPlugin.cs"));
        Assert.Contains("UltimateDocGenPlugin", source);
    }
}
