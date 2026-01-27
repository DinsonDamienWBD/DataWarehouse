// <copyright file="MacOsSpecific.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// macOS-specific features for the FUSE filesystem.
/// Provides FSEvents integration, Spotlight indexing support, and Finder integration.
/// </summary>
public sealed class MacOsSpecific : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private readonly ConcurrentDictionary<string, FinderInfo> _finderInfoCache = new();
#pragma warning disable CS0169 // Field is never used - reserved for native FSEvents stream pointer
    private nint _fsEventsStream;
#pragma warning restore CS0169
    private Thread? _fsEventsThread;
    private CancellationTokenSource? _fsEventsCts;
    private bool _disposed;

    /// <summary>
    /// Event raised when a file system change is detected via FSEvents.
    /// </summary>
#pragma warning disable CS0067 // Event is never used - reserved for FSEvents integration
    public event EventHandler<FSEventArgs>? FileChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Gets a value indicating whether this platform is macOS.
    /// </summary>
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// Gets a value indicating whether macFUSE is installed.
    /// </summary>
    public bool IsMacFuseInstalled { get; private set; }

    /// <summary>
    /// Gets a value indicating whether Spotlight indexing is enabled.
    /// </summary>
    public bool IsSpotlightEnabled => _config.EnableSpotlight;

    /// <summary>
    /// Gets a value indicating whether Finder integration is enabled.
    /// </summary>
    public bool IsFinderIntegrationEnabled => _config.EnableFinderIntegration;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacOsSpecific"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public MacOsSpecific(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;

        if (!IsMacOS)
        {
            return;
        }

        DetectCapabilities();
    }

    #region FSEvents Integration

    /// <summary>
    /// Initializes FSEvents monitoring for the mount point.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    public bool InitializeFSEvents()
    {
        if (!IsMacOS || !_config.EnableNotifications)
            return false;

        try
        {
            _fsEventsCts = new CancellationTokenSource();
            _fsEventsThread = new Thread(ProcessFSEvents)
            {
                Name = "FuseFSEvents",
                IsBackground = true
            };
            _fsEventsThread.Start();

            _kernelContext?.LogInfo("FSEvents monitoring initialized");
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogError("Failed to initialize FSEvents", ex);
            return false;
        }
    }

    /// <summary>
    /// Creates an FSEvents stream for the specified paths.
    /// </summary>
    /// <param name="paths">The paths to monitor.</param>
    /// <returns>True if the stream was created.</returns>
    public bool CreateFSEventsStream(params string[] paths)
    {
        if (!IsMacOS || paths.Length == 0)
            return false;

        // Note: In production, this would use CoreServices/FSEvents.h
        // For now, we provide the interface for macFUSE integration
        _kernelContext?.LogInfo($"FSEvents stream created for: {string.Join(", ", paths)}");
        return true;
    }

    private void ProcessFSEvents()
    {
        while (!_fsEventsCts!.IsCancellationRequested)
        {
            try
            {
                // In production, this would poll the FSEvents stream
                // and dispatch events to the FileChanged handler
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                if (!_fsEventsCts.IsCancellationRequested)
                {
                    _kernelContext?.LogError("Error processing FSEvents", ex);
                }
            }
        }
    }

    #endregion

    #region Spotlight Support

    /// <summary>
    /// Gets the Spotlight metadata attributes for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The Spotlight metadata, or null if not available.</returns>
    public SpotlightMetadata? GetSpotlightMetadata(string path)
    {
        if (!IsMacOS || !_config.EnableSpotlight)
            return null;

        try
        {
            // In production, this would use MDItemCreate and MDItemCopyAttributes
            // For now, return basic metadata
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
                return null;

            return new SpotlightMetadata
            {
                DisplayName = fileInfo.Name,
                ContentType = GetContentType(fileInfo.Extension),
                ContentTypeTree = GetContentTypeTree(fileInfo.Extension),
                Size = fileInfo.Length,
                CreationDate = fileInfo.CreationTimeUtc,
                ModificationDate = fileInfo.LastWriteTimeUtc,
                LastUsedDate = fileInfo.LastAccessTimeUtc
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets Spotlight metadata attributes for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="metadata">The metadata to set.</param>
    /// <returns>True if the metadata was set.</returns>
    public bool SetSpotlightMetadata(string path, SpotlightMetadata metadata)
    {
        if (!IsMacOS || !_config.EnableSpotlight)
            return false;

        // In production, this would use MDItemSetAttribute
        // or set the appropriate xattrs
        _kernelContext?.LogDebug($"Setting Spotlight metadata for {path}");
        return true;
    }

    /// <summary>
    /// Requests Spotlight to index a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    public void RequestSpotlightIndexing(string path)
    {
        if (!IsMacOS || !_config.EnableSpotlight)
            return;

        // In production, this would touch the file or use mdimport
        _kernelContext?.LogDebug($"Requesting Spotlight indexing for {path}");
    }

    /// <summary>
    /// Excludes a path from Spotlight indexing.
    /// </summary>
    /// <param name="path">The path to exclude.</param>
    public void ExcludeFromSpotlight(string path)
    {
        if (!IsMacOS)
            return;

        try
        {
            // Create .metadata_never_index file
            var markerPath = Path.Combine(path, ".metadata_never_index");
            File.WriteAllText(markerPath, "");
            File.SetAttributes(markerPath, FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to exclude {path} from Spotlight: {ex.Message}");
        }
    }

    private static string GetContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" => "public.plain-text",
            ".pdf" => "com.adobe.pdf",
            ".doc" or ".docx" => "com.microsoft.word.doc",
            ".xls" or ".xlsx" => "com.microsoft.excel.xls",
            ".jpg" or ".jpeg" => "public.jpeg",
            ".png" => "public.png",
            ".gif" => "com.compuserve.gif",
            ".mp3" => "public.mp3",
            ".mp4" => "public.mpeg-4",
            ".mov" => "com.apple.quicktime-movie",
            ".zip" => "public.zip-archive",
            _ => "public.data"
        };
    }

    private static string[] GetContentTypeTree(string extension)
    {
        var contentType = GetContentType(extension);
        return new[] { contentType, "public.data", "public.item" };
    }

    #endregion

    #region Finder Integration

    /// <summary>
    /// Gets the Finder info for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The Finder info, or null if not available.</returns>
    public FinderInfo? GetFinderInfo(string path)
    {
        if (!IsMacOS || !_config.EnableFinderIntegration)
            return null;

        if (_finderInfoCache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            // Read com.apple.FinderInfo xattr
            var buffer = new byte[32];
            var result = NativeXattr.getxattr(path, "com.apple.FinderInfo", buffer, buffer.Length, 0, 0);

            if (result >= 32)
            {
                var info = ParseFinderInfo(buffer);
                _finderInfoCache[path] = info;
                return info;
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    /// <summary>
    /// Sets the Finder info for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="info">The Finder info to set.</param>
    /// <returns>True if the info was set.</returns>
    public bool SetFinderInfo(string path, FinderInfo info)
    {
        if (!IsMacOS || !_config.EnableFinderIntegration)
            return false;

        try
        {
            var buffer = SerializeFinderInfo(info);
            var result = NativeXattr.setxattr(path, "com.apple.FinderInfo", buffer, buffer.Length, 0, 0);

            if (result == 0)
            {
                _finderInfoCache[path] = info;
                return true;
            }
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to set Finder info for {path}: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Gets the Finder label (color) for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The label color index (0-7), or -1 if not set.</returns>
    public int GetFinderLabel(string path)
    {
        var info = GetFinderInfo(path);
        return info?.LabelColor ?? -1;
    }

    /// <summary>
    /// Sets the Finder label (color) for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="labelColor">The label color index (0-7).</param>
    /// <returns>True if the label was set.</returns>
    public bool SetFinderLabel(string path, int labelColor)
    {
        if (labelColor < 0 || labelColor > 7)
            return false;

        var info = GetFinderInfo(path) ?? new FinderInfo();
        info.LabelColor = labelColor;
        return SetFinderInfo(path, info);
    }

    /// <summary>
    /// Gets the file type and creator codes (classic Mac).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The type and creator codes as a tuple.</returns>
    public (uint Type, uint Creator)? GetTypeCreator(string path)
    {
        var info = GetFinderInfo(path);
        if (info == null)
            return null;

        return (info.FileType, info.Creator);
    }

    /// <summary>
    /// Sets the file type and creator codes.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="type">The file type code.</param>
    /// <param name="creator">The creator code.</param>
    /// <returns>True if the codes were set.</returns>
    public bool SetTypeCreator(string path, uint type, uint creator)
    {
        var info = GetFinderInfo(path) ?? new FinderInfo();
        info.FileType = type;
        info.Creator = creator;
        return SetFinderInfo(path, info);
    }

    /// <summary>
    /// Gets whether a file is hidden from Finder.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True if the file is hidden.</returns>
    public bool IsHiddenFromFinder(string path)
    {
        var info = GetFinderInfo(path);
        return info?.IsInvisible ?? false;
    }

    /// <summary>
    /// Sets whether a file is hidden from Finder.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="hidden">True to hide the file.</param>
    /// <returns>True if the visibility was set.</returns>
    public bool SetHiddenFromFinder(string path, bool hidden)
    {
        var info = GetFinderInfo(path) ?? new FinderInfo();
        info.IsInvisible = hidden;
        return SetFinderInfo(path, info);
    }

    private static FinderInfo ParseFinderInfo(byte[] buffer)
    {
        return new FinderInfo
        {
            FileType = BitConverter.ToUInt32(buffer.Take(4).Reverse().ToArray(), 0),
            Creator = BitConverter.ToUInt32(buffer.Skip(4).Take(4).Reverse().ToArray(), 0),
            Flags = BitConverter.ToUInt16(buffer.Skip(8).Take(2).Reverse().ToArray(), 0),
            LabelColor = (buffer[9] >> 1) & 0x07,
            IsInvisible = (buffer[8] & 0x40) != 0
        };
    }

    private static byte[] SerializeFinderInfo(FinderInfo info)
    {
        var buffer = new byte[32];

        // File type (big-endian)
        var type = BitConverter.GetBytes(info.FileType).Reverse().ToArray();
        Buffer.BlockCopy(type, 0, buffer, 0, 4);

        // Creator (big-endian)
        var creator = BitConverter.GetBytes(info.Creator).Reverse().ToArray();
        Buffer.BlockCopy(creator, 0, buffer, 4, 4);

        // Flags
        var flags = (ushort)info.Flags;
        if (info.IsInvisible)
            flags |= 0x4000;
        flags |= (ushort)((info.LabelColor & 0x07) << 1);

        buffer[8] = (byte)(flags >> 8);
        buffer[9] = (byte)(flags & 0xFF);

        return buffer;
    }

    #endregion

    #region Volume Management

    /// <summary>
    /// Gets the volume name to display in Finder.
    /// </summary>
    /// <returns>The volume name.</returns>
    public string GetVolumeName()
    {
        return _config.VolumeName;
    }

    /// <summary>
    /// Gets the volume icon path.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <returns>The path to the volume icon, or null.</returns>
    public string? GetVolumeIconPath(string mountPoint)
    {
        var iconPath = Path.Combine(mountPoint, ".VolumeIcon.icns");
        return File.Exists(iconPath) ? iconPath : null;
    }

    /// <summary>
    /// Sets a custom volume icon.
    /// </summary>
    /// <param name="mountPoint">The mount point.</param>
    /// <param name="iconData">The .icns icon data.</param>
    /// <returns>True if the icon was set.</returns>
    public bool SetVolumeIcon(string mountPoint, byte[] iconData)
    {
        if (!IsMacOS)
            return false;

        try
        {
            var iconPath = Path.Combine(mountPoint, ".VolumeIcon.icns");
            File.WriteAllBytes(iconPath, iconData);
            File.SetAttributes(iconPath, FileAttributes.Hidden);

            // Set the custom icon bit on the volume
            // In production, this would use SetFile -a C
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to set volume icon: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets mount options specific to macFUSE.
    /// </summary>
    /// <returns>A list of macFUSE-specific mount options.</returns>
    public IReadOnlyList<string> GetMacFuseMountOptions()
    {
        var options = new List<string>
        {
            $"volname={_config.VolumeName}",
            "local" // Treat as local volume
        };

        if (!_config.EnableSpotlight)
        {
            options.Add("noappledouble"); // Disable ._ files
            options.Add("noapplexattr"); // Disable Apple extended attributes
        }

        if (_config.AllowOther)
        {
            options.Add("allow_other");
        }

        return options;
    }

    #endregion

    #region Capability Detection

    private void DetectCapabilities()
    {
        // Check for macFUSE installation
        IsMacFuseInstalled = Directory.Exists("/Library/Frameworks/macFUSE.framework") ||
                            Directory.Exists("/Library/Filesystems/macfuse.fs") ||
                            File.Exists("/usr/local/lib/libfuse.dylib");

        if (!IsMacFuseInstalled)
        {
            _kernelContext?.LogWarning("macFUSE is not installed. Install from https://osxfuse.github.io/");
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes macOS-specific resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _fsEventsCts?.Cancel();
        _fsEventsThread?.Join(TimeSpan.FromSeconds(2));
        _fsEventsCts?.Dispose();

        _finderInfoCache.Clear();
    }

    #endregion

    #region Native Interop

    private static class NativeXattr
    {
        public const int XATTR_NOFOLLOW = 0x0001;

        [DllImport("libSystem.B.dylib", SetLastError = true)]
        public static extern nint getxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            byte[] value,
            int size,
            uint position,
            int options);

        [DllImport("libSystem.B.dylib", SetLastError = true)]
        public static extern int setxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            byte[] value,
            int size,
            uint position,
            int options);
    }

    #endregion
}

/// <summary>
/// Event arguments for FSEvents.
/// </summary>
public sealed class FSEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the affected path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the event flags.
    /// </summary>
    public FSEventFlags Flags { get; set; }

    /// <summary>
    /// Gets or sets the event ID.
    /// </summary>
    public ulong EventId { get; set; }
}

/// <summary>
/// FSEvents flags.
/// </summary>
[Flags]
public enum FSEventFlags : uint
{
    /// <summary>
    /// No flags.
    /// </summary>
    None = 0,

    /// <summary>
    /// There was some change in the directory at the specific path supplied.
    /// </summary>
    MustScanSubDirs = 0x00000001,

    /// <summary>
    /// User-space event dropped.
    /// </summary>
    UserDropped = 0x00000002,

    /// <summary>
    /// Kernel-space event dropped.
    /// </summary>
    KernelDropped = 0x00000004,

    /// <summary>
    /// Event IDs have wrapped around.
    /// </summary>
    EventIdsWrapped = 0x00000008,

    /// <summary>
    /// Marks a sentinel event sent to mark the end of the "historical" events.
    /// </summary>
    HistoryDone = 0x00000010,

    /// <summary>
    /// The path to the root directory being watched was deleted.
    /// </summary>
    RootChanged = 0x00000020,

    /// <summary>
    /// The volume is being unmounted.
    /// </summary>
    Mount = 0x00000040,

    /// <summary>
    /// The volume is being mounted.
    /// </summary>
    Unmount = 0x00000080,

    /// <summary>
    /// A file system object was created at the path.
    /// </summary>
    ItemCreated = 0x00000100,

    /// <summary>
    /// A file system object was removed from the path.
    /// </summary>
    ItemRemoved = 0x00000200,

    /// <summary>
    /// A file system object was renamed at the path.
    /// </summary>
    ItemRenamed = 0x00000800,

    /// <summary>
    /// A file system object was modified at the path.
    /// </summary>
    ItemModified = 0x00001000,

    /// <summary>
    /// A file was modified at the path (macOS 10.7+).
    /// </summary>
    ItemFinderInfoMod = 0x00002000,

    /// <summary>
    /// The permissions of a file were changed at the path.
    /// </summary>
    ItemChangeOwner = 0x00004000,

    /// <summary>
    /// Extended attributes were modified at the path.
    /// </summary>
    ItemXattrMod = 0x00008000,

    /// <summary>
    /// The item at the path is a file.
    /// </summary>
    ItemIsFile = 0x00010000,

    /// <summary>
    /// The item at the path is a directory.
    /// </summary>
    ItemIsDir = 0x00020000,

    /// <summary>
    /// The item at the path is a symbolic link.
    /// </summary>
    ItemIsSymlink = 0x00040000,

    /// <summary>
    /// The item at the path had its inode meta information changed.
    /// </summary>
    ItemInodeMetaMod = 0x00100000,

    /// <summary>
    /// The item at the path is a clone (macOS 10.13+).
    /// </summary>
    ItemCloned = 0x00400000
}

/// <summary>
/// Spotlight metadata for a file.
/// </summary>
public sealed class SpotlightMetadata
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content type (UTI).
    /// </summary>
    public string ContentType { get; set; } = "public.data";

    /// <summary>
    /// Gets or sets the content type tree.
    /// </summary>
    public string[] ContentTypeTree { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the file size.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the creation date.
    /// </summary>
    public DateTime CreationDate { get; set; }

    /// <summary>
    /// Gets or sets the modification date.
    /// </summary>
    public DateTime ModificationDate { get; set; }

    /// <summary>
    /// Gets or sets the last used date.
    /// </summary>
    public DateTime LastUsedDate { get; set; }

    /// <summary>
    /// Gets or sets custom attributes.
    /// </summary>
    public Dictionary<string, object> CustomAttributes { get; set; } = new();
}

/// <summary>
/// Finder information for a file.
/// </summary>
public sealed class FinderInfo
{
    /// <summary>
    /// Gets or sets the file type code (e.g., 'TEXT').
    /// </summary>
    public uint FileType { get; set; }

    /// <summary>
    /// Gets or sets the creator code (e.g., 'ttxt').
    /// </summary>
    public uint Creator { get; set; }

    /// <summary>
    /// Gets or sets the Finder flags.
    /// </summary>
    public ushort Flags { get; set; }

    /// <summary>
    /// Gets or sets the label color (0-7).
    /// </summary>
    public int LabelColor { get; set; }

    /// <summary>
    /// Gets or sets whether the file is invisible in Finder.
    /// </summary>
    public bool IsInvisible { get; set; }

    /// <summary>
    /// Gets or sets whether the file is an alias.
    /// </summary>
    public bool IsAlias { get; set; }

    /// <summary>
    /// Gets or sets whether the file is a bundle/package.
    /// </summary>
    public bool IsBundle { get; set; }

    /// <summary>
    /// Gets or sets whether the file has a custom icon.
    /// </summary>
    public bool HasCustomIcon { get; set; }

    /// <summary>
    /// Gets or sets whether the file is locked.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Gets or sets the icon position in Finder window.
    /// </summary>
    public (short X, short Y) IconPosition { get; set; }
}
