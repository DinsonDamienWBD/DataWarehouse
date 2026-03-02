using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.QoS;

/// <summary>
/// A 64-byte QoS Policy Vault record (Policy Vault type tag <see cref="PolicyVaultTypeTag"/>)
/// defining the I/O contract for one (TenantId, QoSClass) combination.
/// </summary>
/// <remarks>
/// Up to 512 records fit in the 2-block Policy Vault (32 KiB minus headers).
/// Changing IOPS limits, bandwidth caps, or latency deadlines for a tenant requires only a
/// single 64-byte write to the appropriate Policy Vault record — no inode or extent rewrites.
///
/// Wire format (64 bytes, all integers little-endian):
/// <code>
///   +0x00  2   TenantId               uint16 LE
///   +0x02  1   QoSClass               uint8 (0-7)
///   +0x03  1   Flags                  Bit 0: ENABLED, Bit 1: HARD_LIMIT
///   +0x04  4   MaxIops                uint32 LE (0 = unlimited)
///   +0x08  4   MinGuaranteedIops      uint32 LE (0 = best-effort)
///   +0x0C  4   MaxBandwidthMBps       uint32 LE (0 = unlimited)
///   +0x10  4   MinGuaranteedBandMBps  uint32 LE (0 = best-effort)
///   +0x14  4   LatencyDeadlineUs      uint32 LE (0 = none)
///   +0x18  4   BurstIops              uint32 LE (token bucket burst above MaxIops)
///   +0x1C  2   BurstDurationMs        uint16 LE
///   +0x1E  2   PriorityWeight         uint16 LE (weighted-fair-queue weight)
///   +0x20  8   BytesQuota             uint64 LE (0 = unlimited)
///   +0x28  8   BytesUsed              uint64 LE (updated by Background Scanner)
///   +0x30  16  Reserved               (zero)
/// </code>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: 64-byte QoS Policy Vault record (VOPT-71)")]
public readonly struct QoSPolicyRecord : IEquatable<QoSPolicyRecord>
{
    /// <summary>Serialized size of a QoS Policy Vault record in bytes.</summary>
    public const int Size = 64;

    /// <summary>Policy Vault type tag identifying QoS policy records.</summary>
    public const uint PolicyVaultTypeTag = 0x0003;

    // -----------------------------------------------------------------------
    // Fields
    // -----------------------------------------------------------------------

    /// <summary>Tenant this policy applies to (+0x00, uint16 LE).</summary>
    public ushort TenantId { get; }

    /// <summary>QoS class this policy governs (+0x02, uint8, 0-7).</summary>
    public QoSClass QoSClass { get; }

    /// <summary>
    /// Policy flags (+0x03):
    /// Bit 0 = ENABLED (policy is active),
    /// Bit 1 = HARD_LIMIT (MaxIops/MaxBandwidthMBps are hard caps, not soft targets).
    /// </summary>
    public byte Flags { get; }

    /// <summary>Maximum IOPS allowed (+0x04). 0 = unlimited.</summary>
    public uint MaxIops { get; }

    /// <summary>Guaranteed minimum IOPS (+0x08). 0 = best-effort.</summary>
    public uint MinGuaranteedIops { get; }

    /// <summary>Maximum bandwidth in MiB/s (+0x0C). 0 = unlimited.</summary>
    public uint MaxBandwidthMBps { get; }

    /// <summary>Guaranteed minimum bandwidth in MiB/s (+0x10). 0 = best-effort.</summary>
    public uint MinGuaranteedBandMBps { get; }

    /// <summary>Target latency deadline in microseconds (+0x14). 0 = none.</summary>
    public uint LatencyDeadlineUs { get; }

    /// <summary>Burst IOPS allowed above <see cref="MaxIops"/> (token-bucket, +0x18).</summary>
    public uint BurstIops { get; }

    /// <summary>Burst duration window in milliseconds (+0x1C).</summary>
    public ushort BurstDurationMs { get; }

    /// <summary>Relative weight for weighted-fair-queue scheduling (+0x1E).</summary>
    public ushort PriorityWeight { get; }

    /// <summary>Storage quota in bytes (+0x20). 0 = unlimited.</summary>
    public ulong BytesQuota { get; }

    /// <summary>
    /// Current storage usage in bytes (+0x28). Updated periodically by the
    /// Background Scanner thread; not guaranteed to be accurate to the millisecond.
    /// </summary>
    public ulong BytesUsed { get; }

    // -----------------------------------------------------------------------
    // Derived helpers
    // -----------------------------------------------------------------------

    /// <summary>True when the ENABLED flag (bit 0) is set.</summary>
    public bool IsEnabled => (Flags & 0x01) != 0;

    /// <summary>True when the HARD_LIMIT flag (bit 1) is set.</summary>
    public bool IsHardLimit => (Flags & 0x02) != 0;

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------

    /// <summary>Creates a new QoS Policy Vault record.</summary>
    public QoSPolicyRecord(
        ushort tenantId,
        QoSClass qosClass,
        byte flags,
        uint maxIops,
        uint minGuaranteedIops,
        uint maxBandwidthMBps,
        uint minGuaranteedBandMBps,
        uint latencyDeadlineUs,
        uint burstIops,
        ushort burstDurationMs,
        ushort priorityWeight,
        ulong bytesQuota,
        ulong bytesUsed)
    {
        TenantId = tenantId;
        QoSClass = qosClass;
        Flags = flags;
        MaxIops = maxIops;
        MinGuaranteedIops = minGuaranteedIops;
        MaxBandwidthMBps = maxBandwidthMBps;
        MinGuaranteedBandMBps = minGuaranteedBandMBps;
        LatencyDeadlineUs = latencyDeadlineUs;
        BurstIops = burstIops;
        BurstDurationMs = burstDurationMs;
        PriorityWeight = priorityWeight;
        BytesQuota = bytesQuota;
        BytesUsed = bytesUsed;
    }

    // -----------------------------------------------------------------------
    // Serialization
    // -----------------------------------------------------------------------

    /// <summary>
    /// Serializes this record to exactly <see cref="Size"/> (64) bytes in little-endian format.
    /// </summary>
    /// <param name="buffer">Target buffer; must be at least <see cref="Size"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small.</exception>
    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0x00..0x02], TenantId);         // +0x00
        buffer[0x02] = (byte)QoSClass;                                                   // +0x02
        buffer[0x03] = Flags;                                                             // +0x03
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x04..0x08], MaxIops);           // +0x04
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x08..0x0C], MinGuaranteedIops); // +0x08
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x0C..0x10], MaxBandwidthMBps);  // +0x0C
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x10..0x14], MinGuaranteedBandMBps); // +0x10
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x14..0x18], LatencyDeadlineUs); // +0x14
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x18..0x1C], BurstIops);         // +0x18
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0x1C..0x1E], BurstDurationMs);   // +0x1C
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0x1E..0x20], PriorityWeight);    // +0x1E
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[0x20..0x28], BytesQuota);        // +0x20
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[0x28..0x30], BytesUsed);         // +0x28
        buffer[0x30..0x40].Clear(); // Reserved — 16 bytes of zero                       // +0x30
    }

    /// <summary>
    /// Deserializes a <see cref="QoSPolicyRecord"/> from exactly <see cref="Size"/> (64) bytes
    /// in little-endian format.
    /// </summary>
    /// <param name="buffer">Source buffer; must be at least <see cref="Size"/> bytes.</param>
    /// <returns>The deserialized <see cref="QoSPolicyRecord"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small.</exception>
    public static QoSPolicyRecord ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        ushort tenantId             = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0x00..0x02]);
        var    qosClass             = (QoSClass)buffer[0x02];
        byte   flags                = buffer[0x03];
        uint   maxIops              = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x04..0x08]);
        uint   minGuaranteedIops    = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x08..0x0C]);
        uint   maxBandwidthMBps     = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x0C..0x10]);
        uint   minGuaranteedBand    = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x10..0x14]);
        uint   latencyDeadlineUs    = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x14..0x18]);
        uint   burstIops            = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x18..0x1C]);
        ushort burstDurationMs      = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0x1C..0x1E]);
        ushort priorityWeight       = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0x1E..0x20]);
        ulong  bytesQuota           = BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x20..0x28]);
        ulong  bytesUsed            = BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x28..0x30]);
        // +0x30: 16 reserved bytes — read and discard

        return new QoSPolicyRecord(
            tenantId, qosClass, flags,
            maxIops, minGuaranteedIops,
            maxBandwidthMBps, minGuaranteedBand,
            latencyDeadlineUs, burstIops,
            burstDurationMs, priorityWeight,
            bytesQuota, bytesUsed);
    }

    // -----------------------------------------------------------------------
    // Equality
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public bool Equals(QoSPolicyRecord other)
        => TenantId             == other.TenantId
        && QoSClass             == other.QoSClass
        && Flags                == other.Flags
        && MaxIops              == other.MaxIops
        && MinGuaranteedIops    == other.MinGuaranteedIops
        && MaxBandwidthMBps     == other.MaxBandwidthMBps
        && MinGuaranteedBandMBps == other.MinGuaranteedBandMBps
        && LatencyDeadlineUs    == other.LatencyDeadlineUs
        && BurstIops            == other.BurstIops
        && BurstDurationMs      == other.BurstDurationMs
        && PriorityWeight       == other.PriorityWeight
        && BytesQuota           == other.BytesQuota
        && BytesUsed            == other.BytesUsed;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QoSPolicyRecord other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            HashCode.Combine(TenantId, QoSClass, Flags, MaxIops, MinGuaranteedIops),
            HashCode.Combine(MaxBandwidthMBps, MinGuaranteedBandMBps, LatencyDeadlineUs, BurstIops),
            HashCode.Combine(BurstDurationMs, PriorityWeight, BytesQuota, BytesUsed));

    /// <summary>Equality operator.</summary>
    public static bool operator ==(QoSPolicyRecord left, QoSPolicyRecord right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(QoSPolicyRecord left, QoSPolicyRecord right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"QoSPolicyRecord(Tenant={TenantId}, Class={QoSClass}, MaxIops={MaxIops}, MaxBW={MaxBandwidthMBps} MiB/s, Enabled={IsEnabled})";
}
