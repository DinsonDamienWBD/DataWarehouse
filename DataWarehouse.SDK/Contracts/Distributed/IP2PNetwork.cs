using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for peer-to-peer network communication (DIST-03).
    /// Provides peer discovery, direct messaging, and broadcast capabilities
    /// for decentralized data distribution.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IP2PNetwork
    {
        /// <summary>
        /// Raised when a peer event occurs (discovered, lost, updated).
        /// </summary>
        event Action<PeerEvent>? OnPeerEvent;

        /// <summary>
        /// Discovers all currently known peers in the network.
        /// </summary>
        /// <param name="ct">Cancellation token for the discovery operation.</param>
        /// <returns>A read-only list of known peers.</returns>
        Task<IReadOnlyList<PeerInfo>> DiscoverPeersAsync(CancellationToken ct = default);

        /// <summary>
        /// Sends data directly to a specific peer.
        /// </summary>
        /// <param name="peerId">The target peer identifier.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="ct">Cancellation token for the send operation.</param>
        /// <returns>A task representing the send operation.</returns>
        Task SendToPeerAsync(string peerId, byte[] data, CancellationToken ct = default);

        /// <summary>
        /// Broadcasts data to all known peers.
        /// </summary>
        /// <param name="data">The data to broadcast.</param>
        /// <param name="ct">Cancellation token for the broadcast operation.</param>
        /// <returns>A task representing the broadcast operation.</returns>
        Task BroadcastAsync(byte[] data, CancellationToken ct = default);

        /// <summary>
        /// Sends a request to a peer and waits for a response.
        /// </summary>
        /// <param name="peerId">The target peer identifier.</param>
        /// <param name="request">The request data.</param>
        /// <param name="ct">Cancellation token for the request operation.</param>
        /// <returns>The response data from the peer.</returns>
        Task<byte[]> RequestFromPeerAsync(string peerId, byte[] request, CancellationToken ct = default);
    }

    /// <summary>
    /// Contract for gossip-based protocol for eventually-consistent state propagation (DIST-03).
    /// Gossip protocols spread information epidemically across the cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IGossipProtocol
    {
        /// <summary>
        /// Spreads a gossip message to peers.
        /// The message propagates epidemically through the cluster.
        /// </summary>
        /// <param name="message">The gossip message to spread.</param>
        /// <param name="ct">Cancellation token for the spread operation.</param>
        /// <returns>A task representing the spread operation.</returns>
        Task SpreadAsync(GossipMessage message, CancellationToken ct = default);

        /// <summary>
        /// Gets gossip messages that have been received but not yet processed.
        /// </summary>
        /// <param name="ct">Cancellation token for the retrieval operation.</param>
        /// <returns>A read-only list of pending gossip messages.</returns>
        Task<IReadOnlyList<GossipMessage>> GetPendingAsync(CancellationToken ct = default);

        /// <summary>
        /// Raised when a gossip message is received from a peer.
        /// </summary>
        event Action<GossipMessage>? OnGossipReceived;
    }

    /// <summary>
    /// Information about a peer in the P2P network.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record PeerInfo
    {
        /// <summary>
        /// Unique identifier for the peer.
        /// </summary>
        public required string PeerId { get; init; }

        /// <summary>
        /// Network address of the peer.
        /// </summary>
        public required string Address { get; init; }

        /// <summary>
        /// Port the peer listens on.
        /// </summary>
        public required int Port { get; init; }

        /// <summary>
        /// When the peer was last seen.
        /// </summary>
        public required DateTimeOffset LastSeen { get; init; }

        /// <summary>
        /// Additional metadata about the peer.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Event describing a peer state change.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record PeerEvent
    {
        /// <summary>
        /// The type of peer event.
        /// </summary>
        public required PeerEventType EventType { get; init; }

        /// <summary>
        /// The peer involved in the event.
        /// </summary>
        public required PeerInfo Peer { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Types of peer events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum PeerEventType
    {
        /// <summary>A new peer was discovered.</summary>
        PeerDiscovered,
        /// <summary>A peer was lost (unreachable).</summary>
        PeerLost,
        /// <summary>A peer's information was updated.</summary>
        PeerUpdated
    }

    /// <summary>
    /// A gossip message that propagates epidemically through the cluster.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record GossipMessage
    {
        /// <summary>
        /// Unique identifier for this gossip message.
        /// </summary>
        public required string MessageId { get; init; }

        /// <summary>
        /// The node that originated this gossip message.
        /// </summary>
        public required string OriginNodeId { get; init; }

        /// <summary>
        /// The message payload.
        /// </summary>
        public required byte[] Payload { get; init; }

        /// <summary>
        /// The generation counter for crescent propagation tracking.
        /// </summary>
        public required int Generation { get; init; }

        /// <summary>
        /// When this message was created.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }
}
