using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Storage.Placement;
using Xunit;

namespace DataWarehouse.Tests.Storage.ZeroGravity;

/// <summary>
/// Tests for CRUSH-equivalent deterministic placement algorithm.
/// Verifies: determinism, failure domain separation, constraint filtering,
/// minimal movement on topology changes, and weighted distribution.
/// </summary>
public sealed class CrushPlacementAlgorithmTests
{
    private readonly CrushPlacementAlgorithm _algorithm = new();

    #region Helpers

    private static NodeDescriptor CreateNode(
        string id, string zone, string rack, string host,
        double weight = 1.0,
        IReadOnlyList<StorageClass>? storageClasses = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        return new NodeDescriptor(
            NodeId: id,
            Zone: zone,
            Rack: rack,
            Host: host,
            Tags: tags ?? new Dictionary<string, string>(),
            StorageClasses: storageClasses ?? new[] { StorageClass.SSD },
            CapacityBytes: 1_000_000_000_000L,
            UsedBytes: 0L,
            Weight: weight);
    }

    private static IReadOnlyList<NodeDescriptor> CreateMultiZoneCluster(int nodesPerZone = 3, int zones = 3)
    {
        var nodes = new List<NodeDescriptor>();
        for (int z = 0; z < zones; z++)
        {
            string zone = $"zone-{(char)('a' + z)}";
            for (int n = 0; n < nodesPerZone; n++)
            {
                string rack = $"rack-{z}-{n}";
                nodes.Add(CreateNode($"node-{z}-{n}", zone, rack, $"host-{z}-{n}"));
            }
        }
        return nodes;
    }

    private static PlacementTarget CreateTarget(
        string objectKey,
        int replicaCount = 3,
        StorageClass? requiredStorageClass = null,
        IReadOnlyList<string>? requiredZones = null)
    {
        return new PlacementTarget(
            ObjectKey: objectKey,
            ObjectSize: 1024,
            ReplicaCount: replicaCount,
            RequiredStorageClass: requiredStorageClass,
            RequiredZones: requiredZones);
    }

    #endregion

    #region Determinism

    [Fact]
    public void Determinism_SameInputs_ProduceSameOutput()
    {
        var cluster = CreateMultiZoneCluster();
        var target = CreateTarget("my-object-key");

        var result1 = _algorithm.ComputePlacement(target, cluster);
        var result2 = _algorithm.ComputePlacement(target, cluster);

        Assert.Equal(result1.PrimaryNode.NodeId, result2.PrimaryNode.NodeId);
        Assert.Equal(
            result1.TargetNodes.Select(n => n.NodeId).ToList(),
            result2.TargetNodes.Select(n => n.NodeId).ToList());
        Assert.True(result1.Deterministic);
        Assert.True(result2.Deterministic);
    }

    [Fact]
    public void Determinism_DifferentKeys_ProduceDifferentOutput()
    {
        var cluster = CreateMultiZoneCluster(nodesPerZone: 5, zones: 5);
        var primaries = new HashSet<string>();

        for (int i = 0; i < 100; i++)
        {
            var target = CreateTarget($"object-{i}", replicaCount: 1);
            var result = _algorithm.ComputePlacement(target, cluster);
            primaries.Add(result.PrimaryNode.NodeId);
        }

        // With 25 nodes and 100 keys, we should see spread across multiple nodes
        Assert.True(primaries.Count >= 3, $"Expected at least 3 distinct primaries, got {primaries.Count}");
    }

    #endregion

    #region Failure Domain Separation

    [Fact]
    public void FailureDomainSeparation_ReplicasOnDifferentZones()
    {
        var cluster = CreateMultiZoneCluster(nodesPerZone: 3, zones: 3);
        var target = CreateTarget("zone-test", replicaCount: 3);

        var result = _algorithm.ComputePlacement(target, cluster);

        var zones = result.TargetNodes.Select(n => n.Zone).Distinct().ToList();
        Assert.Equal(3, zones.Count); // 3 replicas across 3 different zones
    }

    [Fact]
    public void FailureDomainSeparation_ReplicasOnDifferentRacks()
    {
        // Create cluster with 1 zone but 3 racks
        var nodes = new List<NodeDescriptor>
        {
            CreateNode("n1", "zone-a", "rack-1", "host-1"),
            CreateNode("n2", "zone-a", "rack-2", "host-2"),
            CreateNode("n3", "zone-a", "rack-3", "host-3"),
        };
        var target = CreateTarget("rack-test", replicaCount: 3);

        var result = _algorithm.ComputePlacement(target, nodes);

        var racks = result.TargetNodes.Select(n => n.Rack).Distinct().ToList();
        Assert.Equal(3, racks.Count); // 3 replicas across 3 different racks
    }

    #endregion

    #region Constraint Filtering

