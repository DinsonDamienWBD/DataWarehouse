using DataWarehouse.Plugins.AirGapBridge;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for Air-Gap Bridge plugin.
/// Validates plugin identity, device management, and construction modes.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "AirGapBridge")]
public class AirGapBridgeTests
{
    [Fact]
    public void AirGapBridgePlugin_CanBeConstructed_WithDefaultConstructor()
    {
        // Act
        using var plugin = new AirGapBridgePlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void AirGapBridgePlugin_HasCorrectIdentity()
    {
        // Arrange
        using var plugin = new AirGapBridgePlugin();

        // Assert
        Assert.Equal("com.datawarehouse.airgap.bridge", plugin.Id);
        Assert.Equal("Air-Gap Bridge (The Mule)", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void AirGapBridgePlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        using var plugin = new AirGapBridgePlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void AirGapBridgePlugin_InfrastructureDomainIsAirGap()
    {
        // Arrange
        using var plugin = new AirGapBridgePlugin();

        // Assert
        Assert.Equal("AirGap", plugin.InfrastructureDomain);
    }

    [Fact]
    public void AirGapBridgePlugin_DevicesInitiallyEmpty()
    {
        // Arrange
        using var plugin = new AirGapBridgePlugin();

        // Assert
        Assert.NotNull(plugin.Devices);
        Assert.Empty(plugin.Devices);
    }

    [Fact]
    public void AirGapBridgePlugin_CanBeConstructed_WithInstanceIdAndMasterKey()
    {
        // Arrange
        var instanceId = "test-instance-001";
        var masterKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(masterKey);

        // Act
        using var plugin = new AirGapBridgePlugin(instanceId, masterKey);

        // Assert
        Assert.NotNull(plugin);
        Assert.Equal("com.datawarehouse.airgap.bridge", plugin.Id);
    }

    [Fact]
    public void AirGapBridgePlugin_Dispose_DoesNotThrowOnMultipleCalls()
    {
        // Arrange
        var plugin = new AirGapBridgePlugin();

        // Act
        plugin.Dispose();
        var exception = Record.Exception(() => plugin.Dispose());

        // Assert
        Assert.Null(exception);
    }
}
