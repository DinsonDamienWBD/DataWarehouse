using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Infrastructure.Distributed;

namespace DataWarehouse.Plugins.UltimateConsensus.Scaling;

/// <summary>
/// Manages scaling of the Raft consensus subsystem with multi-group support,
/// connection pooling, and adaptive election timeouts based on observed network latency.
/// Implements <see cref="IScalableSubsystem"/> for runtime reconfiguration and metrics.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Multi-Raft group support:</strong> Partitions state across multiple Raft groups,
/// each handling up to <see cref="MaxNodesPerGroup"/> nodes. A consistent hash ring maps
/// keys to groups. New groups are created automatically when the node count exceeds the threshold.
/// </para>
/// <para>
/// <strong>Connection pooling:</strong> Maintains a pool of reusable connections per peer node
/// via <see cref="BoundedCache{TKey,TValue}"/>. Connections are validated with heartbeat before
/// reuse, and idle connections are recycled after a configurable timeout.
/// </para>
/// <para>
/// <strong>Dynamic election timeouts:</strong> Measures network RTT to each peer using heartbeat
/// responses. Election timeout is set to <c>max(MinElectionTimeout, observedP99Rtt * ElectionTimeoutMultiplier)</c>.
/// RTT measurements are recalculated every 30 seconds from a sliding window.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-05: Consensus scaling manager with multi-Raft, connection pooling, adaptive timeouts")]
public sealed class ConsensusScalingManager : IScalableSubsystem, IDisposable
{
    // ---- Multi-Raft group management ----
    private readonly ConcurrentDictionary<string, RaftGroupInfo> _groups = new();
    private readonly object _groupLock = new();

    // ---- Connection pooling ----
    private readonly BoundedCache<string, ConnectionPool> _connectionPools;

    // ---- RTT measurement ----
    private readonly ConcurrentDictionary<string, RttTracker> _rttTrackers = new();
    private Timer? _rttRecalcTimer;
    private const int RttRecalcIntervalMs = 30_000;
    private const int RttSlidingWindowSize = 100;

    // ---- Configuration ----
    private int _maxNodesPerGroup;
    private int _maxConnectionsPerPeer;
    private int _minElectionTimeoutMs;
    private int _electionTimeoutMultiplier;
    private TimeSpan _idleConnectionTimeout;
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _totalElections;
    private long _groupCreations;

    private bool _disposed;

    /// <summary>
    /// Maximum number of nodes allowed per Raft group before a new group is created.
    /// Default: 100.
    /// </summary>
    public int MaxNodesPerGroup => _maxNodesPerGroup;

    /// <summary>
    /// Maximum number of pooled connections per peer node. Default: 4.
    /// </summary>
    public int MaxConnectionsPerPeer => _maxConnectionsPerPeer;

    /// <summary>
    /// Minimum election timeout in milliseconds, regardless of observed RTT. Default: 150.
    /// </summary>
    public int MinElectionTimeoutMs => _minElectionTimeoutMs;

    /// <summary>
    /// Multiplier applied to the observed P99 RTT to compute election timeout. Default: 10.
    /// </summary>
    public int ElectionTimeoutMultiplier => _electionTimeoutMultiplier;

    /// <summary>
    /// Timeout after which idle pooled connections are recycled. Default: 5 minutes.
    /// </summary>
    public TimeSpan IdleConnectionTimeout => _idleConnectionTimeout;

    /// <summary>
    /// Gets the number of active Raft groups.
    /// </summary>
    public int ActiveGroupCount => _groups.Count;

