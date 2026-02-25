using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace DataWarehouse.SDK.Primitives.Probabilistic;

/// <summary>
/// Production-ready Top-K Heavy Hitters tracker using Space-Saving algorithm.
/// Tracks the K most frequent items in a stream with guaranteed error bounds.
/// Supports merging for distributed scenarios (T85.4, T85.7).
/// </summary>
/// <remarks>
/// Properties:
/// - Guaranteed: All items with frequency > n/k are tracked
/// - Space complexity: O(k)
/// - No false negatives for items with frequency > n/k
/// - May include items with frequency slightly below threshold
///
/// Use cases:
/// - Trending topics detection
/// - Popular product identification
/// - Hot key detection in caches
/// - DDoS attack source identification
///
/// Thread-safety: NOT thread-safe. Use external synchronization for concurrent access.
/// </remarks>
public sealed class TopKHeavyHitters<T> : IProbabilisticStructure, IMergeable<TopKHeavyHitters<T>> where T : notnull
{
    private readonly int _k;
    private readonly Dictionary<T, Counter> _counters;
    private readonly SortedSet<Counter> _sortedCounters;
    private readonly Func<T, byte[]> _serializer;
    private long _totalCount;

    /// <inheritdoc/>
    public string StructureType => "TopKHeavyHitters";

    /// <inheritdoc/>
    public long MemoryUsageBytes => _k * 64 + 128; // Approximate

    /// <inheritdoc/>
    public double ConfiguredErrorRate => 1.0 / _k;

    /// <inheritdoc/>
    public long ItemCount => _totalCount;

    /// <summary>
    /// Gets the maximum number of items tracked (K).
    /// </summary>
    public int K => _k;

    /// <summary>
    /// Gets the total count of all items added.
    /// </summary>
    public long TotalCount => _totalCount;

    /// <summary>
    /// Gets the current number of tracked items.
    /// </summary>
    public int TrackedCount => _counters.Count;

    /// <summary>
    /// Gets the minimum frequency threshold for guaranteed inclusion.
    /// Items with frequency above this are guaranteed to be in the top-K.
    /// </summary>
    public long GuaranteedThreshold => _totalCount / _k;

    /// <summary>
    /// Creates a Top-K Heavy Hitters tracker.
    /// </summary>
    /// <param name="k">Number of top items to track. Higher K = more accurate, more memory.</param>
    /// <param name="serializer">Optional custom serializer for type T.</param>
    public TopKHeavyHitters(int k = 100, Func<T, byte[]>? serializer = null)
    {
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "K must be at least 1.");

