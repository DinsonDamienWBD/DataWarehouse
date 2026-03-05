using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO;

/// <summary>
/// Platform-specific native methods for raw partition geometry detection.
/// Provides sector size and total device size via OS-specific ioctls.
/// </summary>
/// <remarks>
/// <para>
/// Windows: Uses <c>DeviceIoControl</c> with <c>IOCTL_DISK_GET_DRIVE_GEOMETRY_EX</c> to query
/// the disk geometry including sector size and total disk size.
/// </para>
/// <para>
/// Linux: Uses <c>ioctl</c> with <c>BLKSSZGET</c> (sector size) and <c>BLKGETSIZE64</c> (total bytes).
/// Device paths are typically <c>/dev/nvme0n1</c>, <c>/dev/sda</c>, etc.
/// </para>
/// <para>
/// macOS: Uses <c>ioctl</c> with <c>DKIOCGETBLOCKSIZE</c> and <c>DKIOCGETBLOCKCOUNT</c>.
/// Device paths are typically <c>/dev/rdiskN</c> (character device for raw access).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-82: Cross-platform raw partition geometry detection")]
internal static partial class RawPartitionNativeMethods
{
    // =====================================================================
    // Windows: DeviceIoControl for disk geometry
    // =====================================================================

    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    /// <summary>
    /// DISK_GEOMETRY_EX structure returned by DeviceIoControl on Windows.
    /// Contains physical disk geometry including sector size and total disk size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DiskGeometryEx
    {
        public long Cylinders;
        public int MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
        public long DiskSize;
    }

#if NET7_0_OR_GREATER
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint Win32CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Win32DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        out DiskGeometryEx lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Win32CloseHandle(nint hObject);
#else
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint Win32CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Win32DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        out DiskGeometryEx lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Win32CloseHandle(nint hObject);
#endif

    // =====================================================================
    // Linux: ioctl for block device geometry
    // =====================================================================

    /// <summary>BLKSSZGET: get block device sector size.</summary>
    private const uint LINUX_BLKSSZGET = 0x1268;

    /// <summary>BLKGETSIZE64: get block device size in bytes.</summary>
    private const uint LINUX_BLKGETSIZE64 = 0x80081272;

    /// <summary>O_RDWR: open for read/write.</summary>
    private const int LINUX_O_RDWR = 0x02;

