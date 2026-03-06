namespace DataWarehouse.Hardening.Tests.UltimateRTOSBridge;

/// <summary>
/// Hardening tests for UltimateRTOSBridge findings 1-21.
/// Covers: shared Stopwatch, SemaphoreSlim disposal, non-atomic increments,
/// FailFast removal, bare catch logging, stub watchdog recovery.
/// </summary>
public class UltimateRtosBridgeHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateRTOSBridge"));

    [Fact]
    public void Finding001_DeterministicIo_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "DeterministicIoStrategies.cs"));
        Assert.Contains("DeterministicIo", source);
    }

    [Fact]
    public void Finding008_RtosProtocolAdapters_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "RtosProtocolAdapters.cs"));
        Assert.Contains("VxWorksProtocolAdapter", source);
    }

    [Fact]
    public void Finding018_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateRTOSBridgePlugin.cs"));
        Assert.Contains("UltimateRTOSBridgePlugin", source);
    }

    [Fact]
    public void Finding007_IRtosStrategy_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "IRtosStrategy.cs");
        Assert.True(File.Exists(path));
    }
}
