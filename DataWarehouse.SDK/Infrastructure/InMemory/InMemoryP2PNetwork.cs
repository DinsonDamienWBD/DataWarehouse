using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IP2PNetwork"/> and <see cref="IGossipProtocol"/>.
    /// In single-node mode there are no peers, so discovery returns empty and
    /// send operations throw because there are no peers to communicate with.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryP2PNetwork : IP2PNetwork, IGossipProtocol
    {
        /// <inheritdoc />
        public event Action<PeerEvent>? OnPeerEvent;

        /// <inheritdoc />
        public event Action<GossipMessage>? OnGossipReceived;

        /// <inheritdoc />
        public Task<IReadOnlyList<PeerInfo>> DiscoverPeersAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // In single-node mode there are no peers; no peer events are raised.
            // OnPeerEvent would be raised here when peers join or leave (none in single-node).
            return Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>());
        }

        /// <summary>
        /// Raises a peer event. Used by the host process to notify subscribers when a
        /// peer joins or leaves in multi-node scenarios that reuse the in-memory transport.
        /// </summary>
        /// <param name="peerEvent">The peer event to raise.</param>
        public void RaisePeerEvent(PeerEvent peerEvent)
        {
            OnPeerEvent?.Invoke(peerEvent);
        }

        /// <inheritdoc />
        public Task SendToPeerAsync(string peerId, byte[] data, CancellationToken ct = default)
        {
            throw new InvalidOperationException("No peers available in single-node mode.");
        }

        /// <inheritdoc />
        public Task BroadcastAsync(byte[] data, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<byte[]> RequestFromPeerAsync(string peerId, byte[] request, CancellationToken ct = default)
        {
            throw new InvalidOperationException("No peers available in single-node mode.");
        }

        /// <inheritdoc />
        public Task SpreadAsync(GossipMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // In single-node mode, gossip loops back to local node only
            OnGossipReceived?.Invoke(message);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GossipMessage>> GetPendingAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<GossipMessage>>(Array.Empty<GossipMessage>());
        }
    }
}
