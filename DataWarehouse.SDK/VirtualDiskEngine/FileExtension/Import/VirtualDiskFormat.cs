using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.Import;

/// <summary>
/// Enumerates the supported virtual disk formats that can be imported into DWVD.
/// Each format (except Raw/Img) is identified by magic bytes at a known offset.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Virtual disk formats (FEXT-07)")]
public enum VirtualDiskFormat : byte
{
    /// <summary>Unknown or unrecognized format.</summary>
    Unknown = 0,

    /// <summary>Native DWVD format (already DWVD, no import needed).</summary>
    Dwvd = 1,

    /// <summary>Microsoft VHD (magic: "conectix" at offset 0 for fixed, or at end-512 for dynamic).</summary>
    Vhd = 2,

    /// <summary>Microsoft VHDX (magic: "vhdxfile" at offset 0).</summary>
    Vhdx = 3,

    /// <summary>VMware VMDK (magic: "KDMV" at offset 0, or "# Disk DescriptorFile" text header).</summary>
    Vmdk = 4,

    /// <summary>QEMU QCOW2 (magic: 0x514649FB big-endian at offset 0).</summary>
    Qcow2 = 5,

    /// <summary>VirtualBox VDI (magic: signature 0xBEDA107F at offset 64).</summary>
    Vdi = 6,

    /// <summary>Raw disk image (no magic, sector-aligned size).</summary>
    Raw = 7,

    /// <summary>Generic disk image (alias for Raw, distinguished by extension).</summary>
    Img = 8,
}

/// <summary>
/// Provides metadata and utility methods for <see cref="VirtualDiskFormat"/> values,
/// including canonical file extensions, human-readable names, magic byte definitions,
/// and content detection capability information.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Virtual disk format info (FEXT-07)")]
public static class VirtualDiskFormatInfo
{
    // ── Magic byte constants ────────────────────────────────────────────

    /// <summary>DWVD magic: "DWVD" (0x44575644) at offset 0.</summary>
    private static readonly byte[] DwvdMagic = [0x44, 0x57, 0x56, 0x44];

    /// <summary>VHD magic: "conectix" at offset 0 (fixed-size VHD footer/header).</summary>
    private static readonly byte[] VhdMagic = "conectix"u8.ToArray();

    /// <summary>VHDX magic: "vhdxfile" at offset 0.</summary>
    private static readonly byte[] VhdxMagic = "vhdxfile"u8.ToArray();

    /// <summary>VMDK sparse magic: "KDMV" at offset 0 (little-endian 0x564D444B).</summary>
    private static readonly byte[] VmdkMagic = "KDMV"u8.ToArray();

    /// <summary>VMDK descriptor text header prefix.</summary>
    private static readonly byte[] VmdkDescriptorMagic = "# Disk DescriptorFile"u8.ToArray();

    /// <summary>QCOW2 magic: 0x514649FB (big-endian) at offset 0.</summary>
    private static readonly byte[] Qcow2Magic = [0x51, 0x46, 0x49, 0xFB];

    /// <summary>VDI signature: 0xBEDA107F (little-endian) at offset 64.</summary>
    private static readonly byte[] VdiMagic = [0x7F, 0x10, 0xDA, 0xBE];

    /// <summary>VDI header text: appears at offset 0 in most VDI files.</summary>
    private static readonly byte[] VdiHeaderText = "<<< Oracle VM VirtualBox Disk Image >>>"u8.ToArray();

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the canonical file extension for the given format (including leading dot).
    /// </summary>
    public static string GetExtension(VirtualDiskFormat fmt) => fmt switch
    {
        VirtualDiskFormat.Dwvd => ".dwvd",
        VirtualDiskFormat.Vhd => ".vhd",
        VirtualDiskFormat.Vhdx => ".vhdx",
        VirtualDiskFormat.Vmdk => ".vmdk",
        VirtualDiskFormat.Qcow2 => ".qcow2",
        VirtualDiskFormat.Vdi => ".vdi",
        VirtualDiskFormat.Raw => ".raw",
        VirtualDiskFormat.Img => ".img",
        _ => string.Empty,
    };

    /// <summary>
    /// Returns a human-readable description of the format.
    /// </summary>
    public static string GetDescription(VirtualDiskFormat fmt) => fmt switch
    {
        VirtualDiskFormat.Dwvd => "DataWarehouse Virtual Disk",
        VirtualDiskFormat.Vhd => "Microsoft VHD",
        VirtualDiskFormat.Vhdx => "Microsoft VHDX",
        VirtualDiskFormat.Vmdk => "VMware VMDK",
        VirtualDiskFormat.Qcow2 => "QEMU QCOW2",
        VirtualDiskFormat.Vdi => "VirtualBox VDI",
        VirtualDiskFormat.Raw => "Raw Disk Image",
        VirtualDiskFormat.Img => "Generic Disk Image",
        _ => "Unknown Format",
    };

    /// <summary>
    /// Returns the magic bytes used to identify the format. Empty for Raw/Img/Unknown.
    /// </summary>
    public static ReadOnlyMemory<byte> GetMagicBytes(VirtualDiskFormat fmt) => fmt switch
    {
        VirtualDiskFormat.Dwvd => DwvdMagic,
        VirtualDiskFormat.Vhd => VhdMagic,
        VirtualDiskFormat.Vhdx => VhdxMagic,
        VirtualDiskFormat.Vmdk => VmdkMagic,
        VirtualDiskFormat.Qcow2 => Qcow2Magic,
        VirtualDiskFormat.Vdi => VdiMagic,
        _ => ReadOnlyMemory<byte>.Empty,
    };

    /// <summary>
    /// Returns the byte offset where the magic bytes appear in the file header.
    /// </summary>
    public static int GetMagicOffset(VirtualDiskFormat fmt) => fmt switch
    {
        VirtualDiskFormat.Vdi => 64, // VDI signature at offset 64
        _ => 0,                      // All others at offset 0
    };

    /// <summary>
    /// Returns true if the format supports content detection via magic bytes.
    /// Raw and Img formats have no magic and require extension + size heuristics.
    /// </summary>
    public static bool SupportsContentDetection(VirtualDiskFormat fmt) => fmt switch
    {
        VirtualDiskFormat.Raw => false,
        VirtualDiskFormat.Img => false,
        VirtualDiskFormat.Unknown => false,
        _ => true,
    };
}
