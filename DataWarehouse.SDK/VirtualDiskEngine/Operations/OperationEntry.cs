using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Operations;

/// <summary>
/// Identifies the type of long-running online operation tracked in the OPJR region.
/// Ten operation types are defined per the DWVD v2.1 spec (VOPT-74).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: OPJR operation journal (VOPT-74)")]
public enum OperationType : byte
{
    /// <summary>Online volume resize (grow or shrink).</summary>
    Resize = 0x01,

    /// <summary>RAID level migration (e.g. RAID-5 -> RAID-6).</summary>
    RaidMigrate = 0x02,

    /// <summary>Encrypt a previously unencrypted volume.</summary>
    Encrypt = 0x03,

    /// <summary>Decrypt a previously encrypted volume.</summary>
    Decrypt = 0x04,

    /// <summary>Re-key an already-encrypted volume with a new key.</summary>
    Rekey = 0x05,

    /// <summary>Defragment: relocate extents to reduce fragmentation.</summary>
    Defrag = 0x06,

    /// <summary>Tier migration: move blocks between storage tiers.</summary>
    TierMigrate = 0x07,

    /// <summary>Compress previously uncompressed data in-place.</summary>
    Compress = 0x08,

    /// <summary>Scrub: verify all data blocks against stored checksums.</summary>
    Scrub = 0x09,

    /// <summary>Rebalance: redistribute data across devices for even utilisation.</summary>
    Rebalance = 0x0A,
}

/// <summary>
/// State machine states for an online operation.
/// Terminal states: <see cref="Completed"/>, <see cref="Failed"/>, <see cref="Cancelled"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: OPJR operation journal (VOPT-74)")]
public enum OperationState : byte
{
    /// <summary>Operation has been created but not yet started.</summary>
    Queued = 0x00,

    /// <summary>Operation is actively executing.</summary>
    InProgress = 0x01,

    /// <summary>Operation is temporarily paused (can be resumed).</summary>
    Paused = 0x02,

    /// <summary>Operation completed successfully (terminal).</summary>
    Completed = 0x03,

    /// <summary>Operation failed with an error (terminal).</summary>
    Failed = 0x04,

    /// <summary>Operation was cancelled by the user (terminal).</summary>
    Cancelled = 0x05,
}

/// <summary>
/// Flags describing capabilities of an operation entry.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: OPJR operation journal (VOPT-74)")]
public enum OperationFlags : ushort
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>Operation supports pausing and resuming.</summary>
    Pausable = 1 << 0,

    /// <summary>Operation supports user-initiated cancellation.</summary>
    Cancellable = 1 << 1,
}

/// <summary>
/// Helper for <see cref="OperationState"/> state machine queries.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: OPJR operation journal (VOPT-74)")]
public static class OperationStateHelper
{
    /// <summary>
    /// Returns <see langword="true"/> if the state is terminal
    /// (<see cref="OperationState.Completed"/>, <see cref="OperationState.Failed"/>,
    /// or <see cref="OperationState.Cancelled"/>).
    /// Terminal entries are never modified further.
    /// </summary>
    public static bool IsTerminal(OperationState state) =>
        state is OperationState.Completed or OperationState.Failed or OperationState.Cancelled;
}

