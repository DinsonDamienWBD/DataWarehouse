using DataWarehouse.Plugins.UltimateDataGovernance;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataGovernanceTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataGovernancePlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Governance");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GovernanceCategory_ShouldCoverAllSubsystems()
    {
        var categories = Enum.GetValues<GovernanceCategory>();
        categories.Should().Contain(GovernanceCategory.PolicyManagement);
        categories.Should().Contain(GovernanceCategory.DataOwnership);
        categories.Should().Contain(GovernanceCategory.DataStewardship);
        categories.Should().Contain(GovernanceCategory.DataClassification);
        categories.Should().Contain(GovernanceCategory.LineageTracking);
        categories.Should().Contain(GovernanceCategory.RetentionManagement);
        categories.Should().Contain(GovernanceCategory.RegulatoryCompliance);
        categories.Should().Contain(GovernanceCategory.AuditReporting);
    }

    [Fact]
    public void DataGovernanceCapabilities_ShouldBeConstructable()
    {
        var caps = new DataGovernanceCapabilities
        {
            SupportsAsync = true,
            SupportsBatch = true,
            SupportsRealTime = false,
            SupportsAudit = true,
            SupportsVersioning = true
        };
        caps.SupportsAsync.Should().BeTrue();
        caps.SupportsAudit.Should().BeTrue();
        caps.SupportsRealTime.Should().BeFalse();
    }

    [Fact]
    public void DataGovernanceStrategyRegistry_ShouldAutoDiscover()
    {
        var registry = new DataGovernanceStrategyRegistry();
        var discovered = registry.AutoDiscover(typeof(UltimateDataGovernancePlugin).Assembly);
        discovered.Should().BeGreaterThanOrEqualTo(0);
        registry.Count.Should().Be(discovered);
    }

    [Fact]
    public void DataGovernanceStrategyRegistry_ShouldFilterByCategory()
    {
        var registry = new DataGovernanceStrategyRegistry();
        registry.AutoDiscover(typeof(UltimateDataGovernancePlugin).Assembly);

        var policies = registry.GetByCategory(GovernanceCategory.PolicyManagement);
        policies.Should().NotBeNull();
        foreach (var s in policies)
        {
            s.Category.Should().Be(GovernanceCategory.PolicyManagement);
        }
    }
}
