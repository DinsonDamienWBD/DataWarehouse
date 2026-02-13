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
            return Task.FromResult<IReadOnlyList<PeerInfo>>(Array.Empty<PeerInfo>());
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
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GossipMessage>> GetPendingAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<GossipMessage>>(Array.Empty<GossipMessage>());
        }

        // Suppress CS0067: events are never used in single-node mode
        private void SuppressWarning()
        {
            OnPeerEvent?.Invoke(null!);
            OnGossipReceived?.Invoke(null!);
        }
    }
}
