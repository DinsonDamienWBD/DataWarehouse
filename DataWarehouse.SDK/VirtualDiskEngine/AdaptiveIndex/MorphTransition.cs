using System;
using System.Buffers.Binary;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// State machine for morph transition lifecycle.
/// Each transition progresses through these states in order, with abort possible from any active state.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public enum MorphTransitionState : byte
{
    /// <summary>Transition created but not yet started.</summary>
    NotStarted = 0,

    /// <summary>Preparing target index structure and WAL markers.</summary>
    Preparing = 1,

    /// <summary>Actively migrating entries from source to target index.</summary>
    Migrating = 2,

    /// <summary>Verifying migrated entry count matches source.</summary>
    Verifying = 3,

    /// <summary>Atomically switching active index from source to target.</summary>
    Switching = 4,

    /// <summary>Transition completed successfully.</summary>
    Completed = 5,

    /// <summary>Transition aborted (cancelled or error).</summary>
    Aborted = 6,

    /// <summary>Recovering from a crash during an in-progress transition.</summary>
    CrashRecovery = 7
}

/// <summary>
/// Direction of a morph transition between index levels.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public enum MorphDirection : byte
{
    /// <summary>Morphing from a lower level to a higher level (e.g., SortedArray to ART).</summary>
    Forward = 0,

    /// <summary>Morphing from a higher level to a lower level (e.g., ART to SortedArray).</summary>
    Backward = 1
}

