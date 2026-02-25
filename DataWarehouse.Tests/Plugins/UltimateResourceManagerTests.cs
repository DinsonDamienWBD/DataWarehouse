using Xunit;
using DataWarehouse.Plugins.UltimateResourceManager;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateResourceManagerTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateResourceManagerPlugin();

        Assert.Equal("com.datawarehouse.resourcemanager.ultimate", plugin.Id);
        Assert.Equal("Ultimate Resource Manager", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void Plugin_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateResourceManagerPlugin();

        var strategies = plugin.Registry.GetAll();
        Assert.NotNull(strategies);
        Assert.True(strategies.Count > 0, "Should auto-discover resource management strategies");
    }

    [Fact]
    public void Plugin_HasSemanticDescription()
    {
        using var plugin = new UltimateResourceManagerPlugin();

        Assert.NotNull(plugin.SemanticDescription);
        Assert.NotEmpty(plugin.SemanticDescription);
        Assert.Contains("resource", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }
}
