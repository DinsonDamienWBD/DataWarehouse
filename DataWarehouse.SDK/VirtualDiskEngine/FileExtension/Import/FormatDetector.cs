using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.Import;

/// <summary>
/// Detects virtual disk formats from magic bytes in file headers. Supports DWVD, VHDX,
/// QCOW2, VMDK, VDI, and VHD via magic byte signatures. Raw/Img detection uses file
/// extension and size heuristics. All span-based methods are allocation-free.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Format detector (FEXT-06, FEXT-07)")]
public static class FormatDetector
{
    // ── Magic byte constants (inline for zero-alloc comparisons) ────────

    /// <summary>Minimum header size needed for all magic checks (VDI needs offset 64 + 4 = 68).</summary>
    private const int MinHeaderSize = 68;

    /// <summary>Recommended header read size for DetectFormatAsync.</summary>
    private const int RecommendedHeaderSize = 512;

    // ── Primary detection ───────────────────────────────────────────────

    /// <summary>
    /// Detects the virtual disk format from raw header bytes by checking magic signatures
    /// in priority order: DWVD, VHDX, QCOW2, VMDK, VDI, VHD.
    /// Returns <see cref="VirtualDiskFormat.Unknown"/> if no magic matches (caller may
    /// try Raw/Img heuristic with the extension-aware overload).
    /// </summary>
    /// <param name="headerBytes">
    /// At least 68 bytes from the start of the file for full detection.
    /// Fewer bytes may still detect formats whose magic appears early (DWVD, VHDX, QCOW2, VHD).
    /// </param>
    /// <returns>The detected format, or <see cref="VirtualDiskFormat.Unknown"/>.</returns>
    public static VirtualDiskFormat DetectFormat(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 4)
            return VirtualDiskFormat.Unknown;

        // 1. DWVD: 0x44575644 ("DWVD") at offset 0, big-endian
        if (BinaryPrimitives.ReadUInt32BigEndian(headerBytes) == 0x44575644u)
            return VirtualDiskFormat.Dwvd;

        // 2. VHDX: "vhdxfile" (8 bytes) at offset 0
        if (headerBytes.Length >= 8 &&
            headerBytes[..8].SequenceEqual("vhdxfile"u8))
            return VirtualDiskFormat.Vhdx;

        // 3. QCOW2: 0x514649FB (big-endian) at offset 0
        if (BinaryPrimitives.ReadUInt32BigEndian(headerBytes) == 0x514649FBu)
            return VirtualDiskFormat.Qcow2;

        // 4. VMDK: "KDMV" at offset 0, or text descriptor "# Disk DescriptorFile"
        if (headerBytes[..4].SequenceEqual("KDMV"u8))
            return VirtualDiskFormat.Vmdk;

        if (headerBytes.Length >= 21 &&
            headerBytes[..21].SequenceEqual("# Disk DescriptorFile"u8))
            return VirtualDiskFormat.Vmdk;

        // 5. VDI: 0xBEDA107F (little-endian) at offset 64
        if (headerBytes.Length >= 68)
        {
            uint vdiSig = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(64));
            if (vdiSig == 0xBEDA107Fu)
                return VirtualDiskFormat.Vdi;
        }

        // 6. VHD: "conectix" (8 bytes) at offset 0
        if (headerBytes.Length >= 8 &&
            headerBytes[..8].SequenceEqual("conectix"u8))
            return VirtualDiskFormat.Vhd;

        return VirtualDiskFormat.Unknown;
    }

    /// <summary>
    /// Enhanced format detection that also checks file extension and size for Raw/Img
    /// when magic-based detection returns Unknown.
    /// </summary>
    /// <param name="headerBytes">Header bytes from the file.</param>
    /// <param name="fileExtension">
    /// File extension including leading dot (e.g., ".raw", ".img"). May be null.
    /// </param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <returns>The detected format.</returns>
    public static VirtualDiskFormat DetectFormat(
        ReadOnlySpan<byte> headerBytes, string? fileExtension, long fileSize)
    {
        var result = DetectFormat(headerBytes);
        if (result != VirtualDiskFormat.Unknown)
            return result;

        // Raw/Img heuristic: extension match + size > 512 + sector-aligned
        if (fileExtension is not null && fileSize > 512 && fileSize % 512 == 0)
        {
            var ext = fileExtension.ToLowerInvariant();
            if (ext == ".raw")
                return VirtualDiskFormat.Raw;
            if (ext == ".img")
                return VirtualDiskFormat.Img;
        }

        return VirtualDiskFormat.Unknown;
    }

    /// <summary>
    /// Asynchronously detects the virtual disk format of a file by reading its header
    /// and checking extension/size for Raw/Img fallback.
    /// </summary>
    /// <param name="filePath">Path to the file to detect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detected format.</returns>
    public static async Task<VirtualDiskFormat> DetectFormatAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: RecommendedHeaderSize,
            useAsync: true);

        var bytesToRead = stream.CanSeek ? (int)Math.Min(stream.Length, RecommendedHeaderSize) : RecommendedHeaderSize;
        if (bytesToRead < 4)
            return VirtualDiskFormat.Unknown;

        var buffer = new byte[bytesToRead];
        int totalRead = 0;
        while (totalRead < bytesToRead)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, bytesToRead - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        var extension = Path.GetExtension(filePath);
        return DetectFormat(buffer.AsSpan(0, totalRead), extension, stream.Length);
    }

    /// <summary>
    /// Returns true if the format can be imported (all formats except Unknown and Dwvd).
    /// </summary>
    public static bool IsImportable(VirtualDiskFormat fmt)
        => fmt != VirtualDiskFormat.Unknown && fmt != VirtualDiskFormat.Dwvd;
}
