using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Recovery;

/// <summary>
/// Emergency Recovery Block (AD-67, VOPT-77): always at fixed block 14 of the VDE.
/// Contains plaintext identification, admin contact, and an HMAC integrity seal.
/// This block is NEVER encrypted regardless of module configuration. When the first
/// 32 KB of a VDE file is physically destroyed (NVMe failure, ransomware, accidental dd),
/// this block at block 14 provides identification and contact information needed to begin
/// manual or automated recovery.
///
/// Wire layout (4080 usable bytes after trailer):
///   +0x00  ulong  RecoveryMagic     "DWVDRCVR" (0x4457564452435652)
///   +0x08  Guid   VdeUuid           (16 bytes)
///   +0x18  ulong  CreationTimestamp (8 bytes, UTC nanoseconds)
///   +0x20  ushort FormatMajorVersion
///   +0x22  ushort FormatMinorVersion
///   +0x24  ulong  ModuleManifest    (creation-time snapshot, NOT updated online)
///   +0x2C  ulong  TotalBlocks
///   +0x34  uint   BlockSize
///   +0x38  ushort InodeSize
///   +0x3A  2 bytes padding
///   +0x3C  128 bytes AdminContact   (UTF-8, null-padded)
///   +0xBC  128 bytes OrganizationName (UTF-8, null-padded)
///   +0x13C 64 bytes  VolumeLabel    (UTF-8, null-padded)
///   +0x17C 32 bytes  NamespacePrefix (UTF-8, null-padded)
///   +0x19C 256 bytes RecoveryNotes  (UTF-8, null-padded)
///   +0x29C 32 bytes  RecoveryHMAC   (HMAC-BLAKE3; BCL stand-in: HMACSHA256)
///   (remainder zeroed)
///
/// HMAC covers bytes [0x00..0x29B] (everything before the HMAC field).
///
/// PRODUCTION NOTE: ComputeHmac / VerifyHmac currently use HMACSHA256 as a stand-in
/// for HMAC-BLAKE3 because BLAKE3 is not available in the .NET BCL. When a production
/// BLAKE3 library (e.g. Blake3.NET) is integrated, replace the HMACSHA256 call with
/// the BLAKE3 equivalent for improved performance and security properties.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 VOPT-77: Emergency Recovery Block at block 14 (AD-67)")]
public sealed class EmergencyRecoveryBlock
{
    // ── Layout Constants ─────────────────────────────────────────────────

    /// <summary>Block number at which the RCVR block is always written. Hardcoded by spec.</summary>
    public const int FixedBlockNumber = 14;

    /// <summary>Magic signature ("DWVDRCVR" big-endian ASCII).</summary>
    public const ulong RecoveryMagic = 0x4457564452435652UL;

    /// <summary>Block type tag for RCVR blocks ("RCVR" as big-endian uint32).</summary>
    public const uint BlockTypeTag = BlockTypeTags.RCVR; // 0x52435652

    private const int AdminContactOffset = 0x3C;
    private const int AdminContactSize = 128;
    private const int OrganizationNameOffset = 0xBC;
    private const int OrganizationNameSize = 128;
    private const int VolumeLabelOffset = 0x13C;
    private const int VolumeLabelSize = 64;
    private const int NamespacePrefixOffset = 0x17C;
    private const int NamespacePrefixSize = 32;
    private const int RecoveryNotesOffset = 0x19C;
    private const int RecoveryNotesSize = 256;
    private const int RecoveryHmacOffset = 0x29C;
    private const int RecoveryHmacSize = 32;

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>Volume UUID — matches the VDE's SuperblockV2.VolumeUuid.</summary>
    public Guid VdeUuid { get; set; }

    /// <summary>Volume creation timestamp (UTC nanoseconds since Unix epoch).</summary>
    public ulong CreationTimestamp { get; set; }

    /// <summary>Format major version at creation time.</summary>
    public ushort FormatMajorVersion { get; set; }

    /// <summary>Format minor version at creation time.</summary>
    public ushort FormatMinorVersion { get; set; }

    /// <summary>Module manifest snapshot at creation time. NOT updated online.</summary>
    public ulong ModuleManifest { get; set; }

    /// <summary>Total number of blocks in the volume.</summary>
    public ulong TotalBlocks { get; set; }

    /// <summary>Block size in bytes.</summary>
    public uint BlockSize { get; set; }

    /// <summary>Inode size in bytes.</summary>
    public ushort InodeSize { get; set; }

    /// <summary>Administrator contact information (max 128 UTF-8 bytes).</summary>
    public string AdminContact { get; set; } = string.Empty;

