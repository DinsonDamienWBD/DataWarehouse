using Xunit;
using DataWarehouse.Plugins.UltimateFilesystem;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateFilesystemTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateFilesystemPlugin();

        Assert.Equal("com.datawarehouse.filesystem.ultimate", plugin.Id);
        Assert.Equal("Ultimate Filesystem", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.StorageProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateFilesystemPlugin();

        Assert.True(plugin.Registry.Count > 0, "Should auto-discover filesystem strategies");
        var all = plugin.Registry.GetAll();
        Assert.NotEmpty(all);
    }

    [Fact]
    public void DefaultStrategy_IsAutoDetect()
    {
        using var plugin = new UltimateFilesystemPlugin();

        Assert.Equal("auto-detect", plugin.DefaultStrategy);
    }

    [Fact]
    public void SemanticDescription_ContainsFilesystem()
    {
        using var plugin = new UltimateFilesystemPlugin();

        Assert.Contains("filesystem", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("storage", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticTags_ContainsExpectedTags()
    {
        using var plugin = new UltimateFilesystemPlugin();

        Assert.Contains("filesystem", plugin.SemanticTags);
        Assert.Contains("ntfs", plugin.SemanticTags);
        Assert.Contains("ext4", plugin.SemanticTags);
    }
}
