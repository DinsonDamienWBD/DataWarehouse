// <copyright file="LinuxSpecific.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Linux-specific features for the FUSE filesystem.
/// Provides inotify integration, SELinux label support, cgroups awareness, and io_uring support.
/// </summary>
public sealed class LinuxSpecific : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private readonly ConcurrentDictionary<string, int> _inotifyWatches = new();
    private readonly ConcurrentDictionary<string, string> _selinuxLabels = new();
    private readonly object _inotifyLock = new();
    private int _inotifyFd = -1;
    private Thread? _inotifyThread;
    private CancellationTokenSource? _inotifyCts;
    private bool _disposed;

    /// <summary>
    /// Event raised when a file system change is detected.
    /// </summary>
    public event EventHandler<InotifyEventArgs>? FileChanged;

    /// <summary>
    /// Gets a value indicating whether this platform is Linux.
    /// </summary>
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Gets a value indicating whether inotify is available.
    /// </summary>
    public bool IsInotifyAvailable { get; private set; }

    /// <summary>
    /// Gets a value indicating whether SELinux is enabled.
    /// </summary>
    public bool IsSeLinuxEnabled { get; private set; }

    /// <summary>
    /// Gets a value indicating whether io_uring is available.
    /// </summary>
    public bool IsIoUringAvailable { get; private set; }

    /// <summary>
    /// Gets a value indicating whether cgroups are available.
    /// </summary>
    public bool IsCgroupsAvailable { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LinuxSpecific"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public LinuxSpecific(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;

        if (!IsLinux)
        {
            return;
        }

        DetectCapabilities();
    }

    #region Inotify Integration

    /// <summary>
    /// Initializes the inotify subsystem.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    public bool InitializeInotify()
    {
        if (!IsLinux || !_config.EnableNotifications || !IsInotifyAvailable)
            return false;

        lock (_inotifyLock)
        {
            if (_inotifyFd >= 0)
                return true; // Already initialized

            try
            {
                _inotifyFd = NativeInotify.inotify_init1(NativeInotify.IN_NONBLOCK | NativeInotify.IN_CLOEXEC);
                if (_inotifyFd < 0)
                {
                    _kernelContext?.LogError($"Failed to initialize inotify: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // Start the event processing thread
                _inotifyCts = new CancellationTokenSource();
                _inotifyThread = new Thread(ProcessInotifyEvents)
                {
                    Name = "FuseInotify",
                    IsBackground = true
                };
                _inotifyThread.Start();

                _kernelContext?.LogInfo("Inotify initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                _kernelContext?.LogError("Failed to initialize inotify", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Adds an inotify watch for a path.
    /// </summary>
    /// <param name="path">The path to watch.</param>
    /// <param name="mask">The events to watch for.</param>
    /// <returns>True if the watch was added.</returns>
    public bool AddWatch(string path, InotifyMask mask = InotifyMask.AllEvents)
    {
        if (_inotifyFd < 0)
            return false;

        lock (_inotifyLock)
        {
            if (_inotifyWatches.ContainsKey(path))
                return true; // Already watching

            var wd = NativeInotify.inotify_add_watch(_inotifyFd, path, (uint)mask);
            if (wd < 0)
            {
                _kernelContext?.LogWarning($"Failed to add inotify watch for {path}: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _inotifyWatches[path] = wd;
            return true;
        }
    }

    /// <summary>
    /// Removes an inotify watch for a path.
    /// </summary>
    /// <param name="path">The path to stop watching.</param>
    /// <returns>True if the watch was removed.</returns>
    public bool RemoveWatch(string path)
    {
        if (_inotifyFd < 0)
            return false;

        lock (_inotifyLock)
        {
            if (!_inotifyWatches.TryRemove(path, out var wd))
                return false;

            NativeInotify.inotify_rm_watch(_inotifyFd, wd);
            return true;
        }
    }

    private void ProcessInotifyEvents()
    {
        var buffer = new byte[4096];

        while (!_inotifyCts!.IsCancellationRequested && _inotifyFd >= 0)
        {
            try
            {
                var bytesRead = NativeInotify.read(_inotifyFd, buffer, buffer.Length);

                if (bytesRead <= 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var offset = 0;
                while (offset < bytesRead)
                {
                    var wd = BitConverter.ToInt32(buffer, offset);
                    var mask = (InotifyMask)BitConverter.ToUInt32(buffer, offset + 4);
                    var cookie = BitConverter.ToUInt32(buffer, offset + 8);
                    var len = BitConverter.ToUInt32(buffer, offset + 12);

                    string? name = null;
                    if (len > 0)
                    {
                        name = Encoding.UTF8.GetString(buffer, offset + 16, (int)len).TrimEnd('\0');
                    }

                    // Find the path for this watch descriptor
                    var path = _inotifyWatches.FirstOrDefault(kvp => kvp.Value == wd).Key;

                    if (path != null)
                    {
                        OnFileChanged(new InotifyEventArgs
                        {
                            Path = path,
                            Name = name,
                            Mask = mask,
                            Cookie = cookie
                        });
                    }

                    offset += 16 + (int)len;
                }
            }
            catch (Exception ex)
            {
                if (!_inotifyCts.IsCancellationRequested)
                {
                    _kernelContext?.LogError("Error processing inotify events", ex);
                }
            }
        }
    }

    private void OnFileChanged(InotifyEventArgs e)
    {
        FileChanged?.Invoke(this, e);
    }

    #endregion

    #region SELinux Support

    /// <summary>
    /// Gets the SELinux context for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The SELinux context, or null if not available.</returns>
    public string? GetSeLinuxContext(string path)
    {
        if (!IsLinux || !_config.EnableSeLinux || !IsSeLinuxEnabled)
            return null;

        // Check cache first
        if (_selinuxLabels.TryGetValue(path, out var cached))
            return cached;

        try
        {
            // Try to read the security.selinux xattr
            var buffer = new byte[256];
            var result = NativeXattr.getxattr(path, "security.selinux", buffer, buffer.Length);

            if (result > 0)
            {
                var context = Encoding.UTF8.GetString(buffer, 0, (int)result).TrimEnd('\0');
                _selinuxLabels[path] = context;
                return context;
            }
        }
        catch
        {
            // Ignore errors - SELinux may not be available for this file
        }

        return null;
    }

    /// <summary>
    /// Sets the SELinux context for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="context">The SELinux context.</param>
    /// <returns>True if the context was set.</returns>
    public bool SetSeLinuxContext(string path, string context)
    {
        if (!IsLinux || !_config.EnableSeLinux || !IsSeLinuxEnabled)
            return false;

        try
        {
            var contextBytes = Encoding.UTF8.GetBytes(context + "\0");
            var result = NativeXattr.setxattr(path, "security.selinux", contextBytes, contextBytes.Length, 0);

            if (result == 0)
            {
                _selinuxLabels[path] = context;
                return true;
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to set SELinux context for {path}: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Gets the default SELinux context for a file type.
    /// </summary>
    /// <param name="isDirectory">True if the context is for a directory.</param>
    /// <returns>The default context.</returns>
    public string GetDefaultSeLinuxContext(bool isDirectory)
    {
        // Return a reasonable default context
        return isDirectory
            ? "system_u:object_r:fusefs_t:s0"
            : "system_u:object_r:fusefs_t:s0";
    }

    #endregion

    #region Cgroups Support

    /// <summary>
    /// Gets the cgroup path for the current process.
    /// </summary>
    /// <returns>The cgroup path, or null if not in a cgroup.</returns>
    public string? GetCurrentCgroup()
    {
        if (!IsLinux || !_config.EnableCgroups || !IsCgroupsAvailable)
            return null;

        try
        {
            // Read /proc/self/cgroup
            var content = File.ReadAllText("/proc/self/cgroup");
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split(':');
                if (parts.Length >= 3)
                {
                    // Return the cgroup v2 path or first v1 path
                    if (string.IsNullOrEmpty(parts[1]))
                    {
                        // cgroup v2
                        return parts[2];
                    }
                }
            }

            return lines.Length > 0 ? lines[0].Split(':').LastOrDefault() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets I/O limits from the cgroup.
    /// </summary>
    /// <returns>The I/O limits, or null if not available.</returns>
    public CgroupIoLimits? GetCgroupIoLimits()
    {
        if (!IsLinux || !_config.EnableCgroups || !IsCgroupsAvailable)
            return null;

        var cgroupPath = GetCurrentCgroup();
        if (string.IsNullOrEmpty(cgroupPath))
            return null;

        try
        {
            var limits = new CgroupIoLimits();

            // Try cgroup v2 io.max
            var ioMaxPath = $"/sys/fs/cgroup{cgroupPath}/io.max";
            if (File.Exists(ioMaxPath))
            {
                var content = File.ReadAllText(ioMaxPath);
                ParseCgroupV2IoMax(content, limits);
            }

            // Try cgroup v1 blkio limits
            var blkioPath = $"/sys/fs/cgroup/blkio{cgroupPath}";
            if (Directory.Exists(blkioPath))
            {
                var readBps = Path.Combine(blkioPath, "blkio.throttle.read_bps_device");
                if (File.Exists(readBps))
                {
                    var content = File.ReadAllText(readBps);
                    limits.ReadBytesPerSecond = ParseBlkioLimit(content);
                }

                var writeBps = Path.Combine(blkioPath, "blkio.throttle.write_bps_device");
                if (File.Exists(writeBps))
                {
                    var content = File.ReadAllText(writeBps);
                    limits.WriteBytesPerSecond = ParseBlkioLimit(content);
                }
            }

            return limits;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseCgroupV2IoMax(string content, CgroupIoLimits limits)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts.Skip(1))
            {
                var kvp = part.Split('=');
                if (kvp.Length == 2)
                {
                    var value = kvp[1] == "max" ? long.MaxValue : long.Parse(kvp[1]);
                    switch (kvp[0])
                    {
                        case "rbps":
                            limits.ReadBytesPerSecond = value;
                            break;
                        case "wbps":
                            limits.WriteBytesPerSecond = value;
                            break;
                        case "riops":
                            limits.ReadIopsPerSecond = value;
                            break;
                        case "wiops":
                            limits.WriteIopsPerSecond = value;
                            break;
                    }
                }
            }
        }
    }

    private static long ParseBlkioLimit(string content)
    {
        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value : 0;
    }

    #endregion

    #region io_uring Support

    /// <summary>
    /// Gets a value indicating whether io_uring should be used.
    /// </summary>
    /// <returns>True if io_uring is available and enabled.</returns>
    public bool ShouldUseIoUring()
    {
        return IsLinux && _config.EnableIoUring && IsIoUringAvailable;
    }

    /// <summary>
    /// Gets the kernel version for io_uring compatibility check.
    /// </summary>
    /// <returns>The kernel version tuple, or null if unavailable.</returns>
    public (int Major, int Minor, int Patch)? GetKernelVersion()
    {
        if (!IsLinux)
            return null;

        try
        {
            var version = File.ReadAllText("/proc/version");
            var match = System.Text.RegularExpressions.Regex.Match(version, @"Linux version (\d+)\.(\d+)\.(\d+)");
            if (match.Success)
            {
                return (
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value)
                );
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    #endregion

    #region Capability Detection

    private void DetectCapabilities()
    {
        // Detect inotify
        IsInotifyAvailable = File.Exists("/proc/sys/fs/inotify/max_user_watches");

        // Detect SELinux
        IsSeLinuxEnabled = File.Exists("/sys/fs/selinux/enforce") ||
                          Directory.Exists("/selinux");

        // Detect io_uring (requires kernel 5.1+)
        var kernelVersion = GetKernelVersion();
        if (kernelVersion.HasValue)
        {
            var (major, minor, _) = kernelVersion.Value;
            IsIoUringAvailable = major > 5 || (major == 5 && minor >= 1);
        }

        // Detect cgroups
        IsCgroupsAvailable = Directory.Exists("/sys/fs/cgroup");
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the Linux-specific resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Stop inotify thread
        _inotifyCts?.Cancel();
        _inotifyThread?.Join(TimeSpan.FromSeconds(2));

        // Close inotify file descriptor
        lock (_inotifyLock)
        {
            if (_inotifyFd >= 0)
            {
                NativeInotify.close(_inotifyFd);
                _inotifyFd = -1;
            }
        }

        _inotifyCts?.Dispose();
        _inotifyWatches.Clear();
        _selinuxLabels.Clear();
    }

    #endregion

    #region Native Interop

    private static class NativeInotify
    {
        public const uint IN_NONBLOCK = 0x800;
        public const uint IN_CLOEXEC = 0x80000;

        [DllImport("libc", SetLastError = true)]
        public static extern int inotify_init1(uint flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int inotify_add_watch(int fd, [MarshalAs(UnmanagedType.LPStr)] string pathname, uint mask);

        [DllImport("libc", SetLastError = true)]
        public static extern int inotify_rm_watch(int fd, int wd);

        [DllImport("libc", SetLastError = true)]
        public static extern nint read(int fd, byte[] buf, int count);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);
    }

    private static class NativeXattr
    {
        [DllImport("libc", SetLastError = true)]
        public static extern nint getxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            byte[] value,
            int size);

        [DllImport("libc", SetLastError = true)]
        public static extern int setxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            byte[] value,
            int size,
            int flags);
    }

    #endregion
}

/// <summary>
/// Event arguments for inotify events.
/// </summary>
public sealed class InotifyEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the watched path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the affected file within the watched directory.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the event mask.
    /// </summary>
    public InotifyMask Mask { get; set; }

    /// <summary>
    /// Gets or sets the cookie for related events (e.g., rename).
    /// </summary>
    public uint Cookie { get; set; }

    /// <summary>
    /// Gets the full path of the affected file.
    /// </summary>
    public string FullPath => string.IsNullOrEmpty(Name) ? Path : System.IO.Path.Combine(Path, Name);
}

/// <summary>
/// Inotify event mask flags.
/// </summary>
[Flags]
public enum InotifyMask : uint
{
    /// <summary>
    /// File was accessed.
    /// </summary>
    Access = 0x00000001,

    /// <summary>
    /// File was modified.
    /// </summary>
    Modify = 0x00000002,

    /// <summary>
    /// Metadata changed.
    /// </summary>
    Attrib = 0x00000004,

    /// <summary>
    /// File opened for writing was closed.
    /// </summary>
    CloseWrite = 0x00000008,

    /// <summary>
    /// File not opened for writing was closed.
    /// </summary>
    CloseNoWrite = 0x00000010,

    /// <summary>
    /// File was opened.
    /// </summary>
    Open = 0x00000020,

    /// <summary>
    /// File was moved from watched directory.
    /// </summary>
    MovedFrom = 0x00000040,

    /// <summary>
    /// File was moved to watched directory.
    /// </summary>
    MovedTo = 0x00000080,

    /// <summary>
    /// File/directory created in watched directory.
    /// </summary>
    Create = 0x00000100,

    /// <summary>
    /// File/directory deleted from watched directory.
    /// </summary>
    Delete = 0x00000200,

    /// <summary>
    /// Watched file/directory was deleted.
    /// </summary>
    DeleteSelf = 0x00000400,

    /// <summary>
    /// Watched file/directory was moved.
    /// </summary>
    MoveSelf = 0x00000800,

    /// <summary>
    /// File was closed (CloseWrite | CloseNoWrite).
    /// </summary>
    Close = CloseWrite | CloseNoWrite,

    /// <summary>
    /// File was moved (MovedFrom | MovedTo).
    /// </summary>
    Move = MovedFrom | MovedTo,

    /// <summary>
    /// All events.
    /// </summary>
    AllEvents = Access | Modify | Attrib | Close | Open | Move | Create | Delete | DeleteSelf | MoveSelf
}

/// <summary>
/// Cgroup I/O limits.
/// </summary>
public sealed class CgroupIoLimits
{
    /// <summary>
    /// Gets or sets the read bytes per second limit.
    /// </summary>
    public long ReadBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the write bytes per second limit.
    /// </summary>
    public long WriteBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the read IOPS limit.
    /// </summary>
    public long ReadIopsPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the write IOPS limit.
    /// </summary>
    public long WriteIopsPerSecond { get; set; }
}
