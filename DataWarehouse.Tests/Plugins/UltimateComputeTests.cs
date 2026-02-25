using DataWarehouse.Plugins.UltimateCompute;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for UltimateCompute plugin.
/// Validates plugin identity, semantic metadata, and strategy registry.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateCompute")]
public class UltimateComputeTests
{
    [Fact]
    public void UltimateComputePlugin_CanBeConstructed()
    {
        // Act
        using var plugin = new UltimateComputePlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void UltimateComputePlugin_HasCorrectIdentity()
    {
        // Arrange
        using var plugin = new UltimateComputePlugin();

        // Assert
        Assert.Equal("com.datawarehouse.compute.ultimate", plugin.Id);
        Assert.Equal("Ultimate Compute", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void UltimateComputePlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        using var plugin = new UltimateComputePlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void UltimateComputePlugin_RuntimeTypeIsGeneral()
    {
        // Arrange
        using var plugin = new UltimateComputePlugin();

        // Assert
        Assert.Equal("General", plugin.RuntimeType);
    }

    [Fact]
    public void UltimateComputePlugin_SemanticDescriptionIsNotEmpty()
    {
        // Arrange
        using var plugin = new UltimateComputePlugin();

        // Assert
        Assert.NotNull(plugin.SemanticDescription);
        Assert.NotEmpty(plugin.SemanticDescription);
        Assert.Contains("compute", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UltimateComputePlugin_SemanticTagsContainExpectedCategories()
    {
        // Arrange
        using var plugin = new UltimateComputePlugin();

        // Assert
        Assert.NotNull(plugin.SemanticTags);
        Assert.Contains("compute", plugin.SemanticTags);
        Assert.Contains("wasm", plugin.SemanticTags);
        Assert.Contains("container", plugin.SemanticTags);
        Assert.Contains("gpu", plugin.SemanticTags);
    }

    [Fact]
    public void UltimateComputePlugin_Dispose_DoesNotThrowOnMultipleCalls()
    {
        // Arrange
        var plugin = new UltimateComputePlugin();

        // Act
        plugin.Dispose();
        var exception = Record.Exception(() => plugin.Dispose());

        // Assert
        Assert.Null(exception);
    }
}
