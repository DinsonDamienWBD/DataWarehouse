using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Windows Shell integration for the WinFSP filesystem.
/// Provides overlay icons for sync status, context menu handlers, and property sheet handlers.
/// </summary>
public sealed class ShellExtension : IDisposable
{
    private readonly ShellConfig _config;
    private readonly WinFspFileSystem _fileSystem;
    private readonly Guid _overlayGuid = new("4B8E9D3A-7F2C-4E1B-A9D6-5C8F0E2B1A3D");
    private readonly Guid _contextMenuGuid = new("6D4F2A1E-8C3B-4D9E-B5A2-7E6C1F0D8B4A");
    private readonly Guid _propertySheetGuid = new("9A2E5C8F-1D4B-4E7A-C3B6-0F8D9E2A5C1B");
    private bool _overlaysRegistered;
    private bool _contextMenusRegistered;
    private bool _propertySheetsRegistered;
    private bool _disposed;

    /// <summary>
    /// Initializes the shell extension.
    /// </summary>
    /// <param name="config">Shell configuration.</param>
    /// <param name="fileSystem">The underlying filesystem.</param>
    public ShellExtension(ShellConfig config, WinFspFileSystem fileSystem)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    #region Registration

    /// <summary>
    /// Registers all enabled shell extensions.
    /// Requires administrator privileges.
    /// </summary>
    /// <returns>True if all registrations succeeded.</returns>
    public bool RegisterAll()
    {
        var success = true;

        if (_config.OverlayIcons)
        {
            success &= RegisterOverlayIcons();
        }

        if (_config.ContextMenus)
        {
            success &= RegisterContextMenus();
        }

        if (_config.PropertySheets)
        {
            success &= RegisterPropertySheets();
        }

        if (_config.SearchIndexing)
        {
            success &= RegisterSearchIndexer();
        }

        return success;
    }

    /// <summary>
    /// Unregisters all shell extensions.
    /// </summary>
    public void UnregisterAll()
    {
        if (_overlaysRegistered)
            UnregisterOverlayIcons();

        if (_contextMenusRegistered)
            UnregisterContextMenus();

        if (_propertySheetsRegistered)
            UnregisterPropertySheets();

        UnregisterSearchIndexer();
    }

    #endregion

    #region Overlay Icons

    /// <summary>
    /// Sync status for overlay icons.
    /// </summary>
    public enum SyncStatus
    {
        /// <summary>
        /// File is fully synced.
        /// </summary>
        Synced,

        /// <summary>
        /// File is being synced.
        /// </summary>
        Syncing,

        /// <summary>
        /// File has sync errors.
        /// </summary>
        Error,

        /// <summary>
        /// File is offline/cloud-only.
        /// </summary>
        CloudOnly,

        /// <summary>
        /// File has pending changes.
        /// </summary>
        Pending
    }

    /// <summary>
    /// Gets the sync status for a file path.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>Current sync status.</returns>
    public SyncStatus GetSyncStatus(string path)
    {
        // Check if path is within our filesystem
        var mountPoint = _fileSystem.MountPoint;
        if (string.IsNullOrEmpty(mountPoint) ||
            !path.StartsWith(mountPoint, StringComparison.OrdinalIgnoreCase))
        {
            return SyncStatus.Synced;
        }

        // Get file entry
        var relativePath = path.Substring(mountPoint.Length);
        var entry = _fileSystem.GetEntry(relativePath);

        if (entry == null)
            return SyncStatus.Error;

        // Check file attributes for sync status
        // In a real implementation, this would check actual sync state
        if ((entry.Attributes & WinFspNative.FileAttributes.Offline) != 0)
            return SyncStatus.CloudOnly;

        if ((entry.Attributes & WinFspNative.FileAttributes.RecallOnDataAccess) != 0)
            return SyncStatus.CloudOnly;

        return SyncStatus.Synced;
    }

