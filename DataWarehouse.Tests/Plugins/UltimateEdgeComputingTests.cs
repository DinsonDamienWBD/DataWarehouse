using Xunit;
using DataWarehouse.Plugins.UltimateEdgeComputing;
using EC = DataWarehouse.SDK.Contracts.EdgeComputing;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateEdgeComputingTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        var plugin = new UltimateEdgeComputingPlugin();

        Assert.Equal("ultimate-edge-computing", plugin.Id);
        Assert.Equal("Ultimate Edge Computing", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void Capabilities_ReportsCorrectFeatures()
    {
        var plugin = new UltimateEdgeComputingPlugin();

        Assert.True(plugin.Capabilities.SupportsOfflineMode);
        Assert.True(plugin.Capabilities.SupportsDeltaSync);
        Assert.True(plugin.Capabilities.SupportsEdgeAnalytics);
        Assert.True(plugin.Capabilities.SupportsEdgeML);
        Assert.True(plugin.Capabilities.SupportsMultiEdge);
        Assert.True(plugin.Capabilities.SupportsFederatedLearning);
        Assert.Equal(10000, plugin.Capabilities.MaxEdgeNodes);
    }

    [Fact]
    public void SupportedProtocols_IncludesMqttAndCoap()
    {
        var plugin = new UltimateEdgeComputingPlugin();

        Assert.Contains("mqtt", plugin.Capabilities.SupportedProtocols);
        Assert.Contains("coap", plugin.Capabilities.SupportedProtocols);
        Assert.Contains("grpc", plugin.Capabilities.SupportedProtocols);
    }

    [Fact]
    public async Task InitializeAsync_CreatesSubsystems()
    {
        var plugin = new UltimateEdgeComputingPlugin();
        var config = new EC.EdgeComputingConfiguration();

        await plugin.InitializeAsync(config);

        Assert.NotNull(plugin.NodeManager);
        Assert.NotNull(plugin.DataSynchronizer);
        Assert.NotNull(plugin.OfflineManager);
        Assert.NotNull(plugin.CloudCommunicator);
        Assert.NotNull(plugin.AnalyticsEngine);
        Assert.NotNull(plugin.SecurityManager);
        Assert.NotNull(plugin.ResourceManager);
        Assert.NotNull(plugin.Orchestrator);
        Assert.NotNull(plugin.FederatedLearning);
    }

    [Fact]
    public async Task InitializeAsync_RegistersStrategies()
    {
        var plugin = new UltimateEdgeComputingPlugin();
        var config = new EC.EdgeComputingConfiguration();

        await plugin.InitializeAsync(config);

        var strategies = plugin.GetAllStrategies();
        Assert.NotEmpty(strategies);
        Assert.NotNull(plugin.GetStrategy("comprehensive"));
        Assert.NotNull(plugin.GetStrategy("iot-gateway"));
    }
}
