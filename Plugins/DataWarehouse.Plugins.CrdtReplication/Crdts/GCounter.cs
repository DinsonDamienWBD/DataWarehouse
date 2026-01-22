using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Grow-only counter (G-Counter) CRDT.
    /// A distributed counter that only supports increment operations.
    /// Each node maintains its own counter, and the total is the sum of all counters.
    /// </summary>
    /// <remarks>
    /// Properties:
    /// - Supports increment only (no decrement)
    /// - Merge operation takes the max of each node's counter
    /// - Eventually converges to the same value on all replicas
    /// - Delta-state capable for efficient synchronization
    /// </remarks>
    public sealed class GCounter : ICrdt<GCounter>, IDeltaCrdt<GCounter>
    {
        private readonly ConcurrentDictionary<string, long> _counters;
        private readonly string _nodeId;

        /// <summary>
        /// Creates a new G-Counter for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public GCounter(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _counters = new ConcurrentDictionary<string, long>();
        }

        /// <summary>
        /// Creates a G-Counter with existing state.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="counters">Initial counter state.</param>
        public GCounter(string nodeId, IDictionary<string, long> counters) : this(nodeId)
        {
            if (counters != null)
            {
                foreach (var (node, count) in counters)
                {
                    _counters[node] = count;
                }
            }
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the current total value of the counter (sum of all nodes).
        /// </summary>
        public long Value => _counters.Values.Sum();

        /// <summary>
        /// Gets the local node's counter value.
        /// </summary>
        public long LocalValue => _counters.GetValueOrDefault(_nodeId, 0);

        /// <summary>
        /// Gets the counter values as a read-only dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, long> Counters => _counters;

        /// <summary>
        /// Increments the local counter by 1.
        /// </summary>
        /// <returns>A new G-Counter with the incremented value.</returns>
        public GCounter Increment()
        {
            return Increment(1);
        }

        /// <summary>
        /// Increments the local counter by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to increment by (must be positive).</param>
        /// <returns>A new G-Counter with the incremented value.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when amount is negative.</exception>
        public GCounter Increment(long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Increment amount must be non-negative");

            var newCounter = Clone();
            newCounter._counters.AddOrUpdate(_nodeId, amount, (_, current) => current + amount);
            return newCounter;
        }

        /// <summary>
        /// Increments the local counter in place by 1.
        /// </summary>
        public void IncrementInPlace()
        {
            IncrementInPlace(1);
        }

        /// <summary>
        /// Increments the local counter in place by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to increment by (must be positive).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when amount is negative.</exception>
        public void IncrementInPlace(long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Increment amount must be non-negative");

            _counters.AddOrUpdate(_nodeId, amount, (_, current) => current + amount);
        }

        /// <summary>
        /// Merges this counter with another, taking the maximum of each node's counter.
        /// </summary>
        /// <param name="other">The other counter to merge with.</param>
        /// <returns>A new G-Counter representing the merged state.</returns>
        public GCounter Merge(GCounter other)
        {
            if (other == null)
                return Clone();

            var merged = Clone();
            foreach (var (node, count) in other._counters)
            {
                merged._counters.AddOrUpdate(node, count, (_, current) => Math.Max(current, count));
            }
            return merged;
        }

        /// <summary>
        /// Merges another counter into this one in place.
        /// </summary>
        /// <param name="other">The other counter to merge with.</param>
        public void MergeInPlace(GCounter other)
        {
            if (other == null)
                return;

            foreach (var (node, count) in other._counters)
            {
                _counters.AddOrUpdate(node, count, (_, current) => Math.Max(current, count));
            }
        }

        /// <summary>
        /// Computes the delta state since the given previous state.
        /// </summary>
        /// <param name="previousState">The previous state to compare against.</param>
        /// <returns>A delta containing only the changes.</returns>
        public GCounter GetDelta(GCounter? previousState)
        {
            var delta = new GCounter(_nodeId);

            foreach (var (node, count) in _counters)
            {
                var previousCount = previousState?._counters.GetValueOrDefault(node, 0) ?? 0;
                if (count > previousCount)
                {
                    delta._counters[node] = count;
                }
            }

            return delta;
        }

        /// <summary>
        /// Applies a delta state to this counter.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        /// <returns>A new G-Counter with the delta applied.</returns>
        public GCounter ApplyDelta(GCounter delta)
        {
            return Merge(delta);
        }

        /// <summary>
        /// Applies a delta state to this counter in place.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        public void ApplyDeltaInPlace(GCounter delta)
        {
            MergeInPlace(delta);
        }

        /// <summary>
        /// Creates a deep copy of this counter.
        /// </summary>
        /// <returns>A new G-Counter with the same state.</returns>
        public GCounter Clone()
        {
            return new GCounter(_nodeId, _counters);
        }

        /// <summary>
        /// Resets the local counter to zero (does not affect other nodes).
        /// This breaks CRDT semantics and should only be used for testing.
        /// </summary>
        internal void Reset()
        {
            _counters.Clear();
        }

        /// <summary>
        /// Serializes the counter to JSON.
        /// </summary>
        /// <returns>JSON string representation.</returns>
        public string ToJson()
        {
            return JsonSerializer.Serialize(new
            {
                nodeId = _nodeId,
                counters = _counters
            });
        }

        /// <summary>
        /// Deserializes a G-Counter from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new G-Counter instance.</returns>
        public static GCounter FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";
            var counters = new Dictionary<string, long>();

            if (doc.RootElement.TryGetProperty("counters", out var countersElement))
            {
                foreach (var prop in countersElement.EnumerateObject())
                {
                    counters[prop.Name] = prop.Value.GetInt64();
                }
            }

            return new GCounter(nodeId, counters);
        }

        /// <summary>
        /// Serializes the counter to a byte array.
        /// </summary>
        /// <returns>Byte array representation.</returns>
        public byte[] ToBytes()
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                nodeId = _nodeId,
                counters = _counters
            });
        }

        /// <summary>
        /// Deserializes a G-Counter from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new G-Counter instance.</returns>
        public static GCounter FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Bytes cannot be null or empty", nameof(bytes));

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"GCounter({Value}, nodes={_counters.Count})";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is GCounter other && Equals(other);
        }

        /// <summary>
        /// Determines whether this counter equals another.
        /// </summary>
        /// <param name="other">The other counter to compare.</param>
        /// <returns>True if the counters have the same state.</returns>
        public bool Equals(GCounter? other)
        {
            if (other is null)
                return false;

            if (_counters.Count != other._counters.Count)
                return false;

            foreach (var (node, count) in _counters)
            {
                if (!other._counters.TryGetValue(node, out var otherCount) || count != otherCount)
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var (node, count) in _counters.OrderBy(kv => kv.Key))
            {
                hash.Add(node);
                hash.Add(count);
            }
            return hash.ToHashCode();
        }
    }
}
