using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Positive-Negative Counter (PN-Counter) CRDT.
    /// A distributed counter that supports both increment and decrement operations.
    /// Implemented as two G-Counters: one for increments and one for decrements.
    /// </summary>
    /// <remarks>
    /// Properties:
    /// - Supports increment and decrement operations
    /// - Value = sum of positive counter - sum of negative counter
    /// - Merge operation merges both underlying G-Counters
    /// - Eventually converges to the same value on all replicas
    /// - Delta-state capable for efficient synchronization
    /// </remarks>
    public sealed class PNCounter : ICrdt<PNCounter>, IDeltaCrdt<PNCounter>
    {
        private readonly GCounter _positive;
        private readonly GCounter _negative;
        private readonly string _nodeId;

        /// <summary>
        /// Creates a new PN-Counter for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public PNCounter(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _positive = new GCounter(nodeId);
            _negative = new GCounter(nodeId);
        }

        /// <summary>
        /// Creates a PN-Counter with existing positive and negative counters.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="positive">The positive G-Counter.</param>
        /// <param name="negative">The negative G-Counter.</param>
        private PNCounter(string nodeId, GCounter positive, GCounter negative)
        {
            _nodeId = nodeId;
            _positive = positive ?? throw new ArgumentNullException(nameof(positive));
            _negative = negative ?? throw new ArgumentNullException(nameof(negative));
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the current value of the counter (positive - negative).
        /// </summary>
        public long Value => _positive.Value - _negative.Value;

        /// <summary>
        /// Gets the total positive value (sum of all increments).
        /// </summary>
        public long PositiveValue => _positive.Value;

        /// <summary>
        /// Gets the total negative value (sum of all decrements).
        /// </summary>
        public long NegativeValue => _negative.Value;

        /// <summary>
        /// Gets the positive counter (increments).
        /// </summary>
        public GCounter Positive => _positive;

        /// <summary>
        /// Gets the negative counter (decrements).
        /// </summary>
        public GCounter Negative => _negative;

        /// <summary>
        /// Increments the counter by 1.
        /// </summary>
        /// <returns>A new PN-Counter with the incremented value.</returns>
        public PNCounter Increment()
        {
            return Increment(1);
        }

        /// <summary>
        /// Increments the counter by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to increment by (must be positive).</param>
        /// <returns>A new PN-Counter with the incremented value.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when amount is negative.</exception>
        public PNCounter Increment(long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Increment amount must be non-negative");

            return new PNCounter(_nodeId, _positive.Increment(amount), _negative.Clone());
        }

        /// <summary>
        /// Increments the counter in place by 1.
        /// </summary>
        public void IncrementInPlace()
        {
            IncrementInPlace(1);
        }

        /// <summary>
        /// Increments the counter in place by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to increment by (must be positive).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when amount is negative.</exception>
        public void IncrementInPlace(long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Increment amount must be non-negative");

            _positive.IncrementInPlace(amount);
        }

        /// <summary>
        /// Decrements the counter by 1.
        /// </summary>
        /// <returns>A new PN-Counter with the decremented value.</returns>
        public PNCounter Decrement()
        {
            return Decrement(1);
        }

        /// <summary>
        /// Decrements the counter by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to decrement by (must be positive).</param>
        /// <returns>A new PN-Counter with the decremented value.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when amount is negative.</exception>
        public PNCounter Decrement(long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Decrement amount must be non-negative");

            return new PNCounter(_nodeId, _positive.Clone(), _negative.Increment(amount));
        }

        /// <summary>
        /// Decrements the counter in place by 1.
        /// </summary>
        public void DecrementInPlace()
        {
            DecrementInPlace(1);
        }

        /// <summary>
        /// Decrements the counter in place by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to decrement by (must be positive).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when amount is negative.</exception>
        public void DecrementInPlace(long amount)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "Decrement amount must be non-negative");

            _negative.IncrementInPlace(amount);
        }

        /// <summary>
        /// Adds a value to the counter (positive for increment, negative for decrement).
        /// </summary>
        /// <param name="value">The value to add.</param>
        /// <returns>A new PN-Counter with the updated value.</returns>
        public PNCounter Add(long value)
        {
            return value >= 0 ? Increment(value) : Decrement(-value);
        }

        /// <summary>
        /// Adds a value to the counter in place (positive for increment, negative for decrement).
        /// </summary>
        /// <param name="value">The value to add.</param>
        public void AddInPlace(long value)
        {
            if (value >= 0)
                IncrementInPlace(value);
            else
                DecrementInPlace(-value);
        }

        /// <summary>
        /// Merges this counter with another.
        /// </summary>
        /// <param name="other">The other counter to merge with.</param>
        /// <returns>A new PN-Counter representing the merged state.</returns>
        public PNCounter Merge(PNCounter other)
        {
            if (other == null)
                return Clone();

            return new PNCounter(
                _nodeId,
                _positive.Merge(other._positive),
                _negative.Merge(other._negative)
            );
        }

        /// <summary>
        /// Merges another counter into this one in place.
        /// </summary>
        /// <param name="other">The other counter to merge with.</param>
        public void MergeInPlace(PNCounter other)
        {
            if (other == null)
                return;

            _positive.MergeInPlace(other._positive);
            _negative.MergeInPlace(other._negative);
        }

        /// <summary>
        /// Computes the delta state since the given previous state.
        /// </summary>
        /// <param name="previousState">The previous state to compare against.</param>
        /// <returns>A delta containing only the changes.</returns>
        public PNCounter GetDelta(PNCounter? previousState)
        {
            var positiveDelta = _positive.GetDelta(previousState?._positive);
            var negativeDelta = _negative.GetDelta(previousState?._negative);
            return new PNCounter(_nodeId, positiveDelta, negativeDelta);
        }

        /// <summary>
        /// Applies a delta state to this counter.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        /// <returns>A new PN-Counter with the delta applied.</returns>
        public PNCounter ApplyDelta(PNCounter delta)
        {
            return Merge(delta);
        }

        /// <summary>
        /// Applies a delta state to this counter in place.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        public void ApplyDeltaInPlace(PNCounter delta)
        {
            MergeInPlace(delta);
        }

        /// <summary>
        /// Creates a deep copy of this counter.
        /// </summary>
        /// <returns>A new PN-Counter with the same state.</returns>
        public PNCounter Clone()
        {
            return new PNCounter(_nodeId, _positive.Clone(), _negative.Clone());
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
                positive = _positive.Counters,
                negative = _negative.Counters
            });
        }

        /// <summary>
        /// Deserializes a PN-Counter from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new PN-Counter instance.</returns>
        public static PNCounter FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";

            var positiveDict = new Dictionary<string, long>();
            var negativeDict = new Dictionary<string, long>();

            if (doc.RootElement.TryGetProperty("positive", out var positiveElement))
            {
                foreach (var prop in positiveElement.EnumerateObject())
                {
                    positiveDict[prop.Name] = prop.Value.GetInt64();
                }
            }

            if (doc.RootElement.TryGetProperty("negative", out var negativeElement))
            {
                foreach (var prop in negativeElement.EnumerateObject())
                {
                    negativeDict[prop.Name] = prop.Value.GetInt64();
                }
            }

            return new PNCounter(
                nodeId,
                new GCounter(nodeId, positiveDict),
                new GCounter(nodeId, negativeDict)
            );
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
                positive = _positive.Counters,
                negative = _negative.Counters
            });
        }

        /// <summary>
        /// Deserializes a PN-Counter from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new PN-Counter instance.</returns>
        public static PNCounter FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Bytes cannot be null or empty", nameof(bytes));

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"PNCounter({Value}, +{PositiveValue}/-{NegativeValue})";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is PNCounter other && Equals(other);
        }

        /// <summary>
        /// Determines whether this counter equals another.
        /// </summary>
        /// <param name="other">The other counter to compare.</param>
        /// <returns>True if the counters have the same state.</returns>
        public bool Equals(PNCounter? other)
        {
            if (other is null)
                return false;

            return _positive.Equals(other._positive) && _negative.Equals(other._negative);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(_positive.GetHashCode(), _negative.GetHashCode());
        }
    }
}