    /// <summary>
    /// Gets the overlay icon index for a sync status.
    /// </summary>
    /// <param name="status">The sync status.</param>
    /// <returns>Icon index, or -1 for no overlay.</returns>
    public int GetOverlayIconIndex(SyncStatus status)
    {
        return status switch
        {
            SyncStatus.Synced => 0,     // Green checkmark
            SyncStatus.Syncing => 1,    // Blue sync arrows
            SyncStatus.Error => 2,      // Red X
            SyncStatus.CloudOnly => 3,  // Cloud icon
            SyncStatus.Pending => 4,    // Yellow clock
            _ => -1
        };
    }

    private bool RegisterOverlayIcons()
    {
        try
        {
            // Register overlay icon handlers
            // Format: HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers

            var overlayKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

            // Register each overlay (prefix with spaces for priority)
            var overlays = new[]
            {
                ("  DataWarehouseSynced", SyncStatus.Synced),
                ("  DataWarehouseSyncing", SyncStatus.Syncing),
                ("  DataWarehouseError", SyncStatus.Error),
                ("  DataWarehouseCloud", SyncStatus.CloudOnly),
                ("  DataWarehousePending", SyncStatus.Pending)
            };

            using var baseKey = Registry.LocalMachine.OpenSubKey(overlayKeyPath, true);
            if (baseKey == null)
                return false;

            foreach (var (name, status) in overlays)
            {
                using var overlayKey = baseKey.CreateSubKey(name);
                if (overlayKey == null)
                    continue;

                // Set CLSID of handler
                var clsid = GetOverlayClsid(status);
                overlayKey.SetValue(null, clsid);
            }

            // Register the COM server (simplified - real implementation needs full COM registration)
            RegisterOverlayComServer();

            _overlaysRegistered = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UnregisterOverlayIcons()
    {
        try
        {
            var overlayKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

            using var baseKey = Registry.LocalMachine.OpenSubKey(overlayKeyPath, true);
            if (baseKey == null)
                return;

            var subKeys = baseKey.GetSubKeyNames()
                .Where(n => n.Contains("DataWarehouse", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var name in subKeys)
            {
                baseKey.DeleteSubKey(name, false);
            }

            UnregisterOverlayComServer();
            _overlaysRegistered = false;
        }
        catch
        {
            // Ignore unregistration errors
        }
    }

    private string GetOverlayClsid(SyncStatus status)
    {
        // Generate unique CLSID based on status
        var baseGuid = _overlayGuid;
        var bytes = baseGuid.ToByteArray();
        bytes[15] = (byte)status;
        return new Guid(bytes).ToString("B");
    }

    private void RegisterOverlayComServer()
    {
        // This would register the overlay icon handler COM server
        // Real implementation requires implementing IShellIconOverlayIdentifier
    }

    private void UnregisterOverlayComServer()
    {
        // Unregister COM server
    }

    #endregion

    #region Context Menus

    /// <summary>
    /// Context menu item definition.
    /// </summary>
    public sealed class ContextMenuItem
    {
        /// <summary>
        /// Menu item ID.
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Display text.
        /// </summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>
        /// Help text shown in status bar.
        /// </summary>
        public string? HelpText { get; init; }

        /// <summary>
        /// Icon path or resource.
        /// </summary>
        public string? IconPath { get; init; }

        /// <summary>
        /// Whether this item is enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Sub-menu items.
        /// </summary>
        public List<ContextMenuItem>? SubItems { get; init; }
    }

    /// <summary>
    /// Gets context menu items for a file path.
    /// </summary>
    /// <param name="paths">Selected file paths.</param>
    /// <returns>List of menu items to display.</returns>
    public IEnumerable<ContextMenuItem> GetContextMenuItems(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            yield break;

        // DataWarehouse main menu
        yield return new ContextMenuItem
        {
            Id = "datawarehouse",
            Text = "DataWarehouse",
            SubItems = new List<ContextMenuItem>
            {
                new()
                {
                    Id = "dw_sync",
                    Text = "Sync Now",
                    HelpText = "Synchronize selected files immediately"
                },
                new()
                {
                    Id = "dw_offline",
                    Text = "Make Available Offline",
                    HelpText = "Download files for offline access"
                },
                new()
                {
                    Id = "dw_online",
                    Text = "Free Up Space",
                    HelpText = "Remove local copy, keep in cloud"
                },
                new()
                {
                    Id = "separator1",
                    Text = "-"
                },
                new()
                {
                    Id = "dw_versions",
                    Text = "View Previous Versions",
                    HelpText = "Show version history"
                },
                new()
                {
                    Id = "dw_properties",
                    Text = "DataWarehouse Properties",
                    HelpText = "View DataWarehouse metadata"
                }
            }
        };
    }

    /// <summary>
    /// Handles a context menu command.
    /// </summary>
    /// <param name="commandId">The command ID.</param>
    /// <param name="paths">Selected file paths.</param>
    /// <returns>True if command was handled.</returns>
    public bool HandleContextMenuCommand(string commandId, IEnumerable<string> paths)
    {
        var pathList = paths.ToList();

        return commandId switch
        {
            "dw_sync" => HandleSyncNow(pathList),
            "dw_offline" => HandleMakeOffline(pathList),
            "dw_online" => HandleFreeUpSpace(pathList),
            "dw_versions" => HandleViewVersions(pathList),
            "dw_properties" => HandleViewProperties(pathList),
            _ => false
        };
    }

    private bool HandleSyncNow(List<string> paths)
    {
        // Trigger immediate sync for selected files
        return true;
    }

    private bool HandleMakeOffline(List<string> paths)
    {
        // Download files for offline access
        foreach (var path in paths)
        {
            var relativePath = GetRelativePath(path);
            var entry = _fileSystem.GetEntry(relativePath);
            if (entry != null)
            {
                // Clear offline/cloud-only attributes
                entry.Attributes &= ~(WinFspNative.FileAttributes.Offline | WinFspNative.FileAttributes.RecallOnDataAccess);
            }
        }
        return true;
    }

    private bool HandleFreeUpSpace(List<string> paths)
    {
        // Mark files as cloud-only
        foreach (var path in paths)
        {
            var relativePath = GetRelativePath(path);
            var entry = _fileSystem.GetEntry(relativePath);
            if (entry != null)
            {
                entry.Attributes |= WinFspNative.FileAttributes.Offline;
            }
        }
        return true;
    }

    private bool HandleViewVersions(List<string> paths)
    {
        // Open version history dialog
        return true;
    }

    private bool HandleViewProperties(List<string> paths)
    {
        // Open properties dialog
        return true;
    }

    private bool RegisterContextMenus()
    {
        try
        {
            // Register context menu handler
            // HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers\DataWarehouse

            using var key = Registry.ClassesRoot.CreateSubKey(@"*\shellex\ContextMenuHandlers\DataWarehouse");
            if (key == null)
                return false;

            key.SetValue(null, _contextMenuGuid.ToString("B"));

            // Register for directories
            using var dirKey = Registry.ClassesRoot.CreateSubKey(@"Directory\shellex\ContextMenuHandlers\DataWarehouse");
            dirKey?.SetValue(null, _contextMenuGuid.ToString("B"));

            // Register for directory background
            using var bgKey = Registry.ClassesRoot.CreateSubKey(@"Directory\Background\shellex\ContextMenuHandlers\DataWarehouse");
            bgKey?.SetValue(null, _contextMenuGuid.ToString("B"));

            // Register COM server
            RegisterContextMenuComServer();

            _contextMenusRegistered = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UnregisterContextMenus()
    {
        try
        {
            Registry.ClassesRoot.DeleteSubKey(@"*\shellex\ContextMenuHandlers\DataWarehouse", false);
            Registry.ClassesRoot.DeleteSubKey(@"Directory\shellex\ContextMenuHandlers\DataWarehouse", false);
            Registry.ClassesRoot.DeleteSubKey(@"Directory\Background\shellex\ContextMenuHandlers\DataWarehouse", false);

            UnregisterContextMenuComServer();
            _contextMenusRegistered = false;
        }
        catch
        {
        }
    }

    private void RegisterContextMenuComServer()
    {
        // Register IContextMenu COM server
    }

    private void UnregisterContextMenuComServer()
    {
        // Unregister COM server
    }

    #endregion

    #region Property Sheets

    /// <summary>
    /// Gets property sheet data for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>Property sheet information.</returns>
    public PropertySheetData? GetPropertySheet(string path)
    {
        var relativePath = GetRelativePath(path);
        var entry = _fileSystem.GetEntry(relativePath);

        if (entry == null)
            return null;

        return new PropertySheetData
        {
            Path = path,
            DisplayName = Path.GetFileName(path),
            FileSize = entry.FileSize,
            AllocationSize = (long)entry.AllocationSize,
            Attributes = entry.Attributes.ToString(),
            CreationTime = WinFspNative.FileTimeToDateTime(entry.CreationTime),
            LastAccessTime = WinFspNative.FileTimeToDateTime(entry.LastAccessTime),
            LastWriteTime = WinFspNative.FileTimeToDateTime(entry.LastWriteTime),
            SyncStatus = GetSyncStatus(path).ToString(),
            IndexNumber = entry.IndexNumber
        };
    }

    private bool RegisterPropertySheets()
    {
        try
        {
            // Register property sheet handler
            using var key = Registry.ClassesRoot.CreateSubKey(@"*\shellex\PropertySheetHandlers\DataWarehouse");
            if (key == null)
                return false;

            key.SetValue(null, _propertySheetGuid.ToString("B"));

            RegisterPropertySheetComServer();
            _propertySheetsRegistered = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UnregisterPropertySheets()
    {
        try
        {
            Registry.ClassesRoot.DeleteSubKey(@"*\shellex\PropertySheetHandlers\DataWarehouse", false);
            UnregisterPropertySheetComServer();
            _propertySheetsRegistered = false;
        }
        catch
        {
        }
    }

    private void RegisterPropertySheetComServer()
    {
        // Register IShellPropSheetExt COM server
    }

    private void UnregisterPropertySheetComServer()
    {
        // Unregister COM server
    }

    #endregion

    #region Search Indexing

    private bool RegisterSearchIndexer()
    {
        if (!_config.SearchIndexing)
            return true;

        try
        {
            // Register with Windows Search
            // This requires implementing IFilter and/or ISearchProtocol
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UnregisterSearchIndexer()
    {
        // Unregister from Windows Search
    }

    #endregion

    #region Helper Methods

    private string GetRelativePath(string fullPath)
    {
        var mountPoint = _fileSystem.MountPoint;
        if (string.IsNullOrEmpty(mountPoint))
            return fullPath;

        if (fullPath.StartsWith(mountPoint, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath.Substring(mountPoint.Length);
            if (!relative.StartsWith("\\"))
                relative = "\\" + relative;
            return relative;
        }

        return fullPath;
    }

    /// <summary>
    /// Notifies the shell that icons need to be refreshed.
    /// </summary>
    public static void RefreshShellIcons()
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the shell extension.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Note: We don't unregister on dispose as that requires admin rights
        // and registrations should persist across restarts
        _disposed = true;
    }

    #endregion
}

/// <summary>
/// Data for property sheet display.
/// </summary>
public sealed class PropertySheetData
{
    /// <summary>
    /// Full file path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Allocation size in bytes.
    /// </summary>
    public long AllocationSize { get; init; }

    /// <summary>
    /// File attributes as string.
    /// </summary>
    public string Attributes { get; init; } = string.Empty;

    /// <summary>
    /// Creation time.
    /// </summary>
    public DateTime CreationTime { get; init; }

    /// <summary>
    /// Last access time.
    /// </summary>
    public DateTime LastAccessTime { get; init; }

    /// <summary>
    /// Last write time.
    /// </summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>
    /// Current sync status.
    /// </summary>
    public string SyncStatus { get; init; } = string.Empty;

    /// <summary>
    /// Unique index number.
    /// </summary>
    public ulong IndexNumber { get; init; }
}
