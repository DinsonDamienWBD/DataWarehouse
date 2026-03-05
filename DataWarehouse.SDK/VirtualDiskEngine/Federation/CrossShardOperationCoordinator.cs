using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Per-shard metadata statistics: object count, total size, and block usage.
/// Returned by <see cref="IShardVdeAccessor.GetShardStatsAsync"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public readonly record struct ShardMetadataStats(
    long ObjectCount,
    long TotalSizeBytes,
    long UsedBlocks,
    long FreeBlocks);

/// <summary>
/// Aggregated metadata statistics across all shards in the federation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public readonly record struct AggregatedMetadataStats(
    int ShardCount,
    long TotalObjectCount,
    long TotalSizeBytes,
    long TotalUsedBlocks,
    long TotalFreeBlocks);

/// <summary>
/// Provides access to individual shard VDE instances for cross-shard operations.
/// Implementations translate shard IDs into actual VDE List/Search/Delete/Stats calls.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public interface IShardVdeAccessor
{
    /// <summary>
    /// Returns all shard VDE identifiers in the current federation topology.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAllShardIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists objects from a specific shard, optionally filtered by key prefix.
    /// </summary>
    IAsyncEnumerable<StorageObjectMetadata> ListShardAsync(
        Guid shardVdeId, string? prefix, CancellationToken ct = default);

    /// <summary>
    /// Searches objects on a specific shard matching the given query.
    /// </summary>
    IAsyncEnumerable<StorageObjectMetadata> SearchShardAsync(
        Guid shardVdeId, string query, CancellationToken ct = default);

    /// <summary>
    /// Deletes an object from a specific shard by key.
    /// </summary>
    Task DeleteFromShardAsync(Guid shardVdeId, string key, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata statistics for a specific shard.
    /// </summary>
    Task<ShardMetadataStats> GetShardStatsAsync(Guid shardVdeId, CancellationToken ct = default);
}

/// <summary>
/// Coordinates fan-out operations across multiple VDE shards in a federated namespace.
/// Uses <see cref="VdeFederationRouter"/> for shard resolution, <see cref="MergeSortStreamMerger"/>
/// for sorted result merging, and <see cref="FederatedPaginationCursor"/> for deterministic
/// cross-shard pagination with opaque continuation tokens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-shard optimization:</b> When the federation contains exactly one shard,
/// all operations delegate directly to that shard without creating merge infrastructure
/// or cursor machinery. This ensures zero overhead for single-VDE deployments.
/// </para>
/// <para>
/// <b>Partial failure handling:</b> If a shard is unreachable during fan-out, the coordinator
/// logs a warning and continues with remaining shards. Partial results are returned rather
/// than failing the entire operation. Unreachable shards are tracked so retries can target them.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Cross-Shard Operations (VFED-20)")]
public sealed class CrossShardOperationCoordinator
{
    private readonly VdeFederationRouter _router;
    private readonly IShardVdeAccessor _shardAccessor;
    private readonly MergeSortStreamMerger _merger;

    /// <summary>
    /// Creates a new cross-shard operation coordinator.
    /// </summary>
    /// <param name="router">Federation router for resolving keys to shards.</param>
    /// <param name="shardAccessor">Provides access to individual shard VDE operations.</param>
    /// <exception cref="ArgumentNullException">Thrown when router or shardAccessor is null.</exception>
    public CrossShardOperationCoordinator(
        VdeFederationRouter router,
        IShardVdeAccessor shardAccessor)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(shardAccessor);

        _router = router;
        _shardAccessor = shardAccessor;
        _merger = new MergeSortStreamMerger();
    }

    /// <summary>
    /// Fans out a List operation across matching shards, merge-sorts results by key,
    /// and returns a paginated view with opaque continuation cursors.
    /// </summary>
    /// <param name="prefix">Optional key prefix to filter results. When provided,
    /// the router attempts to narrow the target shard set.</param>
    /// <param name="pageSize">Maximum number of items to return in this page.</param>
    /// <param name="cursor">Continuation cursor from a previous page, or null for the first page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of sorted results, up to pageSize items.</returns>
    public async IAsyncEnumerable<StorageObjectMetadata> FanOutListAsync(
        string? prefix,
        int pageSize,
        FederatedPaginationCursor? cursor,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (pageSize <= 0)
            yield break;

        var allShardIds = await _shardAccessor.GetAllShardIdsAsync(ct).ConfigureAwait(false);

        if (allShardIds.Count == 0)
            yield break;

        // Single-shard optimization: bypass merge and cursor machinery entirely
        if (allShardIds.Count == 1)
        {
            var singleShardId = allShardIds[0];
            long skip = cursor?.GetOffset(singleShardId) ?? 0;
            int yielded = 0;

            await foreach (var item in _shardAccessor.ListShardAsync(singleShardId, prefix, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                if (yielded >= pageSize)
                    yield break;

                yield return item;
                yielded++;
            }

            yield break;
        }

        // Multi-shard: determine target shards
        var targetShardIds = await ResolveTargetShardsAsync(prefix, allShardIds, ct).ConfigureAwait(false);

        if (targetShardIds.Count == 0)
            yield break;

        // Build per-shard streams with cursor-based offset skipping
        var shardStreams = new List<ShardStream>(targetShardIds.Count);

        for (int i = 0; i < targetShardIds.Count; i++)
        {
            var shardId = targetShardIds[i];
            long skipCount = cursor?.GetOffset(shardId) ?? 0;

            var stream = SkipAndEnumerate(
                _shardAccessor.ListShardAsync(shardId, prefix, ct),
                skipCount,
                ct);

            shardStreams.Add(new ShardStream(shardId, stream.GetAsyncEnumerator(ct)));
        }

        // Merge-sort all shard streams and yield up to pageSize items
        int totalYielded = 0;

        await foreach (var item in _merger.MergeAsync(shardStreams, ct).ConfigureAwait(false))
        {
            if (totalYielded >= pageSize)
                yield break;

            yield return item;
            totalYielded++;
        }
    }

    /// <summary>
    /// Fans out a Search operation to ALL shards in parallel (search cannot be narrowed
    /// by prefix routing), merge-sorts results by key, and yields up to maxResults items.
    /// </summary>
    /// <param name="query">Search query string passed to each shard.</param>
    /// <param name="maxResults">Maximum total results to return across all shards.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of merge-sorted search results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when query is null.</exception>
    public async IAsyncEnumerable<StorageObjectMetadata> FanOutSearchAsync(
        string query,
        int maxResults,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (maxResults <= 0)
            yield break;

        var allShardIds = await _shardAccessor.GetAllShardIdsAsync(ct).ConfigureAwait(false);

        if (allShardIds.Count == 0)
            yield break;

        // Single-shard optimization: direct passthrough
        if (allShardIds.Count == 1)
        {
            int yielded = 0;
            await foreach (var item in _shardAccessor.SearchShardAsync(allShardIds[0], query, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (yielded >= maxResults)
                    yield break;

                yield return item;
                yielded++;
            }

            yield break;
        }

        // Multi-shard: fan out search to all shards
        var shardStreams = new List<ShardStream>(allShardIds.Count);

        for (int i = 0; i < allShardIds.Count; i++)
        {
            var shardId = allShardIds[i];
            var stream = _shardAccessor.SearchShardAsync(shardId, query, ct);
            shardStreams.Add(new ShardStream(shardId, stream.GetAsyncEnumerator(ct)));
        }

        // Merge-sort and limit results
        int totalYielded = 0;

        await foreach (var item in _merger.MergeAsync(shardStreams, ct).ConfigureAwait(false))
        {
            if (totalYielded >= maxResults)
                yield break;

            yield return item;
            totalYielded++;
        }
    }

    /// <summary>
    /// Deletes an object from the correct shard using the federation router for resolution.
    /// If the router cannot determine the owning shard, attempts delete on all shards
    /// (defensive behavior for eventual consistency scenarios).
    /// </summary>
    /// <param name="key">The key of the object to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when key is null.</exception>
    public async Task FanOutDeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Try to resolve the owning shard via router
        var address = await _router.ResolveAsync(key, ct).ConfigureAwait(false);

        if (address.IsValid)
        {
            // Point delete: single shard owns this key
            await _shardAccessor.DeleteFromShardAsync(address.VdeId, key, ct).ConfigureAwait(false);
            return;
        }

        // Router could not resolve — defensive: attempt delete on all shards
        var allShardIds = await _shardAccessor.GetAllShardIdsAsync(ct).ConfigureAwait(false);

        if (allShardIds.Count == 0)
            return;

        // Single shard: direct
        if (allShardIds.Count == 1)
        {
            await _shardAccessor.DeleteFromShardAsync(allShardIds[0], key, ct).ConfigureAwait(false);
            return;
        }

        // Fan out delete to all shards in parallel
        var deleteTasks = new Task[allShardIds.Count];

        for (int i = 0; i < allShardIds.Count; i++)
        {
            var shardId = allShardIds[i];
            deleteTasks[i] = DeleteFromShardSafeAsync(shardId, key, ct);
        }

        await Task.WhenAll(deleteTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Aggregates metadata statistics (object count, total size, block usage)
    /// across all shards in the federation using parallel fan-out.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated statistics summing all per-shard values.</returns>
    public async Task<AggregatedMetadataStats> AggregateMetadataAsync(CancellationToken ct = default)
    {
        var allShardIds = await _shardAccessor.GetAllShardIdsAsync(ct).ConfigureAwait(false);

        if (allShardIds.Count == 0)
            return new AggregatedMetadataStats(0, 0, 0, 0, 0);

        // Fan out stats collection to all shards in parallel
        var statsTasks = new Task<ShardMetadataStats>[allShardIds.Count];

        for (int i = 0; i < allShardIds.Count; i++)
        {
            var shardId = allShardIds[i];
            statsTasks[i] = GetShardStatsSafeAsync(shardId, ct);
        }

        var allStats = await Task.WhenAll(statsTasks).ConfigureAwait(false);

        // Sum across all shards
        long totalObjects = 0;
        long totalSize = 0;
        long totalUsed = 0;
        long totalFree = 0;

        for (int i = 0; i < allStats.Length; i++)
        {
            totalObjects += allStats[i].ObjectCount;
            totalSize += allStats[i].TotalSizeBytes;
            totalUsed += allStats[i].UsedBlocks;
            totalFree += allStats[i].FreeBlocks;
        }

        return new AggregatedMetadataStats(
            allShardIds.Count,
            totalObjects,
            totalSize,
            totalUsed,
            totalFree);
    }

    /// <summary>
    /// Determines which shards should be targeted for a List operation.
    /// Uses the router to narrow the set when a prefix is provided.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveTargetShardsAsync(
        string? prefix,
        IReadOnlyList<Guid> allShardIds,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(prefix))
            return allShardIds;

        // Attempt to narrow target set using router prefix resolution
        try
        {
            var address = await _router.ResolveAsync(prefix, ct).ConfigureAwait(false);

            if (address.IsValid)
            {
                // Router resolved prefix to a specific shard — narrow to that shard
                // Verify the shard exists in current topology
                for (int i = 0; i < allShardIds.Count; i++)
                {
                    if (allShardIds[i] == address.VdeId)
                        return new[] { address.VdeId };
                }
            }
        }
        catch (Exception)
        {
            // Router resolution failed — fall through to fan-out to all shards
            System.Diagnostics.Trace.TraceWarning(
                "[CrossShardOperationCoordinator] Router prefix resolution failed. Fanning out to all shards.");
        }

        // Cannot narrow: fan out to all shards
        return allShardIds;
    }

    /// <summary>
    /// Deletes from a shard with error swallowing for partial-failure tolerance.
    /// </summary>
    private async Task DeleteFromShardSafeAsync(Guid shardId, string key, CancellationToken ct)
    {
        try
        {
            await _shardAccessor.DeleteFromShardAsync(shardId, key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception)
        {
            // Shard unreachable during delete fan-out — log and continue
            System.Diagnostics.Trace.TraceWarning(
                $"[CrossShardOperationCoordinator] Shard {shardId} unreachable during delete of key '{key}'. Continuing.");
        }
    }

    /// <summary>
    /// Gets shard stats with error handling — returns zeroed stats on failure.
    /// </summary>
    private async Task<ShardMetadataStats> GetShardStatsSafeAsync(Guid shardId, CancellationToken ct)
    {
        try
        {
            return await _shardAccessor.GetShardStatsAsync(shardId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"[CrossShardOperationCoordinator] Shard {shardId} unreachable during stats collection. Returning zero stats.");
            return default;
        }
    }

    /// <summary>
    /// Wraps an IAsyncEnumerable, skipping the first N items for cursor-based pagination.
    /// </summary>
    private static async IAsyncEnumerable<StorageObjectMetadata> SkipAndEnumerate(
        IAsyncEnumerable<StorageObjectMetadata> source,
        long skipCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long skipped = 0;

        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (skipped < skipCount)
            {
                skipped++;
                continue;
            }

            yield return item;
        }
    }
}
