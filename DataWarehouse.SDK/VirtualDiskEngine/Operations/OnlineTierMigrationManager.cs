using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Operations;

/// <summary>
/// Result of beginning a tier migration operation.
/// Contains the OPJR operation identifier and the source/target region information.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online tier migration (VOPT-76)")]
public readonly struct TierMigrationOperation : IEquatable<TierMigrationOperation>
{
    /// <summary>OPJR operation identifier for this migration.</summary>
    public ulong OperationId { get; }

    /// <summary>Start block index of the source storage tier region.</summary>
    public ulong SourceRegionStart { get; }

    /// <summary>Start block index of the target storage tier region.</summary>
    public ulong TargetRegionStart { get; }

    /// <summary>Constructs a tier migration operation result.</summary>
    public TierMigrationOperation(ulong operationId, ulong sourceRegionStart, ulong targetRegionStart)
    {
        OperationId = operationId;
        SourceRegionStart = sourceRegionStart;
        TargetRegionStart = targetRegionStart;
    }

    /// <inheritdoc />
    public bool Equals(TierMigrationOperation other) =>
        OperationId == other.OperationId
        && SourceRegionStart == other.SourceRegionStart
        && TargetRegionStart == other.TargetRegionStart;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is TierMigrationOperation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(OperationId, SourceRegionStart, TargetRegionStart);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TierMigrationOperation left, TierMigrationOperation right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TierMigrationOperation left, TierMigrationOperation right) =>
        !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"TierMigration(Op={OperationId}, Source={SourceRegionStart}, Target={TargetRegionStart})";
}

/// <summary>
/// Recovery information for an in-progress tier migration found after a crash.
/// Used by the mount path to resume from the last checkpoint.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online tier migration (VOPT-76)")]
public readonly struct TierMigrationRecoveryInfo : IEquatable<TierMigrationRecoveryInfo>
{
    /// <summary>OPJR operation identifier.</summary>
    public ulong OperationId { get; }

    /// <summary>Number of extents already migrated before the crash.</summary>
    public ulong CompletedExtents { get; }

    /// <summary>
    /// Index of the last extent that was successfully migrated and checkpointed.
    /// Resume should start from the extent after this one.
    /// </summary>
    public ulong LastExtentIndex { get; }

    /// <summary>Constructs recovery info.</summary>
    public TierMigrationRecoveryInfo(ulong operationId, ulong completedExtents, ulong lastExtentIndex)
    {
        OperationId = operationId;
        CompletedExtents = completedExtents;
        LastExtentIndex = lastExtentIndex;
    }

    /// <inheritdoc />
    public bool Equals(TierMigrationRecoveryInfo other) =>
        OperationId == other.OperationId
        && CompletedExtents == other.CompletedExtents
        && LastExtentIndex == other.LastExtentIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is TierMigrationRecoveryInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(OperationId, CompletedExtents, LastExtentIndex);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TierMigrationRecoveryInfo left, TierMigrationRecoveryInfo right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TierMigrationRecoveryInfo left, TierMigrationRecoveryInfo right) =>
        !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"TierMigrationRecovery(Op={OperationId}, Completed={CompletedExtents}, LastIdx={LastExtentIndex})";
}

/// <summary>
/// Manages online tier migration operations on a live VDE volume with crash-safe
/// progress tracking via the Operation Journal Region (OPJR).
///
/// Tier migration moves data extents between storage tiers (e.g. SSD hot tier to
/// HDD cold tier).  Progress is checkpointed every
/// <see cref="CheckpointIntervalBlocks"/> blocks to allow crash recovery without
/// restarting the entire migration.
///
/// The caller is responsible for:
///   1. Performing the actual block-level data copy between tiers.
///   2. Updating extent metadata after migration completes.
///   3. Calling <see cref="CheckpointProgress"/> periodically to record progress.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: online tier migration (VOPT-76)")]
public sealed class OnlineTierMigrationManager
{
    /// <summary>
    /// Recommended checkpoint interval: every 4096 blocks a checkpoint is written
    /// to the OPJR for crash-safe resume.
    /// </summary>
    public const int CheckpointIntervalBlocks = 4096;

