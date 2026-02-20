// <copyright file="OverlayfsBackend.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Provides an overlayfs backend for layering DataWarehouse storage on top of a local filesystem.
/// Implements copy-up-on-write semantics with whiteout handling for deleted files.
/// </summary>
/// <remarks>
/// This backend enables:
/// - Layering DataWarehouse virtual files over existing filesystem content
/// - Copy-up on write for efficient storage
/// - Whiteout handling for deletions
/// - Opaque directories for complete replacement
/// - Multiple lower layers support
/// </remarks>
public sealed class OverlayfsBackend : IDisposable
{
    private readonly FuseConfig _config;
    private readonly OverlayfsConfig _overlayConfig;
    private readonly IKernelContext? _kernelContext;
    private readonly BoundedDictionary<string, OverlayEntry> _entryCache = new BoundedDictionary<string, OverlayEntry>(1000);
    private readonly HashSet<string> _whiteouts = new();
    private readonly HashSet<string> _opaqueDirectories = new();
    private readonly object _syncLock = new();
    private bool _disposed;

    /// <summary>
    /// Character device major number for whiteout files.
    /// </summary>
    public const uint WhiteoutDevMajor = 0;

    /// <summary>
    /// Character device minor number for whiteout files.
    /// </summary>
    public const uint WhiteoutDevMinor = 0;

    /// <summary>
    /// Prefix for whiteout file names.
    /// </summary>
    public const string WhiteoutPrefix = ".wh.";

    /// <summary>
    /// Name of the opaque directory marker file.
    /// </summary>
    public const string OpaqueMarker = ".wh..wh..opq";

    /// <summary>
    /// Extended attribute for opaque directories.
    /// </summary>
    public const string OpaqueXattr = "trusted.overlay.opaque";

    /// <summary>
    /// Gets a value indicating whether overlayfs is available on this system.
    /// </summary>
    public static bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Gets the upper directory path.
    /// </summary>
    public string UpperDir => _overlayConfig.UpperDir;

    /// <summary>
    /// Gets the lower directory paths.
    /// </summary>
    public IReadOnlyList<string> LowerDirs => _overlayConfig.LowerDirs;

    /// <summary>
    /// Gets the work directory path.
    /// </summary>
    public string WorkDir => _overlayConfig.WorkDir;

    /// <summary>
    /// Gets a value indicating whether the overlay is mounted.
    /// </summary>
    public bool IsMounted { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OverlayfsBackend"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="overlayConfig">The overlay configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public OverlayfsBackend(FuseConfig config, OverlayfsConfig overlayConfig, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _overlayConfig = overlayConfig ?? throw new ArgumentNullException(nameof(overlayConfig));
        _kernelContext = kernelContext;

        ValidateConfiguration();
    }

    #region Initialization

