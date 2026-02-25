using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Placement;

/// <summary>
/// Bucket types in the CRUSH hierarchy (most general to most specific).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: CRUSH placement")]
public enum BucketType
{
    Root,
    Zone,
    Rack,
    Host
}

/// <summary>
/// A bucket in the CRUSH hierarchy tree.
/// CRUSH uses a hierarchical structure: Root -> Zones -> Racks -> Hosts (leaf nodes with actual storage).
/// Each bucket has a selection algorithm for choosing children.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: CRUSH placement")]
public sealed record CrushBucket
{
    public string Id { get; init; } = string.Empty;
    public BucketType Type { get; init; }
    public double Weight { get; init; } = 1.0;
    public List<CrushBucket> Children { get; } = new();

    /// <summary>
    /// Only set for Host-level (leaf) buckets — references the actual storage node.
    /// </summary>
    public NodeDescriptor? Node { get; init; }

    /// <summary>
    /// Straw2 selection: deterministic weighted selection from children.
    /// Given a placement group seed, selects one child using the Straw2 algorithm.
    /// Straw2 assigns each child a "straw length" = hash(input, child.id) * child.weight.
    /// The child with the longest straw wins. This is deterministic and weight-proportional.
    /// </summary>
    public CrushBucket SelectChild(uint pgSeed, int replicaIndex)
    {
        if (Children.Count == 0)
            throw new InvalidOperationException($"Bucket {Id} has no children to select from.");
        if (Children.Count == 1)
            return Children[0];

        CrushBucket? winner = null;
        long maxStraw = long.MinValue;

        foreach (var child in Children)
        {
            // Straw2 hash: combine pgSeed, replicaIndex, and child ID
            uint hash = CrushHash(pgSeed, (uint)replicaIndex, child.Id);
            // Weight adjustment: straw = hash * weight (simplified integer arithmetic)
            long straw = (long)(hash * child.Weight * 65536.0);

            if (straw > maxStraw)
            {
                maxStraw = straw;
                winner = child;
            }
        }

        return winner!;
    }

    /// <summary>
    /// CRUSH hash function: Jenkins one-at-a-time variant for deterministic mixing.
    /// </summary>
    private static uint CrushHash(uint seed1, uint seed2, string childId)
    {
        uint hash = seed1 ^ (seed2 * 0x5BD1E995);
        foreach (char c in childId)
        {
            hash += (uint)c;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    /// <summary>
    /// Builds a CRUSH bucket hierarchy from a flat list of NodeDescriptors.
    /// Groups by Zone -> Rack -> Host automatically.
    /// </summary>
    public static CrushBucket BuildHierarchy(IReadOnlyList<NodeDescriptor> nodes)
    {
        var root = new CrushBucket { Id = "root", Type = BucketType.Root };

        var byZone = nodes.GroupBy(n => n.Zone ?? "default-zone");
        foreach (var zoneGroup in byZone)
        {
            var zoneBucket = new CrushBucket
            {
                Id = $"zone-{zoneGroup.Key}",
                Type = BucketType.Zone,
                Weight = zoneGroup.Sum(n => n.Weight)
            };

            var byRack = zoneGroup.GroupBy(n => n.Rack ?? "default-rack");
            foreach (var rackGroup in byRack)
            {
                var rackBucket = new CrushBucket
                {
                    Id = $"rack-{zoneGroup.Key}-{rackGroup.Key}",
                    Type = BucketType.Rack,
                    Weight = rackGroup.Sum(n => n.Weight)
                };

                foreach (var node in rackGroup)
                {
                    var hostBucket = new CrushBucket
                    {
                        Id = $"host-{node.NodeId}",
                        Type = BucketType.Host,
                        Weight = node.Weight,
                        Node = node
                    };
                    rackBucket.Children.Add(hostBucket);
                }
                zoneBucket.Children.Add(rackBucket);
            }
            root.Children.Add(zoneBucket);
        }

        return root with { Weight = nodes.Sum(n => n.Weight) };
    }
}
