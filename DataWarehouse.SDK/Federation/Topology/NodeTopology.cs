using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Topology;

/// <summary>
/// Represents the network topology and health metadata for a storage node.
/// </summary>
/// <remarks>
/// <para>
/// NodeTopology describes the physical/logical location of a storage node in the network
/// hierarchy (rack, datacenter, region) along with geographic coordinates and health/capacity
/// metrics. This metadata enables location-aware routing decisions.
/// </para>
/// <para>
/// <strong>Topology Hierarchy:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Node (single machine)</description></item>
///   <item><description>Rack (multiple nodes, typically same physical rack)</description></item>
///   <item><description>Datacenter (multiple racks, same physical building/campus)</description></item>
///   <item><description>Region (multiple datacenters, geographic area like "us-east", "eu-west")</description></item>
/// </list>
/// <para>
/// <strong>Health Score:</strong> Ranges from 0.0 (dead/unavailable) to 1.0 (fully healthy).
/// Routing logic uses health scores to avoid unhealthy nodes. Values below 0.1 are typically
/// excluded from routing.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Node topology metadata for location-aware routing (FOS-04)")]
public sealed record NodeTopology
{
    /// <summary>
    /// Gets the unique identifier for this storage node.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Gets the network address for this node.
    /// </summary>
    /// <remarks>
    /// Typically an IP address or hostname. Used for network communication with the node.
    /// </remarks>
    public required string Address { get; init; }

    /// <summary>
    /// Gets the network port for this node.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Gets the rack identifier for this node (optional).
    /// </summary>
    /// <remarks>
    /// Null if rack information is not available. Used to prefer same-rack routing for
    /// latency optimization.
    /// </remarks>
    public string? Rack { get; init; }

    /// <summary>
    /// Gets the datacenter identifier for this node (optional).
    /// </summary>
    /// <remarks>
    /// Null if datacenter information is not available. Used to prefer same-datacenter routing.
    /// </remarks>
    public string? Datacenter { get; init; }

    /// <summary>
    /// Gets the region identifier for this node (optional).
    /// </summary>
    /// <remarks>
    /// Null if region information is not available. Examples: "us-east-1", "eu-west-2", "ap-south".
    /// </remarks>
    public string? Region { get; init; }

    /// <summary>
    /// Gets the latitude coordinate for this node (optional).
    /// </summary>
    /// <remarks>
    /// Null if geographic coordinates are not available. Used for calculating geographic distance
    /// via Haversine formula when rack/datacenter/region metadata is insufficient.
    /// </remarks>
    public double? Latitude { get; init; }

    /// <summary>
    /// Gets the longitude coordinate for this node (optional).
    /// </summary>
    /// <remarks>
    /// Null if geographic coordinates are not available.
    /// </remarks>
    public double? Longitude { get; init; }

    /// <summary>
    /// Gets the health score for this node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ranges from 0.0 (dead/unavailable) to 1.0 (fully healthy). Default: 1.0.
    /// </para>
    /// <para>
    /// Health scores below 0.1 typically indicate a node should be excluded from routing.
    /// Routing policies multiply proximity scores by health score to penalize unhealthy nodes.
    /// </para>
    /// </remarks>
    public double HealthScore { get; init; } = 1.0;

    /// <summary>
    /// Gets the available free storage capacity in bytes.
    /// </summary>
    /// <remarks>
    /// Used by throughput-optimized routing policies to prefer less-loaded nodes.
    /// </remarks>
    public long FreeBytes { get; init; }

    /// <summary>
    /// Gets the total storage capacity in bytes.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Gets the UTC timestamp of the last heartbeat from this node.
    /// </summary>
    /// <remarks>
    /// Used for staleness detection. Nodes with stale heartbeats (e.g., >5 minutes old) may be
    /// considered unhealthy or offline.
    /// </remarks>
    public DateTimeOffset LastHeartbeat { get; init; }

    /// <summary>
    /// Computes the topology level relationship between this node and another node.
    /// </summary>
    /// <param name="other">The other node to compare against.</param>
    /// <returns>
    /// The <see cref="TopologyLevel"/> indicating the closest shared hierarchy level.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Topology levels are ordered from closest (SameNode) to farthest (CrossRegion):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>SameNode: both nodes have the same NodeId</description></item>
    ///   <item><description>SameRack: different nodes but same Rack (and Rack is non-null)</description></item>
    ///   <item><description>SameDatacenter: different racks but same Datacenter</description></item>
    ///   <item><description>SameRegion: different datacenters but same Region</description></item>
    ///   <item><description>CrossRegion: different regions or no shared hierarchy</description></item>
    /// </list>
    /// </remarks>
    public TopologyLevel GetLevelRelativeTo(NodeTopology other)
    {
        if (NodeId == other.NodeId) return TopologyLevel.SameNode;
        if (Rack == other.Rack && Rack != null) return TopologyLevel.SameRack;
        if (Datacenter == other.Datacenter && Datacenter != null) return TopologyLevel.SameDatacenter;
        if (Region == other.Region && Region != null) return TopologyLevel.SameRegion;
        return TopologyLevel.CrossRegion;
    }
}

/// <summary>
/// Represents the topology hierarchy level between two nodes.
/// </summary>
/// <remarks>
/// TopologyLevel is used for proximity scoring in location-aware routing. Closer levels
/// (lower numeric values) receive higher proximity scores.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Topology level enumeration (FOS-04)")]
public enum TopologyLevel
{
    /// <summary>Same node (NodeId matches).</summary>
    SameNode = 0,

    /// <summary>Different nodes but same rack.</summary>
    SameRack = 1,

    /// <summary>Different racks but same datacenter.</summary>
    SameDatacenter = 2,

    /// <summary>Different datacenters but same region.</summary>
    SameRegion = 3,

    /// <summary>Different regions (maximum distance).</summary>
    CrossRegion = 4
}
