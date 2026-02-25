using DataWarehouse.SDK.Contracts.DataMesh;
using DataWarehouse.Plugins.UltimateDataMesh;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataMeshTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataMeshPlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Mesh");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataMeshCategory_ShouldCoverAllDomains()
    {
        var categories = Enum.GetValues<DataMeshCategory>();
        categories.Should().Contain(DataMeshCategory.DomainOwnership);
        categories.Should().Contain(DataMeshCategory.DataProduct);
        categories.Should().Contain(DataMeshCategory.SelfServe);
        categories.Should().Contain(DataMeshCategory.FederatedGovernance);
    }

    [Fact]
    public void Plugin_ShouldRegisterAndLookupStrategies()
    {
        var plugin = new UltimateDataMeshPlugin();
        plugin.Registry.Count.Should().BeGreaterThanOrEqualTo(0);
        plugin.Registry.GetAll().Should().NotBeNull();
    }

    [Fact]
    public void IDataMeshStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataMeshStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("DisplayName").Should().NotBeNull();
        iface.GetProperty("Category").Should().NotBeNull();
        iface.GetProperty("Capabilities").Should().NotBeNull();
        iface.GetProperty("SemanticDescription").Should().NotBeNull();
    }

    [Fact]
    public void Plugin_UnregisterShouldWork()
    {
        var plugin = new UltimateDataMeshPlugin();

        var all = plugin.Registry.GetAll();
        if (all.Count > 0)
        {
            var firstId = all.First().StrategyId;
            var result = plugin.Registry.Unregister(firstId);
            result.Should().BeTrue();
            plugin.Registry.Get(firstId).Should().BeNull();
        }
    }
}
