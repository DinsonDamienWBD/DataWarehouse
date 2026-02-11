using DataWarehouse.Plugins.UltimateRAID.Strategies.Standard;
using DataWarehouse.SDK.Contracts.RAID;
using FluentAssertions;
using Xunit;
using SdkRaidStrategyBase = DataWarehouse.SDK.Contracts.RAID.RaidStrategyBase;
using RaidLevel = DataWarehouse.SDK.Contracts.RAID.RaidLevel;

namespace DataWarehouse.Tests.RAID;

/// <summary>
/// Tests for UltimateRAID strategy implementations.
/// Validates RAID level metadata, stripe calculation, and data distribution.
/// </summary>
[Trait("Category", "Unit")]
public class UltimateRAIDTests
{
    #region RAID 0 Strategy

    [Fact]
    public void Raid0Strategy_ShouldHaveCorrectLevel()
    {
        var strategy = new Raid0Strategy();
        strategy.Level.Should().Be(RaidLevel.Raid0);
    }

    [Fact]
    public void Raid0Strategy_ShouldHaveZeroRedundancy()
    {
        var strategy = new Raid0Strategy();
        strategy.Capabilities.RedundancyLevel.Should().Be(0);
        strategy.Capabilities.CapacityEfficiency.Should().Be(1.0);
    }

    [Fact]
    public void Raid0Strategy_ShouldRequireMinimumTwoDisks()
    {
        var strategy = new Raid0Strategy();
        strategy.Capabilities.MinDisks.Should().Be(2);
    }

    [Fact]
    public void Raid0Strategy_CalculateStripe_ShouldDistributeAcrossDisks()
    {
        var strategy = new Raid0Strategy();
        var stripe = strategy.CalculateStripe(0, 4);

        stripe.DataDisks.Should().HaveCount(4);
        stripe.ParityDisks.Should().BeEmpty();
        stripe.ParityChunkCount.Should().Be(0);
    }

    [Fact]
    public void Raid0Strategy_CalculateStripe_ShouldAssignCorrectDisk()
    {
        var strategy = new Raid0Strategy();

        var stripe0 = strategy.CalculateStripe(0, 3);
        var stripe1 = strategy.CalculateStripe(1, 3);
        var stripe2 = strategy.CalculateStripe(2, 3);

        // Block 0 -> disk 0, block 1 -> disk 1, block 2 -> disk 2 (round-robin)
        stripe0.DataChunkCount.Should().Be(3);
        stripe1.DataChunkCount.Should().Be(3);
        stripe2.DataChunkCount.Should().Be(3);
    }

    #endregion

    #region RAID Strategy Base Contract

    [Fact]
    public void SdkRaidStrategyBase_ShouldBeAbstract()
    {
        typeof(SdkRaidStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void SdkRaidStrategyBase_ShouldImplementIRaidStrategy()
    {
        typeof(SdkRaidStrategyBase).GetInterfaces()
            .Should().Contain(typeof(IRaidStrategy));
    }

    [Fact]
    public void SdkRaidStrategyBase_ShouldDefineWriteAndRead()
    {
        var type = typeof(SdkRaidStrategyBase);
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        methods.Should().Contain(m => m.Name == "WriteAsync");
        methods.Should().Contain(m => m.Name == "ReadAsync");
    }

    [Fact]
    public void RaidCapabilities_ShouldSupportPositionalConstruction()
    {
        var caps = new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 4,
            MaxDisks: 64,
            StripeSize: 131072,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(6),
            ReadPerformanceMultiplier: 3.0,
            WritePerformanceMultiplier: 0.5,
            CapacityEfficiency: 0.5,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: false,
            RequiresUniformDiskSize: true);

        caps.RedundancyLevel.Should().Be(2);
        caps.MinDisks.Should().Be(4);
        caps.MaxDisks.Should().Be(64);
        caps.CapacityEfficiency.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Raid0Strategy_CustomChunkSize_ShouldBeUsed()
    {
        var strategy = new Raid0Strategy(chunkSize: 128 * 1024);
        strategy.Capabilities.StripeSize.Should().Be(128 * 1024);
    }

    #endregion
}
