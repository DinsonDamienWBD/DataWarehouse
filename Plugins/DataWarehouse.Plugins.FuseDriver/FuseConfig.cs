// <copyright file="FuseConfig.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Configuration options for the FUSE filesystem driver.
/// </summary>
public sealed class FuseConfig
{
    /// <summary>
    /// Gets or sets the mount point path for the filesystem.
    /// </summary>
    /// <remarks>
    /// On Linux/macOS, this should be an existing empty directory.
    /// The directory must have appropriate permissions for the current user.
    /// </remarks>
    public string MountPoint { get; set; } = "/mnt/datawarehouse";

    /// <summary>
    /// Gets or sets a value indicating whether to run in foreground (debug) mode.
    /// </summary>
    /// <remarks>
    /// When true, FUSE runs in the foreground and logs to console.
    /// When false, FUSE daemonizes and runs in the background.
    /// </remarks>
    public bool Foreground { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable debug output.
    /// </summary>
    public bool Debug { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to allow other users to access the mount.
    /// </summary>
    /// <remarks>
    /// Requires 'user_allow_other' in /etc/fuse.conf on Linux.
    /// </remarks>
    public bool AllowOther { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to allow root to access the mount.
    /// </summary>
    public bool AllowRoot { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to use direct I/O mode.
    /// </summary>
    /// <remarks>
    /// When true, bypasses the kernel page cache for all I/O operations.
    /// Useful for applications that do their own caching.
    /// </remarks>
    public bool DirectIO { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable kernel caching.
    /// </summary>
    /// <remarks>
    /// When true, enables the kernel to cache file data and metadata.
    /// Improves performance for frequently accessed files.
    /// </remarks>
    public bool KernelCache { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable automatic unmount on process termination.
    /// </summary>
    public bool AutoUnmount { get; set; } = true;

    /// <summary>
    /// Gets or sets the default file mode (permissions) for new files.
    /// </summary>
    /// <remarks>
    /// Uses Unix octal notation: e.g., 0644 = rw-r--r--
    /// </remarks>
    public uint DefaultFileMode { get; set; } = 0x1A4; // 0644 in octal

    /// <summary>
    /// Gets or sets the default directory mode (permissions) for new directories.
    /// </summary>
    /// <remarks>
    /// Uses Unix octal notation: e.g., 0755 = rwxr-xr-x
    /// </remarks>
    public uint DefaultDirMode { get; set; } = 0x1ED; // 0755 in octal

    /// <summary>
    /// Gets or sets the default user ID for files.
    /// </summary>
    /// <remarks>
    /// When 0, uses the UID of the mounting process.
    /// </remarks>
    public uint DefaultUid { get; set; } = 0;

    /// <summary>
    /// Gets or sets the default group ID for files.
    /// </summary>
    /// <remarks>
    /// When 0, uses the GID of the mounting process.
    /// </remarks>
    public uint DefaultGid { get; set; } = 0;

    /// <summary>
    /// Gets or sets the block size for the filesystem.
    /// </summary>
    /// <remarks>
    /// Affects how file sizes are reported and I/O operations are aligned.
    /// </remarks>
    public uint BlockSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the maximum read size in bytes.
    /// </summary>
    public uint MaxRead { get; set; } = 131072; // 128 KB

    /// <summary>
    /// Gets or sets the maximum write size in bytes.
    /// </summary>
    public uint MaxWrite { get; set; } = 131072; // 128 KB

    /// <summary>
    /// Gets or sets the maximum number of background requests.
    /// </summary>
    public uint MaxBackground { get; set; } = 12;

    /// <summary>
    /// Gets or sets the congestion threshold for background requests.
    /// </summary>
    public uint CongestionThreshold { get; set; } = 9;

    /// <summary>
    /// Gets or sets a value indicating whether to enable writeback caching.
    /// </summary>
    /// <remarks>
    /// When true, enables write caching in the kernel for improved write performance.
    /// Requires FUSE kernel module version 7.23 or later.
    /// </remarks>
    public bool WritebackCache { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable splice read operations.
    /// </summary>
    /// <remarks>
    /// When true, enables zero-copy data transfer for read operations on Linux.
    /// </remarks>
    public bool SpliceRead { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable splice write operations.
    /// </summary>
    /// <remarks>
    /// When true, enables zero-copy data transfer for write operations on Linux.
    /// </remarks>
    public bool SpliceWrite { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable splice move operations.
    /// </summary>
    /// <remarks>
    /// When true, enables zero-copy data transfer for move operations on Linux.
    /// </remarks>
    public bool SpliceMove { get; set; } = true;

    /// <summary>
    /// Gets or sets the attribute cache timeout in seconds.
    /// </summary>
    /// <remarks>
    /// How long file attributes are cached before being re-fetched.
    /// Set to 0 to disable caching.
    /// </remarks>
    public double AttrTimeout { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the entry cache timeout in seconds.
    /// </summary>
    /// <remarks>
    /// How long directory entries are cached before being re-fetched.
    /// Set to 0 to disable caching.
    /// </remarks>
    public double EntryTimeout { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the negative entry cache timeout in seconds.
    /// </summary>
    /// <remarks>
    /// How long non-existent entries are cached (ENOENT results).
    /// Set to 0 to disable caching.
    /// </remarks>
    public double NegativeTimeout { get; set; } = 0.0;

    /// <summary>
    /// Gets or sets a value indicating whether to enable extended attribute (xattr) support.
    /// </summary>
    public bool EnableXattr { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable POSIX ACL support.
    /// </summary>
    public bool EnableAcl { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable inotify/FSEvents integration.
    /// </summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable SELinux label support (Linux only).
    /// </summary>
    public bool EnableSeLinux { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable Spotlight indexing (macOS only).
    /// </summary>
    public bool EnableSpotlight { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable Finder integration (macOS only).
    /// </summary>
    public bool EnableFinderIntegration { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable io_uring support (Linux only).
    /// </summary>
    /// <remarks>
    /// Requires Linux kernel 5.1+ and liburing.
    /// </remarks>
    public bool EnableIoUring { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable cgroups awareness (Linux only).
    /// </summary>
    public bool EnableCgroups { get; set; } = false;

    /// <summary>
    /// Gets or sets the filesystem name shown in mount output.
    /// </summary>
    public string FsName { get; set; } = "datawarehouse";

    /// <summary>
    /// Gets or sets the volume name shown to the OS.
    /// </summary>
    public string VolumeName { get; set; } = "DataWarehouse";

    /// <summary>
    /// Gets or sets additional FUSE mount options.
    /// </summary>
    /// <remarks>
    /// These options are passed directly to the FUSE mount command.
    /// </remarks>
    public List<string> AdditionalOptions { get; set; } = new();

    /// <summary>
    /// Gets the current platform type.
    /// </summary>
    public static FusePlatform CurrentPlatform
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return FusePlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return FusePlatform.MacOS;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
                return FusePlatform.FreeBSD;
            return FusePlatform.Unsupported;
        }
    }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MountPoint))
        {
            throw new InvalidOperationException("Mount point cannot be empty");
        }

        if (CurrentPlatform == FusePlatform.Unsupported)
        {
            throw new InvalidOperationException(
                $"FUSE is not supported on this platform: {RuntimeInformation.OSDescription}");
        }

        if (BlockSize == 0 || (BlockSize & (BlockSize - 1)) != 0)
        {
            throw new InvalidOperationException("Block size must be a power of 2");
        }

        if (MaxRead == 0)
        {
            throw new InvalidOperationException("MaxRead must be greater than 0");
        }

        if (MaxWrite == 0)
        {
            throw new InvalidOperationException("MaxWrite must be greater than 0");
        }

        if (AllowOther && AllowRoot)
        {
            throw new InvalidOperationException("Cannot specify both AllowOther and AllowRoot");
        }

        if (AttrTimeout < 0)
        {
            throw new InvalidOperationException("AttrTimeout cannot be negative");
        }

        if (EntryTimeout < 0)
        {
            throw new InvalidOperationException("EntryTimeout cannot be negative");
        }

        if (NegativeTimeout < 0)
        {
            throw new InvalidOperationException("NegativeTimeout cannot be negative");
        }

        // Platform-specific validations
        if (CurrentPlatform != FusePlatform.Linux)
        {
            if (EnableSeLinux)
            {
                throw new InvalidOperationException("SELinux support is only available on Linux");
            }

            if (EnableIoUring)
            {
                throw new InvalidOperationException("io_uring support is only available on Linux");
            }

            if (EnableCgroups)
            {
                throw new InvalidOperationException("cgroups support is only available on Linux");
            }
        }

        if (CurrentPlatform != FusePlatform.MacOS)
        {
            if (EnableSpotlight)
            {
                throw new InvalidOperationException("Spotlight support is only available on macOS");
            }

            if (EnableFinderIntegration)
            {
                throw new InvalidOperationException("Finder integration is only available on macOS");
            }
        }
    }

    /// <summary>
    /// Creates a default configuration for the current platform.
    /// </summary>
    /// <returns>A new <see cref="FuseConfig"/> with platform-appropriate defaults.</returns>
    public static FuseConfig CreateDefault()
    {
        var config = new FuseConfig();

        switch (CurrentPlatform)
        {
            case FusePlatform.Linux:
                config.MountPoint = "/mnt/datawarehouse";
                config.SpliceRead = true;
                config.SpliceWrite = true;
                config.SpliceMove = true;
                break;

            case FusePlatform.MacOS:
                config.MountPoint = "/Volumes/DataWarehouse";
                config.SpliceRead = false; // Not supported on macOS
                config.SpliceWrite = false;
                config.SpliceMove = false;
                config.EnableFinderIntegration = true;
                break;

            case FusePlatform.FreeBSD:
                config.MountPoint = "/mnt/datawarehouse";
                config.SpliceRead = false;
                config.SpliceWrite = false;
                config.SpliceMove = false;
                break;

            default:
                break;
        }

        return config;
    }
}

/// <summary>
/// Supported FUSE platforms.
/// </summary>
public enum FusePlatform
{
    /// <summary>
    /// Platform is not supported for FUSE operations.
    /// </summary>
    Unsupported = 0,

    /// <summary>
    /// Linux with libfuse3.
    /// </summary>
    Linux = 1,

    /// <summary>
    /// macOS with macFUSE.
    /// </summary>
    MacOS = 2,

    /// <summary>
    /// FreeBSD with FUSE for FreeBSD.
    /// </summary>
    FreeBSD = 3
}
