using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

/// <summary>
/// Type of inode.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public enum InodeType : byte
{
    /// <summary>
    /// Regular file.
    /// </summary>
    File = 0,

    /// <summary>
    /// Directory.
    /// </summary>
    Directory = 1,

    /// <summary>
    /// Symbolic link.
    /// </summary>
    SymLink = 2,

    /// <summary>
    /// Hard link target (same as File, but tracked separately for clarity).
    /// </summary>
    HardLinkTarget = 3
}

/// <summary>
/// POSIX-style file permissions.
/// </summary>
[Flags]
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public enum InodePermissions : ushort
{
    /// <summary>
    /// No permissions.
    /// </summary>
    None = 0,

    /// <summary>
    /// Owner read permission.
    /// </summary>
    OwnerRead = 1 << 8,

    /// <summary>
    /// Owner write permission.
    /// </summary>
    OwnerWrite = 1 << 7,

    /// <summary>
    /// Owner execute permission.
    /// </summary>
    OwnerExecute = 1 << 6,

    /// <summary>
    /// Group read permission.
    /// </summary>
    GroupRead = 1 << 5,

    /// <summary>
    /// Group write permission.
    /// </summary>
    GroupWrite = 1 << 4,

    /// <summary>
    /// Group execute permission.
    /// </summary>
    GroupExecute = 1 << 3,

    /// <summary>
    /// Other read permission.
    /// </summary>
    OtherRead = 1 << 2,

    /// <summary>
    /// Other write permission.
    /// </summary>
    OtherWrite = 1 << 1,

    /// <summary>
    /// Other execute permission.
    /// </summary>
    OtherExecute = 1 << 0,

    /// <summary>
    /// Full owner permissions (rwx).
    /// </summary>
    OwnerAll = OwnerRead | OwnerWrite | OwnerExecute,

    /// <summary>
    /// Full group permissions (rwx).
    /// </summary>
    GroupAll = GroupRead | GroupWrite | GroupExecute,

    /// <summary>
    /// Full other permissions (rwx).
    /// </summary>
    OtherAll = OtherRead | OtherWrite | OtherExecute,

    /// <summary>
    /// Full permissions for all (rwxrwxrwx).
    /// </summary>
    All = OwnerAll | GroupAll | OtherAll
}

