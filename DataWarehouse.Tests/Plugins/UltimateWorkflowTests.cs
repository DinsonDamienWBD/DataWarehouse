using Xunit;
using DataWarehouse.Plugins.UltimateWorkflow;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateWorkflowTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateWorkflowPlugin();

        Assert.Equal("com.datawarehouse.workflow.ultimate", plugin.Id);
        Assert.Equal("Ultimate Workflow", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.OrchestrationProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateWorkflowPlugin();

        var strategies = plugin.Registry.GetAll().ToList();
        Assert.NotEmpty(strategies);
        Assert.True(strategies.Count > 10, "Should auto-discover many workflow strategies");
    }

    [Fact]
    public void Registry_GetStrategy_ReturnsNullForUnknown()
    {
        using var plugin = new UltimateWorkflowPlugin();

        var strategy = plugin.Registry.Get("non-existent");
        Assert.Null(strategy);
    }

    [Fact]
    public void SemanticDescription_ContainsWorkflow()
    {
        using var plugin = new UltimateWorkflowPlugin();

        Assert.Contains("workflow", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dag", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrchestrationMode_IsWorkflow()
    {
        using var plugin = new UltimateWorkflowPlugin();

        Assert.Equal("Workflow", plugin.OrchestrationMode);
    }
}
