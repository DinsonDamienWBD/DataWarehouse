using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Core WinFSP filesystem implementation.
/// Maps WinFSP operations to DataWarehouse storage operations.
/// Handles Windows-specific attributes, security descriptors, and file system semantics.
/// </summary>
public sealed class WinFspFileSystem : IDisposable
{
    private readonly WinFspConfig _config;
    private readonly IStorageProvider? _storageProvider;
    private readonly ConcurrentDictionary<string, FileEntry> _entries;
    private readonly ConcurrentDictionary<string, byte[]> _fileData;
    private readonly ReaderWriterLockSlim _entriesLock;
    private IntPtr _fileSystemHandle;
    private WinFspNative.FspFileSystemInterface _interface;
    private GCHandle _interfaceHandle;
    private bool _mounted;
    private bool _disposed;

    /// <summary>
    /// Initializes a new WinFSP filesystem instance.
    /// </summary>
    /// <param name="config">Configuration options.</param>
    /// <param name="storageProvider">Optional backend storage provider.</param>
    public WinFspFileSystem(WinFspConfig config, IStorageProvider? storageProvider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _storageProvider = storageProvider;
        _entries = new ConcurrentDictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        _fileData = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        _entriesLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        // Initialize root directory
        InitializeRootDirectory();
    }

    /// <summary>
    /// Gets whether the filesystem is currently mounted.
    /// </summary>
    public bool IsMounted => _mounted;

    /// <summary>
    /// Gets the current mount point.
    /// </summary>
    public string? MountPoint { get; private set; }

    /// <summary>
    /// Event raised when a file operation occurs.
    /// </summary>
    public event EventHandler<FileOperationEventArgs>? FileOperationOccurred;

    #region Mount/Unmount Operations

    /// <summary>
    /// Mounts the filesystem.
    /// </summary>
    /// <returns>True if mounted successfully.</returns>
    public bool Mount()
    {
        if (_mounted)
            return true;

        try
        {
            // Determine mount point
            var mountPoint = GetMountPoint();
            if (string.IsNullOrEmpty(mountPoint))
                throw new InvalidOperationException("No valid mount point available.");

            // Create mount point directory if needed
            if (!mountPoint.Contains(':') && _config.CreateMountPoint && !Directory.Exists(mountPoint))
            {
                Directory.CreateDirectory(mountPoint);
            }

            // Set up filesystem parameters
            var volumeParams = CreateVolumeParams();

            // Set up interface callbacks
            _interface = CreateInterface();
            _interfaceHandle = GCHandle.Alloc(_interface, GCHandleType.Pinned);

            // Create WinFSP filesystem
            var result = WinFspNative.FspFileSystemCreate(
                @"\\.\" + _config.FileSystemName,
                ref volumeParams,
                ref _interface,
                out _fileSystemHandle);

            if (!WinFspNative.IsSuccess(result))
            {
                throw new InvalidOperationException($"Failed to create filesystem: 0x{result:X8}");
            }

            // Set mount point
            result = WinFspNative.FspFileSystemSetMountPoint(_fileSystemHandle, mountPoint);
            if (!WinFspNative.IsSuccess(result))
            {
                WinFspNative.FspFileSystemDelete(_fileSystemHandle);
                throw new InvalidOperationException($"Failed to set mount point: 0x{result:X8}");
            }

            // Start dispatcher
            result = WinFspNative.FspFileSystemStartDispatcher(_fileSystemHandle, 0);
            if (!WinFspNative.IsSuccess(result))
            {
                WinFspNative.FspFileSystemRemoveMountPoint(_fileSystemHandle);
                WinFspNative.FspFileSystemDelete(_fileSystemHandle);
                throw new InvalidOperationException($"Failed to start dispatcher: 0x{result:X8}");
            }

            MountPoint = mountPoint;
            _mounted = true;

            OnFileOperationOccurred(new FileOperationEventArgs("Mount", mountPoint, true));

            return true;
        }
        catch (Exception ex)
        {
            OnFileOperationOccurred(new FileOperationEventArgs("Mount", _config.MountPoint ?? _config.DriveLetter ?? "unknown", false, ex.Message));
            throw;
        }
    }

