using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Configuration options for mounting a VDE volume as a native OS filesystem.
/// Controls caching, concurrency, permissions, and platform-specific behavior.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount provider abstraction (VOPT-83)")]
public sealed class MountOptions
{
    /// <summary>
    /// Mount the volume as read-only. Write operations will be rejected.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Number of entries in the ARC cache for this mount session.
    /// Higher values improve read performance at the cost of memory.
    /// </summary>
    public int CacheCapacity { get; set; } = 4096;

    /// <summary>
    /// Allow other users on the system to access the mounted filesystem.
    /// Maps to FUSE allow_other option on Linux/macOS.
    /// </summary>
    public bool AllowOther { get; set; }

    /// <summary>
    /// Automatically unmount when the process exits or the mount handle is disposed.
    /// </summary>
    public bool AutoUnmount { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent filesystem operations.
    /// Controls the semaphore limit in VdeFilesystemAdapter.
    /// </summary>
    public int MaxConcurrentOps { get; set; } = 64;

    /// <summary>
    /// Duration to cache file/directory attribute metadata before re-reading from VDE.
    /// </summary>
    public TimeSpan AttrTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Duration to cache directory entry lookups before re-reading from VDE.
    /// </summary>
    public TimeSpan EntryTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Enable writeback caching for improved write performance.
    /// Maps to FUSE writeback_cache on Linux.
    /// </summary>
    public bool EnableWriteback { get; set; } = true;

    /// <summary>
    /// Platform-specific options passed directly to the underlying mount provider.
    /// Keys and values are provider-dependent (e.g., WinFsp, FUSE3, macFUSE).
    /// </summary>
    public Dictionary<string, string> PlatformOptions { get; set; } = new();
}
