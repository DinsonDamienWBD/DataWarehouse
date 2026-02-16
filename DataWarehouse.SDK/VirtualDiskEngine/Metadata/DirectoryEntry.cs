using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Text;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

/// <summary>
/// Directory entry mapping a name to an inode number.
/// Variable-length structure: inode number + type + name length + name.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public sealed class DirectoryEntry
{
    /// <summary>
    /// Maximum name length in bytes (UTF-8 encoded).
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Inode number referenced by this entry.
    /// </summary>
    public long InodeNumber { get; set; }

    /// <summary>
    /// Type of inode (cached from inode for readdir optimization).
    /// </summary>
    public InodeType Type { get; set; }

    /// <summary>
    /// Name of the entry (file or directory name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Computes the serialized size of this entry.
    /// </summary>
    public int SerializedSize
    {
        get
        {
            int nameBytes = Encoding.UTF8.GetByteCount(Name);
            return 8 + 1 + 1 + nameBytes; // InodeNumber + Type + NameLength + Name
        }
    }

    /// <summary>
    /// Serializes the directory entry to the buffer.
    /// </summary>
    /// <param name="buffer">Buffer to write to.</param>
    /// <returns>Number of bytes written.</returns>
    public int Serialize(Span<byte> buffer)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(Name);
        if (nameBytes.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name exceeds maximum length of {MaxNameLength} bytes.", nameof(Name));
        }

        int offset = 0;

        // InodeNumber (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), InodeNumber);
        offset += 8;

        // Type (1 byte)
        buffer[offset++] = (byte)Type;

        // NameLength (1 byte)
        buffer[offset++] = (byte)nameBytes.Length;

        // Name (variable length)
        nameBytes.CopyTo(buffer.Slice(offset, nameBytes.Length));
        offset += nameBytes.Length;

        return offset;
    }

    /// <summary>
    /// Deserializes a directory entry from the buffer.
    /// </summary>
    /// <param name="buffer">Buffer to read from.</param>
    /// <param name="bytesRead">Number of bytes consumed.</param>
    /// <returns>Deserialized directory entry.</returns>
    public static DirectoryEntry Deserialize(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        if (buffer.Length < 10) // Minimum: 8 + 1 + 1 + 0
        {
            throw new ArgumentException("Buffer too small for directory entry.", nameof(buffer));
        }

        var entry = new DirectoryEntry();
        int offset = 0;

        // InodeNumber
        entry.InodeNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // Type
        entry.Type = (InodeType)buffer[offset++];

        // NameLength
        byte nameLength = buffer[offset++];

        if (buffer.Length < offset + nameLength)
        {
            throw new ArgumentException("Buffer too small for directory entry name.", nameof(buffer));
        }

        // Name
        entry.Name = Encoding.UTF8.GetString(buffer.Slice(offset, nameLength));
        offset += nameLength;

        bytesRead = offset;
        return entry;
    }
}
