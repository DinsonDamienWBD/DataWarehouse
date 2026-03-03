using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Core shard split engine that bisects a data shard's key range at the midpoint,
/// atomically updates the index catalog and routing table, and redistributes data
/// in the background using copy-on-write semantics.
/// </summary>
/// <remarks>
/// <para>The split algorithm:</para>
/// <list type="number">
/// <item><description>Validate the shard has at least 2 slots (otherwise cannot bisect).</description></item>
/// <item><description>Compute midpoint: <c>StartSlot + (EndSlot - StartSlot) / 2</c>.</description></item>
/// <item><description>Create lower-half descriptor reusing original VDE ID for [StartSlot, midSlot).</description></item>
/// <item><description>Consult <see cref="PlacementPolicyEngine"/> for upper-half tier placement.</description></item>
/// <item><description>Create a new VDE data shard for the upper half via the shard creator callback.</description></item>
/// <item><description>Atomically update <see cref="IndexShardCatalog"/>: remove original, add both halves.</description></item>
/// <item><description>Update <see cref="RoutingTable"/> slot assignments for both halves.</description></item>
/// <item><description>Redistribute data via the CoW data redistributor callback.</description></item>
/// <item><description>Update the upper-half UsedBlocks count in the index catalog.</description></item>
/// </list>
/// <para>
/// Thread safety: <see cref="SplitAsync"/> is NOT concurrent-safe for the same shard.
/// Callers must serialize splits per shard (e.g., via a per-shard semaphore at the orchestrator level).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-02)")]
public sealed class ShardSplitEngine
{
    private readonly PlacementPolicyEngine _placementEngine;
    private readonly Func<Guid, DataShardDescriptor, CancellationToken, ValueTask<Guid>> _shardCreator;
    private readonly Func<Guid, Guid, int, int, CancellationToken, ValueTask<long>> _dataRedistributor;