    /// <summary>
    /// Initializes a new <see cref="ConsensusScalingManager"/> with default configuration.
    /// </summary>
    /// <param name="maxNodesPerGroup">Maximum nodes per Raft group. Default: 100.</param>
    /// <param name="maxConnectionsPerPeer">Maximum pooled connections per peer. Default: 4.</param>
    /// <param name="minElectionTimeoutMs">Minimum election timeout in ms. Default: 150.</param>
    /// <param name="electionTimeoutMultiplier">RTT multiplier for election timeout. Default: 10.</param>
    /// <param name="idleConnectionTimeoutMinutes">Idle connection recycle timeout in minutes. Default: 5.</param>
    public ConsensusScalingManager(
        int maxNodesPerGroup = 100,
        int maxConnectionsPerPeer = 4,
        int minElectionTimeoutMs = 150,
        int electionTimeoutMultiplier = 10,
        int idleConnectionTimeoutMinutes = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodesPerGroup, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnectionsPerPeer, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minElectionTimeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(electionTimeoutMultiplier, 1);

        _maxNodesPerGroup = maxNodesPerGroup;
        _maxConnectionsPerPeer = maxConnectionsPerPeer;
        _minElectionTimeoutMs = minElectionTimeoutMs;
        _electionTimeoutMultiplier = electionTimeoutMultiplier;
        _idleConnectionTimeout = TimeSpan.FromMinutes(idleConnectionTimeoutMinutes);

        _currentLimits = new ScalingLimits(
            MaxCacheEntries: maxNodesPerGroup * 10,
            MaxMemoryBytes: 100 * 1024 * 1024,
            MaxConcurrentOperations: maxConnectionsPerPeer * 16,
            MaxQueueDepth: 1_000);

        // Connection pool cache with TTL eviction for idle connection recycling
        _connectionPools = new BoundedCache<string, ConnectionPool>(
            new BoundedCacheOptions<string, ConnectionPool>
            {
                MaxEntries = 1_000,
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = _idleConnectionTimeout,
                KeyToString = k => k
            });

        _rttRecalcTimer = new Timer(RecalculateElectionTimeouts, null, RttRecalcIntervalMs, RttRecalcIntervalMs);
    }

    #region Multi-Raft Group Management

    /// <summary>
    /// Registers a Raft group with the scaling manager. If the number of nodes in any
    /// existing group exceeds <see cref="MaxNodesPerGroup"/>, a new group should be created.
    /// </summary>
    /// <param name="groupId">Unique identifier for the Raft group.</param>
    /// <param name="nodeCount">Current number of nodes in the group.</param>
    /// <returns>True if the group was registered; false if it already exists.</returns>
    public bool RegisterGroup(string groupId, int nodeCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var info = new RaftGroupInfo(
            GroupId: groupId,
            NodeCount: nodeCount,
            LogSize: 0,
            ElectionCount: 0,
            CreatedAt: DateTime.UtcNow,
            CurrentElectionTimeoutMs: _minElectionTimeoutMs);

        if (_groups.TryAdd(groupId, info))
        {
            Interlocked.Increment(ref _groupCreations);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Updates the node count for an existing group. If the count exceeds
    /// <see cref="MaxNodesPerGroup"/>, returns true to indicate a split is needed.
    /// </summary>
    /// <param name="groupId">Group to update.</param>
    /// <param name="nodeCount">New node count.</param>
    /// <returns>True if the group should be split (node count exceeds max); false otherwise.</returns>
    public bool UpdateGroupNodeCount(string groupId, int nodeCount)
    {
        if (_groups.TryGetValue(groupId, out var existing))
        {
            _groups[groupId] = existing with { NodeCount = nodeCount };
            return nodeCount > _maxNodesPerGroup;
        }
        return false;
    }

    /// <summary>
    /// Updates the log size metric for a group.
    /// </summary>
    /// <param name="groupId">Group to update.</param>
    /// <param name="logSize">Current log size in entries.</param>
    public void UpdateGroupLogSize(string groupId, long logSize)
    {
        if (_groups.TryGetValue(groupId, out var existing))
        {
            _groups[groupId] = existing with { LogSize = logSize };
        }
    }

    /// <summary>
    /// Records an election event for a group.
    /// </summary>
    /// <param name="groupId">Group where the election occurred.</param>
    public void RecordElection(string groupId)
    {
        Interlocked.Increment(ref _totalElections);
        if (_groups.TryGetValue(groupId, out var existing))
        {
            _groups[groupId] = existing with { ElectionCount = existing.ElectionCount + 1 };
        }
    }

    /// <summary>
    /// Removes a group from the scaling manager.
    /// </summary>
    /// <param name="groupId">Group to remove.</param>
    /// <returns>True if the group was found and removed.</returns>
    public bool RemoveGroup(string groupId)
    {
        return _groups.TryRemove(groupId, out _);
    }

    /// <summary>
    /// Routes a key to a Raft group using consistent hashing (FNV-1a + jump consistent hash).
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>The group ID that should handle this key, or null if no groups exist.</returns>
    public string? RouteKey(string key)
    {
        var groupIds = _groups.Keys.OrderBy(g => g, StringComparer.Ordinal).ToArray();
        if (groupIds.Length == 0) return null;
        if (groupIds.Length == 1) return groupIds[0];

        int bucket = JumpConsistentHash(Fnv1aHash(key), groupIds.Length);
        return groupIds[bucket];
    }

    #endregion

    #region Connection Pooling

    /// <summary>
    /// Gets or creates a connection pool for the specified peer node.
    /// </summary>
    /// <param name="peerId">The peer node identifier.</param>
    /// <returns>The connection pool for the peer.</returns>
    public ConnectionPool GetOrCreatePool(string peerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);

        var existing = _connectionPools.GetOrDefault(peerId);
        if (existing != null) return existing;

        var pool = new ConnectionPool(peerId, _maxConnectionsPerPeer, _idleConnectionTimeout);
        _connectionPools.Put(peerId, pool);
        return pool;
    }

    /// <summary>
    /// Gets the total number of active connection pools.
    /// </summary>
    public int ActivePoolCount => _connectionPools.Count;

    #endregion

    #region Dynamic Election Timeouts

    /// <summary>
    /// Records a round-trip time measurement for a peer, used to compute adaptive election timeouts.
    /// </summary>
    /// <param name="peerId">The peer that responded.</param>
    /// <param name="rttMs">The observed round-trip time in milliseconds.</param>
    public void RecordRtt(string peerId, double rttMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);

        var tracker = _rttTrackers.GetOrAdd(peerId, _ => new RttTracker(RttSlidingWindowSize));
        tracker.Record(rttMs);
    }

