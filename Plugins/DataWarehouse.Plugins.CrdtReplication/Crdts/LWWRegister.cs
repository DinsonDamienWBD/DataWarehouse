using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Last-Writer-Wins Register (LWW-Register) CRDT.
    /// A register that resolves conflicts by keeping the value with the latest timestamp.
    /// </summary>
    /// <typeparam name="T">The type of value stored in the register.</typeparam>
    /// <remarks>
    /// Properties:
    /// - Supports read and write operations
    /// - Conflicts resolved by timestamp (latest wins)
    /// - Ties broken by node ID for determinism
    /// - Uses HLC timestamp for improved accuracy
    /// - Eventually converges to the same value on all replicas
    /// </remarks>
    public sealed class LWWRegister<T> : ICrdt<LWWRegister<T>>
    {
        private T? _value;
        private HlcTimestamp _timestamp;
        private readonly string _nodeId;
        private readonly HybridLogicalClock _clock;

        /// <summary>
        /// Creates a new LWW-Register for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public LWWRegister(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _clock = new HybridLogicalClock(nodeId);
            _value = default;
            _timestamp = _clock.Now();
        }

        /// <summary>
        /// Creates a LWW-Register with an initial value.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="value">The initial value.</param>
        public LWWRegister(string nodeId, T? value) : this(nodeId)
        {
            _value = value;
            _timestamp = _clock.Now();
        }

        /// <summary>
        /// Creates a LWW-Register with existing state.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="value">The value.</param>
        /// <param name="timestamp">The timestamp.</param>
        private LWWRegister(string nodeId, T? value, HlcTimestamp timestamp)
        {
            _nodeId = nodeId;
            _clock = new HybridLogicalClock(nodeId);
            _value = value;
            _timestamp = timestamp;
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the current value of the register.
        /// </summary>
        public T? Value => _value;

        /// <summary>
        /// Gets the timestamp of the current value.
        /// </summary>
        public HlcTimestamp Timestamp => _timestamp;

        /// <summary>
        /// Gets whether the register has a value.
        /// </summary>
        public bool HasValue => _value != null;

        /// <summary>
        /// Sets a new value in the register.
        /// </summary>
        /// <param name="value">The new value.</param>
        /// <returns>A new LWW-Register with the updated value.</returns>
        public LWWRegister<T> Set(T? value)
        {
            var newTimestamp = _clock.Now();
            return new LWWRegister<T>(_nodeId, value, newTimestamp);
        }

        /// <summary>
        /// Sets a new value in the register in place.
        /// </summary>
        /// <param name="value">The new value.</param>
        public void SetInPlace(T? value)
        {
            _value = value;
            _timestamp = _clock.Now();
        }

        /// <summary>
        /// Sets a new value with a specific timestamp.
        /// Used for applying remote updates.
        /// </summary>
        /// <param name="value">The new value.</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <returns>A new LWW-Register with the updated value if the timestamp is newer.</returns>
        public LWWRegister<T> SetWithTimestamp(T? value, HlcTimestamp timestamp)
        {
            // Update local clock with received timestamp
            _clock.Receive(timestamp);

            if (timestamp > _timestamp)
            {
                return new LWWRegister<T>(_nodeId, value, timestamp);
            }

            return Clone();
        }

        /// <summary>
        /// Sets a new value with a specific timestamp in place.
        /// Used for applying remote updates.
        /// </summary>
        /// <param name="value">The new value.</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <returns>True if the value was updated.</returns>
        public bool SetWithTimestampInPlace(T? value, HlcTimestamp timestamp)
        {
            // Update local clock with received timestamp
            _clock.Receive(timestamp);

            if (timestamp > _timestamp)
            {
                _value = value;
                _timestamp = timestamp;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Merges this register with another, keeping the value with the latest timestamp.
        /// </summary>
        /// <param name="other">The other register to merge with.</param>
        /// <returns>A new LWW-Register with the merged state.</returns>
        public LWWRegister<T> Merge(LWWRegister<T> other)
        {
            if (other == null)
                return Clone();

            // Update local clock
            _clock.Receive(other._timestamp);

            // Keep the value with the larger timestamp
            if (other._timestamp > _timestamp)
            {
                return new LWWRegister<T>(_nodeId, other._value, other._timestamp);
            }
            else if (_timestamp > other._timestamp)
            {
                return Clone();
            }
            else
            {
                // Timestamps equal - break tie by node ID for determinism
                var comparison = string.Compare(_timestamp.NodeId, other._timestamp.NodeId, StringComparison.Ordinal);
                if (comparison > 0)
                {
                    return Clone();
                }
                else
                {
                    return new LWWRegister<T>(_nodeId, other._value, other._timestamp);
                }
            }
        }

        /// <summary>
        /// Merges another register into this one in place.
        /// </summary>
        /// <param name="other">The other register to merge with.</param>
        public void MergeInPlace(LWWRegister<T> other)
        {
            if (other == null)
                return;

            // Update local clock
            _clock.Receive(other._timestamp);

            // Keep the value with the larger timestamp
            if (other._timestamp > _timestamp)
            {
                _value = other._value;
                _timestamp = other._timestamp;
            }
            else if (_timestamp == other._timestamp)
            {
                // Timestamps equal - break tie by node ID for determinism
                var comparison = string.Compare(_timestamp.NodeId, other._timestamp.NodeId, StringComparison.Ordinal);
                if (comparison < 0)
                {
                    _value = other._value;
                    _timestamp = other._timestamp;
                }
            }
        }

        /// <summary>
        /// Compares values and returns the winner based on timestamps.
        /// </summary>
        /// <param name="other">The other register to compare.</param>
        /// <returns>The register with the winning value.</returns>
        public LWWRegister<T> GetWinner(LWWRegister<T> other)
        {
            return Merge(other);
        }

        /// <summary>
        /// Determines if this register's value would win against another.
        /// </summary>
        /// <param name="other">The other register to compare.</param>
        /// <returns>True if this register's value would win.</returns>
        public bool WouldWinAgainst(LWWRegister<T> other)
        {
            if (other == null)
                return true;

            if (_timestamp > other._timestamp)
                return true;

            if (_timestamp < other._timestamp)
                return false;

            // Tie break by node ID
            return string.Compare(_timestamp.NodeId, other._timestamp.NodeId, StringComparison.Ordinal) > 0;
        }

        /// <summary>
        /// Creates a deep copy of this register.
        /// </summary>
        /// <returns>A new LWW-Register with the same state.</returns>
        public LWWRegister<T> Clone()
        {
            return new LWWRegister<T>(_nodeId, _value, _timestamp);
        }

        /// <summary>
        /// Clears the register value.
        /// </summary>
        /// <returns>A new LWW-Register with no value.</returns>
        public LWWRegister<T> Clear()
        {
            return Set(default);
        }

        /// <summary>
        /// Clears the register value in place.
        /// </summary>
        public void ClearInPlace()
        {
            SetInPlace(default);
        }

        /// <summary>
        /// Serializes the register to JSON.
        /// </summary>
        /// <returns>JSON string representation.</returns>
        public string ToJson()
        {
            return JsonSerializer.Serialize(new
            {
                nodeId = _nodeId,
                value = _value,
                timestamp = new
                {
                    physicalTime = _timestamp.PhysicalTime,
                    logicalCounter = _timestamp.LogicalCounter,
                    nodeId = _timestamp.NodeId
                }
            });
        }

        /// <summary>
        /// Deserializes a LWW-Register from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new LWW-Register instance.</returns>
        public static LWWRegister<T> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";

            T? value = default;
            if (doc.RootElement.TryGetProperty("value", out var valueElement) &&
                valueElement.ValueKind != JsonValueKind.Null)
            {
                value = JsonSerializer.Deserialize<T>(valueElement.GetRawText());
            }

            var timestampElement = doc.RootElement.GetProperty("timestamp");
            var timestamp = new HlcTimestamp(
                timestampElement.GetProperty("physicalTime").GetInt64(),
                timestampElement.GetProperty("logicalCounter").GetInt32(),
                timestampElement.GetProperty("nodeId").GetString() ?? ""
            );

            return new LWWRegister<T>(nodeId, value, timestamp);
        }

        /// <summary>
        /// Serializes the register to a byte array.
        /// </summary>
        /// <returns>Byte array representation.</returns>
        public byte[] ToBytes()
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                nodeId = _nodeId,
                value = _value,
                timestamp = new
                {
                    physicalTime = _timestamp.PhysicalTime,
                    logicalCounter = _timestamp.LogicalCounter,
                    nodeId = _timestamp.NodeId
                }
            });
        }

        /// <summary>
        /// Deserializes a LWW-Register from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new LWW-Register instance.</returns>
        public static LWWRegister<T> FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Bytes cannot be null or empty", nameof(bytes));

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"LWWRegister({_value}, ts={_timestamp})";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is LWWRegister<T> other && Equals(other);
        }

        /// <summary>
        /// Determines whether this register equals another.
        /// </summary>
        /// <param name="other">The other register to compare.</param>
        /// <returns>True if the registers have the same state.</returns>
        public bool Equals(LWWRegister<T>? other)
        {
            if (other is null)
                return false;

            return EqualityComparer<T?>.Default.Equals(_value, other._value) &&
                   _timestamp == other._timestamp;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(_value, _timestamp);
        }
    }

    /// <summary>
    /// Non-generic LWW-Register that stores string values.
    /// Convenience class for common use cases.
    /// </summary>
    public sealed class LWWRegister : LWWRegister<string>
    {
        /// <summary>
        /// Creates a new LWW-Register for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        public LWWRegister(string nodeId) : base(nodeId)
        {
        }

        /// <summary>
        /// Creates a LWW-Register with an initial value.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="value">The initial value.</param>
        public LWWRegister(string nodeId, string? value) : base(nodeId, value)
        {
        }
    }
}
