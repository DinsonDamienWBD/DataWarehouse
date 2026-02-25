using DataWarehouse.Plugins.UltimateReplication;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Core;
using DataWarehouse.SDK.Contracts.Replication;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Replication;

/// <summary>
/// Tests for UltimateReplication strategy contracts and CRDT types.
/// Validates replication strategy base, configuration, and CRDT data structures.
/// </summary>
[Trait("Category", "Unit")]
public class UltimateReplicationTests
{
    #region Replication Strategy Base Contract

    [Fact]
    public void ReplicationStrategyBase_ShouldBeAbstract()
    {
        typeof(ReplicationStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void ReplicationStrategyBase_ShouldDefineRequiredProperties()
    {
        var type = typeof(ReplicationStrategyBase);
        type.GetProperty("StrategyId").Should().NotBeNull();
    }

    [Fact]
    public void EnhancedReplicationStrategyBase_ShouldExtendBase()
    {
        typeof(EnhancedReplicationStrategyBase).IsAbstract.Should().BeTrue();
        typeof(EnhancedReplicationStrategyBase)
            .IsSubclassOf(typeof(ReplicationStrategyBase)).Should().BeTrue();
    }

    #endregion

    #region CRDT Types

    [Fact]
    public void GCounterCrdt_ShouldStartAtZero()
    {
        var counter = new GCounterCrdt();
        counter.Value.Should().Be(0);
    }

    [Fact]
    public void GCounterCrdt_Increment_ShouldIncrease()
    {
        var counter = new GCounterCrdt();
        counter.Increment("node-1");
        counter.Increment("node-1");
        counter.Increment("node-2", 5);

        counter.Value.Should().Be(7); // 1 + 1 + 5
    }

    [Fact]
    public void GCounterCrdt_Merge_ShouldTakeMaxPerNode()
    {
        var counter1 = new GCounterCrdt();
        counter1.Increment("node-1", 10);
        counter1.Increment("node-2", 5);

        var counter2 = new GCounterCrdt();
        counter2.Increment("node-1", 3);
        counter2.Increment("node-2", 8);

        counter1.Merge(counter2);

        // node-1: max(10, 3) = 10, node-2: max(5, 8) = 8
        counter1.Value.Should().Be(18);
    }

    [Fact]
    public void GCounterCrdt_Serialization_ShouldRoundtrip()
    {
        var counter = new GCounterCrdt();
        counter.Increment("node-1", 42);
        counter.Increment("node-2", 17);

        var json = counter.ToJson();
        var restored = GCounterCrdt.FromJson(json);

        restored.Value.Should().Be(counter.Value);
    }

    #endregion
}
