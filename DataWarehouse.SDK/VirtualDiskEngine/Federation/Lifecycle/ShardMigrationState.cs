using System;
using System.Buffers.Binary;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Phases of a shard migration lifecycle. Transitions are validated by <see cref="ShardMigrationState.TransitionTo"/>.
/// </summary>
/// <remarks>
/// Legal transitions:
/// <list type="bullet">
/// <item>Pending -> Preparing -> CopyingBlocks -> CatchingUp -> Switching -> Completed</item>
/// <item>Any non-terminal -> RolledBack</item>
/// <item>Any non-terminal -> Failed</item>
/// </list>
/// Terminal phases: Completed, RolledBack, Failed.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-04)")]
public enum MigrationPhase : byte
{
    /// <summary>Migration created but not yet started.</summary>
    Pending = 0,

    /// <summary>Acquiring distributed lock and preparing source/destination.</summary>
    Preparing = 1,

    /// <summary>Bulk block copy from source to destination in progress.</summary>
    CopyingBlocks = 2,

    /// <summary>Delta sync: catching up with writes that occurred during bulk copy.</summary>
    CatchingUp = 3,

    /// <summary>Atomic cutover: reassigning routing table slots from source to destination.</summary>
    Switching = 4,

    /// <summary>Migration completed successfully. Terminal state.</summary>
    Completed = 5,

    /// <summary>Migration rolled back to source. Terminal state.</summary>
    RolledBack = 6,

    /// <summary>Migration failed with an unrecoverable error. Terminal state.</summary>
    Failed = 7
}

/// <summary>
/// Immutable snapshot of migration progress at a point in time.
/// Provides computed properties for percent complete, elapsed time, and estimated remaining time.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-04)")]
public readonly record struct MigrationProgress(
    long BlocksCopied,
    long TotalBlocks,
    long BytesCopied,
    long TotalBytes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    /// <summary>
    /// Percentage of blocks copied (0.0 to 100.0). Returns 0.0 when TotalBlocks is zero.
    /// </summary>
    public double PercentComplete => TotalBlocks > 0
        ? (double)BlocksCopied / TotalBlocks * 100.0
        : 0.0;

    /// <summary>
    /// Elapsed time since the migration started. Uses completion time if available, otherwise current time.
    /// </summary>
    public TimeSpan Elapsed => (CompletedAtUtc ?? DateTimeOffset.UtcNow) - StartedAtUtc;

    /// <summary>
    /// Estimated time remaining based on linear extrapolation from current copy rate.
    /// Returns null when no blocks have been copied yet (rate is unknown).
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (BlocksCopied <= 0 || TotalBlocks <= 0)
                return null;

            long remaining = TotalBlocks - BlocksCopied;
            if (remaining <= 0)
                return TimeSpan.Zero;

            double elapsed = Elapsed.TotalSeconds;
            if (elapsed <= 0.0)
                return null;

            double rate = BlocksCopied / elapsed; // blocks per second
            return TimeSpan.FromSeconds(remaining / rate);
        }
    }
}

/// <summary>
/// Tracks the full state of a shard migration: identity, phase, progress, and failure reason.
/// Phase transitions are validated to enforce the legal state machine.
/// Binary-serializable to a fixed 128-byte layout for WAL/journal persistence.
/// </summary>
/// <remarks>
/// Binary layout (128 bytes, all integers little-endian):
/// <list type="table">
/// <item><term>[0..15]</term><description>MigrationId (GUID)</description></item>
/// <item><term>[16..31]</term><description>SourceShardId (GUID)</description></item>
/// <item><term>[32..47]</term><description>DestinationShardId (GUID)</description></item>
/// <item><term>[48..51]</term><description>StartSlot (Int32)</description></item>
/// <item><term>[52..55]</term><description>EndSlot (Int32)</description></item>
/// <item><term>[56]</term><description>Phase (byte)</description></item>
/// <item><term>[57..63]</term><description>Reserved</description></item>
/// <item><term>[64..71]</term><description>BlocksCopied (Int64)</description></item>
/// <item><term>[72..79]</term><description>TotalBlocks (Int64)</description></item>
/// <item><term>[80..87]</term><description>BytesCopied (Int64)</description></item>
/// <item><term>[88..95]</term><description>TotalBytes (Int64)</description></item>
/// <item><term>[96..103]</term><description>StartedAtUtcTicks (Int64)</description></item>
/// <item><term>[104..111]</term><description>CompletedAtUtcTicks (Int64, 0 if null)</description></item>
/// <item><term>[112..127]</term><description>Reserved</description></item>
/// </list>
/// Thread safety: <see cref="Phase"/> reads are volatile. <see cref="TransitionTo"/> and
/// <see cref="UpdateProgress"/> should be called from a single writer thread (the migration engine).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-04)")]
public sealed class ShardMigrationState
{
    /// <summary>
    /// Fixed binary serialization size in bytes.
    /// </summary>
    public const int SerializedSize = 128;

