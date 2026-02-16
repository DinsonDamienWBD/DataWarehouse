using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Mesh;

/// <summary>
/// Mesh network interface for Zigbee, LoRa, and BLE mesh protocols.
/// </summary>
/// <remarks>
/// <para>
/// Provides multi-hop sensor network communication with topology discovery, message routing,
/// and battery-optimized sleep scheduling. Integrates with AEDS plugin for edge data aggregation.
/// </para>
/// <para>
/// <strong>Lifecycle:</strong> InitializeAsync() → SendMessageAsync()/OnMessageReceived events →
/// DiscoverTopologyAsync() (periodic) → DisposeAsync(). Topology discovery should be run periodically
/// (e.g., every 5-10 minutes) to detect new nodes and link quality changes.
/// </para>
/// <para>
/// <strong>Multi-Hop Routing:</strong> Uses AODV-like (Ad hoc On-Demand Distance Vector) routing.
/// Routes are discovered dynamically when a message is sent to an unknown destination. Route table
/// is cached and refreshed during topology discovery.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh network interface (EDGE-08)")]
public interface IMeshNetwork : IAsyncDisposable
{
    /// <summary>
    /// Initializes the mesh network with specified settings.
    /// </summary>
    /// <param name="settings">Mesh network configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(MeshSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Sends a message to a destination node. Uses multi-hop routing if destination is not a direct neighbor.
    /// </summary>
    /// <param name="destinationNodeId">Destination node ID.</param>
    /// <param name="payload">Message payload (application data).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if mesh is not initialized or no route to destination.</exception>
    Task SendMessageAsync(int destinationNodeId, byte[] payload, CancellationToken ct = default);

    /// <summary>
    /// Discovers the mesh network topology (nodes, links, routes).
    /// Should be called periodically to maintain up-to-date routing information.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current mesh topology snapshot.</returns>
    Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default);

    /// <summary>
    /// Event raised when a message is received from another node.
    /// </summary>
    event EventHandler<MeshMessageReceivedEventArgs>? OnMessageReceived;

    /// <summary>
    /// Event raised when topology changes (new node, node left, link quality changed).
    /// </summary>
    event EventHandler<MeshTopologyChangedEventArgs>? OnTopologyChanged;

    /// <summary>
    /// Gets whether the mesh network is initialized.
    /// </summary>
    bool IsInitialized { get; }
}

/// <summary>
/// Event arguments for mesh message received events.
/// </summary>
/// <param name="SourceNodeId">Source node ID that sent the message.</param>
/// <param name="Payload">Message payload (application data).</param>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh message event args (EDGE-08)")]
public sealed record MeshMessageReceivedEventArgs(int SourceNodeId, byte[] Payload);

/// <summary>
/// Event arguments for mesh topology changed events.
/// </summary>
/// <param name="NewTopology">Updated mesh topology snapshot.</param>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh topology event args (EDGE-08)")]
public sealed record MeshTopologyChangedEventArgs(MeshTopology NewTopology);
