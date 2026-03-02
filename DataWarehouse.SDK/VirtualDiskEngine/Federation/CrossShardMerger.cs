using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Target descriptor for cross-shard List operations.
/// Binds a shard VDE identifier to an async list function.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Merger (VFED-15)")]
public readonly record struct ShardOperationTarget(
    Guid ShardVdeId,
    Func<string?, CancellationToken, IAsyncEnumerable<StorageObjectMetadata>> ListFunc);

/// <summary>
/// Target descriptor for cross-shard Search operations.
/// Binds a shard VDE identifier to an async search function.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Merger (VFED-15)")]
public readonly record struct ShardSearchTarget(
    Guid ShardVdeId,
    Func<string, CancellationToken, IAsyncEnumerable<StorageObjectMetadata>> SearchFunc);

/// <summary>
/// Target descriptor for cross-shard Delete operations.
/// Binds a shard VDE identifier to an async delete function.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Merger (VFED-15)")]
public readonly record struct ShardDeleteTarget(
    Guid ShardVdeId,
    Func<string, CancellationToken, Task> DeleteFunc);

/// <summary>
/// Statistics snapshot from cross-shard merge operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Merger (VFED-15)")]
public readonly record struct CrossShardMergeStats(
    long TotalFanOuts,
    long PartialFailures,
    long TotalItemsMerged);

/// <summary>
/// Fan-out and merge engine for multi-shard List/Search/Delete operations.
/// Uses k-way merge-sort by key (lexicographic) with a priority queue for List/Search,
/// and targeted dispatch for Delete. Handles partial shard failures gracefully:
/// failed shards are removed from the merge and logged, not propagated as exceptions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Merger (VFED-15)")]
public sealed class CrossShardMerger
{
    private readonly FederationOptions _options;

    // Statistics (updated via Interlocked for thread safety)
    private long _totalFanOuts;
    private long _partialFailures;
    private long _totalItemsMerged;

