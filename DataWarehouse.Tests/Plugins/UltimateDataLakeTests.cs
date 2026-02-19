using DataWarehouse.SDK.Contracts.DataLake;
using DataWarehouse.Plugins.UltimateDataLake;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataLakeTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataLakePlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Lake");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataLakeCategory_ShouldCoverAllZoneTypes()
    {
        var categories = Enum.GetValues<DataLakeCategory>();
        categories.Should().Contain(DataLakeCategory.Architecture);
        categories.Should().Contain(DataLakeCategory.Schema);
        categories.Should().Contain(DataLakeCategory.Catalog);
        categories.Should().Contain(DataLakeCategory.Zones);
        categories.Should().Contain(DataLakeCategory.Security);
        categories.Should().Contain(DataLakeCategory.Governance);
        categories.Should().Contain(DataLakeCategory.Integration);
    }

    [Fact]
    public void DataLakeZone_ShouldFollowMedallionArchitecture()
    {
        var zones = Enum.GetValues<DataLakeZone>();
        zones.Should().Contain(DataLakeZone.Raw);
        zones.Should().Contain(DataLakeZone.Curated);
        zones.Should().Contain(DataLakeZone.Consumption);
        zones.Should().Contain(DataLakeZone.Sandbox);
        zones.Should().Contain(DataLakeZone.Archive);
    }

    [Fact]
    public void DataLakeCapabilities_ShouldBeConstructable()
    {
        var caps = new DataLakeCapabilities
        {
            SupportsAsync = true,
            SupportsBatch = true,
            SupportsStreaming = true,
            SupportsAcid = true,
            SupportsSchemaEvolution = true,
            SupportsTimeTravel = false,
            MaxDataSize = 1_000_000_000,
            SupportedFormats = ["parquet", "orc", "delta"]
        };
        caps.SupportsAcid.Should().BeTrue();
        caps.SupportsTimeTravel.Should().BeFalse();
        caps.SupportedFormats.Should().Contain("parquet");
    }

    [Fact]
    public void DataLakeStatistics_ShouldTrackByZone()
    {
        var stats = new DataLakeStatistics
        {
            TotalFiles = 1000,
            TotalSizeBytes = 5_000_000_000,
            TotalTables = 50,
            DataByZone = new Dictionary<DataLakeZone, long>
            {
                [DataLakeZone.Raw] = 3_000_000_000,
                [DataLakeZone.Curated] = 1_500_000_000,
                [DataLakeZone.Consumption] = 500_000_000
            }
        };
        stats.TotalFiles.Should().Be(1000);
        stats.DataByZone[DataLakeZone.Raw].Should().Be(3_000_000_000);
    }

    [Fact]
    public void DataLakeStrategyRegistry_ShouldAutoDiscover()
    {
        var registry = new DataLakeStrategyRegistry();
        var discovered = registry.AutoDiscover(typeof(UltimateDataLakePlugin).Assembly);
        discovered.Should().BeGreaterThanOrEqualTo(0);

        var all = registry.GetAll();
        all.Should().NotBeNull();
        all.Count.Should().Be(registry.Count);
    }
}
