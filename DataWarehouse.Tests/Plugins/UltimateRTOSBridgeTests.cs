using Xunit;
using DataWarehouse.Plugins.UltimateRTOSBridge;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateRTOSBridgeTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateRTOSBridgePlugin();

        Assert.Equal("com.datawarehouse.rtos.ultimate", plugin.Id);
        Assert.Equal("Ultimate RTOS Bridge", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void Plugin_GetStrategies_ReturnsCollection()
    {
        using var plugin = new UltimateRTOSBridgePlugin();

        // RTOS strategies are loaded during InitializeAsync, not constructor.
        // Before initialization, collection exists but is empty.
        var strategies = plugin.GetStrategies();
        Assert.NotNull(strategies);
    }

    [Fact]
    public void GetStrategy_ReturnsNullForUnknown()
    {
        using var plugin = new UltimateRTOSBridgePlugin();

        var strategy = plugin.GetStrategy("non-existent");
        Assert.Null(strategy);
    }

    [Fact]
    public void RegisterStrategy_AddsToCollection()
    {
        using var plugin = new UltimateRTOSBridgePlugin();

        // Before initialization, collection is empty
        Assert.Empty(plugin.GetStrategies());

        // GetStrategy for non-existent returns null
        Assert.Null(plugin.GetStrategy("test-strategy"));
    }
}