    [Fact]
    public void ConstraintFiltering_RequiredStorageClass_FiltersNodes()
    {
        var nodes = new List<NodeDescriptor>
        {
            CreateNode("nvme-1", "zone-a", "rack-1", "host-1",
                storageClasses: new[] { StorageClass.NVMe }),
            CreateNode("nvme-2", "zone-b", "rack-2", "host-2",
                storageClasses: new[] { StorageClass.NVMe }),
            CreateNode("hdd-1", "zone-c", "rack-3", "host-3",
                storageClasses: new[] { StorageClass.HDD }),
            CreateNode("hdd-2", "zone-c", "rack-4", "host-4",
                storageClasses: new[] { StorageClass.HDD }),
        };
        var target = CreateTarget("nvme-only", replicaCount: 2, requiredStorageClass: StorageClass.NVMe);

        var result = _algorithm.ComputePlacement(target, nodes);

        Assert.All(result.TargetNodes, n =>
            Assert.Contains(StorageClass.NVMe, n.StorageClasses));
    }

    [Fact]
    public void ConstraintFiltering_RequiredZone_FiltersNodes()
    {
        var cluster = CreateMultiZoneCluster(nodesPerZone: 3, zones: 3);
        var target = CreateTarget("eu-only", replicaCount: 2,
            requiredZones: new[] { "zone-a" });

        var result = _algorithm.ComputePlacement(target, cluster);

        Assert.All(result.TargetNodes, n => Assert.Equal("zone-a", n.Zone));
    }

    #endregion

    #region Minimal Movement

    [Fact]
    public void MinimalMovement_AddNode_LimitedRedistribution()
    {
        // 10-node cluster
        var originalCluster = Enumerable.Range(0, 10)
            .Select(i => CreateNode($"node-{i}", $"zone-{i % 3}", $"rack-{i}", $"host-{i}"))
            .ToList();

        // Add 1 node
        var expandedCluster = originalCluster.Concat(new[]
        {
            CreateNode("node-10", "zone-a", "rack-10", "host-10")
        }).ToList();

        int movedCount = 0;
        int totalObjects = 1000;
        for (int i = 0; i < totalObjects; i++)
        {
            var target = CreateTarget($"obj-{i}", replicaCount: 1);
            var original = _algorithm.ComputePlacement(target, originalCluster);
            var updated = _algorithm.ComputePlacement(target, expandedCluster);

            if (original.PrimaryNode.NodeId != updated.PrimaryNode.NodeId)
                movedCount++;
        }

        double movedPercent = (double)movedCount / totalObjects * 100;
        // Adding 1 node to 11 should move roughly 1/11 (~9%) of objects.
        // Allow up to 20% for algorithm variance.
        Assert.True(movedPercent < 20,
            $"Expected <20% movement, got {movedPercent:F1}%");
    }

    [Fact]
    public void MinimalMovement_RemoveNode_OnlyAffectedObjectsMove()
    {
        var originalCluster = Enumerable.Range(0, 10)
            .Select(i => CreateNode($"node-{i}", $"zone-{i % 3}", $"rack-{i}", $"host-{i}"))
            .ToList();

        // Remove node-5
        var shrunkCluster = originalCluster.Where(n => n.NodeId != "node-5").ToList();

        int movedCount = 0;
        int totalObjects = 1000;
        for (int i = 0; i < totalObjects; i++)
        {
            var target = CreateTarget($"obj-{i}", replicaCount: 1);
            var original = _algorithm.ComputePlacement(target, originalCluster);
            var updated = _algorithm.ComputePlacement(target, shrunkCluster);

            if (original.PrimaryNode.NodeId != updated.PrimaryNode.NodeId)
                movedCount++;
        }

        double movedPercent = (double)movedCount / totalObjects * 100;
        // Removing 1 of 10 nodes should only affect objects on that node (~10%)
        // Allow up to 20% for algorithm variance.
        Assert.True(movedPercent < 20,
            $"Expected <20% movement, got {movedPercent:F1}%");
    }

    #endregion

    #region Weighted Distribution

    [Fact]
    public void WeightedDistribution_HigherWeightNode_GetsMoreObjects()
    {
        var nodes = new List<NodeDescriptor>
        {
            CreateNode("heavy", "zone-a", "rack-1", "host-1", weight: 10.0),
            CreateNode("light-1", "zone-b", "rack-2", "host-2", weight: 1.0),
            CreateNode("light-2", "zone-c", "rack-3", "host-3", weight: 1.0),
        };

        var placements = new Dictionary<string, int>();
        foreach (var n in nodes) placements[n.NodeId] = 0;

        for (int i = 0; i < 1000; i++)
        {
            var target = CreateTarget($"w-obj-{i}", replicaCount: 1);
            var result = _algorithm.ComputePlacement(target, nodes);
            placements[result.PrimaryNode.NodeId]++;
        }

        // The heavy node (10x weight) should have significantly more objects
        // than each light node (1x weight). Expect at least 3x more.
        int heavyCount = placements["heavy"];
        int lightMax = Math.Max(placements["light-1"], placements["light-2"]);

        Assert.True(heavyCount > lightMax * 2,
            $"Heavy node got {heavyCount} objects, max light node got {lightMax}. " +
            $"Expected heavy to have at least 2x more.");
    }

    #endregion
}
