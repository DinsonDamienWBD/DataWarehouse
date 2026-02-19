using DataWarehouse.Plugins.AedsCore;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for AEDS Core plugin.
/// Validates plugin identity, manifest validation, and null argument handling.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "AedsCore")]
public class AedsCoreTests
{
    private readonly Mock<ILogger<AedsCorePlugin>> _loggerMock = new();

    [Fact]
    public void AedsCorePlugin_CanBeConstructed()
    {
        // Act
        var plugin = new AedsCorePlugin(_loggerMock.Object);

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void AedsCorePlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new AedsCorePlugin(_loggerMock.Object);

        // Assert
        Assert.Equal("com.datawarehouse.aeds.core", plugin.Id);
        Assert.Equal("AEDS Core", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void AedsCorePlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        var plugin = new AedsCorePlugin(_loggerMock.Object);

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void AedsCorePlugin_OrchestrationModeIsAedsCore()
    {
        // Arrange
        var plugin = new AedsCorePlugin(_loggerMock.Object);

        // Assert
        Assert.Equal("AedsCore", plugin.OrchestrationMode);
    }

    [Fact]
    public void AedsCorePlugin_Constructor_ThrowsOnNullLogger()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AedsCorePlugin(null!));
    }

    [Fact]
    public async Task AedsCorePlugin_ValidateManifestAsync_ThrowsOnNullManifest()
    {
        // Arrange
        var plugin = new AedsCorePlugin(_loggerMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => plugin.ValidateManifestAsync(null!));
    }
}
