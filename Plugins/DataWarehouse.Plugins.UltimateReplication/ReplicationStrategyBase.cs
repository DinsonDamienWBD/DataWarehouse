using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;

namespace DataWarehouse.Plugins.UltimateReplication
{
    /// <summary>
    /// Describes the characteristics and capabilities of a replication strategy.
    /// </summary>
    public sealed record ReplicationCharacteristics
    {
        /// <summary>
        /// Gets the name of the replication strategy.
        /// </summary>
        public required string StrategyName { get; init; }

        /// <summary>
        /// Gets the description of the strategy.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Gets the consistency model provided by this strategy.
        /// </summary>
        public required ConsistencyModel ConsistencyModel { get; init; }

        /// <summary>
        /// Gets the capabilities of this strategy.
        /// </summary>
        public required ReplicationCapabilities Capabilities { get; init; }

        /// <summary>
        /// Indicates if the strategy supports automatic conflict resolution.
        /// </summary>
        public bool SupportsAutoConflictResolution { get; init; }

        /// <summary>
        /// Indicates if the strategy supports vector clocks.
        /// </summary>
        public bool SupportsVectorClocks { get; init; }

        /// <summary>
        /// Indicates if the strategy supports delta synchronization.
        /// </summary>
        public bool SupportsDeltaSync { get; init; }

        /// <summary>
        /// Indicates if the strategy supports streaming replication.
        /// </summary>
        public bool SupportsStreaming { get; init; }

        /// <summary>
        /// Gets the typical replication lag in milliseconds.
        /// </summary>
        public long TypicalLagMs { get; init; }

        /// <summary>
        /// Gets the target consistency SLA in milliseconds.
        /// </summary>
        public long ConsistencySlaMs { get; init; }
    }

    /// <summary>
    /// Enhanced vector clock implementation for tracking causal ordering across distributed nodes.
    /// </summary>
    public sealed class EnhancedVectorClock
    {
        private readonly ConcurrentDictionary<string, long> _clock = new();
        private readonly object _lock = new();

        /// <summary>
        /// Gets the clock entries.
        /// </summary>
        public IReadOnlyDictionary<string, long> Entries => _clock;

        /// <summary>
        /// Creates an empty vector clock.
        /// </summary>
        public EnhancedVectorClock() { }

        /// <summary>
        /// Creates a vector clock from existing entries.
        /// </summary>
        public EnhancedVectorClock(IReadOnlyDictionary<string, long> entries)
        {
            foreach (var (key, value) in entries)
                _clock[key] = value;
        }

        /// <summary>
        /// Gets the clock value for a specific node.
        /// </summary>
        public long this[string nodeId] => _clock.GetValueOrDefault(nodeId, 0);

        /// <summary>
        /// Increments the clock for a node.
        /// </summary>
        public void Increment(string nodeId)
        {
            lock (_lock)
            {
                _clock.AddOrUpdate(nodeId, 1, (_, v) => v + 1);
            }
        }

        /// <summary>
        /// Increments in place and returns self.
        /// </summary>
        public EnhancedVectorClock IncrementInPlace(string nodeId)
        {
            Increment(nodeId);
            return this;
        }

        /// <summary>
        /// Merges with another vector clock, taking maximum values.
        /// </summary>
        public void Merge(EnhancedVectorClock other)
        {
            lock (_lock)
            {
                foreach (var (nodeId, value) in other._clock)
                {
                    _clock.AddOrUpdate(nodeId, value, (_, existing) => Math.Max(existing, value));
                }
            }
        }

        /// <summary>
        /// Checks if this clock happens before another.
        /// </summary>
        public bool HappensBefore(EnhancedVectorClock other)
        {
            bool anyLess = false;
            var allNodes = _clock.Keys.Union(other._clock.Keys);

            foreach (var node in allNodes)
            {
                var thisValue = this[node];
                var otherValue = other[node];

                if (thisValue > otherValue)
                    return false;
                if (thisValue < otherValue)
                    anyLess = true;
            }

            return anyLess;
        }

        /// <summary>
        /// Checks if clocks are concurrent (neither happens-before the other).
        /// </summary>
        public bool IsConcurrentWith(EnhancedVectorClock other)
        {
            return !HappensBefore(other) && !other.HappensBefore(this);
        }

        /// <summary>
        /// Creates a deep copy.
        /// </summary>
        public EnhancedVectorClock Clone()
        {
            return new EnhancedVectorClock(_clock);
        }

        /// <summary>
        /// Serializes to JSON.
        /// </summary>
        public string ToJson() => JsonSerializer.Serialize(_clock);