        _k = k;
        _counters = new Dictionary<T, Counter>(k);
        _sortedCounters = new SortedSet<Counter>(Comparer<Counter>.Create((a, b) =>
        {
            var cmp = a.Count.CompareTo(b.Count);
            return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
        }));
        _serializer = serializer ?? (item => Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Creates a Top-K Heavy Hitters from configuration.
    /// </summary>
    public TopKHeavyHitters(ProbabilisticConfig config, Func<T, byte[]>? serializer = null)
        : this((int)(1.0 / config.RelativeError), serializer)
    {
    }

    /// <summary>
    /// Adds an item to the tracker (increment count by 1).
    /// </summary>
    /// <param name="item">Item to add.</param>
    public void Add(T item)
    {
        Add(item, 1);
    }

    /// <summary>
    /// Adds an item with specified count.
    /// </summary>
    /// <param name="item">Item to add.</param>
    /// <param name="count">Count to add (must be positive).</param>
    public void Add(T item, long count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");

        _totalCount += count;

        if (_counters.TryGetValue(item, out var counter))
        {
            // Item already tracked - increment count
            _sortedCounters.Remove(counter);
            counter.Count += count;
            _sortedCounters.Add(counter);
        }
        else if (_counters.Count < _k)
        {
            // Room for new item
            var newCounter = new Counter { Item = item, Count = count, Error = 0, Id = _counters.Count };
            _counters[item] = newCounter;
            _sortedCounters.Add(newCounter);
        }
        else
        {
            // Replace minimum counter (Space-Saving algorithm)
            var minCounter = _sortedCounters.Min!;
            _sortedCounters.Remove(minCounter);
            _counters.Remove(minCounter.Item);

            // New item inherits min count as error bound
            var newCounter = new Counter
            {
                Item = item,
                Count = minCounter.Count + count,
                Error = minCounter.Count,
                Id = minCounter.Id
            };
            _counters[item] = newCounter;
            _sortedCounters.Add(newCounter);
        }
    }

    /// <summary>
    /// Gets the estimated count for an item.
    /// </summary>
    /// <param name="item">Item to query.</param>
    /// <returns>Estimated count (0 if not tracked).</returns>
    public long Estimate(T item)
    {
        return _counters.TryGetValue(item, out var counter) ? counter.Count : 0;
    }

    /// <summary>
    /// Gets the estimated count with probabilistic result metadata.
    /// </summary>
    public ProbabilisticResult<long> Query(T item)
    {
        if (_counters.TryGetValue(item, out var counter))
        {
            return new ProbabilisticResult<long>
            {
                Value = counter.Count,
                LowerBound = counter.Count - counter.Error,
                UpperBound = counter.Count,
                Confidence = 1.0,
                SourceStructure = StructureType
            };
        }

        return new ProbabilisticResult<long>
        {
            Value = 0,
            LowerBound = 0,
            UpperBound = _totalCount / _k, // Max possible error
            Confidence = 1.0,
            SourceStructure = StructureType
        };
    }

    /// <summary>
    /// Gets the top-K items sorted by estimated frequency.
    /// </summary>
    /// <param name="count">Number of items to return (default: all K).</param>
    /// <returns>Items with estimated frequencies and error bounds.</returns>
    public IEnumerable<(T Item, long Count, long MinCount, long MaxCount)> GetTopK(int? count = null)
    {
        return _sortedCounters
            .Reverse()
            .Take(count ?? _k)
            .Select(c => (c.Item, c.Count, MinCount: c.Count - c.Error, MaxCount: c.Count));
    }

    /// <summary>
    /// Gets items guaranteed to be heavy hitters (frequency > n/k).
    /// </summary>
    public IEnumerable<(T Item, long Count)> GetGuaranteedHeavyHitters()
    {
        var threshold = GuaranteedThreshold;
        return _sortedCounters
            .Where(c => c.Count - c.Error > threshold)
            .Select(c => (c.Item, c.Count))
            .OrderByDescending(x => x.Count);
    }

    /// <summary>
    /// Checks if an item is currently being tracked.
    /// </summary>
    public bool IsTracked(T item)
    {
        return _counters.ContainsKey(item);
    }

    /// <inheritdoc/>
    public void Merge(TopKHeavyHitters<T> other)
    {
        foreach (var counter in other._sortedCounters)
        {
            Add(counter.Item, counter.Count);
        }
    }

    /// <inheritdoc/>
    public TopKHeavyHitters<T> Clone()
    {
        var clone = new TopKHeavyHitters<T>(_k, _serializer);
        foreach (var counter in _sortedCounters.Reverse())
        {
            if (clone._counters.Count < _k)
            {
                var newCounter = new Counter
                {
                    Item = counter.Item,
                    Count = counter.Count,
                    Error = counter.Error,
                    Id = clone._counters.Count
                };
                clone._counters[counter.Item] = newCounter;
                clone._sortedCounters.Add(newCounter);
            }
        }
        clone._totalCount = _totalCount;
        return clone;
    }

    /// <inheritdoc/>
    public byte[] Serialize()
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);

        writer.Write(_k);
        writer.Write(_totalCount);
        writer.Write(_counters.Count);

        foreach (var counter in _sortedCounters)
        {
            var itemBytes = _serializer(counter.Item);
            writer.Write(itemBytes.Length);
            writer.Write(itemBytes);
            writer.Write(counter.Count);
            writer.Write(counter.Error);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a Top-K Heavy Hitters from bytes.
    /// Note: Requires a deserializer function since T is generic.
    /// </summary>
    public static TopKHeavyHitters<T> Deserialize(byte[] data, Func<byte[], T> deserializer, Func<T, byte[]>? serializer = null)
    {
        using var ms = new System.IO.MemoryStream(data);
        using var reader = new System.IO.BinaryReader(ms);

        var k = reader.ReadInt32();
        var totalCount = reader.ReadInt64();
        var count = reader.ReadInt32();

        var tracker = new TopKHeavyHitters<T>(k, serializer);
        tracker._totalCount = totalCount;

        for (int i = 0; i < count; i++)
        {
            var itemLen = reader.ReadInt32();
            var itemBytes = reader.ReadBytes(itemLen);
            var item = deserializer(itemBytes);
            var cnt = reader.ReadInt64();
            var error = reader.ReadInt64();

            var counter = new Counter { Item = item, Count = cnt, Error = error, Id = i };
            tracker._counters[item] = counter;
            tracker._sortedCounters.Add(counter);
        }

        return tracker;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _counters.Clear();
        _sortedCounters.Clear();
        _totalCount = 0;
    }

    private class Counter
    {
        public T Item { get; set; } = default!;
        public long Count { get; set; }
        public long Error { get; set; }
        public int Id { get; set; } // For stable sorting
    }
}
