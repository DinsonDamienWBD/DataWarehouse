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
    public void Registry_ShouldSupportRegistrationAndDiscovery()
    {
        var registry = new DatabaseStorageStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateDatabaseStoragePlugin).Assembly);

        var all = registry.GetAllStrategies();
        all.Should().NotBeEmpty("strategies are discovered from the assembly");
    }

    [Fact]
    public void Registry_ShouldLookupByCategory()
    {
        var registry = new DatabaseStorageStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateDatabaseStoragePlugin).Assembly);

        var relational = registry.GetStrategiesByCategory(DataWarehouse.SDK.Database.DatabaseCategory.Relational);
        relational.Should().NotBeNull();

        foreach (var strategy in relational)
        {
            strategy.DatabaseCategory.Should().Be(DataWarehouse.SDK.Database.DatabaseCategory.Relational);
        }
    }

    [Fact]
    public void Registry_ShouldSetAndGetDefaultStrategy()
    {
        var registry = new DatabaseStorageStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateDatabaseStoragePlugin).Assembly);

        var defaultStrategy = registry.GetDefaultStrategy();
        defaultStrategy.Should().NotBeNull("a default strategy should exist after discovery");
    }
}
