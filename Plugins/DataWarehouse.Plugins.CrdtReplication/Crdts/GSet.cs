using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Grow-only Set (G-Set) CRDT.
    /// A distributed set that only supports add operations.
    /// Once an element is added, it can never be removed.
    /// </summary>
    /// <typeparam name="T">The type of elements in the set.</typeparam>
    /// <remarks>
    /// Properties:
    /// - Supports add only (no remove)
    /// - Merge operation is set union
    /// - Eventually converges to the same set on all replicas
    /// - Delta-state capable for efficient synchronization
    /// </remarks>
    public class GSet<T> : ICrdt<GSet<T>>, IDeltaCrdt<GSet<T>>, IEnumerable<T> where T : notnull
    {
        private readonly ConcurrentDictionary<T, byte> _elements;
        private readonly string _nodeId;

        /// <summary>
        /// Creates a new empty G-Set for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public GSet(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _elements = new ConcurrentDictionary<T, byte>();
        }

        /// <summary>
        /// Creates a G-Set with existing elements.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="elements">Initial elements.</param>
        public GSet(string nodeId, IEnumerable<T> elements) : this(nodeId)
        {
            if (elements != null)
            {
                foreach (var element in elements)
                {
                    _elements.TryAdd(element, 0);
                }
            }
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the number of elements in the set.
        /// </summary>
        public int Count => _elements.Count;

        /// <summary>
        /// Gets the elements as a read-only set.
        /// </summary>
        public IReadOnlySet<T> Elements => new HashSet<T>(_elements.Keys);

        /// <summary>
        /// Determines whether the set contains the specified element.
        /// </summary>
        /// <param name="element">The element to look for.</param>
        /// <returns>True if the element is in the set.</returns>
        public bool Contains(T element)
        {
            return _elements.ContainsKey(element);
        }

        /// <summary>
        /// Adds an element to the set.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>A new G-Set with the element added.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public GSet<T> Add(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            var newSet = Clone();
            newSet._elements.TryAdd(element, 0);
            return newSet;
        }

        /// <summary>
        /// Adds an element to the set in place.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>True if the element was added, false if it already existed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public bool AddInPlace(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            return _elements.TryAdd(element, 0);
        }

        /// <summary>
        /// Adds multiple elements to the set.
        /// </summary>
        /// <param name="elements">The elements to add.</param>
        /// <returns>A new G-Set with the elements added.</returns>
        public GSet<T> AddRange(IEnumerable<T> elements)
        {
            if (elements == null)
                return Clone();

            var newSet = Clone();
            foreach (var element in elements)
            {
                if (element != null)
                    newSet._elements.TryAdd(element, 0);
            }
            return newSet;
        }

        /// <summary>
        /// Adds multiple elements to the set in place.
        /// </summary>
        /// <param name="elements">The elements to add.</param>
        /// <returns>The number of elements actually added (not already present).</returns>
        public int AddRangeInPlace(IEnumerable<T> elements)
        {
            if (elements == null)
                return 0;

            var count = 0;
            foreach (var element in elements)
            {
                if (element != null && _elements.TryAdd(element, 0))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Merges this set with another using set union.
        /// </summary>
        /// <param name="other">The other set to merge with.</param>
        /// <returns>A new G-Set containing all elements from both sets.</returns>
        public GSet<T> Merge(GSet<T> other)
        {
            if (other == null)
                return Clone();

            var merged = Clone();
            foreach (var element in other._elements.Keys)
            {
                merged._elements.TryAdd(element, 0);
            }
            return merged;
        }

        /// <summary>
        /// Merges another set into this one in place.
        /// </summary>
        /// <param name="other">The other set to merge with.</param>
        public void MergeInPlace(GSet<T> other)
        {
            if (other == null)
                return;

            foreach (var element in other._elements.Keys)
            {
                _elements.TryAdd(element, 0);
            }
        }

        /// <summary>
        /// Computes the delta state since the given previous state.
        /// </summary>
        /// <param name="previousState">The previous state to compare against.</param>
        /// <returns>A delta containing only the new elements.</returns>
        public GSet<T> GetDelta(GSet<T>? previousState)
        {
            var delta = new GSet<T>(_nodeId);

            foreach (var element in _elements.Keys)
            {
                if (previousState == null || !previousState.Contains(element))
                {
                    delta._elements.TryAdd(element, 0);
                }
            }

            return delta;
        }

        /// <summary>
        /// Applies a delta state to this set.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        /// <returns>A new G-Set with the delta applied.</returns>
        public GSet<T> ApplyDelta(GSet<T> delta)
        {
            return Merge(delta);
        }

        /// <summary>
        /// Applies a delta state to this set in place.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        public void ApplyDeltaInPlace(GSet<T> delta)
        {
            MergeInPlace(delta);
        }

        /// <summary>
        /// Computes the intersection of this set with another.
        /// Note: This is a read-only operation and doesn't follow CRDT semantics.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>A new set containing elements common to both sets.</returns>
        public IReadOnlySet<T> Intersect(GSet<T> other)
        {
            if (other == null)
                return new HashSet<T>();

            return new HashSet<T>(_elements.Keys.Where(e => other.Contains(e)));
        }

        /// <summary>
        /// Computes the difference of this set from another.
        /// Note: This is a read-only operation and doesn't follow CRDT semantics.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns>A new set containing elements in this set but not in the other.</returns>
        public IReadOnlySet<T> Except(GSet<T> other)
        {
            if (other == null)
                return Elements;

            return new HashSet<T>(_elements.Keys.Where(e => !other.Contains(e)));
        }

        /// <summary>
        /// Creates a deep copy of this set.
        /// </summary>
        /// <returns>A new G-Set with the same elements.</returns>
        public GSet<T> Clone()
        {
            return new GSet<T>(_nodeId, _elements.Keys);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the set.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            return _elements.Keys.GetEnumerator();
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Serializes the set to JSON.
        /// </summary>
        /// <returns>JSON string representation.</returns>
        public string ToJson()
        {
            return JsonSerializer.Serialize(new
            {
                nodeId = _nodeId,
                elements = _elements.Keys.ToList()
            });
        }

        /// <summary>
        /// Deserializes a G-Set from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new G-Set instance.</returns>
        public static GSet<T> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";
            var elements = new List<T>();

            if (doc.RootElement.TryGetProperty("elements", out var elementsArray))
            {
                foreach (var element in elementsArray.EnumerateArray())
                {
                    var value = JsonSerializer.Deserialize<T>(element.GetRawText());
                    if (value != null)
                        elements.Add(value);
                }
            }

            return new GSet<T>(nodeId, elements);
        }

        /// <summary>
        /// Serializes the set to a byte array.
        /// </summary>
        /// <returns>Byte array representation.</returns>
        public byte[] ToBytes()
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                nodeId = _nodeId,
                elements = _elements.Keys.ToList()
            });
        }

        /// <summary>
        /// Deserializes a G-Set from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new G-Set instance.</returns>
        public static GSet<T> FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Bytes cannot be null or empty", nameof(bytes));

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var preview = string.Join(", ", _elements.Keys.Take(5));
            if (_elements.Count > 5)
                preview += ", ...";
            return $"GSet({Count} elements: [{preview}])";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is GSet<T> other && Equals(other);
        }

        /// <summary>
        /// Determines whether this set equals another.
        /// </summary>
        /// <param name="other">The other set to compare.</param>
        /// <returns>True if the sets have the same elements.</returns>
        public bool Equals(GSet<T>? other)
        {
            if (other is null)
                return false;

            if (_elements.Count != other._elements.Count)
                return false;

            return _elements.Keys.All(e => other.Contains(e));
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var element in _elements.Keys.OrderBy(e => e?.GetHashCode() ?? 0))
            {
                hash.Add(element);
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Non-generic G-Set that stores elements as strings.
    /// Convenience class for common use cases.
    /// </summary>
    public sealed class GSet : GSet<string>
    {
        /// <summary>
        /// Creates a new empty G-Set for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        public GSet(string nodeId) : base(nodeId)
        {
        }

        /// <summary>
        /// Creates a G-Set with existing elements.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="elements">Initial elements.</param>
        public GSet(string nodeId, IEnumerable<string> elements) : base(nodeId, elements)
        {
        }
    }
}
