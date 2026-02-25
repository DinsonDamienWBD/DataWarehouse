using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Describes a single module's field location within the inode extension area.
/// Each entry is 7 bytes on disk and tells any VDE engine where to find (or skip)
/// a particular module's inode fields, even if the engine does not understand that module.
/// </summary>
/// <remarks>
/// Wire format (7 bytes, little-endian):
/// <code>
///   [0]     ModuleId      (byte)   - module bit position
///   [1..3)  FieldOffset   (ushort) - byte offset within inode
///   [3..5)  FieldSize     (ushort) - field size in bytes
///   [5]     FieldVersion  (byte)   - layout version for this module
///   [6]     Flags         (byte)   - bit 0: ACTIVE, bit 1: MIGRATING
/// </code>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 module field entry (VDE2-05)")]
public readonly struct ModuleFieldEntry : IEquatable<ModuleFieldEntry>
{
    /// <summary>Serialized size of a single module field entry in bytes.</summary>
    public const int SerializedSize = 7;

    /// <summary>Module bit position identifying which module owns this field.</summary>
    public byte ModuleId { get; }

    /// <summary>Byte offset within the inode where this module's fields start.</summary>
    public ushort FieldOffset { get; }

    /// <summary>Size of this module's field area in bytes.</summary>
    public ushort FieldSize { get; }

    /// <summary>Layout version for this module's field format (allows in-place evolution).</summary>
    public byte FieldVersion { get; }

    /// <summary>Entry flags: bit 0 = ACTIVE, bit 1 = MIGRATING.</summary>
    public byte Flags { get; }

    /// <summary>Flag bit indicating the module field is active.</summary>
    public const byte FlagActive = 0x01;

    /// <summary>Flag bit indicating the module field is being migrated to a new version.</summary>
    public const byte FlagMigrating = 0x02;

    /// <summary>True if this module field entry is active.</summary>
    public bool IsActive => (Flags & FlagActive) != 0;

    /// <summary>True if this module field entry is being migrated.</summary>
    public bool IsMigrating => (Flags & FlagMigrating) != 0;

    /// <summary>Creates a new module field entry.</summary>
    public ModuleFieldEntry(byte moduleId, ushort fieldOffset, ushort fieldSize, byte fieldVersion, byte flags)
    {
        ModuleId = moduleId;
        FieldOffset = fieldOffset;
        FieldSize = fieldSize;
        FieldVersion = fieldVersion;
        Flags = flags;
    }

    /// <summary>
    /// Serializes this entry to exactly 7 bytes in little-endian format.
    /// </summary>
    public static void Serialize(in ModuleFieldEntry entry, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        buffer[0] = entry.ModuleId;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[1..3], entry.FieldOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[3..5], entry.FieldSize);
        buffer[5] = entry.FieldVersion;
        buffer[6] = entry.Flags;
    }

    /// <summary>
    /// Deserializes an entry from exactly 7 bytes in little-endian format.
    /// </summary>
    public static ModuleFieldEntry Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException($"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        return new ModuleFieldEntry(
            moduleId: buffer[0],
            fieldOffset: BinaryPrimitives.ReadUInt16LittleEndian(buffer[1..3]),
            fieldSize: BinaryPrimitives.ReadUInt16LittleEndian(buffer[3..5]),
            fieldVersion: buffer[5],
            flags: buffer[6]);
    }

    /// <inheritdoc />
    public bool Equals(ModuleFieldEntry other)
        => ModuleId == other.ModuleId
        && FieldOffset == other.FieldOffset
        && FieldSize == other.FieldSize
        && FieldVersion == other.FieldVersion
        && Flags == other.Flags;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ModuleFieldEntry other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ModuleId, FieldOffset, FieldSize, FieldVersion, Flags);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ModuleFieldEntry left, ModuleFieldEntry right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ModuleFieldEntry left, ModuleFieldEntry right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"ModuleField(Module={ModuleId}, Offset={FieldOffset}, Size={FieldSize}, V{FieldVersion}, Flags=0x{Flags:X2})";
}

