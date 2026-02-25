using DataWarehouse.Plugins.UltimateDataPrivacy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataPrivacyTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataPrivacyPlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Privacy");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void PrivacyCategory_ShouldCoverAllTechniques()
    {
        var categories = Enum.GetValues<PrivacyCategory>();
        categories.Should().Contain(PrivacyCategory.Anonymization);
        categories.Should().Contain(PrivacyCategory.Pseudonymization);
        categories.Should().Contain(PrivacyCategory.Tokenization);
        categories.Should().Contain(PrivacyCategory.Masking);
        categories.Should().Contain(PrivacyCategory.DifferentialPrivacy);
        categories.Should().Contain(PrivacyCategory.PrivacyCompliance);
        categories.Should().Contain(PrivacyCategory.PrivacyPreservingAnalytics);
        categories.Should().Contain(PrivacyCategory.PrivacyMetrics);
    }

    [Fact]
    public void DataPrivacyCapabilities_ShouldBeConstructable()
    {
        var caps = new DataPrivacyCapabilities
        {
            SupportsAsync = true,
            SupportsBatch = true,
            SupportsReversible = false,
            SupportsFormatPreserving = true
        };
        caps.SupportsAsync.Should().BeTrue();
        caps.SupportsReversible.Should().BeFalse();
        caps.SupportsFormatPreserving.Should().BeTrue();
    }

    [Fact]
    public void DataPrivacyStrategyRegistry_ShouldAutoDiscover()
    {
        var registry = new DataPrivacyStrategyRegistry();
        var discovered = registry.AutoDiscover(typeof(UltimateDataPrivacyPlugin).Assembly);
        discovered.Should().BeGreaterThanOrEqualTo(0);
        registry.GetAll().Should().NotBeNull();
    }

    [Fact]
    public void DataPrivacyStrategyRegistry_ShouldFilterByCategory()
    {
        var registry = new DataPrivacyStrategyRegistry();
        registry.AutoDiscover(typeof(UltimateDataPrivacyPlugin).Assembly);

        var masking = registry.GetByCategory(PrivacyCategory.Masking);
        masking.Should().NotBeNull();
        foreach (var s in masking)
        {
            s.Category.Should().Be(PrivacyCategory.Masking);
        }
    }
}
