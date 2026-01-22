using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Vector clock implementation for tracking causality in distributed systems.
    /// Each node maintains a counter, and the vector clock captures the happens-before relationship.
    /// </summary>
    /// <remarks>
    /// Vector clocks are used to determine:
    /// - Concurrent events (neither happened before the other)
    /// - Causal ordering (one event happened before another)
    /// - Conflict detection in distributed systems
    /// </remarks>
    public sealed class VectorClock : IEquatable<VectorClock>, IComparable<VectorClock>
    {
        private readonly ConcurrentDictionary<string, long> _clock;

        /// <summary>
        /// Creates a new empty vector clock.
        /// </summary>
        public VectorClock()
        {
            _clock = new ConcurrentDictionary<string, long>();
        }

        /// <summary>
        /// Creates a vector clock from an existing dictionary of node timestamps.
        /// </summary>
        /// <param name="clock">Dictionary mapping node IDs to their logical timestamps.</param>
        public VectorClock(IDictionary<string, long> clock)
        {
            _clock = new ConcurrentDictionary<string, long>(clock ?? throw new ArgumentNullException(nameof(clock)));
        }

        /// <summary>
        /// Gets the clock entries as a read-only dictionary.
        /// </summary>
        [JsonPropertyName("entries")]
        public IReadOnlyDictionary<string, long> Entries => _clock;

        /// <summary>
        /// Gets the timestamp for a specific node.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <returns>The logical timestamp for the node, or 0 if not present.</returns>
        public long this[string nodeId] => _clock.GetValueOrDefault(nodeId, 0);

        /// <summary>
        /// Gets the number of nodes in this vector clock.
        /// </summary>
        public int Count => _clock.Count;

        /// <summary>
        /// Increments the clock for the specified node and returns a new vector clock.
        /// </summary>
        /// <param name="nodeId">The node whose timestamp to increment.</param>
        /// <returns>A new vector clock with the incremented timestamp.</returns>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public VectorClock Increment(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            var newClock = new VectorClock(_clock);
            newClock._clock.AddOrUpdate(nodeId, 1, (_, current) => current + 1);
            return newClock;
        }

        /// <summary>
        /// Increments the clock for the specified node in place.
        /// </summary>
        /// <param name="nodeId">The node whose timestamp to increment.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public void IncrementInPlace(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _clock.AddOrUpdate(nodeId, 1, (_, current) => current + 1);
        }

        /// <summary>
        /// Sets the timestamp for a specific node.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        /// <param name="timestamp">The logical timestamp to set.</param>
        public void Set(string nodeId, long timestamp)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            if (timestamp < 0)
                throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp cannot be negative");

            _clock[nodeId] = timestamp;
        }

        /// <summary>
        /// Merges this vector clock with another, taking the maximum of each component.
        /// </summary>
        /// <param name="other">The other vector clock to merge with.</param>
        /// <returns>A new vector clock representing the merged state.</returns>
        /// <exception cref="ArgumentNullException">Thrown when other is null.</exception>
        public VectorClock Merge(VectorClock other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            var merged = new VectorClock(_clock);
            foreach (var (nodeId, timestamp) in other._clock)
            {
                merged._clock.AddOrUpdate(nodeId, timestamp, (_, current) => Math.Max(current, timestamp));
            }
            return merged;
        }

        /// <summary>
        /// Merges another vector clock into this one in place.
        /// </summary>
        /// <param name="other">The other vector clock to merge with.</param>
        /// <exception cref="ArgumentNullException">Thrown when other is null.</exception>
        public void MergeInPlace(VectorClock other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            foreach (var (nodeId, timestamp) in other._clock)
            {
                _clock.AddOrUpdate(nodeId, timestamp, (_, current) => Math.Max(current, timestamp));
            }
        }

        /// <summary>
        /// Computes the delta between this clock and another clock.
        /// Returns entries where this clock has higher values.
        /// </summary>
        /// <param name="other">The other vector clock to compare against.</param>
        /// <returns>A dictionary of entries that have changed.</returns>
        public Dictionary<string, long> GetDelta(VectorClock other)
        {
            if (other == null)
                return new Dictionary<string, long>(_clock);

            var delta = new Dictionary<string, long>();
            foreach (var (nodeId, timestamp) in _clock)
            {
                var otherTimestamp = other[nodeId];
                if (timestamp > otherTimestamp)
                {
                    delta[nodeId] = timestamp;
                }
            }
            return delta;
        }

        /// <summary>
        /// Determines the causal relationship between two vector clocks.
        /// </summary>
        /// <param name="other">The other vector clock to compare.</param>
        /// <returns>The causal relationship.</returns>
        public CausalRelation CompareCausality(VectorClock other)
        {
            if (other == null)
                return CausalRelation.HappenedAfter;

            var thisGreater = false;
            var otherGreater = false;
            var allNodes = _clock.Keys.Union(other._clock.Keys);

            foreach (var nodeId in allNodes)
            {
                var thisValue = this[nodeId];
                var otherValue = other[nodeId];

                if (thisValue > otherValue)
                    thisGreater = true;
                else if (otherValue > thisValue)
                    otherGreater = true;
            }

            if (thisGreater && otherGreater)
                return CausalRelation.Concurrent;
            if (thisGreater)
                return CausalRelation.HappenedAfter;
            if (otherGreater)
                return CausalRelation.HappenedBefore;
            return CausalRelation.Equal;
        }

        /// <summary>
        /// Determines if this vector clock happened before another.
        /// </summary>
        /// <param name="other">The other vector clock.</param>
        /// <returns>True if this clock happened before the other.</returns>
        public bool HappenedBefore(VectorClock other)
        {
            return CompareCausality(other) == CausalRelation.HappenedBefore;
        }

        /// <summary>
        /// Determines if this vector clock happened after another.
        /// </summary>
        /// <param name="other">The other vector clock.</param>
        /// <returns>True if this clock happened after the other.</returns>
        public bool HappenedAfter(VectorClock other)
        {
            return CompareCausality(other) == CausalRelation.HappenedAfter;
        }

        /// <summary>
        /// Determines if this vector clock is concurrent with another.
        /// </summary>
        /// <param name="other">The other vector clock.</param>
        /// <returns>True if the clocks are concurrent (neither happened before the other).</returns>
        public bool IsConcurrentWith(VectorClock other)
        {
            return CompareCausality(other) == CausalRelation.Concurrent;
        }

        /// <summary>
        /// Creates a deep copy of this vector clock.
        /// </summary>
        /// <returns>A new vector clock with the same values.</returns>
        public VectorClock Clone()
        {
            return new VectorClock(_clock);
        }

        /// <summary>
        /// Clears all entries from the vector clock.
        /// </summary>
        public void Clear()
        {
            _clock.Clear();
        }

        /// <summary>
        /// Gets the sum of all timestamps (useful for comparison heuristics).
        /// </summary>
        public long Sum => _clock.Values.Sum();

        /// <summary>
        /// Gets the maximum timestamp across all nodes.
        /// </summary>
        public long Max => _clock.Count > 0 ? _clock.Values.Max() : 0;

        /// <summary>
        /// Serializes the vector clock to JSON.
        /// </summary>
        /// <returns>JSON string representation.</returns>
        public string ToJson()
        {
            return JsonSerializer.Serialize(new { entries = _clock });
        }

        /// <summary>
        /// Deserializes a vector clock from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new vector clock instance.</returns>
        public static VectorClock FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new VectorClock();

            using var doc = JsonDocument.Parse(json);
            var entries = new Dictionary<string, long>();

            if (doc.RootElement.TryGetProperty("entries", out var entriesElement))
            {
                foreach (var prop in entriesElement.EnumerateObject())
                {
                    entries[prop.Name] = prop.Value.GetInt64();
                }
            }

            return new VectorClock(entries);
        }

        /// <summary>
        /// Serializes the vector clock to a byte array.
        /// </summary>
        /// <returns>Byte array representation.</returns>
        public byte[] ToBytes()
        {
            return JsonSerializer.SerializeToUtf8Bytes(new { entries = _clock });
        }

        /// <summary>
        /// Deserializes a vector clock from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new vector clock instance.</returns>
        public static VectorClock FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return new VectorClock();

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        #region Equality and Comparison

        /// <inheritdoc />
        public bool Equals(VectorClock? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;

            return CompareCausality(other) == CausalRelation.Equal;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is VectorClock other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var (nodeId, timestamp) in _clock.OrderBy(kv => kv.Key))
            {
                hash.Add(nodeId);
                hash.Add(timestamp);
            }
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public int CompareTo(VectorClock? other)
        {
            if (other is null)
                return 1;

            var relation = CompareCausality(other);
            return relation switch
            {
                CausalRelation.HappenedBefore => -1,
                CausalRelation.HappenedAfter => 1,
                CausalRelation.Equal => 0,
                CausalRelation.Concurrent => Sum.CompareTo(other.Sum), // Arbitrary but deterministic
                _ => 0
            };
        }

        /// <summary>
        /// Equality operator.
        /// </summary>
        public static bool operator ==(VectorClock? left, VectorClock? right)
        {
            if (left is null)
                return right is null;
            return left.Equals(right);
        }

        /// <summary>
        /// Inequality operator.
        /// </summary>
        public static bool operator !=(VectorClock? left, VectorClock? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Less than operator (based on happened-before).
        /// </summary>
        public static bool operator <(VectorClock? left, VectorClock? right)
        {
            if (left is null)
                return right is not null;
            return left.CompareTo(right) < 0;
        }

        /// <summary>
        /// Greater than operator (based on happened-after).
        /// </summary>
        public static bool operator >(VectorClock? left, VectorClock? right)
        {
            if (left is null)
                return false;
            return left.CompareTo(right) > 0;
        }

        /// <summary>
        /// Less than or equal operator.
        /// </summary>
        public static bool operator <=(VectorClock? left, VectorClock? right)
        {
            if (left is null)
                return true;
            return left.CompareTo(right) <= 0;
        }

        /// <summary>
        /// Greater than or equal operator.
        /// </summary>
        public static bool operator >=(VectorClock? left, VectorClock? right)
        {
            if (left is null)
                return right is null;
            return left.CompareTo(right) >= 0;
        }

        #endregion

        /// <inheritdoc />
        public override string ToString()
        {
            var entries = string.Join(", ", _clock.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
            return $"VectorClock({entries})";
        }
    }

    /// <summary>
    /// Represents the causal relationship between two vector clocks.
    /// </summary>
    public enum CausalRelation
    {
        /// <summary>
        /// The clocks are equal.
        /// </summary>
        Equal,

        /// <summary>
        /// This clock happened before the other.
        /// </summary>
        HappenedBefore,

        /// <summary>
        /// This clock happened after the other.
        /// </summary>
        HappenedAfter,

        /// <summary>
        /// The clocks are concurrent (neither happened before the other).
        /// </summary>
        Concurrent
    }

    /// <summary>
    /// Hybrid logical clock combining physical time with logical counters.
    /// Provides better timestamp resolution than pure vector clocks.
    /// </summary>
    public sealed class HybridLogicalClock
    {
        private readonly object _lock = new();
        private long _physicalTime;
        private int _logicalCounter;
        private readonly string _nodeId;

        /// <summary>
        /// Creates a new hybrid logical clock for the specified node.
        /// </summary>
        /// <param name="nodeId">The node identifier.</param>
        public HybridLogicalClock(string nodeId)
        {
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _physicalTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logicalCounter = 0;
        }

        /// <summary>
        /// Gets the current timestamp without advancing the clock.
        /// </summary>
        public HlcTimestamp Current
        {
            get
            {
                lock (_lock)
                {
                    return new HlcTimestamp(_physicalTime, _logicalCounter, _nodeId);
                }
            }
        }

        /// <summary>
        /// Generates a new timestamp for a local event.
        /// </summary>
        /// <returns>The new timestamp.</returns>
        public HlcTimestamp Now()
        {
            lock (_lock)
            {
                var wallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (wallTime > _physicalTime)
                {
                    _physicalTime = wallTime;
                    _logicalCounter = 0;
                }
                else
                {
                    _logicalCounter++;
                }

                return new HlcTimestamp(_physicalTime, _logicalCounter, _nodeId);
            }
        }

        /// <summary>
        /// Updates the clock based on a received timestamp and generates a new timestamp.
        /// </summary>
        /// <param name="received">The timestamp received from another node.</param>
        /// <returns>The new local timestamp.</returns>
        public HlcTimestamp Receive(HlcTimestamp received)
        {
            lock (_lock)
            {
                var wallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var maxPhysical = Math.Max(Math.Max(wallTime, _physicalTime), received.PhysicalTime);

                if (maxPhysical == _physicalTime && maxPhysical == received.PhysicalTime)
                {
                    _logicalCounter = Math.Max(_logicalCounter, received.LogicalCounter) + 1;
                }
                else if (maxPhysical == _physicalTime)
                {
                    _logicalCounter++;
                }
                else if (maxPhysical == received.PhysicalTime)
                {
                    _logicalCounter = received.LogicalCounter + 1;
                }
                else
                {
                    _logicalCounter = 0;
                }

                _physicalTime = maxPhysical;
                return new HlcTimestamp(_physicalTime, _logicalCounter, _nodeId);
            }
        }
    }

    /// <summary>
    /// Timestamp from a hybrid logical clock.
    /// </summary>
    public readonly struct HlcTimestamp : IComparable<HlcTimestamp>, IEquatable<HlcTimestamp>
    {
        /// <summary>
        /// Physical wall clock time in milliseconds since epoch.
        /// </summary>
        public long PhysicalTime { get; }

        /// <summary>
        /// Logical counter for events within the same physical time.
        /// </summary>
        public int LogicalCounter { get; }

        /// <summary>
        /// Node ID that generated this timestamp.
        /// </summary>
        public string NodeId { get; }

        /// <summary>
        /// Creates a new HLC timestamp.
        /// </summary>
        public HlcTimestamp(long physicalTime, int logicalCounter, string nodeId)
        {
            PhysicalTime = physicalTime;
            LogicalCounter = logicalCounter;
            NodeId = nodeId ?? "";
        }

        /// <inheritdoc />
        public int CompareTo(HlcTimestamp other)
        {
            var physicalCompare = PhysicalTime.CompareTo(other.PhysicalTime);
            if (physicalCompare != 0)
                return physicalCompare;

            var logicalCompare = LogicalCounter.CompareTo(other.LogicalCounter);
            if (logicalCompare != 0)
                return logicalCompare;

            return string.Compare(NodeId, other.NodeId, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public bool Equals(HlcTimestamp other)
        {
            return PhysicalTime == other.PhysicalTime &&
                   LogicalCounter == other.LogicalCounter &&
                   NodeId == other.NodeId;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is HlcTimestamp other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(PhysicalTime, LogicalCounter, NodeId);

        /// <inheritdoc />
        public override string ToString() => $"HLC({PhysicalTime}.{LogicalCounter}@{NodeId})";

        /// <summary>Equality operator.</summary>
        public static bool operator ==(HlcTimestamp left, HlcTimestamp right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(HlcTimestamp left, HlcTimestamp right) => !left.Equals(right);

        /// <summary>Less than operator.</summary>
        public static bool operator <(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) < 0;

        /// <summary>Greater than operator.</summary>
        public static bool operator >(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) > 0;

        /// <summary>Less than or equal operator.</summary>
        public static bool operator <=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) <= 0;

        /// <summary>Greater than or equal operator.</summary>
        public static bool operator >=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) >= 0;
    }
}
