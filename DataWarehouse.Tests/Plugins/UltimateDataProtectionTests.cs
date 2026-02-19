using DataWarehouse.Plugins.UltimateDataProtection;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataProtectionTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataProtectionPlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Protection");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DataProtectionCategory_ShouldCoverAllBackupTypes()
    {
        var categories = Enum.GetValues<DataProtectionCategory>();
        categories.Should().Contain(DataProtectionCategory.FullBackup);
        categories.Should().Contain(DataProtectionCategory.IncrementalBackup);
        categories.Should().Contain(DataProtectionCategory.ContinuousProtection);
        categories.Should().Contain(DataProtectionCategory.Snapshot);
        categories.Should().Contain(DataProtectionCategory.Replication);
    }

    [Fact]
    public void DataProtectionStrategyRegistry_ShouldStartEmpty()
    {
        var registry = new DataProtectionStrategyRegistry();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void DataProtectionStrategyRegistry_ShouldFilterByCategory()
    {
        var registry = new DataProtectionStrategyRegistry();
        var fullBackup = registry.GetByCategory(DataProtectionCategory.FullBackup);
        fullBackup.Should().NotBeNull();
        fullBackup.Should().BeEmpty("empty registry has no strategies");
    }

    [Fact]
    public void IDataProtectionStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataProtectionStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("StrategyName").Should().NotBeNull();
        iface.GetProperty("Category").Should().NotBeNull();
        iface.GetProperty("Capabilities").Should().NotBeNull();
    }
}
