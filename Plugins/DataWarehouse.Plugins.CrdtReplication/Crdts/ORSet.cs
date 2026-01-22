using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DataWarehouse.Plugins.CrdtReplication.Crdts
{
    /// <summary>
    /// Observed-Remove Set (OR-Set) CRDT.
    /// A set that supports both add and remove operations with add-wins semantics.
    /// Each element is tagged with unique identifiers to track additions.
    /// </summary>
    /// <typeparam name="T">The type of elements in the set.</typeparam>
    /// <remarks>
    /// Properties:
    /// - Supports add and remove operations
    /// - Add wins over concurrent remove (add-wins semantics)
    /// - Elements can be re-added after removal
    /// - Uses unique tags to track each add operation
    /// - Eventually converges to the same set on all replicas
    /// - Delta-state capable for efficient synchronization
    /// </remarks>
    public sealed class ORSet<T> : ICrdt<ORSet<T>>, IDeltaCrdt<ORSet<T>>, IEnumerable<T> where T : notnull
    {
        private readonly ConcurrentDictionary<T, HashSet<UniqueTag>> _elements;
        private readonly ConcurrentDictionary<UniqueTag, byte> _tombstones;
        private readonly string _nodeId;
        private long _tagCounter;

        /// <summary>
        /// Creates a new empty OR-Set for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        /// <exception cref="ArgumentNullException">Thrown when nodeId is null or empty.</exception>
        public ORSet(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            _nodeId = nodeId;
            _elements = new ConcurrentDictionary<T, HashSet<UniqueTag>>();
            _tombstones = new ConcurrentDictionary<UniqueTag, byte>();
            _tagCounter = 0;
        }

        /// <summary>
        /// Creates an OR-Set with existing state.
        /// </summary>
        private ORSet(
            string nodeId,
            IDictionary<T, HashSet<UniqueTag>> elements,
            IEnumerable<UniqueTag> tombstones,
            long tagCounter)
        {
            _nodeId = nodeId;
            _elements = new ConcurrentDictionary<T, HashSet<UniqueTag>>(
                elements.Select(kv => new KeyValuePair<T, HashSet<UniqueTag>>(kv.Key, new HashSet<UniqueTag>(kv.Value))));
            _tombstones = new ConcurrentDictionary<UniqueTag, byte>(
                tombstones.Select(t => new KeyValuePair<UniqueTag, byte>(t, 0)));
            _tagCounter = tagCounter;
        }

        /// <summary>
        /// Gets the local node identifier.
        /// </summary>
        public string NodeId => _nodeId;

        /// <summary>
        /// Gets the number of visible elements in the set.
        /// </summary>
        public int Count
        {
            get
            {
                return _elements.Count(kv =>
                    kv.Value.Any(tag => !_tombstones.ContainsKey(tag)));
            }
        }

        /// <summary>
        /// Gets the visible elements as a read-only set.
        /// </summary>
        public IReadOnlySet<T> Elements
        {
            get
            {
                return new HashSet<T>(
                    _elements
                        .Where(kv => kv.Value.Any(tag => !_tombstones.ContainsKey(tag)))
                        .Select(kv => kv.Key));
            }
        }

        /// <summary>
        /// Gets the number of tombstones (for diagnostics).
        /// </summary>
        public int TombstoneCount => _tombstones.Count;

        /// <summary>
        /// Determines whether the set contains the specified element.
        /// </summary>
        /// <param name="element">The element to look for.</param>
        /// <returns>True if the element is in the set.</returns>
        public bool Contains(T element)
        {
            if (element == null)
                return false;

            return _elements.TryGetValue(element, out var tags) &&
                   tags.Any(tag => !_tombstones.ContainsKey(tag));
        }

        /// <summary>
        /// Generates a unique tag for an add operation.
        /// </summary>
        private UniqueTag GenerateTag()
        {
            var counter = Interlocked.Increment(ref _tagCounter);
            return new UniqueTag(_nodeId, counter, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// Adds an element to the set.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>A new OR-Set with the element added.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public ORSet<T> Add(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            var newSet = Clone();
            var tag = newSet.GenerateTag();

            newSet._elements.AddOrUpdate(
                element,
                _ => new HashSet<UniqueTag> { tag },
                (_, existing) =>
                {
                    existing.Add(tag);
                    return existing;
                });

            return newSet;
        }

        /// <summary>
        /// Adds an element to the set in place.
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>True if this is a new addition.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public bool AddInPlace(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            var tag = GenerateTag();
            var wasNew = !Contains(element);

            _elements.AddOrUpdate(
                element,
                _ => new HashSet<UniqueTag> { tag },
                (_, existing) =>
                {
                    existing.Add(tag);
                    return existing;
                });

            return wasNew;
        }

        /// <summary>
        /// Adds an element with a specific tag (for applying remote updates).
        /// </summary>
        internal void AddWithTag(T element, UniqueTag tag)
        {
            _elements.AddOrUpdate(
                element,
                _ => new HashSet<UniqueTag> { tag },
                (_, existing) =>
                {
                    existing.Add(tag);
                    return existing;
                });

            // Update tag counter to ensure uniqueness
            var maxCounter = Math.Max(_tagCounter, tag.Counter);
            Interlocked.Exchange(ref _tagCounter, maxCounter);
        }

        /// <summary>
        /// Removes an element from the set.
        /// </summary>
        /// <param name="element">The element to remove.</param>
        /// <returns>A new OR-Set with the element removed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public ORSet<T> Remove(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            var newSet = Clone();

            if (newSet._elements.TryGetValue(element, out var tags))
            {
                // Tombstone all current tags for this element
                foreach (var tag in tags.Where(t => !newSet._tombstones.ContainsKey(t)))
                {
                    newSet._tombstones.TryAdd(tag, 0);
                }
            }

            return newSet;
        }

        /// <summary>
        /// Removes an element from the set in place.
        /// </summary>
        /// <param name="element">The element to remove.</param>
        /// <returns>True if the element was removed, false if it wasn't present.</returns>
        /// <exception cref="ArgumentNullException">Thrown when element is null.</exception>
        public bool RemoveInPlace(T element)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (!_elements.TryGetValue(element, out var tags))
                return false;

            var removed = false;
            foreach (var tag in tags.Where(t => !_tombstones.ContainsKey(t)))
            {
                _tombstones.TryAdd(tag, 0);
                removed = true;
            }

            return removed;
        }

        /// <summary>
        /// Removes a specific tag (for applying remote updates).
        /// </summary>
        internal void RemoveTag(UniqueTag tag)
        {
            _tombstones.TryAdd(tag, 0);
        }

        /// <summary>
        /// Merges this set with another.
        /// </summary>
        /// <param name="other">The other set to merge with.</param>
        /// <returns>A new OR-Set containing the merged state.</returns>
        public ORSet<T> Merge(ORSet<T> other)
        {
            if (other == null)
                return Clone();

            // Merge elements (union of all tags)
            var mergedElements = new Dictionary<T, HashSet<UniqueTag>>();

            foreach (var (element, tags) in _elements)
            {
                mergedElements[element] = new HashSet<UniqueTag>(tags);
            }

            foreach (var (element, tags) in other._elements)
            {
                if (mergedElements.TryGetValue(element, out var existing))
                {
                    existing.UnionWith(tags);
                }
                else
                {
                    mergedElements[element] = new HashSet<UniqueTag>(tags);
                }
            }

            // Merge tombstones (union)
            var mergedTombstones = _tombstones.Keys.Union(other._tombstones.Keys);

            // Merge tag counter
            var maxTagCounter = Math.Max(_tagCounter, other._tagCounter);

            return new ORSet<T>(_nodeId, mergedElements, mergedTombstones, maxTagCounter);
        }

        /// <summary>
        /// Merges another set into this one in place.
        /// </summary>
        /// <param name="other">The other set to merge with.</param>
        public void MergeInPlace(ORSet<T> other)
        {
            if (other == null)
                return;

            // Merge elements
            foreach (var (element, tags) in other._elements)
            {
                _elements.AddOrUpdate(
                    element,
                    _ => new HashSet<UniqueTag>(tags),
                    (_, existing) =>
                    {
                        existing.UnionWith(tags);
                        return existing;
                    });
            }

            // Merge tombstones
            foreach (var tag in other._tombstones.Keys)
            {
                _tombstones.TryAdd(tag, 0);
            }

            // Update tag counter
            var maxCounter = Math.Max(_tagCounter, other._tagCounter);
            Interlocked.Exchange(ref _tagCounter, maxCounter);
        }

        /// <summary>
        /// Computes the delta state since the given previous state.
        /// </summary>
        /// <param name="previousState">The previous state to compare against.</param>
        /// <returns>A delta containing only the changes.</returns>
        public ORSet<T> GetDelta(ORSet<T>? previousState)
        {
            var deltaElements = new Dictionary<T, HashSet<UniqueTag>>();
            var deltaTombstones = new List<UniqueTag>();

            // Find new element tags
            foreach (var (element, tags) in _elements)
            {
                HashSet<UniqueTag>? previousTags = null;
                previousState?._elements.TryGetValue(element, out previousTags);

                var newTags = previousTags == null
                    ? tags
                    : new HashSet<UniqueTag>(tags.Except(previousTags));

                if (newTags.Count > 0)
                {
                    deltaElements[element] = newTags;
                }
            }

            // Find new tombstones
            foreach (var tag in _tombstones.Keys)
            {
                if (previousState == null || !previousState._tombstones.ContainsKey(tag))
                {
                    deltaTombstones.Add(tag);
                }
            }

            return new ORSet<T>(_nodeId, deltaElements, deltaTombstones, _tagCounter);
        }

        /// <summary>
        /// Applies a delta state to this set.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        /// <returns>A new OR-Set with the delta applied.</returns>
        public ORSet<T> ApplyDelta(ORSet<T> delta)
        {
            return Merge(delta);
        }

        /// <summary>
        /// Applies a delta state to this set in place.
        /// </summary>
        /// <param name="delta">The delta state to apply.</param>
        public void ApplyDeltaInPlace(ORSet<T> delta)
        {
            MergeInPlace(delta);
        }

        /// <summary>
        /// Compacts the set by removing tombstoned tags from elements.
        /// This is an optimization that can be done periodically to reduce memory.
        /// </summary>
        public void Compact()
        {
            var elementsToRemove = new List<T>();

            foreach (var (element, tags) in _elements)
            {
                // Remove tombstoned tags
                tags.RemoveWhere(t => _tombstones.ContainsKey(t));

                // If no tags remain, mark element for removal
                if (tags.Count == 0)
                {
                    elementsToRemove.Add(element);
                }
            }

            // Remove elements with no tags
            foreach (var element in elementsToRemove)
            {
                _elements.TryRemove(element, out _);
            }
        }

        /// <summary>
        /// Creates a deep copy of this set.
        /// </summary>
        /// <returns>A new OR-Set with the same state.</returns>
        public ORSet<T> Clone()
        {
            return new ORSet<T>(
                _nodeId,
                _elements.ToDictionary(kv => kv.Key, kv => new HashSet<UniqueTag>(kv.Value)),
                _tombstones.Keys,
                _tagCounter);
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
                tagCounter = _tagCounter,
                elements = _elements.ToDictionary(
                    kv => JsonSerializer.Serialize(kv.Key),
                    kv => kv.Value.Select(t => t.ToJson()).ToList()),
                tombstones = _tombstones.Keys.Select(t => t.ToJson()).ToList()
            });
        }

        /// <summary>
        /// Deserializes an OR-Set from JSON.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>A new OR-Set instance.</returns>
        public static ORSet<T> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var nodeId = doc.RootElement.GetProperty("nodeId").GetString() ?? "unknown";
            var tagCounter = doc.RootElement.GetProperty("tagCounter").GetInt64();

            var elements = new Dictionary<T, HashSet<UniqueTag>>();
            if (doc.RootElement.TryGetProperty("elements", out var elementsElement))
            {
                foreach (var prop in elementsElement.EnumerateObject())
                {
                    var element = JsonSerializer.Deserialize<T>(prop.Name);
                    if (element != null)
                    {
                        var tags = new HashSet<UniqueTag>();
                        foreach (var tagJson in prop.Value.EnumerateArray())
                        {
                            tags.Add(UniqueTag.FromJson(tagJson.GetString() ?? ""));
                        }
                        elements[element] = tags;
                    }
                }
            }

            var tombstones = new List<UniqueTag>();
            if (doc.RootElement.TryGetProperty("tombstones", out var tombstonesArray))
            {
                foreach (var tagJson in tombstonesArray.EnumerateArray())
                {
                    tombstones.Add(UniqueTag.FromJson(tagJson.GetString() ?? ""));
                }
            }

            return new ORSet<T>(nodeId, elements, tombstones, tagCounter);
        }

        /// <summary>
        /// Serializes the set to a byte array.
        /// </summary>
        /// <returns>Byte array representation.</returns>
        public byte[] ToBytes()
        {
            return System.Text.Encoding.UTF8.GetBytes(ToJson());
        }

        /// <summary>
        /// Deserializes an OR-Set from a byte array.
        /// </summary>
        /// <param name="bytes">Byte array representation.</param>
        /// <returns>A new OR-Set instance.</returns>
        public static ORSet<T> FromBytes(byte[] bytes)
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
            return $"ORSet({Count} elements, {TombstoneCount} tombstones: [{preview}])";
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is ORSet<T> other && Equals(other);
        }

        /// <summary>
        /// Determines whether this set equals another (based on visible elements).
        /// </summary>
        /// <param name="other">The other set to compare.</param>
        /// <returns>True if the sets have the same visible elements.</returns>
        public bool Equals(ORSet<T>? other)
        {
            if (other is null)
                return false;

            var thisElements = Elements;
            var otherElements = other.Elements;

            return thisElements.SetEquals(otherElements);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var element in Elements.OrderBy(e => e?.GetHashCode() ?? 0))
            {
                hash.Add(element);
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Unique tag for identifying add operations in OR-Set.
    /// </summary>
    public readonly struct UniqueTag : IEquatable<UniqueTag>
    {
        /// <summary>
        /// Node ID that created this tag.
        /// </summary>
        public string NodeId { get; }

        /// <summary>
        /// Counter value unique to this node.
        /// </summary>
        public long Counter { get; }

        /// <summary>
        /// Timestamp when the tag was created.
        /// </summary>
        public long Timestamp { get; }

        /// <summary>
        /// Creates a new unique tag.
        /// </summary>
        public UniqueTag(string nodeId, long counter, long timestamp)
        {
            NodeId = nodeId ?? "";
            Counter = counter;
            Timestamp = timestamp;
        }

        /// <inheritdoc />
        public bool Equals(UniqueTag other)
        {
            return NodeId == other.NodeId &&
                   Counter == other.Counter &&
                   Timestamp == other.Timestamp;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            return obj is UniqueTag other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(NodeId, Counter, Timestamp);
        }

        /// <summary>
        /// Serializes the tag to a string.
        /// </summary>
        public string ToJson()
        {
            return $"{NodeId}:{Counter}:{Timestamp}";
        }

        /// <summary>
        /// Deserializes a tag from a string.
        /// </summary>
        public static UniqueTag FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return default;

            var parts = json.Split(':');
            if (parts.Length != 3)
                return default;

            return new UniqueTag(
                parts[0],
                long.TryParse(parts[1], out var counter) ? counter : 0,
                long.TryParse(parts[2], out var timestamp) ? timestamp : 0);
        }

        /// <inheritdoc />
        public override string ToString() => ToJson();

        /// <summary>Equality operator.</summary>
        public static bool operator ==(UniqueTag left, UniqueTag right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(UniqueTag left, UniqueTag right) => !left.Equals(right);
    }

    /// <summary>
    /// Non-generic OR-Set that stores string elements.
    /// Convenience class for common use cases.
    /// </summary>
    public sealed class ORSet : ORSet<string>
    {
        /// <summary>
        /// Creates a new empty OR-Set for the specified node.
        /// </summary>
        /// <param name="nodeId">The local node identifier.</param>
        public ORSet(string nodeId) : base(nodeId)
        {
        }
    }
}
