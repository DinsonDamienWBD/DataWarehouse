using DataWarehouse.Plugins.UltimateDataFabric;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataFabricTests
{
    [Fact]
    public void StarTopologyStrategy_ShouldHaveCorrectCharacteristics()
    {
        var strategy = new StarTopologyStrategy();
        strategy.Characteristics.StrategyName.Should().Be("StarTopology");
        strategy.Characteristics.Category.Should().Be(FabricCategory.Topology);
        strategy.Characteristics.Capabilities.SupportsDistributed.Should().BeTrue();
        strategy.Characteristics.Tags.Should().Contain("star");
    }

    [Fact]
    public void MeshTopologyStrategy_ShouldHaveReducedMaxNodes()
    {
        var strategy = new MeshTopologyStrategy();
        strategy.Characteristics.Category.Should().Be(FabricCategory.Topology);
        strategy.Characteristics.Capabilities.MaxNodes.Should().Be(500);
        strategy.Characteristics.Tags.Should().Contain("mesh");
    }

    [Fact]
    public void FederatedTopologyStrategy_ShouldSupportLargeScale()
    {
        var strategy = new FederatedTopologyStrategy();
        strategy.Characteristics.Capabilities.MaxNodes.Should().Be(10000);
        strategy.Characteristics.Capabilities.SupportsSemanticLayer.Should().BeTrue();
        strategy.Characteristics.Capabilities.SupportsCatalog.Should().BeTrue();
    }

    [Fact]
    public async Task TopologyStrategy_ShouldExecuteSuccessfully()
    {
        var strategy = new StarTopologyStrategy();
        var request = new FabricRequest { OperationId = "test-1", Operation = "deploy" };
        var result = await strategy.ExecuteAsync(request);
        result.OperationId.Should().Be("test-1");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Plugin_Registry_ShouldRegisterAndRetrieve()
    {
        var plugin = new UltimateDataFabricPlugin();

        plugin.Registry.Count.Should().BeGreaterThan(0);
        plugin.Registry.Get("StarTopology").Should().NotBeNull();
        plugin.Registry.Get("MeshTopology").Should().NotBeNull();
        plugin.Registry.Get("NonExistent").Should().BeNull();
    }

    [Fact]
    public void FabricCategory_ShouldIncludeAllTypes()
    {
        var categories = Enum.GetValues<FabricCategory>();
        categories.Should().Contain(FabricCategory.Topology);
        categories.Should().Contain(FabricCategory.Virtualization);
        categories.Should().Contain(FabricCategory.MeshIntegration);
        categories.Should().Contain(FabricCategory.AIEnhanced);
    }

    [Fact]
    public void TopologyType_ShouldEnumerateAllVariants()
    {
        var types = Enum.GetValues<TopologyType>();
        types.Should().Contain(TopologyType.Star);
        types.Should().Contain(TopologyType.Mesh);
        types.Should().Contain(TopologyType.Federated);
        types.Should().Contain(TopologyType.Hybrid);
    }
}