    private volatile MigrationPhase _phase;
    private MigrationProgress _progress;

    /// <summary>
    /// Creates a new migration state in the <see cref="MigrationPhase.Pending"/> phase.
    /// </summary>
    /// <param name="sourceShardId">VDE ID of the source shard being migrated.</param>
    /// <param name="destinationShardId">VDE ID of the destination shard receiving data.</param>
    /// <param name="startSlot">First routing table slot (inclusive) owned by this shard.</param>
    /// <param name="endSlot">Last routing table slot (exclusive) owned by this shard.</param>
    public ShardMigrationState(Guid sourceShardId, Guid destinationShardId, int startSlot, int endSlot)
    {
        MigrationId = Guid.NewGuid();
        SourceShardId = sourceShardId;
        DestinationShardId = destinationShardId;
        StartSlot = startSlot;
        EndSlot = endSlot;
        _phase = MigrationPhase.Pending;
        _progress = new MigrationProgress(0, 0, 0, 0, DateTimeOffset.UtcNow, null);
    }

    /// <summary>
    /// Private constructor for deserialization.
    /// </summary>
    private ShardMigrationState(
        Guid migrationId, Guid sourceShardId, Guid destinationShardId,
        int startSlot, int endSlot, MigrationPhase phase, MigrationProgress progress)
    {
        MigrationId = migrationId;
        SourceShardId = sourceShardId;
        DestinationShardId = destinationShardId;
        StartSlot = startSlot;
        EndSlot = endSlot;
        _phase = phase;
        _progress = progress;
    }

    /// <summary>Unique identifier for this migration operation.</summary>
    public Guid MigrationId { get; }

    /// <summary>VDE ID of the source shard being migrated away from.</summary>
    public Guid SourceShardId { get; }

    /// <summary>VDE ID of the destination shard receiving the migrated data.</summary>
    public Guid DestinationShardId { get; }

    /// <summary>First routing table slot (inclusive) in the migrated range.</summary>
    public int StartSlot { get; }

    /// <summary>Last routing table slot (exclusive) in the migrated range.</summary>
    public int EndSlot { get; }

    /// <summary>
    /// Current migration phase. Reads are volatile for thread-safe observation by redirect lookups.
    /// </summary>
    public MigrationPhase Phase => _phase;

    /// <summary>
    /// Current progress snapshot.
    /// </summary>
    public MigrationProgress Progress => _progress;

    /// <summary>
    /// Failure reason if the migration entered <see cref="MigrationPhase.Failed"/> or
    /// <see cref="MigrationPhase.RolledBack"/>.
    /// </summary>
    public string? FailureReason { get; internal set; }

    /// <summary>
    /// Whether the migration is in a terminal phase (Completed, RolledBack, or Failed).
    /// </summary>
    public bool IsTerminal => _phase is MigrationPhase.Completed
        or MigrationPhase.RolledBack
        or MigrationPhase.Failed;

