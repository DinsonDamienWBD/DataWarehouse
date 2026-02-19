using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Base interface for Conflict-free Replicated Data Types (CRDTs).
    /// All CRDT merge operations are commutative, associative, and idempotent.
    /// Uses non-generic interface for runtime dispatch from CrdtRegistry.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT types")]
    internal interface ICrdtType
    {
        /// <summary>
        /// Serializes the CRDT state to a byte array for network transport.
        /// </summary>
        byte[] Serialize();

        /// <summary>
        /// Merges this CRDT with another instance, producing a new merged state.
        /// The merge operation is commutative, associative, and idempotent.
        /// </summary>
        ICrdtType Merge(ICrdtType other);
    }

    /// <summary>
    /// Grow-only counter CRDT (Shapiro et al. 2011).
    /// Each node increments its own counter. Value is the sum of all per-node counters.
    /// Merge uses Math.Max per node (NOT sum) to ensure idempotency.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT types")]
    internal sealed class SdkGCounter : ICrdtType
    {
        private readonly ConcurrentDictionary<string, long> _counts = new();

        /// <summary>
        /// The total counter value (sum of all per-node counts).
        /// </summary>
        public long Value => _counts.Values.Sum();

        /// <summary>
        /// Increments the counter for the specified node.
        /// </summary>
        public void Increment(string nodeId, long amount = 1)
        {
            _counts.AddOrUpdate(nodeId, amount, (_, existing) => existing + amount);
        }

        /// <summary>
        /// Merges two GCounters by taking Math.Max per node (idempotent merge).
        /// </summary>
        public ICrdtType Merge(ICrdtType other)
        {
            if (other is not SdkGCounter otherCounter)
                throw new ArgumentException("Cannot merge with non-GCounter type.", nameof(other));

            var result = new SdkGCounter();

            // Copy this side
            foreach (var (key, value) in _counts)
            {
                result._counts[key] = value;
            }

            // Merge other side using Math.Max (NOT sum -- Pitfall 3)
            foreach (var (key, value) in otherCounter._counts)
            {
                result._counts.AddOrUpdate(key, value, (_, existing) => Math.Max(existing, value));
            }

            return result;
        }

        /// <inheritdoc />
        public byte[] Serialize()
        {
            var dict = _counts.ToDictionary(kv => kv.Key, kv => kv.Value);
            return JsonSerializer.SerializeToUtf8Bytes(dict);
        }

        /// <summary>
        /// Deserializes a GCounter from a byte array.
        /// </summary>
        public static SdkGCounter Deserialize(byte[] data)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, long>>(data)
                ?? new Dictionary<string, long>();

            var counter = new SdkGCounter();
            foreach (var (key, value) in dict)
            {
                counter._counts[key] = value;
            }
            return counter;
        }

        internal IReadOnlyDictionary<string, long> GetCounts() =>
            _counts.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Positive-Negative counter CRDT (supports both increment and decrement).
    /// Implemented as two GCounters: one for increments, one for decrements.
    /// Value = positive.Value - negative.Value.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT types")]
    internal sealed class SdkPNCounter : ICrdtType
    {
        private SdkGCounter _positive = new();
        private SdkGCounter _negative = new();

        /// <summary>
        /// The net counter value (increments minus decrements).
        /// </summary>
        public long Value => _positive.Value - _negative.Value;

        /// <summary>
        /// Increments the counter for the specified node.
        /// </summary>
        public void Increment(string nodeId, long amount = 1)
        {
            _positive.Increment(nodeId, amount);
        }

        /// <summary>
        /// Decrements the counter for the specified node.
        /// </summary>
        public void Decrement(string nodeId, long amount = 1)
        {
            _negative.Increment(nodeId, amount);
        }

        /// <summary>
        /// Merges two PNCounters by merging both internal GCounters independently.
        /// </summary>
        public ICrdtType Merge(ICrdtType other)
        {
            if (other is not SdkPNCounter otherCounter)
                throw new ArgumentException("Cannot merge with non-PNCounter type.", nameof(other));

            var result = new SdkPNCounter
            {
                _positive = (SdkGCounter)_positive.Merge(otherCounter._positive),
                _negative = (SdkGCounter)_negative.Merge(otherCounter._negative)
            };

            return result;
        }

        /// <inheritdoc />
        public byte[] Serialize()
        {
            var wrapper = new PNCounterData
            {
                P = _positive.Serialize(),
                N = _negative.Serialize()
            };
            return JsonSerializer.SerializeToUtf8Bytes(wrapper);
        }

        /// <summary>
        /// Deserializes a PNCounter from a byte array.
        /// </summary>
        public static SdkPNCounter Deserialize(byte[] data)
        {
            var wrapper = JsonSerializer.Deserialize<PNCounterData>(data) ?? new PNCounterData();

            var result = new SdkPNCounter
            {
                _positive = wrapper.P != null ? SdkGCounter.Deserialize(wrapper.P) : new SdkGCounter(),
                _negative = wrapper.N != null ? SdkGCounter.Deserialize(wrapper.N) : new SdkGCounter()
            };

            return result;
        }

        private sealed class PNCounterData
        {
            [JsonPropertyName("p")]
            public byte[]? P { get; set; }

            [JsonPropertyName("n")]
            public byte[]? N { get; set; }
        }
    }

    /// <summary>
    /// Last-Writer-Wins Register CRDT.
    /// Stores a single value with a timestamp. On merge, the value with the higher
    /// timestamp wins. Equal timestamps use NodeId string comparison as deterministic tiebreaker.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT types")]
    internal sealed class SdkLWWRegister : ICrdtType
    {
        /// <summary>
        /// Maximum allowed clock skew for incoming timestamps. Timestamps more than this amount
        /// in the future are rejected to prevent DateTimeOffset.MaxValue poisoning attacks (DIST-08 mitigation).
        /// </summary>
        private static readonly TimeSpan MaxTimestampSkew = TimeSpan.FromHours(1);

        /// <summary>
        /// The current register value.
        /// </summary>
        public byte[] Value { get; private set; } = Array.Empty<byte>();

        /// <summary>
        /// Timestamp of the last write.
        /// </summary>
        public DateTimeOffset Timestamp { get; private set; }

        /// <summary>
        /// Node that performed the last write (tiebreaker).
        /// </summary>
        public string NodeId { get; private set; } = string.Empty;

        /// <summary>
        /// Sets the register value with the current timestamp.
        /// </summary>
        public void Set(byte[] value, string nodeId)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            Timestamp = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Merges two LWW registers. Higher timestamp wins; equal timestamps use NodeId comparison.
        /// DIST-08: Rejects timestamps more than 1 hour in the future to prevent MaxValue poisoning.
        /// </summary>
        public ICrdtType Merge(ICrdtType other)
        {
            if (other is not SdkLWWRegister otherRegister)
                throw new ArgumentException("Cannot merge with non-LWWRegister type.", nameof(other));

            // DIST-08: Reject timestamps too far in the future (prevents DateTimeOffset.MaxValue poisoning)
            if (otherRegister.Timestamp > DateTimeOffset.UtcNow + MaxTimestampSkew)
            {
                return this; // Keep current value -- incoming timestamp is suspicious
            }

            if (otherRegister.Timestamp > Timestamp)
            {
                return otherRegister;
            }

            if (otherRegister.Timestamp == Timestamp)
            {
                // Deterministic tiebreaker: higher NodeId wins
                if (string.Compare(otherRegister.NodeId, NodeId, StringComparison.Ordinal) > 0)
                {
                    return otherRegister;
                }
            }

            return this;
        }

        /// <inheritdoc />
        public byte[] Serialize()
        {
            var wrapper = new LWWRegisterData
            {
                Value = Convert.ToBase64String(Value),
                Timestamp = Timestamp,
                NodeId = NodeId
            };
            return JsonSerializer.SerializeToUtf8Bytes(wrapper);
        }

        /// <summary>
        /// Deserializes a LWWRegister from a byte array.
        /// </summary>
        public static SdkLWWRegister Deserialize(byte[] data)
        {
            var wrapper = JsonSerializer.Deserialize<LWWRegisterData>(data) ?? new LWWRegisterData();

            return new SdkLWWRegister
            {
                Value = wrapper.Value != null ? Convert.FromBase64String(wrapper.Value) : Array.Empty<byte>(),
                Timestamp = wrapper.Timestamp,
                NodeId = wrapper.NodeId ?? string.Empty
            };
        }

        private sealed class LWWRegisterData
        {
            [JsonPropertyName("value")]
            public string? Value { get; set; }

            [JsonPropertyName("timestamp")]
            public DateTimeOffset Timestamp { get; set; }

            [JsonPropertyName("nodeId")]
            public string? NodeId { get; set; }
        }
    }

    /// <summary>
    /// Observed-Remove Set CRDT (OR-Set).
    /// Supports concurrent add and remove operations with observed-remove semantics.
    /// Each add generates a unique tag; remove only removes currently-observed tags.
    /// An element is present if it has tags in the add-set that are not in the remove-set.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: CRDT types")]
    internal sealed class SdkORSet : ICrdtType
    {
        private readonly ConcurrentDictionary<string, HashSet<string>> _addSet = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _removeSet = new();
        internal readonly ConcurrentDictionary<string, DateTimeOffset> _removeTimestamps = new();

        /// <summary>
        /// Gets the currently-observed elements (those with add tags not in remove set).
        /// </summary>
        public IReadOnlySet<string> Elements
        {
            get
            {
                var result = new HashSet<string>();
                foreach (var (element, addTags) in _addSet)
                {
                    var removedTags = _removeSet.GetValueOrDefault(element);
                    if (removedTags == null || addTags.Except(removedTags).Any())
                    {
                        result.Add(element);
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Adds an element to the set with a unique tag.
        /// </summary>
        public void Add(string element, string nodeId)
        {
            string tag = $"{nodeId}:{Guid.NewGuid():N}";
            var tags = _addSet.GetOrAdd(element, _ => new HashSet<string>());
            lock (tags)
            {
                tags.Add(tag);
            }
        }

        /// <summary>
        /// Removes an element by moving all its current add-tags to the remove-set.
        /// Only removes tags that have been observed (observed-remove semantics).
        /// </summary>
        public void Remove(string element)
        {
            if (_addSet.TryGetValue(element, out var addTags))
            {
                var removedTags = _removeSet.GetOrAdd(element, _ => new HashSet<string>());
                var now = DateTimeOffset.UtcNow;
                lock (addTags)
                {
                    lock (removedTags)
                    {
                        foreach (var tag in addTags)
                        {
                            removedTags.Add(tag);
                            var key = $"{element}:{tag}";
                            _removeTimestamps.TryAdd(key, now);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Merges two OR-Sets by unioning both add-sets and remove-sets.
        /// </summary>
        public ICrdtType Merge(ICrdtType other)
        {
            if (other is not SdkORSet otherSet)
                throw new ArgumentException("Cannot merge with non-ORSet type.", nameof(other));

            var result = new SdkORSet();

            // Union add-sets
            MergeSets(_addSet, result._addSet);
            MergeSets(otherSet._addSet, result._addSet);

            // Union remove-sets
            MergeSets(_removeSet, result._removeSet);
            MergeSets(otherSet._removeSet, result._removeSet);

            // Merge remove timestamps (earliest known removal wins -- conservative)
            MergeTimestamps(_removeTimestamps, result._removeTimestamps);
            MergeTimestamps(otherSet._removeTimestamps, result._removeTimestamps);

            return result;
        }

        private static void MergeSets(
            ConcurrentDictionary<string, HashSet<string>> source,
            ConcurrentDictionary<string, HashSet<string>> target)
        {
            foreach (var (element, tags) in source)
            {
                var targetTags = target.GetOrAdd(element, _ => new HashSet<string>());
                lock (tags)
                {
                    lock (targetTags)
                    {
                        targetTags.UnionWith(tags);
                    }
                }
            }
        }

        private static void MergeTimestamps(
            ConcurrentDictionary<string, DateTimeOffset> source,
            ConcurrentDictionary<string, DateTimeOffset> target)
        {
            foreach (var (key, timestamp) in source)
            {
                target.AddOrUpdate(key, timestamp, (_, existing) =>
                    timestamp < existing ? timestamp : existing);
            }
        }

        /// <summary>
        /// Gets the total count of all tags across all elements in the remove-set (tombstone count).
        /// </summary>
        internal int TombstoneCount
        {
            get
            {
                int count = 0;
                foreach (var (_, tags) in _removeSet)
                {
                    lock (tags)
                    {
                        count += tags.Count;
                    }
                }
                return count;
            }
        }

        /// <summary>
        /// Removes an element entirely from both add-set and remove-set, cleaning up timestamps.
        /// Used by <see cref="OrSetPruner"/> for fully-removed elements whose tombstones are causally stable.
        /// </summary>
        internal void PruneElement(string element)
        {
            if (_addSet.TryRemove(element, out var addTags))
            {
                lock (addTags)
                {
                    foreach (var tag in addTags)
                    {
                        _removeTimestamps.TryRemove($"{element}:{tag}", out _);
                    }
                }
            }

            if (_removeSet.TryRemove(element, out var removeTags))
            {
                lock (removeTags)
                {
                    foreach (var tag in removeTags)
                    {
                        _removeTimestamps.TryRemove($"{element}:{tag}", out _);
                    }
                }
            }
        }

        /// <summary>
        /// Compacts an element's add-set to a single surviving tag and clears its remove-set.
        /// Used by <see cref="OrSetPruner"/> to reduce per-element tag count from O(writes) to O(1).
        /// </summary>
        internal void CompactElement(string element, string survivingTag)
        {
            // Replace the add-set with just the surviving tag
            if (_addSet.TryGetValue(element, out var addTags))
            {
                lock (addTags)
                {
                    addTags.Clear();
                    addTags.Add(survivingTag);
                }
            }

            // Clear the remove-set for this element
            if (_removeSet.TryRemove(element, out var removeTags))
            {
                lock (removeTags)
                {
                    foreach (var tag in removeTags)
                    {
                        _removeTimestamps.TryRemove($"{element}:{tag}", out _);
                    }
                }
            }
        }

        /// <summary>
        /// Gets all elements that have entries in either the add-set or remove-set.
        /// Used by <see cref="OrSetPruner"/> to enumerate elements for pruning.
        /// </summary>
        internal IReadOnlyCollection<string> GetAllElements()
        {
            var elements = new HashSet<string>(_addSet.Keys);
            foreach (var key in _removeSet.Keys)
            {
                elements.Add(key);
            }
            return elements;
        }

        /// <summary>
        /// Gets the add-set tags for a specific element. Returns null if element has no add-set entries.
        /// </summary>
        internal IReadOnlyCollection<string>? GetAddTags(string element)
        {
            if (_addSet.TryGetValue(element, out var tags))
            {
                lock (tags)
                {
                    return tags.ToList();
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the remove-set tags for a specific element. Returns null if element has no remove-set entries.
        /// </summary>
        internal IReadOnlyCollection<string>? GetRemoveTags(string element)
        {
            if (_removeSet.TryGetValue(element, out var tags))
            {
                lock (tags)
                {
                    return tags.ToList();
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the timestamp when a specific tag was added to the remove-set for an element.
        /// Returns <see cref="DateTimeOffset.MinValue"/> if no timestamp is recorded (backward compatibility).
        /// </summary>
        internal DateTimeOffset GetRemoveTimestamp(string element, string tag)
        {
            return _removeTimestamps.TryGetValue($"{element}:{tag}", out var timestamp)
                ? timestamp
                : DateTimeOffset.MinValue;
        }

        /// <inheritdoc />
        public byte[] Serialize()
        {
            var wrapper = new ORSetData
            {
                AddSet = SerializeTagSets(_addSet),
                RemoveSet = SerializeTagSets(_removeSet),
                RemoveTimestamps = SerializeTimestamps(_removeTimestamps)
            };
            return JsonSerializer.SerializeToUtf8Bytes(wrapper);
        }

        /// <summary>
        /// Deserializes an OR-Set from a byte array.
        /// Backward compatible: if removeTimestamps field is missing, all tombstones are
        /// treated as having <see cref="DateTimeOffset.MinValue"/> (always eligible for pruning).
        /// </summary>
        public static SdkORSet Deserialize(byte[] data)
        {
            var wrapper = JsonSerializer.Deserialize<ORSetData>(data) ?? new ORSetData();

            var result = new SdkORSet();
            DeserializeTagSets(wrapper.AddSet, result._addSet);
            DeserializeTagSets(wrapper.RemoveSet, result._removeSet);
            DeserializeTimestamps(wrapper.RemoveTimestamps, result._removeTimestamps);
            return result;
        }

        private static Dictionary<string, List<string>> SerializeTagSets(
            ConcurrentDictionary<string, HashSet<string>> sets)
        {
            var dict = new Dictionary<string, List<string>>();
            foreach (var (element, tags) in sets)
            {
                lock (tags)
                {
                    dict[element] = tags.ToList();
                }
            }
            return dict;
        }

        private static void DeserializeTagSets(
            Dictionary<string, List<string>>? source,
            ConcurrentDictionary<string, HashSet<string>> target)
        {
            if (source == null) return;
            foreach (var (element, tags) in source)
            {
                target[element] = new HashSet<string>(tags);
            }
        }

        private static Dictionary<string, string>? SerializeTimestamps(
            ConcurrentDictionary<string, DateTimeOffset> timestamps)
        {
            if (timestamps.IsEmpty) return null;
            var dict = new Dictionary<string, string>();
            foreach (var (key, value) in timestamps)
            {
                dict[key] = value.ToString("O");
            }
            return dict;
        }

        private static void DeserializeTimestamps(
            Dictionary<string, string>? source,
            ConcurrentDictionary<string, DateTimeOffset> target)
        {
            if (source == null) return;
            foreach (var (key, value) in source)
            {
                if (DateTimeOffset.TryParse(value, out var timestamp))
                {
                    target[key] = timestamp;
                }
            }
        }

        private sealed class ORSetData
        {
            [JsonPropertyName("addSet")]
            public Dictionary<string, List<string>>? AddSet { get; set; }

            [JsonPropertyName("removeSet")]
            public Dictionary<string, List<string>>? RemoveSet { get; set; }

            [JsonPropertyName("removeTimestamps")]
            public Dictionary<string, string>? RemoveTimestamps { get; set; }
        }
    }
}
