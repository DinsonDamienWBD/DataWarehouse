using DataWarehouse.Plugins.AppPlatform;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for AppPlatform plugin.
/// Validates plugin identity, capability reporting, and platform domain.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "AppPlatform")]
public class AppPlatformTests
{
    [Fact]
    public void AppPlatformPlugin_HasCorrectId()
    {
        // Arrange
        var plugin = new AppPlatformPlugin();

        // Assert
        Assert.Equal("com.datawarehouse.platform.app", plugin.Id);
    }

    [Fact]
    public void AppPlatformPlugin_HasCorrectNameAndVersion()
    {
        // Arrange
        var plugin = new AppPlatformPlugin();

        // Assert
        Assert.Equal("Application Platform Services", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void AppPlatformPlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        var plugin = new AppPlatformPlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void AppPlatformPlugin_PlatformDomainIsAppPlatform()
    {
        // Arrange
        var plugin = new AppPlatformPlugin();

        // Assert
        Assert.Equal("AppPlatform", plugin.PlatformDomain);
    }

    [Fact]
    public void AppPlatformPlugin_ImplementsIDisposable()
    {
        // Act
        var plugin = new AppPlatformPlugin();
        var exception = Record.Exception(() => plugin.Dispose());

        // Assert
        Assert.Null(exception);
    }
}
