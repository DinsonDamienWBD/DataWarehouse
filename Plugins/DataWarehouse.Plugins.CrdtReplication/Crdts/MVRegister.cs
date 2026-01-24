using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Multi-Value Register (MV-Register) CRDT.
    /// A register that preserves all concurrent values until explicitly resolved.
    /// Uses vector clocks to detect concurrent writes.
    /// </summary>
    /// <typeparam name="T">The type of value stored in the register.</typeparam>
    /// <remarks>
    /// Properties:
    /// - Supports read and write operations
    /// - Concurrent writes result in multiple values
    /// - Causal ordering determines which values supersede others
    /// - No data is lost during concurrent writes
    /// - Eventually converges to the same set of values on all replicas
    /// </remarks>
    public class MVRegister<T> : ICrdt<MVRegister<T>>
    {
        private readonly List<ValueVersion<T>> _versions;
        private readonly string _nodeId;
        private VectorClock _clock;

        /// <summary>
        /// Creates a new empty MV-Register for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public MVRegister(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _versions = new List<ValueVersion<T>>();
            _clock = new VectorClock();
        }

        /// <summary>
        /// Creates a MV-Register with an initial value.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="value">The initial value.</param>
        public MVRegister(string nodeId, T? value) : this(nodeId)
        {
            if (value != null)
            {
                _clock = _clock.Increment(nodeId);
                _versions.Add(new ValueVersion<T>(value, _clock.Clone()));
            }
        }

        /// <summary>
        /// Creates a MV-Register with existing state.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="versions">The value versions.</param>
        /// <param name="clock">The vector clock.</param>
        private MVRegister(string nodeId, IEnumerable<ValueVersion<T>> versions, VectorClock clock)
        {
            _nodeId = nodeId;
            _versions = new List<ValueVersion<T>>(versions);
            _clock = clock.Clone();
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the current vector clock.
        /// </summary>
        public VectorClock Clock => _clock;

        /// <summary>
        /// Gets all current values (may be multiple if concurrent writes occurred).
        /// </summary>
        public IReadOnlyList<T?> Values => _versions.Select(v => v.Value).ToList();

        /// <summary>
        /// Gets all value versions with their vector clocks.
        /// </summary>
        public IReadOnlyList<ValueVersion<T>> Versions => _versions.AsReadOnly();

        /// <summary>
        /// Gets the number of concurrent values.
        /// </summary>
        public int ValueCount => _versions.Count;

        /// <summary>
        /// Gets whether there are multiple concurrent values (conflict).
        /// </summary>
        public bool HasConflict => _versions.Count > 1;

        /// <summary>
        /// Gets a single value if there's no conflict, or throws if there are multiple values.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when there are multiple concurrent values.</exception>
        public T? Value
        {
            get
            {
                if (_versions.Count == 0)
                    return default;
                if (_versions.Count == 1)
                    return _versions[0].Value;
                throw new InvalidOperationException(
                    $"Multiple concurrent values exist ({_versions.Count}). Use Values property or resolve the conflict first.");
            }
        }

        /// <summary>
        /// Gets a single value, or the first value if there are multiple.
        /// </summary>
        public T? FirstValue => _versions.Count > 0 ? _versions[0].Value : default;

        /// <summary>
        /// Sets a new value, replacing all previous values.
        /// </summary>
        /// <param name="value">The new value.</param>
        /// <returns>A new MV-Register with the updated value.</returns>
        public MVRegister<T> Set(T? value)
        {
            var newClock = _clock.Increment(_nodeId);
            var newVersions = new List<ValueVersion<T>>
            {
                new ValueVersion<T>(value, newClock.Clone())
            };
            return new MVRegister<T>(_nodeId, newVersions, newClock);
        }

        /// <summary>
        /// Sets a new value in place, replacing all previous values.
        /// </summary>
        /// <param name="value">The new value.</param>
        public void SetInPlace(T? value)
        {
            _clock.IncrementInPlace(_nodeId);
            _versions.Clear();
            _versions.Add(new ValueVersion<T>(value, _clock.Clone()));
        }

        /// <summary>
        /// Resolves a conflict by selecting a single value.
        /// </summary>
        /// <param name="value">The resolved value to keep.</param>
        /// <returns>A new MV-Register with the resolved value.</returns>
        public MVRegister<T> Resolve(T? value)
        {
            return Set(value);
        }

        /// <summary>
        /// Resolves a conflict by selecting a value using a resolver function.
        /// </summary>
        /// <param name="resolver">Function that selects a value from the concurrent values.</param>
        /// <returns>A new MV-Register with the resolved value.</returns>
        public MVRegister<T> Resolve(Func<IReadOnlyList<T?>, T?> resolver)
        {
            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));

            var resolvedValue = resolver(Values);
            return Set(resolvedValue);
        }

        /// <summary>
        /// Resolves a conflict in place by selecting a single value.
        /// </summary>
        /// <param name="value">The resolved value to keep.</param>
        public void ResolveInPlace(T? value)
        {
            SetInPlace(value);
        }

        /// <summary>
        /// Merges this register with another, preserving concurrent values.
        /// </summary>
        /// <param name="other">The other register to merge with.</param>
        /// <returns>A new MV-Register with the merged state.</returns>
        public MVRegister<T> Merge(MVRegister<T> other)
        {
            if (other == null)
                return Clone();

            var mergedClock = _clock.Merge(other._clock);
            var mergedVersions = new List<ValueVersion<T>>();

            // Add versions from this register that are not dominated by other
            foreach (var version in _versions)
            {
                var dominated = other._versions.Any(ov =>
                    version.Clock.HappenedBefore(ov.Clock));

                if (!dominated)
                {
                    mergedVersions.Add(version.Clone());
                }
            }

            // Add versions from other register that are not dominated by this
            foreach (var version in other._versions)
            {
                var dominated = _versions.Any(tv =>
                    version.Clock.HappenedBefore(tv.Clock));

                var alreadyPresent = mergedVersions.Any(mv =>
                    mv.Clock.Equals(version.Clock));

                if (!dominated && !alreadyPresent)
                {
                    mergedVersions.Add(version.Clone());
                }
            }

            // Remove duplicates by vector clock
            var uniqueVersions = mergedVersions
                .GroupBy(v => v.Clock.ToString())
                .Select(g => g.First())
                .ToList();

            return new MVRegister<T>(_nodeId, uniqueVersions, mergedClock);
        }

        /// <summary>
        /// Merges another register into this one in place.
        /// </summary>
        /// <param name="other">The other register to merge with.</param>
        public void MergeInPlace(MVRegister<T> other)
        {
            if (other == null)
                return;

            var merged = Merge(other);
            _clock = merged._clock;
            _versions.Clear();
            _versions.AddRange(merged._versions);
        }

        /// <summary>
        /// Creates a deep copy of this register.
        /// </summary>
        /// <returns>A new MV-Register with the same state.</returns>
        public MVRegister<T> Clone()
        {
            return new MVRegister<T>(
                _nodeId,
                _versions.Select(v => v.Clone()),
                _clock
            );
        }

        /// <summary>
        /// Clears the register, removing all values.
        /// </summary>
        /// <returns>A new empty MV-Register.</returns>
        public MVRegister<T> Clear()
        {
            var newClock = _clock.Increment(_nodeId);
            return new MVRegister<T>(_nodeId, Enumerable.Empty<ValueVersion<T>>(), newClock);
        }

        /// <summary>
        /// Clears the register in place.
        /// </summary>
        public void ClearInPlace()
        {
            _clock.IncrementInPlace(_nodeId);
            _versions.Clear();
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
                clock = _clock.Entries,
                versions = _versions.Select(v => new
                {
                    value = v.Value,
                    clock = v.Clock.Entries
                }).ToList()
            });
        }

        /// <summary>
        /// Deserializes a MV-Register from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new MV-Register instance.</returns>
        public static MVRegister<T> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";

            var clockDict = new Dictionary<string, long>();
            if (doc.RootElement.TryGetProperty("clock", out var clockElement))
            {
                foreach (var prop in clockElement.EnumerateObject())
                {
                    clockDict[prop.Name] = prop.Value.GetInt64();
                }
            }
            var clock = new VectorClock(clockDict);

            var versions = new List<ValueVersion<T>>();
            if (doc.RootElement.TryGetProperty("versions", out var versionsArray))
            {
                foreach (var versionElement in versionsArray.EnumerateArray())
                {
                    T? value = default;
                    if (versionElement.TryGetProperty("value", out var valueElement) &&
                        valueElement.ValueKind != JsonValueKind.Null)
                    {
                        value = JsonSerializer.Deserialize<T>(valueElement.GetRawText());
                    }

                    var vClockDict = new Dictionary<string, long>();
                    if (versionElement.TryGetProperty("clock", out var vClockElement))
                    {
                        foreach (var prop in vClockElement.EnumerateObject())
                        {
                            vClockDict[prop.Name] = prop.Value.GetInt64();
                        }
                    }

                    versions.Add(new ValueVersion<T>(value, new VectorClock(vClockDict)));
                }
            }

            return new MVRegister<T>(nodeId, versions, clock);
        }

        /// <summary>
        /// Serializes the register to a byte array.
        /// </summary>
        /// <returns>Byte array representation.</returns>
        public byte[] ToBytes()
        {
            return System.Text.Encoding.UTF8.GetBytes(ToJson());
        }

        /// <summary>
        /// Deserializes a MV-Register from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new MV-Register instance.</returns>
        public static MVRegister<T> FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Bytes cannot be null or empty", nameof(bytes));

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var valuesStr = string.Join(", ", Values.Select(v => v?.ToString() ?? "null").Take(5));
            if (Values.Count > 5)
                valuesStr += ", ...";
            return $"MVRegister({Values.Count} values: [{valuesStr}], conflict={HasConflict})";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is MVRegister<T> other && Equals(other);
        }

        /// <summary>
        /// Determines whether this register equals another.
        /// </summary>
        /// <param name="other">The other register to compare.</param>
        /// <returns>True if the registers have the same state.</returns>
        public bool Equals(MVRegister<T>? other)
        {
            if (other is null)
                return false;

            if (!_clock.Equals(other._clock))
                return false;

            if (_versions.Count != other._versions.Count)
                return false;

            var sortedThis = _versions.OrderBy(v => v.Clock.ToString()).ToList();
            var sortedOther = other._versions.OrderBy(v => v.Clock.ToString()).ToList();

            for (var i = 0; i < sortedThis.Count; i++)
            {
                if (!sortedThis[i].Equals(sortedOther[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_clock);
            foreach (var version in _versions.OrderBy(v => v.Clock.ToString()))
            {
                hash.Add(version);
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Represents a value with its associated vector clock version.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    public sealed class ValueVersion<T> : IEquatable<ValueVersion<T>>
    {
        /// <summary>
        /// The value.
        /// </summary>
        public T? Value { get; }

        /// <summary>
        /// The vector clock when this value was set.
        /// </summary>
        public VectorClock Clock { get; }

        /// <summary>
        /// Creates a new value version.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="clock">The vector clock.</param>
        public ValueVersion(T? value, VectorClock clock)
        {
            Value = value;
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Creates a deep copy of this value version.
        /// </summary>
        /// <returns>A new ValueVersion with the same state.</returns>
        public ValueVersion<T> Clone()
        {
            return new ValueVersion<T>(Value, Clock.Clone());
        }

        /// <inheritdoc />
        public bool Equals(ValueVersion<T>? other)
        {
            if (other is null)
                return false;

            return EqualityComparer<T?>.Default.Equals(Value, other.Value) &&
                   Clock.Equals(other.Clock);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is ValueVersion<T> other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(Value, Clock);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"ValueVersion({Value}, clock={Clock})";
        }
    }

    /// <summary>
    /// Non-generic MV-Register that stores string values.
    /// Convenience class for common use cases.
    /// </summary>
    public sealed class MVRegister : MVRegister<string>
    {
        /// <summary>
        /// Creates a new MV-Register for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        public MVRegister(string nodeId) : base(nodeId)
        {
        }

        /// <summary>
        /// Creates a MV-Register with an initial value.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="value">The initial value.</param>
        public MVRegister(string nodeId, string? value) : base(nodeId, value)
        {
        }
    }
}
