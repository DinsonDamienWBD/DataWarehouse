using Xunit;
using DataWarehouse.Plugins.UltimateServerless;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateServerlessTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateServerlessPlugin();

        Assert.Equal("com.datawarehouse.serverless.ultimate", plugin.Id);
        Assert.Equal("Ultimate Serverless", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.OrchestrationProvider, plugin.Category);
    }

    [Fact]
    public void Plugin_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateServerlessPlugin();

        var byCategory = plugin.StrategiesByCategory;
        Assert.NotEmpty(byCategory);
    }

    [Fact]
    public void RegisterFunction_CreatesConfig()
    {
        using var plugin = new UltimateServerlessPlugin();

        plugin.RegisterFunction(new ServerlessFunctionConfig
        {
            FunctionId = "fn-001",
            Name = "ProcessOrder",
            Platform = ServerlessPlatform.AwsLambda,
            Runtime = ServerlessRuntime.DotNet,
            Handler = "OrderHandler::Process",
            MemoryMb = 256,
            TimeoutSeconds = 30
        });

        var fn = plugin.GetFunction("fn-001");
        Assert.NotNull(fn);
        Assert.Equal("ProcessOrder", fn.Name);
        Assert.Equal(256, fn.MemoryMb);
    }

    [Fact]
    public void SemanticDescription_ContainsServerless()
    {
        using var plugin = new UltimateServerlessPlugin();

        Assert.Contains("serverless", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }
}
