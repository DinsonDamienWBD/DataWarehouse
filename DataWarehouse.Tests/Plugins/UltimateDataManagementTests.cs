using DataWarehouse.Plugins.UltimateDataManagement;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataManagementTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataManagementPlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Management");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataManagementCategory_ShouldCoverAllSubsystems()
    {
        var categories = Enum.GetValues<DataManagementCategory>();
        categories.Should().Contain(DataManagementCategory.Caching);
        categories.Should().Contain(DataManagementCategory.Indexing);
        categories.Should().Contain(DataManagementCategory.Lifecycle);
        categories.Should().Contain(DataManagementCategory.Versioning);
        categories.Should().Contain(DataManagementCategory.Branching);
        categories.Should().Contain(DataManagementCategory.EventSourcing);
    }

    [Fact]
    public void DataManagementCapabilities_ShouldBeConstructable()
    {
        var caps = new DataManagementCapabilities
        {
            SupportsAsync = true,
            SupportsBatch = true,
            SupportsDistributed = false,
            SupportsTransactions = true,
            SupportsTTL = true,
            MaxThroughput = 100_000,
            TypicalLatencyMs = 0.5
        };
        caps.SupportsAsync.Should().BeTrue();
        caps.SupportsTTL.Should().BeTrue();
        caps.MaxThroughput.Should().Be(100_000);
        caps.TypicalLatencyMs.Should().Be(0.5);
    }

    [Fact]
    public void DataManagementStatistics_ShouldTrackOperations()
    {
        var stats = new DataManagementStatistics
        {
            TotalReads = 1000,
            TotalWrites = 500,
            TotalDeletes = 50,
            CacheHits = 800,
            CacheMisses = 200,
            TotalBytesRead = 10_000_000,
            TotalBytesWritten = 5_000_000
        };
        stats.TotalReads.Should().Be(1000);
        stats.CacheHits.Should().Be(800);
        stats.TotalBytesWritten.Should().Be(5_000_000);
    }

    [Fact]
    public void IDataManagementStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataManagementStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("DisplayName").Should().NotBeNull();
        iface.GetProperty("Category").Should().NotBeNull();
    }
}