    /// <summary>Organization name (max 128 UTF-8 bytes).</summary>
    public string OrganizationName { get; set; } = string.Empty;

    /// <summary>Volume label (max 64 UTF-8 bytes).</summary>
    public string VolumeLabel { get; set; } = string.Empty;

    /// <summary>Namespace prefix for dw:// URI scheme (max 32 UTF-8 bytes).</summary>
    public string NamespacePrefix { get; set; } = string.Empty;

    /// <summary>
    /// Free-form recovery notes: escalation procedures, backup locations, vendor contacts
    /// (max 256 UTF-8 bytes).
    /// </summary>
    public string RecoveryNotes { get; set; } = string.Empty;

    /// <summary>HMAC integrity seal (32 bytes, HMAC-BLAKE3 in production / HMACSHA256 stand-in).</summary>
    public byte[] RecoveryHMAC { get; set; } = new byte[RecoveryHmacSize];

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Serializes this block into <paramref name="buffer"/> (which must be at least
    /// <paramref name="blockSize"/> bytes). Writes the RCVR universal block trailer.
    /// The HMAC field must have been populated by the caller via <see cref="ComputeHmac"/>
    /// before calling this method.
    /// </summary>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, UniversalBlockTrailer.Size);

        // Zero the entire block first (ensures null-padding of string fields)
        buffer.Slice(0, blockSize).Clear();

        int offset = 0;

        // +0x00: RecoveryMagic (8 bytes, little-endian)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), RecoveryMagic);
        offset += 8;

        // +0x08: VdeUuid (16 bytes)
        VdeUuid.TryWriteBytes(buffer.Slice(offset));
        offset += 16;

        // +0x18: CreationTimestamp (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), CreationTimestamp);
        offset += 8;

        // +0x20: FormatMajorVersion (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), FormatMajorVersion);
        offset += 2;

        // +0x22: FormatMinorVersion (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), FormatMinorVersion);
        offset += 2;

        // +0x24: ModuleManifest (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), ModuleManifest);
        offset += 8;

        // +0x2C: TotalBlocks (8 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset), TotalBlocks);
        offset += 8;

        // +0x34: BlockSize (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), BlockSize);
        offset += 4;

        // +0x38: InodeSize (2 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), InodeSize);
        offset += 2;

        // +0x3A: 2 bytes padding (already zeroed)
        offset += 2;

        // +0x3C: AdminContact (128 bytes, UTF-8 null-padded)
        WriteFixedString(buffer.Slice(AdminContactOffset), AdminContact, AdminContactSize);

        // +0xBC: OrganizationName (128 bytes, UTF-8 null-padded)
        WriteFixedString(buffer.Slice(OrganizationNameOffset), OrganizationName, OrganizationNameSize);

        // +0x13C: VolumeLabel (64 bytes, UTF-8 null-padded)
        WriteFixedString(buffer.Slice(VolumeLabelOffset), VolumeLabel, VolumeLabelSize);

        // +0x17C: NamespacePrefix (32 bytes, UTF-8 null-padded)
        WriteFixedString(buffer.Slice(NamespacePrefixOffset), NamespacePrefix, NamespacePrefixSize);

        // +0x19C: RecoveryNotes (256 bytes, UTF-8 null-padded)
        WriteFixedString(buffer.Slice(RecoveryNotesOffset), RecoveryNotes, RecoveryNotesSize);

        // +0x29C: RecoveryHMAC (32 bytes)
        var hmac = RecoveryHMAC ?? Array.Empty<byte>();
        var hmacLen = Math.Min(hmac.Length, RecoveryHmacSize);
        hmac.AsSpan(0, hmacLen).CopyTo(buffer.Slice(RecoveryHmacOffset));

        // Write the universal block trailer (RCVR tag, generation 0)
        UniversalBlockTrailer.Write(buffer, blockSize, BlockTypeTag, generation: 0);
    }

    /// <summary>
    /// Deserializes an Emergency Recovery Block from <paramref name="buffer"/>.
    /// Validates the magic signature.
    /// </summary>
    /// <exception cref="InvalidDataException">Magic mismatch — buffer does not contain a valid RCVR block.</exception>
    public static EmergencyRecoveryBlock Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        if (buffer.Length < blockSize)
            throw new ArgumentException($"Buffer must be at least {blockSize} bytes.", nameof(buffer));

        int offset = 0;

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        if (magic != RecoveryMagic)
            throw new InvalidDataException(
                $"RCVR magic mismatch: expected 0x{RecoveryMagic:X16}, got 0x{magic:X16}");