    /// <summary>
    /// Unmounts the filesystem.
    /// </summary>
    public void Unmount()
    {
        if (!_mounted)
            return;

        try
        {
            WinFspNative.FspFileSystemStopDispatcher(_fileSystemHandle);
            WinFspNative.FspFileSystemRemoveMountPoint(_fileSystemHandle);
            WinFspNative.FspFileSystemDelete(_fileSystemHandle);

            if (_interfaceHandle.IsAllocated)
                _interfaceHandle.Free();

            _mounted = false;
            var mp = MountPoint;
            MountPoint = null;

            OnFileOperationOccurred(new FileOperationEventArgs("Unmount", mp ?? "unknown", true));
        }
        catch (Exception ex)
        {
            OnFileOperationOccurred(new FileOperationEventArgs("Unmount", MountPoint ?? "unknown", false, ex.Message));
            throw;
        }
    }

    #endregion

    #region Volume Operations

    /// <summary>
    /// Gets volume information.
    /// </summary>
    public WinFspNative.VolumeInfo GetVolumeInfo()
    {
        long totalSize = _config.TotalSize > 0 ? _config.TotalSize : 1024L * 1024 * 1024 * 100; // Default 100GB
        long freeSize = _config.FreeSize > 0 ? _config.FreeSize : totalSize - GetUsedSpace();

        return new WinFspNative.VolumeInfo
        {
            TotalSize = (ulong)totalSize,
            FreeSize = (ulong)Math.Max(0, freeSize),
            VolumeLabelLength = (ushort)(_config.VolumeLabel.Length * 2),
            VolumeLabel = _config.VolumeLabel
        };
    }

    /// <summary>
    /// Sets the volume label.
    /// </summary>
    public void SetVolumeLabel(string label)
    {
        // Note: In a real implementation, this would persist the label
        // For now, the config is read-only after initialization
    }

    #endregion

    #region Entry Operations

    /// <summary>
    /// Checks if a path exists.
    /// </summary>
    public bool PathExists(string path)
    {
        var normalizedPath = NormalizePath(path);
        return _entries.ContainsKey(normalizedPath);
    }

    /// <summary>
    /// Gets an entry by path.
    /// </summary>
    public FileEntry? GetEntry(string path)
    {
        var normalizedPath = NormalizePath(path);
        return _entries.TryGetValue(normalizedPath, out var entry) ? entry : null;
    }

    /// <summary>
    /// Adds a new entry.
    /// </summary>
    public void AddEntry(FileEntry entry)
    {
        var normalizedPath = NormalizePath(entry.Path);
        _entries[normalizedPath] = entry with { Path = normalizedPath };

        if (!entry.IsDirectory)
        {
            _fileData[normalizedPath] = Array.Empty<byte>();
        }

        OnFileOperationOccurred(new FileOperationEventArgs(
            entry.IsDirectory ? "CreateDirectory" : "CreateFile",
            normalizedPath,
            true));
    }

