// <copyright file="NamespaceManager.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Manages Linux namespace operations for container compatibility and isolation.
/// Provides support for mount namespaces, user namespaces, and rootless containers.
/// </summary>
/// <remarks>
/// This class enables the FUSE driver to work correctly within:
/// - Docker containers
/// - Podman containers (including rootless)
/// - LXC/LXD containers
/// - Kubernetes pods
/// - Custom namespace configurations
/// </remarks>
public sealed class NamespaceManager : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private NamespaceInfo? _currentNamespaces;
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether namespace operations are available.
    /// </summary>
    public static bool IsNamespaceSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Gets the current namespace mode.
    /// </summary>
    public NamespaceMode CurrentMode { get; private set; } = NamespaceMode.Host;

    /// <summary>
    /// Gets the detected container runtime, if any.
    /// </summary>
    public ContainerRuntime DetectedRuntime { get; private set; } = ContainerRuntime.None;

    /// <summary>
    /// Gets a value indicating whether running in a rootless container.
    /// </summary>
    public bool IsRootless { get; private set; }

    /// <summary>
    /// Gets a value indicating whether user namespace mapping is active.
    /// </summary>
    public bool HasUserNamespace { get; private set; }

    /// <summary>
    /// Gets the current user's UID mapping in the user namespace.
    /// </summary>
    public UidGidMapping? UidMapping { get; private set; }

    /// <summary>
    /// Gets the current user's GID mapping in the user namespace.
    /// </summary>
    public UidGidMapping? GidMapping { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NamespaceManager"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public NamespaceManager(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;

        if (IsNamespaceSupported)
        {
            DetectEnvironment();
        }
    }

    #region Environment Detection

    /// <summary>
    /// Detects the current namespace environment.
    /// </summary>
    public void DetectEnvironment()
    {
        if (!IsNamespaceSupported)
            return;

        try
        {
            // Get current namespace info
            _currentNamespaces = GetNamespaceInfo(Environment.ProcessId);

            // Detect container runtime
            DetectedRuntime = DetectContainerRuntime();

            // Check for user namespace
            HasUserNamespace = CheckUserNamespace();

            // Check for rootless mode
            IsRootless = DetectRootlessMode();

            // Parse UID/GID mappings
            ParseUidGidMappings();

            // Determine namespace mode
            CurrentMode = DetermineNamespaceMode();

            _kernelContext?.LogInfo($"Namespace environment: Mode={CurrentMode}, Runtime={DetectedRuntime}, Rootless={IsRootless}");
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to detect namespace environment: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets namespace information for a process.
    /// </summary>
    /// <param name="pid">The process ID.</param>
    /// <returns>The namespace information.</returns>
    public NamespaceInfo? GetNamespaceInfo(int pid)
    {
        if (!IsNamespaceSupported)
            return null;

        try
        {
            var info = new NamespaceInfo { Pid = pid };
            var nsPath = $"/proc/{pid}/ns";

            if (!Directory.Exists(nsPath))
                return null;

            // Read namespace inode numbers
            info.MountNs = ReadNamespaceInode(Path.Combine(nsPath, "mnt"));
            info.UserNs = ReadNamespaceInode(Path.Combine(nsPath, "user"));
            info.PidNs = ReadNamespaceInode(Path.Combine(nsPath, "pid"));
            info.NetNs = ReadNamespaceInode(Path.Combine(nsPath, "net"));
            info.IpcNs = ReadNamespaceInode(Path.Combine(nsPath, "ipc"));
            info.UtsNs = ReadNamespaceInode(Path.Combine(nsPath, "uts"));
            info.CgroupNs = ReadNamespaceInode(Path.Combine(nsPath, "cgroup"));

            return info;
        }
        catch
        {
            return null;
        }
    }

    private static ulong ReadNamespaceInode(string path)
    {
        try
        {
            // Read the symlink target which is like "mnt:[4026531840]"
            var target = File.ReadAllText($"/proc/self/fd/{GetFdForPath(path)}");
            var match = System.Text.RegularExpressions.Regex.Match(target, @"\[(\d+)\]");
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out var inode))
            {
                return inode;
            }
        }
        catch
        {
            // Try alternative method using stat
            try
            {
                var linkTarget = ReadSymlink(path);
                if (!string.IsNullOrEmpty(linkTarget))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(linkTarget, @"\[(\d+)\]");
                    if (match.Success && ulong.TryParse(match.Groups[1].Value, out var inode))
                    {
                        return inode;
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        return 0;
    }

    private static int GetFdForPath(string path)
    {
        // This is a simplified version - in production, use proper syscall
        return 0;
    }

    private static string? ReadSymlink(string path)
    {
        try
        {
            var buffer = new byte[256];
            var result = NativeNamespace.readlink(path, buffer, buffer.Length);
            if (result > 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, (int)result);
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private ContainerRuntime DetectContainerRuntime()
    {
        // Check Docker
        if (File.Exists("/.dockerenv") || IsInCgroup("docker"))
        {
            return ContainerRuntime.Docker;
        }

        // Check Podman
        if (Environment.GetEnvironmentVariable("container") == "podman" ||
            IsInCgroup("libpod"))
        {
            return ContainerRuntime.Podman;
        }

        // Check LXC/LXD
        if (Environment.GetEnvironmentVariable("container") == "lxc" ||
            IsInCgroup("lxc"))
        {
            return ContainerRuntime.Lxc;
        }

        // Check Kubernetes
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")))
        {
            return ContainerRuntime.Kubernetes;
        }

        // Check systemd-nspawn
        if (Environment.GetEnvironmentVariable("container") == "systemd-nspawn")
        {
            return ContainerRuntime.SystemdNspawn;
        }

        // Generic container check
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("container")))
        {
            return ContainerRuntime.Other;
        }

        return ContainerRuntime.None;
    }

    private static bool IsInCgroup(string pattern)
    {
        try
        {
            var cgroup = File.ReadAllText("/proc/self/cgroup");
            return cgroup.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckUserNamespace()
    {
        try
        {
            // Check if we're in a non-initial user namespace
            var uidMap = File.ReadAllText("/proc/self/uid_map").Trim();
            if (string.IsNullOrEmpty(uidMap))
                return false;

            var lines = uidMap.Split('\n');
            if (lines.Length != 1)
                return true; // Multiple mappings indicate user namespace

            var parts = lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                // Single identity mapping (0 0 4294967295) means no user namespace
                return !(parts[0] == "0" && parts[1] == "0" && parts[2] == "4294967295");
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool DetectRootlessMode()
    {
        // Rootless if:
        // 1. Running as non-root inside container with user namespace
        // 2. Podman rootless (ROOTLESS_CGROUP env var)
        // 3. Docker rootless (dockerd-rootless-setuptool indicator)

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROOTLESS_CGROUP")))
            return true;

        if (DetectedRuntime == ContainerRuntime.Podman && HasUserNamespace)
            return true;

        // Check if real UID is not root but we have user namespace
        if (HasUserNamespace && GetRealUid() != 0)
            return true;

        return false;
    }

    private void ParseUidGidMappings()
    {
        try
        {
            // Parse UID mapping
            var uidMapContent = File.ReadAllText("/proc/self/uid_map").Trim();
            UidMapping = ParseMapping(uidMapContent);

            // Parse GID mapping
            var gidMapContent = File.ReadAllText("/proc/self/gid_map").Trim();
            GidMapping = ParseMapping(gidMapContent);
        }
        catch
        {
            // Ignore mapping errors
        }
    }

    private static UidGidMapping? ParseMapping(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var mapping = new UidGidMapping();

        foreach (var line in lines)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                uint.TryParse(parts[0], out var insideId) &&
                uint.TryParse(parts[1], out var outsideId) &&
                uint.TryParse(parts[2], out var count))
            {
                mapping.Entries.Add(new MappingEntry
                {
                    InsideId = insideId,
                    OutsideId = outsideId,
                    Count = count
                });
            }
        }

        return mapping.Entries.Count > 0 ? mapping : null;
    }

    private NamespaceMode DetermineNamespaceMode()
    {
        if (DetectedRuntime == ContainerRuntime.None && !HasUserNamespace)
            return NamespaceMode.Host;

        if (HasUserNamespace)
            return IsRootless ? NamespaceMode.RootlessContainer : NamespaceMode.UserNamespace;

        return NamespaceMode.Container;
    }

    #endregion

    #region Namespace Operations

    /// <summary>
    /// Enters a mount namespace.
    /// </summary>
    /// <param name="pid">The process ID whose mount namespace to enter.</param>
    /// <returns>True if successful.</returns>
    public bool EnterMountNamespace(int pid)
    {
        if (!IsNamespaceSupported)
            return false;

        try
        {
            var nsPath = $"/proc/{pid}/ns/mnt";
            var fd = NativeNamespace.open(nsPath, NativeNamespace.O_RDONLY);
            if (fd < 0)
            {
                _kernelContext?.LogError($"Failed to open mount namespace: {Marshal.GetLastWin32Error()}");
                return false;
            }

            try
            {
                var result = NativeNamespace.setns(fd, NativeNamespace.CLONE_NEWNS);
                if (result < 0)
                {
                    _kernelContext?.LogError($"Failed to enter mount namespace: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                _kernelContext?.LogInfo($"Entered mount namespace of PID {pid}");
                return true;
            }
            finally
            {
                NativeNamespace.close(fd);
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception entering mount namespace: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a new mount namespace (unshare).
    /// </summary>
    /// <returns>True if successful.</returns>
    public bool CreateMountNamespace()
    {
        if (!IsNamespaceSupported)
            return false;

        try
        {
            var result = NativeNamespace.unshare(NativeNamespace.CLONE_NEWNS);
            if (result < 0)
            {
                _kernelContext?.LogError($"Failed to create mount namespace: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _kernelContext?.LogInfo("Created new mount namespace");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception creating mount namespace: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Makes mount propagation private for the current mount namespace.
    /// </summary>
    /// <param name="mountPoint">The mount point to make private.</param>
    /// <returns>True if successful.</returns>
    public bool MakeMountPrivate(string mountPoint)
    {
        if (!IsNamespaceSupported)
            return false;

        try
        {
            var result = NativeNamespace.mount(
                null,
                mountPoint,
                null,
                NativeNamespace.MS_PRIVATE | NativeNamespace.MS_REC,
                null);

            if (result < 0)
            {
                _kernelContext?.LogError($"Failed to make mount private: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _kernelContext?.LogDebug($"Made mount {mountPoint} private");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception making mount private: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Makes mount propagation shared for the current mount namespace.
    /// </summary>
    /// <param name="mountPoint">The mount point to make shared.</param>
    /// <returns>True if successful.</returns>
    public bool MakeMountShared(string mountPoint)
    {
        if (!IsNamespaceSupported)
            return false;

        try
        {
            var result = NativeNamespace.mount(
                null,
                mountPoint,
                null,
                NativeNamespace.MS_SHARED | NativeNamespace.MS_REC,
                null);

            if (result < 0)
            {
                _kernelContext?.LogError($"Failed to make mount shared: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _kernelContext?.LogDebug($"Made mount {mountPoint} shared");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception making mount shared: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Makes mount propagation slave for the current mount namespace.
    /// </summary>
    /// <param name="mountPoint">The mount point to make slave.</param>
    /// <returns>True if successful.</returns>
    public bool MakeMountSlave(string mountPoint)
    {
        if (!IsNamespaceSupported)
            return false;

        try
        {
            var result = NativeNamespace.mount(
                null,
                mountPoint,
                null,
                NativeNamespace.MS_SLAVE | NativeNamespace.MS_REC,
                null);

            if (result < 0)
            {
                _kernelContext?.LogError($"Failed to make mount slave: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _kernelContext?.LogDebug($"Made mount {mountPoint} slave");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception making mount slave: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Container Compatibility

    /// <summary>
    /// Configures FUSE for container compatibility.
    /// </summary>
    /// <returns>True if configuration was successful.</returns>
    public bool ConfigureForContainer()
    {
        if (DetectedRuntime == ContainerRuntime.None)
            return true;

        _kernelContext?.LogInfo($"Configuring for container runtime: {DetectedRuntime}");

        // Check /dev/fuse availability
        if (!File.Exists("/dev/fuse"))
        {
            _kernelContext?.LogError("/dev/fuse not available in container");
            return false;
        }

        // For rootless containers, verify user namespace setup
        if (IsRootless)
        {
            return ConfigureRootless();
        }

        // For privileged containers, standard setup applies
        return true;
    }

    /// <summary>
    /// Configures FUSE for rootless container operation.
    /// </summary>
    /// <returns>True if configuration was successful.</returns>
    public bool ConfigureRootless()
    {
        if (!IsRootless)
            return true;

        _kernelContext?.LogInfo("Configuring for rootless mode");

        // Verify subuid/subgid mappings exist
        if (UidMapping == null || GidMapping == null)
        {
            _kernelContext?.LogWarning("No UID/GID mappings found for rootless mode");
        }

        // Check for fusermount3 or fusermount
        var fusermountPath = FindFusermount();
        if (string.IsNullOrEmpty(fusermountPath))
        {
            _kernelContext?.LogWarning("fusermount not found, some operations may fail");
        }

        return true;
    }

    /// <summary>
    /// Gets the recommended FUSE mount options for the current container environment.
    /// </summary>
    /// <returns>The recommended mount options.</returns>
    public IEnumerable<string> GetContainerMountOptions()
    {
        var options = new List<string>();

        if (DetectedRuntime != ContainerRuntime.None)
        {
            // Allow access from all users in container
            options.Add("allow_other");

            // Use default permissions for container compatibility
            options.Add("default_permissions");
        }

        if (IsRootless)
        {
            // For rootless, map to container user
            if (UidMapping?.Entries.FirstOrDefault() is { } uidEntry)
            {
                options.Add($"uid={uidEntry.InsideId}");
            }

            if (GidMapping?.Entries.FirstOrDefault() is { } gidEntry)
            {
                options.Add($"gid={gidEntry.InsideId}");
            }
        }

        return options;
    }

    /// <summary>
    /// Translates a UID from inside the user namespace to outside.
    /// </summary>
    /// <param name="insideUid">The UID inside the namespace.</param>
    /// <returns>The translated UID, or the original if no mapping applies.</returns>
    public uint TranslateUidToOutside(uint insideUid)
    {
        if (UidMapping == null)
            return insideUid;

        foreach (var entry in UidMapping.Entries)
        {
            if (insideUid >= entry.InsideId && insideUid < entry.InsideId + entry.Count)
            {
                return entry.OutsideId + (insideUid - entry.InsideId);
            }
        }

        return insideUid;
    }

    /// <summary>
    /// Translates a UID from outside the user namespace to inside.
    /// </summary>
    /// <param name="outsideUid">The UID outside the namespace.</param>
    /// <returns>The translated UID, or the original if no mapping applies.</returns>
    public uint TranslateUidToInside(uint outsideUid)
    {
        if (UidMapping == null)
            return outsideUid;

        foreach (var entry in UidMapping.Entries)
        {
            if (outsideUid >= entry.OutsideId && outsideUid < entry.OutsideId + entry.Count)
            {
                return entry.InsideId + (outsideUid - entry.OutsideId);
            }
        }

        return outsideUid;
    }

    /// <summary>
    /// Translates a GID from inside the user namespace to outside.
    /// </summary>
    /// <param name="insideGid">The GID inside the namespace.</param>
    /// <returns>The translated GID, or the original if no mapping applies.</returns>
    public uint TranslateGidToOutside(uint insideGid)
    {
        if (GidMapping == null)
            return insideGid;

        foreach (var entry in GidMapping.Entries)
        {
            if (insideGid >= entry.InsideId && insideGid < entry.InsideId + entry.Count)
            {
                return entry.OutsideId + (insideGid - entry.InsideId);
            }
        }

        return insideGid;
    }

    /// <summary>
    /// Translates a GID from outside the user namespace to inside.
    /// </summary>
    /// <param name="outsideGid">The GID outside the namespace.</param>
    /// <returns>The translated GID, or the original if no mapping applies.</returns>
    public uint TranslateGidToInside(uint outsideGid)
    {
        if (GidMapping == null)
            return outsideGid;

        foreach (var entry in GidMapping.Entries)
        {
            if (outsideGid >= entry.OutsideId && outsideGid < entry.OutsideId + entry.Count)
            {
                return entry.InsideId + (outsideGid - entry.OutsideId);
            }
        }

        return outsideGid;
    }

    #endregion

    #region Helper Methods

    private static string? FindFusermount()
    {
        var paths = new[] { "/usr/bin/fusermount3", "/usr/bin/fusermount", "/bin/fusermount3", "/bin/fusermount" };
        return paths.FirstOrDefault(File.Exists);
    }

    private static uint GetRealUid()
    {
        try
        {
            return NativeNamespace.getuid();
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the namespace manager resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _currentNamespaces = null;
    }

    #endregion

    #region Native Interop

    private static class NativeNamespace
    {
        public const int CLONE_NEWNS = 0x00020000;
        public const int CLONE_NEWUSER = 0x10000000;
        public const int CLONE_NEWPID = 0x20000000;
        public const int CLONE_NEWNET = 0x40000000;

        public const int O_RDONLY = 0;

        public const uint MS_PRIVATE = 1 << 18;
        public const uint MS_SHARED = 1 << 20;
        public const uint MS_SLAVE = 1 << 19;
        public const uint MS_REC = 16384;

        [DllImport("libc", SetLastError = true)]
        public static extern int setns(int fd, int nstype);

        [DllImport("libc", SetLastError = true)]
        public static extern int unshare(int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern nint readlink(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            byte[] buf,
            int bufsiz);

        [DllImport("libc", SetLastError = true)]
        public static extern int mount(
            [MarshalAs(UnmanagedType.LPStr)] string? source,
            [MarshalAs(UnmanagedType.LPStr)] string target,
            [MarshalAs(UnmanagedType.LPStr)] string? filesystemtype,
            uint mountflags,
            [MarshalAs(UnmanagedType.LPStr)] string? data);

        [DllImport("libc", SetLastError = true)]
        public static extern uint getuid();
    }

    #endregion
}

/// <summary>
/// Information about a process's namespaces.
/// </summary>
public sealed class NamespaceInfo
{
    /// <summary>
    /// Gets or sets the process ID.
    /// </summary>
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the mount namespace inode.
    /// </summary>
    public ulong MountNs { get; set; }

    /// <summary>
    /// Gets or sets the user namespace inode.
    /// </summary>
    public ulong UserNs { get; set; }

    /// <summary>
    /// Gets or sets the PID namespace inode.
    /// </summary>
    public ulong PidNs { get; set; }

    /// <summary>
    /// Gets or sets the network namespace inode.
    /// </summary>
    public ulong NetNs { get; set; }

    /// <summary>
    /// Gets or sets the IPC namespace inode.
    /// </summary>
    public ulong IpcNs { get; set; }

    /// <summary>
    /// Gets or sets the UTS namespace inode.
    /// </summary>
    public ulong UtsNs { get; set; }

    /// <summary>
    /// Gets or sets the cgroup namespace inode.
    /// </summary>
    public ulong CgroupNs { get; set; }
}

/// <summary>
/// Namespace operation mode.
/// </summary>
public enum NamespaceMode
{
    /// <summary>
    /// Running on host system (no containers).
    /// </summary>
    Host,

    /// <summary>
    /// Running in a standard (privileged) container.
    /// </summary>
    Container,

    /// <summary>
    /// Running with user namespace mapping.
    /// </summary>
    UserNamespace,

    /// <summary>
    /// Running in a rootless container.
    /// </summary>
    RootlessContainer
}

/// <summary>
/// Detected container runtime.
/// </summary>
public enum ContainerRuntime
{
    /// <summary>
    /// No container detected (running on host).
    /// </summary>
    None,

    /// <summary>
    /// Docker container.
    /// </summary>
    Docker,

    /// <summary>
    /// Podman container.
    /// </summary>
    Podman,

    /// <summary>
    /// LXC or LXD container.
    /// </summary>
    Lxc,

    /// <summary>
    /// Kubernetes pod.
    /// </summary>
    Kubernetes,

    /// <summary>
    /// systemd-nspawn container.
    /// </summary>
    SystemdNspawn,

    /// <summary>
    /// Other or unknown container runtime.
    /// </summary>
    Other
}

/// <summary>
/// UID or GID mapping for user namespaces.
/// </summary>
public sealed class UidGidMapping
{
    /// <summary>
    /// Gets the mapping entries.
    /// </summary>
    public List<MappingEntry> Entries { get; } = new();
}

/// <summary>
/// A single UID/GID mapping entry.
/// </summary>
public sealed class MappingEntry
{
    /// <summary>
    /// Gets or sets the ID inside the namespace.
    /// </summary>
    public uint InsideId { get; set; }

    /// <summary>
    /// Gets or sets the ID outside the namespace.
    /// </summary>
    public uint OutsideId { get; set; }

    /// <summary>
    /// Gets or sets the number of IDs in this mapping.
    /// </summary>
    public uint Count { get; set; }
}