    /// <summary>O_DIRECT: bypass page cache.</summary>
    private const int LINUX_O_DIRECT = 0x4000;

#if NET7_0_OR_GREATER
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxOpen(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int LinuxClose(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int LinuxIoctlInt(int fd, uint request, ref int value);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int LinuxIoctlLong(int fd, uint request, ref long value);
#else
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LinuxClose(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int LinuxIoctlInt(int fd, uint request, ref int value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int LinuxIoctlLong(int fd, uint request, ref long value);
#endif

    // =====================================================================
    // macOS: ioctl for disk device geometry
    // =====================================================================

    /// <summary>DKIOCGETBLOCKSIZE: get disk block (sector) size.</summary>
    private const uint MACOS_DKIOCGETBLOCKSIZE = 0x40046418;

    /// <summary>DKIOCGETBLOCKCOUNT: get disk block count.</summary>
    private const uint MACOS_DKIOCGETBLOCKCOUNT = 0x40086419;

    /// <summary>F_NOCACHE: disable file system caching.</summary>
    private const int MACOS_F_NOCACHE = 48;

    /// <summary>F_SETFL: set file status flags (fcntl).</summary>
    private const int MACOS_F_SETFL = 4;

    /// <summary>O_RDWR: open for read/write.</summary>
    private const int MACOS_O_RDWR = 0x02;

#if NET7_0_OR_GREATER
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacOsOpen(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int MacOsClose(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int MacOsIoctlUint(int fd, uint request, ref uint value);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int MacOsIoctlUlong(int fd, uint request, ref ulong value);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int MacOsFcntl(int fd, int cmd, int arg);
#else
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOsOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int MacOsClose(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int MacOsIoctlUint(int fd, uint request, ref uint value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int MacOsIoctlUlong(int fd, uint request, ref ulong value);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int MacOsFcntl(int fd, int cmd, int arg);
#endif

    // =====================================================================
    // Public API
    // =====================================================================

    /// <summary>
    /// Detects the physical sector size and total size of a raw partition or block device.
    /// Platform-dispatched: calls Windows DeviceIoControl, Linux ioctl, or macOS ioctl
    /// depending on the current OS.
    /// </summary>
    /// <param name="devicePath">
    /// The raw device path. Examples:
    /// <list type="bullet">
    /// <item><description>Windows: <c>\\.\PhysicalDrive0</c>, <c>\\.\HarddiskVolume1</c></description></item>
    /// <item><description>Linux: <c>/dev/nvme0n1</c>, <c>/dev/sda</c>, <c>/dev/nvme0n1p1</c></description></item>
    /// <item><description>macOS: <c>/dev/rdisk0</c>, <c>/dev/rdisk2</c></description></item>
    /// </list>
    /// </param>
    /// <returns>
    /// A tuple of (sectorSize, totalBytes) where sectorSize is the physical sector size
    /// in bytes and totalBytes is the total device/partition size in bytes.
    /// </returns>
    /// <exception cref="PlatformNotSupportedException">Thrown on unsupported operating systems.</exception>
    /// <exception cref="InvalidOperationException">Thrown when device geometry query fails.</exception>
    public static (int sectorSize, long totalBytes) GetPartitionGeometry(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetPartitionGeometryWindows(devicePath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetPartitionGeometryLinux(devicePath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return GetPartitionGeometryMacOs(devicePath);
        }

        throw new PlatformNotSupportedException(
            $"Raw partition geometry detection is not supported on {RuntimeInformation.OSDescription}. " +
            "Supported platforms: Windows, Linux, macOS.");
    }

    /// <summary>
    /// Opens a raw device handle for direct I/O on the current platform.
    /// Returns a platform-specific file descriptor or handle.
    /// </summary>
    /// <param name="devicePath">The raw device path.</param>
    /// <returns>The native handle (Windows HANDLE or Unix file descriptor cast to nint).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the device cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the process lacks sufficient privileges for raw device access.
    /// </exception>
    public static nint OpenDevice(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OpenDeviceWindows(devicePath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return OpenDeviceLinux(devicePath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return OpenDeviceMacOs(devicePath);
        }

        throw new PlatformNotSupportedException(
            $"Raw device access is not supported on {RuntimeInformation.OSDescription}.");
    }

    /// <summary>
    /// Closes a native device handle opened by <see cref="OpenDevice"/>.
    /// </summary>
    public static void CloseDevice(nint handle)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Win32CloseHandle(handle);
        }
        else
        {
            // Unix: handle is an fd cast to nint
            int fd = (int)handle;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LinuxClose(fd);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                MacOsClose(fd);
            }
        }
    }

    // =====================================================================
    // Windows implementation
    // =====================================================================

    private static (int sectorSize, long totalBytes) GetPartitionGeometryWindows(string devicePath)
    {
        nint handle = Win32CreateFile(
            devicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            0,
            nint.Zero);

        if (handle == -1 || handle == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 5) // ERROR_ACCESS_DENIED
            {
                throw new UnauthorizedAccessException(
                    $"Cannot query geometry for '{devicePath}': access denied. " +
                    "Raw partition access requires Administrator privileges on Windows.");
            }
            throw new InvalidOperationException(
                $"Failed to open device '{devicePath}' for geometry query (Win32 error {error}).");
        }

        try
        {
            bool success = Win32DeviceIoControl(
                handle,
                IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                nint.Zero,
                0,
                out DiskGeometryEx geometry,
                (uint)Marshal.SizeOf<DiskGeometryEx>(),
                out _,
                nint.Zero);

            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"DeviceIoControl(IOCTL_DISK_GET_DRIVE_GEOMETRY_EX) failed for '{devicePath}' " +
                    $"(Win32 error {error}).");
            }

            return ((int)geometry.BytesPerSector, geometry.DiskSize);
        }
        finally
        {
            Win32CloseHandle(handle);
        }
    }

    private static nint OpenDeviceWindows(string devicePath)
    {
        nint handle = Win32CreateFile(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            0, // Exclusive access
            nint.Zero,
            OPEN_EXISTING,
            FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
            nint.Zero);

        if (handle == -1 || handle == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 5) // ERROR_ACCESS_DENIED
            {
                throw new UnauthorizedAccessException(
                    $"Raw partition access requires elevated privileges " +
                    "(root on Linux/macOS, Administrator on Windows).");
            }
            throw new InvalidOperationException(
                $"Failed to open raw device '{devicePath}' (Win32 error {error}).");
        }

        return handle;
    }

    // =====================================================================
    // Linux implementation
    // =====================================================================

    private static (int sectorSize, long totalBytes) GetPartitionGeometryLinux(string devicePath)
    {
        int fd = LinuxOpen(devicePath, LINUX_O_RDWR);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == 13) // EACCES
            {
                throw new UnauthorizedAccessException(
                    $"Cannot query geometry for '{devicePath}': permission denied. " +
                    "Raw partition access requires root privileges on Linux.");
            }
            throw new InvalidOperationException(
                $"Failed to open device '{devicePath}' for geometry query (errno {errno}).");
        }

        try
        {
            int sectorSize = 0;
            if (LinuxIoctlInt(fd, LINUX_BLKSSZGET, ref sectorSize) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"ioctl(BLKSSZGET) failed for '{devicePath}' (errno {errno}).");
            }

            long totalBytes = 0;
            if (LinuxIoctlLong(fd, LINUX_BLKGETSIZE64, ref totalBytes) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"ioctl(BLKGETSIZE64) failed for '{devicePath}' (errno {errno}).");
            }

            return (sectorSize, totalBytes);
        }
        finally
        {
            LinuxClose(fd);
        }
    }

    private static nint OpenDeviceLinux(string devicePath)
    {
        int fd = LinuxOpen(devicePath, LINUX_O_RDWR | LINUX_O_DIRECT);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == 13) // EACCES
            {
                throw new UnauthorizedAccessException(
                    "Raw partition access requires elevated privileges " +
                    "(root on Linux/macOS, Administrator on Windows).");
            }
            throw new InvalidOperationException(
                $"Failed to open raw device '{devicePath}' (errno {errno}).");
        }

        return (nint)fd;
    }

    // =====================================================================
    // macOS implementation
    // =====================================================================

    private static (int sectorSize, long totalBytes) GetPartitionGeometryMacOs(string devicePath)
    {
        int fd = MacOsOpen(devicePath, MACOS_O_RDWR);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == 13) // EACCES
            {
                throw new UnauthorizedAccessException(
                    $"Cannot query geometry for '{devicePath}': permission denied. " +
                    "Raw partition access requires root privileges on macOS.");
            }
            throw new InvalidOperationException(
                $"Failed to open device '{devicePath}' for geometry query (errno {errno}).");
        }

        try
        {
            uint blockSize = 0;
            if (MacOsIoctlUint(fd, MACOS_DKIOCGETBLOCKSIZE, ref blockSize) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"ioctl(DKIOCGETBLOCKSIZE) failed for '{devicePath}' (errno {errno}).");
            }

            ulong blockCount = 0;
            if (MacOsIoctlUlong(fd, MACOS_DKIOCGETBLOCKCOUNT, ref blockCount) < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"ioctl(DKIOCGETBLOCKCOUNT) failed for '{devicePath}' (errno {errno}).");
            }

            long totalBytes = (long)(blockSize * blockCount);
            return ((int)blockSize, totalBytes);
        }
        finally
        {
            MacOsClose(fd);
        }
    }

    private static nint OpenDeviceMacOs(string devicePath)
    {
        int fd = MacOsOpen(devicePath, MACOS_O_RDWR);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno == 13) // EACCES
            {
                throw new UnauthorizedAccessException(
                    "Raw partition access requires elevated privileges " +
                    "(root on Linux/macOS, Administrator on Windows).");
            }
            throw new InvalidOperationException(
                $"Failed to open raw device '{devicePath}' (errno {errno}).");
        }

        // Disable file system caching on macOS via F_NOCACHE
        MacOsFcntl(fd, MACOS_F_NOCACHE, 1);

        return (nint)fd;
    }
}
