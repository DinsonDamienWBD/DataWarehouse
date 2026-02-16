using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// Mesh network topology representation.
/// </summary>
/// <remarks>
/// <para>
/// Contains discovered nodes, links (neighbor relationships), and computed routes for multi-hop communication.
/// Topology is dynamic and refreshed periodically via DiscoverTopologyAsync().
/// </para>
/// <para>
/// <strong>Routing:</strong> Routes dictionary maps destination node ID to path (list of intermediate node IDs).
/// Empty path means direct neighbor (single-hop). Multi-element path indicates multi-hop route.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh topology (EDGE-08)")]
public sealed record MeshTopology
{
    /// <summary>
    /// List of discovered nodes in the mesh network.
    /// </summary>
    public required IReadOnlyList<MeshNode> Nodes { get; init; }

    /// <summary>
    /// List of links between nodes (neighbor relationships with link quality).
    /// </summary>
    public required IReadOnlyList<MeshLink> Links { get; init; }

    /// <summary>
    /// Computed routes from this node to all reachable destinations.
    /// Key: Destination node ID. Value: Path (list of intermediate node IDs, empty for direct neighbors).
    /// </summary>
    public required IReadOnlyDictionary<int, int[]> Routes { get; init; }
}

/// <summary>
/// Mesh network node descriptor.
/// </summary>
/// <param name="NodeId">Unique node ID in the mesh network.</param>
/// <param name="Role">Node role (Coordinator, Router, EndDevice, Gateway).</param>
/// <param name="Address">Protocol-specific address (e.g., LoRa DevAddr, Zigbee MAC).</param>
/// <param name="BatteryLevel">Battery level percentage (0-100); null if AC-powered or unknown.</param>
/// <param name="LastSeen">Timestamp of last communication with this node.</param>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh node descriptor (EDGE-08)")]
public sealed record MeshNode(
    int NodeId,
    NodeRole Role,
    string? Address,
    int? BatteryLevel,
    DateTime LastSeen);

/// <summary>
/// Mesh network link (neighbor relationship).
/// </summary>
/// <param name="SourceNodeId">Source node ID.</param>
/// <param name="TargetNodeId">Target node ID.</param>
/// <param name="LinkQuality">Link quality indicator (0-100, higher is better). Based on RSSI, SNR, or packet loss.</param>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh link descriptor (EDGE-08)")]
public sealed record MeshLink(
    int SourceNodeId,
    int TargetNodeId,
    int LinkQuality);
