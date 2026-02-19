using DataWarehouse.Plugins.DataMarketplace;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for DataMarketplace plugin.
/// Validates plugin identity, configuration defaults, and metadata.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "DataMarketplace")]
public class DataMarketplaceTests
{
    [Fact]
    public void DataMarketplacePlugin_CanBeConstructed_WithDefaultConfig()
    {
        // Act
        var plugin = new DataMarketplacePlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void DataMarketplacePlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new DataMarketplacePlugin();

        // Assert
        Assert.Equal("datawarehouse.plugins.commerce.marketplace", plugin.Id);
        Assert.Equal("Data Marketplace (T83)", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void DataMarketplacePlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        var plugin = new DataMarketplacePlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void DataMarketplacePlugin_PlatformDomainIsDataMarketplace()
    {
        // Arrange
        var plugin = new DataMarketplacePlugin();

        // Assert
        Assert.Equal("DataMarketplace", plugin.PlatformDomain);
    }

    [Fact]
    public void DataMarketplacePlugin_CanBeConstructed_WithCustomConfig()
    {
        // Arrange
        var config = new DataMarketplaceConfig();

        // Act
        var plugin = new DataMarketplacePlugin(config);

        // Assert
        Assert.NotNull(plugin);
        Assert.Equal("datawarehouse.plugins.commerce.marketplace", plugin.Id);
    }
}
