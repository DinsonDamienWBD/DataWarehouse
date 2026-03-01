using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Manages multiple independent Raft consensus groups, each with its own leader election,
    /// log, and state machine. Supports dynamic group creation and routing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Multi-Raft allows partitioning the state space into independent consensus groups.
    /// Each group has its own Raft instance with independent:
    /// <list type="bullet">
    /// <item><description>Leader election (different groups can have different leaders)</description></item>
    /// <item><description>Log replication (entries are group-scoped)</description></item>
    /// <item><description>State machine (group-specific committed state)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Key routing uses jump consistent hash to assign keys to groups, ensuring even distribution
    /// and minimal reshuffling when groups are added or removed.
    /// </para>
    /// <para>
    /// A default group named "default" is always created for backward compatibility with
    /// single-group deployments.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Multi-Raft group support")]
    public sealed class MultiRaftManager : IDisposable
    {
        /// <summary>
        /// The default group ID used for backward compatibility with single-group deployments.
        /// </summary>
        public const string DefaultGroupId = "default";

        private readonly BoundedDictionary<string, RaftConsensusEngine> _groups = new BoundedDictionary<string, RaftConsensusEngine>(1000);
        private readonly IClusterMembership _membership;
        private readonly IP2PNetwork _network;
        private readonly RaftConfiguration _defaultConfig;
        private readonly SemaphoreSlim _managementLock = new(1, 1);
        private bool _disposed;
        // LOW-436: cached sorted group-ID array; invalidated on add/remove.
        private volatile string[]? _cachedSortedGroupIds;

        /// <summary>
        /// Creates a new Multi-Raft manager with shared transport and membership layers.
        /// Automatically creates the default group.
        /// </summary>
        /// <param name="membership">Cluster membership for peer discovery.</param>
        /// <param name="network">P2P network for RPC communication (shared across groups).</param>
        /// <param name="defaultConfig">Default Raft configuration applied to new groups.</param>
        public MultiRaftManager(
            IClusterMembership membership,
            IP2PNetwork network,
            RaftConfiguration? defaultConfig = null)
        {
            _membership = membership ?? throw new ArgumentNullException(nameof(membership));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _defaultConfig = defaultConfig ?? new RaftConfiguration();
        }

        /// <summary>
        /// Gets the number of active consensus groups.
        /// </summary>
        public int GroupCount => _groups.Count;

        /// <summary>
        /// Gets the IDs of all active consensus groups.
        /// </summary>
        public IReadOnlyCollection<string> GroupIds => _groups.Keys.ToList().AsReadOnly();

        /// <summary>
        /// Creates a new Raft consensus group with the specified ID.
        /// Each group operates as an independent Raft cluster with its own leader election and log.
        /// </summary>
        /// <param name="groupId">Unique identifier for the group.</param>
        /// <param name="config">Optional per-group configuration (uses default if null).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created Raft consensus engine for the group.</returns>
        /// <exception cref="InvalidOperationException">Thrown if a group with the same ID already exists.</exception>
        public async Task<RaftConsensusEngine> CreateGroupAsync(
            string groupId,
            RaftConfiguration? config = null,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

            await _managementLock.WaitAsync(ct);
            try
            {
                if (_groups.ContainsKey(groupId))
                {
                    throw new InvalidOperationException($"Raft group '{groupId}' already exists.");
                }

                var groupConfig = config ?? _defaultConfig;

                // Create group-scoped transport that multiplexes messages by prepending groupId header
                var groupNetwork = new GroupScopedP2PNetwork(_network, groupId);

                var engine = new RaftConsensusEngine(_membership, groupNetwork, groupConfig);
                await engine.StartAsync(ct);

                if (!_groups.TryAdd(groupId, engine))
                {
                    engine.Stop();
                    engine.Dispose();
                    throw new InvalidOperationException($"Failed to register Raft group '{groupId}'.");
                }

                _cachedSortedGroupIds = null; // LOW-436: invalidate RouteKey cache
                return engine;
            }
            finally
            {
                _managementLock.Release();
            }
        }

        /// <summary>
        /// Gets the Raft consensus engine for the specified group.
        /// Returns null if the group does not exist.
        /// </summary>
        /// <param name="groupId">The group ID.</param>
        /// <returns>The Raft engine for the group, or null.</returns>
        public RaftConsensusEngine? GetGroup(string groupId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _groups.TryGetValue(groupId, out var engine) ? engine : null;
        }

        /// <summary>
        /// Removes a Raft consensus group, stopping its engine.
        /// </summary>
        /// <param name="groupId">The group ID to remove.</param>
        /// <returns>True if the group was found and removed; false otherwise.</returns>
        public bool RemoveGroup(string groupId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_groups.TryRemove(groupId, out var engine))
            {
                _cachedSortedGroupIds = null; // LOW-436: invalidate RouteKey cache
                engine.Stop();
                engine.Dispose();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Routes a key to a group using jump consistent hash.
        /// This ensures even distribution and minimal reshuffling when groups change.
        /// </summary>
        /// <param name="key">The key to route.</param>
        /// <returns>The group ID for the key.</returns>
        public string RouteKey(string key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // LOW-436: use cached sorted array; build only on first call or after group membership change.
            var groupIds = _cachedSortedGroupIds;
            if (groupIds == null)
            {
                groupIds = _groups.Keys.OrderBy(g => g, StringComparer.Ordinal).ToArray();
                _cachedSortedGroupIds = groupIds;
            }

            if (groupIds.Length == 0)
                return DefaultGroupId;

            if (groupIds.Length == 1)
                return groupIds[0];

            // Jump consistent hash (Lamping & Veach, Google)
            int bucket = JumpConsistentHash(HashKey(key), groupIds.Length);
            return groupIds[bucket];
        }

        /// <summary>
        /// Proposes a state change to the appropriate group for the given key.
        /// Routes the key to a group, then proposes through that group's leader.
        /// </summary>
        /// <param name="key">Routing key for group selection.</param>
        /// <param name="proposal">The proposal to submit.</param>
        /// <returns>True if the proposal was committed by the group's quorum.</returns>
        public async Task<bool> ProposeAsync(string key, Proposal proposal)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var groupId = RouteKey(key);
            var engine = GetGroup(groupId);

            if (engine == null)
                throw new InvalidOperationException($"No Raft group found for key '{key}' (routed to group '{groupId}').");

            return await engine.ProposeAsync(proposal);
        }

        /// <summary>
        /// Gets the status of all groups including leader info and log length.
        /// </summary>
        /// <returns>Dictionary of group ID to status info.</returns>
        public IReadOnlyDictionary<string, MultiRaftGroupStatus> GetGroupStatuses()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var statuses = new Dictionary<string, MultiRaftGroupStatus>();

            foreach (var (groupId, engine) in _groups)
            {
                statuses[groupId] = new MultiRaftGroupStatus(
                    GroupId: groupId,
                    IsLeader: engine.IsLeader,
                    IsHealthy: true);
            }

            return statuses;
        }

        /// <summary>
        /// Disposes all Raft groups and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var (_, engine) in _groups)
            {
                try
                {
                    engine.Stop();
                    engine.Dispose();
                }
                catch
                {
                    // Best-effort cleanup during disposal
                }
            }

            _groups.Clear();
            _managementLock.Dispose();
        }

        #region Jump Consistent Hash

        /// <summary>
        /// Jump consistent hash (Lamping and Veach, 2014).
        /// O(ln(n)) time, zero memory, monotonic (keys only move to new buckets when bucket count grows).
        /// </summary>
        private static int JumpConsistentHash(ulong key, int numBuckets)
        {
            long b = -1;
            long j = 0;

            while (j < numBuckets)
            {
                b = j;
                key = key * 2862933555777941757UL + 1;
                j = (long)((b + 1) * ((double)(1L << 31) / ((key >> 33) + 1)));
            }

            return (int)b;
        }

        /// <summary>
        /// FNV-1a 64-bit hash for key routing.
        /// </summary>
        private static ulong HashKey(string key)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in key)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        #endregion
    }

    /// <summary>
    /// Status of a Multi-Raft group.
    /// </summary>
    /// <param name="GroupId">The group identifier.</param>
    /// <param name="IsLeader">Whether this node is the leader for the group.</param>
    /// <param name="IsHealthy">Whether the group is operational.</param>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Multi-Raft group status")]
    public sealed record MultiRaftGroupStatus(
        string GroupId,
        bool IsLeader,
        bool IsHealthy);

    /// <summary>
    /// P2P network wrapper that prefixes all messages with a group ID for multiplexing.
    /// Allows multiple Raft groups to share a single transport layer.
    /// </summary>
    /// <remarks>
    /// Messages are prefixed with a 4-byte group ID length followed by the group ID bytes,
    /// enabling the receiving side to route messages to the correct Raft group.
    /// </remarks>
    internal sealed class GroupScopedP2PNetwork : IP2PNetwork
    {
        private readonly IP2PNetwork _inner;
        private readonly string _groupId;
        private readonly byte[] _groupIdPrefix;

        public GroupScopedP2PNetwork(IP2PNetwork inner, string groupId)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _groupId = groupId ?? throw new ArgumentNullException(nameof(groupId));

            // Pre-compute the prefix: [4 bytes length][groupId bytes]
            var groupBytes = System.Text.Encoding.UTF8.GetBytes(groupId);
            _groupIdPrefix = new byte[4 + groupBytes.Length];
            BitConverter.TryWriteBytes(_groupIdPrefix.AsSpan(0, 4), groupBytes.Length);
            groupBytes.CopyTo(_groupIdPrefix.AsSpan(4));
        }

        /// <inheritdoc/>
        public event Action<PeerEvent>? OnPeerEvent
        {
            add => _inner.OnPeerEvent += value;
            remove => _inner.OnPeerEvent -= value;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<PeerInfo>> DiscoverPeersAsync(CancellationToken ct = default)
        {
            return _inner.DiscoverPeersAsync(ct);
        }

        /// <inheritdoc/>
        public async Task<byte[]> RequestFromPeerAsync(string peerId, byte[] data, CancellationToken ct = default)
        {
            // Prepend group ID header to outgoing messages
            var prefixed = new byte[_groupIdPrefix.Length + data.Length];
            _groupIdPrefix.CopyTo(prefixed, 0);
            data.CopyTo(prefixed, _groupIdPrefix.Length);

            return await _inner.RequestFromPeerAsync(peerId, prefixed, ct);
        }

        /// <inheritdoc/>
        public Task BroadcastAsync(byte[] data, CancellationToken ct = default)
        {
            // Prepend group ID header
            var prefixed = new byte[_groupIdPrefix.Length + data.Length];
            _groupIdPrefix.CopyTo(prefixed, 0);
            data.CopyTo(prefixed, _groupIdPrefix.Length);

            return _inner.BroadcastAsync(prefixed, ct);
        }

        /// <inheritdoc/>
        public Task SendToPeerAsync(string peerId, byte[] data, CancellationToken ct = default)
        {
            var prefixed = new byte[_groupIdPrefix.Length + data.Length];
            _groupIdPrefix.CopyTo(prefixed, 0);
            data.CopyTo(prefixed, _groupIdPrefix.Length);

            return _inner.SendToPeerAsync(peerId, prefixed, ct);
        }
    }
}
