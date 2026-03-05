using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Merges adjacent underutilized shards into a single shard, consolidating the index catalog
/// and routing table, migrating data from the absorbed shard, and decommissioning the freed VDE.
/// </summary>
/// <remarks>
/// <para>
/// The merge algorithm always selects the lower-slot shard as the survivor (keeps its VDE ID)
/// and absorbs the upper-slot shard. This minimizes data movement when the lower shard typically
/// has more data due to hash distribution patterns.
/// </para>
/// <para>
/// Thread safety: callers must serialize merges that involve the same shard. Concurrent merges
/// on disjoint shard pairs are safe. The engine itself does not maintain internal state between calls.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-03)")]
public sealed class ShardMergeEngine
{
    private readonly Func<Guid, Guid, CancellationToken, ValueTask<long>> _dataMigrator;
    private readonly Func<Guid, CancellationToken, ValueTask> _shardDecommissioner;

    /// <summary>
    /// Creates a new shard merge engine with the specified data migration and decommission callbacks.
    /// </summary>
    /// <param name="dataMigrator">
    /// Migrates all data from source shard VDE (first Guid) to target shard VDE (second Guid).
    /// Returns the number of blocks migrated.
    /// </param>
    /// <param name="shardDecommissioner">
    /// Decommissions a shard VDE (cleanup, free resources). Takes the VDE ID to decommission.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dataMigrator"/> or <paramref name="shardDecommissioner"/> is null.
    /// </exception>
    public ShardMergeEngine(
        Func<Guid, Guid, CancellationToken, ValueTask<long>> dataMigrator,
        Func<Guid, CancellationToken, ValueTask> shardDecommissioner)
    {
        ArgumentNullException.ThrowIfNull(dataMigrator);
        ArgumentNullException.ThrowIfNull(shardDecommissioner);

        _dataMigrator = dataMigrator;
        _shardDecommissioner = shardDecommissioner;
    }

    /// <summary>
    /// Merges two adjacent shards into one. The lower-slot shard survives; the upper-slot shard
    /// is absorbed and its VDE decommissioned.
    /// </summary>
    /// <param name="lowerShard">The lower-slot shard descriptor (must have smaller StartSlot).</param>
    /// <param name="upperShard">The upper-slot shard descriptor (must be adjacent: lowerShard.EndSlot == upperShard.StartSlot).</param>
    /// <param name="indexCatalog">The index shard catalog to update atomically.</param>
    /// <param name="routingTable">The routing table to consolidate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ShardMergeResult"/> describing the outcome.</returns>
    public async ValueTask<ShardMergeResult> MergeAsync(
        DataShardDescriptor lowerShard,
        DataShardDescriptor upperShard,
        IndexShardCatalog indexCatalog,
        RoutingTable routingTable,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexCatalog);
        ArgumentNullException.ThrowIfNull(routingTable);

        // Step 1: Validate adjacency
        if (lowerShard.EndSlot != upperShard.StartSlot)
        {
            return ShardMergeResult.Skipped(lowerShard, upperShard,
                $"Shards are not adjacent: lower ends at {lowerShard.EndSlot}, upper starts at {upperShard.StartSlot}");
        }

        // Step 2: Validate order
        if (lowerShard.StartSlot >= upperShard.StartSlot)
        {
            return ShardMergeResult.Skipped(lowerShard, upperShard,
                "Shards are not in correct order");
        }

        // Step 3: Validate availability
        if (!lowerShard.IsAvailable || !upperShard.IsAvailable)
        {
            return ShardMergeResult.Skipped(lowerShard, upperShard,
                "One or both shards unavailable");
        }

        // Step 4: Start timing
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Step 5: Lower shard survives (keeps its VDE ID). Upper shard is absorbed.

            // Step 6: Build merged descriptor
            var mergedShard = new DataShardDescriptor
            {
                DataVdeId = lowerShard.DataVdeId,
                StartSlot = lowerShard.StartSlot,
                EndSlot = upperShard.EndSlot,
                CapacityBlocks = lowerShard.CapacityBlocks + upperShard.CapacityBlocks,
                UsedBlocks = lowerShard.UsedBlocks, // Updated after migration
                Health = DataShardHealth.Healthy,
                LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks
            };

            // Step 7: Migrate data from upper shard to lower shard
            long blocksMigrated = await _dataMigrator(upperShard.DataVdeId, lowerShard.DataVdeId, ct)
                .ConfigureAwait(false);

