using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Provides thin provisioning support for DWVD v2.0 files via OS-native sparse file semantics.
/// On Windows (NTFS), uses FSCTL_SET_SPARSE. On Linux (ext4/xfs/btrfs), sparse files are
/// created naturally via SetLength + seek-past-write. On macOS (APFS), sparse files are
/// supported natively.
/// </summary>
/// <remarks>
/// Thin provisioning allows creating a VDE with a large logical size (e.g. 1 TB) while only
/// consuming physical disk space for the blocks that have been written (~1 MB for metadata).
/// Unwritten regions read as zeros and occupy no disk space.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 thin provisioning (VDE2-06)")]
public static class ThinProvisioning
{
    // ── Windows P/Invoke ────────────────────────────────────────────────

    private const int FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeHandle hDevice,
        int dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(
        string lpFileName,
        out uint lpFileSizeHigh);

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a sparse file at the specified path with the given logical size.
    /// The file will occupy minimal physical space until blocks are written.
    /// </summary>
    /// <param name="path">Full path for the new VDE file.</param>
    /// <param name="logicalSize">Logical file size in bytes.</param>
    /// <returns>An open <see cref="FileStream"/> positioned at byte 0.</returns>
    /// <exception cref="ArgumentException">Path is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Logical size is not positive.</exception>
    public static FileStream CreateSparseFile(string path, long logicalSize)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("File path cannot be null or empty.", nameof(path));
        if (logicalSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalSize), "Logical size must be positive.");

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 4096, FileOptions.None);

        try
        {
            // Set file length (creates sparse extent on NTFS/ext4/APFS)
            stream.SetLength(logicalSize);

            // On Windows, explicitly mark as sparse via FSCTL_SET_SPARSE
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SetSparseFlag(stream);
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a block of data at the specified byte offset in a sparse file.
    /// Unwritten regions between written blocks remain sparse holes (zero-read, no allocation).
    /// </summary>
    /// <param name="stream">An open file stream (should be sparse).</param>
    /// <param name="blockOffset">Byte offset where the block should be written.</param>
    /// <param name="data">Block data to write.</param>
    public static void WriteSparseBlock(FileStream stream, long blockOffset, ReadOnlySpan<byte> data)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        stream.Seek(blockOffset, SeekOrigin.Begin);
        stream.Write(data);
    }

    /// <summary>
    /// Checks whether the current operating system supports sparse files.
    /// </summary>
    /// <returns>
    /// True on Windows (NTFS assumed), Linux (ext4/xfs/btrfs assumed), or macOS (APFS assumed).
    /// False on unrecognized platforms.
    /// </returns>
    public static bool IsSparseFileSupported()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true; // NTFS supports sparse files
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return true; // ext4, xfs, btrfs all support sparse
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return true; // APFS supports sparse files
        return false;
    }

    /// <summary>
    /// Returns the physical (on-disk) size of a file, accounting for sparse regions.
    /// On Windows uses GetCompressedFileSize. On Linux uses file allocation info.
    /// Falls back to <see cref="FileInfo.Length"/> if native calls fail.
    /// </summary>
    /// <param name="path">Path to the file.</param>
    /// <returns>Physical size in bytes.</returns>
    public static long GetPhysicalSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("File path cannot be null or empty.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetPhysicalSizeWindows(path);
        }

        // Linux/macOS: fallback to FileInfo.Length
        // In production, stat().st_blocks * 512 would be used via P/Invoke,
        // but FileInfo.Length is used here as a cross-platform fallback.
        // The actual sparse behavior is OS-level; this returns logical size on non-Windows.
        return GetPhysicalSizeUnix(path);
    }

    /// <summary>
    /// Returns the logical (apparent) size of a file.
    /// </summary>
    /// <param name="path">Path to the file.</param>
    /// <returns>Logical size in bytes.</returns>
    public static long GetLogicalSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("File path cannot be null or empty.", nameof(path));

        return new FileInfo(path).Length;
    }

    // ── Private Helpers ─────────────────────────────────────────────────

    private static void SetSparseFlag(FileStream stream)
    {
        try
        {
            bool result = DeviceIoControl(
                stream.SafeFileHandle,
                FSCTL_SET_SPARSE,
                IntPtr.Zero, 0,
                IntPtr.Zero, 0,
                out _,
                IntPtr.Zero);

            if (!result)
            {
                // Non-fatal: file system may not support sparse (e.g. FAT32).
                // The VDE will still work but without thin provisioning benefits.
                var error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine(
                    $"FSCTL_SET_SPARSE failed with Win32 error {error}. " +
                    "Thin provisioning may not be effective on this file system.");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: continue without sparse flag
            System.Diagnostics.Debug.WriteLine(
                $"Failed to set sparse flag: {ex.Message}. Continuing without thin provisioning.");
        }
    }

    private static long GetPhysicalSizeWindows(string path)
    {
        try
        {
            uint low = GetCompressedFileSizeW(path, out uint high);
            if (low == 0xFFFFFFFF)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    // Fallback to logical size
                    return new FileInfo(path).Length;
                }
            }
            return ((long)high << 32) | low;
        }
        catch
        {
            return new FileInfo(path).Length;
        }
    }

    private static long GetPhysicalSizeUnix(string path)
    {
        // Use 'du -k' to get actual disk usage (includes holes/sparse accounting).
        // stat().st_blocks * 512 is the gold standard, but requires P/Invoke.
        // 'du -k' is universally available on Linux/macOS and returns 1K-block counts.
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "du",
                    ArgumentList = { "-k", "--", path },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // du output format: "<1K-blocks>\t<path>"
            var tab = output.IndexOf('\t');
            if (tab > 0 && long.TryParse(output.AsSpan(0, tab).Trim(), out long kiloBlocks))
                return kiloBlocks * 1024L;

            return new FileInfo(path).Length;
        }
        catch
        {
            return new FileInfo(path).Length;
        }
    }
}
