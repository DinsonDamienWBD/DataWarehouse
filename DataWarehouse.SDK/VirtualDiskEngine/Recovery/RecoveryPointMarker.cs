using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Recovery;

/// <summary>
/// Recovery Point Marker (RPMK): a 48-byte WAL record inserted at configurable RPO
/// (Recovery Point Objective) intervals into the metadata WAL. These markers define
/// consistent recovery points that tools can use for point-in-time recovery.
///
/// Wire layout (exactly 48 bytes):
///   +0x00  uint   RecordType        "RP01" (0x52503031)
///   +0x04  uint   Flags             Bit 0 = DATA_WAL_CONSISTENT, Bit 1 = APPLICATION_CONSISTENT
///   +0x08  ulong  MarkerSequence    monotonically increasing sequence number
///   +0x10  ulong  UtcNanoseconds    wall-clock time (UTC nanoseconds since Unix epoch)
///   +0x18  ulong  MetadataWalLsn    metadata WAL log sequence number at this marker
///   +0x20  ulong  DataWalLsn        data WAL LSN (0 if not synced at marker time)
///   +0x28  ulong  MerkleRootSnapshot block number of Merkle root snapshot block
///
/// The marker is appended to the metadata WAL ring at the configured RPO interval
/// (e.g. every 15 seconds, or every N transactions). Recovery tools scan the metadata WAL
/// for RP01 records and use the highest-sequence consistent marker as the recovery target.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-78: Recovery Point Marker (48-byte RPMK WAL record)")]
public readonly struct RecoveryPointMarker : IEquatable<RecoveryPointMarker>
{
    // ── Layout Constants ─────────────────────────────────────────────────

    /// <summary>Total size of one Recovery Point Marker record in bytes.</summary>
    public const int Size = 48;

    /// <summary>Record type identifier ("RP01" big-endian, 0x52503031).</summary>
    public const uint RecordType = 0x52503031;

    private const uint FlagDataWalConsistent = 0x0001;
    private const uint FlagApplicationConsistent = 0x0002;

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>
    /// Flags field at +0x04.
    /// Bit 0 = DATA_WAL_CONSISTENT: data WAL has been flushed and is consistent at this marker.
    /// Bit 1 = APPLICATION_CONSISTENT: application-level quiesce confirmed before this marker.
    /// </summary>
    public uint Flags { get; }

    /// <summary>Monotonically increasing sequence number for ordering markers.</summary>
    public ulong MarkerSequence { get; }

    /// <summary>Wall-clock time of the marker (UTC nanoseconds since Unix epoch).</summary>
    public ulong UtcNanoseconds { get; }

    /// <summary>Metadata WAL log sequence number at the time this marker was written.</summary>
    public ulong MetadataWalLsn { get; }

    /// <summary>Data WAL log sequence number at the time this marker was written. 0 if not synced.</summary>
    public ulong DataWalLsn { get; }

    /// <summary>Block number of the Merkle root snapshot corresponding to this recovery point.</summary>
    public ulong MerkleRootSnapshot { get; }

    // ── Convenience Properties ───────────────────────────────────────────

    /// <summary>True when the data WAL is consistent at this recovery point (Flags bit 0).</summary>
    public bool IsDataWalConsistent => (Flags & FlagDataWalConsistent) != 0;

    /// <summary>True when an application-consistent snapshot was taken (Flags bit 1).</summary>
    public bool IsApplicationConsistent => (Flags & FlagApplicationConsistent) != 0;

    // ── Constructor ──────────────────────────────────────────────────────

    /// <summary>Creates a Recovery Point Marker with all fields specified.</summary>
    public RecoveryPointMarker(
        uint flags,
        ulong markerSequence,
        ulong utcNanoseconds,
        ulong metadataWalLsn,
        ulong dataWalLsn,
        ulong merkleRootSnapshot)
    {
        Flags = flags;
        MarkerSequence = markerSequence;
        UtcNanoseconds = utcNanoseconds;
        MetadataWalLsn = metadataWalLsn;
        DataWalLsn = dataWalLsn;
        MerkleRootSnapshot = merkleRootSnapshot;
    }

    // ── Factory ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Recovery Point Marker for the current moment, using the provided
    /// WAL sequence numbers and Merkle root snapshot block.
    /// </summary>
    public static RecoveryPointMarker Create(
        ulong sequence,
        ulong metadataWalLsn,
        ulong dataWalLsn,
        ulong merkleRootSnapshot,
        bool dataWalConsistent = true,
        bool applicationConsistent = false)
    {
        uint flags = 0;
        if (dataWalConsistent)    flags |= FlagDataWalConsistent;
        if (applicationConsistent) flags |= FlagApplicationConsistent;

        // UTC nanoseconds since Unix epoch
        var utcNs = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);

        return new RecoveryPointMarker(flags, sequence, utcNs, metadataWalLsn, dataWalLsn, merkleRootSnapshot);
    }

    // ── Serialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Writes this marker as exactly 48 bytes into <paramref name="buffer"/> starting at index 0.
    /// </summary>
    public void WriteTo(Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, Size);

        int offset = 0;

        // +0x00: RecordType
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), RecordType);
        offset += 4;

        // +0x04: Flags
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), Flags);
        offset += 4;

        // +0x08: MarkerSequence
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), MarkerSequence);
        offset += 8;

        // +0x10: UtcNanoseconds
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), UtcNanoseconds);
        offset += 8;

        // +0x18: MetadataWalLsn
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), MetadataWalLsn);
        offset += 8;

        // +0x20: DataWalLsn
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), DataWalLsn);
        offset += 8;

        // +0x28: MerkleRootSnapshot
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), MerkleRootSnapshot);
    }

    /// <summary>
    /// Reads a Recovery Point Marker from exactly 48 bytes starting at the beginning of
    /// <paramref name="buffer"/>. Validates the RecordType field.
    /// </summary>
    /// <exception cref="InvalidDataException">RecordType is not "RP01".</exception>
    public static RecoveryPointMarker ReadFrom(ReadOnlySpan<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, Size);

        int offset = 0;

        var recordType = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        if (recordType != RecordType)
            throw new InvalidDataException(
                $"RecoveryPointMarker record type mismatch: expected 0x{RecordType:X8} (RP01), got 0x{recordType:X8}.");

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var utcNs = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var metaLsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var dataLsn = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var merkle = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));

        return new RecoveryPointMarker(flags, sequence, utcNs, metaLsn, dataLsn, merkle);
    }

    // ── Equality ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(RecoveryPointMarker other) =>
        Flags == other.Flags
        && MarkerSequence == other.MarkerSequence
        && UtcNanoseconds == other.UtcNanoseconds
        && MetadataWalLsn == other.MetadataWalLsn
        && DataWalLsn == other.DataWalLsn
        && MerkleRootSnapshot == other.MerkleRootSnapshot;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RecoveryPointMarker other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(MarkerSequence, UtcNanoseconds, MetadataWalLsn);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RecoveryPointMarker left, RecoveryPointMarker right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RecoveryPointMarker left, RecoveryPointMarker right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"RPMK(Seq={MarkerSequence}, MetaLsn={MetadataWalLsn}, DataLsn={DataWalLsn}, " +
        $"DataWalOk={IsDataWalConsistent}, AppOk={IsApplicationConsistent})";
}
