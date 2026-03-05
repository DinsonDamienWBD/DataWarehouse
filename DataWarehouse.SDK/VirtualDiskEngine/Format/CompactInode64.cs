using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// A 64-byte compact inode for tiny objects (config files, tags, metadata entries).
/// Objects up to 48 bytes are stored entirely inline with zero block allocation.
/// This dramatically reduces storage overhead for small objects compared to the
/// 256+ byte standard inodes.
/// </summary>
/// <remarks>
/// Layout (64 bytes total, little-endian):
/// <code>
///   [0..8)    InodeNumber       (long)   - unique inode number
///   [8]       Type              (byte)   - InodeType
///   [9]       Flags             (byte)   - InodeFlags (always includes InlineData)
///   [10..12)  InlineDataSize    (ushort) - number of valid inline data bytes (0-48)
///   [12..16)  OwnerId           (uint)   - truncated owner hash (4 bytes)
///   [16..64)  InlineData        (48 bytes) - inline object data
/// </code>
/// No extent pointers, no timestamps -- compact = minimal overhead.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-06 compact 64-byte inode for inline tiny objects")]
public readonly struct CompactInode64 : IEquatable<CompactInode64>
{
    /// <summary>Total serialized size of a compact inode in bytes.</summary>
    public const int SerializedSize = 64;

    /// <summary>Size of the header before the inline data area.</summary>
    public const int HeaderSize = 16;

    /// <summary>Maximum number of bytes that can be stored inline.</summary>
    public const int MaxInlineDataSize = SerializedSize - HeaderSize; // 48

    /// <summary>Unique inode number within this VDE.</summary>
    public long InodeNumber { get; }

    /// <summary>Object type (file, directory, symlink, etc.).</summary>
    public InodeType Type { get; }

    /// <summary>Inode-level flags (always includes InlineData for compact inodes).</summary>
    public InodeFlags Flags { get; }

    /// <summary>Number of valid inline data bytes (0-48).</summary>
    public ushort InlineDataSize { get; }

    /// <summary>Truncated owner hash (4-byte hash of the full owner GUID).</summary>
    public uint OwnerId { get; }

    /// <summary>Inline data stored directly in the inode (up to 48 bytes).</summary>
    public byte[] InlineData { get; }

    /// <summary>Creates a new compact inode.</summary>
    public CompactInode64(
        long inodeNumber,
        InodeType type,
        InodeFlags flags,
        ushort inlineDataSize,
        uint ownerId,
        byte[] inlineData)
    {
        InodeNumber = inodeNumber;
        Type = type;
        Flags = flags | InodeFlags.InlineData;
        InlineDataSize = Math.Min(inlineDataSize, (ushort)MaxInlineDataSize);
        OwnerId = ownerId;
        InlineData = inlineData ?? new byte[MaxInlineDataSize];
    }

    /// <summary>
    /// Returns true if the given object size can fit entirely inline in a compact inode.
    /// </summary>
    /// <param name="objectSize">Size of the object in bytes.</param>
    /// <returns>True if the object fits in the 48-byte inline area.</returns>
    public static bool CanFitInline(long objectSize) => objectSize >= 0 && objectSize <= MaxInlineDataSize;

    /// <summary>
    /// Converts this compact inode to a standard InodeV2 for when an object grows
    /// beyond inline capacity and needs block allocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Important</strong>: The <see cref="InlineData"/> payload is NOT copied into the returned
    /// <see cref="InodeV2"/>. The caller is responsible for writing the inline data content to the
    /// allocated extent(s) after promotion. The returned inode sets <c>Size = InlineDataSize</c> so
    /// the caller knows how many bytes need to be migrated.
    /// </para>
    /// </remarks>
    /// <returns>A new InodeV2 populated from this compact inode's fields (without inline data).</returns>
    public InodeV2 ToStandardInode()
    {
        var standard = new InodeV2
        {
            InodeNumber = InodeNumber,
            Type = Type,
            Flags = Flags,
            Permissions = InodePermissions.None,
            LinkCount = 1,
            OwnerId = Guid.Empty, // Full GUID not available from truncated hash
            Size = InlineDataSize,
            AllocatedSize = 0,
            CreatedUtc = DateTimeOffset.UtcNow.Ticks,
            ModifiedUtc = DateTimeOffset.UtcNow.Ticks,
            AccessedUtc = DateTimeOffset.UtcNow.Ticks,
            ChangedUtc = DateTimeOffset.UtcNow.Ticks,
            ExtentCount = 0,
        };
        return standard;
    }

    /// <summary>
    /// Serializes this compact inode to exactly 64 bytes in little-endian format.
    /// </summary>
    public static byte[] Serialize(in CompactInode64 inode)
    {
        var buffer = new byte[SerializedSize];
        SerializeToSpan(in inode, buffer);
        return buffer;
    }

    /// <summary>
    /// Serializes this compact inode into the provided buffer.
    /// </summary>
    public static void SerializeToSpan(in CompactInode64 inode, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        buffer[..SerializedSize].Clear();

        BinaryPrimitives.WriteInt64LittleEndian(buffer[0..8], inode.InodeNumber);
        buffer[8] = (byte)inode.Type;
        buffer[9] = (byte)inode.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], inode.InlineDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], inode.OwnerId);

        // Copy inline data (up to 48 bytes)
        int copyLen = Math.Min(inode.InlineData.Length, MaxInlineDataSize);
        inode.InlineData.AsSpan(0, copyLen).CopyTo(buffer[HeaderSize..]);
    }

    /// <summary>
    /// Deserializes a compact inode from a buffer of at least 64 bytes.
    /// </summary>
    public static CompactInode64 Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        long inodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer[0..8]);
        var type = (InodeType)buffer[8];
        var flags = (InodeFlags)buffer[9];
        ushort inlineDataSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..12]);
        uint ownerId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]);

        var inlineData = new byte[MaxInlineDataSize];
        buffer.Slice(HeaderSize, MaxInlineDataSize).CopyTo(inlineData);

        return new CompactInode64(inodeNumber, type, flags, inlineDataSize, ownerId, inlineData);
    }

    /// <inheritdoc />
    public bool Equals(CompactInode64 other)
        => InodeNumber == other.InodeNumber
        && Type == other.Type
        && Flags == other.Flags
        && InlineDataSize == other.InlineDataSize
        && OwnerId == other.OwnerId
        && InlineData.AsSpan(0, InlineDataSize).SequenceEqual(other.InlineData.AsSpan(0, other.InlineDataSize));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CompactInode64 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(InodeNumber, Type, Flags, InlineDataSize, OwnerId);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(CompactInode64 left, CompactInode64 right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(CompactInode64 left, CompactInode64 right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"CompactInode64(Inode={InodeNumber}, Type={Type}, InlineSize={InlineDataSize}/{MaxInlineDataSize})";
}
