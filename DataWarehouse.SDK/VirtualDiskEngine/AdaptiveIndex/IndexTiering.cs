using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Index;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Hot/warm/cold tiered index access that distributes entries across three tiers based on
/// access frequency, tracked by a count-min sketch.
/// </summary>
/// <remarks>
/// <para>
/// Tier layout:
/// <list type="bullet">
///   <item><description><b>L1 Hot</b>: ART index for frequently accessed entries (&lt;=1us lookup).</description></item>
///   <item><description><b>L2 Warm</b>: Be-tree for moderately accessed entries (&lt;=100us lookup).</description></item>
///   <item><description><b>L3 Cold</b>: Backing store for rarely accessed entries (&lt;=10ms lookup).</description></item>
/// </list>
/// </para>
/// <para>
/// Promotion occurs when access frequency exceeds the promotion threshold. Demotion occurs
/// when a tier exceeds its capacity limit. A background timer periodically scans L1 for
/// entries that have fallen below the frequency threshold and demotes them to L2.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID - Tiering")]
public sealed class IndexTiering : IAdaptiveIndex, IAsyncDisposable
{
    private readonly IAdaptiveIndex _l1Hot;
    private readonly IAdaptiveIndex _l2Warm;
    private readonly IAdaptiveIndex _l3Cold;
    private readonly CountMinSketch _accessSketch;
    private readonly int _promotionThreshold;
    private readonly long _l1MaxEntries;
    private readonly long _l2MaxEntries;
    private readonly SemaphoreSlim _tierLock = new(1, 1);
    private readonly Timer _backgroundTimer;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    /// <summary>
    /// Initializes a new tiered index with three tiers.
    /// </summary>
    /// <param name="l1Hot">The hot tier index (should be ART or similar fast in-memory structure).</param>
    /// <param name="l2Warm">The warm tier index (should be Be-tree or similar balanced structure).</param>
    /// <param name="l3Cold">The cold tier index (should be learned index or disk-backed).</param>
    /// <param name="promotionThreshold">Minimum access count to promote from L2/L3 to L1 (default 10).</param>
    /// <param name="l1MaxEntries">Maximum entries in L1 before demotion (default 100,000).</param>
    /// <param name="l2MaxEntries">Maximum entries in L2 before demotion to L3 (default 1,000,000).</param>
    public IndexTiering(
        IAdaptiveIndex l1Hot,
        IAdaptiveIndex l2Warm,
        IAdaptiveIndex l3Cold,
        int promotionThreshold = 10,
        long l1MaxEntries = 100_000,
        long l2MaxEntries = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(l1Hot);
        ArgumentNullException.ThrowIfNull(l2Warm);
        ArgumentNullException.ThrowIfNull(l3Cold);
        ArgumentOutOfRangeException.ThrowIfLessThan(promotionThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(l1MaxEntries, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(l2MaxEntries, 1L);

        _l1Hot = l1Hot;
        _l2Warm = l2Warm;
        _l3Cold = l3Cold;
        _accessSketch = new CountMinSketch();
        _promotionThreshold = promotionThreshold;
        _l1MaxEntries = l1MaxEntries;
        _l2MaxEntries = l2MaxEntries;

        // Background tier management every 30 seconds
        _backgroundTimer = new Timer(
            _ => _ = ManageTiersAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    /// <summary>Gets the L1 (hot) tier index.</summary>
    public IAdaptiveIndex L1Hot => _l1Hot;

    /// <summary>Gets the L2 (warm) tier index.</summary>
    public IAdaptiveIndex L2Warm => _l2Warm;

    /// <summary>Gets the L3 (cold) tier index.</summary>
    public IAdaptiveIndex L3Cold => _l3Cold;

    /// <inheritdoc />
    public MorphLevel CurrentLevel => _l2Warm.CurrentLevel;

    /// <inheritdoc />
    public long ObjectCount => _l1Hot.ObjectCount + _l2Warm.ObjectCount + _l3Cold.ObjectCount;

    /// <inheritdoc />
    public long RootBlockNumber => -1;

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is required by IAdaptiveIndex but only raised by AdaptiveIndexEngine
    public event Action<MorphLevel, MorphLevel>? LevelChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public async Task<long?> LookupAsync(byte[] key, CancellationToken ct = default)
    {
        _accessSketch.Increment(key);

        // Check L1 (hot) first
        var result = await _l1Hot.LookupAsync(key, ct).ConfigureAwait(false);
        if (result.HasValue) return result;

        // Check L2 (warm)
        result = await _l2Warm.LookupAsync(key, ct).ConfigureAwait(false);
        if (result.HasValue)
        {
            // Promote to L1 if access frequency exceeds threshold
            if (_accessSketch.Estimate(key) >= _promotionThreshold)
            {
                _ = PromoteAsync(key, result.Value, _l2Warm, _l1Hot, ct);
            }
            return result;
        }

        // Check L3 (cold)
        result = await _l3Cold.LookupAsync(key, ct).ConfigureAwait(false);
        if (result.HasValue)
        {
            // Promote to L1 if access frequency exceeds threshold
            if (_accessSketch.Estimate(key) >= _promotionThreshold)
            {
                _ = PromoteAsync(key, result.Value, _l3Cold, _l1Hot, ct);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task InsertAsync(byte[] key, long value, CancellationToken ct = default)
    {
        // New inserts go to L2 (warm) by default
        return _l2Warm.InsertAsync(key, value, ct);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(byte[] key, long newValue, CancellationToken ct = default)
    {
        // Try to update in whichever tier holds the key
        if (await _l1Hot.UpdateAsync(key, newValue, ct).ConfigureAwait(false)) return true;
        if (await _l2Warm.UpdateAsync(key, newValue, ct).ConfigureAwait(false)) return true;
        return await _l3Cold.UpdateAsync(key, newValue, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        // Try to delete from whichever tier holds the key
        if (await _l1Hot.DeleteAsync(key, ct).ConfigureAwait(false)) return true;
        if (await _l2Warm.DeleteAsync(key, ct).ConfigureAwait(false)) return true;
        return await _l3Cold.DeleteAsync(key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(byte[] Key, long Value)> RangeQueryAsync(
        byte[]? startKey, byte[]? endKey, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Collect from all three tiers and merge-sort
        var tasks = new[]
        {
            CollectRangeAsync(_l1Hot, startKey, endKey, ct),
            CollectRangeAsync(_l2Warm, startKey, endKey, ct),
            CollectRangeAsync(_l3Cold, startKey, endKey, ct)
        };

        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Merge-sort using a sorted set
        var heap = new SortedSet<(int ListIndex, int ItemIndex, byte[] Key, long Value)>(
            Comparer<(int ListIndex, int ItemIndex, byte[] Key, long Value)>.Create((a, b) =>
            {
                int cmp = CompareKeys(a.Key, b.Key);
                return cmp != 0 ? cmp : a.ListIndex.CompareTo(b.ListIndex);
            }));

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
        var t1 = _l1Hot.CountAsync(ct);
        var t2 = _l2Warm.CountAsync(ct);
        var t3 = _l3Cold.CountAsync(ct);
        await Task.WhenAll(t1, t2, t3).ConfigureAwait(false);
        return t1.Result + t2.Result + t3.Result;
    }

    /// <inheritdoc />
    public Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default)
    {
        // Morph all tiers
        return Task.WhenAll(
            _l1Hot.MorphToAsync(targetLevel, ct),
            _l2Warm.MorphToAsync(targetLevel, ct),
            _l3Cold.MorphToAsync(targetLevel, ct));
    }

    /// <inheritdoc />
    public Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default)
        => _l2Warm.RecommendLevelAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        await _backgroundTimer.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _tierLock.Dispose();

        foreach (var tier in new[] { _l1Hot, _l2Warm, _l3Cold })
        {
            if (tier is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (tier is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task PromoteAsync(byte[] key, long value, IAdaptiveIndex source, IAdaptiveIndex target, CancellationToken ct)
    {
        await _tierLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Check if already in target
            var existing = await target.LookupAsync(key, ct).ConfigureAwait(false);
            if (existing.HasValue) return;

            await target.InsertAsync(key, value, ct).ConfigureAwait(false);
            await source.DeleteAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IndexTiering.PromoteAsync] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _tierLock.Release();
        }
    }

    private async Task ManageTiersAsync()
    {
        if (_disposed != 0) return;

        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;

        try
        {
            // Decay access counters
            _accessSketch.Decay();

            // Demote L1 entries below frequency threshold when L1 is over capacity
            if (_l1Hot.ObjectCount > _l1MaxEntries)
            {
                await DemoteTierAsync(_l1Hot, _l2Warm, _l1MaxEntries, ct).ConfigureAwait(false);
            }

            // Demote L2 entries to L3 when L2 is over capacity
            if (_l2Warm.ObjectCount > _l2MaxEntries)
            {
                await DemoteTierAsync(_l2Warm, _l3Cold, _l2MaxEntries, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[IndexTiering.ManageTiersAsync] {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task DemoteTierAsync(IAdaptiveIndex source, IAdaptiveIndex target, long maxEntries, CancellationToken ct)
    {
        await _tierLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Collect entries to demote (least frequent)
            var candidates = new List<(byte[] Key, long Value, int Frequency)>();

            await foreach (var (key, value) in source.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
            {
                int freq = _accessSketch.Estimate(key);
                candidates.Add((key, value, freq));
            }

            // Sort by frequency ascending (least frequent first)
            candidates.Sort((a, b) => a.Frequency.CompareTo(b.Frequency));

            // Demote entries until we're under the limit
            long excess = source.ObjectCount - maxEntries;
            int demoted = 0;

            foreach (var (key, value, _) in candidates)
            {
                if (demoted >= excess) break;
                ct.ThrowIfCancellationRequested();

                try
                {
                    await target.InsertAsync(key, value, ct).ConfigureAwait(false);
                    await source.DeleteAsync(key, ct).ConfigureAwait(false);
                    demoted++;
                }
                catch (InvalidOperationException)
                {
                    // Key already exists in target, just remove from source
                    await target.UpdateAsync(key, value, ct).ConfigureAwait(false);
                    await source.DeleteAsync(key, ct).ConfigureAwait(false);
                    demoted++;
                }
            }
        }
        finally
        {
            _tierLock.Release();
        }
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

    /// <summary>
    /// Probabilistic data structure for approximate frequency counting.
    /// Uses 4 hash functions with 65536-wide counters and periodic decay.
    /// </summary>
    internal sealed class CountMinSketch
    {
        private const int Depth = 4;
        private const int Width = 65536;

        private readonly int[][] _counters;
        private readonly ulong[] _seeds = { 0UL, 0x9E3779B97F4A7C15UL, 0x517CC1B727220A95UL, 0x6C62272E07BB0142UL };

        /// <summary>
        /// Initializes a new count-min sketch with 4 hash functions and 65536-wide counters.
        /// </summary>
        public CountMinSketch()
        {
            _counters = new int[Depth][];
            for (int i = 0; i < Depth; i++)
            {
                _counters[i] = new int[Width];
            }
        }

        /// <summary>
        /// Increments the estimated count for the given key.
        /// </summary>
        /// <param name="key">The key to increment.</param>
        public void Increment(byte[] key)
        {
            for (int i = 0; i < Depth; i++)
            {
                int idx = GetIndex(key, i);
                Interlocked.Increment(ref _counters[i][idx]);
            }
        }

        /// <summary>
        /// Returns the estimated count for the given key (minimum of all hash positions).
        /// </summary>
        /// <param name="key">The key to estimate.</param>
        /// <returns>The estimated access count.</returns>
        public int Estimate(byte[] key)
        {
            int min = int.MaxValue;
            for (int i = 0; i < Depth; i++)
            {
                int idx = GetIndex(key, i);
                int val = Volatile.Read(ref _counters[i][idx]);
                if (val < min) min = val;
            }
            return min;
        }

        /// <summary>
        /// Halves all counters to prevent stale hot entries from persisting.
        /// Called periodically (every 60 seconds by default).
        /// </summary>
        public void Decay()
        {
            for (int i = 0; i < Depth; i++)
            {
                for (int j = 0; j < Width; j++)
                {
                    // Atomic halve using CAS loop
                    int current;
                    int halved;
                    do
                    {
                        current = Volatile.Read(ref _counters[i][j]);
                        halved = current >> 1;
                    } while (Interlocked.CompareExchange(ref _counters[i][j], halved, current) != current);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetIndex(byte[] key, int hashIndex)
        {
            ulong hash = XxHash64.HashToUInt64(key, (long)_seeds[hashIndex]);
            return (int)(hash % Width);
        }
    }
}