/// <summary>
/// On-disk inode structure containing file metadata.
/// Fixed size: 256 bytes on disk (excluding extended attributes overflow).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public sealed class Inode
{
    /// <summary>
    /// Fixed inode size on disk (256 bytes).
    /// </summary>
    public const int InodeSize = 256;

    /// <summary>
    /// Number of inodes per 4096-byte block.
    /// </summary>
    public const int InodesPerBlock = VdeConstants.DefaultBlockSize / InodeSize; // 16

    /// <summary>
    /// Number of direct block pointers.
    /// </summary>
    public const int DirectBlockCount = 12;

    /// <summary>
    /// Unique inode number (stable across renames).
    /// </summary>
    public long InodeNumber { get; set; }

    /// <summary>
    /// Type of inode.
    /// </summary>
    public InodeType Type { get; set; }

    /// <summary>
    /// POSIX-style permissions.
    /// </summary>
    public InodePermissions Permissions { get; set; }

    /// <summary>
    /// Hard link reference count.
    /// </summary>
    public int LinkCount { get; set; }

    /// <summary>
    /// Owner user ID.
    /// </summary>
    public long OwnerId { get; set; }

    /// <summary>
    /// Owner group ID.
    /// </summary>
    public long GroupId { get; set; }

    /// <summary>
    /// Size in bytes (for files) or entry count (for directories).
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Creation timestamp (Unix seconds UTC).
    /// </summary>
    public long CreatedUtc { get; set; }

    /// <summary>
    /// Last modified timestamp (Unix seconds UTC).
    /// </summary>
    public long ModifiedUtc { get; set; }

    /// <summary>
    /// Last accessed timestamp (Unix seconds UTC).
    /// </summary>
    public long AccessedUtc { get; set; }

    /// <summary>
    /// Direct block pointers (12 pointers for small files).
    /// Each entry is a block number.
    /// </summary>
    public long[] DirectBlockPointers { get; set; } = new long[DirectBlockCount];

    /// <summary>
    /// Indirect block pointer (points to a block containing block numbers).
    /// </summary>
    public long IndirectBlockPointer { get; set; }

    /// <summary>
    /// Double indirect block pointer (points to a block of block numbers, each pointing to data blocks).
    /// </summary>
    public long DoubleIndirectPointer { get; set; }

    /// <summary>
    /// Block number containing extended attributes overflow data.
    /// Zero if no extended attributes.
    /// </summary>
    public long ExtendedAttributesBlock { get; set; }

    /// <summary>
    /// Symbolic link target path (for symlinks only, stored separately from data blocks).
    /// Max 255 bytes UTF-8 encoded.
    /// </summary>
    public string? SymLinkTarget { get; set; }

    /// <summary>
    /// Extended attributes (key-value pairs).
    /// Loaded from overflow blocks when needed.
    /// </summary>
    public Dictionary<string, byte[]> ExtendedAttributes { get; set; } = new();

    /// <summary>
    /// Serializes the inode to a 256-byte buffer.
    /// </summary>
    /// <param name="buffer">Buffer to write to (must be at least 256 bytes).</param>
    public void Serialize(Span<byte> buffer)
    {
        if (buffer.Length < InodeSize)
        {
            throw new ArgumentException($"Buffer must be at least {InodeSize} bytes.", nameof(buffer));
        }

        buffer.Clear();

        int offset = 0;

        // InodeNumber (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), InodeNumber);
        offset += 8;

        // Type (1 byte)
        buffer[offset++] = (byte)Type;

        // Permissions (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset, 2), (ushort)Permissions);
        offset += 2;

        // LinkCount (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset, 4), LinkCount);
        offset += 4;

        // OwnerId (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), OwnerId);
        offset += 8;

        // GroupId (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), GroupId);
        offset += 8;

        // Size (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), Size);
        offset += 8;

        // CreatedUtc (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), CreatedUtc);
        offset += 8;

        // ModifiedUtc (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), ModifiedUtc);
        offset += 8;

        // AccessedUtc (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), AccessedUtc);
        offset += 8;

        // DirectBlockPointers (12 * 8 = 96 bytes)
        for (int i = 0; i < DirectBlockCount; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), DirectBlockPointers[i]);
            offset += 8;
        }

        // IndirectBlockPointer (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), IndirectBlockPointer);
        offset += 8;

        // DoubleIndirectPointer (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), DoubleIndirectPointer);
        offset += 8;

        // ExtendedAttributesBlock (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), ExtendedAttributesBlock);
        offset += 8;

        // SymLinkTarget (64 bytes reserved, length-prefixed string)
        if (!string.IsNullOrEmpty(SymLinkTarget))
        {
            byte[] targetBytes = Encoding.UTF8.GetBytes(SymLinkTarget);
            int targetLen = Math.Min(targetBytes.Length, 63); // Max 63 bytes for target
            buffer[offset] = (byte)targetLen;
            targetBytes.AsSpan(0, targetLen).CopyTo(buffer.Slice(offset + 1, targetLen));
        }
        else
        {
            buffer[offset] = 0;
        }
        offset += 64;

        // Total: 8 + 1 + 2 + 4 + 8 + 8 + 8 + 8 + 8 + 8 + 96 + 8 + 8 + 8 + 64 = 243 bytes
        // Remaining 13 bytes reserved for future use
    }

    /// <summary>
    /// Deserializes an inode from a 256-byte buffer.
    /// </summary>
    /// <param name="buffer">Buffer to read from (must be at least 256 bytes).</param>
    /// <returns>Deserialized inode.</returns>
    public static Inode Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < InodeSize)
        {
            throw new ArgumentException($"Buffer must be at least {InodeSize} bytes.", nameof(buffer));
        }

        var inode = new Inode();
        int offset = 0;

        // InodeNumber
        inode.InodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // Type
        inode.Type = (InodeType)buffer[offset++];

        // Permissions
        inode.Permissions = (InodePermissions)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
        offset += 2;

        // LinkCount
        inode.LinkCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        // OwnerId
        inode.OwnerId = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // GroupId
        inode.GroupId = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // Size
        inode.Size = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // CreatedUtc
        inode.CreatedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // ModifiedUtc
        inode.ModifiedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // AccessedUtc
        inode.AccessedUtc = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // DirectBlockPointers
        for (int i = 0; i < DirectBlockCount; i++)
        {
            inode.DirectBlockPointers[i] = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
            offset += 8;
        }

        // IndirectBlockPointer
        inode.IndirectBlockPointer = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // DoubleIndirectPointer
        inode.DoubleIndirectPointer = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // ExtendedAttributesBlock
        inode.ExtendedAttributesBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // SymLinkTarget
        byte targetLen = buffer[offset];
        if (targetLen > 0)
        {
            inode.SymLinkTarget = Encoding.UTF8.GetString(buffer.Slice(offset + 1, targetLen));
        }
        offset += 64;

        return inode;
    }
}
