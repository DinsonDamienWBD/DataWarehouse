using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Operations;

/// <summary>
/// Manages the crash-recoverable online resize protocol for DWVD v2.1 volumes (VOPT-75).
///
/// The protocol works as follows:
///
/// 1. <see cref="BeginResize"/> creates an OPJR entry and returns a <see cref="ResizeOperation"/>
///    whose <c>PendingTotalBlocks</c> and <c>ResizeJournalOperationId</c> values must be
///    written atomically to the volume's Superblock before any data blocks are moved.
///
/// 2. As blocks are processed, the caller invokes <see cref="ProcessBlocks"/>.  Every
///    <see cref="CheckpointIntervalBlocks"/> (4096) blocks a checkpoint is written to the
///    OPJR, ensuring that after a crash the engine can resume from the last safe position
///    rather than restarting from the beginning.  With 4 KiB blocks this means losing at
///    most 16 MiB of progress.
///
/// 3. On success, <see cref="CompleteResize"/> marks the OPJR entry as
///    <see cref="OperationState.Completed"/>.  The caller must clear
///    <c>PendingTotalBlocks</c> and <c>ResizeJournalOperationId</c> in the Superblock.
///
/// 4. On startup, the engine calls <see cref="CheckForPendingResize"/> to detect an
///    incomplete resize.  The returned <see cref="ResizeRecoveryInfo"/> tells the engine
///    which operation to resume and from which block offset.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Online resize manager (VOPT-75)")]
public sealed class OnlineResizeManager
{
    /// <summary>
    /// Number of blocks processed between OPJR checkpoints.
    /// After a crash the engine re-processes at most this many blocks.
    /// At 4 KiB per block this represents 16 MiB of maximum re-work.
    /// </summary>
    public const int CheckpointIntervalBlocks = 4096;

    private readonly OperationJournalRegion _journal;
    private readonly int _blockSize;

    /// <summary>
    /// Creates an <see cref="OnlineResizeManager"/> backed by the given OPJR region.
    /// </summary>
    /// <param name="journal">The operation journal for this volume.</param>
    /// <param name="blockSize">Block size of the volume in bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    public OnlineResizeManager(OperationJournalRegion journal, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        _journal = journal;
        _blockSize = blockSize;
    }

    // ── Core methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Begins a new online resize and returns the information that must be atomically
    /// written to the Superblock.
    /// </summary>
    /// <remarks>
    /// The caller MUST persist <see cref="ResizeOperation.PendingTotalBlocks"/> and
    /// <see cref="ResizeOperation.ResizeJournalOperationId"/> to the Superblock in the
    /// same atomic write before processing any blocks.  If the engine crashes before this
    /// Superblock write is complete the pending resize is invisible and the volume is
    /// unmodified.
    /// </remarks>
    /// <param name="currentTotalBlocks">Current block count of the volume.</param>
    /// <param name="newTotalBlocks">Target block count (must differ from <paramref name="currentTotalBlocks"/>).</param>
    /// <returns>A <see cref="ResizeOperation"/> with the OPJR operation identifier and the target block count.</returns>
    /// <exception cref="ArgumentException"><paramref name="newTotalBlocks"/> equals <paramref name="currentTotalBlocks"/>.</exception>
    public ResizeOperation BeginResize(ulong currentTotalBlocks, ulong newTotalBlocks)
    {
        if (newTotalBlocks == currentTotalBlocks)
            throw new ArgumentException(
                "newTotalBlocks must differ from currentTotalBlocks.", nameof(newTotalBlocks));

        ulong totalUnits = newTotalBlocks > currentTotalBlocks
            ? newTotalBlocks - currentTotalBlocks
            : currentTotalBlocks - newTotalBlocks;

        // Encode newTotalBlocks in the 32-byte operation payload (first 8 bytes, LE).
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, newTotalBlocks);

        ulong operationId = _journal.CreateOperation(
            type: OperationType.Resize,
            sourceStart: 0,
            targetStart: 0,
            totalUnits: totalUnits,
            payload: payload);

        // Immediately transition from Queued -> InProgress so crash recovery can detect it.
        _journal.TransitionState(operationId, OperationState.InProgress);

        return new ResizeOperation(
            OperationId: operationId,
            PendingTotalBlocks: newTotalBlocks,
            ResizeJournalOperationId: operationId);
    }

    /// <summary>
    /// Called after each block (or batch) is processed.  Writes an OPJR checkpoint every
    /// <see cref="CheckpointIntervalBlocks"/> blocks.
    /// </summary>
    /// <param name="operationId">The operation identifier returned by <see cref="BeginResize"/>.</param>
    /// <param name="blocksProcessedSoFar">Cumulative count of blocks processed so far.</param>
    /// <returns>
    /// A <see cref="ResizeCheckpoint"/> indicating whether a checkpoint was written
    /// and the current progress.
    /// </returns>
    /// <exception cref="KeyNotFoundException">Operation not found in the journal.</exception>
    public ResizeCheckpoint ProcessBlocks(ulong operationId, ulong blocksProcessedSoFar)
    {
        var entry = _journal.GetOperation(operationId)
            ?? throw new KeyNotFoundException(
                $"Resize operation {operationId} not found in OPJR.");

        bool checkpoint = blocksProcessedSoFar > 0
                          && blocksProcessedSoFar % CheckpointIntervalBlocks == 0;

        uint progressPercent = entry.TotalUnits > 0
            ? (uint)Math.Min(1_000_000UL, blocksProcessedSoFar * 1_000_000UL / entry.TotalUnits)
            : 0u;

        if (checkpoint)
        {
            _journal.CheckpointProgress(
                operationId,
                completedUnits: blocksProcessedSoFar,
                checkpointData: blocksProcessedSoFar);
        }

        return new ResizeCheckpoint(
            WasCheckpointed: checkpoint,
            CompletedUnits: blocksProcessedSoFar,
            ProgressPercent: progressPercent);
    }

    /// <summary>
    /// Marks the resize operation as completed.
    /// </summary>
    /// <remarks>
    /// After this call the caller MUST clear <c>PendingTotalBlocks</c> and
    /// <c>ResizeJournalOperationId</c> in the Superblock atomically.
    /// </remarks>
    /// <param name="operationId">The operation identifier returned by <see cref="BeginResize"/>.</param>
    /// <exception cref="KeyNotFoundException">Operation not found.</exception>
    /// <exception cref="InvalidOperationException">Transition to Completed was rejected by the journal.</exception>
    public void CompleteResize(ulong operationId)
    {
        var entry = _journal.GetOperation(operationId)
            ?? throw new KeyNotFoundException(
                $"Resize operation {operationId} not found in OPJR.");

        // Final checkpoint at 100% before marking complete.
        _journal.CheckpointProgress(
            operationId,
            completedUnits: entry.TotalUnits,
            checkpointData: entry.TotalUnits);

        if (!_journal.TransitionState(operationId, OperationState.Completed))
            throw new InvalidOperationException(
                $"Failed to transition resize operation {operationId} to Completed.");
    }

    /// <summary>
    /// Scans the OPJR for a resize operation left in the <see cref="OperationState.InProgress"/>
    /// state, indicating that a previous resize did not complete cleanly.
    /// </summary>
    /// <returns>
    /// A <see cref="ResizeRecoveryInfo"/> if an incomplete resize is found, or
    /// <see langword="null"/> if no recovery is needed.
    /// </returns>
    public ResizeRecoveryInfo? CheckForPendingResize()
    {
        var candidates = _journal.GetOperationsRequiringRecovery();

        foreach (var entry in candidates)
        {
            if (entry.Type != OperationType.Resize)
                continue;

            // Recover newTotalBlocks from the operation payload.
            ulong newTotalBlocks = entry.OperationPayload.Length >= 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(entry.OperationPayload.AsSpan(0, 8))
                : 0;

            return new ResizeRecoveryInfo(
                OperationId: entry.OperationId,
                CompletedUnits: entry.CompletedUnits,
                NewTotalBlocks: newTotalBlocks);
        }

        return null;
    }
}

