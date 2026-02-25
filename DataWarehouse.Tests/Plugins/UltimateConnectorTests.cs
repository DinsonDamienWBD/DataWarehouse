using DataWarehouse.Plugins.UltimateConnector;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for UltimateConnector plugin.
/// Validates plugin identity, strategy registry, and semantic metadata.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateConnector")]
public class UltimateConnectorTests
{
    [Fact]
    public void UltimateConnectorPlugin_CanBeConstructed()
    {
        // Act
        var plugin = new UltimateConnectorPlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void UltimateConnectorPlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new UltimateConnectorPlugin();

        // Assert
        Assert.Equal("com.datawarehouse.connector.ultimate", plugin.Id);
        Assert.Equal("Ultimate Connector", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void UltimateConnectorPlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        var plugin = new UltimateConnectorPlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void UltimateConnectorPlugin_ProtocolIsUniversalMultiProtocol()
    {
        // Arrange
        var plugin = new UltimateConnectorPlugin();

        // Assert
        Assert.Equal("universal-multi-protocol", plugin.Protocol);
    }

    [Fact]
    public void UltimateConnectorPlugin_SemanticDescriptionIsNotEmpty()
    {
        // Arrange
        var plugin = new UltimateConnectorPlugin();

        // Assert
        Assert.NotNull(plugin.SemanticDescription);
        Assert.NotEmpty(plugin.SemanticDescription);
        Assert.Contains("connector", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UltimateConnectorPlugin_SemanticTagsContainExpectedCategories()
    {
        // Arrange
        var plugin = new UltimateConnectorPlugin();

        // Assert
        Assert.NotNull(plugin.SemanticTags);
        Assert.Contains("connector", plugin.SemanticTags);
        Assert.Contains("database", plugin.SemanticTags);
        Assert.Contains("messaging", plugin.SemanticTags);
    }

    [Fact]
    public void ConnectionStrategyRegistry_CanBeInstantiated()
    {
        // Act
        var registry = new ConnectionStrategyRegistry();

        // Assert
        Assert.NotNull(registry);
        var all = registry.GetAll();
        Assert.NotNull(all);
    }
}