        var vdeUuid = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        var creationTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var formatMajor = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        var formatMinor = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        var moduleManifest = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var totalBlocks = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        var blockSizeVal = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        // offset += 2; offset += 2; // padding — skip to named offsets

        var adminContact = ReadFixedString(buffer.Slice(AdminContactOffset), AdminContactSize);
        var organizationName = ReadFixedString(buffer.Slice(OrganizationNameOffset), OrganizationNameSize);
        var volumeLabel = ReadFixedString(buffer.Slice(VolumeLabelOffset), VolumeLabelSize);
        var namespacePrefix = ReadFixedString(buffer.Slice(NamespacePrefixOffset), NamespacePrefixSize);
        var recoveryNotes = ReadFixedString(buffer.Slice(RecoveryNotesOffset), RecoveryNotesSize);

        var hmac = new byte[RecoveryHmacSize];
        buffer.Slice(RecoveryHmacOffset, RecoveryHmacSize).CopyTo(hmac);

        return new EmergencyRecoveryBlock
        {
            VdeUuid = vdeUuid,
            CreationTimestamp = creationTimestamp,
            FormatMajorVersion = formatMajor,
            FormatMinorVersion = formatMinor,
            ModuleManifest = moduleManifest,
            TotalBlocks = totalBlocks,
            BlockSize = blockSizeVal,
            InodeSize = inodeSize,
            AdminContact = adminContact,
            OrganizationName = organizationName,
            VolumeLabel = volumeLabel,
            NamespacePrefix = namespacePrefix,
            RecoveryNotes = recoveryNotes,
            RecoveryHMAC = hmac,
        };
    }

    /// <summary>
    /// Computes the HMAC over bytes [0x00..0x29B] (all fields before the HMAC field).
    ///
    /// PRODUCTION NOTE: Uses HMACSHA256 as a stand-in for HMAC-BLAKE3. Replace with
    /// a BLAKE3 HMAC when Blake3.NET or an equivalent is available in the project.
    /// The key should be derived from hardware_serial + recovery_passphrase.
    /// </summary>
    public byte[] ComputeHmac(byte[] hmacKey)
    {
        if (hmacKey is null) throw new ArgumentNullException(nameof(hmacKey));

        // Serialize into a temp buffer to get the exact byte range to authenticate
        int blockSize = (int)BlockSize > 0 ? (int)BlockSize : FormatConstants.DefaultBlockSize;
        var temp = new byte[blockSize];
        // Temporarily clear HMAC field, serialize, then compute over [0..0x29B]
        var savedHmac = RecoveryHMAC;
        RecoveryHMAC = new byte[RecoveryHmacSize];
        try
        {
            Serialize(temp, blockSize);
        }
        finally
        {
            RecoveryHMAC = savedHmac;
        }

        // HMAC covers bytes [0x00..0x29B] (668 bytes)
        using var hmac = new HMACSHA256(hmacKey);
        return hmac.ComputeHash(temp, 0, RecoveryHmacOffset);
    }

    /// <summary>
    /// Verifies the stored <see cref="RecoveryHMAC"/> by recomputing it from the current
    /// field values and performing a constant-time comparison.
    /// </summary>
    public bool VerifyHmac(byte[] hmacKey)
    {
        var expected = ComputeHmac(hmacKey);
        return CryptographicOperations.FixedTimeEquals(expected, RecoveryHMAC);
    }

    // ── String Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="value"/> as UTF-8 into <paramref name="dest"/>, truncating to
    /// <paramref name="maxBytes"/> and null-padding any remaining bytes.
    /// </summary>
    public static void WriteFixedString(Span<byte> dest, string value, int maxBytes)
    {
        if (dest.Length < maxBytes)
            throw new ArgumentException($"Destination span must be at least {maxBytes} bytes.", nameof(dest));

        dest.Slice(0, maxBytes).Clear();
        if (string.IsNullOrEmpty(value)) return;

        var encoded = Encoding.UTF8.GetBytes(value);
        var len = Math.Min(encoded.Length, maxBytes);
        encoded.AsSpan(0, len).CopyTo(dest);
    }

    /// <summary>
    /// Reads a null-terminated UTF-8 string from <paramref name="source"/> up to
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public static string ReadFixedString(ReadOnlySpan<byte> source, int maxBytes)
    {
        if (source.Length < maxBytes)
            maxBytes = source.Length;

        var end = source.Slice(0, maxBytes).IndexOf((byte)0);
        var len = end < 0 ? maxBytes : end;
        return Encoding.UTF8.GetString(source.Slice(0, len));
    }
}
