using DataWarehouse.Plugins.UltimateFilesystem;
using DataWarehouse.Plugins.UltimateFilesystem.Strategies;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for WindowsWinFspFilesystemStrategy (consolidated from WinFspDriver plugin).
/// Validates strategy identity, capabilities, and detection behavior.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateFilesystem")]
public class WinFspDriverTests
{
    [Fact]
    public void WindowsWinFspFilesystemStrategy_CanBeConstructed()
    {
        // Act
        var strategy = new WindowsWinFspFilesystemStrategy();

        // Assert
        Assert.NotNull(strategy);
    }

    [Fact]
    public void WindowsWinFspFilesystemStrategy_HasCorrectIdentity()
    {
        // Arrange
        var strategy = new WindowsWinFspFilesystemStrategy();

        // Assert
        Assert.Equal("driver-windows-winfsp", strategy.StrategyId);
        Assert.Equal("Windows WinFSP Driver", strategy.DisplayName);
    }

    [Fact]
    public void WindowsWinFspFilesystemStrategy_CategoryIsVirtual()
    {
        // Arrange
        var strategy = new WindowsWinFspFilesystemStrategy();

        // Assert
        Assert.Equal(FilesystemStrategyCategory.Virtual, strategy.Category);
    }

    [Fact]
    public void WindowsWinFspFilesystemStrategy_HasCorrectCapabilities()
    {
        // Arrange
        var strategy = new WindowsWinFspFilesystemStrategy();

        // Assert
        Assert.True(strategy.Capabilities.SupportsDirectIo);
        Assert.True(strategy.Capabilities.SupportsAsyncIo);
        Assert.True(strategy.Capabilities.SupportsMmap);
        Assert.True(strategy.Capabilities.SupportsAutoDetect);
    }

    [Fact]
    public void WindowsWinFspFilesystemStrategy_HasSemanticDescription()
    {
        // Arrange
        var strategy = new WindowsWinFspFilesystemStrategy();

        // Assert
        Assert.Contains("WinFSP", strategy.SemanticDescription);
        Assert.Contains("Windows", strategy.SemanticDescription);
    }

    [Fact]
    public void WindowsWinFspFilesystemStrategy_HasTags()
    {
        // Arrange
        var strategy = new WindowsWinFspFilesystemStrategy();

        // Assert
        Assert.Contains("winfsp", strategy.Tags);
        Assert.Contains("windows", strategy.Tags);
        Assert.Contains("ntfs", strategy.Tags);
    }
}
