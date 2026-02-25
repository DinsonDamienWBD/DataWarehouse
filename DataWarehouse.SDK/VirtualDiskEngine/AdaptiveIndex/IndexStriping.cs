using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Index;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// N-way parallel index striping that distributes keys across multiple <see cref="IAdaptiveIndex"/> instances
/// for increased throughput. Each stripe is independently thread-safe, enabling true parallel reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// Key-to-stripe assignment uses XxHash64 for deterministic, uniform distribution. Point operations
/// (Lookup, Insert, Delete) route to a single stripe in O(1). Range queries fan out to all stripes
/// in parallel and merge-sort the results.
/// </para>
/// <para>
/// Analogous to RAID-0 for storage: striping multiplies throughput at the cost of no redundancy.
/// Combine with <see cref="IndexMirroring"/> for RAID-10-like configurations.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID - Striping")]
public sealed class IndexStriping : IAdaptiveIndex, IAsyncDisposable
{
    private readonly IAdaptiveIndex[] _stripes;

    /// <summary>
    /// Initializes a new striped index with N stripes created by the provided factory.
    /// </summary>
    /// <param name="stripeCount">Number of stripes (must be >= 2).</param>
    /// <param name="stripeFactory">Factory that creates an index for a given stripe ordinal.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if stripeCount is less than 2.</exception>
    /// <exception cref="ArgumentNullException">Thrown if stripeFactory is null.</exception>
    public IndexStriping(int stripeCount, Func<int, IAdaptiveIndex> stripeFactory)
    {
        ArgumentNullException.ThrowIfNull(stripeFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(stripeCount, 2);

        _stripes = new IAdaptiveIndex[stripeCount];
        for (int i = 0; i < stripeCount; i++)
        {
            _stripes[i] = stripeFactory(i) ?? throw new InvalidOperationException(
                $"Stripe factory returned null for stripe {i}.");
        }
    }

    /// <summary>
    /// Gets the number of stripes.
    /// </summary>
    public int StripeCount => _stripes.Length;

    /// <summary>
    /// Returns the recommended stripe count based on available processor cores.
    /// </summary>
    /// <returns>A stripe count capped at 16 and floored at 2.</returns>
    public static int RecommendStripeCount() => Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <inheritdoc />
    public MorphLevel CurrentLevel => _stripes[0].CurrentLevel;

    /// <inheritdoc />
    public long ObjectCount
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _stripes.Length; i++)
            {
                total += _stripes[i].ObjectCount;
            }
            return total;
        }
    }

    /// <inheritdoc />
    public long RootBlockNumber => -1;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        int stripe = GetStripeIndex(key);
        return _stripes[stripe].LookupAsync(key, ct);
    }

    /// <inheritdoc />
    public Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        int stripe = GetStripeIndex(key);
        return _stripes[stripe].InsertAsync(key, value, ct);
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        int stripe = GetStripeIndex(key);
        return _stripes[stripe].UpdateAsync(key, newValue, ct);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        int stripe = GetStripeIndex(key);
        return _stripes[stripe].DeleteAsync(key, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey, byte[]? endKey, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Fan out to all stripes in parallel, collect results, merge-sort by key
        var tasks = new Task<List<(byte[] Key, long Value)>>[_stripes.Length];
        for (int i = 0; i < _stripes.Length; i++)
        {
            int idx = i;
            tasks[i] = CollectRangeAsync(_stripes[idx], startKey, endKey, ct);
        }

        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Merge-sort using a priority queue (min-heap by key)
        var heap = new SortedSet<(int ListIndex, int ItemIndex, byte[] Key, long Value)>(
            Comparer<(int ListIndex, int ItemIndex, byte[] Key, long Value)>.Create((a, b) =>
            {
                int cmp = CompareKeys(a.Key, b.Key);
                return cmp != 0 ? cmp : a.ListIndex.CompareTo(b.ListIndex);
            }));

        // Seed the heap with the first element from each non-empty list
        for (int i = 0; i < allResults.Length; i++)
        {
            if (allResults[i].Count > 0)
            {
                var item = allResults[i][0];
                heap.Add((i, 0, item.Key, item.Value));
            }
        }

        while (heap.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var min = heap.Min;
            heap.Remove(min);

            yield return (min.Key, min.Value);

            int nextIdx = min.ItemIndex + 1;
            if (nextIdx < allResults[min.ListIndex].Count)
            {
                var next = allResults[min.ListIndex][nextIdx];
                heap.Add((min.ListIndex, nextIdx, next.Key, next.Value));
            }
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var tasks = new Task<long>[_stripes.Length];
        for (int i = 0; i < _stripes.Length; i++)
        {
            tasks[i] = _stripes[i].CountAsync(ct);
        }

        var counts = await Task.WhenAll(tasks).ConfigureAwait(false);
        long total = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += counts[i];
        }
        return total;
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        var tasks = new Task[_stripes.Length];
        for (int i = 0; i < _stripes.Length; i++)
        {
            tasks[i] = _stripes[i].MorphToAsync(targetLevel, ct);
        }
        return Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
        => _stripes[0].RecommendLevelAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        for (int i = 0; i < _stripes.Length; i++)
        {
            if (_stripes[i] is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_stripes[i] is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetStripeIndex(byte[] key)
    {
        ulong hash = XxHash64.HashToUInt64(key);
        return (int)(hash % (ulong)_stripes.Length);
    }

    private static async Task<List<(byte[] Key, long Value)>> CollectRangeAsync(
        IAdaptiveIndex index, byte[]? startKey, byte[]? endKey, CancellationToken ct)
    {
        var results = new List<(byte[] Key, long Value)>();
        await foreach (var item in index.RangeQueryAsync(startKey, endKey, ct).ConfigureAwait(false))
        {
            results.Add(item);
        }
        return results;
    }

    private static int CompareKeys(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }
}
