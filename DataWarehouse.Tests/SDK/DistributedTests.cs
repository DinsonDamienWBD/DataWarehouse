using DataWarehouse.SDK.Infrastructure.Distributed;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

/// <summary>
/// Tests for distributed infrastructure: ConsistentHashRing.
/// Note: SWIM, Raft, and CRDT types are internal to the SDK and not directly testable from
/// the test project. These tests focus on the public ConsistentHashRing API.
/// </summary>
[Trait("Category", "Unit")]
public class DistributedTests
{
    #region ConsistentHashRing - Construction

    [Fact]
    public void Constructor_DefaultVirtualNodes_ShouldBe150()
    {
        using var ring = new ConsistentHashRing();
        ring.VirtualNodeCount.Should().Be(150);
    }

    [Fact]
    public void Constructor_CustomVirtualNodes_ShouldBeStored()
    {
        using var ring = new ConsistentHashRing(200);
        ring.VirtualNodeCount.Should().Be(200);
    }

    [Fact]
    public void Constructor_ZeroVirtualNodes_ShouldThrow()
    {
        var act = () => new ConsistentHashRing(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NegativeVirtualNodes_ShouldThrow()
    {
        var act = () => new ConsistentHashRing(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region ConsistentHashRing - AddNode/RemoveNode

    [Fact]
    public void AddNode_ShouldAllowKeyLookup()
    {
        using var ring = new ConsistentHashRing(10);
        ring.AddNode("node-1");

        var node = ring.GetNode("test-key");
        node.Should().Be("node-1");
    }

    [Fact]
    public void AddNode_NullNodeId_ShouldThrow()
    {
        using var ring = new ConsistentHashRing(10);
        var act = () => ring.AddNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RemoveNode_ShouldRemoveFromRing()
    {
        using var ring = new ConsistentHashRing(10);
        ring.AddNode("node-1");
        ring.AddNode("node-2");
        ring.RemoveNode("node-1");

        var node = ring.GetNode("any-key");
        node.Should().Be("node-2");
    }

    [Fact]
    public void RemoveNode_NullNodeId_ShouldThrow()
    {
        using var ring = new ConsistentHashRing(10);
        var act = () => ring.RemoveNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ConsistentHashRing - GetNode

    [Fact]
    public void GetNode_EmptyRing_ShouldThrow()
    {
        using var ring = new ConsistentHashRing(10);
        var act = () => ring.GetNode("key");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetNode_NullKey_ShouldThrow()
    {
        using var ring = new ConsistentHashRing(10);
        ring.AddNode("node-1");
        var act = () => ring.GetNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetNode_Deterministic_SameKeySameNode()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-a");
        ring.AddNode("node-b");
        ring.AddNode("node-c");

        var first = ring.GetNode("my-key");
        var second = ring.GetNode("my-key");
        first.Should().Be(second);
    }

    [Fact]
    public void GetNode_MultipleNodes_ShouldDistributeKeys()
    {
        using var ring = new ConsistentHashRing(150);
        ring.AddNode("node-1");
        ring.AddNode("node-2");
        ring.AddNode("node-3");

        var nodes = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            nodes.Add(ring.GetNode($"key-{i}"));
        }

        // With 1000 keys and 3 nodes, all nodes should get some keys
        nodes.Count.Should().Be(3, "All 3 nodes should receive at least some keys");
    }

    #endregion

    #region ConsistentHashRing - GetNodes (replica selection)

    [Fact]
    public void GetNodes_ShouldReturnRequestedCount()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-1");
        ring.AddNode("node-2");
        ring.AddNode("node-3");

        var nodes = ring.GetNodes("key", 2);
        nodes.Should().HaveCount(2);
    }

    [Fact]
    public void GetNodes_ShouldReturnDistinctPhysicalNodes()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-1");
        ring.AddNode("node-2");
        ring.AddNode("node-3");

        var nodes = ring.GetNodes("key", 3);
        nodes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GetNodes_MoreThanPhysicalNodes_ShouldReturnAllPhysical()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-1");
        ring.AddNode("node-2");

        var nodes = ring.GetNodes("key", 5);
        nodes.Should().HaveCount(2, "Cannot return more distinct nodes than physical count");
    }

    [Fact]
    public void GetNodes_ZeroCount_ShouldThrow()
    {
        using var ring = new ConsistentHashRing(10);
        ring.AddNode("node-1");
        var act = () => ring.GetNodes("key", 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetNodes_EmptyRing_ShouldThrow()
    {
        using var ring = new ConsistentHashRing(10);
        var act = () => ring.GetNodes("key", 1);
        act.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region ConsistentHashRing - Rebalancing

    [Fact]
    public void AddNode_ShouldOnlyAffectNearbyKeys()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-1");
        ring.AddNode("node-2");

        // Record assignment before adding node-3
        var keysBefore = new Dictionary<string, string>();
        for (int i = 0; i < 100; i++)
        {
            keysBefore[$"key-{i}"] = ring.GetNode($"key-{i}");
        }

        ring.AddNode("node-3");

        // Most keys should stay the same
        int unchanged = 0;
        for (int i = 0; i < 100; i++)
        {
            if (ring.GetNode($"key-{i}") == keysBefore[$"key-{i}"])
                unchanged++;
        }

        // At least 30% of keys should remain (rough threshold - consistent hashing property)
        unchanged.Should().BeGreaterThan(30, "Adding a node should not reassign most keys");
    }

    [Fact]
    public void RemoveNode_ShouldOnlyAffectRemovedNodeKeys()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-1");
        ring.AddNode("node-2");
        ring.AddNode("node-3");

        // Record assignment before removing
        var keysBefore = new Dictionary<string, string>();
        for (int i = 0; i < 100; i++)
        {
            keysBefore[$"key-{i}"] = ring.GetNode($"key-{i}");
        }

        ring.RemoveNode("node-3");

        // Keys NOT on node-3 should keep their assignment
        int keysOnNode3 = keysBefore.Values.Count(v => v == "node-3");
        int unchangedNonNode3 = 0;
        for (int i = 0; i < 100; i++)
        {
            if (keysBefore[$"key-{i}"] != "node-3" && ring.GetNode($"key-{i}") == keysBefore[$"key-{i}"])
                unchangedNonNode3++;
        }

        int nonNode3Count = 100 - keysOnNode3;
        if (nonNode3Count > 0)
        {
            unchangedNonNode3.Should().Be(nonNode3Count, "Keys not on removed node should keep their assignment");
        }
    }

    #endregion

    #region ConsistentHashRing - Thread Safety

    [Fact]
    public void ConcurrentAccess_ShouldNotThrow()
    {
        using var ring = new ConsistentHashRing(50);
        ring.AddNode("node-1");
        ring.AddNode("node-2");

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 100; j++)
            {
                var node = ring.GetNode($"key-{i}-{j}");
                node.Should().NotBeNullOrEmpty();
            }
        })).ToArray();

        Task.WaitAll(tasks);
    }

    #endregion

    #region ConsistentHashRing - Dispose

    [Fact]
    public void Dispose_ShouldBeSafe()
    {
        var ring = new ConsistentHashRing(10);
        ring.AddNode("node-1");
        ring.Dispose();
        ring.Should().NotBeNull("Dispose should complete without throwing");
    }

    [Fact]
    public void Dispose_DoubleShouldBeSafe()
    {
        var ring = new ConsistentHashRing(10);
        ring.Dispose();
        ring.Dispose();
        ring.Should().NotBeNull("Double Dispose should be safe");
    }

    #endregion
}
