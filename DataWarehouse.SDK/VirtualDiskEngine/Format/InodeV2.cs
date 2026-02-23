using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Inode flags controlling whole-inode properties.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 inode flags (VDE2-05)")]
public enum InodeFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>Inode content is encrypted.</summary>
    Encrypted = 1,

    /// <summary>Inode content is compressed.</summary>
    Compressed = 2,

    /// <summary>Write-Once-Read-Many: content cannot be modified after creation.</summary>
    Worm = 4,

    /// <summary>Small data is stored inline in the inode rather than in data blocks.</summary>
    InlineData = 8,
}

/// <summary>
/// Variable-size v2.0 inode structure (320-576 bytes depending on active modules).
/// The inode is the metadata record for every object in the VDE. Its variable size
/// based on selected modules is a key innovation of v2.0 composability.
/// </summary>
/// <remarks>
/// Core layout (304 bytes):
/// <code>
///   [0..8)     InodeNumber           (long)
///   [8]        Type                  (InodeType : byte)
///   [9]        Flags                 (InodeFlags : byte)
///   [10..12)   Permissions           (InodePermissions : ushort)
///   [12..16)   LinkCount             (int)
///   [16..32)   OwnerId              (Guid, 16 bytes)
///   [32..40)   Size                  (long)
///   [40..48)   AllocatedSize         (long)
///   [48..56)   CreatedUtc            (long, ticks)
///   [56..64)   ModifiedUtc           (long, ticks)
///   [64..72)   AccessedUtc           (long, ticks)
///   [72..80)   ChangedUtc            (long, ticks)
///   [80..84)   ExtentCount           (int)
///   [84..276)  Extents               (8 x 24 = 192 bytes)
///   [276..284) IndirectExtentBlock   (long)
///   [284..292) DoubleIndirectBlock   (long)
///   [292..300) ExtendedAttributeBlock(long)
///   [300..304) Reserved              (4 bytes, zero)
/// </code>
/// After core: module field data (variable), then padding to alignment boundary.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 inode structure (VDE2-05)")]
public sealed class InodeV2
{
    // ── Core fields (304 bytes) ─────────────────────────────────────────

    /// <summary>Unique inode number within this VDE.</summary>
    public long InodeNumber { get; set; }

    /// <summary>Object type (file, directory, symlink, etc.).</summary>
    public InodeType Type { get; set; }

    /// <summary>Inode-level flags (encrypted, compressed, WORM, inline data).</summary>
    public InodeFlags Flags { get; set; }

    /// <summary>POSIX-style permissions (owner/group/other read/write/execute).</summary>
    public InodePermissions Permissions { get; set; }

    /// <summary>Number of hard links pointing to this inode.</summary>
    public int LinkCount { get; set; }

    /// <summary>Owner identifier (maps to an external user/principal ID).</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Logical size of the file data in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Allocated (physical) size on disk in bytes (may exceed Size due to block alignment).</summary>
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

    /// <summary>Inline extent array (always 8 slots; only ExtentCount are valid).</summary>
    public InodeExtent[] Extents { get; set; } = new InodeExtent[FormatConstants.MaxExtentsPerInode];

    /// <summary>Block number of the indirect extent block (0 if none).</summary>
    public long IndirectExtentBlock { get; set; }

    /// <summary>Block number of the double-indirect extent block (0 if none).</summary>
    public long DoubleIndirectBlock { get; set; }

    /// <summary>Block number of the extended attribute block (0 if none).</summary>
    public long ExtendedAttributeBlock { get; set; }

    // ── Module fields (variable size) ───────────────────────────────────

    /// <summary>
    /// Raw module field data. Contains all active module fields concatenated in
    /// module bit-position order. Interpret using <see cref="InodeLayoutDescriptor"/>.
    /// </summary>
    public byte[] ModuleFieldData { get; set; } = Array.Empty<byte>();

    // ── Convenience accessors for well-known module fields ──────────────

    /// <summary>Gets the raw bytes for the given module's field area, or empty if not present.</summary>
    public ReadOnlySpan<byte> GetModuleFieldRaw(InodeLayoutDescriptor descriptor, ModuleId module)
    {
        int offset = descriptor.GetModuleFieldOffset(module);
        if (offset < 0) return ReadOnlySpan<byte>.Empty;

        int size = descriptor.GetModuleFieldSize(module);
        int dataOffset = offset - descriptor.CoreFieldsEnd;
        if (dataOffset < 0 || dataOffset + size > ModuleFieldData.Length)
            return ReadOnlySpan<byte>.Empty;

        return ModuleFieldData.AsSpan(dataOffset, size);
    }

