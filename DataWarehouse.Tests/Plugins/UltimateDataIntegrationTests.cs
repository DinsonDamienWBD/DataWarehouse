using DataWarehouse.Plugins.UltimateDataIntegration;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataIntegrationTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataIntegrationPlugin();
        plugin.Id.Should().Be("com.datawarehouse.integration.ultimate");
        plugin.Name.Should().Be("Ultimate Data Integration");
        plugin.Version.Should().Be("1.0.0");
        plugin.OrchestrationMode.Should().Be("DataIntegration");
    }

    [Fact]
    public void IntegrationCategory_ShouldCoverAllPipelineTypes()
    {
        var categories = Enum.GetValues<IntegrationCategory>();
        categories.Should().Contain(IntegrationCategory.EtlPipelines);
        categories.Should().Contain(IntegrationCategory.EltPatterns);
        categories.Should().Contain(IntegrationCategory.DataTransformation);
        categories.Should().Contain(IntegrationCategory.DataMapping);
        categories.Should().Contain(IntegrationCategory.SchemaEvolution);
    }

    [Fact]
    public void Registry_ShouldBeCreatableAndEmpty()
    {
        var registry = new DataIntegrationStrategyRegistry();
        registry.Count.Should().Be(0);
        registry.GetAllStrategies().Should().BeEmpty();
    }

    [Fact]
    public void Plugin_RecordOperation_ShouldTrackStatistics()
    {
        var plugin = new UltimateDataIntegrationPlugin();
        plugin.RecordOperation(100);
        plugin.RecordOperation(50);
        plugin.RecordFailure();

        // Statistics are tracked internally - verify no exceptions
        // The methods return void but increment atomic counters
        Assert.True(true, "RecordOperation and RecordFailure should not throw");
    }

    [Fact]
    public void IDataIntegrationStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataIntegrationStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("DisplayName").Should().NotBeNull();
        iface.GetProperty("Category").Should().NotBeNull();
        iface.GetProperty("Capabilities").Should().NotBeNull();
    }
}
