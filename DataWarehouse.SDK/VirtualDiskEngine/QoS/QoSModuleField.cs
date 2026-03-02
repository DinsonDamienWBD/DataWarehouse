using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.QoS;

/// <summary>
/// Represents the 4-byte QOS module inode field stored in the Module Overflow Block
/// when the QOS module (bit 34 of the Module Registry) is active.
/// </summary>
/// <remarks>
/// Wire format (4 bytes, little-endian):
/// <code>
///   Bytes 0-1:  TenantId  (uint16 LE) — identifies the owning tenant
///   Byte  2:    QoSClass  (uint8)     — QoS class 0-7 matching <see cref="QoSClass"/>
///   Byte  3:    Reserved  (zero)      — must be zero on write; ignored on read
/// </code>
/// The <see cref="TenantId"/> and <see cref="QoSClass"/> together select which
/// QoS Policy Vault record (type tag 0x0003) applies to this inode's I/O operations.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: QOS module 4-byte inode field (VOPT-71)")]
public readonly struct QoSModuleField : IEquatable<QoSModuleField>
{
    /// <summary>Serialized size of the QOS module inode field in bytes.</summary>
    public const int Size = 4;

    /// <summary>Tenant identifier (uint16 LE at byte offset 0).</summary>
    public ushort TenantId { get; }

    /// <summary>QoS class (uint8 at byte offset 2). Values 0-7 match <see cref="QoSClass"/>.</summary>
    public QoSClass QoSClass { get; }

    /// <summary>Reserved byte at offset 3. Always zero.</summary>
    public byte Reserved => 0;

    /// <summary>Creates a new QOS module inode field value.</summary>
    /// <param name="tenantId">Tenant identifier (uint16).</param>
    /// <param name="qosClass">QoS class for this inode's I/O operations.</param>
    public QoSModuleField(ushort tenantId, QoSClass qosClass)
    {
        TenantId = tenantId;
        QoSClass = qosClass;
    }

    /// <summary>
    /// Serializes this field to exactly <see cref="Size"/> (4) bytes in little-endian format.
    /// </summary>
    /// <param name="buffer">Target buffer; must be at least <see cref="Size"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small.</exception>
    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], TenantId);
        buffer[2] = (byte)QoSClass;
        buffer[3] = 0; // Reserved — always zero
    }

    /// <summary>
    /// Deserializes a <see cref="QoSModuleField"/> from exactly <see cref="Size"/> (4) bytes
    /// in little-endian format.
    /// </summary>
    /// <param name="buffer">Source buffer; must be at least <see cref="Size"/> bytes.</param>
    /// <returns>The deserialized <see cref="QoSModuleField"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small.</exception>
    public static QoSModuleField ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Size)
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));

        ushort tenantId = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]);
        var qosClass = (QoSClass)buffer[2];
        // byte 3 is Reserved; read and discard

        return new QoSModuleField(tenantId, qosClass);
    }

    /// <inheritdoc />
    public bool Equals(QoSModuleField other)
        => TenantId == other.TenantId && QoSClass == other.QoSClass;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QoSModuleField other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TenantId, QoSClass);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(QoSModuleField left, QoSModuleField right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(QoSModuleField left, QoSModuleField right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"QoSModuleField(TenantId={TenantId}, QoSClass={QoSClass})";
}
