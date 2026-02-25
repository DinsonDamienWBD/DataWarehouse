using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DataWarehouse.SDK.Replication;
using Xunit;

namespace DataWarehouse.Tests.Replication;

/// <summary>
/// Tests for DottedVersionVector (DVV) causality tracking with membership-aware pruning.
/// </summary>
public class DvvTests
{
    #region Test Helpers

    /// <summary>
    /// Simple in-memory cluster membership for testing.
    /// </summary>
    private sealed class TestClusterMembership : IReplicationClusterMembership
    {
        private readonly HashSet<string> _nodes = new();
        private readonly List<Action<string>> _addedCallbacks = new();
        private readonly List<Action<string>> _removedCallbacks = new();

        public IReadOnlySet<string> GetActiveNodes() => _nodes;

        public void RegisterNodeAdded(Action<string> callback) => _addedCallbacks.Add(callback);
        public void RegisterNodeRemoved(Action<string> callback) => _removedCallbacks.Add(callback);

        public void AddNode(string nodeId)
        {
            _nodes.Add(nodeId);
            foreach (var cb in _addedCallbacks) cb(nodeId);
        }

        public void RemoveNode(string nodeId)
        {
            _nodes.Remove(nodeId);
            foreach (var cb in _removedCallbacks) cb(nodeId);
        }
    }

    #endregion

    [Fact]
    public void Increment_UpdatesVersionAndDot()
    {
        var dvv = new DottedVersionVector();

        dvv.Increment("node-1");

        Assert.Equal(1L, dvv.GetVersion("node-1"));
        Assert.Equal(1, dvv.Count);

        dvv.Increment("node-1");

        Assert.Equal(2L, dvv.GetVersion("node-1"));
        Assert.Equal(1, dvv.Count);
    }

    [Fact]
    public void HappensBefore_DetectsCausality()
    {
        var dvvA = new DottedVersionVector();
        var dvvB = new DottedVersionVector();

        dvvA.Increment("node-1");

        dvvB.Increment("node-1");
        dvvB.Increment("node-1");

        // A (v1) happens-before B (v2)
        Assert.True(dvvA.HappensBefore(dvvB));
        Assert.False(dvvB.HappensBefore(dvvA));
    }

    [Fact]
    public void HappensBefore_EmptyDvvHappensBeforeNonEmpty()
    {
        var empty = new DottedVersionVector();
        var nonEmpty = new DottedVersionVector();
        nonEmpty.Increment("node-1");

        Assert.True(empty.HappensBefore(nonEmpty));
        Assert.False(nonEmpty.HappensBefore(empty));
    }

    [Fact]
    public void HappensBefore_EqualDvvs_ReturnsFalse()
    {
        var dvvA = new DottedVersionVector();
        var dvvB = new DottedVersionVector();

        dvvA.Increment("node-1");
        dvvB.Increment("node-1");

        Assert.False(dvvA.HappensBefore(dvvB));
        Assert.False(dvvB.HappensBefore(dvvA));
    }

    [Fact]
    public void Merge_TakesMaxVersions()
    {
        var dvvA = new DottedVersionVector();
        var dvvB = new DottedVersionVector();

        dvvA.Increment("node-1");
        dvvA.Increment("node-1");
        dvvA.Increment("node-2");

        dvvB.Increment("node-1");
        dvvB.Increment("node-2");
        dvvB.Increment("node-2");
        dvvB.Increment("node-3");

        var merged = dvvA.Merge(dvvB);

        Assert.Equal(2L, merged.GetVersion("node-1")); // max(2, 1)
        Assert.Equal(2L, merged.GetVersion("node-2")); // max(1, 2)
        Assert.Equal(1L, merged.GetVersion("node-3")); // max(0, 1)
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void IsConcurrent_DetectsConflicts()
    {
        var dvvA = new DottedVersionVector();
        var dvvB = new DottedVersionVector();

        // A advances node-1, B advances node-2 (concurrent)
        dvvA.Increment("node-1");
        dvvB.Increment("node-2");

        Assert.True(dvvA.IsConcurrent(dvvB));
        Assert.True(dvvB.IsConcurrent(dvvA));

        // Not concurrent with itself
        Assert.False(dvvA.IsConcurrent(dvvA));
    }

    [Fact]
    public void IsConcurrent_CausallyRelated_ReturnsFalse()
    {
        var dvvA = new DottedVersionVector();
        var dvvB = new DottedVersionVector();

        dvvA.Increment("node-1");

        dvvB.Increment("node-1");
        dvvB.Increment("node-1");

        Assert.False(dvvA.IsConcurrent(dvvB));
        Assert.False(dvvB.IsConcurrent(dvvA));
    }

    [Fact]
    public void PruneDeadNodes_RemovesInactiveEntries()
    {
        var membership = new TestClusterMembership();
        membership.AddNode("node-1");
        membership.AddNode("node-2");
        membership.AddNode("node-3");

        var dvv = new DottedVersionVector(membership);
        dvv.Increment("node-1");
        dvv.Increment("node-2");
        dvv.Increment("node-3");

        Assert.Equal(3, dvv.Count);

        // Remove node-2, should be pruned
        membership.RemoveNode("node-2");
        dvv.PruneDeadNodes();

        Assert.Equal(2, dvv.Count);
        Assert.Equal(0L, dvv.GetVersion("node-2"));
        Assert.Equal(1L, dvv.GetVersion("node-1"));
        Assert.Equal(1L, dvv.GetVersion("node-3"));
    }

    [Fact]
    public void MembershipCallback_TriggersAutoPrune()
    {
        var membership = new TestClusterMembership();
        membership.AddNode("node-1");
        membership.AddNode("node-2");

        var dvv = new DottedVersionVector(membership);
        dvv.Increment("node-1");
        dvv.Increment("node-2");

        Assert.Equal(2, dvv.Count);

        // Remove a node -- auto-prune should fire via callback
        membership.RemoveNode("node-2");

        // After callback, node-2 should be pruned
        Assert.Equal(1, dvv.Count);
        Assert.Equal(0L, dvv.GetVersion("node-2"));
    }

    [Fact]
    public void ToImmutableDictionary_RoundTrips()
    {
        var dvv = new DottedVersionVector();
        dvv.Increment("node-1");
        dvv.Increment("node-1");
        dvv.Increment("node-2");

        var immutable = dvv.ToImmutableDictionary();
        var restored = DottedVersionVector.FromDictionary(immutable);

        Assert.Equal(2L, restored.GetVersion("node-1"));
        Assert.Equal(1L, restored.GetVersion("node-2"));
        Assert.Equal(2, restored.Count);
    }

    [Fact]
    public void Increment_NullNodeId_Throws()
    {
        var dvv = new DottedVersionVector();
        Assert.Throws<ArgumentNullException>(() => dvv.Increment(null!));
    }

    [Fact]
    public void Merge_WithNull_ReturnsSelf()
    {
        var dvv = new DottedVersionVector();
        dvv.Increment("node-1");

        var merged = dvv.Merge(null!);

        Assert.Equal(1L, merged.GetVersion("node-1"));
    }

    [Fact]
    public void PruneDeadNodes_NoMembership_IsNoop()
    {
        var dvv = new DottedVersionVector(); // No membership
        dvv.Increment("node-1");
        dvv.Increment("node-2");

        dvv.PruneDeadNodes(); // Should not throw

        Assert.Equal(2, dvv.Count);
    }
}