    /// <summary>
    /// Initializes the overlay directories.
    /// </summary>
    /// <returns>True if initialization was successful.</returns>
    public bool Initialize()
    {
        try
        {
            // Create upper directory if it doesn't exist
            if (!Directory.Exists(UpperDir))
            {
                Directory.CreateDirectory(UpperDir);
                _kernelContext?.LogInfo($"Created upper directory: {UpperDir}");
            }

            // Create work directory if it doesn't exist
            if (!Directory.Exists(WorkDir))
            {
                Directory.CreateDirectory(WorkDir);
                _kernelContext?.LogInfo($"Created work directory: {WorkDir}");
            }

            // Verify lower directories exist
            foreach (var lowerDir in LowerDirs)
            {
                if (!Directory.Exists(lowerDir))
                {
                    _kernelContext?.LogError($"Lower directory does not exist: {lowerDir}");
                    return false;
                }
            }

            // Load existing whiteouts from upper directory
            LoadWhiteouts();

            // Load opaque directories
            LoadOpaqueDirectories();

            _kernelContext?.LogInfo($"Overlayfs backend initialized with {LowerDirs.Count} lower layers");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Failed to initialize overlayfs backend: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Mounts the overlay filesystem using kernel overlayfs.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <returns>True if mount was successful.</returns>
    public bool MountKernelOverlay(string mountPoint)
    {
        if (!IsAvailable)
        {
            _kernelContext?.LogError("Kernel overlayfs not available on this platform");
            return false;
        }

        try
        {
            var lowerDirOption = string.Join(":", LowerDirs);
            var mountOptions = $"lowerdir={lowerDirOption},upperdir={UpperDir},workdir={WorkDir}";

            if (_overlayConfig.EnableRedirectDir)
            {
                mountOptions += ",redirect_dir=on";
            }

            if (_overlayConfig.EnableIndex)
            {
                mountOptions += ",index=on";
            }

            if (_overlayConfig.EnableMetacopy)
            {
                mountOptions += ",metacopy=on";
            }

            if (_overlayConfig.EnableNfsExport)
            {
                mountOptions += ",nfs_export=on";
            }

            var result = NativeOverlay.mount(
                "overlay",
                mountPoint,
                "overlay",
                0,
                mountOptions);

            if (result < 0)
            {
                _kernelContext?.LogError($"Failed to mount overlayfs: {Marshal.GetLastWin32Error()}");
                return false;
            }

            IsMounted = true;
            _kernelContext?.LogInfo($"Kernel overlayfs mounted at {mountPoint}");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception mounting overlayfs: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unmounts the overlay filesystem.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <returns>True if unmount was successful.</returns>
    public bool UnmountKernelOverlay(string mountPoint)
    {
        if (!IsMounted)
            return true;

        try
        {
            var result = NativeOverlay.umount(mountPoint);
            if (result < 0)
            {
                // Try lazy unmount
                result = NativeOverlay.umount2(mountPoint, NativeOverlay.MNT_DETACH);
            }

            if (result < 0)
            {
                _kernelContext?.LogError($"Failed to unmount overlayfs: {Marshal.GetLastWin32Error()}");
                return false;
            }

            IsMounted = false;
            _kernelContext?.LogInfo("Kernel overlayfs unmounted");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Exception unmounting overlayfs: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region File Operations

    /// <summary>
    /// Looks up a file in the overlay stack.
    /// </summary>
    /// <param name="path">The file path relative to overlay root.</param>
    /// <returns>The overlay entry, or null if not found.</returns>
    public OverlayEntry? Lookup(string path)
    {
        path = NormalizePath(path);

        // Check cache first
        if (_entryCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        // Check if whited out
        if (IsWhitedOut(path))
        {
            return null;
        }

        // Check parent for opaque directory
        var parentPath = GetParentPath(path);
        if (_opaqueDirectories.Contains(parentPath))
        {
            // Parent is opaque, only check upper
            var entry = LookupInLayer(path, UpperDir, OverlayLayer.Upper);
            if (entry != null)
            {
                _entryCache[path] = entry;
            }
            return entry;
        }

        // Check upper layer first
        var upperEntry = LookupInLayer(path, UpperDir, OverlayLayer.Upper);
        if (upperEntry != null)
        {
            _entryCache[path] = upperEntry;
            return upperEntry;
        }

        // Check lower layers in order
        for (var i = 0; i < LowerDirs.Count; i++)
        {
            var lowerEntry = LookupInLayer(path, LowerDirs[i], OverlayLayer.Lower);
            if (lowerEntry != null)
            {
                lowerEntry.LowerLayerIndex = i;
                _entryCache[path] = lowerEntry;
                return lowerEntry;
            }
        }

        return null;
    }

    /// <summary>
    /// Performs copy-up of a file from lower to upper layer.
    /// </summary>
    /// <param name="path">The file path relative to overlay root.</param>
    /// <returns>True if copy-up was successful.</returns>
    public bool CopyUp(string path)
    {
        path = NormalizePath(path);

        var entry = Lookup(path);
        if (entry == null)
        {
            _kernelContext?.LogError($"Cannot copy up: path not found: {path}");
            return false;
        }

        if (entry.Layer == OverlayLayer.Upper)
        {
            // Already in upper layer
            return true;
        }

        try
        {
            lock (_syncLock)
            {
                var sourcePath = entry.RealPath;
                var destPath = Path.Combine(UpperDir, path.TrimStart('/'));

                // Ensure parent directory exists in upper
                var destParent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
                {
                    // Copy up parent directories first
                    var parentPath = GetParentPath(path);
                    if (parentPath != "/")
                    {
                        CopyUpDirectory(parentPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(destParent);
                    }
                }

                if (entry.IsDirectory)
                {
                    // Create directory in upper (don't copy contents)
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    // Copy file
                    File.Copy(sourcePath, destPath, overwrite: true);
                }

                // Copy attributes and xattrs
                CopyAttributes(sourcePath, destPath);

                // Update cache
                entry.Layer = OverlayLayer.Upper;
                entry.RealPath = destPath;

                _kernelContext?.LogDebug($"Copied up: {path}");
                return true;
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Copy up failed for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a whiteout for a deleted file.
    /// </summary>
    /// <param name="path">The path to white out.</param>
    /// <returns>True if whiteout was created.</returns>
    public bool CreateWhiteout(string path)
    {
        path = NormalizePath(path);

        try
        {
            lock (_syncLock)
            {
                var whiteoutName = WhiteoutPrefix + Path.GetFileName(path);
                var whiteoutDir = Path.GetDirectoryName(Path.Combine(UpperDir, path.TrimStart('/')));

                if (string.IsNullOrEmpty(whiteoutDir))
                {
                    whiteoutDir = UpperDir;
                }

                // Ensure parent directory exists
                if (!Directory.Exists(whiteoutDir))
                {
                    Directory.CreateDirectory(whiteoutDir);
                }

                var whiteoutPath = Path.Combine(whiteoutDir, whiteoutName);

                // Create whiteout as character device (0,0) on Linux
                // On other systems, create marker file
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var dev = NativeOverlay.makedev(WhiteoutDevMajor, WhiteoutDevMinor);
                    var result = NativeOverlay.mknod(whiteoutPath, NativeOverlay.S_IFCHR, dev);

                    if (result < 0)
                    {
                        // Fall back to marker file if mknod fails (no CAP_MKNOD)
                        File.Create(whiteoutPath).Dispose();
                    }
                }
                else
                {
                    // Create marker file on non-Linux systems
                    File.Create(whiteoutPath).Dispose();
                }

                _whiteouts.Add(path);
                _entryCache.TryRemove(path, out _);

                _kernelContext?.LogDebug($"Created whiteout: {path}");
                return true;
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Failed to create whiteout for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes a whiteout to reveal the underlying file.
    /// </summary>
    /// <param name="path">The path to unwhite out.</param>
    /// <returns>True if whiteout was removed.</returns>
    public bool RemoveWhiteout(string path)
    {
        path = NormalizePath(path);

        try
        {
            lock (_syncLock)
            {
                var whiteoutName = WhiteoutPrefix + Path.GetFileName(path);
                var whiteoutDir = Path.GetDirectoryName(Path.Combine(UpperDir, path.TrimStart('/')));

                if (string.IsNullOrEmpty(whiteoutDir))
                {
                    whiteoutDir = UpperDir;
                }

                var whiteoutPath = Path.Combine(whiteoutDir, whiteoutName);

                if (File.Exists(whiteoutPath))
                {
                    File.Delete(whiteoutPath);
                }

                _whiteouts.Remove(path);
                _entryCache.TryRemove(path, out _);

                _kernelContext?.LogDebug($"Removed whiteout: {path}");
                return true;
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Failed to remove whiteout for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Marks a directory as opaque, hiding all lower layer contents.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <returns>True if the directory was marked opaque.</returns>
    public bool MarkOpaque(string path)
    {
        path = NormalizePath(path);

        try
        {
            lock (_syncLock)
            {
                var upperPath = Path.Combine(UpperDir, path.TrimStart('/'));

                if (!Directory.Exists(upperPath))
                {
                    _kernelContext?.LogError($"Cannot mark opaque: directory not in upper layer: {path}");
                    return false;
                }

                // Create opaque marker file
                var markerPath = Path.Combine(upperPath, OpaqueMarker);
                File.Create(markerPath).Dispose();

                // Also set the xattr if possible
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    try
                    {
                        var value = Encoding.UTF8.GetBytes("y");
                        NativeOverlay.setxattr(upperPath, OpaqueXattr, value, value.Length, 0);
                    }
                    catch
                    {
                        // Ignore xattr errors
                    }
                }

                _opaqueDirectories.Add(path);

                _kernelContext?.LogDebug($"Marked directory opaque: {path}");
                return true;
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError($"Failed to mark opaque: {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lists directory contents from all layers.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <returns>The merged directory entries.</returns>
    public IEnumerable<OverlayDirEntry> ReadDir(string path)
    {
        path = NormalizePath(path);

        var entries = new Dictionary<string, OverlayDirEntry>();
        var whiteoutSet = new HashSet<string>();

        // Check if directory is opaque
        var isOpaque = _opaqueDirectories.Contains(path);

        // Read upper layer first
        var upperPath = Path.Combine(UpperDir, path.TrimStart('/'));
        if (Directory.Exists(upperPath))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(upperPath))
            {
                var name = Path.GetFileName(entry);

                // Skip whiteout files but record them
                if (name.StartsWith(WhiteoutPrefix))
                {
                    if (name == OpaqueMarker)
                    {
                        isOpaque = true;
                    }
                    else
                    {
                        whiteoutSet.Add(name.Substring(WhiteoutPrefix.Length));
                    }
                    continue;
                }

                var isDir = Directory.Exists(entry);
                entries[name] = new OverlayDirEntry
                {
                    Name = name,
                    IsDirectory = isDir,
                    Layer = OverlayLayer.Upper,
                    RealPath = entry
                };
            }
        }

        // If opaque, don't read lower layers
        if (isOpaque)
        {
            return entries.Values;
        }

        // Read lower layers
        for (var i = 0; i < LowerDirs.Count; i++)
        {
            var lowerPath = Path.Combine(LowerDirs[i], path.TrimStart('/'));
            if (!Directory.Exists(lowerPath))
                continue;

            foreach (var entry in Directory.EnumerateFileSystemEntries(lowerPath))
            {
                var name = Path.GetFileName(entry);

                // Skip if whited out
                if (whiteoutSet.Contains(name))
                    continue;

                // Skip if already in entries (upper takes precedence)
                if (entries.ContainsKey(name))
                    continue;

                var isDir = Directory.Exists(entry);
                entries[name] = new OverlayDirEntry
                {
                    Name = name,
                    IsDirectory = isDir,
                    Layer = OverlayLayer.Lower,
                    LowerLayerIndex = i,
                    RealPath = entry
                };
            }
        }

        return entries.Values;
    }

    /// <summary>
    /// Checks if a path is whited out.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the path is whited out.</returns>
    public bool IsWhitedOut(string path)
    {
        path = NormalizePath(path);
        return _whiteouts.Contains(path);
    }

    #endregion

    #region Helper Methods

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_overlayConfig.UpperDir))
        {
            throw new ArgumentException("Upper directory must be specified", nameof(_overlayConfig));
        }

        if (_overlayConfig.LowerDirs == null || _overlayConfig.LowerDirs.Count == 0)
        {
            throw new ArgumentException("At least one lower directory must be specified", nameof(_overlayConfig));
        }

        if (string.IsNullOrEmpty(_overlayConfig.WorkDir))
        {
            throw new ArgumentException("Work directory must be specified", nameof(_overlayConfig));
        }
    }

    private void LoadWhiteouts()
    {
        try
        {
            LoadWhiteoutsRecursive(UpperDir, "");
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to load whiteouts: {ex.Message}");
        }
    }

    private void LoadWhiteoutsRecursive(string physicalPath, string virtualPath)
    {
        if (!Directory.Exists(physicalPath))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(physicalPath))
        {
            var name = Path.GetFileName(entry);

            if (name.StartsWith(WhiteoutPrefix))
            {
                if (name != OpaqueMarker)
                {
                    var originalName = name.Substring(WhiteoutPrefix.Length);
                    var fullPath = string.IsNullOrEmpty(virtualPath)
                        ? "/" + originalName
                        : virtualPath + "/" + originalName;
                    _whiteouts.Add(fullPath);
                }
            }
            else if (Directory.Exists(entry))
            {
                var subPath = string.IsNullOrEmpty(virtualPath)
                    ? "/" + name
                    : virtualPath + "/" + name;
                LoadWhiteoutsRecursive(entry, subPath);
            }
        }
    }

    private void LoadOpaqueDirectories()
    {
        try
        {
            LoadOpaqueDirectoriesRecursive(UpperDir, "");
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to load opaque directories: {ex.Message}");
        }
    }

    private void LoadOpaqueDirectoriesRecursive(string physicalPath, string virtualPath)
    {
        if (!Directory.Exists(physicalPath))
            return;

        // Check for opaque marker
        var markerPath = Path.Combine(physicalPath, OpaqueMarker);
        if (File.Exists(markerPath))
        {
            var path = string.IsNullOrEmpty(virtualPath) ? "/" : virtualPath;
            _opaqueDirectories.Add(path);
        }

        foreach (var entry in Directory.EnumerateDirectories(physicalPath))
        {
            var name = Path.GetFileName(entry);
            var subPath = string.IsNullOrEmpty(virtualPath)
                ? "/" + name
                : virtualPath + "/" + name;
            LoadOpaqueDirectoriesRecursive(entry, subPath);
        }
    }

    private OverlayEntry? LookupInLayer(string path, string layerPath, OverlayLayer layer)
    {
        var realPath = Path.Combine(layerPath, path.TrimStart('/'));

        if (File.Exists(realPath))
        {
            var info = new FileInfo(realPath);
            return new OverlayEntry
            {
                Path = path,
                RealPath = realPath,
                Layer = layer,
                IsDirectory = false,
                Size = info.Length,
                ModTime = info.LastWriteTimeUtc
            };
        }

        if (Directory.Exists(realPath))
        {
            var info = new DirectoryInfo(realPath);
            return new OverlayEntry
            {
                Path = path,
                RealPath = realPath,
                Layer = layer,
                IsDirectory = true,
                Size = 0,
                ModTime = info.LastWriteTimeUtc
            };
        }

        return null;
    }

    private void CopyUpDirectory(string path)
    {
        var entry = Lookup(path);
        if (entry == null)
        {
            // Create directory
            var destPath = Path.Combine(UpperDir, path.TrimStart('/'));
            Directory.CreateDirectory(destPath);
            return;
        }

        if (entry.Layer == OverlayLayer.Upper)
            return;

        var sourcePath = entry.RealPath;
        var targetPath = Path.Combine(UpperDir, path.TrimStart('/'));

        // Copy up parent first
        var parentPath = GetParentPath(path);
        if (parentPath != "/" && parentPath != path)
        {
            CopyUpDirectory(parentPath);
        }

        Directory.CreateDirectory(targetPath);
        CopyAttributes(sourcePath, targetPath);

        entry.Layer = OverlayLayer.Upper;
        entry.RealPath = targetPath;
    }

    private static void CopyAttributes(string source, string dest)
    {
        try
        {
            var sourceInfo = new FileInfo(source);
            var destInfo = new FileInfo(dest);

            destInfo.CreationTimeUtc = sourceInfo.CreationTimeUtc;
            destInfo.LastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
            destInfo.LastAccessTimeUtc = sourceInfo.LastAccessTimeUtc;
            destInfo.Attributes = sourceInfo.Attributes;
        }
        catch
        {
            // Ignore attribute copy errors
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "/";

        var normalized = path.Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static string GetParentPath(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == "/")
            return "/";

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalized[..lastSlash];
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the overlay backend resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _entryCache.Clear();
        _whiteouts.Clear();
        _opaqueDirectories.Clear();
    }

    #endregion

    #region Native Interop

    private static class NativeOverlay
    {
        public const uint S_IFCHR = 0x2000;
        public const int MNT_DETACH = 2;

        [DllImport("libc", SetLastError = true)]
        public static extern int mount(
            [MarshalAs(UnmanagedType.LPStr)] string source,
            [MarshalAs(UnmanagedType.LPStr)] string target,
            [MarshalAs(UnmanagedType.LPStr)] string filesystemtype,
            uint mountflags,
            [MarshalAs(UnmanagedType.LPStr)] string data);

        [DllImport("libc", SetLastError = true)]
        public static extern int umount([MarshalAs(UnmanagedType.LPStr)] string target);

        [DllImport("libc", SetLastError = true)]
        public static extern int umount2([MarshalAs(UnmanagedType.LPStr)] string target, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int mknod([MarshalAs(UnmanagedType.LPStr)] string pathname, uint mode, ulong dev);

        [DllImport("libc", SetLastError = true)]
        public static extern int setxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            byte[] value,
            int size,
            int flags);

        public static ulong makedev(uint major, uint minor)
        {
            return ((ulong)major << 8) | minor;
        }
    }

    #endregion
}

/// <summary>
/// Configuration for the overlayfs backend.
/// </summary>
public sealed class OverlayfsConfig
{
    /// <summary>
    /// Gets or sets the upper (writable) directory path.
    /// </summary>
    public string UpperDir { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lower (read-only) directory paths.
    /// </summary>
    public List<string> LowerDirs { get; set; } = new();

    /// <summary>
    /// Gets or sets the work directory path.
    /// </summary>
    public string WorkDir { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to enable redirect_dir for rename optimization.
    /// </summary>
    public bool EnableRedirectDir { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable index for inode consistency.
    /// </summary>
    public bool EnableIndex { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable metacopy for metadata-only copy-up.
    /// </summary>
    public bool EnableMetacopy { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to enable NFS export support.
    /// </summary>
    public bool EnableNfsExport { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to use userspace overlay instead of kernel.
    /// </summary>
    public bool UseUserspaceOverlay { get; set; } = false;
}

/// <summary>
/// An entry in the overlay filesystem.
/// </summary>
public sealed class OverlayEntry
{
    /// <summary>
    /// Gets or sets the virtual path in the overlay.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the real path on disk.
    /// </summary>
    public string RealPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the layer containing this entry.
    /// </summary>
    public OverlayLayer Layer { get; set; }

    /// <summary>
    /// Gets or sets the lower layer index (if in lower layer).
    /// </summary>
    public int LowerLayerIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Gets or sets the file size.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the modification time.
    /// </summary>
    public DateTime ModTime { get; set; }
}

/// <summary>
/// A directory entry in the overlay filesystem.
/// </summary>
public sealed class OverlayDirEntry
{
    /// <summary>
    /// Gets or sets the entry name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Gets or sets the layer containing this entry.
    /// </summary>
    public OverlayLayer Layer { get; set; }

    /// <summary>
    /// Gets or sets the lower layer index (if in lower layer).
    /// </summary>
    public int LowerLayerIndex { get; set; }

    /// <summary>
    /// Gets or sets the real path on disk.
    /// </summary>
    public string RealPath { get; set; } = string.Empty;
}

/// <summary>
/// The layer containing an overlay entry.
/// </summary>
public enum OverlayLayer
{
    /// <summary>
    /// The upper (writable) layer.
    /// </summary>
    Upper,

    /// <summary>
    /// A lower (read-only) layer.
    /// </summary>
    Lower
}