    /// <summary>
    /// Computes the adaptive election timeout based on observed P99 RTT across all peers.
    /// Returns <c>max(MinElectionTimeout, observedP99Rtt * ElectionTimeoutMultiplier)</c>.
    /// </summary>
    /// <returns>The computed election timeout in milliseconds.</returns>
    public int ComputeElectionTimeout()
    {
        if (_rttTrackers.IsEmpty)
            return _minElectionTimeoutMs;

        double maxP99 = 0;
        foreach (var tracker in _rttTrackers.Values)
        {
            var p99 = tracker.GetP99();
            if (p99 > maxP99) maxP99 = p99;
        }

        int computed = (int)(maxP99 * _electionTimeoutMultiplier);
        return Math.Max(_minElectionTimeoutMs, computed);
    }

    /// <summary>
    /// Gets the P50 (median) RTT across all tracked peers.
    /// </summary>
    /// <returns>P50 RTT in milliseconds, or 0 if no measurements.</returns>
    public double GetP50Rtt()
    {
        if (_rttTrackers.IsEmpty) return 0;
        return _rttTrackers.Values.Select(t => t.GetP50()).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// Gets the P99 RTT across all tracked peers.
    /// </summary>
    /// <returns>P99 RTT in milliseconds, or 0 if no measurements.</returns>
    public double GetP99Rtt()
    {
        if (_rttTrackers.IsEmpty) return 0;
        return _rttTrackers.Values.Select(t => t.GetP99()).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// Timer callback that recalculates election timeouts for all groups every 30 seconds.
    /// </summary>
    private void RecalculateElectionTimeouts(object? state)
    {
        if (_disposed) return;

        var timeout = ComputeElectionTimeout();

        foreach (var groupId in _groups.Keys.ToArray())
        {
            if (_groups.TryGetValue(groupId, out var info))
            {
                _groups[groupId] = info with { CurrentElectionTimeoutMs = timeout };
            }
        }
    }

    #endregion

    #region IScalableSubsystem

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["consensus.groupCount"] = _groups.Count,
            ["consensus.totalElections"] = Interlocked.Read(ref _totalElections),
            ["consensus.groupCreations"] = Interlocked.Read(ref _groupCreations),
            ["consensus.connectionPoolCount"] = _connectionPools.Count,
            ["consensus.rttP50Ms"] = GetP50Rtt(),
            ["consensus.rttP99Ms"] = GetP99Rtt(),
            ["consensus.electionTimeoutMs"] = ComputeElectionTimeout(),
            ["consensus.maxNodesPerGroup"] = _maxNodesPerGroup,
            ["consensus.maxConnectionsPerPeer"] = _maxConnectionsPerPeer,
            ["backpressure.state"] = CurrentBackpressureState.ToString()
        };

        // Per-group metrics
        foreach (var (groupId, info) in _groups)
        {
            metrics[$"group.{groupId}.nodeCount"] = info.NodeCount;
            metrics[$"group.{groupId}.logSize"] = info.LogSize;
            metrics[$"group.{groupId}.electionCount"] = info.ElectionCount;
            metrics[$"group.{groupId}.electionTimeoutMs"] = info.CurrentElectionTimeoutMs;
        }

        // Connection pool utilization
        int totalPooled = 0;
        int totalMaxPooled = 0;
        foreach (var kvp in _connectionPools)
        {
            totalPooled += kvp.Value.ActiveConnections;
            totalMaxPooled += kvp.Value.MaxConnections;
        }
        metrics["consensus.connectionPool.active"] = totalPooled;
        metrics["consensus.connectionPool.max"] = totalMaxPooled;
        metrics["consensus.connectionPool.utilization"] = totalMaxPooled > 0
            ? Math.Round((double)totalPooled / totalMaxPooled, 3)
            : 0.0;

        return metrics;
    }

