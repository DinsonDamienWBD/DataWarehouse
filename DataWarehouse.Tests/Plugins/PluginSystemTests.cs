using DataWarehouse.Kernel.Plugins;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for the plugin system contracts (replaces PluginTests.cs).
/// Validates plugin base classes, categories, and lifecycle contracts.
/// </summary>
[Trait("Category", "Unit")]
public class PluginSystemTests
{
    [Fact]
    public void IPlugin_ShouldDefineIdNameVersion()
    {
        var type = typeof(IPlugin);
        type.GetProperty("Id").Should().NotBeNull();
        type.GetProperty("Name").Should().NotBeNull();
        type.GetProperty("Version").Should().NotBeNull();
    }

    [Fact]
    public void PluginBase_ShouldBeAbstractWithCategory()
    {
        var type = typeof(PluginBase);
        type.IsAbstract.Should().BeTrue();
        type.GetProperty("Category").Should().NotBeNull();
    }

    [Fact]
    public void FeaturePluginBase_ShouldInheritPluginBase()
    {
        typeof(FeaturePluginBase).IsSubclassOf(typeof(PluginBase)).Should().BeTrue();
    }

    [Fact]
    public void StorageProviderPluginBase_ShouldInheritPluginBase()
    {
        typeof(StorageProviderPluginBase).IsSubclassOf(typeof(PluginBase)).Should().BeTrue();
    }

    [Fact]
    public void InMemoryStoragePlugin_ShouldBeSealed()
    {
        typeof(InMemoryStoragePlugin).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void InMemoryStoragePlugin_ShouldHaveStableId()
    {
        var plugin = new InMemoryStoragePlugin();
        plugin.Id.Should().Be("datawarehouse.kernel.storage.inmemory");
    }

    [Fact]
    public void InMemoryStoragePlugin_ShouldSupportListing()
    {
        var plugin = new InMemoryStoragePlugin();
        plugin.Should().BeAssignableTo<IListableStorage>();
    }

    [Fact]
    public void PluginCategory_ShouldIncludeStorageAndSecurity()
    {
        var categories = Enum.GetNames<PluginCategory>();
        categories.Should().Contain("StorageProvider");
        categories.Should().Contain("SecurityProvider");
    }

    [Fact]
    public void InMemoryStorageConfig_Unlimited_ShouldHaveNoLimits()
    {
        var config = InMemoryStorageConfig.Unlimited;
        config.MaxMemoryBytes.Should().BeNull();
        config.MaxItemCount.Should().BeNull();
    }

    [Fact]
    public void InMemoryStorageConfig_SmallCache_ShouldHaveReasonableLimits()
    {
        var config = InMemoryStorageConfig.SmallCache;
        config.MaxMemoryBytes.Should().Be(100 * 1024 * 1024);
        config.MaxItemCount.Should().Be(10_000);
    }
}
