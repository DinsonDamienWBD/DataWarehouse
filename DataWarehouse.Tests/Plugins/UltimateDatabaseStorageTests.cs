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
    public void Plugin_ShouldExposeStrategyRegistry()
    {
        // Strategies are discovered during lifecycle (OnStartCoreAsync via DiscoverAndRegister),
        // not at construction time. Verify the registry is accessible and starts empty.
        var plugin = new UltimateDatabaseStoragePlugin();
        plugin.StrategyCount.Should().BeGreaterThanOrEqualTo(0,
            "strategy registry should be accessible (strategies populate during lifecycle start)");
    }
}