    /// <summary>
    /// Creates a new cross-shard merger with the specified federation options.
    /// </summary>
    /// <param name="options">Federation configuration (timeouts, parallelism, limits).</param>
    /// <exception cref="ArgumentNullException">Thrown when options is null.</exception>
    public CrossShardMerger(FederationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Fans out a List operation to multiple shards and performs k-way merge-sort by Key (lexicographic).
    /// Each shard enumerator advances independently. When a shard is exhausted, it is removed from the queue.
    /// If a shard fails, a warning is logged and the remaining shards continue (partial results).
    /// </summary>
    /// <param name="prefix">Optional key prefix filter passed to each shard.</param>
    /// <param name="targets">List of shard targets to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of merge-sorted results. Caller can break to implement page boundaries.</returns>
    public async IAsyncEnumerable<StorageObjectMetadata> MergeListAsync(
        string? prefix,
        IReadOnlyList<ShardOperationTarget> targets,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
            yield break;

        Interlocked.Increment(ref _totalFanOuts);

        using var mergeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        mergeTimeoutCts.CancelAfter(_options.CrossShardMergeTimeout);
        var mergeCt = mergeTimeoutCts.Token;

        // Initialize enumerators for all shards (up to MaxConcurrentShardOperations)
        var enumerators = new IAsyncEnumerator<StorageObjectMetadata>?[targets.Count];
        var activeShard = new bool[targets.Count];

        // Priority queue: (item, shardIndex) keyed by item.Key for lexicographic ordering
        var pq = new PriorityQueue<(StorageObjectMetadata Item, int ShardIndex), string>(targets.Count);

        try
        {
            // Start all shard enumerators and seed the priority queue with first item from each
            using var semaphore = new SemaphoreSlim(_options.MaxConcurrentShardOperations);

            var initTasks = new Task[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                int shardIdx = i;
                initTasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(mergeCt).ConfigureAwait(false);
                    try
                    {
                        var enumerator = targets[shardIdx].ListFunc(prefix, mergeCt).GetAsyncEnumerator(mergeCt);
                        enumerators[shardIdx] = enumerator;

                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            activeShard[shardIdx] = true;
                            lock (pq)
                            {
                                pq.Enqueue((enumerator.Current, shardIdx), enumerator.Current.Key ?? string.Empty);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (mergeCt.IsCancellationRequested)
                    {
                        throw; // Propagate cancellation
                    }
                    catch (Exception)
                    {
                        // Partial failure: shard is unavailable, continue with remaining shards
                        Interlocked.Increment(ref _partialFailures);
                        System.Diagnostics.Trace.TraceWarning(
                            $"[CrossShardMerger] Shard {targets[shardIdx].ShardVdeId} failed during List fan-out. Continuing with remaining shards.");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, mergeCt);
            }

            await Task.WhenAll(initTasks).ConfigureAwait(false);

            // K-way merge: extract min from priority queue, advance that shard's enumerator
            while (pq.Count > 0)
            {
                mergeCt.ThrowIfCancellationRequested();

                var (item, shardIndex) = pq.Dequeue();
                Interlocked.Increment(ref _totalItemsMerged);
                yield return item;

                // Advance the shard enumerator that produced this item
                var enumerator = enumerators[shardIndex];
                if (enumerator != null)
                {
                    try
                    {
                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            pq.Enqueue((enumerator.Current, shardIndex), enumerator.Current.Key ?? string.Empty);
                        }
                        else
                        {
                            activeShard[shardIndex] = false;
                        }
                    }
                    catch (OperationCanceledException) when (mergeCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Shard failed mid-stream, remove from merge
                        Interlocked.Increment(ref _partialFailures);
                        activeShard[shardIndex] = false;
                        System.Diagnostics.Trace.TraceWarning(
                            $"[CrossShardMerger] Shard {targets[shardIndex].ShardVdeId} failed mid-List. Removed from merge.");
                    }
                }
            }
        }
        finally
        {
            // Dispose all enumerators
            for (int i = 0; i < enumerators.Length; i++)
            {
                if (enumerators[i] != null)
                {
                    try
                    {
                        await enumerators[i]!.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore dispose errors
                    }
                }
            }
        }
    }

    /// <summary>
    /// Fans out a Search operation to multiple shards and performs k-way merge-sort by Key (lexicographic).
    /// Yields at most <see cref="FederationOptions.SearchMaxResults"/> items total across all shards.
    /// </summary>
    /// <param name="query">Search query string passed to each shard.</param>
    /// <param name="targets">List of shard search targets.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of merge-sorted search results, capped at SearchMaxResults.</returns>
    public async IAsyncEnumerable<StorageObjectMetadata> MergeSearchAsync(
        string query,
        IReadOnlyList<ShardSearchTarget> targets,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
            yield break;

        Interlocked.Increment(ref _totalFanOuts);

        using var mergeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        mergeTimeoutCts.CancelAfter(_options.CrossShardMergeTimeout);
        var mergeCt = mergeTimeoutCts.Token;

        var enumerators = new IAsyncEnumerator<StorageObjectMetadata>?[targets.Count];
        var pq = new PriorityQueue<(StorageObjectMetadata Item, int ShardIndex), string>(targets.Count);
        int totalYielded = 0;

        try
        {
            // Initialize enumerators and seed priority queue
            using var semaphore = new SemaphoreSlim(_options.MaxConcurrentShardOperations);

            var initTasks = new Task[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                int shardIdx = i;
                initTasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(mergeCt).ConfigureAwait(false);
                    try
                    {
                        var enumerator = targets[shardIdx].SearchFunc(query, mergeCt).GetAsyncEnumerator(mergeCt);
                        enumerators[shardIdx] = enumerator;

                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            lock (pq)
                            {
                                pq.Enqueue((enumerator.Current, shardIdx), enumerator.Current.Key ?? string.Empty);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (mergeCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _partialFailures);
                        System.Diagnostics.Trace.TraceWarning(
                            $"[CrossShardMerger] Shard {targets[shardIdx].ShardVdeId} failed during Search fan-out. Continuing with remaining shards.");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, mergeCt);
            }

            await Task.WhenAll(initTasks).ConfigureAwait(false);

            // K-way merge with result count limit
            while (pq.Count > 0 && totalYielded < _options.SearchMaxResults)
            {
                mergeCt.ThrowIfCancellationRequested();

                var (item, shardIndex) = pq.Dequeue();
                Interlocked.Increment(ref _totalItemsMerged);
                totalYielded++;
                yield return item;

                if (totalYielded >= _options.SearchMaxResults)
                    break;

                // Advance the shard enumerator
                var enumerator = enumerators[shardIndex];
                if (enumerator != null)
                {
                    try
                    {
                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            pq.Enqueue((enumerator.Current, shardIndex), enumerator.Current.Key ?? string.Empty);
                        }
                    }
                    catch (OperationCanceledException) when (mergeCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _partialFailures);
                        System.Diagnostics.Trace.TraceWarning(
                            $"[CrossShardMerger] Shard {targets[shardIndex].ShardVdeId} failed mid-Search. Removed from merge.");
                    }
                }
            }
        }
        finally
        {
            for (int i = 0; i < enumerators.Length; i++)
            {
                if (enumerators[i] != null)
                {
                    try
                    {
                        await enumerators[i]!.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore dispose errors
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sends a delete operation to the resolved shard. The key has a single home shard
    /// (determined by the federation router), so this is not a true fan-out.
    /// Returns the count of shards that successfully processed the delete.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="targets">Resolved delete targets (typically 1 shard).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of shards that successfully processed the delete.</returns>
    public async Task<long> FanOutDeleteAsync(
        string key,
        IReadOnlyList<ShardDeleteTarget> targets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
            return 0;

        Interlocked.Increment(ref _totalFanOuts);

        long successCount = 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ShardOperationTimeout);
        var opCt = timeoutCts.Token;

        for (int i = 0; i < targets.Count; i++)
        {
            int retries = 0;
            bool succeeded = false;

            while (!succeeded && retries <= _options.MaxRetryPerShard)
            {
                try
                {
                    opCt.ThrowIfCancellationRequested();
                    await targets[i].DeleteFunc(key, opCt).ConfigureAwait(false);
                    Interlocked.Increment(ref successCount);
                    succeeded = true;
                }
                catch (OperationCanceledException) when (opCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    retries++;
                    if (retries > _options.MaxRetryPerShard)
                    {
                        Interlocked.Increment(ref _partialFailures);
                        System.Diagnostics.Trace.TraceWarning(
                            $"[CrossShardMerger] Shard {targets[i].ShardVdeId} failed Delete for key '{key}' after {_options.MaxRetryPerShard} retries.");
                    }
                }
            }
        }

        return successCount;
    }

    /// <summary>
    /// Returns a snapshot of cross-shard merge statistics.
    /// </summary>
    /// <returns>Current statistics snapshot.</returns>
    public CrossShardMergeStats GetStats()
    {
        return new CrossShardMergeStats(
            Interlocked.Read(ref _totalFanOuts),
            Interlocked.Read(ref _partialFailures),
            Interlocked.Read(ref _totalItemsMerged));
    }
}
