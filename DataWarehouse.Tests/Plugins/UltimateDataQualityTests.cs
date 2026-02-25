using DataWarehouse.Plugins.UltimateDataQuality;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataQualityTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataQualityPlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Quality");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataQualityCategory_ShouldCoverAllDimensions()
    {
        var categories = Enum.GetValues<DataQualityCategory>();
        categories.Should().Contain(DataQualityCategory.Validation);
        categories.Should().Contain(DataQualityCategory.Profiling);
        categories.Should().Contain(DataQualityCategory.Cleansing);
        categories.Should().Contain(DataQualityCategory.DuplicateDetection);
        categories.Should().Contain(DataQualityCategory.Standardization);
        categories.Should().Contain(DataQualityCategory.Scoring);
        categories.Should().Contain(DataQualityCategory.Monitoring);
        categories.Should().Contain(DataQualityCategory.Reporting);
    }

    [Fact]
    public void DataQualityCapabilities_ShouldBeConstructable()
    {
        var caps = new DataQualityCapabilities
        {
            SupportsAsync = true,
            SupportsBatch = true,
            SupportsStreaming = false,
            SupportsDistributed = true,
            SupportsIncremental = true
        };
        caps.SupportsAsync.Should().BeTrue();
        caps.SupportsStreaming.Should().BeFalse();
        caps.SupportsDistributed.Should().BeTrue();
        caps.SupportsIncremental.Should().BeTrue();
    }

    [Fact]
    public void IDataQualityStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataQualityStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("DisplayName").Should().NotBeNull();
        iface.GetProperty("Category").Should().NotBeNull();
    }
}
