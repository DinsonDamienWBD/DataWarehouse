using DataWarehouse.SDK.Contracts.RAID;
using FluentAssertions;
using Xunit;
using RaidLevel = DataWarehouse.SDK.Contracts.RAID.RaidLevel;

namespace DataWarehouse.Tests.Hyperscale;

/// <summary>
/// Tests for SDK RAID and erasure coding contracts (replaces ErasureCodingTests).
/// Validates RAID strategy interfaces and types via reflection.
/// </summary>
[Trait("Category", "Unit")]
public class SdkRaidContractTests
{
    [Fact]
    public void IRaidStrategy_ShouldExistInSdk()
    {
        var type = typeof(IRaidStrategy);
        type.Should().NotBeNull();
        type.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void RaidLevel_ShouldContainStandardLevels()
    {
        Enum.GetValues<RaidLevel>().Should().Contain(RaidLevel.Raid0);
        Enum.GetValues<RaidLevel>().Should().Contain(RaidLevel.Raid1);
        Enum.GetValues<RaidLevel>().Should().Contain(RaidLevel.Raid5);
        Enum.GetValues<RaidLevel>().Should().Contain(RaidLevel.Raid6);
    }

    [Fact]
    public void RaidCapabilities_ShouldBeRecord()
    {
        var type = typeof(RaidCapabilities);
        type.Should().NotBeNull();
        type.IsValueType.Should().BeFalse();
        type.GetProperty("RedundancyLevel").Should().NotBeNull();
        type.GetProperty("MinDisks").Should().NotBeNull();
    }

    [Fact]
    public void RaidStrategyBase_ShouldBeAbstract()
    {
        var type = typeof(RaidStrategyBase);
        type.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void RaidStrategyBase_ShouldImplementIRaidStrategy()
    {
        typeof(RaidStrategyBase).GetInterfaces().Should().Contain(typeof(IRaidStrategy));
    }

    [Fact]
    public void RaidStrategyBase_ShouldDefineDistributeAndReconstruct()
    {
        var type = typeof(RaidStrategyBase);
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        methods.Should().Contain(m => m.Name == "WriteAsync");
        methods.Should().Contain(m => m.Name == "ReadAsync");
    }

    [Fact]
    public void RaidCapabilities_Constructor_ShouldSetProperties()
    {
        var caps = new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 3,
            MaxDisks: 32,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 2.5,
            WritePerformanceMultiplier: 0.75,
            CapacityEfficiency: 0.67,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        caps.RedundancyLevel.Should().Be(1);
        caps.MinDisks.Should().Be(3);
        caps.SupportsHotSpare.Should().BeTrue();
        caps.CapacityEfficiency.Should().BeApproximately(0.67, 0.01);
    }
}