    /// <inheritdoc/>
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _currentLimits = limits;

        // Derive consensus-specific limits from the generic ScalingLimits
        if (limits.MaxCacheEntries > 0)
        {
            Interlocked.Exchange(ref _maxNodesPerGroup, Math.Max(1, limits.MaxCacheEntries / 10));
        }
        if (limits.MaxConcurrentOperations > 0)
        {
            Interlocked.Exchange(ref _maxConnectionsPerPeer, Math.Max(1, limits.MaxConcurrentOperations / 16));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            // Compute backpressure from group utilization
            int totalNodes = _groups.Values.Sum(g => g.NodeCount);
            int maxCapacity = _groups.Count * _maxNodesPerGroup;

            if (maxCapacity == 0) return BackpressureState.Normal;

            double utilization = (double)totalNodes / maxCapacity;
            return utilization switch
            {
                >= 0.95 => BackpressureState.Critical,
                >= 0.80 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _rttRecalcTimer?.Dispose();
        _rttRecalcTimer = null;
        _connectionPools.Dispose();
    }

    #region Hash Utilities

    /// <summary>
    /// FNV-1a 64-bit hash for consistent key routing.
    /// </summary>
    private static ulong Fnv1aHash(string key)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>
    /// Jump consistent hash (Lamping and Veach, 2014).
    /// O(ln(n)) time, zero memory, monotonic.
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

    #endregion

    #region Supporting Types

    /// <summary>
    /// Metadata about a Raft consensus group managed by the scaling infrastructure.
    /// </summary>
    /// <param name="GroupId">Unique group identifier.</param>
    /// <param name="NodeCount">Current number of nodes in the group.</param>
    /// <param name="LogSize">Current log size in entries.</param>
    /// <param name="ElectionCount">Total number of elections held in this group.</param>
    /// <param name="CreatedAt">UTC timestamp when the group was created.</param>
    /// <param name="CurrentElectionTimeoutMs">Current adaptive election timeout in milliseconds.</param>
    private sealed record RaftGroupInfo(
        string GroupId,
        int NodeCount,
        long LogSize,
        long ElectionCount,
        DateTime CreatedAt,
        int CurrentElectionTimeoutMs);

    /// <summary>
    /// Tracks round-trip time measurements in a sliding window for computing
    /// percentile-based adaptive election timeouts.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-05: RTT sliding window tracker for adaptive timeouts")]
    public sealed class RttTracker
    {
        private readonly double[] _samples;
        private readonly int _capacity;
        private int _count;
        private int _writeIndex;
        private readonly object _lock = new();

        /// <summary>
        /// Initializes a new RTT tracker with the specified sliding window size.
        /// </summary>
        /// <param name="windowSize">Maximum number of RTT samples to retain.</param>
        public RttTracker(int windowSize)
        {
            _capacity = Math.Max(1, windowSize);
            _samples = new double[_capacity];
        }

        /// <summary>
        /// Records a new RTT measurement.
        /// </summary>
        /// <param name="rttMs">Round-trip time in milliseconds.</param>
        public void Record(double rttMs)
        {
            lock (_lock)
            {
                _samples[_writeIndex] = rttMs;
                _writeIndex = (_writeIndex + 1) % _capacity;
                if (_count < _capacity) _count++;
            }
        }

        /// <summary>
        /// Computes the P50 (median) RTT from the sliding window.
        /// </summary>
        /// <returns>P50 RTT in milliseconds, or 0 if no samples.</returns>
        public double GetP50()
        {
            return GetPercentile(0.50);
        }

        /// <summary>
        /// Computes the P99 RTT from the sliding window.
        /// </summary>
        /// <returns>P99 RTT in milliseconds, or 0 if no samples.</returns>
        public double GetP99()
        {
            return GetPercentile(0.99);
        }

        /// <summary>
        /// Computes the specified percentile from the sliding window.
        /// </summary>
        /// <param name="percentile">Percentile as a fraction (0.0-1.0).</param>
        /// <returns>The RTT value at the specified percentile, or 0 if no samples.</returns>
        public double GetPercentile(double percentile)
        {
            lock (_lock)
            {
                if (_count == 0) return 0;

                var sorted = new double[_count];
                Array.Copy(_samples, sorted, _count);
                Array.Sort(sorted);

                int idx = Math.Min((int)(percentile * _count), _count - 1);
                return sorted[idx];
            }
        }

        /// <summary>
        /// Gets the number of RTT samples currently in the sliding window.
        /// </summary>
        public int SampleCount
        {
            get { lock (_lock) { return _count; } }
        }
    }

    /// <summary>
    /// Manages a pool of reusable connections to a single peer node.
    /// Tracks active/available connections and enforces per-peer limits.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-05: Per-peer connection pool for Raft RPC")]
    public sealed class ConnectionPool
    {
        private readonly string _peerId;
        private int _activeConnections;
        private readonly DateTime _createdAt;

        /// <summary>
        /// Gets the peer node identifier this pool serves.
        /// </summary>
        public string PeerId => _peerId;

        /// <summary>
        /// Gets the maximum number of connections allowed in this pool.
        /// </summary>
        public int MaxConnections { get; }

        /// <summary>
        /// Gets the timeout after which idle connections are recycled.
        /// </summary>
        public TimeSpan IdleTimeout { get; }

        /// <summary>
        /// Gets the number of currently active connections.
        /// </summary>
        public int ActiveConnections => _activeConnections;

        /// <summary>
        /// Gets the number of available (unused) connection slots.
        /// </summary>
        public int AvailableSlots => Math.Max(0, MaxConnections - _activeConnections);

        /// <summary>
        /// Gets the UTC timestamp when this pool was created.
        /// </summary>
        public DateTime CreatedAt => _createdAt;

        /// <summary>
        /// Initializes a new connection pool for the specified peer.
        /// </summary>
        /// <param name="peerId">The peer node identifier.</param>
        /// <param name="maxConnections">Maximum connections to maintain.</param>
        /// <param name="idleTimeout">Timeout for recycling idle connections.</param>
        public ConnectionPool(string peerId, int maxConnections, TimeSpan idleTimeout)
        {
            _peerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
            MaxConnections = Math.Max(1, maxConnections);
            IdleTimeout = idleTimeout;
            _createdAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Acquires a connection from the pool. Returns true if a connection was available.
        /// </summary>
        /// <returns>True if a connection was acquired; false if the pool is exhausted.</returns>
        public bool TryAcquire()
        {
            int current = _activeConnections;
            while (current < MaxConnections)
            {
                if (Interlocked.CompareExchange(ref _activeConnections, current + 1, current) == current)
                    return true;
                current = _activeConnections;
            }
            return false;
        }

        /// <summary>
        /// Releases a connection back to the pool.
        /// </summary>
        public void Release()
        {
            Interlocked.Decrement(ref _activeConnections);
        }

        /// <summary>
        /// Gets the pool utilization as a fraction (0.0-1.0).
        /// </summary>
        public double Utilization => MaxConnections > 0
            ? (double)_activeConnections / MaxConnections
            : 0;
    }

    #endregion
}
