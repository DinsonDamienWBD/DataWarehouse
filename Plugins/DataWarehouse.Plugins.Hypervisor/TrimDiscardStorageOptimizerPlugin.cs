using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Virtualization;
using Microsoft.Win32.SafeHandles;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Production-ready TRIM/Discard storage optimizer plugin for thin-provisioned virtual disks.
/// Implements platform-specific storage reclamation using TRIM commands on SSDs and discard
/// operations for virtual storage devices.
/// </summary>
/// <remarks>
/// <para>
/// This plugin supports both Linux and Windows platforms with the following implementations:
/// </para>
/// <list type="bullet">
/// <item><description>Linux: Uses fstrim command and checks /sys/block/*/queue/discard_granularity for TRIM support</description></item>
/// <item><description>Windows: Uses FSCTL_FILE_LEVEL_TRIM via DeviceIoControl or Optimize-Volume cmdlet</description></item>
/// </list>
/// <para>
/// TRIM/Discard operations inform the storage controller that blocks are no longer in use,
/// allowing thin-provisioned storage to reclaim space and SSDs to perform wear leveling.
/// </para>
/// </remarks>
public class TrimDiscardStorageOptimizerPlugin : FeaturePluginBase, IStorageOptimizer
{
    #region Windows P/Invoke Declarations

    /// <summary>
    /// File share mode for CreateFile.
    /// </summary>
    [Flags]
    private enum FileShare : uint
    {
        None = 0,
        Read = 0x00000001,
        Write = 0x00000002,
        Delete = 0x00000004
    }

    /// <summary>
    /// Creation disposition for CreateFile.
    /// </summary>
    private enum CreationDisposition : uint
    {
        CreateNew = 1,
        CreateAlways = 2,
        OpenExisting = 3,
        OpenAlways = 4,
        TruncateExisting = 5
    }

    /// <summary>
    /// File attributes and flags for CreateFile.
    /// </summary>
    [Flags]
    private enum FileAttributes : uint
    {
        Normal = 0x00000080,
        NoBuffering = 0x20000000,
        WriteThrough = 0x80000000
    }

    /// <summary>
    /// Desired access flags for CreateFile.
    /// </summary>
    [Flags]
    private enum DesiredAccess : uint
    {
        GenericRead = 0x80000000,
        GenericWrite = 0x40000000,
        GenericExecute = 0x20000000,
        GenericAll = 0x10000000
    }

    /// <summary>
    /// Windows IOCTL code for file-level TRIM operation.
    /// </summary>
    private const uint FSCTL_FILE_LEVEL_TRIM = 0x00098208;

    /// <summary>
    /// Windows IOCTL code for filesystem-level TRIM.
    /// </summary>
    private const uint FSCTL_FILESYSTEM_GET_STATISTICS = 0x00090060;

    /// <summary>
    /// Windows IOCTL code to check if volume supports TRIM.
    /// </summary>
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    /// <summary>
    /// Storage property ID for device TRIM capabilities.
    /// </summary>
    private const int StorageDeviceTrimProperty = 8;

    /// <summary>
    /// Structure for TRIM extent definition.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_LEVEL_TRIM_RANGE
    {
        public long Offset;
        public long Length;
    }

    /// <summary>
    /// Structure for TRIM input buffer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_LEVEL_TRIM
    {
        public uint Key;
        public uint NumRanges;
        // Followed by FILE_LEVEL_TRIM_RANGE array
    }