        /// <summary>
        /// Deserializes from JSON.
        /// </summary>
        public static EnhancedVectorClock FromJson(string json)
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? new();
            return new EnhancedVectorClock(entries);
        }

        /// <summary>
        /// Returns string representation.
        /// </summary>
        public override string ToString()
        {
            return "{" + string.Join(", ", _clock.Select(kv => $"{kv.Key}:{kv.Value}")) + "}";
        }
    }

    /// <summary>
    /// Represents a detected replication conflict.
    /// </summary>
    public sealed class EnhancedReplicationConflict
    {
        /// <summary>
        /// The data identifier.
        /// </summary>
        public required string DataId { get; init; }

        /// <summary>
        /// Local version information.
        /// </summary>
        public required EnhancedVectorClock LocalVersion { get; init; }

        /// <summary>
        /// Remote version information.
        /// </summary>
        public required EnhancedVectorClock RemoteVersion { get; init; }

        /// <summary>
        /// Local data payload.
        /// </summary>
        public required ReadOnlyMemory<byte> LocalData { get; init; }

        /// <summary>
        /// Remote data payload.
        /// </summary>
        public required ReadOnlyMemory<byte> RemoteData { get; init; }

        /// <summary>
        /// Local node identifier.
        /// </summary>
        public required string LocalNodeId { get; init; }

        /// <summary>
        /// Remote node identifier.
        /// </summary>
        public required string RemoteNodeId { get; init; }

        /// <summary>
        /// When the conflict was detected.
        /// </summary>
        public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Local metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string>? LocalMetadata { get; init; }

        /// <summary>
        /// Remote metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string>? RemoteMetadata { get; init; }
    }

    /// <summary>
    /// Tracks replication lag between nodes.
    /// </summary>
    public sealed class ReplicationLagTracker
    {
        private readonly ConcurrentDictionary<string, LagEntry> _lagEntries = new();

        private sealed class LagEntry
        {
            public TimeSpan CurrentLag { get; set; }
            public TimeSpan MaxLag { get; set; }
            public TimeSpan MinLag { get; set; } = TimeSpan.MaxValue;
            public TimeSpan AverageLag { get; set; }
            public int SampleCount { get; set; }
            public DateTimeOffset LastUpdated { get; set; }
        }

        /// <summary>
        /// Records a lag measurement for a node.
        /// </summary>
        public void RecordLag(string nodeId, TimeSpan lag)
        {
            _lagEntries.AddOrUpdate(nodeId,
                _ => new LagEntry
                {
                    CurrentLag = lag,
                    MaxLag = lag,
                    MinLag = lag,
                    AverageLag = lag,
                    SampleCount = 1,
                    LastUpdated = DateTimeOffset.UtcNow
                },
                (_, entry) =>
                {
                    entry.CurrentLag = lag;
                    entry.MaxLag = lag > entry.MaxLag ? lag : entry.MaxLag;
                    entry.MinLag = lag < entry.MinLag ? lag : entry.MinLag;
                    entry.AverageLag = TimeSpan.FromMilliseconds(
                        (entry.AverageLag.TotalMilliseconds * entry.SampleCount + lag.TotalMilliseconds) / (entry.SampleCount + 1));
                    entry.SampleCount++;
                    entry.LastUpdated = DateTimeOffset.UtcNow;
                    return entry;
                });
        }

        /// <summary>
        /// Gets the current lag for a node.
        /// </summary>
        public TimeSpan GetCurrentLag(string nodeId)
        {
            return _lagEntries.TryGetValue(nodeId, out var entry) ? entry.CurrentLag : TimeSpan.Zero;
        }

        /// <summary>
        /// Gets lag statistics for a node.
        /// </summary>
        public (TimeSpan Current, TimeSpan Max, TimeSpan Min, TimeSpan Avg) GetLagStats(string nodeId)
        {
            if (_lagEntries.TryGetValue(nodeId, out var entry))
                return (entry.CurrentLag, entry.MaxLag, entry.MinLag, entry.AverageLag);
            return (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        }

        /// <summary>
        /// Gets all nodes exceeding a lag threshold.
        /// </summary>
        public IEnumerable<string> GetNodesExceedingThreshold(TimeSpan threshold)
        {
            return _lagEntries
                .Where(kv => kv.Value.CurrentLag > threshold)
                .Select(kv => kv.Key);
        }
    }

    /// <summary>
    /// Anti-entropy protocol for ensuring eventual consistency.
    /// </summary>
    public sealed class AntiEntropyProtocol
    {
        private readonly ConcurrentDictionary<string, EnhancedVectorClock> _nodeVersions = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSyncTimes = new();
        private readonly TimeSpan _syncInterval;
        private readonly Random _random = new();

        /// <summary>
        /// Creates a new anti-entropy protocol instance.
        /// </summary>
        public AntiEntropyProtocol(TimeSpan? syncInterval = null)
        {
            _syncInterval = syncInterval ?? TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Updates the known version for a node.
        /// </summary>
        public void UpdateNodeVersion(string nodeId, EnhancedVectorClock version)
        {
            _nodeVersions[nodeId] = version.Clone();
            _lastSyncTimes[nodeId] = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Gets nodes that need synchronization based on version drift.
        /// </summary>
        public IEnumerable<string> GetNodesNeedingSync(EnhancedVectorClock localVersion)
        {
            foreach (var (nodeId, remoteVersion) in _nodeVersions)
            {
                if (localVersion.IsConcurrentWith(remoteVersion) || remoteVersion.HappensBefore(localVersion))
                {
                    var lastSync = _lastSyncTimes.GetValueOrDefault(nodeId, DateTimeOffset.MinValue);
                    if (DateTimeOffset.UtcNow - lastSync > _syncInterval)
                        yield return nodeId;
                }
            }
        }

        /// <summary>
        /// Selects random nodes for gossip (push-pull anti-entropy).
        /// </summary>
        public IEnumerable<string> SelectGossipTargets(int count)
        {
            var nodes = _nodeVersions.Keys.ToList();
            return nodes.OrderBy(_ => _random.Next()).Take(Math.Min(count, nodes.Count));
        }

        /// <summary>
        /// Computes the Merkle tree hash for detecting data divergence.
        /// </summary>
        public string ComputeMerkleRoot(IEnumerable<(string Key, byte[] Data)> items)
        {
            var hashes = items
                .OrderBy(x => x.Key)
                .Select(x => ComputeHash(x.Key + Convert.ToBase64String(x.Data)))
                .ToList();

            while (hashes.Count > 1)
            {
                var newHashes = new List<string>();
                for (int i = 0; i < hashes.Count; i += 2)
                {
                    var left = hashes[i];
                    var right = i + 1 < hashes.Count ? hashes[i + 1] : left;
                    newHashes.Add(ComputeHash(left + right));
                }
                hashes = newHashes;
            }

            return hashes.FirstOrDefault() ?? string.Empty;
        }

        private static string ComputeHash(string input)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }
    }

    /// <summary>
    /// Enhanced base class for replication strategies with vector clock management,
    /// conflict detection/resolution, replication lag tracking, and anti-entropy protocols.
    /// </summary>
    public abstract class EnhancedReplicationStrategyBase : ReplicationStrategyBase
    {
        /// <summary>
        /// Vector clock for this node.
        /// </summary>
        protected EnhancedVectorClock VectorClock { get; } = new();

        /// <summary>
        /// Replication lag tracker.
        /// </summary>
        protected ReplicationLagTracker LagTracker { get; } = new();

        /// <summary>
        /// Anti-entropy protocol instance.
        /// </summary>
        protected AntiEntropyProtocol AntiEntropy { get; }

        /// <summary>
        /// Local node identifier.
        /// </summary>
        protected string LocalNodeId { get; }

        /// <summary>
        /// Conflict resolution method to use.
        /// </summary>
        protected ConflictResolutionMethod ConflictResolution { get; set; } = ConflictResolutionMethod.LastWriteWins;

        /// <summary>
        /// Gets the characteristics of this replication strategy.
        /// </summary>
        public abstract ReplicationCharacteristics Characteristics { get; }

        /// <summary>
        /// Creates a new enhanced replication strategy.
        /// </summary>
        protected EnhancedReplicationStrategyBase(string? nodeId = null, TimeSpan? antiEntropyInterval = null)
        {
            LocalNodeId = nodeId ?? $"node-{Guid.NewGuid():N}"[..16];
            AntiEntropy = new AntiEntropyProtocol(antiEntropyInterval);
            VectorClock.Increment(LocalNodeId);
        }

        /// <summary>
        /// Increments local vector clock for a write operation.
        /// </summary>
        protected void IncrementLocalClock()
        {
            VectorClock.Increment(LocalNodeId);
        }

        /// <summary>
        /// Merges a remote vector clock with local clock.
        /// </summary>
        protected void MergeRemoteClock(EnhancedVectorClock remoteClock)
        {
            VectorClock.Merge(remoteClock);
            VectorClock.Increment(LocalNodeId);
        }

        /// <summary>
        /// Detects conflict using enhanced vector clock comparison.
        /// </summary>
        public virtual EnhancedReplicationConflict? DetectConflictEnhanced(
            EnhancedVectorClock localVersion,
            EnhancedVectorClock remoteVersion,
            ReadOnlyMemory<byte> localData,
            ReadOnlyMemory<byte> remoteData,
            string remoteNodeId)
        {
            if (!localVersion.IsConcurrentWith(remoteVersion))
                return null;

            if (localData.Span.SequenceEqual(remoteData.Span))
                return null;

            return new EnhancedReplicationConflict
            {
                DataId = Guid.NewGuid().ToString(),
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                LocalData = localData,
                RemoteData = remoteData,
                LocalNodeId = LocalNodeId,
                RemoteNodeId = remoteNodeId
            };
        }

        /// <summary>
        /// Resolves a conflict using the configured resolution method.
        /// </summary>
        public virtual async Task<(ReadOnlyMemory<byte> ResolvedData, EnhancedVectorClock ResolvedVersion)> ResolveConflictEnhancedAsync(
            EnhancedReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);
            mergedClock.Increment(LocalNodeId);

            return ConflictResolution switch
            {
                ConflictResolutionMethod.LastWriteWins => ResolveByTimestamp(conflict, mergedClock),
                ConflictResolutionMethod.Crdt => await ResolveByCrdtAsync(conflict, mergedClock, cancellationToken),
                ConflictResolutionMethod.Merge => await ResolveBytMergeAsync(conflict, mergedClock, cancellationToken),
                _ => (conflict.LocalData, mergedClock)
            };
        }

        private (ReadOnlyMemory<byte>, EnhancedVectorClock) ResolveByTimestamp(
            EnhancedReplicationConflict conflict,
            EnhancedVectorClock mergedClock)
        {
            var localTs = conflict.LocalMetadata?.TryGetValue("timestamp", out var lts) == true
                ? DateTimeOffset.Parse(lts)
                : DateTimeOffset.MinValue;
            var remoteTs = conflict.RemoteMetadata?.TryGetValue("timestamp", out var rts) == true
                ? DateTimeOffset.Parse(rts)
                : DateTimeOffset.MinValue;

            return remoteTs > localTs
                ? (conflict.RemoteData, mergedClock)
                : (conflict.LocalData, mergedClock);
        }

        /// <summary>
        /// Override to implement CRDT-based conflict resolution.
        /// </summary>
        protected virtual Task<(ReadOnlyMemory<byte>, EnhancedVectorClock)> ResolveByCrdtAsync(
            EnhancedReplicationConflict conflict,
            EnhancedVectorClock mergedClock,
            CancellationToken ct)
        {
            // Default: keep local data, subclasses override with actual CRDT logic
            return Task.FromResult<(ReadOnlyMemory<byte>, EnhancedVectorClock)>((conflict.LocalData, mergedClock));
        }

        /// <summary>
        /// Override to implement merge-based conflict resolution.
        /// </summary>
        protected virtual Task<(ReadOnlyMemory<byte>, EnhancedVectorClock)> ResolveBytMergeAsync(
            EnhancedReplicationConflict conflict,
            EnhancedVectorClock mergedClock,
            CancellationToken ct)
        {
            // Default: concatenate data, subclasses override with actual merge logic
            var merged = new byte[conflict.LocalData.Length + conflict.RemoteData.Length];
            conflict.LocalData.CopyTo(merged);
            conflict.RemoteData.CopyTo(merged.AsMemory(conflict.LocalData.Length));
            return Task.FromResult<(ReadOnlyMemory<byte>, EnhancedVectorClock)>((merged, mergedClock));
        }

        /// <summary>
        /// Records replication lag to a target node.
        /// </summary>
        protected void RecordReplicationLag(string targetNodeId, TimeSpan lag)
        {
            LagTracker.RecordLag(targetNodeId, lag);
        }

        /// <summary>
        /// Gets nodes needing anti-entropy synchronization.
        /// </summary>
        protected IEnumerable<string> GetNodesNeedingAntiEntropy()
        {
            return AntiEntropy.GetNodesNeedingSync(VectorClock);
        }

        /// <summary>
        /// Updates the known version for a remote node.
        /// </summary>
        protected void UpdateRemoteNodeVersion(string nodeId, EnhancedVectorClock version)
        {
            AntiEntropy.UpdateNodeVersion(nodeId, version);
        }
    }
}
