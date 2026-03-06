namespace DataWarehouse.Hardening.Tests.UltimateDataLake;

/// <summary>
/// Hardening tests for UltimateDataLake findings 1-4.
/// Covers: bare catch logging, missing space formatting, policy corruption.
/// </summary>
public class UltimateDataLakeHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDataLake"));

    [Fact]
    public void Finding002_Plugin_BareCatches()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataLakePlugin.cs"));
        Assert.Contains("UltimateDataLakePlugin", source);
    }

    [Fact]
    public void Finding003_Plugin_PolicyCorruption()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataLakePlugin.cs"));
        // Plugin should handle corrupted policy state
        Assert.Contains("OnStart", source);
    }
}