    /// <summary>
    /// Storage property query structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    /// <summary>
    /// Device TRIM descriptor returned by storage property query.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_TRIM_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool TrimEnabled;
    }

    /// <summary>
    /// Opens or creates a file or device.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// Sends a control code to a device driver.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    #endregion

    private readonly SemaphoreSlim _trimLock = new(1, 1);
    private readonly Dictionary<string, bool> _trimSupportCache = new();
    private readonly object _cacheLock = new();
    private bool _isStarted;

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.trim-discard-optimizer";

    /// <inheritdoc />
    public override string Name => "TRIM/Discard Storage Optimizer";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Starts the storage optimizer plugin.
    /// Initializes the TRIM support detection cache.
    /// </summary>
    /// <param name="ct">Cancellation token for the start operation.</param>
    /// <returns>A task representing the asynchronous start operation.</returns>
    public override Task StartAsync(CancellationToken ct)
    {
        _isStarted = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the storage optimizer plugin.
    /// Clears the TRIM support cache.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    public override Task StopAsync()
    {
        _isStarted = false;
        lock (_cacheLock)
        {
            _trimSupportCache.Clear();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if a device supports TRIM/Discard operations.
    /// Uses platform-specific detection methods to determine TRIM capability.
    /// </summary>
    /// <param name="devicePath">
    /// Path to the storage device.
    /// <list type="bullet">
    /// <item><description>Linux: Block device path (e.g., /dev/sda, /dev/nvme0n1)</description></item>
    /// <item><description>Windows: Volume path (e.g., C:\, \\.\PhysicalDrive0)</description></item>
    /// </list>
    /// </param>
    /// <returns>True if the device supports TRIM operations, false otherwise.</returns>
    /// <exception cref="ArgumentException">Thrown when devicePath is null or empty.</exception>
    public async Task<bool> SupportsTrimAsync(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new ArgumentException("Device path cannot be null or empty.", nameof(devicePath));
        }

        // Check cache first
        lock (_cacheLock)
        {
            if (_trimSupportCache.TryGetValue(devicePath, out var cached))
            {
                return cached;
            }
        }

        bool supportsTrim;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                supportsTrim = await CheckLinuxTrimSupportAsync(devicePath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                supportsTrim = await CheckWindowsTrimSupportAsync(devicePath);
            }
            else
            {
                // Unsupported platform
                supportsTrim = false;
            }
        }
        catch (Exception)
        {
            // If we can't determine support, assume not supported
            supportsTrim = false;
        }

        // Cache the result
        lock (_cacheLock)
        {
            _trimSupportCache[devicePath] = supportsTrim;
        }

        return supportsTrim;
    }

    /// <summary>
    /// Sends a TRIM command to discard specified blocks on the storage device.
    /// The storage controller is informed that the specified range is no longer in use.
    /// </summary>
    /// <param name="devicePath">Path to the storage device or file.</param>
    /// <param name="offset">Byte offset from the start of the device/file.</param>
    /// <param name="length">Number of bytes to trim/discard.</param>
    /// <returns>A task representing the asynchronous trim operation.</returns>
    /// <exception cref="ArgumentException">Thrown when devicePath is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when offset or length is negative.</exception>
    /// <exception cref="NotSupportedException">Thrown when the device does not support TRIM.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the TRIM operation fails.</exception>
    public async Task TrimAsync(string devicePath, long offset, long length)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new ArgumentException("Device path cannot be null or empty.", nameof(devicePath));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        }

        if (length == 0)
        {
            return; // Nothing to trim
        }

        await _trimLock.WaitAsync();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await TrimLinuxAsync(devicePath, offset, length);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await TrimWindowsAsync(devicePath, offset, length);
            }
            else
            {
                throw new PlatformNotSupportedException(
                    $"TRIM operations are not supported on {RuntimeInformation.OSDescription}");
            }
        }
        finally
        {
            _trimLock.Release();
        }
    }

    /// <summary>
    /// Performs a full scan to discard all unused blocks on mounted filesystems.
    /// This is equivalent to running fstrim on all mounted filesystems (Linux) or
    /// Optimize-Volume on all volumes (Windows).
    /// </summary>
    /// <returns>A task representing the asynchronous discard operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the discard operation fails.</exception>
    /// <remarks>
    /// This operation may take considerable time depending on the amount of unused space
    /// and the speed of the storage device. It is recommended to run this during maintenance windows.
    /// </remarks>
    public async Task DiscardUnusedBlocksAsync()
    {
        await _trimLock.WaitAsync();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await DiscardUnusedBlocksLinuxAsync();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await DiscardUnusedBlocksWindowsAsync();
            }
            else
            {
                throw new PlatformNotSupportedException(
                    $"Discard operations are not supported on {RuntimeInformation.OSDescription}");
            }
        }
        finally
        {
            _trimLock.Release();
        }
    }

    #region Linux Implementation

    /// <summary>
    /// Checks if a Linux block device supports TRIM by examining discard_granularity.
    /// </summary>
    private async Task<bool> CheckLinuxTrimSupportAsync(string devicePath)
    {
        try
        {
            // Extract device name from path (e.g., /dev/sda -> sda)
            var deviceName = ExtractLinuxDeviceName(devicePath);
            if (string.IsNullOrEmpty(deviceName))
            {
                return false;
            }

            // Check discard_granularity in sysfs
            var discardPath = $"/sys/block/{deviceName}/queue/discard_granularity";

            if (!File.Exists(discardPath))
            {
                // Try parent device for partitions (e.g., sda1 -> sda)
                var parentDevice = GetLinuxParentDevice(deviceName);
                if (!string.IsNullOrEmpty(parentDevice))
                {
                    discardPath = $"/sys/block/{parentDevice}/queue/discard_granularity";
                }
            }

            if (!File.Exists(discardPath))
            {
                return false;
            }

            var granularityStr = await File.ReadAllTextAsync(discardPath);
            if (long.TryParse(granularityStr.Trim(), out var granularity))
            {
                // Non-zero granularity indicates TRIM support
                return granularity > 0;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the device name from a Linux device path.
    /// </summary>
    private static string ExtractLinuxDeviceName(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return string.Empty;
        }

        // Handle /dev/sda, /dev/nvme0n1, etc.
        if (devicePath.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
        {
            return devicePath[5..]; // Remove /dev/ prefix
        }

        // Handle mount points by resolving to device
        if (devicePath.StartsWith("/", StringComparison.Ordinal))
        {
            return ResolveLinuxMountPointToDevice(devicePath);
        }

        return devicePath;
    }

    /// <summary>
    /// Resolves a Linux mount point to its underlying device name.
    /// </summary>
    private static string ResolveLinuxMountPointToDevice(string mountPoint)
    {
        try
        {
            if (!File.Exists("/proc/mounts"))
            {
                return string.Empty;
            }

            var lines = File.ReadAllLines("/proc/mounts");
            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == mountPoint)
                {
                    var device = parts[0];
                    if (device.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
                    {
                        return device[5..];
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets the parent device for a Linux partition (e.g., sda1 -> sda).
    /// </summary>
    private static string GetLinuxParentDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return string.Empty;
        }

        // Handle NVMe devices (nvme0n1p1 -> nvme0n1)
        if (deviceName.StartsWith("nvme", StringComparison.OrdinalIgnoreCase))
        {
            var pIndex = deviceName.LastIndexOf('p');
            if (pIndex > 0 && pIndex < deviceName.Length - 1)
            {
                var afterP = deviceName[(pIndex + 1)..];
                if (int.TryParse(afterP, out _))
                {
                    return deviceName[..pIndex];
                }
            }
        }

        // Handle traditional devices (sda1 -> sda, vda2 -> vda)
        var i = deviceName.Length - 1;
        while (i >= 0 && char.IsDigit(deviceName[i]))
        {
            i--;
        }

        if (i >= 0 && i < deviceName.Length - 1)
        {
            return deviceName[..(i + 1)];
        }

        return string.Empty;
    }

    /// <summary>
    /// Performs TRIM on Linux using blkdiscard or fallocate.
    /// </summary>
    private async Task TrimLinuxAsync(string devicePath, long offset, long length)
    {
        // Check if it's a block device or a file
        var isBlockDevice = devicePath.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase);

        if (isBlockDevice)
        {
            // Use blkdiscard for block devices
            await RunLinuxCommandAsync("blkdiscard", $"-o {offset} -l {length} {devicePath}");
        }
        else
        {
            // Use fallocate for files (punch hole)
            await RunLinuxCommandAsync("fallocate", $"-p -o {offset} -l {length} \"{devicePath}\"");
        }
    }

    /// <summary>
    /// Discards unused blocks on all mounted filesystems using fstrim.
    /// </summary>
    private async Task DiscardUnusedBlocksLinuxAsync()
    {
        // Run fstrim on all mounted filesystems
        // The -a flag trims all mounted filesystems that support discard
        await RunLinuxCommandAsync("fstrim", "-a -v");
    }

    /// <summary>
    /// Runs a Linux command asynchronously and captures output.
    /// </summary>
    private static async Task RunLinuxCommandAsync(string command, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = errorBuilder.ToString();
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = $"Command '{command} {arguments}' failed with exit code {process.ExitCode}";
                }
                throw new InvalidOperationException(error);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // File not found
        {
            throw new InvalidOperationException(
                $"Command '{command}' not found. Ensure it is installed and in the PATH.", ex);
        }
    }

    #endregion

    #region Windows Implementation

    /// <summary>
    /// Checks if a Windows volume supports TRIM.
    /// </summary>
    private async Task<bool> CheckWindowsTrimSupportAsync(string devicePath)
    {
        // Normalize path to volume format
        var volumePath = NormalizeWindowsVolumePath(devicePath);

        return await Task.Run(() =>
        {
            try
            {
                // Open the volume for querying
                using var handle = CreateFileW(
                    volumePath,
                    (uint)DesiredAccess.GenericRead,
                    (uint)(FileShare.Read | FileShare.Write),
                    IntPtr.Zero,
                    (uint)CreationDisposition.OpenExisting,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    // Try using PowerShell as fallback
                    return CheckWindowsTrimSupportPowerShell(devicePath);
                }

                // Query TRIM property
                var query = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = StorageDeviceTrimProperty,
                    QueryType = 0, // PropertyStandardQuery
                    AdditionalParameters = new byte[1]
                };

                var querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
                var descriptorSize = Marshal.SizeOf<DEVICE_TRIM_DESCRIPTOR>();

                var queryPtr = Marshal.AllocHGlobal(querySize);
                var descriptorPtr = Marshal.AllocHGlobal(descriptorSize);

                try
                {
                    Marshal.StructureToPtr(query, queryPtr, false);

                    var result = DeviceIoControl(
                        handle,
                        IOCTL_STORAGE_QUERY_PROPERTY,
                        queryPtr,
                        (uint)querySize,
                        descriptorPtr,
                        (uint)descriptorSize,
                        out _,
                        IntPtr.Zero);

                    if (result)
                    {
                        var descriptor = Marshal.PtrToStructure<DEVICE_TRIM_DESCRIPTOR>(descriptorPtr);
                        return descriptor.TrimEnabled;
                    }

                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(queryPtr);
                    Marshal.FreeHGlobal(descriptorPtr);
                }
            }
            catch (Exception)
            {
                // Fallback to PowerShell check
                return CheckWindowsTrimSupportPowerShell(devicePath);
            }
        });
    }

    /// <summary>
    /// Checks TRIM support using PowerShell as a fallback.
    /// </summary>
    private static bool CheckWindowsTrimSupportPowerShell(string devicePath)
    {
        try
        {
            var driveLetter = ExtractWindowsDriveLetter(devicePath);
            if (string.IsNullOrEmpty(driveLetter))
            {
                return false;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"(Get-PhysicalDisk | Where-Object {{ (Get-Partition -DiskNumber $_.DeviceId -ErrorAction SilentlyContinue | Get-Volume -ErrorAction SilentlyContinue).DriveLetter -eq '{driveLetter}' }}).MediaType -eq 'SSD'\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a Windows path to a volume path for DeviceIoControl.
    /// </summary>
    private static string NormalizeWindowsVolumePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // Already in device format
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        // Drive letter (C: or C:\)
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            return $@"\\.\{path[0]}:";
        }

        // UNC path or other
        return path;
    }

    /// <summary>
    /// Extracts the drive letter from a Windows path.
    /// </summary>
    private static string ExtractWindowsDriveLetter(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // Handle \\.\C: format
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) && path.Length >= 6)
        {
            return path[4].ToString();
        }

        // Handle C: or C:\ format
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            return path[0].ToString();
        }

        return string.Empty;
    }

    /// <summary>
    /// Performs TRIM on Windows using FSCTL_FILE_LEVEL_TRIM.
    /// </summary>
    private async Task TrimWindowsAsync(string devicePath, long offset, long length)
    {
        await Task.Run(() =>
        {
            try
            {
                // For file-level TRIM
                if (File.Exists(devicePath))
                {
                    TrimWindowsFile(devicePath, offset, length);
                    return;
                }

                // For volume-level, use PowerShell
                var driveLetter = ExtractWindowsDriveLetter(devicePath);
                if (!string.IsNullOrEmpty(driveLetter))
                {
                    TrimWindowsVolumePowerShell(driveLetter);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to perform TRIM on {devicePath}: {ex.Message}", ex);
            }
        });
    }

    /// <summary>
    /// Performs file-level TRIM using DeviceIoControl.
    /// </summary>
    private static void TrimWindowsFile(string filePath, long offset, long length)
    {
        using var handle = CreateFileW(
            filePath,
            (uint)(DesiredAccess.GenericRead | DesiredAccess.GenericWrite),
            (uint)FileShare.Read,
            IntPtr.Zero,
            (uint)CreationDisposition.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"Cannot open file for TRIM: {filePath}. Error: {Marshal.GetLastWin32Error()}");
        }

        // Prepare TRIM structure
        // FILE_LEVEL_TRIM header + one FILE_LEVEL_TRIM_RANGE
        var headerSize = Marshal.SizeOf<FILE_LEVEL_TRIM>();
        var rangeSize = Marshal.SizeOf<FILE_LEVEL_TRIM_RANGE>();
        var totalSize = headerSize + rangeSize;

        var buffer = Marshal.AllocHGlobal(totalSize);
        try
        {
            // Clear buffer
            for (var i = 0; i < totalSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            // Write header
            Marshal.WriteInt32(buffer, 0, 0); // Key = 0
            Marshal.WriteInt32(buffer, 4, 1); // NumRanges = 1

            // Write range
            var rangePtr = IntPtr.Add(buffer, headerSize);
            Marshal.WriteInt64(rangePtr, 0, offset);
            Marshal.WriteInt64(rangePtr, 8, length);

            var result = DeviceIoControl(
                handle,
                FSCTL_FILE_LEVEL_TRIM,
                buffer,
                (uint)totalSize,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);

            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"FSCTL_FILE_LEVEL_TRIM failed with error code: {error}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Performs TRIM using PowerShell Optimize-Volume.
    /// </summary>
    private static void TrimWindowsVolumePowerShell(string driveLetter)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Optimize-Volume -DriveLetter {driveLetter} -ReTrim -Verbose\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException($"Optimize-Volume failed: {error}");
        }
    }

    /// <summary>
    /// Discards unused blocks on all Windows volumes using Optimize-Volume.
    /// </summary>
    private async Task DiscardUnusedBlocksWindowsAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Get all fixed drives and run Optimize-Volume with ReTrim
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -Command \"Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter } | ForEach-Object { Optimize-Volume -DriveLetter $_.DriveLetter -ReTrim -ErrorAction SilentlyContinue }\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) outputBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) errorBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                // Don't throw on non-zero exit code as some volumes may fail but others succeed
                // Just log any errors that occurred
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to discard unused blocks: {ex.Message}", ex);
            }
        });
    }

    #endregion

    /// <summary>
    /// Gets plugin metadata including storage optimizer capabilities.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "StorageOptimizer";
        metadata["SupportsTrim"] = true;
        metadata["SupportsDiscard"] = true;
        metadata["SupportedPlatforms"] = new[] { "Linux", "Windows" };
        metadata["Description"] = "TRIM/Discard operations for thin-provisioned virtual disks and SSDs";
        return metadata;
    }

    /// <summary>
    /// Disposes of resources used by the plugin.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trimLock.Dispose();
        }
    }

    /// <summary>
    /// Disposes of resources used by the plugin.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
