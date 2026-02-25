using Xunit;
using DataWarehouse.Plugins.UltimateSustainability;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateSustainabilityTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        var plugin = new UltimateSustainabilityPlugin();

        Assert.Equal("com.datawarehouse.sustainability.ultimate", plugin.Id);
        Assert.Equal("Ultimate Sustainability", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(SDK.Primitives.PluginCategory.MetricsProvider, plugin.Category);
    }

    [Fact]
    public void Plugin_AutoDiscoversStrategies()
    {
        var plugin = new UltimateSustainabilityPlugin();

        var strategies = plugin.GetRegisteredStrategies();
        Assert.NotNull(strategies);
        Assert.True(strategies.Count > 0, "Should auto-discover sustainability strategies");
    }

    [Fact]
    public void GetStrategy_ReturnsNullForUnknown()
    {
        var plugin = new UltimateSustainabilityPlugin();

        var strategy = plugin.GetStrategy("non-existent-strategy");
        Assert.Null(strategy);
    }

    [Fact]
    public void InfrastructureDomain_IsSustainability()
    {
        var plugin = new UltimateSustainabilityPlugin();

        Assert.Equal("Sustainability", plugin.InfrastructureDomain);
    }

    [Fact]
    public void GetAggregateStatistics_ReturnsStatistics()
    {
        var plugin = new UltimateSustainabilityPlugin();

        var stats = plugin.GetAggregateStatistics();
        Assert.NotNull(stats);
    }
}