/// <summary>
/// Self-describing inode layout descriptor stored in the superblock. Records the exact
/// byte layout of every module's field area within an inode, enabling any VDE engine to
/// parse inodes regardless of which modules it understands.
/// </summary>
/// <remarks>
/// Wire format:
/// <code>
///   [0..2)   InodeSize        (ushort) - actual inode size in bytes
///   [2..4)   CoreFieldsEnd    (ushort) - end of core fields (always 304 in v2.0)
///   [4]      ModuleFieldCount (byte)   - number of module field entries (0-19)
///   [5]      PaddingBytes     (byte)   - reserved bytes at inode end
///   [6..8)   Reserved         (ushort) - must be zero
///   [8..)    ModuleFields     (N * 7 bytes) - per-module field entries
/// </code>
/// Total serialized size: 8 + ModuleFieldCount * 7 (max 141 bytes for 19 modules).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 inode layout descriptor (VDE2-05)")]
public readonly struct InodeLayoutDescriptor : IEquatable<InodeLayoutDescriptor>
{
    /// <summary>Size of the descriptor header in bytes (before module field entries).</summary>
    public const int HeaderSize = 8;

    /// <summary>Actual inode size in bytes (aligned to 64-byte boundary).</summary>
    public ushort InodeSize { get; }

    /// <summary>Byte offset where core fields end (always 304 in v2.0).</summary>
    public ushort CoreFieldsEnd { get; }

    /// <summary>Number of module field entries (0-19).</summary>
    public byte ModuleFieldCount { get; }

    /// <summary>Number of padding bytes at the end of the inode.</summary>
    public byte PaddingBytes { get; }

    /// <summary>Reserved field (must be zero).</summary>
    public ushort Reserved { get; }

    /// <summary>Per-module field entries describing where each module's data lives in the inode.</summary>
    public ModuleFieldEntry[] ModuleFields { get; }

    /// <summary>Total serialized size of this descriptor in bytes.</summary>
    public int TotalSerializedSize => HeaderSize + ModuleFieldCount * ModuleFieldEntry.SerializedSize;

    /// <summary>Creates a new inode layout descriptor.</summary>
    public InodeLayoutDescriptor(
        ushort inodeSize,
        ushort coreFieldsEnd,
        byte moduleFieldCount,
        byte paddingBytes,
        ushort reserved,
        ModuleFieldEntry[] moduleFields)
    {
        InodeSize = inodeSize;
        CoreFieldsEnd = coreFieldsEnd;
        ModuleFieldCount = moduleFieldCount;
        PaddingBytes = paddingBytes;
        Reserved = reserved;
        ModuleFields = moduleFields;
    }

    /// <summary>
    /// Creates an inode layout descriptor from a module manifest by calculating the
    /// inode size and building module field entries for all active modules with inode fields.
    /// </summary>
    /// <param name="moduleManifest">32-bit module manifest.</param>
    /// <returns>A fully populated layout descriptor.</returns>
    public static InodeLayoutDescriptor Create(uint moduleManifest)
    {
        var sizeResult = InodeSizeCalculator.Calculate(moduleManifest);
        var layout = sizeResult.ModuleFieldLayout;

        var entries = new ModuleFieldEntry[layout.Count];
        for (int i = 0; i < layout.Count; i++)
        {
            var (module, offset, size) = layout[i];
            entries[i] = new ModuleFieldEntry(
                moduleId: (byte)module,
                fieldOffset: (ushort)offset,
                fieldSize: (ushort)size,
                fieldVersion: 1, // v2.0 initial layout version
                flags: ModuleFieldEntry.FlagActive);
        }

        return new InodeLayoutDescriptor(
            inodeSize: (ushort)sizeResult.InodeSize,
            coreFieldsEnd: (ushort)sizeResult.CoreBytes,
            moduleFieldCount: (byte)entries.Length,
            paddingBytes: (byte)sizeResult.PaddingBytes,
            reserved: 0,
            moduleFields: entries);
    }

    /// <summary>
    /// Returns the byte offset within the inode for the given module's field area,
    /// or -1 if the module is not present in this layout.
    /// </summary>
    public int GetModuleFieldOffset(ModuleId module)
    {
        byte id = (byte)module;
        for (int i = 0; i < ModuleFields.Length; i++)
        {
            if (ModuleFields[i].ModuleId == id)
                return ModuleFields[i].FieldOffset;
        }
        return -1;
    }

    /// <summary>
    /// Returns the field size in bytes for the given module, or 0 if not present.
    /// </summary>
    public int GetModuleFieldSize(ModuleId module)
    {
        byte id = (byte)module;
        for (int i = 0; i < ModuleFields.Length; i++)
        {
            if (ModuleFields[i].ModuleId == id)
                return ModuleFields[i].FieldSize;
        }
        return 0;
    }

    /// <summary>
    /// Checks whether the given module has a field entry in this layout.
    /// </summary>
    public bool HasModule(ModuleId module)
    {
        byte id = (byte)module;
        for (int i = 0; i < ModuleFields.Length; i++)
        {
            if (ModuleFields[i].ModuleId == id)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Serializes this descriptor to the target buffer.
    /// </summary>
    public static void Serialize(in InodeLayoutDescriptor desc, Span<byte> buffer)
    {
        int required = desc.TotalSerializedSize;
        if (buffer.Length < required)
            throw new ArgumentException($"Buffer must be at least {required} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], desc.InodeSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], desc.CoreFieldsEnd);
        buffer[4] = desc.ModuleFieldCount;
        buffer[5] = desc.PaddingBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], desc.Reserved);

        for (int i = 0; i < desc.ModuleFieldCount; i++)
        {
            int offset = HeaderSize + i * ModuleFieldEntry.SerializedSize;
            ModuleFieldEntry.Serialize(in desc.ModuleFields[i], buffer[offset..]);
        }
    }

    /// <summary>
    /// Deserializes a layout descriptor from the source buffer.
    /// </summary>
    public static InodeLayoutDescriptor Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes.", nameof(buffer));

        ushort inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]);
        ushort coreFieldsEnd = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]);
        byte moduleFieldCount = buffer[4];
        byte paddingBytes = buffer[5];
        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]);

        int required = HeaderSize + moduleFieldCount * ModuleFieldEntry.SerializedSize;
        if (buffer.Length < required)
            throw new ArgumentException($"Buffer must be at least {required} bytes for {moduleFieldCount} module entries.", nameof(buffer));

        var entries = new ModuleFieldEntry[moduleFieldCount];
        for (int i = 0; i < moduleFieldCount; i++)
        {
            int offset = HeaderSize + i * ModuleFieldEntry.SerializedSize;
            entries[i] = ModuleFieldEntry.Deserialize(buffer[offset..]);
        }

        return new InodeLayoutDescriptor(inodeSize, coreFieldsEnd, moduleFieldCount, paddingBytes, reserved, entries);
    }

    /// <inheritdoc />
    public bool Equals(InodeLayoutDescriptor other)
        => InodeSize == other.InodeSize
        && CoreFieldsEnd == other.CoreFieldsEnd
        && ModuleFieldCount == other.ModuleFieldCount
        && PaddingBytes == other.PaddingBytes;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InodeLayoutDescriptor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(InodeSize, CoreFieldsEnd, ModuleFieldCount, PaddingBytes);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(InodeLayoutDescriptor left, InodeLayoutDescriptor right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(InodeLayoutDescriptor left, InodeLayoutDescriptor right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
        => $"InodeLayout(Size={InodeSize}, Core={CoreFieldsEnd}, Modules={ModuleFieldCount}, Padding={PaddingBytes})";
}