    private readonly OperationJournalRegion _journal;
    private readonly int _blockSize;

    /// <summary>
    /// Initialises the tier migration manager.
    /// </summary>
    /// <param name="journal">The OPJR region to record operations in.</param>
    /// <param name="blockSize">Volume block size in bytes (e.g. 4096).</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    public OnlineTierMigrationManager(OperationJournalRegion journal, int blockSize)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, 0, nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Begins an online tier migration.
    /// Creates an OPJR entry of type <see cref="OperationType.TierMigrate"/> and transitions
    /// it to <see cref="OperationState.InProgress"/>.
    /// </summary>
    /// <param name="sourceRegionStart">Start block index of the source tier region.</param>
    /// <param name="targetRegionStart">Start block index of the target tier region.</param>
    /// <param name="totalExtents">Total number of extents to migrate.</param>
    /// <returns>Operation info with the OPJR identifier and region boundaries.</returns>
    public TierMigrationOperation BeginTierMigration(
        ulong sourceRegionStart,
        ulong targetRegionStart,
        ulong totalExtents)
    {
        ulong operationId = _journal.CreateOperation(
            type: OperationType.TierMigrate,
            sourceStart: sourceRegionStart,
            targetStart: targetRegionStart,
            totalUnits: totalExtents);

        // Transition from Queued to InProgress immediately.
        _journal.TransitionState(operationId, OperationState.InProgress);

        return new TierMigrationOperation(operationId, sourceRegionStart, targetRegionStart);
    }

    /// <summary>
    /// Records a progress checkpoint for the tier migration.
    /// Should be called after every <see cref="CheckpointIntervalBlocks"/> blocks
    /// of data have been successfully copied.
    /// </summary>
    /// <param name="operationId">OPJR operation identifier from <see cref="BeginTierMigration"/>.</param>
    /// <param name="extentsProcessed">Total number of extents migrated so far (cumulative).</param>
    /// <param name="lastExtentIndex">Index of the last extent successfully migrated.
    /// Used as checkpoint data for crash recovery resume.</param>
    /// <exception cref="KeyNotFoundException">Operation not found in OPJR.</exception>
    public void CheckpointProgress(ulong operationId, ulong extentsProcessed, ulong lastExtentIndex)
    {
        _journal.CheckpointProgress(
            operationId,
            completedUnits: extentsProcessed,
            checkpointData: lastExtentIndex);
    }

    /// <summary>
    /// Marks a tier migration as completed.  The caller should then update extent
    /// metadata to reflect the new tier assignments.
    /// </summary>
    /// <param name="operationId">OPJR operation identifier.</param>
    /// <exception cref="InvalidOperationException">State transition failed.</exception>
    public void CompleteMigration(ulong operationId)
    {
        if (!_journal.TransitionState(operationId, OperationState.Completed))
        {
            throw new InvalidOperationException(
                $"Failed to complete tier migration operation {operationId}. " +
                "The operation may not exist or is already in a terminal state.");
        }
    }

    /// <summary>
    /// Checks the OPJR for any in-progress tier migration that was interrupted
    /// by a crash.  Returns recovery information if found, or <see langword="null"/>
    /// if no pending migration exists.
    /// </summary>
    /// <returns>Recovery info for the pending migration, or null.</returns>
    public TierMigrationRecoveryInfo? CheckForPendingMigration()
    {
        var pending = _journal.GetOperationsRequiringRecovery();

        foreach (var entry in pending)
        {
            if (entry.Type != OperationType.TierMigrate)
                continue;

            return new TierMigrationRecoveryInfo(
                operationId: entry.OperationId,
                completedExtents: entry.CompletedUnits,
                lastExtentIndex: entry.CheckpointData);
        }

        return null;
    }
}
