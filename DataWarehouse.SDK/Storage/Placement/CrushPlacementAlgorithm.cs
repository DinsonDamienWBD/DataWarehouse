using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// CRUSH-equivalent deterministic placement algorithm.
/// Given an object key and cluster map, deterministically selects primary + replica nodes
/// without any central lookup. Any client with the same cluster map computes the same result.
/// </summary>
/// <remarks>
/// <para>Algorithm:</para>
/// <list type="number">
/// <item>Hash the object key to produce a placement group seed (uint)</item>
/// <item>Build a bucket hierarchy from the cluster map</item>
/// <item>For each replica (0..N-1), traverse the hierarchy using Straw2 selection</item>
/// <item>Apply failure domain separation (no two replicas in same zone/rack unless forced)</item>
/// <item>Apply placement constraints (storage class, tags, compliance regions)</item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 58: CRUSH-equivalent deterministic placement")]
public sealed class CrushPlacementAlgorithm : IPlacementAlgorithm
{
    private const int MaxRetries = 50;

    /// <inheritdoc />
    public PlacementDecision ComputePlacement(
        PlacementTarget target,
        IReadOnlyList<NodeDescriptor> clusterMap,
        IReadOnlyList<PlacementConstraint>? constraints = null)
    {
        if (clusterMap.Count == 0)
            throw new ArgumentException("Cluster map is empty.", nameof(clusterMap));

        var eligibleNodes = ApplyConstraints(clusterMap, target, constraints);
        if (eligibleNodes.Count == 0)
            throw new InvalidOperationException("No eligible nodes after applying placement constraints.");

        var hierarchy = CrushBucket.BuildHierarchy(eligibleNodes);
        uint pgSeed = HashObjectKey(target.ObjectKey);
        int replicaCount = Math.Min(target.ReplicaCount, eligibleNodes.Count);

        var selectedNodes = new List<NodeDescriptor>();
        var usedZones = new HashSet<string>();
        var usedRacks = new HashSet<string>();
        var usedHosts = new HashSet<string>();

        for (int replica = 0; replica < replicaCount; replica++)
        {
            NodeDescriptor? selected = null;
            for (int retry = 0; retry < MaxRetries; retry++)
            {
                var leaf = TraverseHierarchy(hierarchy, pgSeed, replica + retry * 1000);

                if (leaf.Node == null) continue;
                var candidate = leaf.Node;

                // Failure domain separation: prefer different zones, then different racks
                bool zoneConflict = target.RequiredZones == null &&
                    usedZones.Contains(candidate.Zone) &&
                    eligibleNodes.Any(n => !usedZones.Contains(n.Zone));
                bool rackConflict = usedRacks.Contains(candidate.Rack) &&
                    eligibleNodes.Any(n => !usedRacks.Contains(n.Rack));
                bool hostConflict = usedHosts.Contains(candidate.NodeId);

                if (hostConflict) continue; // never place two replicas on same host
                if (zoneConflict && retry < MaxRetries / 2) continue; // try harder for zone separation
                if (rackConflict && retry < MaxRetries / 3) continue; // try for rack separation

                selected = candidate;
                break;
            }

            if (selected == null)
            {
                // Fallback: take any unused host
                selected = eligibleNodes.FirstOrDefault(n => !usedHosts.Contains(n.NodeId));
                if (selected == null) break; // exhausted all hosts
            }

            selectedNodes.Add(selected);
            usedZones.Add(selected.Zone);
            usedRacks.Add(selected.Rack);
            usedHosts.Add(selected.NodeId);
        }

        if (selectedNodes.Count == 0)
            throw new InvalidOperationException("Failed to select any nodes for placement.");

        return new PlacementDecision(
            TargetNodes: selectedNodes,
            PrimaryNode: selectedNodes[0],
            ReplicaNodes: selectedNodes.Skip(1).ToList(),
            PlacementRuleId: $"crush-{pgSeed:X8}",
            Deterministic: true,
            Timestamp: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public PlacementDecision RecomputeOnNodeChange(
        PlacementTarget target,
        IReadOnlyList<NodeDescriptor> newClusterMap,
        PlacementDecision previousDecision)
    {
        // CRUSH property: recompute with new map. Only objects on changed nodes move.
        // The algorithm naturally minimizes movement because:
        // - Hash is stable (same key = same pgSeed)
        // - Straw2 only changes selection when weights shift significantly
        // - Removed nodes force reselection only for their objects
        return ComputePlacement(target, newClusterMap);
    }

    /// <inheritdoc />
    public double EstimateMovementOnResize(
        IReadOnlyList<NodeDescriptor> currentMap,
        IReadOnlyList<NodeDescriptor> newMap)
    {
        double currentTotal = currentMap.Sum(n => n.Weight);
        if (currentTotal == 0) return 1.0;

        double newTotal = newMap.Sum(n => n.Weight);

        // Nodes removed: their data must move
        var removedWeight = currentMap
            .Where(c => !newMap.Any(n => n.NodeId == c.NodeId))
            .Sum(n => n.Weight);

        // Nodes added: pull data proportional to their weight
        var addedWeight = newMap
            .Where(n => !currentMap.Any(c => c.NodeId == n.NodeId))
            .Sum(n => n.Weight);

        return Math.Min(1.0, (removedWeight + addedWeight / Math.Max(newTotal, 1.0) * currentTotal) / currentTotal);
    }

    private static CrushBucket TraverseHierarchy(CrushBucket bucket, uint pgSeed, int replicaIndex)
    {
        if (bucket.Type == BucketType.Host)
            return bucket;

        var child = bucket.SelectChild(pgSeed, replicaIndex);
        return TraverseHierarchy(child, pgSeed, replicaIndex);
    }

    private static IReadOnlyList<NodeDescriptor> ApplyConstraints(
        IReadOnlyList<NodeDescriptor> nodes,
        PlacementTarget target,
        IReadOnlyList<PlacementConstraint>? constraints)
    {
        var eligible = nodes.ToList();

        // Filter by required storage class
        if (target.RequiredStorageClass != null)
        {
            eligible = eligible.Where(n =>
                n.StorageClasses != null && n.StorageClasses.Contains(target.RequiredStorageClass.Value))
                .ToList();
        }

        // Filter by required zones
        if (target.RequiredZones != null && target.RequiredZones.Count > 0)
        {
            eligible = eligible.Where(n =>
                target.RequiredZones.Contains(n.Zone))
                .ToList();
        }

        // Filter by required tags
        if (target.RequiredTags != null)
        {
            foreach (var tag in target.RequiredTags)
            {
                eligible = eligible.Where(n =>
                    n.Tags != null && n.Tags.TryGetValue(tag.Key, out var v) && v == tag.Value)
                    .ToList();
            }
        }

        // Apply explicit constraints
        if (constraints != null)
        {
            foreach (var constraint in constraints.Where(c => c.Required))
            {
                eligible = constraint.ConstraintType switch
                {
                    PlacementConstraintType.Zone => eligible.Where(n => n.Zone == constraint.Value).ToList(),
                    PlacementConstraintType.Rack => eligible.Where(n => n.Rack == constraint.Value).ToList(),
                    PlacementConstraintType.Host => eligible.Where(n => n.Host == constraint.Value).ToList(),
                    PlacementConstraintType.Tag => eligible.Where(n =>
                        n.Tags != null && n.Tags.TryGetValue(constraint.Key, out var v) && v == constraint.Value).ToList(),
                    _ => eligible
                };
            }
        }

        return eligible;
    }

    /// <summary>
    /// Deterministic hash of object key to placement group seed.
    /// Uses FNV-1a for speed and good distribution.
    /// </summary>
    private static uint HashObjectKey(string key)
    {
        uint hash = 2166136261u;
        foreach (char c in key)
        {
            hash ^= (uint)c;
            hash *= 16777619u;
        }
        return hash;
    }
}
