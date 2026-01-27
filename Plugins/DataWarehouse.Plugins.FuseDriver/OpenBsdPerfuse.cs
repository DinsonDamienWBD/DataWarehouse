// <copyright file="OpenBsdPerfuse.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Provides OpenBSD perfuse (PUFFS-based FUSE) support for the FUSE driver.
/// Implements perfuse-specific P/Invoke, pledge/unveil support, and OpenBSD compatibility.
/// </summary>
/// <remarks>
/// OpenBSD uses perfuse (PUFFS FUSE) for userspace filesystems:
/// - perfuse is a libfuse-compatible layer on top of PUFFS
/// - Supports pledge and unveil for security sandboxing
/// - Requires different mount/unmount procedures
/// </remarks>
public sealed class OpenBsdPerfuse : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private bool _isPledged;
    private bool _isUnveiled;
    private readonly List<string> _unveiledPaths = new();
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether this is OpenBSD.
    /// </summary>
    public static bool IsOpenBsd
    {
        get
        {
            try
            {
                var buffer = new byte[256];
                if (NativePerfuse.uname(buffer) == 0)
                {
                    var sysname = Encoding.UTF8.GetString(buffer).Split('\0')[0];
                    return sysname.Equals("OpenBSD", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Ignore detection errors
            }

            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether perfuse is available.
    /// </summary>
    public static bool IsPerfuseAvailable
    {
        get
        {
            if (!IsOpenBsd)
                return false;

            // Check for perfuse library
            try
            {
                var handle = NativePerfuse.dlopen("libperfuse.so", NativePerfuse.RTLD_NOW);
                if (handle != IntPtr.Zero)
                {
                    NativePerfuse.dlclose(handle);
                    return true;
                }
            }
            catch
            {
                // Ignore
            }

            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the process has been pledged.
    /// </summary>
    public bool IsPledged => _isPledged;

    /// <summary>
    /// Gets a value indicating whether unveil has been used.
    /// </summary>
    public bool IsUnveiled => _isUnveiled;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenBsdPerfuse"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public OpenBsdPerfuse(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;
    }

    #region Pledge Support

    /// <summary>
    /// Applies pledge restrictions to the process.
    /// </summary>
    /// <param name="promises">The pledge promises (e.g., "stdio rpath wpath fuse").</param>
    /// <param name="execpromises">Optional exec promises for child processes.</param>
    /// <returns>True if pledge was successful.</returns>
    /// <remarks>
    /// Common promises for FUSE:
    /// - stdio: Basic I/O operations
    /// - rpath: Read filesystem paths
    /// - wpath: Write filesystem paths
    /// - cpath: Create/delete filesystem paths
    /// - fattr: Change file attributes
    /// - fuse: FUSE filesystem operations
    /// - unix: Unix domain sockets (for communication)
    /// </remarks>
    public bool Pledge(string promises, string? execpromises = null)
    {
        if (!IsOpenBsd)
        {
            _kernelContext?.LogWarning("pledge is only available on OpenBSD");
            return false;
        }

        try
        {
            var result = NativePerfuse.pledge(promises, execpromises);
            if (result != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                _kernelContext?.LogError($"pledge failed with errno {errno}");
                return false;
            }

            _isPledged = true;
            _kernelContext?.LogInfo($"pledge applied: {promises}");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"pledge exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the recommended pledge promises for FUSE operation.
    /// </summary>
    /// <returns>The recommended pledge string.</returns>
    public static string GetFusePromises()
    {
        // Core promises needed for FUSE filesystem
        return "stdio rpath wpath cpath fattr fuse unix";
    }

    /// <summary>
    /// Gets the minimal pledge promises for read-only FUSE operation.
    /// </summary>
    /// <returns>The minimal pledge string.</returns>
    public static string GetReadOnlyPromises()
    {
        return "stdio rpath fuse unix";
    }

    #endregion

    #region Unveil Support

    /// <summary>
    /// Unveils a path with specified permissions.
    /// </summary>
    /// <param name="path">The path to unveil.</param>
    /// <param name="permissions">The permissions (r=read, w=write, x=execute, c=create).</param>
    /// <returns>True if unveil was successful.</returns>
    /// <remarks>
    /// Unveil restricts filesystem access to explicitly unveiled paths.
    /// After calling unveil(null, null), no more paths can be unveiled.
    /// </remarks>
    public bool Unveil(string path, string permissions)
    {
        if (!IsOpenBsd)
        {
            _kernelContext?.LogWarning("unveil is only available on OpenBSD");
            return false;
        }

        try
        {
            var result = NativePerfuse.unveil(path, permissions);
            if (result != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                _kernelContext?.LogError($"unveil failed for {path} with errno {errno}");
                return false;
            }

            _unveiledPaths.Add($"{path}:{permissions}");
            _isUnveiled = true;
            _kernelContext?.LogDebug($"unveiled: {path} ({permissions})");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"unveil exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finalizes unveil, preventing further path additions.
    /// </summary>
    /// <returns>True if finalization was successful.</returns>
    public bool FinalizeUnveil()
    {
        if (!IsOpenBsd)
            return false;

        try
        {
            var result = NativePerfuse.unveil(null, null);
            if (result != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                _kernelContext?.LogError($"unveil finalization failed with errno {errno}");
                return false;
            }

            _kernelContext?.LogInfo("unveil finalized");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"unveil finalization exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unveils the necessary paths for FUSE operation.
    /// </summary>
    /// <returns>True if all unveils were successful.</returns>
    public bool UnveilForFuse()
    {
        var success = true;

        // Unveil mount point with full permissions
        success &= Unveil(_config.MountPoint, "rwxc");

        // Unveil /dev/fuse for FUSE operations
        success &= Unveil("/dev/fuse", "rw");

        // Unveil common system paths needed
        success &= Unveil("/etc/fuse.conf", "r");
        success &= Unveil("/etc/mtab", "rw");
        success &= Unveil("/proc", "r");

        // Unveil tmp for work files
        success &= Unveil("/tmp", "rwc");

        return success;
    }

    #endregion

    #region Perfuse Operations

    /// <summary>
    /// Initializes perfuse for FUSE operations.
    /// </summary>
    /// <returns>True if initialization was successful.</returns>
    public bool Initialize()
    {
        if (!IsPerfuseAvailable)
        {
            _kernelContext?.LogError("perfuse is not available");
            return false;
        }

        _kernelContext?.LogInfo("Initializing perfuse for OpenBSD");

        // On OpenBSD, perfuse handles FUSE through PUFFS
        // The initialization is mostly automatic when using libfuse API

        return true;
    }

    /// <summary>
    /// Mounts the filesystem using perfuse.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <param name="options">Mount options.</param>
    /// <returns>True if mount was successful.</returns>
    public bool Mount(string mountPoint, string? options = null)
    {
        if (!IsPerfuseAvailable)
        {
            _kernelContext?.LogError("perfuse is not available");
            return false;
        }

        try
        {
            // Build mount arguments for perfuse
            var args = BuildPerfuseArgs(mountPoint, options);

            // On OpenBSD, we use mount_fusefs command
            // The perfuse library bridges to PUFFS kernel interface
            var result = NativePerfuse.mount(
                "fuse",
                mountPoint,
                0,
                options ?? "");

            if (result != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                _kernelContext?.LogError($"perfuse mount failed with errno {errno}");
                return false;
            }

            _kernelContext?.LogInfo($"perfuse mounted at {mountPoint}");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"perfuse mount exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unmounts the filesystem.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <returns>True if unmount was successful.</returns>
    public bool Unmount(string mountPoint)
    {
        try
        {
            var result = NativePerfuse.unmount(mountPoint, 0);
            if (result != 0)
            {
                // Try with MNT_FORCE
                result = NativePerfuse.unmount(mountPoint, NativePerfuse.MNT_FORCE);
            }

            if (result != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                _kernelContext?.LogError($"perfuse unmount failed with errno {errno}");
                return false;
            }

            _kernelContext?.LogInfo($"perfuse unmounted from {mountPoint}");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"perfuse unmount exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets OpenBSD-specific mount options.
    /// </summary>
    /// <returns>The recommended mount options for OpenBSD.</returns>
    public IEnumerable<string> GetOpenBsdMountOptions()
    {
        var options = new List<string>();

        // OpenBSD-specific options
        options.Add($"fsname={_config.FsName}");

        if (_config.AllowOther)
        {
            options.Add("allow_other");
        }

        if (_config.DirectIO)
        {
            options.Add("direct_io");
        }

        // OpenBSD perfuse-specific
        options.Add("nosuid");
        options.Add("nodev");

        return options;
    }

    #endregion

    #region System Information

    /// <summary>
    /// Gets the OpenBSD version.
    /// </summary>
    /// <returns>The OpenBSD version string, or null if not OpenBSD.</returns>
    public static string? GetOpenBsdVersion()
    {
        if (!IsOpenBsd)
            return null;

        try
        {
            var buffer = new byte[256];
            if (NativePerfuse.uname(buffer) == 0)
            {
                // uname struct: sysname, nodename, release, version, machine
                // Each is 256 bytes on OpenBSD
                var parts = Encoding.UTF8.GetString(buffer).Split('\0');
                if (parts.Length >= 3)
                {
                    return parts[2]; // release field
                }
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    /// <summary>
    /// Checks if a specific pledge promise is available.
    /// </summary>
    /// <param name="promise">The promise to check.</param>
    /// <returns>True if the promise is available.</returns>
    public static bool IsPledgePromiseAvailable(string promise)
    {
        // All standard promises should be available on supported OpenBSD versions
        var standardPromises = new HashSet<string>
        {
            "stdio", "rpath", "wpath", "cpath", "dpath", "tmppath",
            "inet", "mcast", "fattr", "chown", "flock", "unix",
            "dns", "getpw", "sendfd", "recvfd", "tape", "tty",
            "proc", "exec", "prot_exec", "settime", "ps", "vminfo",
            "id", "pf", "route", "wroute", "audio", "video",
            "bpf", "unveil", "error", "fuse"
        };

        return standardPromises.Contains(promise);
    }

    /// <summary>
    /// Gets the machine architecture.
    /// </summary>
    /// <returns>The machine architecture string.</returns>
    public static string? GetMachineArchitecture()
    {
        if (!IsOpenBsd)
            return RuntimeInformation.OSArchitecture.ToString();

        try
        {
            var buffer = new byte[1280]; // 5 * 256 for full utsname struct
            if (NativePerfuse.uname(buffer) == 0)
            {
                // machine is the 5th field
                var fullString = Encoding.UTF8.GetString(buffer);
                var offset = 256 * 4; // Skip first 4 fields
                var machine = fullString.Substring(offset).Split('\0')[0];
                return machine;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    #endregion

    #region Helper Methods

    private string[] BuildPerfuseArgs(string mountPoint, string? options)
    {
        var args = new List<string>
        {
            "datawarehouse", // argv[0]
            mountPoint
        };

        if (_config.Foreground)
            args.Add("-f");

        if (_config.Debug)
            args.Add("-d");

        var optList = GetOpenBsdMountOptions().ToList();

        if (!string.IsNullOrEmpty(options))
        {
            optList.AddRange(options.Split(','));
        }

        if (optList.Count > 0)
        {
            args.Add("-o");
            args.Add(string.Join(",", optList));
        }

        return args.ToArray();
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the OpenBSD perfuse resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _unveiledPaths.Clear();
    }

    #endregion

    #region Native Interop

    private static class NativePerfuse
    {
        public const int RTLD_NOW = 2;
        public const int MNT_FORCE = 0x00080000;

        [DllImport("libc", SetLastError = true)]
        public static extern int pledge(
            [MarshalAs(UnmanagedType.LPStr)] string? promises,
            [MarshalAs(UnmanagedType.LPStr)] string? execpromises);

        [DllImport("libc", SetLastError = true)]
        public static extern int unveil(
            [MarshalAs(UnmanagedType.LPStr)] string? path,
            [MarshalAs(UnmanagedType.LPStr)] string? permissions);

        [DllImport("libc", SetLastError = true)]
        public static extern int mount(
            [MarshalAs(UnmanagedType.LPStr)] string type,
            [MarshalAs(UnmanagedType.LPStr)] string dir,
            int flags,
            [MarshalAs(UnmanagedType.LPStr)] string data);

        [DllImport("libc", SetLastError = true)]
        public static extern int unmount(
            [MarshalAs(UnmanagedType.LPStr)] string dir,
            int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int uname(byte[] buf);

        [DllImport("libc")]
        public static extern IntPtr dlopen(
            [MarshalAs(UnmanagedType.LPStr)] string filename,
            int flags);

        [DllImport("libc")]
        public static extern int dlclose(IntPtr handle);
    }

    #endregion
}
