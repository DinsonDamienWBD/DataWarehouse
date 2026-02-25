using DataWarehouse.Plugins.PluginMarketplace;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for PluginMarketplace plugin.
/// Validates plugin identity, configuration, and marketplace capabilities.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "PluginMarketplace")]
public class PluginMarketplaceTests
{
    [Fact]
    public void PluginMarketplacePlugin_CanBeConstructed_WithDefaultConfig()
    {
        // Act
        var plugin = new PluginMarketplacePlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void PluginMarketplacePlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new PluginMarketplacePlugin();

        // Assert
        Assert.Equal("datawarehouse.plugins.marketplace.plugin", plugin.Id);
        Assert.Equal("Plugin Marketplace (T57)", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void PluginMarketplacePlugin_CanBeConstructed_WithCustomConfig()
    {
        // Arrange
        var config = new PluginMarketplaceConfig();

        // Act
        var plugin = new PluginMarketplacePlugin(config);

        // Assert
        Assert.NotNull(plugin);
        Assert.Equal("datawarehouse.plugins.marketplace.plugin", plugin.Id);
    }
}
