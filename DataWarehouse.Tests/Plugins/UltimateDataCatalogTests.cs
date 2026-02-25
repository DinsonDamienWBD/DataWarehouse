using DataWarehouse.Plugins.UltimateDataCatalog;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataCatalogTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataCatalogPlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Catalog");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataCatalogCategory_ShouldCoverAllSubsystems()
    {
        var categories = Enum.GetValues<DataCatalogCategory>();
        categories.Should().Contain(DataCatalogCategory.AssetDiscovery);
        categories.Should().Contain(DataCatalogCategory.SchemaRegistry);
        categories.Should().Contain(DataCatalogCategory.SearchDiscovery);
        categories.Should().Contain(DataCatalogCategory.DataRelationships);
        categories.Should().Contain(DataCatalogCategory.AccessControl);
        categories.Should().Contain(DataCatalogCategory.CatalogApi);
        categories.Should().Contain(DataCatalogCategory.CatalogUI);
    }

    [Fact]
    public void DataCatalogCapabilities_ShouldBeConstructable()
    {
        var caps = new DataCatalogCapabilities
        {
            SupportsAsync = true,
            SupportsBatch = true,
            SupportsRealTime = false,
            SupportsFederation = true,
            SupportsVersioning = true,
            SupportsMultiTenancy = false,
            MaxEntries = 10_000_000
        };
        caps.SupportsAsync.Should().BeTrue();
        caps.SupportsBatch.Should().BeTrue();
        caps.SupportsRealTime.Should().BeFalse();
        caps.MaxEntries.Should().Be(10_000_000);
    }

    [Fact]
    public void IDataCatalogStrategy_ShouldDefineExpectedProperties()
    {
        var iface = typeof(IDataCatalogStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("DisplayName").Should().NotBeNull();
        iface.GetProperty("Category").Should().NotBeNull();
        iface.GetProperty("Capabilities").Should().NotBeNull();
        iface.GetProperty("SemanticDescription").Should().NotBeNull();
    }
}
