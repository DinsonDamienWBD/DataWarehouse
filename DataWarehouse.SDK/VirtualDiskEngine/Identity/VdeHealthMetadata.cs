using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Health state of a VDE volume, tracking its lifecycle from creation through
/// mounting, clean/dirty shutdown, recovery, and degraded operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- health state enum (VTMP-09)")]
public enum VdeHealthState : byte
{
    /// <summary>VDE created but never mounted.</summary>
    Created = 0,

    /// <summary>Cleanly unmounted -- no pending operations.</summary>
    Clean = 1,

    /// <summary>Currently mounted (dirty flag -- unclean if found on next open).</summary>
    Mounted = 2,

    /// <summary>Was mounted, detected unclean shutdown on next open.</summary>
    DirtyShutdown = 3,

    /// <summary>Recovery or repair operation in progress.</summary>
    Recovering = 4,

    /// <summary>Operating with known issues (some checks failed but TamperResponse allowed open).</summary>
    Degraded = 5
}

/// <summary>
/// Tracks operational health telemetry for a VDE volume: state machine transitions,
/// mount/unmount counts, error counts, timestamps, and cumulative mounted duration.
/// Serializes to a fixed 64-byte buffer for storage in the integrity anchor or
/// extended metadata reserved area.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- health metadata (VTMP-09)")]
public sealed class VdeHealthMetadata
{
    /// <summary>Serialized size in bytes.</summary>
    public const int SerializedSize = 64;

    /// <summary>Size of the reserved area at the end of the serialized buffer.</summary>
    private const int ReservedSize = 12;

    /// <summary>Padding after the State byte to align to 4 bytes.</summary>
    private const int PaddingSize = 3;

    // ── Fields ──────────────────────────────────────────────────────────

    /// <summary>Current health state of the volume.</summary>
    public VdeHealthState State { get; private set; }

    /// <summary>Total number of times this volume has been mounted.</summary>
    public long MountCount { get; private set; }

    /// <summary>Total number of errors recorded on this volume.</summary>
    public long ErrorCount { get; private set; }

    /// <summary>Volume creation timestamp (UTC ticks).</summary>
    public long CreationTimestampUtcTicks { get; }

    /// <summary>Timestamp of the most recent mount operation (UTC ticks).</summary>
    public long LastMountTimestampUtcTicks { get; private set; }

    /// <summary>Timestamp of the most recent unmount operation (UTC ticks).</summary>
    public long LastUnmountTimestampUtcTicks { get; private set; }

    /// <summary>Cumulative time the volume has been mounted (UTC ticks).</summary>
    public long TotalMountedDurationTicks { get; private set; }

    // ── Constructor ─────────────────────────────────────────────────────

    private VdeHealthMetadata(
        VdeHealthState state,
        long mountCount,
        long errorCount,
        long creationTimestampUtcTicks,
        long lastMountTimestampUtcTicks,
        long lastUnmountTimestampUtcTicks,
        long totalMountedDurationTicks)
    {
        State = state;
        MountCount = mountCount;
        ErrorCount = errorCount;
        CreationTimestampUtcTicks = creationTimestampUtcTicks;
        LastMountTimestampUtcTicks = lastMountTimestampUtcTicks;
        LastUnmountTimestampUtcTicks = lastUnmountTimestampUtcTicks;
        TotalMountedDurationTicks = totalMountedDurationTicks;
    }

    // ── Factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new health metadata instance for a freshly created volume.
    /// State is <see cref="VdeHealthState.Created"/>, all counters are zero.
    /// </summary>
    /// <returns>A new VdeHealthMetadata with initial values.</returns>
    public static VdeHealthMetadata CreateNew()
    {
        return new VdeHealthMetadata(
            state: VdeHealthState.Created,
            mountCount: 0,
            errorCount: 0,
            creationTimestampUtcTicks: DateTimeOffset.UtcNow.Ticks,
            lastMountTimestampUtcTicks: 0,
            lastUnmountTimestampUtcTicks: 0,
            totalMountedDurationTicks: 0);
    }

    // ── State Transitions ───────────────────────────────────────────────

