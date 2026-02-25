using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// A 512-byte extended inode for metadata-rich objects requiring nanosecond timestamps,
/// inline extended attributes, compression dictionary references, per-object encryption IVs,
/// and MVCC version chain pointers.
/// </summary>
/// <remarks>
/// Layout (512 bytes total):
/// <code>
///   [0..304)    Core InodeV2 fields         (304 bytes)
///   [304..312)  CreatedNs                   (long, nanosecond timestamp)
///   [312..320)  ModifiedNs                  (long, nanosecond timestamp)
///   [320..328)  AccessedNs                  (long, nanosecond timestamp)
///   [328..392)  InlineXattrArea             (64 bytes, inline extended attributes)
///   [392..400)  CompressionDictionaryRef    (long, block number of compression dict)
///   [400..416)  PerObjectEncryptionIV       (16 bytes, AES-256 initialization vector)
///   [416..424)  MvccVersionChainHead        (long, block number of version chain head)
///   [424..432)  MvccTransactionId           (long, current transaction ID)
///   [432..436)  SnapshotRefCount            (int, number of snapshots referencing this inode)
///   [436..452)  ReplicationVector           (16 bytes, vector clock for replication)
///   [452..512)  Reserved                    (60 bytes, for future modules)
/// </code>
/// Total extended area: 208 bytes (304 + 208 = 512).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-08 extended 512-byte inode with rich metadata")]
public sealed class ExtendedInode512
{
    /// <summary>Total serialized size of an extended inode in bytes.</summary>
    public const int SerializedSize = 512;

    /// <summary>Offset where the extended fields begin (after InodeV2 core).</summary>
    public const int ExtendedFieldsOffset = FormatConstants.InodeCoreSize; // 304

    /// <summary>Size of the extended fields area in bytes.</summary>
    public const int ExtendedFieldsSize = SerializedSize - ExtendedFieldsOffset; // 208

    /// <summary>Maximum size of the inline extended attributes area.</summary>
    public const int MaxInlineXattrSize = 64;

    /// <summary>Size of the per-object encryption IV in bytes.</summary>
    public const int EncryptionIVSize = 16;

    /// <summary>Size of the replication vector clock in bytes.</summary>
    public const int ReplicationVectorSize = 16;

    /// <summary>Size of the reserved area for future modules.</summary>
    public const int ReservedSize = 60;

    // ── Core InodeV2 fields ─────────────────────────────────────────────

    /// <summary>Unique inode number within this VDE.</summary>
    public long InodeNumber { get; set; }

    /// <summary>Object type (file, directory, symlink, etc.).</summary>
    public InodeType Type { get; set; }

    /// <summary>Inode-level flags.</summary>
    public InodeFlags Flags { get; set; }

    /// <summary>POSIX-style permissions.</summary>
    public InodePermissions Permissions { get; set; }

    /// <summary>Number of hard links pointing to this inode.</summary>
    public int LinkCount { get; set; }

    /// <summary>Owner identifier.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Logical size of the file data in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Allocated (physical) size on disk in bytes.</summary>
    public long AllocatedSize { get; set; }

    /// <summary>Creation timestamp (UTC ticks).</summary>
    public long CreatedUtc { get; set; }

    /// <summary>Last modification timestamp (UTC ticks).</summary>
    public long ModifiedUtc { get; set; }

    /// <summary>Last access timestamp (UTC ticks).</summary>
    public long AccessedUtc { get; set; }

    /// <summary>Last inode change timestamp (UTC ticks).</summary>
    public long ChangedUtc { get; set; }

    /// <summary>Number of valid inline extents (0-8).</summary>
    public int ExtentCount { get; set; }

    /// <summary>Inline extent array (always 8 slots).</summary>
    public InodeExtent[] Extents { get; set; } = new InodeExtent[FormatConstants.MaxExtentsPerInode];

    /// <summary>Block number of the indirect extent block (0 if none).</summary>
    public long IndirectExtentBlock { get; set; }

    /// <summary>Block number of the double-indirect extent block (0 if none).</summary>
    public long DoubleIndirectBlock { get; set; }

    /// <summary>Block number of the extended attribute block (0 if none).</summary>
    public long ExtendedAttributeBlock { get; set; }

    // ── Extended fields (208 bytes) ─────────────────────────────────────

    /// <summary>Nanosecond-precision creation timestamp.</summary>
    public long CreatedNs { get; set; }

    /// <summary>Nanosecond-precision modification timestamp.</summary>
    public long ModifiedNs { get; set; }

    /// <summary>Nanosecond-precision access timestamp.</summary>
    public long AccessedNs { get; set; }