/// <summary>
/// Tracks a single morph transition between two index levels.
/// Provides progress observability, cancellation support, and state machine transitions.
/// </summary>
/// <remarks>
/// <para>
/// A morph transition migrates all entries from a source index at one level to a target index
/// at another level. The transition is WAL-journaled with checkpoint markers for crash recovery.
/// </para>
/// <para>
/// Progress is tracked via <see cref="MigratedEntries"/> using Interlocked operations for
/// thread-safe observation from monitoring threads while migration runs on the worker thread.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public sealed class MorphTransition
{
    private long _migratedEntries;
    private MorphTransitionState _state;

    /// <summary>
    /// Unique identifier for this transition.
    /// </summary>
    public Guid TransitionId { get; }

    /// <summary>
    /// Source index level being migrated from.
    /// </summary>
    public MorphLevel SourceLevel { get; }

    /// <summary>
    /// Target index level being migrated to.
    /// </summary>
    public MorphLevel TargetLevel { get; }

    /// <summary>
    /// Direction of the morph (forward = lower to higher, backward = higher to lower).
    /// </summary>
    public MorphDirection Direction { get; }

    /// <summary>
    /// Current state of the transition.
    /// </summary>
    public MorphTransitionState State
    {
        get => _state;
        internal set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Total number of entries to migrate from the source index.
    /// </summary>
    public long TotalEntries { get; internal set; }

    /// <summary>
    /// Number of entries migrated so far. Updated atomically via Interlocked.
    /// </summary>
    public long MigratedEntries => Interlocked.Read(ref _migratedEntries);

    /// <summary>
    /// Migration progress as a ratio from 0.0 to 1.0.
    /// Returns 1.0 when TotalEntries is zero (empty index migration is trivially complete).
    /// </summary>
    public double Progress => TotalEntries == 0 ? 1.0 : (double)Interlocked.Read(ref _migratedEntries) / TotalEntries;

    /// <summary>
    /// When the transition was started.
    /// </summary>
    public DateTimeOffset StartTime { get; }

    /// <summary>
    /// When the transition completed, was aborted, or null if still in progress.
    /// </summary>
    public DateTimeOffset? EndTime { get; internal set; }

    /// <summary>
    /// Elapsed time since transition start. Uses current time if still in progress.
    /// </summary>
    public TimeSpan Elapsed => (EndTime ?? DateTimeOffset.UtcNow) - StartTime;

    /// <summary>
    /// Cancellation token source for requesting cancellation of this transition.
    /// </summary>
    public CancellationTokenSource Cts { get; }

    /// <summary>
    /// Error message if the transition was aborted due to an error. Null if no error.
    /// </summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>
    /// Raised when the transition state changes.
    /// </summary>
    public event Action<MorphTransitionState>? StateChanged;

    /// <summary>
    /// Initializes a new <see cref="MorphTransition"/>.
    /// </summary>
    /// <param name="sourceLevel">Source index level.</param>
    /// <param name="targetLevel">Target index level.</param>
    public MorphTransition(MorphLevel sourceLevel, MorphLevel targetLevel)
    {
        TransitionId = Guid.NewGuid();
        SourceLevel = sourceLevel;
        TargetLevel = targetLevel;
        Direction = targetLevel > sourceLevel ? MorphDirection.Forward : MorphDirection.Backward;
        _state = MorphTransitionState.NotStarted;
        StartTime = DateTimeOffset.UtcNow;
        Cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Atomically increments the migrated entries counter.
    /// </summary>
    /// <param name="count">Number of entries to add.</param>
    internal void IncrementMigrated(long count = 1)
    {
        Interlocked.Add(ref _migratedEntries, count);
    }

    /// <summary>
    /// Sets the migrated entries counter to a specific value (used during crash recovery resume).
    /// </summary>
    /// <param name="value">The checkpoint value to resume from.</param>
    internal void SetMigrated(long value)
    {
        Interlocked.Exchange(ref _migratedEntries, value);
    }
}

/// <summary>
/// Types of WAL markers used to journal morph transitions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public enum MorphWalMarkerType : byte
{
    /// <summary>Marks the start of a morph transition.</summary>
    MorphStart = 1,

    /// <summary>Checkpoint during migration: records entries migrated so far.</summary>
    MorphCheckpoint = 2,

    /// <summary>Marks successful completion of a morph transition.</summary>
    MorphComplete = 3,

    /// <summary>Marks abortion of a morph transition.</summary>
    MorphAbort = 4
}

/// <summary>
/// WAL entry for morph transitions. Fixed 64-byte record for predictable serialization.
/// </summary>
/// <remarks>
/// Layout (64 bytes):
/// [Type:1][TransitionId:16][SourceLevel:1][TargetLevel:1][CheckpointedEntries:8][TargetRootBlock:8][Reserved:29]
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public readonly record struct MorphWalMarker
{
    /// <summary>Fixed serialized size in bytes.</summary>
    public const int SerializedSize = 64;

    /// <summary>Type of morph WAL marker.</summary>
    public MorphWalMarkerType Type { get; init; }

    /// <summary>Transition identifier linking all markers for the same morph.</summary>
    public Guid TransitionId { get; init; }

    /// <summary>Source index level.</summary>
    public MorphLevel SourceLevel { get; init; }

    /// <summary>Target index level.</summary>
    public MorphLevel TargetLevel { get; init; }

    /// <summary>Number of entries confirmed migrated at this checkpoint.</summary>
    public long CheckpointedEntries { get; init; }

    /// <summary>Root block number of the new index being built.</summary>
    public long TargetRootBlock { get; init; }

    /// <summary>
    /// Serializes this marker to a 64-byte buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer. Must be at least 64 bytes.</param>
    /// <returns>Number of bytes written (always 64).</returns>
    /// <exception cref="ArgumentException">Buffer too small.</exception>
    public int Serialize(Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
        {
            throw new ArgumentException($"Buffer too small: need {SerializedSize} bytes, got {buffer.Length}", nameof(buffer));
        }

        buffer.Slice(0, SerializedSize).Clear();

        int offset = 0;
        buffer[offset++] = (byte)Type;

        TransitionId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;

        buffer[offset++] = (byte)SourceLevel;
        buffer[offset++] = (byte)TargetLevel;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), CheckpointedEntries);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), TargetRootBlock);
        // Remaining bytes are reserved (already zeroed)

        return SerializedSize;
    }

    /// <summary>
    /// Deserializes a morph WAL marker from a buffer.
    /// </summary>
    /// <param name="buffer">Source buffer. Must be at least 64 bytes.</param>
    /// <returns>Deserialized marker.</returns>
    /// <exception cref="ArgumentException">Buffer too small.</exception>
    public static MorphWalMarker Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
        {
            throw new ArgumentException($"Buffer too small: need {SerializedSize} bytes, got {buffer.Length}", nameof(buffer));
        }

        int offset = 0;
        var type = (MorphWalMarkerType)buffer[offset++];

        var transitionId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var sourceLevel = (MorphLevel)buffer[offset++];
        var targetLevel = (MorphLevel)buffer[offset++];

        long checkpointedEntries = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        long targetRootBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));

        return new MorphWalMarker
        {
            Type = type,
            TransitionId = transitionId,
            SourceLevel = sourceLevel,
            TargetLevel = targetLevel,
            CheckpointedEntries = checkpointedEntries,
            TargetRootBlock = targetRootBlock
        };
    }
}

/// <summary>
/// Immutable snapshot of morph transition progress for external observability.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-07 Morph transitions")]
public sealed record MorphProgress
{
    /// <summary>Transition identifier.</summary>
    public Guid TransitionId { get; init; }

    /// <summary>Current state of the transition.</summary>
    public MorphTransitionState State { get; init; }

    /// <summary>Migration progress ratio (0.0 to 1.0).</summary>
    public double Progress { get; init; }

    /// <summary>Number of entries migrated so far.</summary>
    public long MigratedEntries { get; init; }

    /// <summary>Total number of entries to migrate.</summary>
    public long TotalEntries { get; init; }

    /// <summary>Time elapsed since transition started.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Entries migrated per second (throughput metric).</summary>
    public double EntriesPerSecond { get; init; }
}
