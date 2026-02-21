using DataWarehouse.Plugins.UltimateDatabaseStorage;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDatabaseStorageTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDatabaseStoragePlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().NotBeNullOrWhiteSpace();
        plugin.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Plugin_ShouldDiscoverStrategies()
    {
        var plugin = new UltimateDatabaseStoragePlugin();
        plugin.StrategyCount.Should().BeGreaterThan(0, "strategies are discovered from the assembly");
    }
}