    /// <summary>Inline extended attributes area (up to 64 bytes).</summary>
    public byte[] InlineXattrArea { get; set; } = new byte[MaxInlineXattrSize];

    /// <summary>Block number of the compression dictionary (0 if none).</summary>
    public long CompressionDictionaryRef { get; set; }

    /// <summary>Per-object encryption initialization vector (16 bytes, AES-256).</summary>
    public byte[] PerObjectEncryptionIV { get; set; } = new byte[EncryptionIVSize];

    /// <summary>Block number of the MVCC version chain head (0 if none).</summary>
    public long MvccVersionChainHead { get; set; }

    /// <summary>Current MVCC transaction ID for this inode.</summary>
    public long MvccTransactionId { get; set; }

    /// <summary>Number of snapshots referencing this inode.</summary>
    public int SnapshotRefCount { get; set; }

    /// <summary>Vector clock for multi-site replication (16 bytes).</summary>
    public byte[] ReplicationVector { get; set; } = new byte[ReplicationVectorSize];

    // ── Inline xattr accessors ──────────────────────────────────────────

    /// <summary>
    /// Gets the inline extended attributes as a read-only span.
    /// </summary>
    public ReadOnlySpan<byte> GetInlineXattrs() => InlineXattrArea.AsSpan();

    /// <summary>
    /// Sets the inline extended attributes (max 64 bytes).
    /// </summary>
    /// <param name="data">The xattr data to store inline.</param>
    /// <exception cref="ArgumentException">If data exceeds 64 bytes.</exception>
    public void SetInlineXattrs(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxInlineXattrSize)
            throw new ArgumentException(
                $"Inline xattr data must not exceed {MaxInlineXattrSize} bytes, got {data.Length}.",
                nameof(data));

