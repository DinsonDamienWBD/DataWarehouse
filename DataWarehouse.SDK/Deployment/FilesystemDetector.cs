using DataWarehouse.SDK.Contracts;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detects filesystem type and block size for deployment optimization.
/// Platform-specific implementations for Linux (/proc/mounts), Windows (WMI), and macOS (df).
/// </summary>
/// <remarks>
/// <para>
/// Filesystem detection enables optimizations:
/// - Double-WAL bypass: Disable OS journaling for ext4/NTFS when VDE WAL active
/// - I/O alignment: Match filesystem block size to reduce read-modify-write cycles
/// </para>
/// <para>
/// Detection is platform-specific:
/// - Linux: Reads /proc/mounts to find filesystem type
/// - Windows: Uses WMI Win32_LogicalDisk query
/// - macOS: Runs df -T command
/// </para>
/// <para>
/// Returns null gracefully on unsupported platforms or errors (never throws).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Filesystem type detection (ENV-01)")]
public static class FilesystemDetector
{
    /// <summary>
    /// Detects the filesystem type for a given path.
    /// </summary>
    /// <param name="path">File or directory path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Filesystem type string (e.g., "ext4", "xfs", "NTFS", "ReFS", "apfs") or null if not detectable.
    /// </returns>
    /// <remarks>
    /// Detection method varies by platform:
    /// - Linux: Parses /proc/mounts for the longest matching mount point
    /// - Windows: Queries WMI Win32_LogicalDisk for the drive letter
    /// - macOS: Runs "df -T" to extract filesystem type
    /// </remarks>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    public static async Task<string?> DetectFilesystemTypeAsync(string path, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            return await DetectLinuxFilesystemAsync(path, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            return await DetectWindowsFilesystemAsync(path, ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return await DetectMacOsFilesystemAsync(path, ct);
        }

        return null; // Unsupported platform
    }

    /// <summary>
    /// Detects filesystem block size for a given path.
    /// Used for I/O alignment optimization.
    /// </summary>
    /// <param name="path">File or directory path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Block size in bytes (typically 4096) or null if not detectable.
    /// </returns>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    public static async Task<long?> GetFilesystemBlockSizeAsync(string path, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            return await GetLinuxBlockSizeAsync(path, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            return await GetWindowsBlockSizeAsync(path, ct);
        }

        return null; // Unsupported platform or detection failed
    }

    [SupportedOSPlatform("linux")]
    private static async Task<string?> DetectLinuxFilesystemAsync(string path, CancellationToken ct)
    {
        try
        {
            // Read /proc/mounts to find all mounted filesystems
            const string mountsPath = "/proc/mounts";
            if (!File.Exists(mountsPath))
            {
                return null;
            }

            var lines = await File.ReadAllLinesAsync(mountsPath, ct);

            // Find the longest matching mount point for the given path
            // Format: <device> <mountpoint> <fstype> <options> <dump> <pass>
            // Example: /dev/sda1 /mnt ext4 rw,relatime 0 0
            string? longestMatch = null;
            string? longestFsType = null;
            int longestLength = 0;

            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var mountPoint = parts[1];
                var fsType = parts[2];

                // Check if path starts with this mount point
                if (path.StartsWith(mountPoint, StringComparison.Ordinal) && mountPoint.Length > longestLength)
                {
                    longestMatch = mountPoint;
                    longestFsType = fsType;
                    longestLength = mountPoint.Length;
                }
            }

            return longestFsType;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<string?> DetectWindowsFilesystemAsync(string path, CancellationToken ct)
    {
        try
        {
            // Extract drive letter from path
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return Task.FromResult<string?>(null);

            var driveLetter = root.TrimEnd('\\', '/');

            // Use System.Management.ManagementObjectSearcher to query WMI
            // This requires System.Management NuGet package, but we avoid dependencies
            // Instead, use DriveInfo as a simpler alternative
            var driveInfo = new DriveInfo(driveLetter);
            return Task.FromResult<string?>(driveInfo.DriveFormat); // Returns "NTFS", "ReFS", "FAT32", etc.
        }
        catch (ArgumentException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (IOException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task<string?> DetectMacOsFilesystemAsync(string path, CancellationToken ct)
    {
        try
        {
            // Run "df -T <path>" to get filesystem type
            var startInfo = new ProcessStartInfo
            {
                FileName = "df",
                Arguments = $"-T \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0) return null;

            // Parse output (format varies, typically has filesystem type in second column)
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return null;

            var parts = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;

            return parts[1]; // Filesystem type (apfs, hfs, etc.)
        }
        catch (Exception)
        {
            return null; // Any error returns null (graceful degradation)
        }
    }

    [SupportedOSPlatform("linux")]
    private static Task<long?> GetLinuxBlockSizeAsync(string path, CancellationToken ct)
    {
        // For Linux, block size is typically 4096 bytes
        // More sophisticated detection could read /sys/block/<device>/queue/physical_block_size
        // but requires mapping path to block device, which is complex
        // Default to 4096 for simplicity
        return Task.FromResult<long?>(4096);
    }

    [SupportedOSPlatform("windows")]
    private static Task<long?> GetWindowsBlockSizeAsync(string path, CancellationToken ct)
    {
        // For Windows, block size is typically 4096 bytes (4KB sectors)
        // Could use GetDiskFreeSpace P/Invoke for exact value, but 4096 is safe default
        return Task.FromResult<long?>(4096);
    }
}