    /// <summary>
    /// Deletes an entry.
    /// </summary>
    public bool DeleteEntry(string path)
    {
        var normalizedPath = NormalizePath(path);

        _entriesLock.EnterWriteLock();
        try
        {
            if (!_entries.TryRemove(normalizedPath, out var entry))
                return false;

            if (!entry.IsDirectory)
            {
                _fileData.TryRemove(normalizedPath, out _);
            }
            else
            {
                // Delete all children (recursive)
                var prefix = normalizedPath + "\\";
                var childPaths = _entries.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var childPath in childPaths)
                {
                    _entries.TryRemove(childPath, out _);
                    _fileData.TryRemove(childPath, out _);
                }
            }

            OnFileOperationOccurred(new FileOperationEventArgs(
                entry.IsDirectory ? "DeleteDirectory" : "DeleteFile",
                normalizedPath,
                true));

            return true;
        }
        finally
        {
            _entriesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Renames an entry.
    /// </summary>
    public bool RenameEntry(string oldPath, string newPath)
    {
        var normalizedOld = NormalizePath(oldPath);
        var normalizedNew = NormalizePath(newPath);

        _entriesLock.EnterWriteLock();
        try
        {
            if (!_entries.TryRemove(normalizedOld, out var entry))
                return false;

            // Update path and add to new location
            var newEntry = entry with { Path = normalizedNew };
            _entries[normalizedNew] = newEntry;

            // Move file data
            if (!entry.IsDirectory && _fileData.TryRemove(normalizedOld, out var data))
            {
                _fileData[normalizedNew] = data;
            }

            // Rename children for directories
            if (entry.IsDirectory)
            {
                var oldPrefix = normalizedOld + "\\";
                var childPaths = _entries.Keys
                    .Where(k => k.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var childPath in childPaths)
                {
                    if (_entries.TryRemove(childPath, out var childEntry))
                    {
                        var newChildPath = normalizedNew + childPath.Substring(normalizedOld.Length);
                        _entries[newChildPath] = childEntry with { Path = newChildPath };

                        if (!childEntry.IsDirectory && _fileData.TryRemove(childPath, out var childData))
                        {
                            _fileData[newChildPath] = childData;
                        }
                    }
                }
            }

            OnFileOperationOccurred(new FileOperationEventArgs("Rename", $"{normalizedOld} -> {normalizedNew}", true));

            return true;
        }
        finally
        {
            _entriesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Checks if a directory has children.
    /// </summary>
    public bool HasChildren(string path)
    {
        var normalizedPath = NormalizePath(path);
        var prefix = normalizedPath == "\\" ? "\\" : normalizedPath + "\\";

        return _entries.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k != normalizedPath);
    }

    /// <summary>
    /// Lists directory contents.
    /// </summary>
    public IEnumerable<FileEntry> ListDirectory(string path, string? pattern = null)
    {
        var normalizedPath = NormalizePath(path);
        var prefix = normalizedPath == "\\" ? "\\" : normalizedPath + "\\";

        foreach (var kvp in _entries)
        {
            // Must be immediate child
            if (!kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = kvp.Key.Substring(prefix.Length);
            if (relativePath.Contains('\\'))
                continue; // Not immediate child

            // Apply pattern filter
            if (!string.IsNullOrEmpty(pattern) && pattern != "*")
            {
                var fileName = Path.GetFileName(kvp.Key);
                if (!MatchPattern(fileName, pattern))
                    continue;
            }

            yield return kvp.Value;
        }
    }

    #endregion

    #region Data Operations

    /// <summary>
    /// Reads file data.
    /// </summary>
    public byte[]? ReadData(string path, long offset, int length)
    {
        var normalizedPath = NormalizePath(path);

        if (!_fileData.TryGetValue(normalizedPath, out var data))
            return null;

        if (offset >= data.Length)
            return Array.Empty<byte>();

        var actualLength = (int)Math.Min(length, data.Length - offset);
        var result = new byte[actualLength];
        Array.Copy(data, offset, result, 0, actualLength);

        OnFileOperationOccurred(new FileOperationEventArgs("Read", normalizedPath, true));

        return result;
    }

    /// <summary>
    /// Writes file data.
    /// </summary>
    public void WriteData(string path, long offset, byte[] data)
    {
        var normalizedPath = NormalizePath(path);

        _entriesLock.EnterWriteLock();
        try
        {
            if (!_fileData.TryGetValue(normalizedPath, out var existingData))
            {
                existingData = Array.Empty<byte>();
            }

            var newEndOffset = offset + data.Length;
            var newLength = (int)Math.Max(existingData.Length, newEndOffset);

            var newData = new byte[newLength];
            Array.Copy(existingData, newData, existingData.Length);
            Array.Copy(data, 0, newData, offset, data.Length);

            _fileData[normalizedPath] = newData;

            // Update entry
            if (_entries.TryGetValue(normalizedPath, out var entry))
            {
                entry.FileSize = newLength;
                entry.LastWriteTime = WinFspNative.GetCurrentFileTime();
                entry.ChangeTime = entry.LastWriteTime;
            }

            OnFileOperationOccurred(new FileOperationEventArgs("Write", normalizedPath, true));
        }
        finally
        {
            _entriesLock.ExitWriteLock();
        }
    }

    #endregion

    #region Private Methods

    private void InitializeRootDirectory()
    {
        var root = new FileEntry
        {
            Path = "\\",
            IsDirectory = true,
            Attributes = WinFspNative.FileAttributes.Directory | WinFspNative.FileAttributes.System,
            AllocationSize = 0,
            FileSize = 0,
            CreationTime = WinFspNative.GetCurrentFileTime(),
            LastAccessTime = WinFspNative.GetCurrentFileTime(),
            LastWriteTime = WinFspNative.GetCurrentFileTime(),
            ChangeTime = WinFspNative.GetCurrentFileTime(),
            IndexNumber = 1
        };

        _entries["\\"] = root;
    }

    private string GetMountPoint()
    {
        if (!string.IsNullOrEmpty(_config.MountPoint))
            return _config.MountPoint;

        if (!string.IsNullOrEmpty(_config.DriveLetter))
        {
            var letter = _config.DriveLetter.TrimEnd(':');
            return $"{letter}:";
        }

        // Find first available drive letter
        var usedDrives = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (!usedDrives.Contains(c))
                return $"{c}:";
        }

        throw new InvalidOperationException("No available drive letters.");
    }

    private WinFspNative.FspFileSystemParams CreateVolumeParams()
    {
        return new WinFspNative.FspFileSystemParams
        {
            Version = 0,
            SectorSize = (ushort)_config.SectorSize,
            SectorsPerAllocationUnit = (ushort)_config.SectorsPerAllocationUnit,
            MaxComponentLength = (ushort)_config.MaxComponentLength,
            VolumeCreationTime = WinFspNative.GetCurrentFileTime(),
            VolumeSerialNumber = (uint)DateTime.UtcNow.Ticks,
            TransactTimeout = 0,
            IrpTimeout = 0,
            IrpCapacity = 0,
            FileInfoTimeout = 1000,
            CaseSensitiveSearch = _config.CaseSensitive ? 1u : 0u,
            CasePreservedNames = _config.CasePreserved ? 1u : 0u,
            UnicodeOnDisk = _config.UnicodeOnDisk ? 1u : 0u,
            PersistentAcls = _config.PersistentAcls ? 1u : 0u,
            ReparsePoints = _config.SupportReparsePoints ? 1u : 0u,
            ReparsePointsAccessCheck = 1,
            NamedStreams = _config.SupportNamedStreams ? 1u : 0u,
            HardLinks = _config.SupportHardLinks ? 1u : 0u,
            ExtendedAttributes = _config.SupportExtendedAttributes ? 1u : 0u,
            WslFeatures = 0,
            FlushAndPurgeOnCleanup = 1,
            DeviceControl = 0,
            UmFileContextIsUserContext2 = 0,
            UmFileContextIsFullContext = 0,
            AllowOpenInKernelMode = 0,
            CasePreservedExtendedAttributes = 1,
            Prefix = string.Empty,
            FileSystemName = _config.FileSystemName
        };
    }

    private WinFspNative.FspFileSystemInterface CreateInterface()
    {
        // Note: In a full implementation, these would be proper callback implementations
        // For now, return empty interface - actual callbacks set up through WinFspDriverPlugin
        return new WinFspNative.FspFileSystemInterface();
    }

    private long GetUsedSpace()
    {
        return _fileData.Values.Sum(d => (long)d.Length);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "\\";

        path = path.Replace('/', '\\');
        if (!path.StartsWith("\\"))
            path = "\\" + path;

        if (path.Length > 1 && path.EndsWith("\\"))
            path = path.TrimEnd('\\');

        return path;
    }

    private static bool MatchPattern(string fileName, string pattern)
    {
        // Simple wildcard matching
        if (pattern == "*" || pattern == "*.*")
            return true;

        var regexPattern = "^" +
            System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") +
            "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            fileName,
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private void OnFileOperationOccurred(FileOperationEventArgs args)
    {
        FileOperationOccurred?.Invoke(this, args);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the filesystem.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Unmount();
        _entriesLock.Dispose();
        _entries.Clear();
        _fileData.Clear();

        _disposed = true;
    }

    #endregion
}

/// <summary>
/// Event arguments for file operations.
/// </summary>
public sealed class FileOperationEventArgs : EventArgs
{
    /// <summary>
    /// Operation type (Create, Read, Write, Delete, etc.).
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Path of the file/directory.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Error message if operation failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Timestamp of the operation.
    /// </summary>
    public DateTime Timestamp { get; }

    public FileOperationEventArgs(string operation, string path, bool success, string? errorMessage = null)
    {
        Operation = operation;
        Path = path;
        Success = success;
        ErrorMessage = errorMessage;
        Timestamp = DateTime.UtcNow;
    }
}
