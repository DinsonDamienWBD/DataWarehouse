using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Orchestrates morph transitions between adaptive index levels with WAL-journaled
/// copy-on-write semantics, crash recovery, cancellation, and zero-downtime dual-write protocol.
/// </summary>
/// <remarks>
/// <para>
/// The engine handles both forward morphs (lower to higher level) and backward morphs
/// (higher to lower level, with compaction). During migration the old index continues
/// serving all reads; new writes are dual-written to both old and a pending queue, then
/// drained into the target after the main migration completes.
/// </para>
/// <para>
/// WAL markers (MorphStart, MorphCheckpoint, MorphComplete, MorphAbort) enable crash
/// recovery: on restart, incomplete transitions resume from the latest checkpoint or
/// are cleanly aborted if no checkpoint exists.
/// </para>
/// <para>
/// Only one morph transition may execute at a time, enforced by a <see cref="SemaphoreSlim"/>.
/// Concurrent reads continue on the source index without blocking.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public sealed class MorphTransitionEngine
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly int _blockSize;
    private readonly SemaphoreSlim _morphLock = new(1, 1);
    private readonly ConcurrentQueue<(byte[] Key, long Value)> _pendingWrites = new();

    private MorphTransition? _currentTransition;

    /// <summary>
    /// Batch size for entry migration. Entries are migrated in batches for progress tracking.
    /// </summary>
    public const int MigrationBatchSize = 10_000;

    /// <summary>
    /// WAL checkpoint interval: a checkpoint marker is written every N entries.
    /// </summary>
    public const int CheckpointInterval = 100_000;

    /// <summary>
    /// Progress event fire interval: progress is reported every N entries.
    /// </summary>
    public const int ProgressFireInterval = 10_000;

    /// <summary>
    /// Raised when progress is updated during migration.
    /// </summary>
    public event Action<MorphProgress>? ProgressUpdated;

    /// <summary>
    /// Initializes a new <see cref="MorphTransitionEngine"/>.
    /// </summary>
    /// <param name="device">Block device for I/O operations.</param>
    /// <param name="allocator">Block allocator for creating new index structures.</param>
    /// <param name="wal">Write-ahead log for crash-safe transition journaling.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public MorphTransitionEngine(IBlockDevice device, IBlockAllocator allocator, IWriteAheadLog wal, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Returns a snapshot of the current transition progress, or null if no transition is active.
    /// </summary>
    public MorphProgress? GetProgress()
    {
        var transition = _currentTransition;
        if (transition is null)
        {
            return null;
        }

        var elapsed = transition.Elapsed;
        double entriesPerSecond = elapsed.TotalSeconds > 0
            ? transition.MigratedEntries / elapsed.TotalSeconds
            : 0;

        return new MorphProgress
        {
            TransitionId = transition.TransitionId,
            State = transition.State,
            Progress = transition.Progress,
            MigratedEntries = transition.MigratedEntries,
            TotalEntries = transition.TotalEntries,
            Elapsed = elapsed,
            EntriesPerSecond = entriesPerSecond
        };
    }

    /// <summary>
    /// Executes a forward morph from the source index to a higher-level target index.
    /// Migrates all entries with WAL checkpoints, zero-downtime dual-write, and cancellation support.
    /// </summary>
    /// <param name="source">Source index to migrate from. Continues serving reads during migration.</param>
    /// <param name="targetLevel">Target morph level (must be higher than source).</param>
    /// <param name="ct">Cancellation token. Checked between batches; on cancel the source remains active.</param>
    /// <returns>The new target index with all entries migrated, or null if cancelled.</returns>
    /// <exception cref="InvalidOperationException">Another morph is already in progress.</exception>
    public async Task<IAdaptiveIndex?> ExecuteForwardMorphAsync(
        IAdaptiveIndex source,
        MorphLevel targetLevel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (targetLevel <= source.CurrentLevel)
        {
            throw new ArgumentException(
                $"Forward morph requires target level ({targetLevel}) > source level ({source.CurrentLevel}).",
                nameof(targetLevel));
        }

        return await ExecuteMorphCoreAsync(source, targetLevel, MorphDirection.Forward, 0, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a backward morph from the source index to a lower-level target index (compaction + demotion).
    /// </summary>
    /// <param name="source">Source index to compact and demote.</param>
    /// <param name="targetLevel">Target morph level (must be lower than source).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new target index with all entries migrated, or null if cancelled.</returns>
    /// <exception cref="InvalidOperationException">Another morph is already in progress.</exception>
    public async Task<IAdaptiveIndex?> ExecuteBackwardMorphAsync(
        IAdaptiveIndex source,
        MorphLevel targetLevel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (targetLevel >= source.CurrentLevel)
        {
            throw new ArgumentException(
                $"Backward morph requires target level ({targetLevel}) < source level ({source.CurrentLevel}).",
                nameof(targetLevel));
        }

        return await ExecuteMorphCoreAsync(source, targetLevel, MorphDirection.Backward, 0, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers from a crash by scanning the WAL for incomplete morph transitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each MorphStart without a matching MorphComplete or MorphAbort:
    /// - If a MorphCheckpoint exists with checkpointed entries > 0, the transition can be resumed.
    /// - If no checkpoint exists, the partial target is discarded and a MorphAbort is written.
    /// </para>
    /// </remarks>
    /// <param name="wal">Write-ahead log to scan for incomplete transitions.</param>
    /// <returns>
    /// List of recovery results indicating what was found and what action was taken.
    /// </returns>
    public async Task<IReadOnlyList<MorphRecoveryResult>> RecoverFromCrashAsync(IWriteAheadLog wal)
    {
        ArgumentNullException.ThrowIfNull(wal);

        var results = new List<MorphRecoveryResult>();
        var walEntries = await wal.ReplayAsync().ConfigureAwait(false);

        // Parse morph markers from WAL entries
        var startMarkers = new Dictionary<Guid, MorphWalMarker>();
        var latestCheckpoints = new Dictionary<Guid, MorphWalMarker>();
        var completedOrAborted = new HashSet<Guid>();

        foreach (var entry in walEntries)
        {
            if (entry.Type != JournalEntryType.Checkpoint || entry.AfterImage is null ||
                entry.AfterImage.Length < MorphWalMarker.SerializedSize)
            {
                continue;
            }

            var marker = MorphWalMarker.Deserialize(entry.AfterImage);

            switch (marker.Type)
            {
                case MorphWalMarkerType.MorphStart:
                    startMarkers[marker.TransitionId] = marker;
                    break;

                case MorphWalMarkerType.MorphCheckpoint:
                    latestCheckpoints[marker.TransitionId] = marker;
                    break;

                case MorphWalMarkerType.MorphComplete:
                case MorphWalMarkerType.MorphAbort:
                    completedOrAborted.Add(marker.TransitionId);
                    break;
            }
        }

        // Process incomplete transitions
        foreach (var (transitionId, startMarker) in startMarkers)
        {
            if (completedOrAborted.Contains(transitionId))
            {
                continue; // Already finished
            }

            MorphRecoveryAction action;
            long checkpointedEntries = 0;

            if (latestCheckpoints.TryGetValue(transitionId, out var checkpoint) &&
                checkpoint.CheckpointedEntries > 0)
            {
                // Has checkpoint — transition can potentially be resumed
                action = MorphRecoveryAction.ResumeFromCheckpoint;
                checkpointedEntries = checkpoint.CheckpointedEntries;
            }
            else
            {
                // No checkpoint — discard partial target, abort
                action = MorphRecoveryAction.Aborted;
                await WriteWalMarkerAsync(new MorphWalMarker
                {
                    Type = MorphWalMarkerType.MorphAbort,
                    TransitionId = transitionId,
                    SourceLevel = startMarker.SourceLevel,
                    TargetLevel = startMarker.TargetLevel,
                    CheckpointedEntries = 0,
                    TargetRootBlock = startMarker.TargetRootBlock
                }).ConfigureAwait(false);
            }

            results.Add(new MorphRecoveryResult
            {
                TransitionId = transitionId,
                SourceLevel = startMarker.SourceLevel,
                TargetLevel = startMarker.TargetLevel,
                Action = action,
                CheckpointedEntries = checkpointedEntries,
                TargetRootBlock = startMarker.TargetRootBlock
            });
        }

        return results;
    }

    /// <summary>
    /// Core morph execution used by both forward and backward morph paths.
    /// </summary>
    private async Task<IAdaptiveIndex?> ExecuteMorphCoreAsync(
        IAdaptiveIndex source,
        MorphLevel targetLevel,
        MorphDirection direction,
        long resumeFromEntry,
        CancellationToken ct)
    {
        if (!await _morphLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Another morph transition is already in progress.");
        }

        try
        {
            var transition = new MorphTransition(source.CurrentLevel, targetLevel);
            _currentTransition = transition;

            // Phase 1: Preparing
            transition.State = MorphTransitionState.Preparing;
            long totalEntries = await source.CountAsync(ct).ConfigureAwait(false);
            transition.TotalEntries = totalEntries;

            // Create target index at target level
            var target = CreateIndexForLevel(targetLevel);

            // Write WAL MorphStart marker
            await WriteWalMarkerAsync(new MorphWalMarker
            {
                Type = MorphWalMarkerType.MorphStart,
                TransitionId = transition.TransitionId,
                SourceLevel = source.CurrentLevel,
                TargetLevel = targetLevel,
                CheckpointedEntries = 0,
                TargetRootBlock = target.RootBlockNumber
            }).ConfigureAwait(false);

            // Phase 2: Migrating
            transition.State = MorphTransitionState.Migrating;
            if (resumeFromEntry > 0)
            {
                transition.SetMigrated(resumeFromEntry);
            }

            long migratedCount = resumeFromEntry;
            long batchCount = 0;
            long sinceLastCheckpoint = resumeFromEntry;
            long sinceLastProgressFire = 0;

            // Clear pending writes queue for dual-write protocol
            while (_pendingWrites.TryDequeue(out _)) { }

            await foreach (var entry in source.RangeQueryAsync(null, null, ct).ConfigureAwait(false))
            {
                // Skip already-migrated entries when resuming from checkpoint
                if (migratedCount < resumeFromEntry)
                {
                    migratedCount++;
                    continue;
                }

                // Check cancellation between batches
                if (batchCount >= MigrationBatchSize)
                {
                    batchCount = 0;
                    if (ct.IsCancellationRequested || transition.Cts.IsCancellationRequested)
                    {
                        return await AbortTransitionAsync(transition, target).ConfigureAwait(false);
                    }
                }

                await target.InsertAsync(entry.Key, entry.Value, ct).ConfigureAwait(false);
                transition.IncrementMigrated();
                migratedCount++;
                batchCount++;
                sinceLastCheckpoint++;
                sinceLastProgressFire++;

                // WAL checkpoint every CheckpointInterval entries
                if (sinceLastCheckpoint >= CheckpointInterval)
                {
                    await WriteWalMarkerAsync(new MorphWalMarker
                    {
                        Type = MorphWalMarkerType.MorphCheckpoint,
                        TransitionId = transition.TransitionId,
                        SourceLevel = source.CurrentLevel,
                        TargetLevel = targetLevel,
                        CheckpointedEntries = migratedCount,
                        TargetRootBlock = target.RootBlockNumber
                    }).ConfigureAwait(false);
                    sinceLastCheckpoint = 0;
                }

                // Fire progress event every ProgressFireInterval entries
                if (sinceLastProgressFire >= ProgressFireInterval)
                {
                    FireProgressEvent(transition);
                    sinceLastProgressFire = 0;
                }
            }

            // Drain any pending writes that arrived during migration (dual-write protocol)
            await DrainPendingWritesAsync(target, ct).ConfigureAwait(false);

            // Phase 3: Verifying
            transition.State = MorphTransitionState.Verifying;
            long targetCount = await target.CountAsync(ct).ConfigureAwait(false);
            long sourceCount = await source.CountAsync(ct).ConfigureAwait(false);
            long pendingCount = 0;

            // Account for writes that may have arrived during drain
            while (_pendingWrites.TryDequeue(out var pending))
            {
                await target.InsertAsync(pending.Key, pending.Value, ct).ConfigureAwait(false);
                pendingCount++;
            }

            // Target should have at least sourceCount entries (may have more due to concurrent writes)
            if (targetCount + pendingCount < sourceCount)
            {
                transition.ErrorMessage = $"Verification failed: target has {targetCount + pendingCount} entries but source has {sourceCount}.";
                return await AbortTransitionAsync(transition, target).ConfigureAwait(false);
            }

            // Phase 4: Switching
            transition.State = MorphTransitionState.Switching;

            // Write WAL MorphComplete marker (atomic commit point)
            await WriteWalMarkerAsync(new MorphWalMarker
            {
                Type = MorphWalMarkerType.MorphComplete,
                TransitionId = transition.TransitionId,
                SourceLevel = source.CurrentLevel,
                TargetLevel = targetLevel,
                CheckpointedEntries = migratedCount + pendingCount,
                TargetRootBlock = target.RootBlockNumber
            }).ConfigureAwait(false);

            // Phase 5: Completed
            transition.State = MorphTransitionState.Completed;
            transition.EndTime = DateTimeOffset.UtcNow;
            FireProgressEvent(transition);

            return target;
        }
        catch (OperationCanceledException)
        {
            if (_currentTransition is { } t)
            {
                t.ErrorMessage = "Transition cancelled.";
                t.State = MorphTransitionState.Aborted;
                t.EndTime = DateTimeOffset.UtcNow;
            }
            return null;
        }
        finally
        {
            _currentTransition = null;
            _morphLock.Release();
        }
    }

    /// <summary>
    /// Enqueues a write for dual-write protocol during active morph transitions.
    /// If no transition is active, this is a no-op and returns false.
    /// </summary>
    /// <param name="key">Key to write.</param>
    /// <param name="value">Value to write.</param>
    /// <returns>True if the write was enqueued (transition is active), false otherwise.</returns>
    public bool EnqueueDualWrite(byte[] key, long value)
    {
        if (_currentTransition is null || _currentTransition.State != MorphTransitionState.Migrating)
        {
            return false;
        }

        _pendingWrites.Enqueue((key, value));
        return true;
    }

    /// <summary>
    /// Drains all pending writes from the dual-write queue into the target index.
    /// </summary>
    private async Task DrainPendingWritesAsync(IAdaptiveIndex target, CancellationToken ct)
    {
        while (_pendingWrites.TryDequeue(out var pending))
        {
            await target.InsertAsync(pending.Key, pending.Value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Aborts a transition: writes MorphAbort WAL marker and returns null.
    /// The source index remains active and unmodified.
    /// </summary>
    private async Task<IAdaptiveIndex?> AbortTransitionAsync(MorphTransition transition, IAdaptiveIndex target)
    {
        transition.State = MorphTransitionState.Aborted;
        transition.EndTime = DateTimeOffset.UtcNow;

        // Write WAL abort marker
        await WriteWalMarkerAsync(new MorphWalMarker
        {
            Type = MorphWalMarkerType.MorphAbort,
            TransitionId = transition.TransitionId,
            SourceLevel = transition.SourceLevel,
            TargetLevel = transition.TargetLevel,
            CheckpointedEntries = transition.MigratedEntries,
            TargetRootBlock = target.RootBlockNumber
        }).ConfigureAwait(false);

        // Dispose target if disposable (free allocated blocks)
        if (target is IDisposable disposable)
        {
            disposable.Dispose();
        }

        FireProgressEvent(transition);
        return null;
    }

    /// <summary>
    /// Writes a morph WAL marker as a JournalEntry checkpoint.
    /// </summary>
    private async Task WriteWalMarkerAsync(MorphWalMarker marker)
    {
        var buffer = new byte[MorphWalMarker.SerializedSize];
        marker.Serialize(buffer);

        var entry = new JournalEntry
        {
            TransactionId = -1,
            Type = JournalEntryType.Checkpoint,
            TargetBlockNumber = marker.TargetRootBlock,
            BeforeImage = new byte[] { (byte)marker.SourceLevel },
            AfterImage = buffer
        };

        await _wal.AppendEntryAsync(entry).ConfigureAwait(false);
        await _wal.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Fires a progress update event with a snapshot of the current transition state.
    /// </summary>
    private void FireProgressEvent(MorphTransition transition)
    {
        var elapsed = transition.Elapsed;
        double entriesPerSecond = elapsed.TotalSeconds > 0
            ? transition.MigratedEntries / elapsed.TotalSeconds
            : 0;

        ProgressUpdated?.Invoke(new MorphProgress
        {
            TransitionId = transition.TransitionId,
            State = transition.State,
            Progress = transition.Progress,
            MigratedEntries = transition.MigratedEntries,
            TotalEntries = transition.TotalEntries,
            Elapsed = elapsed,
            EntriesPerSecond = entriesPerSecond
        });
    }

    /// <summary>
    /// Factory method to create an index implementation for a given morph level.
    /// </summary>
    private IAdaptiveIndex CreateIndexForLevel(MorphLevel level) => level switch
    {
        MorphLevel.DirectPointer => new DirectPointerIndex(),
        MorphLevel.SortedArray => new SortedArrayIndex(10_000),
        MorphLevel.AdaptiveRadixTree => new ArtIndex(),
        MorphLevel.BeTree => throw new NotSupportedException($"Level {level} morph target not yet supported."),
        MorphLevel.LearnedIndex => throw new NotSupportedException($"Level {level} morph target not yet supported."),
        MorphLevel.BeTreeForest => throw new NotSupportedException($"Level {level} morph target not yet supported."),
        MorphLevel.DistributedRouting => throw new NotSupportedException($"Level {level} morph target not yet supported."),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown morph level.")
    };
}

/// <summary>
/// Action taken during crash recovery for an incomplete morph transition.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public enum MorphRecoveryAction : byte
{
    /// <summary>Transition was aborted (no checkpoint found or unrecoverable).</summary>
    Aborted = 0,

    /// <summary>Transition can be resumed from the latest checkpoint.</summary>
    ResumeFromCheckpoint = 1
}

/// <summary>
/// Result of crash recovery analysis for a single incomplete morph transition.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public sealed record MorphRecoveryResult
{
    /// <summary>Transition identifier.</summary>
    public Guid TransitionId { get; init; }

    /// <summary>Source morph level.</summary>
    public MorphLevel SourceLevel { get; init; }

    /// <summary>Target morph level.</summary>
    public MorphLevel TargetLevel { get; init; }

    /// <summary>Recovery action taken.</summary>
    public MorphRecoveryAction Action { get; init; }

    /// <summary>Number of entries confirmed migrated at the latest checkpoint.</summary>
    public long CheckpointedEntries { get; init; }

    /// <summary>Root block number of the partial target index.</summary>
    public long TargetRootBlock { get; init; }
}
