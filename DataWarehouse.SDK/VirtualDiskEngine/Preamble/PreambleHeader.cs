using System;
using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Flags indicating optional features active in the preamble region.
/// Stored in the Flags field of <see cref="PreambleHeader"/> (bits 0-2).
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-61 bootable preamble flags")]
public enum PreambleFlags : ushort
{
    /// <summary>No optional preamble features active.</summary>
    None = 0,

    /// <summary>Preamble payload sections are compressed.</summary>
    CompressionActive = 1,

    /// <summary>A cryptographic signature is present for secure boot verification.</summary>
    SignaturePresent = 2,

    /// <summary>Runtime payload is encrypted and requires key material to load.</summary>
    EncryptedRuntime = 4,
}

/// <summary>
/// Target CPU architecture for the bootable preamble payloads.
/// Stored in bits 3-5 of the Flags field in <see cref="PreambleHeader"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-61 bootable preamble target architecture")]
public enum TargetArchitecture : byte
{
    /// <summary>x86-64 (AMD64) architecture.</summary>
    X86_64 = 0,

    /// <summary>AArch64 (ARM64) architecture.</summary>
    Aarch64 = 1,

    /// <summary>RISC-V 64-bit architecture.</summary>
    RiscV64 = 2,
}

/// <summary>
/// 64-byte on-disk header for the optional bootable preamble region (VOPT-61).
/// When present, the preamble occupies bytes before Block 0 and contains a micro-kernel,
/// SPDK driver pack, and lightweight runtime for bare-metal boot from a DWVD file.
/// </summary>
/// <remarks>
/// <para>Byte layout (little-endian):</para>
/// <list type="table">
/// <listheader><term>Offset</term><description>Field</description></listheader>
/// <item><term>0-7</term><description>Magic ("DWVD-BOO" as LE uint64)</description></item>
/// <item><term>8-9</term><description>PreambleVersion (currently 1)</description></item>
/// <item><term>10-11</term><description>Flags (bits 0-2: PreambleFlags, bits 3-5: TargetArchitecture)</description></item>
/// <item><term>12-15</term><description>ContentHashOffset (offset of 32-byte BLAKE3 content hash)</description></item>
/// <item><term>16-23</term><description>PreambleTotalSize (total bytes including all payloads)</description></item>
/// <item><term>24-31</term><description>VdeOffset (byte offset of Block 0, must be block-aligned)</description></item>
/// <item><term>32-35</term><description>KernelOffset</description></item>
/// <item><term>36-39</term><description>KernelSize</description></item>
/// <item><term>40-43</term><description>SpdkOffset</description></item>
/// <item><term>44-47</term><description>SpdkSize</description></item>
/// <item><term>48-51</term><description>RuntimeOffset</description></item>
/// <item><term>52-55</term><description>RuntimeSize</description></item>
/// <item><term>56-63</term><description>HeaderChecksum (BLAKE3(bytes[0..55]) truncated to 8 bytes)</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-61 bootable preamble header struct")]
public readonly struct PreambleHeader : IEquatable<PreambleHeader>
{
    /// <summary>Serialized size of the preamble header in bytes.</summary>
    public const int SerializedSize = 64;

    /// <summary>The 8-byte magic signature identifying a DWVD bootable preamble ("DWVD-BOO").</summary>
    public static ReadOnlySpan<byte> MagicBytes => "DWVD-BOO"u8;

    /// <summary>
    /// Expected magic value as a little-endian uint64 representation of "DWVD-BOO".
    /// </summary>
    public static readonly ulong ExpectedMagic = BuildExpectedMagic();

    /// <summary>Magic identifier (bytes 0-7). Must equal "DWVD-BOO" as LE uint64.</summary>
    public ulong Magic { get; }

    /// <summary>Preamble format version (bytes 8-9). Currently 1.</summary>
    public ushort PreambleVersion { get; }

    /// <summary>
    /// Combined flags field (bytes 10-11).
    /// Bits 0-2: <see cref="PreambleFlags"/>, bits 3-5: <see cref="TargetArchitecture"/>.
    /// </summary>
    public ushort Flags { get; }

    /// <summary>Byte offset within the preamble region of the 32-byte BLAKE3 content hash (bytes 12-15).</summary>
    public uint ContentHashOffset { get; }

    /// <summary>Total size of the preamble region in bytes, including all payloads (bytes 16-23).</summary>
    public ulong PreambleTotalSize { get; }

    /// <summary>
    /// Byte offset of VDE Block 0 from the start of the file (bytes 24-31).
    /// Must be aligned to the VDE block size and >= <see cref="PreambleTotalSize"/>.
    /// </summary>
    public ulong VdeOffset { get; }

    /// <summary>Byte offset of the micro-kernel payload within the preamble (bytes 32-35).</summary>
    public uint KernelOffset { get; }

    /// <summary>Size of the micro-kernel payload in bytes (bytes 36-39).</summary>
    public uint KernelSize { get; }

    /// <summary>Byte offset of the SPDK driver pack within the preamble (bytes 40-43).</summary>
    public uint SpdkOffset { get; }

    /// <summary>Size of the SPDK driver pack in bytes (bytes 44-47).</summary>
    public uint SpdkSize { get; }

    /// <summary>Byte offset of the lightweight runtime within the preamble (bytes 48-51).</summary>
    public uint RuntimeOffset { get; }

    /// <summary>Size of the lightweight runtime in bytes (bytes 52-55).</summary>
    public uint RuntimeSize { get; }

    /// <summary>
    /// Header integrity checksum (bytes 56-63).
    /// Computed as BLAKE3(bytes[0..55]) truncated to 8 bytes (SHA-256 fallback until BLAKE3 ships in BCL).
    /// </summary>
    public ulong HeaderChecksum { get; }

    /// <summary>
    /// Creates a new <see cref="PreambleHeader"/> with all fields specified.
    /// </summary>
    public PreambleHeader(
        ulong magic,
        ushort preambleVersion,
        ushort flags,
        uint contentHashOffset,
        ulong preambleTotalSize,
        ulong vdeOffset,
        uint kernelOffset,
        uint kernelSize,
        uint spdkOffset,
        uint spdkSize,
        uint runtimeOffset,
        uint runtimeSize,
        ulong headerChecksum)
    {
        Magic = magic;
        PreambleVersion = preambleVersion;
        Flags = flags;
        ContentHashOffset = contentHashOffset;
        PreambleTotalSize = preambleTotalSize;
        VdeOffset = vdeOffset;
        KernelOffset = kernelOffset;
        KernelSize = kernelSize;
        SpdkOffset = spdkOffset;
        SpdkSize = spdkSize;
        RuntimeOffset = runtimeOffset;
        RuntimeSize = runtimeSize;
        HeaderChecksum = headerChecksum;
    }

    /// <summary>Extracts the <see cref="PreambleFlags"/> from the combined Flags field (bits 0-2).</summary>
    public PreambleFlags ParsedFlags => (PreambleFlags)(Flags & 0x07);

    /// <summary>Extracts the <see cref="TargetArchitecture"/> from the combined Flags field (bits 3-5).</summary>
    public TargetArchitecture Architecture => (TargetArchitecture)((Flags >> 3) & 0x07);

    /// <summary>
    /// Serializes a <see cref="PreambleHeader"/> into exactly 64 bytes using little-endian byte order.
    /// </summary>
    /// <param name="header">The header to serialize.</param>
    /// <param name="buffer">Destination buffer, must be at least 64 bytes.</param>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static void Serialize(in PreambleHeader header, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt64LittleEndian(buffer, header.Magic);                          // 0-7
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8), header.PreambleVersion);       // 8-9
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(10), header.Flags);                // 10-11
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12), header.ContentHashOffset);    // 12-15
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(16), header.PreambleTotalSize);    // 16-23
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(24), header.VdeOffset);            // 24-31
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(32), header.KernelOffset);         // 32-35
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(36), header.KernelSize);           // 36-39
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(40), header.SpdkOffset);           // 40-43
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(44), header.SpdkSize);             // 44-47
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(48), header.RuntimeOffset);        // 48-51
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(52), header.RuntimeSize);          // 52-55
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(56), header.HeaderChecksum);       // 56-63
    }

    /// <summary>
    /// Deserializes a <see cref="PreambleHeader"/> from a 64-byte little-endian buffer.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least 64 bytes.</param>
    /// <returns>The deserialized preamble header.</returns>
    /// <exception cref="ArgumentException">Buffer is too small.</exception>
    public static PreambleHeader Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        return new PreambleHeader(
            magic: BinaryPrimitives.ReadUInt64LittleEndian(buffer),                         // 0-7
            preambleVersion: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(8)),      // 8-9
            flags: BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(10)),               // 10-11
            contentHashOffset: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12)),   // 12-15
            preambleTotalSize: BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(16)),   // 16-23
            vdeOffset: BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(24)),           // 24-31
            kernelOffset: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(32)),        // 32-35
            kernelSize: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(36)),          // 36-39
            spdkOffset: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(40)),          // 40-43
            spdkSize: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(44)),            // 44-47
            runtimeOffset: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(48)),       // 48-51
            runtimeSize: BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(52)),         // 52-55
            headerChecksum: BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(56)));     // 56-63
    }

    /// <summary>
    /// Validates that the <see cref="Magic"/> field matches the expected "DWVD-BOO" value.
    /// </summary>
    /// <returns><c>true</c> if the magic is valid; otherwise <c>false</c>.</returns>
    public bool ValidateMagic() => Magic == ExpectedMagic;

    /// <summary>
    /// Validates that <see cref="VdeOffset"/> is aligned to the specified block size
    /// and is at least as large as <see cref="PreambleTotalSize"/>.
    /// </summary>
    /// <param name="blockSize">The VDE block size in bytes (must be a power of two).</param>
    /// <returns><c>true</c> if VdeOffset is valid; otherwise <c>false</c>.</returns>
    public bool ValidateVdeOffset(int blockSize)
    {
        if (blockSize <= 0)
            return false;

        // VdeOffset must be aligned to blockSize
        if (VdeOffset % (ulong)blockSize != 0)
            return false;

        // VdeOffset must be >= PreambleTotalSize (preamble must fit before Block 0)
        if (VdeOffset < PreambleTotalSize)
            return false;

        return true;
    }

    /// <inheritdoc />
    public bool Equals(PreambleHeader other) =>
        Magic == other.Magic
        && PreambleVersion == other.PreambleVersion
        && Flags == other.Flags
        && ContentHashOffset == other.ContentHashOffset
        && PreambleTotalSize == other.PreambleTotalSize
        && VdeOffset == other.VdeOffset
        && KernelOffset == other.KernelOffset
        && KernelSize == other.KernelSize
        && SpdkOffset == other.SpdkOffset
        && SpdkSize == other.SpdkSize
        && RuntimeOffset == other.RuntimeOffset
        && RuntimeSize == other.RuntimeSize
        && HeaderChecksum == other.HeaderChecksum;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PreambleHeader other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Magic);
        h.Add(PreambleVersion);
        h.Add(Flags);
        h.Add(ContentHashOffset);
        h.Add(PreambleTotalSize);
        h.Add(VdeOffset);
        h.Add(KernelOffset);
        h.Add(KernelSize);
        h.Add(SpdkOffset);
        h.Add(SpdkSize);
        h.Add(RuntimeOffset);
        h.Add(RuntimeSize);
        h.Add(HeaderChecksum);
        return h.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PreambleHeader left, PreambleHeader right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PreambleHeader left, PreambleHeader right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"PreambleHeader(v{PreambleVersion}, Flags=0x{Flags:X4}, VdeOffset=0x{VdeOffset:X}, " +
        $"Total={PreambleTotalSize}, Kernel={KernelSize}B, SPDK={SpdkSize}B, Runtime={RuntimeSize}B)";

    private static ulong BuildExpectedMagic()
    {
        Span<byte> temp = stackalloc byte[8];
        "DWVD-BOO"u8.CopyTo(temp);
        return BinaryPrimitives.ReadUInt64LittleEndian(temp);
    }
}