    /// <summary>Writes raw bytes for the given module's field area.</summary>
    public void SetModuleFieldRaw(InodeLayoutDescriptor descriptor, ModuleId module, ReadOnlySpan<byte> data)
    {
        int offset = descriptor.GetModuleFieldOffset(module);
        if (offset < 0)
            throw new InvalidOperationException($"Module {module} is not present in the layout descriptor.");

        int size = descriptor.GetModuleFieldSize(module);
        if (data.Length != size)
            throw new ArgumentException($"Data must be exactly {size} bytes for module {module}.", nameof(data));

        int dataOffset = offset - descriptor.CoreFieldsEnd;
        EnsureModuleFieldDataCapacity(descriptor);
        data.CopyTo(ModuleFieldData.AsSpan(dataOffset, size));
    }

    // ── Security module helpers (ModuleId.Security, 24 bytes) ──────────

    /// <summary>Gets the encryption key slot index from the Security module fields.</summary>
    public int GetEncryptionKeySlot(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Security);
        return raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw[..4]) : 0;
    }

    /// <summary>Gets the ACL policy ID from the Security module fields.</summary>
    public int GetAclPolicyId(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Security);
        return raw.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(raw[4..8]) : 0;
    }

    /// <summary>Gets the 16-byte content hash from the Security module fields.</summary>
    public ReadOnlySpan<byte> GetContentHash(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Security);
        return raw.Length >= 24 ? raw[8..24] : ReadOnlySpan<byte>.Empty;
    }

    // ── Tags module helpers (ModuleId.Tags, 136 bytes) ─────────────────

    /// <summary>Gets the number of inline tags from the Tags module fields.</summary>
    public int GetInlineTagCount(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Tags);
        return raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw[..4]) : 0;
    }

    /// <summary>Gets the tag overflow block number from the Tags module fields.</summary>
    public int GetTagOverflowBlock(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Tags);
        return raw.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(raw[4..8]) : 0;
    }

    /// <summary>Gets the 128-byte inline tag area from the Tags module fields.</summary>
    public ReadOnlySpan<byte> GetInlineTagArea(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Tags);
        return raw.Length >= 136 ? raw[8..136] : ReadOnlySpan<byte>.Empty;
    }

    // ── Replication module helpers (ModuleId.Replication, 8 bytes) ──────

    /// <summary>Gets the replication generation from the Replication module fields.</summary>
    public int GetReplicationGeneration(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Replication);
        return raw.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(raw[..4]) : 0;
    }

    /// <summary>Gets the dirty flag from the Replication module fields.</summary>
    public int GetReplicationDirtyFlag(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Replication);
        return raw.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(raw[4..8]) : 0;
    }

    // ── RAID module helpers (ModuleId.Raid, 4 bytes) ───────────────────

    /// <summary>Gets the RAID shard ID from the RAID module fields.</summary>
    public short GetRaidShardId(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Raid);
        return raw.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(raw[..2]) : (short)0;
    }

    /// <summary>Gets the RAID group ID from the RAID module fields.</summary>
    public short GetRaidGroupId(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Raid);
        return raw.Length >= 4 ? BinaryPrimitives.ReadInt16LittleEndian(raw[2..4]) : (short)0;
    }

    // ── Streaming module helpers (ModuleId.Streaming, 8 bytes) ─────────

    /// <summary>Gets the stream sequence number from the Streaming module fields.</summary>
    public long GetStreamSequenceNumber(InodeLayoutDescriptor descriptor)
    {
        var raw = GetModuleFieldRaw(descriptor, ModuleId.Streaming);
        return raw.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(raw[..8]) : 0;
    }

    // ── Serialization ──────────────────────────────────────────────────

    /// <summary>
    /// Serializes this inode to a byte array of exactly descriptor.InodeSize bytes.
    /// </summary>
    public static byte[] Serialize(InodeV2 inode, InodeLayoutDescriptor descriptor)
    {
        var buffer = new byte[descriptor.InodeSize];
        SerializeToSpan(inode, descriptor, buffer);
        return buffer;
    }

    /// <summary>
    /// Serializes this inode into the provided buffer.
    /// </summary>
    public static void SerializeToSpan(InodeV2 inode, InodeLayoutDescriptor descriptor, Span<byte> buffer)
    {
        if (buffer.Length < descriptor.InodeSize)
            throw new ArgumentException($"Buffer must be at least {descriptor.InodeSize} bytes.", nameof(buffer));

        // Clear buffer (ensures padding bytes are zero)
        buffer[..descriptor.InodeSize].Clear();

        // Core fields (304 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[0..8], inode.InodeNumber);
        buffer[8] = (byte)inode.Type;
        buffer[9] = (byte)inode.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], (ushort)inode.Permissions);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[12..16], inode.LinkCount);

        // OwnerId as 16 bytes (little-endian GUID)
        if (!inode.OwnerId.TryWriteBytes(buffer[16..32]))
            throw new InvalidOperationException("Failed to write OwnerId GUID.");

        BinaryPrimitives.WriteInt64LittleEndian(buffer[32..40], inode.Size);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[40..48], inode.AllocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[48..56], inode.CreatedUtc);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[56..64], inode.ModifiedUtc);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[64..72], inode.AccessedUtc);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[72..80], inode.ChangedUtc);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[80..84], inode.ExtentCount);

        // 8 extents x 24 bytes = 192 bytes at offset 84
        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            int extOffset = 84 + i * InodeExtent.SerializedSize;
            InodeExtent.Serialize(in inode.Extents[i], buffer[extOffset..]);
        }

        // Indirect/double-indirect/extended attribute blocks at offset 276
        BinaryPrimitives.WriteInt64LittleEndian(buffer[276..284], inode.IndirectExtentBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[284..292], inode.DoubleIndirectBlock);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[292..300], inode.ExtendedAttributeBlock);
        // [300..304) reserved, already zeroed

        // Module field data (starts at CoreFieldsEnd = 304)
        if (inode.ModuleFieldData.Length > 0)
        {
            int moduleAreaSize = Math.Min(inode.ModuleFieldData.Length,
                descriptor.InodeSize - descriptor.CoreFieldsEnd - descriptor.PaddingBytes);
            if (moduleAreaSize > 0)
            {
                inode.ModuleFieldData.AsSpan(0, moduleAreaSize)
                    .CopyTo(buffer[descriptor.CoreFieldsEnd..]);
            }
        }

        // Padding bytes are already zero from the Clear() call
    }

    /// <summary>
    /// Deserializes an inode from a buffer using the layout descriptor.
    /// </summary>
    public static InodeV2 Deserialize(ReadOnlySpan<byte> buffer, InodeLayoutDescriptor descriptor)
    {
        if (buffer.Length < descriptor.InodeSize)
            throw new ArgumentException($"Buffer must be at least {descriptor.InodeSize} bytes.", nameof(buffer));

        var inode = new InodeV2
        {
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

        // Deserialize 8 extents
        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            int extOffset = 84 + i * InodeExtent.SerializedSize;
            inode.Extents[i] = InodeExtent.Deserialize(buffer[extOffset..]);
        }

        inode.IndirectExtentBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[276..284]);
        inode.DoubleIndirectBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[284..292]);
        inode.ExtendedAttributeBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[292..300]);

        // Module field data
        int moduleFieldBytes = descriptor.InodeSize - descriptor.CoreFieldsEnd - descriptor.PaddingBytes;
        if (moduleFieldBytes > 0)
        {
            inode.ModuleFieldData = buffer.Slice(descriptor.CoreFieldsEnd, moduleFieldBytes).ToArray();
        }

        return inode;
    }

    /// <summary>
    /// Ensures the ModuleFieldData array has sufficient capacity for all module fields
    /// described in the layout descriptor.
    /// </summary>
    private void EnsureModuleFieldDataCapacity(InodeLayoutDescriptor descriptor)
    {
        int requiredSize = descriptor.InodeSize - descriptor.CoreFieldsEnd - descriptor.PaddingBytes;
        if (ModuleFieldData.Length < requiredSize)
        {
            var newData = new byte[requiredSize];
            ModuleFieldData.AsSpan().CopyTo(newData);
            ModuleFieldData = newData;
        }
    }
}