    /// <summary>
    /// Transitions to the specified phase after validating the transition is legal.
    /// </summary>
    /// <param name="next">Target phase.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the transition from the current phase to <paramref name="next"/> is not allowed.
    /// </exception>
    public void TransitionTo(MigrationPhase next)
    {
        MigrationPhase current = _phase;

        if (!IsLegalTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Illegal migration phase transition from {current} to {next}.");
        }

        _phase = next;

        // Record completion time for terminal phases
        if (next is MigrationPhase.Completed or MigrationPhase.RolledBack or MigrationPhase.Failed)
        {
            _progress = _progress with { CompletedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>
    /// Updates the progress snapshot with current copy metrics.
    /// </summary>
    /// <param name="blocksCopied">Total blocks copied so far.</param>
    /// <param name="totalBlocks">Total blocks that need to be copied.</param>
    /// <param name="bytesCopied">Total bytes copied so far.</param>
    /// <param name="totalBytes">Total bytes that need to be copied.</param>
    public void UpdateProgress(long blocksCopied, long totalBlocks, long bytesCopied, long totalBytes)
    {
        _progress = new MigrationProgress(
            blocksCopied, totalBlocks, bytesCopied, totalBytes,
            _progress.StartedAtUtc, _progress.CompletedAtUtc);
    }

    /// <summary>
    /// Serializes this migration state to a 128-byte span using BinaryPrimitives.
    /// </summary>
    /// <param name="destination">Target span, must be at least <see cref="SerializedSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when the span is too small.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SerializedSize)
            throw new ArgumentException($"Destination span must be at least {SerializedSize} bytes.", nameof(destination));

        // Clear reserved areas
        destination[..SerializedSize].Clear();

        MigrationId.TryWriteBytes(destination[0..16]);
        SourceShardId.TryWriteBytes(destination[16..32]);
        DestinationShardId.TryWriteBytes(destination[32..48]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[48..52], StartSlot);
        BinaryPrimitives.WriteInt32LittleEndian(destination[52..56], EndSlot);
        destination[56] = (byte)_phase;
        // [57..63] reserved

        MigrationProgress p = _progress;
        BinaryPrimitives.WriteInt64LittleEndian(destination[64..72], p.BlocksCopied);
        BinaryPrimitives.WriteInt64LittleEndian(destination[72..80], p.TotalBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[80..88], p.BytesCopied);
        BinaryPrimitives.WriteInt64LittleEndian(destination[88..96], p.TotalBytes);
        BinaryPrimitives.WriteInt64LittleEndian(destination[96..104], p.StartedAtUtc.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[104..112], p.CompletedAtUtc?.UtcTicks ?? 0L);
        // [112..127] reserved
    }

    /// <summary>
    /// Deserializes a migration state from a 128-byte span.
    /// </summary>
    /// <param name="source">Source span, must be at least <see cref="SerializedSize"/> bytes.</param>
    /// <returns>The deserialized migration state.</returns>
    /// <exception cref="ArgumentException">Thrown when the span is too small.</exception>
    public static ShardMigrationState ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < SerializedSize)
            throw new ArgumentException($"Source span must be at least {SerializedSize} bytes.", nameof(source));

        var migrationId = new Guid(source[0..16]);
        var sourceShardId = new Guid(source[16..32]);
        var destinationShardId = new Guid(source[32..48]);
        int startSlot = BinaryPrimitives.ReadInt32LittleEndian(source[48..52]);
        int endSlot = BinaryPrimitives.ReadInt32LittleEndian(source[52..56]);
        var phase = (MigrationPhase)source[56];

        long blocksCopied = BinaryPrimitives.ReadInt64LittleEndian(source[64..72]);
        long totalBlocks = BinaryPrimitives.ReadInt64LittleEndian(source[72..80]);
        long bytesCopied = BinaryPrimitives.ReadInt64LittleEndian(source[80..88]);
        long totalBytes = BinaryPrimitives.ReadInt64LittleEndian(source[88..96]);
        long startedTicks = BinaryPrimitives.ReadInt64LittleEndian(source[96..104]);
        long completedTicks = BinaryPrimitives.ReadInt64LittleEndian(source[104..112]);

        var progress = new MigrationProgress(
            blocksCopied, totalBlocks, bytesCopied, totalBytes,
            new DateTimeOffset(startedTicks, TimeSpan.Zero),
            completedTicks != 0L ? new DateTimeOffset(completedTicks, TimeSpan.Zero) : null);

        return new ShardMigrationState(
            migrationId, sourceShardId, destinationShardId,
            startSlot, endSlot, phase, progress);
    }

    /// <summary>
    /// Determines whether a phase transition is legal according to the migration state machine.
    /// </summary>
    private static bool IsLegalTransition(MigrationPhase from, MigrationPhase to)
    {
        // Terminal phases cannot transition
        if (from is MigrationPhase.Completed or MigrationPhase.RolledBack or MigrationPhase.Failed)
            return false;

        // Any non-terminal can go to RolledBack or Failed
        if (to is MigrationPhase.RolledBack or MigrationPhase.Failed)
            return true;

        // Forward transitions in the happy path
        return (from, to) switch
        {
            (MigrationPhase.Pending, MigrationPhase.Preparing) => true,
            (MigrationPhase.Preparing, MigrationPhase.CopyingBlocks) => true,
            (MigrationPhase.CopyingBlocks, MigrationPhase.CatchingUp) => true,
            (MigrationPhase.CatchingUp, MigrationPhase.Switching) => true,
            (MigrationPhase.Switching, MigrationPhase.Completed) => true,
            _ => false
        };
    }
}
