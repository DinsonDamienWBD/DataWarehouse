using DataWarehouse.Plugins.AdaptiveTransport;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for AdaptiveTransport plugin.
/// Validates plugin identity, protocol state, and configuration defaults.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "AdaptiveTransport")]
public class AdaptiveTransportTests
{
    [Fact]
    public void AdaptiveTransportPlugin_CanBeConstructed_WithDefaultConfig()
    {
        // Act
        var plugin = new AdaptiveTransportPlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void AdaptiveTransportPlugin_HasCorrectId()
    {
        // Arrange
        var plugin = new AdaptiveTransportPlugin();

        // Assert
        Assert.Equal("datawarehouse.plugins.transport.adaptive", plugin.Id);
    }

    [Fact]
    public void AdaptiveTransportPlugin_HasCorrectNameAndVersion()
    {
        // Arrange
        var plugin = new AdaptiveTransportPlugin();

        // Assert
        Assert.Equal("Adaptive Transport (Protocol Morphing)", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void AdaptiveTransportPlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        var plugin = new AdaptiveTransportPlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void AdaptiveTransportPlugin_DefaultProtocolIsTcp()
    {
        // Arrange
        var plugin = new AdaptiveTransportPlugin();

        // Assert
        Assert.Equal(TransportProtocol.Tcp, plugin.CurrentProtocol);
    }

    [Fact]
    public void AdaptiveTransportConfig_DefaultValues_AreReasonable()
    {
        // Arrange
        var config = new AdaptiveTransportConfig();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), config.QualityCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), config.QualityCheckTimeout);
        Assert.Equal(5, config.ProbeCount);
        Assert.Equal(1400, config.UdpChunkSize);
    }

    [Fact]
    public void AdaptiveTransportPlugin_CanBeConstructed_WithCustomConfig()
    {
        // Arrange
        var config = new AdaptiveTransportConfig
        {
            QualityCheckInterval = TimeSpan.FromSeconds(60),
            ProbeCount = 10
        };

        // Act
        var plugin = new AdaptiveTransportPlugin(config);

        // Assert
        Assert.NotNull(plugin);
        Assert.Equal("datawarehouse.plugins.transport.adaptive", plugin.Id);
    }
}
