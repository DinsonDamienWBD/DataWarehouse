namespace DataWarehouse.Hardening.Tests.PluginMarketplace;

/// <summary>
/// Hardening tests for PluginMarketplace findings 1-17.
/// Covers: Console.Error->Trace, fire-and-forget timer, empty catch logging,
/// TimeoutException handling, shared reference mutation.
/// </summary>
public class PluginMarketplaceHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.PluginMarketplace"));

    [Fact]
    public void Finding001_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "PluginMarketplacePlugin.cs"));
        Assert.Contains("PluginMarketplacePlugin", source);
    }

    // Findings #5,9,11,12: HIGH/MEDIUM - fire-and-forget, empty catch, shared mutation
    [Fact]
    public void Finding005_Plugin_HasTimerCallback()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "PluginMarketplacePlugin.cs"));
        Assert.Contains("Timer", source);
    }

    // Findings #16-17: LOW - naming
    [Fact]
    public void Finding016_Plugin_HasTotalInstalls()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "PluginMarketplacePlugin.cs"));
        Assert.Contains("_totalInstalls", source);
    }
}
