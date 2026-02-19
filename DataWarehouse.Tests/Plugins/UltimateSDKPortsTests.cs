using Xunit;
using DataWarehouse.Plugins.UltimateSDKPorts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateSDKPortsTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateSDKPortsPlugin();

        Assert.Equal("com.datawarehouse.sdkports.ultimate", plugin.Id);
        Assert.Equal("Ultimate SDK Ports", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(PluginCategory.InterfaceProvider, plugin.Category);
    }

    [Fact]
    public void Registry_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateSDKPortsPlugin();

        var strategies = plugin.Registry.GetAll().ToList();
        Assert.NotEmpty(strategies);
        Assert.True(strategies.Count > 10, "Should auto-discover many SDK port strategies");
    }

    [Fact]
    public void SemanticDescription_ContainsSDK()
    {
        using var plugin = new UltimateSDKPortsPlugin();

        Assert.Contains("sdk", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("python", plugin.SemanticDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticTags_ContainsExpectedLanguages()
    {
        using var plugin = new UltimateSDKPortsPlugin();

        Assert.Contains("python", plugin.SemanticTags);
        Assert.Contains("go", plugin.SemanticTags);
        Assert.Contains("rust", plugin.SemanticTags);
        Assert.Contains("grpc", plugin.SemanticTags);
    }

    [Fact]
    public void PlatformDomain_IsSDKPorts()
    {
        using var plugin = new UltimateSDKPortsPlugin();

        Assert.Equal("SDKPorts", plugin.PlatformDomain);
    }
}
