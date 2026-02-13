using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distributed
{
    /// <summary>
    /// Contract for replication synchronization supporting online/offline sync
    /// with conflict resolution (DIST-05).
    /// Complements existing <see cref="DataWarehouse.SDK.Replication.IMultiMasterReplication"/> with
    /// a unified sync contract supporting air-gapped and hybrid deployments.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public interface IReplicationSync
    {
        /// <summary>
        /// Raised when a sync event occurs.
        /// </summary>
        event Action<SyncEvent>? OnSyncEvent;

        /// <summary>
        /// Gets the current synchronization mode.
        /// </summary>
        SyncMode CurrentMode { get; }

        /// <summary>
        /// Synchronizes data with a target node.
        /// </summary>
        /// <param name="request">The sync request specifying target and parameters.</param>
        /// <param name="ct">Cancellation token for the sync operation.</param>
        /// <returns>The result of the synchronization.</returns>
        Task<SyncResult> SyncAsync(SyncRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gets the current sync status with a specific target node.
        /// </summary>
        /// <param name="targetNodeId">The target node to check sync status for.</param>
        /// <param name="ct">Cancellation token for the status check.</param>
        /// <returns>The current sync status.</returns>
        Task<SyncStatus> GetSyncStatusAsync(string targetNodeId, CancellationToken ct = default);

        /// <summary>
        /// Resolves a sync conflict using the specified strategy.
        /// </summary>
        /// <param name="conflict">The conflict to resolve.</param>
        /// <param name="ct">Cancellation token for the resolution operation.</param>
        /// <returns>The conflict resolution result.</returns>
        Task<ConflictResolutionResult> ResolveConflictAsync(SyncConflict conflict, CancellationToken ct = default);
    }

    /// <summary>
    /// Synchronization modes supported by IReplicationSync.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum SyncMode
    {
        /// <summary>Continuous online synchronization with real-time replication.</summary>
        Online,
        /// <summary>Offline mode -- sync happens via batch export/import.</summary>
        Offline,
        /// <summary>Air-gapped mode -- sync via physical media with cryptographic verification.</summary>
        AirGap,
        /// <summary>Hybrid mode -- online when connected, offline when disconnected.</summary>
        Hybrid
    }

    /// <summary>
    /// A request to synchronize data with a target node.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record SyncRequest
    {
        /// <summary>
        /// The target node to synchronize with.
        /// </summary>
        public required string TargetNodeId { get; init; }

        /// <summary>
        /// The synchronization mode to use.
        /// </summary>
        public required SyncMode Mode { get; init; }

        /// <summary>
        /// Optional filter to sync only specific data sets.
        /// </summary>
        public IReadOnlyList<string>? DataSetFilter { get; init; }

        /// <summary>
        /// Optional timestamp to sync only changes since a specific time.
        /// </summary>
        public DateTimeOffset? SinceTimestamp { get; init; }
    }

    /// <summary>
    /// Result of a synchronization operation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record SyncResult
    {
        /// <summary>
        /// Whether the sync completed successfully.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Number of items synchronized.
        /// </summary>
        public required long ItemsSynced { get; init; }

        /// <summary>
        /// Number of conflicts detected during synchronization.
        /// </summary>
        public required long ConflictsDetected { get; init; }

        /// <summary>
        /// How long the sync took.
        /// </summary>
        public required TimeSpan Duration { get; init; }

        /// <summary>
        /// Error message if the sync failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Creates a successful sync result.
        /// </summary>
        /// <param name="itemsSynced">Number of items synced.</param>
        /// <param name="conflictsDetected">Number of conflicts detected.</param>
        /// <param name="duration">How long it took.</param>
        /// <returns>A successful sync result.</returns>
        public static SyncResult Ok(long itemsSynced, long conflictsDetected, TimeSpan duration) =>
            new() { Success = true, ItemsSynced = itemsSynced, ConflictsDetected = conflictsDetected, Duration = duration };

        /// <summary>
        /// Creates a failed sync result.
        /// </summary>
        /// <param name="errorMessage">The error message.</param>
        /// <param name="duration">How long before failure.</param>
        /// <returns>A failed sync result.</returns>
        public static SyncResult Error(string errorMessage, TimeSpan duration) =>
            new() { Success = false, ItemsSynced = 0, ConflictsDetected = 0, Duration = duration, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Current sync status with a specific target node.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record SyncStatus
    {
        /// <summary>
        /// The target node identifier.
        /// </summary>
        public required string TargetNodeId { get; init; }

        /// <summary>
        /// Current state of the sync.
        /// </summary>
        public required SyncState State { get; init; }

        /// <summary>
        /// Progress percentage (0.0 to 100.0).
        /// </summary>
        public required double ProgressPercent { get; init; }

        /// <summary>
        /// When the last successful sync completed.
        /// </summary>
        public required DateTimeOffset LastSyncAt { get; init; }
    }

    /// <summary>
    /// States of a synchronization operation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum SyncState
    {
        /// <summary>No sync is in progress.</summary>
        Idle,
        /// <summary>Sync is actively in progress.</summary>
        Syncing,
        /// <summary>Sync has been paused.</summary>
        Paused,
        /// <summary>Sync encountered an error.</summary>
        Error,
        /// <summary>Sync has completed successfully.</summary>
        Completed
    }

    /// <summary>
    /// A conflict detected during synchronization.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record SyncConflict
    {
        /// <summary>
        /// The key of the conflicting item.
        /// </summary>
        public required string Key { get; init; }

        /// <summary>
        /// The local value of the conflicting item.
        /// </summary>
        public required byte[] LocalValue { get; init; }

        /// <summary>
        /// The remote value of the conflicting item.
        /// </summary>
        public required byte[] RemoteValue { get; init; }

        /// <summary>
        /// When the local value was last modified.
        /// </summary>
        public required DateTimeOffset LocalTimestamp { get; init; }

        /// <summary>
        /// When the remote value was last modified.
        /// </summary>
        public required DateTimeOffset RemoteTimestamp { get; init; }
    }

    /// <summary>
    /// Result of resolving a sync conflict.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record ConflictResolutionResult
    {
        /// <summary>
        /// The key of the resolved item.
        /// </summary>
        public required string Key { get; init; }

        /// <summary>
        /// The strategy used to resolve the conflict.
        /// </summary>
        public required ConflictResolutionStrategy Strategy { get; init; }

        /// <summary>
        /// The resolved value.
        /// </summary>
        public required byte[] ResolvedValue { get; init; }
    }

    /// <summary>
    /// Strategies for resolving sync conflicts.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum ConflictResolutionStrategy
    {
        /// <summary>Local value wins.</summary>
        LocalWins,
        /// <summary>Remote value wins.</summary>
        RemoteWins,
        /// <summary>Most recently modified value wins.</summary>
        LatestWins,
        /// <summary>Values are merged.</summary>
        Merge,
        /// <summary>Custom resolution logic is applied.</summary>
        Custom
    }

    /// <summary>
    /// A sync event describing what happened during synchronization.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public record SyncEvent
    {
        /// <summary>
        /// The type of sync event.
        /// </summary>
        public required SyncEventType EventType { get; init; }

        /// <summary>
        /// The target node involved in the sync.
        /// </summary>
        public required string TargetNodeId { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Optional detail about the event.
        /// </summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Types of sync events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Distributed contracts")]
    public enum SyncEventType
    {
        /// <summary>Sync has started.</summary>
        SyncStarted,
        /// <summary>Sync has completed successfully.</summary>
        SyncCompleted,
        /// <summary>Sync has failed.</summary>
        SyncFailed,
        /// <summary>A conflict was detected.</summary>
        ConflictDetected,
        /// <summary>A conflict was resolved.</summary>
        ConflictResolved
    }
}