            mergedShard = mergedShard with { UsedBlocks = lowerShard.UsedBlocks + blocksMigrated };

            // Step 8: Atomic index update with rollback on failure
            bool upperRemoved = false;
            bool lowerRemoved = false;
            try
            {
                upperRemoved = indexCatalog.RemoveDataShard(upperShard.DataVdeId);
                lowerRemoved = indexCatalog.RemoveDataShard(lowerShard.DataVdeId);
                indexCatalog.AddDataShard(mergedShard);
            }
            catch (Exception ex)
            {
                // Rollback: attempt to restore original state
                try
                {
                    // Try removing the merged shard if it was added
                    indexCatalog.RemoveDataShard(mergedShard.DataVdeId);
                }
                catch
                {
                    // Best-effort rollback
                }

                try
                {
                    if (lowerRemoved)
                        indexCatalog.AddDataShard(lowerShard);
                }
                catch
                {
                    // Best-effort rollback
                }

                try
                {
                    if (upperRemoved)
                        indexCatalog.AddDataShard(upperShard);
                }
                catch
                {
                    // Best-effort rollback
                }

                stopwatch.Stop();
                return new ShardMergeResult
                {
                    Status = MergeStatus.Failed,
                    LowerShard = lowerShard,
                    UpperShard = upperShard,
                    MergedShard = mergedShard,
                    DecommissionedVdeId = Guid.Empty,
                    BlocksMigrated = blocksMigrated,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    FailureReason = $"Index catalog update failed: {ex.Message}"
                };
            }

            // Step 9: Routing table update -- assign full merged range to survivor
            routingTable.AssignRange(mergedShard.StartSlot, mergedShard.EndSlot, mergedShard.DataVdeId);

            // Step 10: Decommission absorbed shard
            await _shardDecommissioner(upperShard.DataVdeId, ct).ConfigureAwait(false);

            // Step 11: Complete
            stopwatch.Stop();
            return new ShardMergeResult
            {
                Status = MergeStatus.Completed,
                LowerShard = lowerShard,
                UpperShard = upperShard,
                MergedShard = mergedShard,
                DecommissionedVdeId = upperShard.DataVdeId,
                BlocksMigrated = blocksMigrated,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ShardMergeResult
            {
                Status = MergeStatus.Failed,
                LowerShard = lowerShard,
                UpperShard = upperShard,
                MergedShard = default,
                DecommissionedVdeId = Guid.Empty,
                BlocksMigrated = 0,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                FailureReason = ex.Message
            };
        }
    }

    /// <summary>
    /// Batch-processes a sorted list of merge candidates, merging adjacent pairs sequentially.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are expected pre-sorted by <see cref="DataShardDescriptor.StartSlot"/> (as returned
    /// by ShardCapacityMonitor.FindMergeCandidatesAsync). The method walks the list looking for
    /// adjacent pairs and merges them one at a time. After merging a pair, the absorbed shard is
    /// skipped in the scan.
    /// </para>
    /// <para>
    /// Merges are performed sequentially (not in parallel) because index catalog updates must be
    /// serialized to avoid corrupt state.
    /// </para>
    /// </remarks>
    /// <param name="candidates">Merge candidates sorted by StartSlot ascending.</param>
    /// <param name="indexCatalog">The index shard catalog to update.</param>
    /// <param name="routingTable">The routing table to consolidate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of merge results for each attempted pair.</returns>
    public async ValueTask<IReadOnlyList<ShardMergeResult>> MergeCandidatesAsync(
        IReadOnlyList<DataShardDescriptor> candidates,
        IndexShardCatalog indexCatalog,
        RoutingTable routingTable,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(indexCatalog);
        ArgumentNullException.ThrowIfNull(routingTable);

        var results = new List<ShardMergeResult>();

        if (candidates.Count < 2)
            return results;

        int i = 0;
        while (i < candidates.Count - 1)
        {
            ct.ThrowIfCancellationRequested();

            DataShardDescriptor current = candidates[i];
            DataShardDescriptor next = candidates[i + 1];

            // Check adjacency: current.EndSlot == next.StartSlot
            if (current.EndSlot == next.StartSlot)
            {
                ShardMergeResult result = await MergeAsync(current, next, indexCatalog, routingTable, ct)
                    .ConfigureAwait(false);
                results.Add(result);

                // Skip the absorbed shard (next) since it no longer exists
                i += 2;
            }
            else
            {
                // Not adjacent, move to next candidate
                i++;
            }
        }

        return results;
    }
}
