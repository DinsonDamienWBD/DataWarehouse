using DataWarehouse.Plugins.FuseDriver;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for FuseDriver plugin.
/// Validates plugin identity, platform detection, and configuration.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "FuseDriver")]
public class FuseDriverTests
{
    [Fact]
    public void FuseDriverPlugin_CanBeConstructed_WithDefaultConstructor()
    {
        // Act
        using var plugin = new FuseDriverPlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void FuseDriverPlugin_HasCorrectIdentity()
    {
        // Arrange
        using var plugin = new FuseDriverPlugin();

        // Assert
        Assert.Equal("com.datawarehouse.plugins.filesystem.fuse", plugin.Id);
        Assert.Equal("FUSE Filesystem Driver", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void FuseDriverPlugin_CategoryIsInterfaceProvider()
    {
        // Arrange
        using var plugin = new FuseDriverPlugin();

        // Assert
        Assert.Equal(PluginCategory.InterfaceProvider, plugin.Category);
    }

    [Fact]
    public void FuseDriverPlugin_ProtocolIsFuse()
    {
        // Arrange
        using var plugin = new FuseDriverPlugin();

        // Assert
        Assert.Equal("FUSE", plugin.Protocol);
    }

    [Fact]
    public void FuseDriverPlugin_PlatformDetection_ReturnsValidValue()
    {
        // Arrange
        using var plugin = new FuseDriverPlugin();

        // Assert - on Windows, FUSE is not supported; on Linux/macOS it may be
        var platform = plugin.Platform;
        Assert.True(Enum.IsDefined(typeof(FusePlatform), platform));
    }

    [Fact]
    public void FuseConfig_DefaultValues_AreReasonable()
    {
        // Act
        var config = new FuseConfig();

        // Assert
        Assert.NotNull(config);
        // CurrentPlatform should return a valid enum value
        var platform = FuseConfig.CurrentPlatform;
        Assert.True(Enum.IsDefined(typeof(FusePlatform), platform));
    }

    [Fact]
    public void FuseDriverPlugin_CanBeConstructed_WithConfig()
    {
        // Arrange
        var config = new FuseConfig();

        // Act
        using var plugin = new FuseDriverPlugin(config);

        // Assert
        Assert.NotNull(plugin);
        Assert.Equal("com.datawarehouse.plugins.filesystem.fuse", plugin.Id);
    }
}
