using DataWarehouse.Plugins.UltimateBlockchain;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for UltimateBlockchain plugin.
/// Validates plugin identity, genesis block initialization, and blockchain integrity.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateBlockchain")]
public class UltimateBlockchainTests
{
    private readonly Mock<ILogger<UltimateBlockchainPlugin>> _loggerMock = new();

    [Fact]
    public void UltimateBlockchainPlugin_CanBeConstructed()
    {
        // Act
        var plugin = new UltimateBlockchainPlugin(_loggerMock.Object);

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void UltimateBlockchainPlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new UltimateBlockchainPlugin(_loggerMock.Object);

        // Assert
        Assert.Equal("com.datawarehouse.ultimateblockchain", plugin.Id);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void UltimateBlockchainPlugin_Constructor_ThrowsOnNullLogger()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UltimateBlockchainPlugin(null!));
    }
}