    /// <summary>
    /// Records a mount operation: transitions to <see cref="VdeHealthState.Mounted"/>,
    /// increments mount count, and records the mount timestamp.
    /// </summary>
    public void RecordMount()
    {
        State = VdeHealthState.Mounted;
        MountCount++;
        LastMountTimestampUtcTicks = DateTimeOffset.UtcNow.Ticks;
    }

    /// <summary>
    /// Records a clean unmount: accumulates mounted duration, transitions to
    /// <see cref="VdeHealthState.Clean"/>, and records the unmount timestamp.
    /// </summary>
    public void RecordUnmount()
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        if (LastMountTimestampUtcTicks > 0)
        {
            TotalMountedDurationTicks += now - LastMountTimestampUtcTicks;
        }
        State = VdeHealthState.Clean;
        LastUnmountTimestampUtcTicks = now;
    }

    /// <summary>Records an error occurrence by incrementing the error counter.</summary>
    public void RecordError()
    {
        ErrorCount++;
    }

    /// <summary>
    /// Records that a dirty (unclean) shutdown was detected.
    /// Transitions to <see cref="VdeHealthState.DirtyShutdown"/>.
    /// </summary>
    public void RecordDirtyShutdown()
    {
        State = VdeHealthState.DirtyShutdown;
    }

    /// <summary>
    /// Records the start of a recovery/repair operation.
    /// Transitions to <see cref="VdeHealthState.Recovering"/>.
    /// </summary>
    public void RecordRecoveryStart()
    {
        State = VdeHealthState.Recovering;
    }

    /// <summary>
    /// Records successful completion of a recovery/repair operation.
    /// Transitions to <see cref="VdeHealthState.Clean"/>.
    /// </summary>
    public void RecordRecoveryComplete()
    {
        State = VdeHealthState.Clean;
    }

    /// <summary>
    /// Records that the volume is operating in a degraded state (some checks failed
    /// but TamperResponse policy allowed the volume to remain open).
    /// Transitions to <see cref="VdeHealthState.Degraded"/>.
    /// </summary>
    public void RecordDegraded()
    {
        State = VdeHealthState.Degraded;
    }

    // ── Serialization ───────────────────────────────────────────────────
    // Layout (64 bytes):
    // [State:1][Padding:3][MountCount:8 LE][ErrorCount:8 LE][CreationTs:8 LE]
    // [LastMountTs:8 LE][LastUnmountTs:8 LE][TotalMountedDuration:8 LE][Reserved:12]

    /// <summary>
    /// Serializes the health metadata into a fixed 64-byte buffer.
    /// </summary>
    /// <param name="hm">The health metadata to serialize.</param>
    /// <param name="buffer">Destination buffer, must be at least 64 bytes.</param>
    public static void Serialize(VdeHealthMetadata hm, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        buffer.Slice(0, SerializedSize).Clear();

        int offset = 0;

        // State (1 byte)
        buffer[offset] = (byte)hm.State;
        offset += 1;

        // Padding (3 bytes, already zeroed)
        offset += PaddingSize;

        // MountCount (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), hm.MountCount);
        offset += 8;

        // ErrorCount (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), hm.ErrorCount);
        offset += 8;

        // CreationTimestampUtcTicks (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), hm.CreationTimestampUtcTicks);
        offset += 8;

        // LastMountTimestampUtcTicks (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), hm.LastMountTimestampUtcTicks);
        offset += 8;

        // LastUnmountTimestampUtcTicks (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), hm.LastUnmountTimestampUtcTicks);
        offset += 8;

        // TotalMountedDurationTicks (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), hm.TotalMountedDurationTicks);
        // offset += 8; // Reserved 12 bytes already zeroed
    }

    /// <summary>
    /// Deserializes health metadata from a fixed 64-byte buffer.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least 64 bytes.</param>
    /// <returns>The deserialized health metadata.</returns>
    public static VdeHealthMetadata Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        int offset = 0;

        var state = (VdeHealthState)buffer[offset];
        offset += 1 + PaddingSize;

        var mountCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var errorCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var creationTs = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var lastMountTs = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var lastUnmountTs = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var totalMountedDuration = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        return new VdeHealthMetadata(
            state, mountCount, errorCount, creationTs,
            lastMountTs, lastUnmountTs, totalMountedDuration);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"VdeHealthMetadata(State={State}, Mounts={MountCount}, Errors={ErrorCount})";
}
