using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Describes a single shard's async stream for merge operations.
/// Binds a shard VDE identifier to its enumerator so the merger
/// can track which shard produced each item.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public readonly record struct ShardStream(Guid ShardVdeId, IAsyncEnumerator<StorageObjectMetadata> Enumerator);

/// <summary>
/// K-way merge-sort engine for combining multiple per-shard
/// <see cref="IAsyncEnumerable{StorageObjectMetadata}"/> streams into a single
/// unified sorted output. Uses a min-heap (<see cref="PriorityQueue{TElement, TPriority}"/>)
/// for O(log K) per-element extraction where K is the number of active shards.
/// </summary>
/// <remarks>
/// <para>
/// Single-shard optimization: when only one source stream is provided, the merger
/// yields directly from that stream with zero heap allocation or comparison overhead.
/// </para>
/// <para>
/// All enumerators are disposed in the finally block, even on cancellation or exception.
/// Empty streams are skipped during initialization (no heap entry created).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public sealed class MergeSortStreamMerger
{
    private readonly IComparer<StorageObjectMetadata> _comparer;

    /// <summary>
    /// Creates a new merge-sort stream merger.
    /// </summary>
    /// <param name="comparer">
    /// Custom comparer for ordering results. When null, defaults to ordinal
    /// string comparison on <see cref="StorageObjectMetadata.Key"/>.
    /// </param>
    public MergeSortStreamMerger(IComparer<StorageObjectMetadata>? comparer = null)
    {
        _comparer = comparer ?? StorageObjectMetadataKeyComparer.Instance;
    }

    /// <summary>
    /// Performs K-way merge-sort over multiple shard streams, yielding items
    /// in sorted order according to the configured comparer.
    /// </summary>
    /// <param name="sources">The per-shard streams to merge.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A single unified sorted async enumerable.</returns>
    /// <exception cref="ArgumentNullException">Thrown when sources is null.</exception>
    public async IAsyncEnumerable<StorageObjectMetadata> MergeAsync(
        IReadOnlyList<ShardStream> sources,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
            yield break;

        // Single-source fast path: yield directly without heap allocation
        if (sources.Count == 1)
        {
            var single = sources[0].Enumerator;
            try
            {
                while (await single.MoveNextAsync().ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return single.Current;
                }
            }
            finally
            {
                await single.DisposeAsync().ConfigureAwait(false);
            }

            yield break;
        }

        // Multi-source K-way merge using min-heap
        var heap = new PriorityQueue<(StorageObjectMetadata Item, int SourceIndex), StorageObjectMetadata>(
            sources.Count, _comparer);

        // Track enumerators for disposal
        var enumerators = new IAsyncEnumerator<StorageObjectMetadata>?[sources.Count];

        try
        {
            // Initialize: advance each enumerator once, seed heap with first items
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var enumerator = sources[i].Enumerator;
                enumerators[i] = enumerator;

                try
                {
                    if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        heap.Enqueue((enumerator.Current, i), enumerator.Current);
                    }
                    // Empty stream: skip (enumerator still tracked for disposal)
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Partial failure during init: log warning, continue with remaining
                    System.Diagnostics.Trace.TraceWarning(
                        $"[MergeSortStreamMerger] Shard {sources[i].ShardVdeId} failed during initialization. Skipping.");
                }
            }

            // Main merge loop: extract min, yield, advance source, re-enqueue
            while (heap.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (item, sourceIndex) = heap.Dequeue();
                yield return item;

                var enumerator = enumerators[sourceIndex];
                if (enumerator is null)
                    continue;

                try
                {
                    if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        heap.Enqueue((enumerator.Current, sourceIndex), enumerator.Current);
                    }
                    // Exhausted: no re-enqueue, source drops out naturally
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Shard failed mid-stream: remove from merge, continue with remaining
                    System.Diagnostics.Trace.TraceWarning(
                        $"[MergeSortStreamMerger] Shard {sources[sourceIndex].ShardVdeId} failed mid-merge. Removed from merge.");
                    enumerators[sourceIndex] = null; // Prevent further advances
                }
            }
        }
        finally
        {
            // Dispose all enumerators (even on cancellation)
            for (int i = 0; i < enumerators.Length; i++)
            {
                if (enumerators[i] is not null)
                {
                    try
                    {
                        await enumerators[i]!.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore dispose errors — best effort cleanup
                    }
                }
            }
        }
    }

    /// <summary>
    /// Default comparer that sorts <see cref="StorageObjectMetadata"/> by Key
    /// using ordinal string comparison for consistent cross-platform ordering.
    /// </summary>
    private sealed class StorageObjectMetadataKeyComparer : IComparer<StorageObjectMetadata>
    {
        public static readonly StorageObjectMetadataKeyComparer Instance = new();

        public int Compare(StorageObjectMetadata? x, StorageObjectMetadata? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return string.Compare(x.Key, y.Key, StringComparison.Ordinal);
        }
    }
}
