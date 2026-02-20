using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Configuration for gossip-based P2P data replication.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Gossip-based P2P replication")]
    public sealed record GossipReplicatorConfiguration
    {
        /// <summary>
        /// Maximum capacity of the bounded pending message queue.
        /// </summary>
        public int MaxPendingMessages { get; init; } = 1000;

        /// <summary>
        /// Number of peers to forward each gossip message to (fanout).
        /// </summary>
        public int FanoutCount { get; init; } = 3;

        /// <summary>
        /// Maximum number of hops before a gossip message expires.
        /// </summary>
        public int MaxGenerations { get; init; } = 5;

        /// <summary>
        /// Interval in milliseconds for background gossip sweep.
        /// </summary>
        public int GossipIntervalMs { get; init; } = 500;
    }

    /// <summary>
    /// Gossip-based replicator implementing IGossipProtocol for bounded epidemic data propagation.
    /// Messages spread to a configurable fanout of random peers with generation tracking
    /// to prevent infinite propagation. Deduplication via seen-message set prevents reprocessing.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Gossip-based P2P replication")]
    public sealed class GossipReplicator : IGossipProtocol, IDisposable
    {
        private readonly IP2PNetwork _network;
        private readonly IClusterMembership _membership;
        private readonly GossipReplicatorConfiguration _config;
        private readonly Channel<GossipMessage> _pendingChannel;
        private readonly BoundedDictionary<string, DateTimeOffset> _seenMessages = new BoundedDictionary<string, DateTimeOffset>(1000);
        private readonly SemaphoreSlim _seenCleanupLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Task? _cleanupTask;

        /// <summary>
        /// Maximum number of seen message entries before eviction.
        /// </summary>
        private const int MaxSeenMessages = 10_000;

        /// <summary>
        /// Duration after which seen message entries are eligible for pruning.
        /// </summary>
        private static readonly TimeSpan SeenMessageExpiry = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Creates a new gossip replicator for epidemic data propagation.
        /// </summary>
        /// <param name="network">P2P network for message delivery.</param>
        /// <param name="membership">Cluster membership for peer discovery.</param>
        /// <param name="config">Optional gossip configuration.</param>
        public GossipReplicator(
            IP2PNetwork network,
            IClusterMembership membership,
            GossipReplicatorConfiguration? config = null)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _membership = membership ?? throw new ArgumentNullException(nameof(membership));
            _config = config ?? new GossipReplicatorConfiguration();

            _pendingChannel = Channel.CreateBounded<GossipMessage>(new BoundedChannelOptions(_config.MaxPendingMessages)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = false
            });

            // Subscribe to network events for incoming gossip data
            _network.OnPeerEvent += HandleIncomingPeerEvent;

            // Start background cleanup task
            _cleanupTask = Task.Run(() => RunCleanupLoopAsync(_cts.Token), _cts.Token);
        }

        /// <inheritdoc />
        public event Action<GossipMessage>? OnGossipReceived;

        /// <inheritdoc />
        public async Task SpreadAsync(GossipMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Check generation limit -- drop if exceeded
            if (message.Generation >= _config.MaxGenerations)
            {
                return;
            }

            // Add to seen set for deduplication
            _seenMessages[message.MessageId] = DateTimeOffset.UtcNow;

            // Select random peers (excluding self and origin)
            var selfId = _membership.GetSelf().NodeId;
            var availableMembers = _membership.GetMembers()
                .Where(m => !string.Equals(m.NodeId, selfId, StringComparison.Ordinal)
                         && !string.Equals(m.NodeId, message.OriginNodeId, StringComparison.Ordinal))
                .ToList();

            int fanout = Math.Min(_config.FanoutCount, availableMembers.Count);

            if (fanout > 0)
            {
                // Fisher-Yates partial shuffle for random peer selection
                for (int i = availableMembers.Count - 1; i > 0 && i >= availableMembers.Count - fanout; i--)
                {
                    int j = RandomNumberGenerator.GetInt32(0, i + 1);
                    (availableMembers[i], availableMembers[j]) = (availableMembers[j], availableMembers[i]);
                }

                // Send to selected peers with incremented generation
                var forwardMessage = message with { Generation = message.Generation + 1 };
                var data = JsonSerializer.SerializeToUtf8Bytes(forwardMessage, GossipJsonContext.Default.GossipMessage);

                var startIndex = Math.Max(0, availableMembers.Count - fanout);
                for (int i = startIndex; i < availableMembers.Count; i++)
                {
                    try
                    {
                        await _network.SendToPeerAsync(availableMembers[i].NodeId, data, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Peer may be unreachable; continue with other peers
                    }
                }
            }

            // Fire locally so the local node also processes the message
            OnGossipReceived?.Invoke(message);

            // Add to pending channel for local consumption
            _pendingChannel.Writer.TryWrite(message);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GossipMessage>> GetPendingAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var messages = new List<GossipMessage>();

            while (_pendingChannel.Reader.TryRead(out var message))
            {
                messages.Add(message);
            }

            return Task.FromResult<IReadOnlyList<GossipMessage>>(messages.AsReadOnly());
        }

        private void HandleIncomingPeerEvent(PeerEvent peerEvent)
        {
            // Peer events don't carry data payload directly in this architecture.
            // The actual gossip data arrives via RequestFromPeerAsync or SendToPeerAsync.
            // We handle gossip messages received through network data subscriptions.
        }

        /// <summary>
        /// Processes an incoming gossip message from the network.
        /// Called when data arrives via the P2P network transport layer.
        /// </summary>
        internal void ProcessIncomingData(byte[] data)
        {
            try
            {
                var message = JsonSerializer.Deserialize(data, GossipJsonContext.Default.GossipMessage);
                if (message == null) return;

                // Check deduplication
                if (_seenMessages.ContainsKey(message.MessageId))
                {
                    return; // Already seen, skip
                }

                // Mark as seen
                _seenMessages[message.MessageId] = DateTimeOffset.UtcNow;

                // Add to pending channel
                _pendingChannel.Writer.TryWrite(message);

                // Fire event
                OnGossipReceived?.Invoke(message);

                // Re-spread with incremented generation (if within limit)
                if (message.Generation < _config.MaxGenerations)
                {
                    _ = SpreadToRandomPeersAsync(message);
                }
            }
            catch
            {
                // Failed to deserialize -- may not be a gossip message
            }
        }

        private async Task SpreadToRandomPeersAsync(GossipMessage message)
        {
            try
            {
                var selfId = _membership.GetSelf().NodeId;
                var availableMembers = _membership.GetMembers()
                    .Where(m => !string.Equals(m.NodeId, selfId, StringComparison.Ordinal)
                             && !string.Equals(m.NodeId, message.OriginNodeId, StringComparison.Ordinal))
                    .ToList();

                int fanout = Math.Min(_config.FanoutCount, availableMembers.Count);
                if (fanout == 0) return;

                for (int i = availableMembers.Count - 1; i > 0 && i >= availableMembers.Count - fanout; i--)
                {
                    int j = RandomNumberGenerator.GetInt32(0, i + 1);
                    (availableMembers[i], availableMembers[j]) = (availableMembers[j], availableMembers[i]);
                }

                var forwardMessage = message with { Generation = message.Generation + 1 };
                var data = JsonSerializer.SerializeToUtf8Bytes(forwardMessage, GossipJsonContext.Default.GossipMessage);

                var startIndex = Math.Max(0, availableMembers.Count - fanout);
                for (int i = startIndex; i < availableMembers.Count; i++)
                {
                    try
                    {
                        await _network.SendToPeerAsync(availableMembers[i].NodeId, data, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Peer unreachable
                    }
                }
            }
            catch
            {
                // Spreading failure should not crash
            }
        }

        private async Task RunCleanupLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _seenCleanupLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var expiredKeys = _seenMessages
                        .Where(kv => (now - kv.Value) > SeenMessageExpiry)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _seenMessages.TryRemove(key, out _);
                    }

                    // Enforce max size -- evict oldest if over limit
                    if (_seenMessages.Count > MaxSeenMessages)
                    {
                        var toEvict = _seenMessages
                            .OrderBy(kv => kv.Value)
                            .Take(_seenMessages.Count - MaxSeenMessages)
                            .Select(kv => kv.Key)
                            .ToList();

                        foreach (var key in toEvict)
                        {
                            _seenMessages.TryRemove(key, out _);
                        }
                    }
                }
                finally
                {
                    _seenCleanupLock.Release();
                }
            }
        }

        /// <summary>
        /// Disposes the gossip replicator, stopping background tasks and releasing resources.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _pendingChannel.Writer.TryComplete();
            _cts.Dispose();
            _seenCleanupLock.Dispose();
            _network.OnPeerEvent -= HandleIncomingPeerEvent;
        }
    }

    /// <summary>
    /// Source-generated JSON context for gossip message serialization.
    /// </summary>
    [System.Text.Json.Serialization.JsonSerializable(typeof(GossipMessage))]
    internal partial class GossipJsonContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
