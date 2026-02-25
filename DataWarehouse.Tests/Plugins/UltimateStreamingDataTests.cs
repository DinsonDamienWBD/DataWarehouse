using Xunit;
using DataWarehouse.Plugins.UltimateStreamingData;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateStreamingDataTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateStreamingDataPlugin();

        Assert.Equal("com.datawarehouse.streaming.ultimate", plugin.Id);
        Assert.Equal("Ultimate Streaming Data", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.OrchestrationProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateStreamingDataPlugin();

        var strategies = plugin.Registry.GetAllStrategies().ToList();
        Assert.NotEmpty(strategies);
        Assert.True(strategies.Count > 10, "Should auto-discover many streaming strategies");
    }

    [Fact]
    public void SemanticDescription_ContainsStreamingKeywords()
    {
        using var plugin = new UltimateStreamingDataPlugin();

        Assert.Contains("streaming", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kafka", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticTags_ContainsExpectedTags()
    {
        using var plugin = new UltimateStreamingDataPlugin();

        Assert.Contains("streaming", plugin.SemanticTags);
        Assert.Contains("real-time", plugin.SemanticTags);
        Assert.Contains("kafka", plugin.SemanticTags);
        Assert.Contains("event-driven", plugin.SemanticTags);
    }

    [Fact]
    public void AuditEnabled_DefaultsToTrue()
    {
        using var plugin = new UltimateStreamingDataPlugin();

        Assert.True(plugin.AuditEnabled);
    }
}
