using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Replication
{
    /// <summary>
    /// Membership-aware Dotted Version Vector (DVV) for causality tracking in dynamic clusters.
    /// Extends traditional vector clocks with cluster membership awareness, enabling automatic
    /// pruning of dead node entries to prevent unbounded vector growth.
    ///
    /// <para><strong>Causality Semantics:</strong></para>
    /// <list type="bullet">
    ///   <item><see cref="HappensBefore"/>: Returns true if this DVV causally precedes another
    ///     (all entries &lt;= other, at least one strictly &lt;).</item>
    ///   <item><see cref="IsConcurrent"/>: Returns true if neither DVV happens-before the other
    ///     (true concurrency requiring conflict resolution).</item>
    ///   <item><see cref="Merge"/>: Point-wise maximum of all entries from both DVVs.</item>
    /// </list>
    ///
    /// <para><strong>Pruning Behavior:</strong></para>
    /// When a node leaves the cluster, its entries are automatically removed from the vector
    /// via <see cref="PruneDeadNodes"/>. This is triggered by membership change callbacks
    /// registered during construction. Manual pruning is also available.
    ///
    /// <para><strong>Thread Safety:</strong></para>
    /// All operations are thread-safe. The internal vector uses <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// The <see cref="Increment"/> method uses interlocked operations for atomic version bumps.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 41.1-06: KS7 DVV with membership-aware pruning")]
    public sealed class DottedVersionVector
    {
        private readonly ConcurrentDictionary<string, (long Version, string Dot)> _vector = new();
        private readonly IReplicationClusterMembership? _membership;
        private long _dotCounter;

        /// <summary>
        /// Creates a new DVV with cluster membership awareness.
        /// Registers for node removal callbacks to auto-prune dead node entries.
        /// </summary>
        /// <param name="membership">
        /// Cluster membership provider for pruning decisions.
        /// If null, pruning must be triggered manually via <see cref="PruneDeadNodes"/>.
        /// </param>
        public DottedVersionVector(IReplicationClusterMembership? membership = null)
        {
            _membership = membership;
            _membership?.RegisterNodeRemoved(_ => PruneDeadNodes());
        }

        /// <summary>
        /// Private constructor for deserialization / merge results.
        /// </summary>
        private DottedVersionVector(
            IReadOnlyDictionary<string, (long Version, string Dot)> entries,
            IReplicationClusterMembership? membership)
        {
            _membership = membership;
            foreach (var (key, value) in entries)
            {
                _vector[key] = value;
            }
            _membership?.RegisterNodeRemoved(_ => PruneDeadNodes());
        }

        /// <summary>
        /// Increments the version for the specified node and updates its dot identifier.
        /// The dot uniquely identifies this specific update event.
        /// </summary>
        /// <param name="nodeId">The node identifier performing the update.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="nodeId"/> is null.</exception>
        public void Increment(string nodeId)
        {
            ArgumentNullException.ThrowIfNull(nodeId);

            var dotSeq = Interlocked.Increment(ref _dotCounter);
            _vector.AddOrUpdate(
                nodeId,
                _ => (1L, $"{nodeId}:{dotSeq}"),
                (_, existing) => (existing.Version + 1, $"{nodeId}:{dotSeq}"));
        }

        /// <summary>
        /// Determines if this DVV causally happens-before another.
        /// Returns true if all entries in this DVV are less than or equal to
        /// the corresponding entries in <paramref name="other"/>, with at least
        /// one entry strictly less.
        /// </summary>
        /// <param name="other">The DVV to compare against.</param>
        /// <returns>True if this DVV happens-before <paramref name="other"/>.</returns>
        public bool HappensBefore(DottedVersionVector other)
        {
            if (other is null) return false;
            if (_vector.IsEmpty) return !other._vector.IsEmpty;

            bool atLeastOneLess = false;

            foreach (var (nodeId, (version, _)) in _vector)
            {
                var otherVersion = other.GetVersion(nodeId);
                if (version > otherVersion) return false;
                if (version < otherVersion) atLeastOneLess = true;
            }

            // Check if other has entries we don't have
            if (!atLeastOneLess)
            {
                foreach (var (nodeId, _) in other._vector)
                {
                    if (!_vector.ContainsKey(nodeId))
                    {
                        atLeastOneLess = true;
                        break;
                    }
                }
            }

            return atLeastOneLess;
        }

        /// <summary>
        /// Merges this DVV with another by taking the maximum version for each node.
        /// The resulting DVV represents the combined causal history of both inputs.
        /// </summary>
        /// <param name="other">The DVV to merge with.</param>
        /// <returns>A new DVV containing the merged causal history.</returns>
        public DottedVersionVector Merge(DottedVersionVector other)
        {
            if (other is null) return this;

            var merged = new Dictionary<string, (long Version, string Dot)>();

            // Add all entries from this vector
            foreach (var (nodeId, entry) in _vector)
            {
                merged[nodeId] = entry;
            }

            // Merge with other vector (take max)
            foreach (var (nodeId, (otherVersion, otherDot)) in other._vector)
            {
                if (merged.TryGetValue(nodeId, out var existing))
                {
                    if (otherVersion > existing.Version)
                    {
                        merged[nodeId] = (otherVersion, otherDot);
                    }
                }
                else
                {
                    merged[nodeId] = (otherVersion, otherDot);
                }
            }

            return new DottedVersionVector(merged, _membership);
        }

        /// <summary>
        /// Determines if this DVV is concurrent with another (neither happens-before the other).
        /// Concurrent DVVs indicate true conflicts that require resolution (CRDT merge, LWW, etc.).
        /// </summary>
        /// <param name="other">The DVV to compare against.</param>
        /// <returns>True if the DVVs are concurrent (conflicting).</returns>
        public bool IsConcurrent(DottedVersionVector other)
        {
            if (other is null) return false;
            return !HappensBefore(other) && !other.HappensBefore(this)
                   && !IsEqual(other);
        }

        /// <summary>
        /// Removes entries for nodes that are no longer active in the cluster.
        /// Called automatically when membership change callbacks fire, or manually.
        /// No-op if no membership provider was configured.
        /// </summary>
        public void PruneDeadNodes()
        {
            if (_membership is null) return;

            var activeNodes = _membership.GetActiveNodes();
            var deadKeys = _vector.Keys.Where(k => !activeNodes.Contains(k)).ToList();

            foreach (var key in deadKeys)
            {
                _vector.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Converts this DVV to an immutable dictionary for serialization or transport.
        /// </summary>
        /// <returns>An immutable snapshot of the vector entries.</returns>
        public ImmutableDictionary<string, (long Version, string Dot)> ToImmutableDictionary()
        {
            return _vector.ToImmutableDictionary();
        }

        /// <summary>
        /// Reconstructs a DVV from a serialized immutable dictionary.
        /// </summary>
        /// <param name="entries">The serialized entries.</param>
        /// <param name="membership">Optional cluster membership provider for pruning.</param>
        /// <returns>A new DVV initialized with the provided entries.</returns>
        public static DottedVersionVector FromDictionary(
            ImmutableDictionary<string, (long Version, string Dot)> entries,
            IReplicationClusterMembership? membership = null)
        {
            ArgumentNullException.ThrowIfNull(entries);
            return new DottedVersionVector(entries, membership);
        }

        /// <summary>
        /// Gets the version number for a specific node, or 0 if absent.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>The version number, or 0.</returns>
        public long GetVersion(string nodeId)
        {
            return _vector.TryGetValue(nodeId, out var entry) ? entry.Version : 0L;
        }

        /// <summary>
        /// Gets the number of entries in this DVV.
        /// </summary>
        public int Count => _vector.Count;

        private bool IsEqual(DottedVersionVector other)
        {
            if (_vector.Count != other._vector.Count) return false;

            foreach (var (nodeId, (version, _)) in _vector)
            {
                if (other.GetVersion(nodeId) != version) return false;
            }

            return true;
        }
    }
}
