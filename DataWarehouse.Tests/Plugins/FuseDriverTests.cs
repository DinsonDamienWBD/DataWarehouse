using DataWarehouse.Plugins.UltimateFilesystem;
using DataWarehouse.Plugins.UltimateFilesystem.Strategies;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for UnixFuseFilesystemStrategy (consolidated from FuseDriver plugin).
/// Validates strategy identity, capabilities, and detection behavior.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateFilesystem")]
public class FuseDriverTests
{
    [Fact]
    public void UnixFuseFilesystemStrategy_CanBeConstructed()
    {
        // Act
        var strategy = new UnixFuseFilesystemStrategy();

        // Assert
        Assert.NotNull(strategy);
    }

    [Fact]
    public void UnixFuseFilesystemStrategy_HasCorrectIdentity()
    {
        // Arrange
        var strategy = new UnixFuseFilesystemStrategy();

        // Assert
        Assert.Equal("driver-unix-fuse", strategy.StrategyId);
        Assert.Equal("Unix FUSE Driver", strategy.DisplayName);
    }

    [Fact]
    public void UnixFuseFilesystemStrategy_CategoryIsVirtual()
    {
        // Arrange
        var strategy = new UnixFuseFilesystemStrategy();

        // Assert
        Assert.Equal(FilesystemStrategyCategory.Virtual, strategy.Category);
    }

    [Fact]
    public void UnixFuseFilesystemStrategy_HasCorrectCapabilities()
    {
        // Arrange
        var strategy = new UnixFuseFilesystemStrategy();

        // Assert
        Assert.True(strategy.Capabilities.SupportsDirectIo);
        Assert.True(strategy.Capabilities.SupportsAsyncIo);
        Assert.True(strategy.Capabilities.SupportsMmap);
        Assert.True(strategy.Capabilities.SupportsAutoDetect);
    }

    [Fact]
    public void UnixFuseFilesystemStrategy_HasSemanticDescription()
    {
        // Arrange
        var strategy = new UnixFuseFilesystemStrategy();

        // Assert
        Assert.Contains("FUSE", strategy.SemanticDescription);
        Assert.Contains("POSIX", strategy.SemanticDescription);
    }

    [Fact]
    public void UnixFuseFilesystemStrategy_HasTags()
    {
        // Arrange
        var strategy = new UnixFuseFilesystemStrategy();

        // Assert
        Assert.Contains("fuse", strategy.Tags);
        Assert.Contains("unix", strategy.Tags);
        Assert.Contains("linux", strategy.Tags);
    }
}
