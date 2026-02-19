using DataWarehouse.SDK.Contracts.DataFormat;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataFormatTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new DataWarehouse.Plugins.UltimateDataFormat.UltimateDataFormatPlugin();
        plugin.Id.Should().Be("com.datawarehouse.dataformat.ultimate");
        plugin.Name.Should().Be("Ultimate Data Format");
        plugin.Version.Should().Be("1.0.0");
        plugin.FormatFamily.Should().Be("Universal");
    }

    [Fact]
    public void DataFormatCapabilities_ShouldBeConstructableWithAllFlags()
    {
        var caps = new DataFormatCapabilities
        {
            Bidirectional = true,
            Streaming = true,
            SchemaAware = true
        };
        caps.Bidirectional.Should().BeTrue();
        caps.Streaming.Should().BeTrue();
        caps.SchemaAware.Should().BeTrue();
    }

    [Fact]
    public void DomainFamily_ShouldCoverExpectedDomains()
    {
        var families = Enum.GetValues<DomainFamily>();
        families.Should().Contain(DomainFamily.General);
        families.Should().Contain(DomainFamily.Analytics);
        families.Should().Contain(DomainFamily.Scientific);
    }

    [Fact]
    public void IDataFormatStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataFormatStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("DisplayName").Should().NotBeNull();
        iface.GetProperty("Capabilities").Should().NotBeNull();
        iface.GetProperty("FormatInfo").Should().NotBeNull();
        iface.GetMethod("DetectFormatAsync").Should().NotBeNull();
        iface.GetMethod("ParseAsync").Should().NotBeNull();
        iface.GetMethod("SerializeAsync").Should().NotBeNull();
    }
}
