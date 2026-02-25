using DataWarehouse.SDK.Contracts.Transit;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataTransitTests
{
    [Fact]
    public void IDataTransitStrategy_ShouldDefineExpectedMembers()
    {
        var iface = typeof(IDataTransitStrategy);
        iface.GetProperty("StrategyId").Should().NotBeNull();
        iface.GetProperty("Name").Should().NotBeNull();
        iface.GetProperty("Capabilities").Should().NotBeNull();
        iface.GetMethod("TransferAsync").Should().NotBeNull();
        iface.GetMethod("ResumeTransferAsync").Should().NotBeNull();
        iface.GetMethod("CancelTransferAsync").Should().NotBeNull();
    }

    [Fact]
    public void TransitCapabilities_ShouldBeConstructableWithAllFlags()
    {
        var caps = new TransitCapabilities
        {
            SupportsResumable = true,
            SupportsStreaming = true,
            SupportsDelta = false,
            SupportsMultiPath = true,
            SupportsCompression = true,
            SupportsEncryption = true,
            SupportsOffline = false,
            MaxTransferSizeBytes = long.MaxValue,
            SupportedProtocols = ["http2", "sftp", "grpc"]
        };
        caps.SupportsResumable.Should().BeTrue();
        caps.SupportsCompression.Should().BeTrue();
        caps.SupportsEncryption.Should().BeTrue();
        caps.SupportsDelta.Should().BeFalse();
        caps.SupportedProtocols.Should().Contain("sftp");
        caps.MaxTransferSizeBytes.Should().Be(long.MaxValue);
    }

    [Fact]
    public void TransitCapabilities_DefaultValues_ShouldBeFalse()
    {
        var caps = new TransitCapabilities();
        caps.SupportsResumable.Should().BeFalse();
        caps.SupportsStreaming.Should().BeFalse();
        caps.SupportsDelta.Should().BeFalse();
        caps.SupportsP2P.Should().BeFalse();
        caps.SupportsOffline.Should().BeFalse();
        caps.MaxTransferSizeBytes.Should().Be(0);
    }
}
