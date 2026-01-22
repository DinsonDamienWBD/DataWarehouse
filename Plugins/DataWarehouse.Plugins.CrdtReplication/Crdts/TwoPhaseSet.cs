using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Two-Phase Set (2P-Set) CRDT.
    /// A distributed set that supports both add and remove operations.
    /// Implemented as two G-Sets: one for added elements and one for removed elements (tombstones).
    /// </summary>
    /// <typeparam name="T">The type of elements in the set.</typeparam>
    /// <remarks>
    /// Properties:
    /// - Supports add and remove operations
    /// - An element can only be removed if it has been added
    /// - Once removed, an element can never be added again (tombstone)
    /// - Visible elements = added set - removed set
    /// - Eventually converges to the same set on all replicas
    /// </remarks>
    public sealed class TwoPhaseSet<T> : ICrdt<TwoPhaseSet<T>>, IDeltaCrdt<TwoPhaseSet<T>>, IEnumerable<T> where T : notnull
    {
        private readonly GSet<T> _added;
        private readonly GSet<T> _removed;
        private readonly string _nodeId;

        /// <summary>
        /// Creates a new empty 2P-Set for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public TwoPhaseSet(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _added = new GSet<T>(nodeId);
            _removed = new GSet<T>(nodeId);
        }

        /// <summary>
        /// Creates a 2P-Set with existing added and removed sets.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <param name="added">The add set (G-Set).</param>
        /// <param name="removed">The tombstone set (G-Set).</param>
        private TwoPhaseSet(string nodeId, GSet<T> added, GSet<T> removed)
        {
            _nodeId = nodeId;
            _added = added ?? throw new ArgumentNullException(nameof(added));
            _removed = removed ?? throw new ArgumentNullException(nameof(removed));
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the number of visible elements in the set.
        /// </summary>
        public int Count => _added.Elements.Count(e => !_removed.Contains(e));

        /// <summary>
        /// Gets the visible elements as a read-only set.
        /// </summary>
        public IReadOnlySet<T> Elements => new HashSet<T>(_added.Elements.Where(e => !_removed.Contains(e)));

        /// <summary>
        /// Gets the add set.
        /// </summary>
        public GSet<T> AddedSet => _added;

        /// <summary>
        /// Gets the tombstone set.
        /// </summary>
        public GSet<T> RemovedSet => _removed;

        /// <summary>
        /// Gets the number of tombstones (removed elements).
        /// </summary>
        public int TombstoneCount => _removed.Count;

        /// <summary>
        /// Determines whether the set contains the specified element.
        /// </summary>
        /// <param name="element">The element to look for.</param>
        /// <returns>True if the element is in the set and not removed.</returns>
        public bool Contains(T element)
        {
            return _added.Contains(element) && !_removed.Contains(element);
        }

        /// <summary>
        /// Determines whether an element has been removed (tombstoned).
        /// </summary>
        /// <param name="element">The element to check.</param>
        /// <returns>True if the element has been removed.</returns>
        public bool IsRemoved(T element)
        {
            return _removed.Contains(element);
        }

        /// <summary>
        /// Determines whether an element was ever added (regardless of removal).
        /// </summary>
        /// <param name="element">The element to check.</param>
        /// <returns>True if the element was ever added.</returns>
        public bool WasEverAdded(T element)
        {
            return _added.Contains(element);
        }

        /// <summary>
        /// Adds an element to the set.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>A new 2P-Set with the element added, or the same set if the element is tombstoned.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when trying to add a removed element.</exception>
        public TwoPhaseSet<T> Add(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (_removed.Contains(element))
                throw new InvalidOperationException($"Cannot add element that has been removed (tombstoned)");

            return new TwoPhaseSet<T>(_nodeId, _added.Add(element), _removed.Clone());
        }

        /// <summary>
        /// Tries to add an element to the set.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <param name="result">The resulting set if successful.</param>
        /// <returns>True if the element was added, false if it's tombstoned.</returns>
        public bool TryAdd(T element, out TwoPhaseSet<T> result)
        {
            if (element == null || _removed.Contains(element))
            {
                result = this;
                return false;
            }

            result = new TwoPhaseSet<T>(_nodeId, _added.Add(element), _removed.Clone());
            return true;
        }

        /// <summary>
        /// Adds an element to the set in place.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>True if the element was added, false if it's tombstoned.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public bool AddInPlace(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (_removed.Contains(element))
                return false;

            return _added.AddInPlace(element);
        }

        /// <summary>
        /// Removes an element from the set.
        /// </summary>
        /// <param name="element">The element to remove.</param>
        /// <returns>A new 2P-Set with the element removed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when trying to remove an element that was never added.</exception>
        public TwoPhaseSet<T> Remove(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (!_added.Contains(element))
                throw new InvalidOperationException($"Cannot remove element that was never added");

            return new TwoPhaseSet<T>(_nodeId, _added.Clone(), _removed.Add(element));
        }

        /// <summary>
        /// Tries to remove an element from the set.
        /// </summary>
        /// <param name="element">The element to remove.</param>
        /// <param name="result">The resulting set if successful.</param>
        /// <returns>True if the element was removed, false if it was never added.</returns>
        public bool TryRemove(T element, out TwoPhaseSet<T> result)
        {
            if (element == null || !_added.Contains(element))
            {
                result = this;
                return false;
            }

            result = new TwoPhaseSet<T>(_nodeId, _added.Clone(), _removed.Add(element));
            return true;
        }

        /// <summary>
        /// Removes an element from the set in place.
        /// </summary>
        /// <param name="element">The element to remove.</param>
        /// <returns>True if the element was removed, false if it was never added or already removed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public bool RemoveInPlace(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (!_added.Contains(element) || _removed.Contains(element))
                return false;

            return _removed.AddInPlace(element);
        }

        /// <summary>
        /// Merges this set with another.
        /// </summary>
        /// <param name="other">The other set to merge with.</param>
        /// <returns>A new 2P-Set containing the merged state.</returns>
        public TwoPhaseSet<T> Merge(TwoPhaseSet<T> other)
        {
            if (other == null)
                return Clone();

            return new TwoPhaseSet<T>(
                _nodeId,
                _added.Merge(other._added),
                _removed.Merge(other._removed)
            );
        }

        /// <summary>
        /// Merges another set into this one in place.
        /// </summary>
        /// <param name="other">The other set to merge with.</param>
        public void MergeInPlace(TwoPhaseSet<T> other)
        {
            if (other == null)
                return;

            _added.MergeInPlace(other._added);
            _removed.MergeInPlace(other._removed);
        }

        /// <summary>
        /// Computes the delta state since the given previous state.
        /// </summary>
        /// <param name="previousState">The previous state to compare against.</param>
        /// <returns>A delta containing only the changes.</returns>
        public TwoPhaseSet<T> GetDelta(TwoPhaseSet<T>? previousState)
        {
            var addedDelta = _added.GetDelta(previousState?._added);
            var removedDelta = _removed.GetDelta(previousState?._removed);
            return new TwoPhaseSet<T>(_nodeId, addedDelta, removedDelta);
        }

        /// <summary>
        /// Applies a delta state to this set.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        /// <returns>A new 2P-Set with the delta applied.</returns>
        public TwoPhaseSet<T> ApplyDelta(TwoPhaseSet<T> delta)
        {
            return Merge(delta);
        }

        /// <summary>
        /// Applies a delta state to this set in place.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        public void ApplyDeltaInPlace(TwoPhaseSet<T> delta)
        {
            MergeInPlace(delta);
        }

        /// <summary>
        /// Creates a deep copy of this set.
        /// </summary>
        /// <returns>A new 2P-Set with the same state.</returns>
        public TwoPhaseSet<T> Clone()
        {
            return new TwoPhaseSet<T>(_nodeId, _added.Clone(), _removed.Clone());
        }

        /// <summary>
        /// Returns an enumerator that iterates through the visible elements.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            return Elements.GetEnumerator();
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
                added = _added.Elements.ToList(),
                removed = _removed.Elements.ToList()
            });
        }

        /// <summary>
        /// Deserializes a 2P-Set from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new 2P-Set instance.</returns>
        public static TwoPhaseSet<T> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";

            var addedList = new List<T>();
            var removedList = new List<T>();

            if (doc.RootElement.TryGetProperty("added", out var addedArray))
            {
                foreach (var element in addedArray.EnumerateArray())
                {
                    var value = JsonSerializer.Deserialize<T>(element.GetRawText());
                    if (value != null)
                        addedList.Add(value);
                }
            }

            if (doc.RootElement.TryGetProperty("removed", out var removedArray))
            {
                foreach (var element in removedArray.EnumerateArray())
                {
                    var value = JsonSerializer.Deserialize<T>(element.GetRawText());
                    if (value != null)
                        removedList.Add(value);
                }
            }

            return new TwoPhaseSet<T>(
                nodeId,
                new GSet<T>(nodeId, addedList),
                new GSet<T>(nodeId, removedList)
            );
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
                added = _added.Elements.ToList(),
                removed = _removed.Elements.ToList()
            });
        }

        /// <summary>
        /// Deserializes a 2P-Set from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new 2P-Set instance.</returns>
        public static TwoPhaseSet<T> FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("Bytes cannot be null or empty", nameof(bytes));

            return FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var preview = string.Join(", ", Elements.Take(5));
            if (Count > 5)
                preview += ", ...";
            return $"TwoPhaseSet({Count} elements, {TombstoneCount} tombstones: [{preview}])";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is TwoPhaseSet<T> other && Equals(other);
        }

        /// <summary>
        /// Determines whether this set equals another.
        /// </summary>
        /// <param name="other">The other set to compare.</param>
        /// <returns>True if the sets have the same state.</returns>
        public bool Equals(TwoPhaseSet<T>? other)
        {
            if (other is null)
                return false;

            return _added.Equals(other._added) && _removed.Equals(other._removed);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(_added.GetHashCode(), _removed.GetHashCode());
        }
    }

    /// <summary>
    /// Non-generic 2P-Set that stores elements as strings.
    /// Convenience class for common use cases.
    /// </summary>
    public sealed class TwoPhaseSet : TwoPhaseSet<string>
    {
        /// <summary>
        /// Creates a new empty 2P-Set for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        public TwoPhaseSet(string nodeId) : base(nodeId)
        {
        }
    }
}