    /// <summary>
    /// Creates a new shard split engine.
    /// </summary>
    /// <param name="placementEngine">
    /// Engine that computes tier-aware placement for the newly created shard.
    /// </param>
    /// <param name="shardCreator">
    /// Callback that creates a new empty VDE data shard. Takes the parent shard ID and
    /// a descriptor template, returns the new VDE ID.
    /// </param>
    /// <param name="dataRedistributor">
    /// Callback that copies blocks from a source shard to a target shard for a given slot range
    /// [startSlot, endSlot). Returns the number of blocks copied. This is the CoW background copy.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    public ShardSplitEngine(
        PlacementPolicyEngine placementEngine,
        Func<Guid, DataShardDescriptor, CancellationToken, ValueTask<Guid>> shardCreator,
        Func<Guid, Guid, int, int, CancellationToken, ValueTask<long>> dataRedistributor)
    {
        ArgumentNullException.ThrowIfNull(placementEngine);
        ArgumentNullException.ThrowIfNull(shardCreator);
        ArgumentNullException.ThrowIfNull(dataRedistributor);

        _placementEngine = placementEngine;
        _shardCreator = shardCreator;
        _dataRedistributor = dataRedistributor;
    }

    /// <summary>
    /// Splits a data shard by bisecting its key range at the midpoint, atomically updating
    /// the index catalog and routing table, and redistributing data to the new upper-half shard.
    /// </summary>
    /// <param name="shard">The data shard to split. Must have at least 2 slots.</param>
    /// <param name="indexCatalog">The index shard catalog to update atomically.</param>
    /// <param name="routingTable">The routing table whose slot assignments are updated.</param>
    /// <param name="newShardTier">The storage tier for the newly created upper-half shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ShardSplitResult"/> describing the outcome. On success, both halves and
    /// the placement decision are populated. On failure, the original shard is restored.
    /// </returns>
    public async ValueTask<ShardSplitResult> SplitAsync(
        DataShardDescriptor shard,
        IndexShardCatalog indexCatalog,
        RoutingTable routingTable,
        StorageTier newShardTier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexCatalog);
        ArgumentNullException.ThrowIfNull(routingTable);

        // 1. Validate slot range is wide enough to bisect
        int slotRange = shard.EndSlot - shard.StartSlot;
        if (slotRange < 2)
        {
            return ShardSplitResult.Skipped(shard, "Slot range too narrow to bisect");
        }

        // 2. Start timing
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 3. Bisect the key range
            int midSlot = shard.StartSlot + slotRange / 2;

            // 4. Create lower half descriptor (reuses original VDE ID)
            var lowerHalf = new DataShardDescriptor
            {
                DataVdeId = shard.DataVdeId,
                StartSlot = shard.StartSlot,
                EndSlot = midSlot,
                CapacityBlocks = shard.CapacityBlocks,
                UsedBlocks = 0, // updated after redistribution accounting
                Health = DataShardHealth.Healthy,
                LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks
            };

            // 5. Compute placement for the new upper shard
            var placement = await _placementEngine.ComputePlacementAsync(shard, newShardTier, ct)
                .ConfigureAwait(false);

            // 6. Create the upper half VDE via callback
            var upperTemplate = new DataShardDescriptor
            {
                StartSlot = midSlot,
                EndSlot = shard.EndSlot,
                CapacityBlocks = shard.CapacityBlocks,
                UsedBlocks = 0,
                Health = DataShardHealth.Healthy,
                LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks
            };

            Guid newVdeId = await _shardCreator(shard.DataVdeId, upperTemplate, ct)
                .ConfigureAwait(false);

            // 7. Build upper half descriptor with the allocated VDE ID
            var upperHalf = new DataShardDescriptor
            {
                DataVdeId = newVdeId,
                StartSlot = midSlot,
                EndSlot = shard.EndSlot,
                CapacityBlocks = shard.CapacityBlocks,
                UsedBlocks = 0,
                Health = DataShardHealth.Healthy,
                LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks
            };

            // 8. Atomic index update with rollback on failure
            bool indexUpdated = false;
            bool lowerAdded = false;
            bool upperAdded = false;

            try
            {
                indexCatalog.RemoveDataShard(shard.DataVdeId);
                indexUpdated = true;

                indexCatalog.AddDataShard(lowerHalf);
                lowerAdded = true;

                indexCatalog.AddDataShard(upperHalf);
                upperAdded = true;
            }
            catch (Exception ex)
            {
                // Rollback: remove whatever was added, restore original
                if (upperAdded)
                    indexCatalog.RemoveDataShard(upperHalf.DataVdeId);
                if (lowerAdded)
                    indexCatalog.RemoveDataShard(lowerHalf.DataVdeId);
                if (indexUpdated)
                    indexCatalog.AddDataShard(shard);

                stopwatch.Stop();
                return new ShardSplitResult
                {
                    Status = SplitStatus.Failed,
                    OriginalShard = shard,
                    LowerHalf = null,
                    UpperHalf = null,
                    NewShardPlacement = null,
                    BlocksRedistributed = 0,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    FailureReason = $"Index update failed: {ex.Message}"
                };
            }

            // 9. Routing table update
            routingTable.AssignRange(lowerHalf.StartSlot, lowerHalf.EndSlot, lowerHalf.DataVdeId);
            routingTable.AssignRange(upperHalf.StartSlot, upperHalf.EndSlot, upperHalf.DataVdeId);

            // 10. Background data redistribution (CoW copy of blocks in the upper slot range)
            long blocksMoved = await _dataRedistributor(
                shard.DataVdeId, newVdeId, midSlot, shard.EndSlot, ct)
                .ConfigureAwait(false);

            // 11. Update upper half UsedBlocks in the index catalog
            upperHalf = upperHalf with { UsedBlocks = blocksMoved };
            indexCatalog.UpdateDataShard(upperHalf);

            // 12. Return completed result
            stopwatch.Stop();
            return new ShardSplitResult
            {
                Status = SplitStatus.Completed,
                OriginalShard = shard,
                LowerHalf = lowerHalf,
                UpperHalf = upperHalf,
                NewShardPlacement = placement,
                BlocksRedistributed = blocksMoved,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                FailureReason = null
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ShardSplitResult
            {
                Status = SplitStatus.Failed,
                OriginalShard = shard,
                LowerHalf = null,
                UpperHalf = null,
                NewShardPlacement = null,
                BlocksRedistributed = 0,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                FailureReason = ex.Message
            };
        }
    }
}