        InlineXattrArea.AsSpan().Clear();
        data.CopyTo(InlineXattrArea);
    }

    // ── Conversion from standard inode ──────────────────────────────────

    /// <summary>
    /// Upgrades a standard InodeV2 to an ExtendedInode512, copying all core fields
    /// and initializing extended fields with defaults.
    /// </summary>
    /// <param name="standard">The standard inode to upgrade.</param>
    /// <returns>A new ExtendedInode512 with core fields from the standard inode.</returns>
    public static ExtendedInode512 FromStandard(InodeV2 standard)
    {
        var extended = new ExtendedInode512
        {
            InodeNumber = standard.InodeNumber,
            Type = standard.Type,
            Flags = standard.Flags,
            Permissions = standard.Permissions,
            LinkCount = standard.LinkCount,
            OwnerId = standard.OwnerId,
            Size = standard.Size,
            AllocatedSize = standard.AllocatedSize,
            CreatedUtc = standard.CreatedUtc,
            ModifiedUtc = standard.ModifiedUtc,
            AccessedUtc = standard.AccessedUtc,
            ChangedUtc = standard.ChangedUtc,
            ExtentCount = standard.ExtentCount,
            IndirectExtentBlock = standard.IndirectExtentBlock,
            DoubleIndirectBlock = standard.DoubleIndirectBlock,
            ExtendedAttributeBlock = standard.ExtendedAttributeBlock,
            // Extended fields default: nanosecond timestamps from tick-based ones
            CreatedNs = standard.CreatedUtc * 100, // ticks to nanoseconds
            ModifiedNs = standard.ModifiedUtc * 100,
            AccessedNs = standard.AccessedUtc * 100,
        };

        // Copy extents
        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            extended.Extents[i] = standard.Extents[i];
        }

        return extended;
    }

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes this extended inode to exactly 512 bytes.
    /// </summary>
    public static byte[] Serialize(ExtendedInode512 inode)
    {
        var buffer = new byte[SerializedSize];
        SerializeToSpan(inode, buffer);
        return buffer;
    }

    /// <summary>
    /// Serializes this extended inode into the provided buffer.
    /// </summary>
    public static void SerializeToSpan(ExtendedInode512 inode, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        buffer[..SerializedSize].Clear();

        // Core fields (304 bytes) -- same layout as InodeV2
        BinaryPrimitives.WriteInt64LittleEndian(buffer[0..8], inode.InodeNumber);
        buffer[8] = (byte)inode.Type;
        buffer[9] = (byte)inode.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], (ushort)inode.Permissions);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[12..16], inode.LinkCount);

        if (!inode.OwnerId.TryWriteBytes(buffer[16..32]))
            throw new InvalidOperationException("Failed to write OwnerId GUID.");

        BinaryPrimitives.WriteInt64LittleEndian(buffer[32..40], inode.Size);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[40..48], inode.AllocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[48..56], inode.CreatedUtc);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[56..64], inode.ModifiedUtc);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[64..72], inode.AccessedUtc);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[72..80], inode.ChangedUtc);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[80..84], inode.ExtentCount);

        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            int extOffset = 84 + i * InodeExtent.SerializedSize;
            InodeExtent.Serialize(in inode.Extents[i], buffer[extOffset..]);
        }

        BinaryPrimitives.WriteInt64LittleEndian(buffer[276..284], inode.IndirectExtentBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[284..292], inode.DoubleIndirectBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[292..300], inode.ExtendedAttributeBlock);
        // [300..304) reserved, already zeroed

        // Extended fields (208 bytes starting at offset 304)
        int o = ExtendedFieldsOffset;
        BinaryPrimitives.WriteInt64LittleEndian(buffer[o..(o + 8)], inode.CreatedNs);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(o + 8)..(o + 16)], inode.ModifiedNs);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(o + 16)..(o + 24)], inode.AccessedNs);

        // Inline xattr area (64 bytes at offset 328)
        inode.InlineXattrArea.AsSpan(0, MaxInlineXattrSize).CopyTo(buffer[(o + 24)..(o + 88)]);

        // Compression dictionary ref (8 bytes at offset 392)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(o + 88)..(o + 96)], inode.CompressionDictionaryRef);

        // Per-object encryption IV (16 bytes at offset 400)
        inode.PerObjectEncryptionIV.AsSpan(0, EncryptionIVSize).CopyTo(buffer[(o + 96)..(o + 112)]);

        // MVCC version chain head (8 bytes at offset 416)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(o + 112)..(o + 120)], inode.MvccVersionChainHead);

        // MVCC transaction ID (8 bytes at offset 424)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(o + 120)..(o + 128)], inode.MvccTransactionId);

        // Snapshot ref count (4 bytes at offset 432)
        BinaryPrimitives.WriteInt32LittleEndian(buffer[(o + 128)..(o + 132)], inode.SnapshotRefCount);

        // Replication vector (16 bytes at offset 436)
        inode.ReplicationVector.AsSpan(0, ReplicationVectorSize).CopyTo(buffer[(o + 132)..(o + 148)]);

        // [452..512) reserved, already zeroed
    }

    /// <summary>
    /// Deserializes an extended inode from a buffer of at least 512 bytes.
    /// </summary>
    public static ExtendedInode512 Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        var inode = new ExtendedInode512
        {
            // Core fields
            InodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer[0..8]),
            Type = (InodeType)buffer[8],
            Flags = (InodeFlags)buffer[9],
            Permissions = (InodePermissions)BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..12]),
            LinkCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[12..16]),
            OwnerId = new Guid(buffer[16..32]),
            Size = BinaryPrimitives.ReadInt64LittleEndian(buffer[32..40]),
            AllocatedSize = BinaryPrimitives.ReadInt64LittleEndian(buffer[40..48]),
            CreatedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer[48..56]),
            ModifiedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer[56..64]),
            AccessedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer[64..72]),
            ChangedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer[72..80]),
            ExtentCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[80..84]),
        };

        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            int extOffset = 84 + i * InodeExtent.SerializedSize;
            inode.Extents[i] = InodeExtent.Deserialize(buffer[extOffset..]);
        }

        inode.IndirectExtentBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[276..284]);
        inode.DoubleIndirectBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[284..292]);
        inode.ExtendedAttributeBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[292..300]);

        // Extended fields
        int o = ExtendedFieldsOffset;
        inode.CreatedNs = BinaryPrimitives.ReadInt64LittleEndian(buffer[o..(o + 8)]);
        inode.ModifiedNs = BinaryPrimitives.ReadInt64LittleEndian(buffer[(o + 8)..(o + 16)]);
        inode.AccessedNs = BinaryPrimitives.ReadInt64LittleEndian(buffer[(o + 16)..(o + 24)]);

        buffer.Slice(o + 24, MaxInlineXattrSize).CopyTo(inode.InlineXattrArea);
        inode.CompressionDictionaryRef = BinaryPrimitives.ReadInt64LittleEndian(buffer[(o + 88)..(o + 96)]);
        buffer.Slice(o + 96, EncryptionIVSize).CopyTo(inode.PerObjectEncryptionIV);
        inode.MvccVersionChainHead = BinaryPrimitives.ReadInt64LittleEndian(buffer[(o + 112)..(o + 120)]);
        inode.MvccTransactionId = BinaryPrimitives.ReadInt64LittleEndian(buffer[(o + 120)..(o + 128)]);
        inode.SnapshotRefCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[(o + 128)..(o + 132)]);
        buffer.Slice(o + 132, ReplicationVectorSize).CopyTo(inode.ReplicationVector);

        return inode;
    }
}
