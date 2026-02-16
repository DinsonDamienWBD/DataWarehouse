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
        /// </summary>
        public ICrdtType Merge(ICrdtType other)
        {
            if (other is not SdkLWWRegister otherRegister)
                throw new ArgumentException("Cannot merge with non-LWWRegister type.", nameof(other));

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
                lock (addTags)
                {
                    lock (removedTags)
                    {
                        foreach (var tag in addTags)
                        {
                            removedTags.Add(tag);
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

        /// <inheritdoc />
        public byte[] Serialize()
        {
            var wrapper = new ORSetData
            {
                AddSet = SerializeTagSets(_addSet),
                RemoveSet = SerializeTagSets(_removeSet)
            };
            return JsonSerializer.SerializeToUtf8Bytes(wrapper);
        }

        /// <summary>
        /// Deserializes an OR-Set from a byte array.
        /// </summary>
        public static SdkORSet Deserialize(byte[] data)
        {
            var wrapper = JsonSerializer.Deserialize<ORSetData>(data) ?? new ORSetData();

            var result = new SdkORSet();
            DeserializeTagSets(wrapper.AddSet, result._addSet);
            DeserializeTagSets(wrapper.RemoveSet, result._removeSet);
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

        private sealed class ORSetData
        {
            [JsonPropertyName("addSet")]
            public Dictionary<string, List<string>>? AddSet { get; set; }

            [JsonPropertyName("removeSet")]
            public Dictionary<string, List<string>>? RemoveSet { get; set; }
        }
    }
}
