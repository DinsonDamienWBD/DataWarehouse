using Xunit;
using DataWarehouse.Plugins.UltimateStorageProcessing;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateStorageProcessingTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateStorageProcessingPlugin();

        Assert.Equal("com.datawarehouse.storageprocessing.ultimate", plugin.Id);
        Assert.Equal("Ultimate Storage Processing", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void GetStrategy_ReturnsNullForUnknown()
    {
        using var plugin = new UltimateStorageProcessingPlugin();

        var strategy = plugin.GetStrategy("non-existent-strategy");
        Assert.Null(strategy);
    }

    [Fact]
    public void SemanticDescription_ContainsStorageProcessing()
    {
        using var plugin = new UltimateStorageProcessingPlugin();

        Assert.Contains("storage processing", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compression", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticTags_ContainsExpectedCategories()
    {
        using var plugin = new UltimateStorageProcessingPlugin();

        Assert.Contains("storage-processing", plugin.SemanticTags);
        Assert.Contains("compression", plugin.SemanticTags);
        Assert.Contains("media", plugin.SemanticTags);
    }
}