// ── Supporting value types ────────────────────────────────────────────────────

/// <summary>
/// Return value from <see cref="OnlineResizeManager.BeginResize"/>.
/// Must be written atomically to the Superblock before block processing begins.
/// </summary>
/// <param name="OperationId">OPJR identifier for this resize operation.</param>
/// <param name="PendingTotalBlocks">Target block count to store in the Superblock.</param>
/// <param name="ResizeJournalOperationId">Same as <paramref name="OperationId"/>; stored in the Superblock to link the resize to its OPJR entry.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Online resize (VOPT-75)")]
public readonly record struct ResizeOperation(
    ulong OperationId,
    ulong PendingTotalBlocks,
    ulong ResizeJournalOperationId);

/// <summary>
/// Return value from <see cref="OnlineResizeManager.ProcessBlocks"/>.
/// </summary>
/// <param name="WasCheckpointed">True if an OPJR checkpoint was written during this call.</param>
/// <param name="CompletedUnits">Cumulative blocks processed so far.</param>
/// <param name="ProgressPercent">Progress in units of 0.0001% (divide by 10,000 for percent).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Online resize (VOPT-75)")]
public readonly record struct ResizeCheckpoint(
    bool WasCheckpointed,
    ulong CompletedUnits,
    uint ProgressPercent);

/// <summary>
/// Return value from <see cref="OnlineResizeManager.CheckForPendingResize"/>.
/// Provides the engine with everything needed to resume the interrupted resize.
/// </summary>
/// <param name="OperationId">OPJR operation to resume.</param>
/// <param name="CompletedUnits">Last safely checkpointed block count; resume from here.</param>
/// <param name="NewTotalBlocks">The original resize target block count.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Online resize (VOPT-75)")]
public readonly record struct ResizeRecoveryInfo(
    ulong OperationId,
    ulong CompletedUnits,
    ulong NewTotalBlocks);
