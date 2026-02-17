using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Threading;
using System.Threading.Tasks;

using SyncResult = DataWarehouse.SDK.Contracts.Distributed.SyncResult;
using SyncConflict = DataWarehouse.SDK.Contracts.Distributed.SyncConflict;
using ConflictResolutionResult = DataWarehouse.SDK.Contracts.Distributed.ConflictResolutionResult;
using ConflictResolutionStrategy = DataWarehouse.SDK.Contracts.Distributed.ConflictResolutionStrategy;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IReplicationSync"/>.
    /// In single-node mode there is no replication target, so sync always reports
    /// success with zero items synced and conflicts always resolve to LocalWins.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryReplicationSync : IReplicationSync
    {
        /// <inheritdoc />
        public event Action<SyncEvent>? OnSyncEvent;

        /// <inheritdoc />
        public SyncMode CurrentMode => SyncMode.Offline;

        /// <inheritdoc />
        public Task<SyncResult> SyncAsync(SyncRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SyncResult.Ok(0, 0, TimeSpan.Zero));
        }

        /// <inheritdoc />
        public Task<SyncStatus> GetSyncStatusAsync(string targetNodeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new SyncStatus
            {
                TargetNodeId = targetNodeId,
                State = SyncState.Completed,
                ProgressPercent = 100.0,
                LastSyncAt = DateTimeOffset.UtcNow
            });
        }

        /// <inheritdoc />
        public Task<ConflictResolutionResult> ResolveConflictAsync(SyncConflict conflict, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ConflictResolutionResult
            {
                Key = conflict.Key,
                Strategy = ConflictResolutionStrategy.LocalWins,
                ResolvedValue = conflict.LocalValue
            });
        }
    }
}