/// <summary>
/// A 128-byte operation journal entry stored in the OPJR region of the DWVD v2.1 format.
/// Records the full state machine, progress, and checkpoint data for one long-running operation.
///
/// Layout (all fields little-endian):
/// +0x00  OperationId         ulong   8 bytes
/// +0x08  Type                byte    1 byte
/// +0x09  State               byte    1 byte
/// +0x0A  Flags               ushort  2 bytes
/// +0x0C  ProgressPercent     uint    4 bytes  (0-1,000,000 for six decimal places)
/// +0x10  StartUtcTicks       ulong   8 bytes
/// +0x18  LastUpdateUtcTicks  ulong   8 bytes
/// +0x20  EstimatedEndTicks   ulong   8 bytes  (0 = unknown)
/// +0x28  SourceRegionStart   ulong   8 bytes
/// +0x30  TargetRegionStart   ulong   8 bytes
/// +0x38  TotalUnits          ulong   8 bytes
/// +0x40  CompletedUnits      ulong   8 bytes
/// +0x48  CheckpointData      ulong   8 bytes
/// +0x50  OperationPayload    32 bytes (operation-specific data)
/// +0x70  Reserved            16 bytes (zeroed)
/// Total = 128 bytes
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: OPJR operation journal (VOPT-74)")]
public readonly struct OperationEntry : IEquatable<OperationEntry>
{
    /// <summary>Fixed size of a serialised operation entry in bytes.</summary>
    public const int Size = 128;

    private const int PayloadOffset = 0x50;
    private const int PayloadLength = 32;
    private const int ReservedOffset = 0x70;
    private const int ReservedLength = 16;

    /// <summary>Monotonically increasing identifier for this operation.</summary>
    public ulong OperationId { get; }

    /// <summary>The category of operation.</summary>
    public OperationType Type { get; }

    /// <summary>Current state machine position.</summary>
    public OperationState State { get; }

    /// <summary>Capability flags for this operation.</summary>
    public OperationFlags Flags { get; }

    /// <summary>
    /// Progress in units of 0.0001 percent (i.e. divide by 10,000 to get percent).
    /// Range 0..1,000,000 represents 0.000000%..100.000000%.
    /// </summary>
    public uint ProgressPercent { get; }

    /// <summary>UTC start time expressed as <see cref="DateTime.Ticks"/>.</summary>
    public ulong StartUtcTicks { get; }

    /// <summary>UTC last-update time expressed as <see cref="DateTime.Ticks"/>.</summary>
    public ulong LastUpdateUtcTicks { get; }

    /// <summary>Estimated completion time as <see cref="DateTime.Ticks"/>, or 0 if unknown.</summary>
    public ulong EstimatedEndTicks { get; }

    /// <summary>Start block index of the source region.</summary>
    public ulong SourceRegionStart { get; }

    /// <summary>Start block index of the target region.</summary>
    public ulong TargetRegionStart { get; }

    /// <summary>Total work units (e.g. blocks to process).</summary>
    public ulong TotalUnits { get; }

    /// <summary>Completed work units since the last checkpoint.</summary>
    public ulong CompletedUnits { get; }

    /// <summary>
    /// Operation-specific checkpoint value allowing resume from the last safe position
    /// after a crash.
    /// </summary>
    public ulong CheckpointData { get; }

    /// <summary>
    /// Operation-specific payload (up to 32 bytes).  May encode parameters such as
    /// the target block count for a resize or the new RAID level for a migration.
    /// </summary>
    public byte[] OperationPayload { get; }

    /// <summary>
    /// Convenience accessor: progress as a 0.0–100.0 percentage with six decimal places.
    /// </summary>
    public double ProgressAsPercent => ProgressPercent / 10000.0;

    /// <summary>Constructs an operation entry with all fields explicitly specified.</summary>
    public OperationEntry(
        ulong operationId,
        OperationType type,
        OperationState state,
        OperationFlags flags,
        uint progressPercent,
        ulong startUtcTicks,
        ulong lastUpdateUtcTicks,
        ulong estimatedEndTicks,
        ulong sourceRegionStart,
        ulong targetRegionStart,
        ulong totalUnits,
        ulong completedUnits,
        ulong checkpointData,
        byte[]? operationPayload = null)
    {
        OperationId = operationId;
        Type = type;
        State = state;
        Flags = flags;
        ProgressPercent = progressPercent;
        StartUtcTicks = startUtcTicks;
        LastUpdateUtcTicks = lastUpdateUtcTicks;
        EstimatedEndTicks = estimatedEndTicks;
        SourceRegionStart = sourceRegionStart;
        TargetRegionStart = targetRegionStart;
        TotalUnits = totalUnits;
        CompletedUnits = completedUnits;
        CheckpointData = checkpointData;

        if (operationPayload is null || operationPayload.Length == 0)
        {
            OperationPayload = new byte[PayloadLength];
        }
        else if (operationPayload.Length == PayloadLength)
        {
            OperationPayload = operationPayload;
        }
        else
        {
            // Copy into a fixed-size array to guarantee exactly 32 bytes.
            var buf = new byte[PayloadLength];
            Array.Copy(operationPayload, buf, Math.Min(operationPayload.Length, PayloadLength));
            OperationPayload = buf;
        }
    }

    /// <summary>
    /// Writes this entry into <paramref name="buffer"/> at offset 0.
    /// The buffer must be at least <see cref="Size"/> (128) bytes.
    /// </summary>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        buffer.Slice(0, Size).Clear();

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x00), OperationId);
        buffer[0x08] = (byte)Type;
        buffer[0x09] = (byte)State;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(0x0A), (ushort)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(0x0C), ProgressPercent);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x10), StartUtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x18), LastUpdateUtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x20), EstimatedEndTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x28), SourceRegionStart);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x30), TargetRegionStart);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x38), TotalUnits);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x40), CompletedUnits);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(0x48), CheckpointData);

        OperationPayload.AsSpan().CopyTo(buffer.Slice(PayloadOffset, PayloadLength));
        // Reserved bytes at +0x70 are already zeroed by the Clear() above.
    }

    /// <summary>
    /// Reads an <see cref="OperationEntry"/> from the first <see cref="Size"/> bytes of
    /// <paramref name="buffer"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static OperationEntry ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        var operationId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x00));
        var type = (OperationType)buffer[0x08];
        var state = (OperationState)buffer[0x09];
        var flags = (OperationFlags)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(0x0A));
        var progressPercent = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0x0C));
        var startUtcTicks = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x10));
        var lastUpdateUtcTicks = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x18));
        var estimatedEndTicks = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x20));
        var sourceRegionStart = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x28));
        var targetRegionStart = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x30));
        var totalUnits = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x38));
        var completedUnits = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x40));
        var checkpointData = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x48));
        var payload = buffer.Slice(PayloadOffset, PayloadLength).ToArray();

        return new OperationEntry(
            operationId, type, state, flags, progressPercent,
            startUtcTicks, lastUpdateUtcTicks, estimatedEndTicks,
            sourceRegionStart, targetRegionStart,
            totalUnits, completedUnits, checkpointData, payload);
    }

    /// <inheritdoc />
    public bool Equals(OperationEntry other) =>
        OperationId == other.OperationId
        && Type == other.Type
        && State == other.State
        && Flags == other.Flags
        && ProgressPercent == other.ProgressPercent
        && StartUtcTicks == other.StartUtcTicks
        && LastUpdateUtcTicks == other.LastUpdateUtcTicks
        && EstimatedEndTicks == other.EstimatedEndTicks
        && SourceRegionStart == other.SourceRegionStart
        && TargetRegionStart == other.TargetRegionStart
        && TotalUnits == other.TotalUnits
        && CompletedUnits == other.CompletedUnits
        && CheckpointData == other.CheckpointData
        && OperationPayload.AsSpan().SequenceEqual(other.OperationPayload.AsSpan());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is OperationEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(OperationId, Type, State);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(OperationEntry left, OperationEntry right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(OperationEntry left, OperationEntry right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"Op({OperationId}, {Type}, {State}, {ProgressAsPercent:F2}%)";
}
