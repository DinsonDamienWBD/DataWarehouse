using System.Buffers.Binary;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Emergency Recovery Block: a plaintext block at a fixed position (block 9) that
/// contains essential volume identification and recovery contact information.
/// This block is NEVER encrypted, allowing recovery tools to identify a volume
/// and contact the administrator without needing any decryption keys.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- emergency recovery block (VTMP-08)")]
public readonly struct EmergencyRecoveryBlock : IEquatable<EmergencyRecoveryBlock>
{
    // ── Layout constants ────────────────────────────────────────────────

    /// <summary>Block number where the emergency recovery block is always located.</summary>
    public const int FixedBlockNumber = 9;

    /// <summary>Size of the "ERCV" magic marker at offset 0.</summary>
    public const int MagicSize = 4;

    /// <summary>Size of the volume UUID field (16 bytes).</summary>
    public const int VolumeUuidSize = 16;

    /// <summary>Size of the creation timestamp field (8 bytes, UTC ticks).</summary>
    public const int CreationTimestampSize = 8;

    /// <summary>Size of the volume label buffer (64 bytes, UTF-8 null-padded).</summary>
    public const int VolumeLabelSize = 64;

    /// <summary>Size of the contact info buffer (128 bytes, UTF-8 null-padded).</summary>
    public const int ContactInfoSize = 128;

    /// <summary>Size of the format version field (1+1+2 = 4 bytes).</summary>
    public const int FormatVersionSize = 4;

    /// <summary>Size of the error count field (8 bytes).</summary>
    public const int ErrorCountSize = 8;

    /// <summary>Size of the mount count field (8 bytes).</summary>
    public const int MountCountSize = 8;

    /// <summary>Total fixed-size payload: 4+16+8+64+128+4+8+8 = 240 bytes.</summary>
    public const int TotalFixedSize = MagicSize + VolumeUuidSize + CreationTimestampSize
        + VolumeLabelSize + ContactInfoSize + FormatVersionSize + ErrorCountSize + MountCountSize;

    /// <summary>"ERCV" magic bytes for identification.</summary>
    private static readonly byte[] ErcvMagic = { 0x45, 0x52, 0x43, 0x56 }; // "ERCV"

    // ── Fields ──────────────────────────────────────────────────────────

    /// <summary>128-bit UUID identifying this volume.</summary>
    public Guid VolumeUuid { get; }

    /// <summary>Volume creation timestamp as UTC ticks.</summary>
    public long CreationTimestampUtcTicks { get; }

    /// <summary>Volume label (64 bytes, UTF-8 null-padded).</summary>
    public byte[] VolumeLabel { get; }

    /// <summary>Administrator contact information for data recovery (128 bytes, UTF-8 null-padded).</summary>
    public byte[] ContactInfo { get; }

    /// <summary>Format major version.</summary>
    public byte MajorVersion { get; }

    /// <summary>Format minor version.</summary>
    public byte MinorVersion { get; }

    /// <summary>Format specification revision.</summary>
    public ushort SpecRevision { get; }

    /// <summary>Cumulative error count for this volume.</summary>
    public long ErrorCount { get; }

    /// <summary>Cumulative mount count for this volume.</summary>
    public long MountCount { get; }

    // ── Constructor ─────────────────────────────────────────────────────

    /// <summary>Creates a new EmergencyRecoveryBlock with all fields specified.</summary>
    public EmergencyRecoveryBlock(
        Guid volumeUuid,
        long creationTimestampUtcTicks,
        byte[] volumeLabel,
        byte[] contactInfo,
        byte majorVersion,
        byte minorVersion,
        ushort specRevision,
        long errorCount,
        long mountCount)
    {
        VolumeUuid = volumeUuid;
        CreationTimestampUtcTicks = creationTimestampUtcTicks;
        VolumeLabel = volumeLabel ?? new byte[VolumeLabelSize];
        ContactInfo = contactInfo ?? new byte[ContactInfoSize];
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        SpecRevision = specRevision;
        ErrorCount = errorCount;
        MountCount = mountCount;
    }

    // ── Factory Methods ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a default emergency recovery block from a SuperblockV2.
    /// </summary>
    /// <param name="sb">The superblock to extract volume identity from.</param>
    /// <returns>A new EmergencyRecoveryBlock populated from the superblock.</returns>
    public static EmergencyRecoveryBlock CreateDefault(in SuperblockV2 sb)
    {
        return new EmergencyRecoveryBlock(
            volumeUuid: sb.VolumeUuid,
            creationTimestampUtcTicks: sb.CreatedTimestampUtc,
            volumeLabel: sb.VolumeLabel != null ? (byte[])sb.VolumeLabel.Clone() : new byte[VolumeLabelSize],
            contactInfo: new byte[ContactInfoSize],
            majorVersion: sb.Magic.MajorVersion,
            minorVersion: sb.Magic.MinorVersion,
            specRevision: sb.Magic.SpecRevision,
            errorCount: 0,
            mountCount: 0);
    }

    /// <summary>
    /// Creates a default emergency recovery block from individual identity fields.
    /// </summary>
    /// <param name="volumeUuid">Volume UUID.</param>
    /// <param name="createdTimestamp">Volume creation timestamp (UTC ticks).</param>
    /// <param name="label">Volume label (64 bytes, UTF-8 null-padded).</param>
    /// <returns>A new EmergencyRecoveryBlock with the specified identity.</returns>
    public static EmergencyRecoveryBlock CreateDefault(Guid volumeUuid, long createdTimestamp, byte[] label)
    {
        return new EmergencyRecoveryBlock(
            volumeUuid: volumeUuid,
            creationTimestampUtcTicks: createdTimestamp,
            volumeLabel: label ?? new byte[VolumeLabelSize],
            contactInfo: new byte[ContactInfoSize],
            majorVersion: FormatConstants.FormatMajorVersion,
            minorVersion: FormatConstants.FormatMinorVersion,
            specRevision: FormatConstants.SpecRevision,
            errorCount: 0,
            mountCount: 0);
    }

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Serializes the emergency recovery block into a block-sized buffer.
    /// Writes "ERCV" magic at offset 0, all fields sequentially, and a
    /// UniversalBlockTrailer at the end with the ERCV tag.
    /// </summary>
    /// <param name="erb">The emergency recovery block to serialize.</param>
    /// <param name="buffer">Destination buffer, must be at least <paramref name="blockSize"/> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public static void Serialize(in EmergencyRecoveryBlock erb, Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        buffer.Slice(0, blockSize).Clear();

        int offset = 0;

        // Magic "ERCV" (4 bytes)
        ErcvMagic.AsSpan().CopyTo(buffer.Slice(offset));
        offset += MagicSize;

        // Volume UUID (16 bytes)
        erb.VolumeUuid.TryWriteBytes(buffer.Slice(offset));
        offset += VolumeUuidSize;

        // Creation timestamp (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), erb.CreationTimestampUtcTicks);
        offset += CreationTimestampSize;

        // Volume label (64 bytes)
        var label = erb.VolumeLabel ?? Array.Empty<byte>();
        var labelLen = Math.Min(label.Length, VolumeLabelSize);
        label.AsSpan(0, labelLen).CopyTo(buffer.Slice(offset));
        offset += VolumeLabelSize;

        // Contact info (128 bytes)
        var contact = erb.ContactInfo ?? Array.Empty<byte>();
        var contactLen = Math.Min(contact.Length, ContactInfoSize);
        contact.AsSpan(0, contactLen).CopyTo(buffer.Slice(offset));
        offset += ContactInfoSize;

        // Format version: major (1) + minor (1) + spec revision (2 LE)
        buffer[offset++] = erb.MajorVersion;
        buffer[offset++] = erb.MinorVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), erb.SpecRevision);
        offset += 2;

        // Error count (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), erb.ErrorCount);
        offset += ErrorCountSize;

        // Mount count (8 bytes LE)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), erb.MountCount);

        // Universal block trailer with ERCV tag
        UniversalBlockTrailer.Write(buffer, blockSize, BlockTypeTags.ERCV, generation: 1);
    }

    /// <summary>
    /// Deserializes an emergency recovery block from a block-sized buffer.
    /// Validates the "ERCV" magic at offset 0.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least <paramref name="blockSize"/> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>The deserialized emergency recovery block.</returns>
    /// <exception cref="VdeIdentityException">Magic bytes do not match "ERCV".</exception>
    public static EmergencyRecoveryBlock Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        int offset = 0;

        // Validate magic
        if (buffer[0] != ErcvMagic[0] || buffer[1] != ErcvMagic[1]
            || buffer[2] != ErcvMagic[2] || buffer[3] != ErcvMagic[3])
        {
            throw new VdeIdentityException("Emergency recovery block magic mismatch: expected 'ERCV'.");
        }
        offset += MagicSize;

        // Volume UUID (16 bytes)
        var volumeUuid = new Guid(buffer.Slice(offset, VolumeUuidSize));
        offset += VolumeUuidSize;

        // Creation timestamp (8 bytes LE)
        var creationTs = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += CreationTimestampSize;

        // Volume label (64 bytes)
        var volumeLabel = new byte[VolumeLabelSize];
        buffer.Slice(offset, VolumeLabelSize).CopyTo(volumeLabel);
        offset += VolumeLabelSize;

        // Contact info (128 bytes)
        var contactInfo = new byte[ContactInfoSize];
        buffer.Slice(offset, ContactInfoSize).CopyTo(contactInfo);
        offset += ContactInfoSize;

        // Format version
        var majorVersion = buffer[offset++];
        var minorVersion = buffer[offset++];
        var specRevision = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        // Error count (8 bytes LE)
        var errorCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += ErrorCountSize;

        // Mount count (8 bytes LE)
        var mountCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        return new EmergencyRecoveryBlock(
            volumeUuid, creationTs, volumeLabel, contactInfo,
            majorVersion, minorVersion, specRevision, errorCount, mountCount);
    }

    // ── Stream I/O ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the emergency recovery block from a stream at the fixed block position (block 9).
    /// No decryption keys are required -- the block is always plaintext.
    /// </summary>
    /// <param name="stream">The VDE stream to read from.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>The deserialized emergency recovery block.</returns>
    public static EmergencyRecoveryBlock ReadFromStream(Stream stream, int blockSize)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (blockSize <= 0 || blockSize > 65536)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be between 1 and 65536 bytes.");

        long position = (long)FixedBlockNumber * blockSize;
        stream.Seek(position, SeekOrigin.Begin);

        var buffer = new byte[blockSize];
        int totalRead = 0;
        while (totalRead < blockSize)
        {
            int bytesRead = stream.Read(buffer, totalRead, blockSize - totalRead);
            if (bytesRead == 0)
                throw new VdeIdentityException($"Unexpected end of stream reading emergency recovery block at offset {position}.");
            totalRead += bytesRead;
        }

        return Deserialize(buffer, blockSize);
    }

    /// <summary>
    /// Writes the emergency recovery block to a stream at the fixed block position (block 9).
    /// </summary>
    /// <param name="stream">The VDE stream to write to.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="erb">The emergency recovery block to write.</param>
    public static void WriteToStream(Stream stream, int blockSize, in EmergencyRecoveryBlock erb)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        var buffer = new byte[blockSize];
        Serialize(erb, buffer, blockSize);

        long position = (long)FixedBlockNumber * blockSize;
        stream.Seek(position, SeekOrigin.Begin);
        stream.Write(buffer, 0, blockSize);
    }

    /// <summary>
    /// Checks if an emergency recovery block is present at block 9 by reading the magic bytes.
    /// </summary>
    /// <param name="stream">The VDE stream to check.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>True if the first 4 bytes at block 9 match "ERCV".</returns>
    public static bool IsPresent(Stream stream, int blockSize)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        long position = (long)FixedBlockNumber * blockSize;
        if (stream.Length < position + MagicSize)
            return false;

        stream.Seek(position, SeekOrigin.Begin);

        Span<byte> magic = stackalloc byte[MagicSize];
        var buffer = new byte[MagicSize];
        int totalRead = 0;
        while (totalRead < MagicSize)
        {
            int bytesRead = stream.Read(buffer, totalRead, MagicSize - totalRead);
            if (bytesRead == 0) return false;
            totalRead += bytesRead;
        }

        return buffer[0] == ErcvMagic[0] && buffer[1] == ErcvMagic[1]
            && buffer[2] == ErcvMagic[2] && buffer[3] == ErcvMagic[3];
    }

    // ── Helper Methods ──────────────────────────────────────────────────

    /// <summary>
    /// Gets the volume label as a trimmed UTF-8 string.
    /// </summary>
    public string GetVolumeLabelString()
    {
        if (VolumeLabel is null) return string.Empty;
        var end = Array.IndexOf(VolumeLabel, (byte)0);
        if (end < 0) end = VolumeLabel.Length;
        return Encoding.UTF8.GetString(VolumeLabel, 0, end);
    }

    /// <summary>
    /// Gets the contact info as a trimmed UTF-8 string.
    /// </summary>
    public string GetContactInfoString()
    {
        if (ContactInfo is null) return string.Empty;
        var end = Array.IndexOf(ContactInfo, (byte)0);
        if (end < 0) end = ContactInfo.Length;
        return Encoding.UTF8.GetString(ContactInfo, 0, end);
    }

    /// <summary>
    /// Creates a contact info byte array from a string, UTF-8 encoded and padded to 128 bytes.
    /// </summary>
    /// <param name="info">The contact information string.</param>
    /// <returns>A 128-byte null-padded UTF-8 buffer.</returns>
    public static byte[] CreateContactInfo(string info)
    {
        var result = new byte[ContactInfoSize];
        if (!string.IsNullOrEmpty(info))
        {
            var bytes = Encoding.UTF8.GetBytes(info);
            var len = Math.Min(bytes.Length, ContactInfoSize);
            Array.Copy(bytes, result, len);
        }
        return result;
    }

    // ── Equality ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Equals(EmergencyRecoveryBlock other) =>
        VolumeUuid == other.VolumeUuid
        && CreationTimestampUtcTicks == other.CreationTimestampUtcTicks
        && MajorVersion == other.MajorVersion
        && MinorVersion == other.MinorVersion
        && SpecRevision == other.SpecRevision
        && ErrorCount == other.ErrorCount
        && MountCount == other.MountCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EmergencyRecoveryBlock other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(VolumeUuid, CreationTimestampUtcTicks, MountCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(EmergencyRecoveryBlock left, EmergencyRecoveryBlock right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(EmergencyRecoveryBlock left, EmergencyRecoveryBlock right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"EmergencyRecoveryBlock(Volume={VolumeUuid}, Created={CreationTimestampUtcTicks}, Mounts={MountCount}, Errors={ErrorCount})";
}
